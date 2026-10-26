using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Only My Voice: Exo learns the user's voice (a voiceprint taught from the live meter's last seconds, then refined in
/// calls by <see cref="VoiceprintLearning"/>) and, in calls, holds back voices that aren't theirs: someone else in the
/// room, a TV, a stream playing on speakers. A voiceprint is NVIDIA's
/// TitaNet-small speaker embedding (NeMo, CC-BY-4.0; 192 numbers), run through sherpa-onnx's C interface
/// (Apache-2.0, with ONNX Runtime, both shipped beside Exo). The model (40 MB) is fetched once, pinned, from
/// sherpa-onnx's release when the user first teaches Exo their voice. The voiceprint stays on this PC, DPAPI-protected
/// (<see cref="ExoDpapi"/>); it is never sent anywhere.
/// Measured on TTS voices through the cleaned 16 kHz voice frames: one second of the enrolled voice scored 0.67-0.87
/// against its voiceprint, other voices 0.12-0.47 (another man's at most 0.47), so a voice under <see cref="Other"/> twice
/// in a row is held back and one over <see cref="Mine"/> goes out again.
/// </summary>
internal sealed class MyVoice : IThreadPoolWorkItem
{
    internal const float Other = 0.55f, Mine = 0.60f;
    /// <summary>The least speech (in seconds, after the gate) a teach takes.</summary>
    internal const int TeachSeconds = 8;
    private const int Rate = 16000, Window = Rate * 3 / 2, First = Rate, Step = Rate / 2;
    private const int GapFrames = 50; // a second without voice: whoever talks next is judged afresh
    private const int Down = Opus.FrameSamples / 3;

    // The voiceprint judged against; replaced when learning folds a sample in (on the judgment's own thread).
    private float[] _mine;
    private readonly Decimator _decimator = new();
    private readonly float[] _mono = new float[Opus.FrameSamples], _down = new float[Down];
    private readonly float[] _ring = new float[Window], _work = new float[Window];
    private int _write, _filled, _sinceCheck, _quiet, _workLength, _busy, _generation, _workGeneration, _judged;
    private Verdict _verdict;
    // The safety stop: how much of the last 20 s of speech (20 buckets of a second's voiced frames) was held back.
    private readonly int[] _heldPerSecond = new int[StopSeconds];
    private int _bucket, _inBucket, _seconds, _stopped;
    private bool _holding;

    private MyVoice(float[] mine) => _mine = mine;

    /// <summary>For a call's chain: the voiceprint and model are here, else null. Reads a file: not for the audio thread.</summary>
    internal static MyVoice? Create() => Voiceprint.Load() is { } mine && SpeakerModel.Ready() ? new MyVoice(mine) : null;

    /// <summary>Speech this long, held back past <see cref="StopShare"/> of it, stops holding back for the call.</summary>
    internal const int StopSeconds = 20;
    internal const float StopShare = 0.5f;

    /// <summary>Whether the voice talking now is held back as not the user's (never once the safety stop has come).</summary>
    internal bool HoldingBack => Volatile.Read(ref _stopped) == 0 && Volatile.Read(ref _verdict.Held);

    /// <summary>
    /// The safety stop came: more than half of the last 20 s of speech was judged not the user's. That is almost always
    /// the user's own voice misjudged (a short or quiet teach, a shouting voice in a game: the reason it was added), and
    /// being muted in a call is worse than anything getting through, so holding back stays off for this call and the
    /// voice panel says so. A TV talking 20 s straight while the user is silent stops it too: the price of never muting
    /// them.
    /// </summary>
    internal bool Stopped => Volatile.Read(ref _stopped) != 0;

