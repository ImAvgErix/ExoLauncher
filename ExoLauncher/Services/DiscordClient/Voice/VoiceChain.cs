using System.Globalization;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// What the chain did to the last frame. Suppression: "standard" (WebRTC's own, Discord's Standard: <see cref="WebRtcNoise"/>), "on" (DeepFilterNet ran, turning noise down by up to SuppressionLimitDb), "light" (its first stage alone, while the CPU can't keep up with both: <see cref="Overload"/>),
/// "mic" (the microphone's own stands in), "loading", "missing" (its plugin isn't here or won't load) or "off". Gain: "windows" (Windows' own, where the microphone's driver keeps it on) or "off". Tuned: a kept Voice Tune tuning (or Voice Tune's take) set the
/// suppression's strength. app.log says it as the chain runs, in its order.
/// </summary>
internal readonly record struct VoiceStages(string Suppression, int SuppressionLimitDb, bool Focus, string Gain, bool Tuned)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"low cut, noise suppression {Suppression}{(Suppression is "on" or "light" ? $" (up to {SuppressionLimitDb} dB)" : "")}, {(Focus ? "Voice Focus" : "gate")}, gain control {Gain}{(Focus ? "" : ", release fade")}, {(Tuned ? "tuned for this mic" : "not tuned")}");
}

/// <summary>
/// The microphone's processing, one chain for a call, Voice Check and Voice Tune's "after", so what they let you
/// hear is what the call sends, in WebRTC's order (Chrome's and Discord's): the low cut and the knock detector; noise suppression, Exo AI (DeepFilterNet at the level Krisp runs its own model at, see
/// <see cref="NoiseSuppressor"/>) or Standard (WebRTC's own, <see cref="WebRtcNoise"/>), stepped aside where the
/// microphone's own is as good, e.g. NVIDIA Broadcast; then Voice Focus or the level gate, which decides on the cleaned
/// frame, measured against the microphone's sound it came from (<see cref="NoiseSuppressor.InputDb"/>, suppression's
/// delay back) and against floors learned from both, so the room's fans and hum never open it
/// (<see cref="VoiceGate.Voiced"/>); and the gate's release fade and lead-in (Discord's 100 ms before a
/// word). Nothing waits on a later frame: none of it adds delay. Fed 20 ms stereo frames in place, from one thread.
/// </summary>
internal sealed class VoiceChain : IDisposable
{
    private readonly VoiceGate _gate = new();
    private readonly LowCut _lowCut = new();
    private readonly KnockDetector _knock = new();
    private WebRtcNoise? _standard;
    private NoiseSuppressor? _suppressor;
    private MyVoice? _myVoice;
    private Task<MyVoice?>? _myVoiceLoading;
    private int _myVoiceRetryIn;
    private VoiceFocus? _focus;
    // The kept tuning for the microphone open now, read again only when the mic or the kept tunings change.
    private (string? Mic, Dictionary<string, string>? Tunes, VoiceTuning? Tuning) _kept;

    /// <summary>A tuning to use instead of the microphone's kept one (Voice Tune's take, before it is kept).</summary>
    internal VoiceTuning? Tuning { get; init; }

    /// <summary>Whether the microphone's own noise suppression stands in for Exo's, when already known (else learned off this thread).</summary>
    internal bool? StepsAside { get; init; }

    /// <summary>
    /// Muted: Voice Focus drops the frame it holds, so unmuting never sends sound from before, and the gate's hold ends
    /// (cut at once, no fade: mute is meant to be instant); the first frame after it fades in.
    /// </summary>
    internal void Mute()
    {
        _focus?.Mute();
        _gate.Mute();
        _sending = false;
        _leadCount = 0;
        _leadReady = false;
    }

    /// <summary>What ran on the last frame (<see cref="Process"/> sets it).</summary>
    internal VoiceStages Stages { get; private set; }

    /// <summary>
    /// A recording's chain (<see cref="RecordedVoice"/>), not a call's: the call's noise suppression and level, but no gate
    /// or fade (the voice runs on whole).
    /// </summary>
    internal bool Recording { get; init; }

    /// <summary>
    /// A recording's tap on a call's chain (<see cref="RecordedVoice"/>): each frame cleaned but not yet faded, led in or
    /// focused, with the gate's verdict and the chain's delay then, so one noise suppression serves both. Read at once:
    /// the frame goes on to be sent.
    /// </summary>
    internal Action<float[], bool, int>? Cleaned { get; set; }


    /// <summary>The noise gate's frames let through and held shut, and the loudest held shut, since the last read.</summary>
    internal (int Open, int Shut, float LoudestShut) TakeGateStats() => _gate.TakeStats();

