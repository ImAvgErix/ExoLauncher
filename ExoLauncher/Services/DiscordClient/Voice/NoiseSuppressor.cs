using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>Fetching Exo's DeepFilterNet library (outside the unsafe class: it awaits).</summary>
internal static class NoiseSuppressorDownload
{
    /// <summary>DeepFilterNet as one of Exo's parts (<see cref="ExoComponents"/>): setup fetches it with Exo.</summary>
    internal static readonly ExoComponent Component = new("DeepFilterNet noise suppression", "Noise suppression",
        NoiseSuppressor.Plugin.ExpectedSize, 0, Ready,
        (progress, ct) => VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(NoiseSuppressor.Plugin, NoiseSuppressor.PluginPath, _ => true, ct, done => progress(done, unpacking: false)),
        () => NoiseSuppressor.PluginPath);

    // The library file last found whole (its size and time): hashing 50 MB again is skipped while it's unchanged.
    private static (long Size, DateTime Written, bool Whole)? _checked;

    /// <summary>Whether the library is here and whole; hashes it only when the file changed since it last checked.</summary>
    internal static bool Ready()
    {
        var file = new FileInfo(NoiseSuppressor.PluginPath);
        if (!file.Exists) return false;
        if (_checked is { } last && last.Size == file.Length && last.Written == file.LastWriteTimeUtc) return last.Whole;
        var whole = VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(NoiseSuppressor.Plugin, NoiseSuppressor.PluginPath, _ => true);
        _checked = (file.Length, file.LastWriteTimeUtc, whole);
        return whole;
    }

    /// <summary>Fetches the library if it isn't here yet (verified before it is kept); quiet when offline. Never on the caller's thread.</summary>
    internal static Task EnsureAsync(CancellationToken ct) => Task.Run(() => FetchAsync(ct), ct);

    private static async Task FetchAsync(CancellationToken ct)
    {
        if (Ready()) { RemoveOldPlugin(); return; }
        try
        {
            await VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(NoiseSuppressor.Plugin, NoiseSuppressor.PluginPath, _ => true, ct).ConfigureAwait(false);
            AppLog.Info("Discord voice: noise suppression ready.");
            RemoveOldPlugin();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Info("Discord voice: noise suppression not fetched (" + ex.GetType().Name + ").");
        }
    }