    /// <summary>
    /// Each 20 ms stereo frame, cleaned, with whether the gate let it through (before this holds it back): voiced
    /// frames are kept at 16 kHz, and every half second (once a second of voice is in) the last 1.5 s is judged off
    /// the audio thread. Nothing is allocated here.
    /// </summary>
    internal void Feed(float[] stereo, bool voiced, bool holding)
    {
        // Off (Only My Voice not on), it still judges, to learn, but holds nobody back: no safety stop, no log of it.
        Volatile.Write(ref _holding, holding);
        for (var i = 0; i < Opus.FrameSamples; i++) _mono[i] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f;
        _decimator.Run(_mono, _down);
        if (!voiced)
        {
            if (++_quiet == GapFrames)
            {
                _filled = _sinceCheck = 0;
                Interlocked.Increment(ref _generation);
                _verdict = default;
            }
            return;
        }
        _quiet = 0;
        if (holding) CountHeld();
        foreach (var sample in _down) { _ring[_write] = sample; _write = (_write + 1) % Window; }
        _filled = Math.Min(_filled + Down, Window);
        _sinceCheck += Down;
        if (_filled < First || _sinceCheck < Step || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _sinceCheck = 0;
        _workLength = _filled;
        for (var i = 0; i < _filled; i++) _work[i] = _ring[(_write - _filled + i + Window) % Window];
        _workGeneration = Volatile.Read(ref _generation);
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    // One voiced frame toward the safety stop, on the audio thread: a second's frames (50) per bucket.
    private void CountHeld()
    {
        if (Volatile.Read(ref _verdict.Held)) _heldPerSecond[_bucket]++;
        if (++_inBucket < 50) return;
        _inBucket = 0;
        _seconds = Math.Min(_seconds + 1, StopSeconds);
        _bucket = (_bucket + 1) % StopSeconds;
        if (_seconds == StopSeconds && _stopped == 0 && ShouldStop(_heldPerSecond))
        {
            Volatile.Write(ref _stopped, 1);
            AppLog.Info("Discord voice: Only My Voice held back most of your own speech for 20 s, so it stops holding back for this call; teach your voice again.");
        }
        _heldPerSecond[_bucket] = 0;
    }

    /// <summary>Over the buckets of the last <see cref="StopSeconds"/> of speech (each a second's 50 voiced frames, how many were held back): more than <see cref="StopShare"/> held back.</summary>
    internal static bool ShouldStop(ReadOnlySpan<int> heldPerSecond)
    {
        var held = 0;
        foreach (var h in heldPerSecond) held += h;
        return held > StopShare * 50 * heldPerSecond.Length;
    }

    void IThreadPoolWorkItem.Execute()
    {
        try
        {
            if (SpeakerModel.Embed(_work.AsSpan(0, _workLength)) is not { } now || now.Length != _mine.Length) return;
            var similarity = VoiceprintLearning.Dot(now, _mine);
            // Every third judgment of a full window is 1.5 s of speech none of the others learned from: clearly the
            // user's, it goes toward the next sample the voiceprint learns from.
            if (_workLength == Window && ++_judged % 3 == 0 && VoiceprintLearning.Learn(now, similarity, Window / (float)Rate) is { } learned)
                _mine = learned;
            if (_workGeneration != Volatile.Read(ref _generation)) return; // a pause came: a new voice is judged afresh
            var before = _verdict.Held;
            _verdict = Judge(_verdict, similarity);
            if (_verdict.Held != before && Volatile.Read(ref _holding))
                AppLog.Info(_verdict.Held
                    ? $"Discord voice: holding back a voice that isn't yours (similarity {similarity:0.00})."
                    : $"Discord voice: your voice again (similarity {similarity:0.00}).");
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>Where a voice stands: held back, and how many judgments in a row fell under <see cref="Other"/>.</summary>
    internal struct Verdict
    {
        internal bool Held;
        internal int Low;
    }

    /// <summary>The next verdict: held after two judgments under <see cref="Other"/>, out again at <see cref="Mine"/> or over.</summary>
    internal static Verdict Judge(Verdict now, float similarity)
    {
        if (similarity >= Mine) return default;
        if (similarity >= Other) return now with { Low = 0 };
        var low = now.Low + 1;
        return new Verdict { Low = low, Held = now.Held || low >= 2 };
    }

    /// <summary>
    /// Learns the voice from the live meter's take (its cleaned mono 48 kHz frames and which the gate let through):
    /// at least three seconds of voice. Fetches the model first if it isn't here. A message for the user, null when learned.
    /// </summary>
    internal static async Task<string?> LearnAsync(float[] cleaned, bool[] voiced, CancellationToken ct)
    {
        var voice = new List<float>();
        var decimator = new Decimator();
        var frame = new float[Opus.FrameSamples];
        var down = new float[Down];
        for (var f = 0; f < voiced.Length; f++)
        {
            Array.Copy(cleaned, f * Opus.FrameSamples, frame, 0, Opus.FrameSamples);
            decimator.Run(frame, down);
            if (voiced[f]) voice.AddRange(down);
        }
        // Eight seconds of speech at the least: five (three before) left a voiceprint that judged the user's own voice
        // in a game not theirs, and Only My Voice held them back.
        if (voice.Count < TeachSeconds * Rate) return "Talk on the meter above for about ten seconds first, then teach it again.";
        if (!await SpeakerModel.EnsureAsync(ct).ConfigureAwait(false)) return "The voice model couldn't be downloaded. Check your connection and try again.";
        if (await Task.Run(() => SpeakerModel.Embed(voice.ToArray()), ct).ConfigureAwait(false) is not { } print) return "Exo couldn't learn the voice from that take. Record it again.";
        // A teach starts learning over: this take is the anchor calls learn around.
        Voiceprint.Save(new VoiceprintLearning.State(print, print.ToArray(), voice.Count / (float)Rate, 0));
        VoiceprintLearning.Reset();
        AppLog.Info($"Discord voice: learned your voice from {voice.Count / (float)Rate:0.0} s of speech.");
        return null;
    }

    /// <summary>48 kHz to 16 kHz: a 31-tap windowed-sinc low pass at 7 kHz, every third sample kept; its history carries across frames.</summary>
    internal sealed class Decimator
    {
        private static readonly float[] Taps = MakeTaps();
        private readonly float[] _history = new float[Taps.Length - 1 + Opus.FrameSamples];

        private static float[] MakeTaps()
        {
            var taps = new float[31];
            const double cutoff = 7000.0 / Opus.SampleRate;
            double sum = 0;
            for (var i = 0; i < taps.Length; i++)
            {
                var n = i - (taps.Length - 1) / 2.0;
                var sinc = n == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * n) / (Math.PI * n);
                var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps.Length - 1));
                taps[i] = (float)(sinc * window);
                sum += taps[i];
            }
            for (var i = 0; i < taps.Length; i++) taps[i] = (float)(taps[i] / sum);
            return taps;
        }

        internal void Run(ReadOnlySpan<float> mono48k, Span<float> into16k)
        {
            var keep = Taps.Length - 1;
            Array.Copy(_history, _history.Length - keep, _history, 0, keep);
            mono48k.CopyTo(_history.AsSpan(keep));
            for (var o = 0; o < into16k.Length; o++)
            {
                float acc = 0;
                var at = o * 3;
                for (var t = 0; t < Taps.Length; t++) acc += Taps[t] * _history[at + t];
                into16k[o] = acc;
            }
        }
    }
}

/// <summary>The user's voiceprint on this PC (what was taught, what calls refined, how much speech it is from), DPAPI-protected, in Exo's voice folder.</summary>
internal static class Voiceprint
{
    private static string _path = Path.Combine(PathHelper.AppDataDir, "voice", "my-voice.bin");
    private static bool? _learned;