    /// <summary>Whether the gate let the last frame through, before Only My Voice held it back (Voice Check learns from these).</summary>
    internal bool Voiced { get; private set; }

    /// <summary>The last frame's level as the gate judged it (dBFS): after noise suppression, before gain control.</summary>
    internal float GateLevel => _gate.Level;

    /// <summary>
    /// How far behind the microphone the last frame's sound came out of the chain, in samples at 48 kHz: noise
    /// suppression's measured delay and Voice Focus's, where they ran.
    /// </summary>
    internal int DelaySamples { get; private set; }

    /// <summary>Runs one frame through in place; whether the gate is open (voice to send).</summary>
    internal bool Process(float[] frame, VoiceProcessing processing, string? micId, bool systemGain)
    {
        // First, the low cut: what is under a voice (a desk's knocks through the stand, rumble, breath pops) never reaches
        // gain control, suppression or the gate, which read it as sound to send.
        _lowCut.Process(frame);
        // A knock is judged in the frame it comes out of suppression in, its delay later.
        _knocks = (_knocks << 1) | (_knock.Hit(frame) ? 1u : 0u);
        var exo = processing.Profile == "exo";
        // A kept Voice Tune is no longer used: Exo Voice is one fixed chain (only Voice Tune's own take still passes one).
        var tuning = exo ? Tuning : null;
        var limit = Math.Min(tuning?.SuppressionLimitDb ?? NoiseSuppressor.GentleDb, NoiseSuppressor.MostDb);
        var suppression = "off";
        float? heard = null, speech = null;
        DelaySamples = 0;
        // A mic that cleans itself (NVIDIA Broadcast, AMD Noise Suppression and the like): Exo's steps aside, so nothing runs twice.
        if (processing.NoiseSuppression && StepsAside == true) suppression = "mic";
        else if (processing.NoiseSuppression && processing.Suppressor == "standard")
        {
            if ((_standard ??= WebRtcNoise.Create()) is { } standard)
            {
                standard.Process(frame);
                heard = standard.InputDb;
                suppression = "standard";
            }
            else suppression = "missing";
        }
        else if (processing.NoiseSuppression && Suppressor() is { } suppressor)
        {
            suppressor.Process(frame, limit);
            (heard, speech) = (suppressor.InputDb, suppressor.SpeechDb);
            suppression = suppressor.Load.Light ? "light" : "on";
            DelaySamples = suppressor.DelaySamples;
        }
        else if (processing.NoiseSuppression) suppression = _missing ? "missing" : "loading";
        Stages = new VoiceStages(suppression, limit, processing.Focus, systemGain ? "windows" : "off", tuning is not null);
        var focus = processing.Focus && !Recording;
        var cleaned = Cleaned;
        var open = false;
        if (!focus || cleaned is not null)
        {
            // The gate judges what suppression left (Discord's gate reads Krisp's verdict the same way), against the sound it came from.
            var behind = Math.Min(DelaySamples / Opus.FrameSamples, 30);
            var knock = (_knocks >> behind & 1) != 0;
            open = _gate.Voiced(frame, processing.AutoSensitivity, processing.Threshold, heard, speech, knock);
            // Only My Voice: a voice judged not the user's is held back (MyVoice), judged on what the gate let through.
            if (!focus)
            {
                Voiced = open;
                if (processing.OnlyMyVoice && Mine() is { } mine)
                {
                    mine.Feed(frame, open);
                    if (open && mine.HoldingBack) open = false;
                }
            }
            cleaned?.Invoke(frame, open, DelaySamples);
        }
        if (focus)
        {
            _focus ??= new VoiceFocus();
            DelaySamples += VoiceFocus.DelaySamples;
            return _focus.Process(frame, processing.AutoSensitivity, processing.Threshold, false);
        }
        // Push to Talk sends by its key, whatever the gate says: nothing to fade or lead in.
        if (!processing.PushToTalk && !Recording)
        {
            Fade(frame, open);
            Lead(frame, open);
        }
        return open;
    }

    // The gate's release: the hold's last frame fades out over its 20 ms (a raised cosine, to silence on its last
    // sample), so a voice never ends on a step the other side hears as a click, and Opus's last frame ends at silence.
    // The next opening fades in over its first 2.5 ms from there. Mute cuts at once instead (see Mute).
    private static readonly float[] Release = Ramp(Opus.FrameSamples, down: true), Attack = Ramp(120, down: false);
    private bool _sending;