    // DeepFilterNet's LADSPA plugin (3.13.1 and before) and the copies 3.13.1 made of it, 50 MB each: unused now.
    private static void RemoveOldPlugin()
    {
        var folder = Path.GetDirectoryName(NoiseSuppressor.PluginPath);
        if (folder is null || !Directory.Exists(folder)) return;
        foreach (var old in Directory.EnumerateFiles(folder, "deep_filter*.dll"))
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// Noise suppression for the microphone and, with Noise Suppression for Others, for each voice heard: DeepFilterNet 3's
/// low-latency model (Rikorose/DeepFilterNet v0.5.6, MIT/Apache-2.0) through its own C interface, built by Exo from that
/// release's source (voice\build.ps1) with three entry points of Exo's added (the built-in model, the stage
/// thresholds, and a frame call that never unwinds into Exo). Fetched from Exo's GitHub release (pinned size and
/// SHA-256, <see cref="VerifiedGitHubReleaseDownloader"/>) by Exo's setup with Exo's other parts (<see cref="ExoComponents"/>),
/// so it is ready for the first call. A 20 ms stereo frame is mixed to mono, cleaned as DeepFilterNet's two
/// 10 ms hops on the caller's own thread, and spread back to both channels.
/// It used to run through DeepFilterNet's LADSPA plugin, which hands each frame to a worker thread that polls every 2 ms
/// and, whenever that thread falls behind (a busy PC, a coarse timer), pushes silence into the output and adds 10 ms of
/// delay, up to a second; and whose instances all shared one model's buffers. On some PCs voices went out silent,
/// choppy or garbled. Here nothing waits on a thread or a timer, nothing is inserted, and each instance has a model of
/// its own. A frame the library can't clean passes through as it came.
/// It turns noise down by at most <see cref="MostDb"/> (Voice Tune's strength, else <see cref="GentleDb"/>), with
/// DeepFilterNet's own post-filter on at β 0.02 (it won the voice-chain evaluation over the same chain with it off).
/// Its delay is DeepFilterNet's 10 ms STFT hop (the low-latency model has no lookahead), measured
/// from the sound by <see cref="DelaySamples"/>. Each hop's local SNR estimate (<see cref="SpeechDb"/>) is its own
/// judgment of how much voice the frame holds, which the gate reads as Discord's reads Krisp's.
/// As Krisp runs its own DeepFilterNet-type model, the sound goes in at the level the model knows (<see cref="ModelDb"/>,
/// the mic's level over about the last 3 s brought there) and comes out at its own level again, so a quiet or a hot mic
/// is cleaned as well as one at the model's level; chosen by ear over the plain model.
/// </summary>
internal sealed unsafe class NoiseSuppressor : IDisposable
{
    /// <summary>
    /// How far noise suppression may turn noise down (DeepFilterNet's attenuation limit, dB): 70, at most 80 (Voice Tune,
    /// a noisy room). The limit isn't the noise taken off, about 31 dB more: on white noise 50 takes 19 dB (a fan stayed
    /// audible under the voice), 70 takes 39 and 100 (its own default, used before) everything, which took words with the
    /// noise: voices came out robotic and the gate held whole words shut (-26 dBFS frames in a real call's log).
    /// </summary>
    internal const int GentleDb = 70, MostDb = 80;

    /// <summary>The level (dBFS) the sound goes into the model at: Krisp's (its trainVolume 2000 of int16).</summary>
    internal const float ModelDb = -24f;
    private const float LevelSeconds = 3f, LevelFrom = -70f, MostLiftDb = 40f, MostCutDb = 20f;
    private float _levelDb = float.NaN;

    private const int Hop = 480, WarmSamples = Opus.SampleRate / 2;

    internal static readonly GitHubReleaseAsset Plugin = new(
        "UhhErix", "ExoLauncher", "v3.13.3", "exo-deepfilter-0.5.6.dll",
        53095936, "dc392076a100a4742b0e7d051bf0e630c9b6d7ceb29a35567acb65e3c7ee3b49");

    internal static string PluginPath { get; set; } = Path.Combine(PathHelper.AppDataDir, "voice", "exo-deepfilter.dll");

    private static nint _library;
    private static delegate* unmanaged[Cdecl]<float, nint> _create;
    private static delegate* unmanaged[Cdecl]<nint, void> _free;
    private static delegate* unmanaged[Cdecl]<nint, float, void> _attenuation, _postFilter;
    private static delegate* unmanaged[Cdecl]<nint, float, float, float, void> _thresholds;
    private static delegate* unmanaged[Cdecl]<nint, float*, float*, float> _process;
    private static readonly Lock LoadGate = new();

    private static bool LoadLibrary()
    {
        lock (LoadGate)
        {
            if (_library != 0) return true;
            if (!NoiseSuppressorDownload.Ready() || !NativeLibrary.TryLoad(PluginPath, out var library)) return false;
            if (!NativeLibrary.TryGetExport(library, "df_create_default", out var create)
                || !NativeLibrary.TryGetExport(library, "df_set_atten_lim", out var attenuation)
                || !NativeLibrary.TryGetExport(library, "df_set_post_filter_beta", out var postFilter)
                || !NativeLibrary.TryGetExport(library, "df_set_thresholds", out var thresholds)
                || !NativeLibrary.TryGetExport(library, "df_process_frame_checked", out var process))
                return false;
            _create = (delegate* unmanaged[Cdecl]<float, nint>)create;
            _attenuation = (delegate* unmanaged[Cdecl]<nint, float, void>)attenuation;
            _postFilter = (delegate* unmanaged[Cdecl]<nint, float, void>)postFilter;
            _thresholds = (delegate* unmanaged[Cdecl]<nint, float, float, float, void>)thresholds;
            _process = (delegate* unmanaged[Cdecl]<nint, float*, float*, float>)process;
            if (NativeLibrary.TryGetExport(library, "df_free", out var free)) _free = (delegate* unmanaged[Cdecl]<nint, void>)free;
            _library = library;
            return true;
        }
    }

    // DeepFilterNet's own runtime thresholds for skipping stages (no processing on silence, none beyond the first stage
    // on clean speech). Light: the deep filtering stage's threshold at its lowest, so no frame runs it (DeepFilterNet's
    // own advice for a CPU too slow for it); its first stage, the ERB gains, still takes the noise down.
    private const float MinDb = -10f, MaxErbDb = 30f, FullDf = 20f, LightDf = -15f;

    private readonly nint _state;
    private readonly float* _input, _output;
    private readonly DelayTracker _delay = new();
    private float _limit = float.NaN;
    private bool _light;
    private bool _failed;

    /// <summary>How long its runs take, and so whether it runs light (<see cref="Overload"/>).</summary>
    internal Overload Load { get; } = new();

    /// <summary>How far behind its input the cleaned sound runs, in samples at 48 kHz, as last measured (see <see cref="DelayTracker"/>).</summary>
    internal int DelaySamples => _delay.Samples;

    /// <summary>The level (dBFS) of the microphone's sound the last cleaned frame came from, once known (see <see cref="DelayTracker.InputDb"/>).</summary>
    internal float? InputDb => _delay.InputDb;

    /// <summary>
    /// DeepFilterNet's local SNR estimate for the last frame (dB, the louder of its two hops): how much of it it judged
    /// voice over noise, from -15 (noise alone) to 35 (clean voice). Null before the first frame or after one it couldn't clean.
    /// </summary>
    internal float? SpeechDb { get; private set; }

    private NoiseSuppressor(nint state)
    {
        _state = state;
        _input = (float*)NativeMemory.AllocZeroed(WarmSamples, sizeof(float));
        _output = (float*)NativeMemory.AllocZeroed(WarmSamples, sizeof(float));
        _postFilter(state, 0.02f);
        ApplyThresholds();
    }

    // DeepFilterNet's stage thresholds; the light argument drops it to the first (ERB) stage alone while the CPU can't
    // keep up (DeepFilterNet's own advice for a CPU too slow for it).
    private void ApplyThresholds(bool light = false) => _thresholds(_state, MinDb, MaxErbDb, light ? LightDf : FullDf);

    // One let go of waits here for the next call, Voice Check or Voice Tune (each holds its own model: about 40 MB of its
    // own memory and 80 MB in use, half a second to load), and is freed after a minute unused (df_free), so a PC between
    // calls doesn't carry it. A call loads one as it joins (DiscordSession.JoinVoice), well inside the connection's own time.
    private static readonly Stack<NoiseSuppressor> Idle = new();
    private static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(1);
    private static Timer? _sweep;
    private int _inUse = 1;
    private long _idleSince;

    /// <summary>A suppressor for 20 ms frames at 48 kHz (one let go of before, or new), or null when the library isn't there.</summary>
    internal static NoiseSuppressor? Create()
    {
        NoiseSuppressor? idle;
        lock (Idle) Idle.TryPop(out idle);
        if (idle is not null)
        {
            // Warmed as a new one is, at full strength: silence alone left the last user's noise in the model's running
            // statistics (3 s of loud noise let a fan through the next call's gate, measured in the voice chain test).
            idle._inUse = 1;
            idle._light = false;
            idle.ApplyThresholds();
            idle.WarmUp();
            return idle;
        }
        try
        {
            if (!LoadLibrary()) return null;
            var state = _create(GentleDb);
            if (state == 0)
            {
                AppLog.Info("Discord voice: noise suppression could not start its model.");
                return null;
            }
            var suppressor = new NoiseSuppressor(state);
            suppressor.WarmUp();
            return suppressor;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLog.Info("Discord voice: noise suppression unavailable (" + ex.GetType().Name + ").");
            return null;
        }
    }

    /// <summary>
    /// Cleans a 20 ms stereo frame in place, turning noise down by up to <paramref name="limitDb"/> (at most
    /// <see cref="MostDb"/>; Voice Tune sets less for a quiet room, which sounds more natural).
    /// </summary>
    internal void Process(float[] stereo, int limitDb = GentleDb)
    {
        var limit = Math.Clamp(limitDb, 0, MostDb);
        if (limit != _limit) { _attenuation(_state, limit); _limit = limit; }
        if (Load.Light != _light) { _light = Load.Light; ApplyThresholds(_light); }
        var frame = stereo.Length / 2;
        double power = 0;
        for (var i = 0; i < frame; i++) { var s = _input[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f; power += s * (double)s; }
        // The mic's level over about the last 3 s (frames under -70 dBFS are silence, not a level), and the gain to the model's.
        var db = 10 * MathF.Log10((float)(power / frame) + 1e-12f);
        if (db > LevelFrom && SpeechDb > VoiceGate.SpeechDb) _levelDb = float.IsNaN(_levelDb) ? db : _levelDb + (db - _levelDb) * frame / (LevelSeconds * Opus.SampleRate);
        var gain = float.IsNaN(_levelDb) ? 1f : MathF.Pow(10, Math.Clamp(ModelDb - _levelDb, -MostCutDb, MostLiftDb) / 20);
        if (gain != 1f) for (var i = 0; i < frame; i++) _input[i] *= gain;
        var started = Stopwatch.GetTimestamp();
        float speech = float.NegativeInfinity;
        var ok = true;
        for (var at = 0; at + Hop <= frame; at += Hop)
        {
            var snr = _process(_state, _input + at, _output + at);
            if (float.IsNaN(snr)) { ok = false; break; }
            speech = MathF.Max(speech, snr);
        }
        Load.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (!ok)
        {
            // Passed through as it came: a voice is never lost to a frame the model refused.
            if (!_failed) AppLog.Info("Discord voice: noise suppression let a frame through uncleaned (the model refused it).");
            _failed = true;
            SpeechDb = null;
            return;
        }
        SpeechDb = speech;
        if (gain != 1f)
            for (var i = 0; i < frame; i++) { _input[i] /= gain; _output[i] /= gain; }
        _delay.Add(new ReadOnlySpan<float>(_input, frame), new ReadOnlySpan<float>(_output, frame));
        for (var i = 0; i < frame; i++) stereo[i * 2] = stereo[i * 2 + 1] = _output[i];
    }

    // A new instance's first pass through each stage is the slow one (90 ms and more): it runs first on half a second of
    // a tone in noise, which takes both stages (silence would skip them), before any frame of a call reaches it.
    private void WarmUp()
    {
        var random = new Random(1);
        for (var i = 0; i < WarmSamples; i++)
            _input[i] = 0.1f * MathF.Sin(2 * MathF.PI * 180 * i / Opus.SampleRate) + 0.03f * (float)(random.NextDouble() * 2 - 1);
        for (var at = 0; at + Hop <= WarmSamples; at += Hop) _process(_state, _input + at, _output + at);
        Flush();
    }

    // 100 ms of silence through, so what it still holds of its last user's sound (its STFT's last hop and the past
    // spectrum deep filtering reads) never comes out in the next one's frames.
    private void Flush()
    {
        new Span<float>(_input, Hop).Clear();
        for (var i = 0; i < 10; i++) _process(_state, _input, _output);
        _delay.Reset();
        Load.Reset();
        SpeechDb = null;
        _levelDb = float.NaN;
    }

    /// <summary>Lets go of it for the next user (see <see cref="Idle"/>); a second call does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _inUse, 0) == 0) return;
        _idleSince = Environment.TickCount64;
        lock (Idle)
        {
            Idle.Push(this);
            if (_free != null) _sweep ??= new Timer(_ => Sweep(), null, IdleFor, IdleFor);
        }
    }

    // Frees each model unused for a minute; the sweep stops when none is left waiting.
    private static void Sweep()
    {
        List<NoiseSuppressor> stale;
        lock (Idle)
        {
            var now = Environment.TickCount64;
            var all = Idle.ToArray();
            stale = [.. all.Where(s => now - s._idleSince >= IdleFor.TotalMilliseconds)];
            if (stale.Count == 0) return;
            Idle.Clear();
            foreach (var keep in all.Reverse().Where(s => !stale.Contains(s))) Idle.Push(keep);
            if (Idle.Count == 0) { _sweep?.Dispose(); _sweep = null; }
        }
        foreach (var s in stale)
        {
            _free(s._state);
            NativeMemory.Free(s._input);
            NativeMemory.Free(s._output);
        }
        AppLog.Info($"Discord voice: noise suppression freed {stale.Count} unused model{(stale.Count == 1 ? "" : "s")}.");
    }
}

/// <summary>
/// How far behind its input DeepFilterNet's output runs, measured from the sound: a whole number of its 10 ms hops (its
/// STFT's one, and however many its buffer holds), from 1 to <see cref="MostHops"/>. Each loud frame's output is
/// compared with the input that many hops back; once 10 loud frames agree on one, it is the delay. Pitch repeats
/// in a voice, so one frame can't tell a hop from the next; ten frames of a voice's pitch moving can. Until then
/// it is the plugin's starting delay, two hops. Nothing is allocated per frame.
/// </summary>
internal sealed class DelayTracker
{
    internal const int Hop = 480, MostHops = 4, Start = 2 * Hop;
    private const int Frame = Opus.FrameSamples, Votes = 10;
    private const float Loud = 1e-5f; // -50 dBFS mean power

    // The input of the last MostHops hops before this frame, then this frame's.
    private readonly float[] _history = new float[MostHops * Hop + Frame];
    private readonly double[] _score = new double[MostHops];
    private int _votes;

    /// <summary>The delay in samples at 48 kHz.</summary>
    internal int Samples { get; private set; } = Start;

    /// <summary>
    /// The level (dBFS) of the input the last output came from: the frame <see cref="Samples"/> back, so it lines up
    /// with what came out (the frame that went in is that much ahead of it). Null until that much has gone in since the
    /// start (what it holds before then is silence, or the last user's sound).
    /// </summary>
    internal float? InputDb { get; private set; }

    private int _frames; // since the start, counted up to MostHops

    /// <summary>One frame's input and the output it gave back.</summary>
    internal void Add(ReadOnlySpan<float> input, ReadOnlySpan<float> output)
    {
        var at = MostHops * Hop;
        input.CopyTo(_history.AsSpan(at));
        InputDb = null;
        if (_frames * Frame >= Samples)
        {
            double behind = 0;
            for (var n = at - Samples; n < at - Samples + Frame; n++) behind += _history[n] * _history[n];
            InputDb = MathF.Max(-100f, 10 * MathF.Log10((float)(behind / Frame) + 1e-10f));
        }
        if (_frames < MostHops) _frames++;
        double inPower = 0, outPower = 0;
        for (var n = 0; n < Frame; n++) { inPower += input[n] * input[n]; outPower += output[n] * output[n]; }
        if (inPower / Frame > Loud && outPower / Frame > Loud / 100)
        {
            for (var k = 0; k < MostHops; k++)
            {
                var from = at - (k + 1) * Hop;
                double xy = 0, xx = 0;
                for (var n = 0; n < Frame; n++)
                {
                    double x = _history[from + n];
                    xy += x * output[n];
                    xx += x * x;
                }
                _score[k] += xy / Math.Sqrt(xx * outPower + 1e-30);
            }
            if (++_votes == Votes) Decide();
        }
        _history.AsSpan(Frame).CopyTo(_history);
    }

    /// <summary>A new user: back to the plugin's starting delay until measured again.</summary>
    internal void Reset()
    {
        Array.Clear(_score);
        _votes = 0;
        _frames = 0;
        Samples = Start;
    }

    // The hop count the ten frames matched best, when it clearly did (its average correlation over 0.5, and 0.1 over the next).
    private void Decide()
    {
        int best = 0, next = -1;
        for (var k = 1; k < MostHops; k++) if (_score[k] > _score[best]) best = k;
        for (var k = 0; k < MostHops; k++) if (k != best && (next < 0 || _score[k] > _score[next])) next = k;
        if (_score[best] / Votes > 0.5 && (_score[best] - _score[next]) / Votes > 0.1) Samples = (best + 1) * Hop;
        Array.Clear(_score);
        _votes = 0;
    }
}

/// <summary>
/// Whether DeepFilterNet runs light, from how long each 20 ms run takes. A run waits on the plugin's worker for one
/// 10 ms hop's inference and its 2 ms polls: 4 to 6 ms at rest on a Ryzen 5 5600X, and no slower with every core busy
/// (24 burners at normal priority, or 12 at high), since Exo runs the worker above them. A worker that can't keep up
/// makes runs come back slow, and a late one makes the plugin add 10 ms of delay for 20 s or more (and at a second of it,
/// abort). So, as Krisp swaps its model for a smaller one once 20 frames in a row run over its line, it goes light when
/// <see cref="SlowRuns"/> of the last <see cref="Window"/> runs took over <see cref="SlowMs"/> (half the frame: the worker
/// at three quarters of its hop or more); one stall alone (a garbage collection, a busy moment elsewhere in Exo) never
/// does, which a late-run rule did at rest. Light is about 30% less CPU (25% of a core to 18% on that Ryzen, a fan behind
/// speech) at the same delay. It goes back once a span of runs had almost none slow (<see cref="SlowShare"/>): the span is
/// <see cref="Settle"/> runs (20 s, the plugin's own wait before it takes its added delay back) the first two times, then
/// longer each time it had to go light again, as Krisp's wait grows (Fibonacci: 20, 20, 40, 60, 100 s, at most 13 spans,
/// about 4 minutes), so a PC that stays busy doesn't swap back and forth; a new call starts over (<see cref="Reset"/>).
/// </summary>
internal sealed class Overload
{
    internal const int Window = 20, SlowRuns = 15, Settle = 1000;
    internal const double SlowMs = 10, SlowShare = 0.01;
    private readonly bool[] _last = new bool[Window];
    private int _at, _slowInWindow, _settled, _slow, _times;

    internal bool Light { get; private set; }

    /// <summary>How many runs it waits, light, before going back: <see cref="Settle"/> times a Fibonacci number of the times it went light.</summary>
    internal int SettleRuns
    {
        get
        {
            int a = 1, b = 1;
            for (var i = 1; i < _times; i++) (a, b) = (b, Math.Min(a + b, 13));
            return Settle * a;
        }
    }

    /// <summary>One run's time in milliseconds.</summary>
    internal void Add(double ms)
    {
        var slow = ms > SlowMs;
        if (Light)
        {
            if (slow) _slow++;
            if (++_settled < SettleRuns) return;
            if (_slow <= _settled * SlowShare) Back();
            (_settled, _slow) = (0, 0);
            return;
        }
        if (_last[_at]) _slowInWindow--;
        _last[_at] = slow;
        if (slow) _slowInWindow++;
        _at = (_at + 1) % Window;
        if (_slowInWindow < SlowRuns) return;
        Light = true;
        _times++;
        (_settled, _slow) = (0, 0);
    }

    private void Back()
    {
        Light = false;
        Array.Clear(_last);
        _slowInWindow = 0;
    }

    /// <summary>A new call: full again, and the first wait.</summary>
    internal void Reset()
    {
        Back();
        (_settled, _slow, _times) = (0, 0, 0);
    }
}
