using ExoLauncher.Helpers;

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
    private static bool _monitor;

    // The meter's own voice in the speaker mix while Hear Your Voice is on: never a call's SSRC.
    private const uint MonitorSsrc = 0x45584F;

    /// <summary>Whether the speakers play back the voice as the call sends it (Hear Your Voice), and whether they are headphones.</summary>
    internal static (bool On, bool OnHead) Monitoring => (_monitor, _onHead);

    /// <summary>
    /// Hear Your Voice: the mic's frames, processed exactly as a call sends them, mixed onto the speakers. With the
    /// speakers not on the head the echo canceller is up too (it only exists when both sides run), so the played-back
    /// voice doesn't come straight back through the mic; a real howl is still possible on loud speakers.
    /// </summary>
    internal static void Monitor(bool on)
    {
        lock (Gate)
        {
            if (_monitor == on) return;
            _monitor = on;
            AppLog.Info($"Discord voice: hear your voice {(on ? "on" : "off")}.");
        }
        // The render side (and its echo canceller) can't join a capture already running: the meter restarts.
        if (on || _audio is not null) { StopAudio(); Start(); }
    }

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
            // The last 8 s as the chain cleaned it, kept so Teach Exo My Voice learns from live talking, not a recording.
            var take = new float[1000][]; // 20 s: a teach takes eight seconds of speech out of what the meter heard
            var takeVoiced = new bool[400];
            var takeAt = 0;
            for (var i = 0; i < take.Length; i++) take[i] = new float[Opus.FrameSamples];
            audio.Captured += frame =>
            {
                var processing = VoiceProcessing.Current;
                before = MathF.Max(before, Level(frame));
                var open = chain.Process(frame, processing, audio.MicId, audio.SystemGain);
                judged = MathF.Max(judged, chain.GateLevel);
                for (var i = 0; i < Opus.FrameSamples; i++) take[takeAt][i] = (frame[2 * i] + frame[2 * i + 1]) * 0.5f;
                takeVoiced[takeAt] = open;
                takeAt = (takeAt + 1) % take.Length;
                lock (Gate) _take = (take, takeVoiced, takeAt, Math.Min(frames + 1, take.Length));
                if (open) after = MathF.Max(after, Level(frame));
                if (_monitor && open)
                {
                    // What the call sends: the lead-in ahead of the opening, then the frame itself; held-back sound
                    // queues nothing, so the speakers go quiet exactly as the call does.
                    foreach (var lead in chain.TakeLead()) Play(audio, lead);
                    Play(audio, frame);
                }
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
            audio.Start(capture: true, render: _monitor);
            _audio = audio;
            _chain = chain;
            // The page renews every 5 s but a throttled WebView can drop several: outlast 45 s of misses, not 15.
            _watchdog = new Timer(_ => { if (Environment.TickCount64 - _renewedAt > 45_000) Stop(); }, null, 5000, 5000);
        }
    }

    internal static void Stop()
    {
        _monitor = false; // the page left: hear your voice ends with the meter
        StopAudio();
    }

    private static (float[][] Ring, bool[] Voiced, int At, int Count)? _take;

    /// <summary>The meter's last seconds as cleaned frames and the gate's verdicts, oldest first; null before any frame.</summary>
    internal static (float[] Cleaned, bool[] Voiced)? Take()
    {
        lock (Gate)
        {
            if (_take is not { Count: > 0 } t) return null;
            var cleaned = new float[t.Count * Opus.FrameSamples];
            var voiced = new bool[t.Count];
            for (var i = 0; i < t.Count; i++)
            {
                var at = (t.At - t.Count + i + t.Ring.Length) % t.Ring.Length;
                Array.Copy(t.Ring[at], 0, cleaned, i * Opus.FrameSamples, Opus.FrameSamples);
                voiced[i] = t.Voiced[at];
            }
            return (cleaned, voiced);
        }
    }

    private static void StopAudio()
    {
        VoiceAudio? audio;
        VoiceChain? chain;
        lock (Gate)
        {
            (audio, chain) = (_audio, _chain);
            (_audio, _chain) = (null, null);
            _take = null;
            _watchdog?.Dispose();
            _watchdog = null;
        }
        audio?.Dispose();
        // After the capture thread stopped: it is the chain's only user.
        chain?.Dispose();
    }

    private static void Play(VoiceAudio audio, float[] frame)
    {
        var copy = audio.Rent();
        frame.CopyTo(copy, 0);
        audio.Play(MonitorSsrc, copy, level: false);
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