    private void Fade(float[] frame, bool open)
    {
        // An opening with a lead-in fades in at the lead-in's first frame instead (see Lead).
        var opening = _leadCount > 0 ? _lead[(_leadAt - _leadCount + LeadFrames) % LeadFrames] : frame;
        if (open && !_sending)
            for (var i = 0; i < Attack.Length; i++) { opening[2 * i] *= Attack[i]; opening[2 * i + 1] *= Attack[i]; }
        if (open && _gate.Closing)
            for (var i = 0; i < Release.Length; i++) { frame[2 * i] *= Release[i]; frame[2 * i + 1] *= Release[i]; }
        _sending = open;
    }

    // Discord's vadLeading (5 frames): the 100 ms before the gate opens are kept, and go out just ahead of the frame that
    // opened it, so a word's first sound (its consonant, under what opens the gate) is never cut. Kept only while shut.
    internal const int LeadFrames = 5;
    private readonly float[][] _lead = [.. Enumerable.Range(0, LeadFrames).Select(_ => new float[VoiceAudio.FrameFloats])];
    private int _leadAt, _leadCount;
    private bool _leadReady;

    private void Lead(float[] frame, bool open)
    {
        if (open) { _leadReady = !_leadReady && _leadCount > 0 && !_wasOpen; _wasOpen = true; if (!_leadReady) _leadCount = 0; return; }
        _wasOpen = false;
        _leadReady = false;
        frame.CopyTo(_lead[_leadAt], 0);
        _leadAt = (_leadAt + 1) % LeadFrames;
        _leadCount = Math.Min(_leadCount + 1, LeadFrames);
    }

    private bool _wasOpen;

    /// <summary>
    /// On the frame that opened the gate: the frames just before it to send first, oldest first (none otherwise). Each
    /// is the chain's own buffer, good until the next frame.
    /// </summary>
    internal IEnumerable<float[]> TakeLead()
    {
        if (!_leadReady) yield break;
        _leadReady = false;
        var count = _leadCount;
        _leadCount = 0;
        for (var i = count; i > 0; i--) yield return _lead[(_leadAt - i + LeadFrames) % LeadFrames];
    }

    private static float[] Ramp(int samples, bool down)
    {
        var ramp = new float[samples];
        for (var i = 0; i < samples; i++)
            ramp[i] = down ? 0.5f * (1 + MathF.Cos(MathF.PI * (i + 1) / samples)) : 0.5f * (1 - MathF.Cos(MathF.PI * i / samples));
        return ramp;
    }

    /// <summary>
    /// Loads noise suppression now, on this thread (Voice Check and Voice Tune, before their first frame, so it is
    /// cleaned like every other). A call needn't: its chain loads it off the audio thread while the first frames pass.
    /// </summary>
    internal void Prepare(VoiceProcessing processing)
    {
        if (!processing.NoiseSuppression) return;
        if (processing.Suppressor != "standard") _suppressor ??= NoiseSuppressor.Create();
    }

    // The voiceprint and model are read off the audio thread; until they are, nothing is held back.
    private MyVoice? Mine()
    {
        if (_myVoice is not null) return _myVoice;
        if (_myVoiceLoading is null && --_myVoiceRetryIn <= 0) _myVoiceLoading = Task.Run(MyVoice.Create);
        if (_myVoiceLoading is not { IsCompleted: true } done) return null;
        _myVoiceLoading = null;
        _myVoice = done.IsCompletedSuccessfully ? done.Result : null;
        if (_myVoice is null) _myVoiceRetryIn = 250; // the model or voiceprint isn't readable: looked for again in 5 s
        return _myVoice;
    }

    private uint _knocks;

    // DeepFilterNet's model takes a moment to load (and its plugin a hash check the first time): never on the audio
    // thread. Until it is in, frames pass as they are; while the plugin isn't here (still downloading), it is looked
    // for again each second.
    private NoiseSuppressor? Suppressor()
    {
        if (_suppressor is not null) return _suppressor;
        if (_loading is { IsCompleted: true } done)
        {
            _loading = null;
            _suppressor = done.IsCompletedSuccessfully ? done.Result : null;
            _missing = _suppressor is null;
            if (_missing) _retryIn = 50;
            return _suppressor;
        }
        if (_loading is null && --_retryIn <= 0) _loading = Task.Run(NoiseSuppressor.Create);
        return null;
    }

    private Task<NoiseSuppressor?>? _loading;
    private int _retryIn;
    private bool _missing;

    /// <summary>Whether the chosen microphone's own noise suppression is as good as Exo's (reads the device: not for the audio thread).</summary>
    internal static bool MicStepsAside(string? chosen) =>
        AudioDevices.Identify(chosen) is { } id && MicCatalog.StandsDown(MicCatalog.Describe(id), MicStage.NoiseSuppression);

