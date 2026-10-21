namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// The live microphone in Discord settings > Voice & Video: while the page shows it, the chosen microphone is opened
/// (shared, alongside a call if there is one, as a call opens it) and run through the call's own chain
/// (<see cref="VoiceChain"/>, with the kept tuning), so what it shows is what a call sends. About 16 times a second
/// discord.micLevel carries the loudest level going in, as the gate judged it (after noise suppression: the Input
/// Sensitivity meter) and coming out since the last one (dBFS, -100 for silence; out is silence while the gate holds
/// the voice back), whether the gate is open, and what ran on the sound (echo cancellation as a call on these speakers
/// would run it: this meter plays nothing, so there is no echo to take out). The page
/// renews it every few seconds; left alone for 15 seconds (the page closed), it stops by itself.
/// </summary>
internal static class MicTest
{
    private static readonly Lock Gate = new();
    private static VoiceAudio? _audio;
    private static VoiceChain? _chain;
    private static Timer? _watchdog;
    private static long _renewedAt;
    private static volatile bool _onHead;

    internal static void Start()
    {
        lock (Gate)
        {
            _renewedAt = Environment.TickCount64;
            if (_audio is not null) return;
            OutputChanged();
            var audio = new VoiceAudio();
            var chain = new VoiceChain();
            var frames = 0;
            float before = -100f, judged = -100f, after = -100f;
            var voiced = false;
            (VoiceStages, string) shown = default;
            object? stages = null;
            audio.Captured += frame =>
            {
                var processing = VoiceProcessing.Current;
                before = MathF.Max(before, Level(frame));
                var open = chain.Process(frame, processing, audio.MicId, audio.SystemGain);
                judged = MathF.Max(judged, chain.GateLevel);
                if (open) after = MathF.Max(after, Level(frame));
                voiced |= open;
                if (++frames % 3 != 0) return;
                // The stages go as they are, rebuilt only when they change.
                var now = (chain.Stages, !processing.EchoCancellation ? "off" : _onHead ? "head" : "on");
                if (stages is null || now != shown)
                {
                    shown = now;
                    stages = Describe(now.Stages, now.Item2, processing);
                }
                DiscordSession.Publish("discord.micLevel", new { level = MathF.Round(before, 1), gateLevel = MathF.Round(judged, 1), after = MathF.Round(after, 1), voiced, stages });
                (before, judged, after, voiced) = (-100f, -100f, -100f, false);
            };
            audio.Start(capture: true, render: false);
            _audio = audio;
            _chain = chain;
            _watchdog = new Timer(_ => { if (Environment.TickCount64 - _renewedAt > 15_000) Stop(); }, null, 5000, 5000);
        }
    }

    internal static void Stop()
    {
        VoiceAudio? audio;
        VoiceChain? chain;
        lock (Gate)
        {
            (audio, chain) = (_audio, _chain);
            (_audio, _chain) = (null, null);
            _watchdog?.Dispose();
            _watchdog = null;
        }
        audio?.Dispose();
        // After the capture thread stopped: it is the chain's only user.
        chain?.Dispose();
    }

    /// <summary>Speakers were chosen (or the page opened): whether they are on your head, read off the audio thread.</summary>
    internal static void OutputChanged() => _onHead = AudioDevices.OnYourHead(App.Services.Settings.Current.DiscordOutputDevice);

    /// <summary>A new microphone was chosen: the meter follows it.</summary>
    internal static void Reopen()
    {
        lock (Gate) _audio?.Reopen(captureSide: true);
    }

    private static float Level(float[] frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        return MathF.Max(-100f, 10 * MathF.Log10((float)(sum / frame.Length) + 1e-10f));
    }

    private static object Describe(VoiceStages s, string echo, VoiceProcessing processing) => new
    {
        profile = processing.Profile,
        suppression = s.Suppression,
        suppressionLimitDb = s.SuppressionLimitDb,
        focus = s.Focus,
        gain = s.Gain,
        echo,
        tuned = s.Tuned,
    };
}
