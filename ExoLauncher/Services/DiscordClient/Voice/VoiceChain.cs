using System.Globalization;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// What the chain did to the last frame. Suppression: "on" (DeepFilterNet ran, turning noise down by up to
/// SuppressionLimitDb), "light" (its first stage alone, while the CPU can't keep up with both: <see cref="Overload"/>),
/// "mic" (the microphone's own stands in), "loading", "missing" (its plugin isn't here or won't load) or "off". Gain: "exo", "windows" or "off". Tuned: a kept Voice Tune tuning (or Voice Tune's take) set the
/// suppression's strength.
/// </summary>
internal readonly record struct VoiceStages(string Suppression, int SuppressionLimitDb, bool Focus, string Gain, bool Tuned)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"noise suppression {Suppression}{(Suppression is "on" or "light" ? $" (up to {SuppressionLimitDb} dB)" : "")}, Voice Focus {(Focus ? "on" : "off")}, gain control {Gain}, {(Tuned ? "tuned for this mic" : "not tuned")}");
}

/// <summary>
/// The microphone's processing, one chain for a call, Voice Check and Voice Tune's "after", so what they let you
/// hear is what the call sends, in this order: Exo's gain control where Windows' isn't on, bringing a quiet microphone up
/// to a speaking level (<see cref="InputLeveler"/>); noise suppression (at the kept tuning's strength or a gentle one, never
/// more than <see cref="NoiseSuppressor.MostDb"/>, and stepped aside in Exo Voice where the microphone's own is as good,
/// e.g. NVIDIA Broadcast); then Voice Focus or the level gate, which decides on the cleaned frame, measured against the
/// microphone's sound it came from (<see cref="NoiseSuppressor.InputDb"/>, suppression's delay back) and against floors
/// learned from both, so the room's fans and hum never open it (<see cref="VoiceGate.Voiced"/>); then Clip Guard.
/// Nothing waits on a later frame: the gate adds no delay. Fed 20 ms stereo
/// frames in place, from one thread.
/// </summary>
internal sealed class VoiceChain : IDisposable
{
    private readonly VoiceGate _gate = new();
    private InputLeveler? _leveler;
    private NoiseSuppressor? _suppressor;
    private VoiceFocus? _focus;
    // The kept tuning for the microphone open now, read again only when the mic or the kept tunings change.
    private (string? Mic, Dictionary<string, string>? Tunes, VoiceTuning? Tuning) _kept;

    /// <summary>A tuning to use instead of the microphone's kept one (Voice Tune's take, before it is kept).</summary>
    internal VoiceTuning? Tuning { get; init; }

    /// <summary>Whether the microphone's own noise suppression stands in for Exo's, when already known (else learned off this thread).</summary>
    internal bool? StepsAside { get; init; }

    /// <summary>Muted: Voice Focus drops the frame it holds, so unmuting never sends sound from before.</summary>
    internal void Mute() => _focus?.Mute();

    /// <summary>What ran on the last frame (<see cref="Process"/> sets it).</summary>
    internal VoiceStages Stages { get; private set; }

    /// <summary>The noise gate's frames let through and held shut, and the loudest held shut, since the last read.</summary>
    internal (int Open, int Shut, float LoudestShut) TakeGateStats() => _gate.TakeStats();

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
        var exo = processing.Profile == "exo";
        // A kept Voice Tune is no longer used: Exo Voice is one fixed chain (only Voice Tune's own take still passes one).
        var tuning = exo ? Tuning : null;
        var limit = Math.Min(tuning?.SuppressionLimitDb ?? NoiseSuppressor.GentleDb, NoiseSuppressor.MostDb);
        var suppression = "off";
        NoiseSuppressor? cleaned = null;
        DelaySamples = 0;
        // Gain control first, so suppression and the gate get the voice at a speaking level (see InputLeveler).
        var leveled = processing.AutoGain && !systemGain;
        if (leveled)
        {
            if (_leveler is null || _leveler.Mic != micId) { _leveler?.Keep(); _leveler = new InputLeveler(micId); }
            _leveler.Process(frame);
        }
        // A mic that cleans itself (NVIDIA Broadcast, AMD Noise Suppression and the like): Exo's steps aside, so nothing runs twice.
        if (processing.NoiseSuppression && StepsAside == true) suppression = "mic";
        else if (processing.NoiseSuppression && Suppressor() is { } suppressor)
        {
            suppressor.Process(frame, limit);
            cleaned = suppressor;
            suppression = suppressor.Load.Light ? "light" : "on";
            DelaySamples = suppressor.DelaySamples;
        }
        else if (processing.NoiseSuppression) suppression = _missing ? "missing" : "loading";
        Stages = new VoiceStages(suppression, limit, processing.Focus, leveled ? "exo" : processing.AutoGain ? "windows" : "off", tuning is not null);
        if (processing.Focus)
        {
            _focus ??= new VoiceFocus();
            DelaySamples += VoiceFocus.DelaySamples;
            return _focus.Process(frame, processing.AutoSensitivity, processing.Threshold, false);
        }
        // The gate judges what suppression left (Discord's gate reads Krisp's verdict the same way), against the sound it came from.
        var open = _gate.Voiced(frame, processing.AutoSensitivity, processing.Threshold, cleaned?.InputDb, cleaned?.SpeechDb);
        ClipGuard(frame);
        return open;
    }

    // Clip Guard: a limiter that keeps every sample under -1 dBFS, so gain control or a shout never clips. It turns down at
    // once (no lookahead, so no delay) and comes back over about 50 ms; a voice under the ceiling passes untouched.
    private const float Ceiling = 0.891f;
    private static readonly float Recover = 1 - MathF.Exp(-1f / (0.05f * Opus.SampleRate));
    private float _guard = 1f;

    private void ClipGuard(float[] frame)
    {
        for (var i = 0; i + 1 < frame.Length; i += 2)
        {
            var peak = MathF.Max(MathF.Abs(frame[i]), MathF.Abs(frame[i + 1]));
            if (peak * _guard > Ceiling) _guard = Ceiling / peak;
            frame[i] *= _guard;
            frame[i + 1] *= _guard;
            _guard += (1f - _guard) * Recover;
        }
    }

    /// <summary>
    /// Loads noise suppression now, on this thread (Voice Check and Voice Tune, before their first frame, so it is
    /// cleaned like every other). A call needn't: its chain loads it off the audio thread while the first frames pass.
    /// </summary>
    internal void Prepare(VoiceProcessing processing)
    {
        if (processing.NoiseSuppression) _suppressor ??= NoiseSuppressor.Create();
    }

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
        _leveler?.Keep();
        _suppressor?.Dispose();
        _suppressor = null;
        // One still loading is let go of when it is in.
        _loading?.ContinueWith(t => t.Result?.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        _loading = null;
    }
}