    private VoiceTuning? Kept(string? mic)
    {
        var tunes = App.Services.Settings.Shared.DiscordVoiceTunes;
        if (mic != _kept.Mic || !ReferenceEquals(tunes, _kept.Tunes)) _kept = (mic, tunes, VoiceTune.For(mic));
        return _kept.Tuning;
    }


    public void Dispose()
    {
        _standard?.Dispose();
        _standard = null;
        _suppressor?.Dispose();
        _suppressor = null;
        // One still loading is let go of when it is in.
        _loading?.ContinueWith(t => t.Result?.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        _loading = null;
    }
}

/// <summary>
/// The microphone's low cut: a 4th-order Butterworth high-pass at 80 Hz (two biquads, as WebRTC's), per channel of the
/// interleaved stereo frame. A voice's lowest fundamental is about 85 Hz; a knock on the desk, a stand's rumble and a
/// pop are mostly under it.
/// </summary>
internal sealed class LowCut
{
    private const double Hz = 80, Rate = 48000;
    private readonly double[] _b = new double[3], _a = new double[2];
    // Per channel, per stage: x1, x2, y1, y2.
    private readonly double[,] _state = new double[4, 4];

    internal LowCut()
    {
        var w = 2 * Math.PI * Hz / Rate;
        var alpha = Math.Sin(w) / (2 * 0.7071);
        var cos = Math.Cos(w);
        var a0 = 1 + alpha;
        _b[0] = (1 + cos) / 2 / a0; _b[1] = -(1 + cos) / a0; _b[2] = (1 + cos) / 2 / a0;
        _a[0] = -2 * cos / a0; _a[1] = (1 - alpha) / a0;
    }

    internal void Process(float[] stereo)
    {
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            stereo[i] = (float)Run(Run(stereo[i], 0), 1);
            stereo[i + 1] = (float)Run(Run(stereo[i + 1], 2), 3);
        }
    }

    private double Run(double x, int at)
    {
        var y = _b[0] * x + _b[1] * _state[at, 0] + _b[2] * _state[at, 1] - _a[0] * _state[at, 2] - _a[1] * _state[at, 3];
        _state[at, 1] = _state[at, 0]; _state[at, 0] = x;
        _state[at, 3] = _state[at, 2]; _state[at, 2] = y;
        return y;
    }
}

/// <summary>
/// A knock, tap or click (the desk through the mic's stand, a key, the mouse): sound that jumps more than 10 dB within 5 ms
/// from what came just before, to a level that matters (-55 dBFS and up), and has no pitch. A word can start that fast in a
/// quiet room too, but a voice repeats itself (its pitch, 80 to 400 Hz: the frame's normalized autocorrelation at that
/// lag is high) where a knock is noise. Read on the microphone's sound after the low cut, before anything else.
/// </summary>
internal sealed class KnockDetector
{
    private const int Block = 240; // 5 ms of a channel at 48 kHz
    // 10 dB in 5 ms: a light tap or a mouse click over a fan's noise jumps that much (20 let them through); a voice that
    // starts as fast has a pitch and is told apart by it.
    private const float Jump = 10f, Loud = -55f;
    private float _last = -100f;

    /// <summary>Whether a knock begins in this 20 ms stereo frame.</summary>
    internal bool Hit(float[] stereo)
    {
        var hit = false;
        var samples = stereo.Length / 2;
        for (var start = 0; start + Block <= samples; start += Block)
        {
            double sum = 0;
            for (var i = start; i < start + Block; i++) sum += stereo[i * 2] * (double)stereo[i * 2];
            var db = (float)(10 * Math.Log10(sum / Block + 1e-10));
            if (db >= Loud && db - _last > Jump) hit = true;
            _last = db;
        }
        return hit && !Pitched(stereo, samples);
    }

    // The frame's normalized autocorrelation at any lag of 2.5 to 12.5 ms (400 to 80 Hz) over 0.5: a voice's pitch.
    private static bool Pitched(float[] stereo, int samples)
    {
        for (var lag = 120; lag <= 600 && lag < samples / 2; lag += 4)
        {
            double cross = 0, a = 0, b = 0;
            for (var i = 0; i + lag < samples; i++)
            {
                double x = stereo[i * 2], y = stereo[(i + lag) * 2];
                cross += x * y; a += x * x; b += y * y;
            }
            if (cross / Math.Sqrt(a * b + 1e-20) > 0.5) return true;
        }
        return false;
    }
}