    internal static string FilePath
    {
        get => _path;
        set { _path = value; _learned = null; }
    }

    // Read once, then kept by Save and Forget: VoiceProcessing asks on every audio frame.
    internal static bool Learned => _learned ??= File.Exists(FilePath);

    /// <summary>The voiceprint calls are judged against, or null while there is none (or it can't be read).</summary>
    internal static float[]? Load() => LoadState()?.Print;

    /// <summary>All of it: the anchor, the voiceprint, the seconds of speech and the updates; a voiceprint from before learning is its own anchor.</summary>
    internal static VoiceprintLearning.State? LoadState()
    {
        try { return VoiceprintLearning.Decode(ExoDpapi.Unprotect(File.ReadAllBytes(FilePath))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { return null; }
    }

    /// <summary>How many seconds of the user's speech the voiceprint is from, rounded down; 0 with none.</summary>
    internal static int Seconds => (int)(LoadState()?.Seconds ?? 0);

    internal static void Save(VoiceprintLearning.State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, ExoDpapi.Protect(VoiceprintLearning.Encode(state)));
        File.Move(temp, FilePath, overwrite: true);
        _learned = true;
    }

    internal static void Forget()
    {
        try { File.Delete(FilePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _learned = File.Exists(FilePath);
        VoiceprintLearning.Reset();
    }
}
