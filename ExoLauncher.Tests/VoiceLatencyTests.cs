using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// The microphone's delay as app.log reports it. Ways it could mislead: the voice's age counted from the frame's own
/// first sample while the packet carries older sound (what noise suppression and Voice Focus hold back), the stages
/// not adding up to the age, or noise suppression's delay misread (a voice's pitch repeats, so a hop off can look
/// like a match).
/// </summary>
public sealed class VoiceLatencyTests
{
    private const int Rate = 48000, Frame = 960;

    // A frame whose first sample was captured at t, whole 20 ms later, taken up 1 ms after that, through the chain in
    // 2.5 ms and on the socket 0.5 ms later, with 20 ms held back by noise suppression: its oldest sound is 44 ms old.
    [Fact]
    public void TheAgeAtTheSocketCountsWhatTheChainHeldBack()
    {
        const long t = 5_000_000_000, ms = 10_000;
        Assert.Equal(t - 20 * ms, VoiceLatency.AudioAt(t, 960));
        Assert.Equal(t - 5 * ms, VoiceLatency.AudioAt(t, 240));
        var latency = new VoiceLatency();
        latency.Add(t, t + 20 * ms, t + 21 * ms, t + 235 * ms / 10, t + 24 * ms, heldSamples: 960);
        latency.Add(t, t + 20 * ms, t + 21 * ms, t + 235 * ms / 10, t + 24 * ms, heldSamples: 480);
        var a = latency.Averages();
        Assert.Equal(2, latency.Packets);
        Assert.Equal(39.0, a.Age, 6); // 44 and 34
        Assert.Equal(44.0, a.Max, 6);
        Assert.Equal((20.0, 15.0, 1.0, 2.5, 0.5), (a.Capture, a.Held, a.Queued, a.Chain, a.Send));
        Assert.Equal(a.Age, a.Capture + a.Held + a.Queued + a.Chain + a.Send, 6);
        latency.Reset();
        Assert.Equal(0, latency.Packets);
    }

    // DeepFilterNet's delay is a whole number of its 10 ms hops: each is found from the sound, broadband noise and a
    // voice alike (the voice's pitch at 100 Hz repeats every 480 samples, a hop exactly, so only its pitch moving tells).
    [Theory]
    [InlineData(480, false)]
    [InlineData(960, false)]
    [InlineData(1440, false)]
    [InlineData(1920, false)]
    [InlineData(480, true)]
    [InlineData(960, true)]
    [InlineData(1440, true)]
    [InlineData(1920, true)]
    public void NoiseSuppressionsDelayIsMeasuredFromTheSound(int delay, bool voice)
    {
        var tracker = new DelayTracker();
        Assert.Equal(DelayTracker.Start, tracker.Samples);
        // From another delay first, so the one found is measured, not the one it starts at.
        Feed(tracker, voice, delay == 1920 ? 480 : 1920, new Random(delay));
        Assert.NotEqual(delay, tracker.Samples);
        Feed(tracker, voice, delay, new Random(delay + 1));
        Assert.Equal(delay, tracker.Samples);
        tracker.Reset();
        Assert.Equal(DelayTracker.Start, tracker.Samples);
    }

    // 30 frames of sound and what noise suppression would give back for each: the input that many samples back,
    // turned down, with a little left of something else.
    private static void Feed(DelayTracker tracker, bool voice, int delay, Random random)
    {
        var input = voice ? Voice(30 * Frame, random) : Noise(30 * Frame, random);
        var output = new float[Frame];
        for (var f = 0; f < 30; f++)
        {
            for (var n = 0; n < Frame; n++)
            {
                var at = f * Frame + n - delay;
                output[n] = (at >= 0 ? 0.5f * input[at] : 0) + 0.002f * (float)(random.NextDouble() - 0.5);
            }
            tracker.Add(input.AsSpan(f * Frame, Frame), output);
        }
    }

    [Fact]
    public void SilenceLeavesTheDelayAsItWas()
    {
        var tracker = new DelayTracker();
        var quiet = new float[Frame];
        for (var f = 0; f < 50; f++) tracker.Add(quiet, quiet);
        Assert.Equal(DelayTracker.Start, tracker.Samples);
    }

    private static float[] Noise(int length, Random random)
    {
        var x = new float[length];
        for (var n = 0; n < length; n++) x[n] = 0.1f * (float)(random.NextDouble() * 2 - 1);
        return x;
    }

    // A voice at about -30 dBFS: glottal pulses gliding 95-140 Hz through a formant.
    private static float[] Voice(int length, Random random)
    {
        var x = new float[length];
        double phase = 0, y0 = 0, y1 = 0;
        var r = Math.Exp(-Math.PI * 100 / Rate);
        for (var n = 0; n < length; n++)
        {
            var f0 = 117.5 + 22.5 * Math.Sin(2 * Math.PI * n / (0.4 * Rate));
            phase += f0 / Rate;
            var pulse = phase >= 1 ? 1.0 : 0;
            if (phase >= 1) phase -= 1;
            var y = pulse + 2 * r * Math.Cos(2 * Math.PI * 650 / Rate) * y0 - r * r * y1;
            (y1, y0) = (y0, y);
            x[n] = (float)(1.5 * y * (1 - r)) + 0.0005f * (float)(random.NextDouble() - 0.5);
        }
        return x;
    }
}
