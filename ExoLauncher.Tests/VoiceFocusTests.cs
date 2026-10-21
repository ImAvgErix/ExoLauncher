using ExoLauncher.Models;
using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Voice Focus on synthetic sound (a pulse train through vowel formants, room noise, hum). Ways it could fail
/// silently: delay added to the voice; the voice dimmed, or its first sound cut by a gate that opens late; a word
/// chopped by the gate closing in a short pause; hum and rumble under the voice; a peak over -1 dBFS reaching Opus;
/// the frame said to be voice when nothing in it is open, or not when it is; allocations on the audio thread; the
/// profile turning it on or off against the setting.
/// </summary>
public sealed class VoiceFocusTests
{
    private const int Rate = 48000, Frame = 960;

    // Each 1 ms of the output against the input is the same sound at the same time: nothing is held back.
    [Fact]
    public void AddsNoDelay()
    {
        Assert.Equal(0, VoiceFocus.DelaySamples);
        var sound = new float[Rate];
        var noise = Noise(Rate / 2, -20, new Random(11));
        noise.CopyTo(sound, Rate / 4);
        var (output, _) = Run(sound);
        var (from, to) = (Rate / 4 + Rate / 10, Rate / 4 + Rate * 4 / 10); // the gate open throughout
        var best = Enumerable.Range(-48, 97).MaxBy(lag => Correlate(sound, output, from, to, lag));
        Assert.Equal(0, best);
    }

    // A word that starts at once (no easing in) is let through from its first millisecond: mid-frame the gate opened
    // 2 ms before, and where the word starts a frame it is open within 1 ms.
    [Theory]
    [InlineData(10 * Frame + 517)]
    [InlineData(10 * Frame + 240)]
    [InlineData(10 * Frame)]
    public void TheGateOpensForAWordsFirstSound(int start)
    {
        var sound = Noise(Rate, -75, new Random(12));
        var word = Vowel(Rate / 5, 160);
        Scale(word, -36);
        for (var n = 0; n < word.Length; n++) sound[start + n] += word[n];
        var (output, _) = Run(sound);
        const int ms = Rate / 1000;
        if (start % Frame != 0) Assert.InRange(Db(output, start, start + ms) - Db(sound, start, start + ms), -1.0, 0.5);
        else Assert.InRange(Db(output, start + ms, start + 2 * ms) - Db(sound, start + ms, start + 2 * ms), -1.0, 0.5);
        Assert.True(Db(sound, start - 10 * ms, start - 3 * ms) - Db(output, start - 10 * ms, start - 3 * ms) > 30); // shut until 2-3 ms before it
    }

    // Soft and loud syllables with pauses shorter than the hold, over room noise: no syllable is turned down or cut
    // short, and the pauses between them aren't shut either (a gate closing there chops the words apart).
    [Fact]
    public void TheGateNeverChopsSpeech()
    {
        var sound = Noise(5 * Rate, -70, new Random(13));
        var speech = new float[5 * Rate];
        var syllables = Speak(speech, new Random(14), start: 0.5, count: 16, gap: 0.03);
        var rng = new Random(15);
        foreach (var (from, to) in syllables) Scale(speech.AsSpan(from, to - from), rng.Next(2) == 0 ? -46 : -24);
        for (var n = 0; n < sound.Length; n++) sound[n] += speech[n];
        var (output, _) = Run(sound);
        foreach (var (from, to) in syllables) Assert.InRange(Db(output, from, to) - Db(sound, from, to), -0.5, 0.5);
        for (var s = 1; s < syllables.Count; s++)
            Assert.InRange(Db(output, syllables[s - 1].To, syllables[s].From) - Db(sound, syllables[s - 1].To, syllables[s].From), -0.5, 0.5);
    }

    [Fact]
    public void SpeechPassesWithinOneDecibelAndItsFirstSoundIsKept()
    {
        var speech = new float[6 * Rate];
        var syllables = Speak(speech, new Random(1), start: 0.5, count: 8, gap: 0.45); // each from a closed gate
        foreach (var (from, to) in syllables) Scale(speech.AsSpan(from, to - from), -34); // each under the compressor's knee
        var (output, _) = Run(speech);
        foreach (var (from, to) in syllables)
        {
            Assert.InRange(Db(output, from, to) - Db(speech, from, to), -1.0, 1.0);
            // Its first 10 ms: a gate opening late would cost 10 dB or more; the high-pass's few ms of delay on a low voice, up to 2.
            Assert.InRange(Db(output, from, from + Rate / 100) - Db(speech, from, from + Rate / 100), -2.0, 1.0);
        }
    }

    [Fact]
    public void HumAndRumbleUnderTheVoiceAreCut()
    {
        var speech = new float[3 * Rate];
        var syllables = Speak(speech, new Random(6), start: 0.3, count: 12, gap: 0.05);
        Scale(speech, -26);
        var sound = (float[])speech.Clone();
        for (var n = 0; n < sound.Length; n++) sound[n] += 0.05f * MathF.Sin(2 * MathF.PI * 50 * n / Rate) + 0.05f * MathF.Sin(2 * MathF.PI * 25 * n / Rate);
        var (output, _) = Run(sound);
        var (from, to) = (syllables[2].From, syllables[^1].To); // the gate held open throughout
        Assert.True(Tone(sound, from, to, 50) - Tone(output, from, to, 50) >= 15);
        Assert.True(Tone(sound, from, to, 25) - Tone(output, from, to, 25) >= 30);
    }

    [Fact]
    public void NothingLeavesAboveMinusOneDbfs()
    {
        var sound = new float[3 * Rate];
        Speak(sound, new Random(7), start: 0.2, count: 10, gap: 0.05);
        Scale(sound, -6);
        for (var n = 0; n < sound.Length; n++) sound[n] = Math.Clamp(sound[n] * 4, -1f, 1f); // a mic clipping
        for (var n = 0; n < Rate / 1000; n++) sound[Rate + n] = n % 2 == 0 ? 1f : -1f; // a full-scale crack starting a frame
        var (output, _) = Run(sound, level: true);
        Assert.True(output.Max(MathF.Abs) <= 0.8913f);
        Assert.True(output.Max(MathF.Abs) > 0.7f); // limited, not just turned down
    }

    [Fact]
    public void SaysVoiceWhileItIsOpenAndNotAfterTheHold()
    {
        var speech = new float[3 * Rate];
        var syllables = Speak(speech, new Random(9), start: 0.5, count: 3, gap: 0.1);
        Scale(speech, -26);
        var (_, voiced) = Run(speech);
        var first = syllables[0].From / Frame;
        var last = syllables[^1].To / Frame;
        Assert.DoesNotContain(true, voiced[..(first - 1)]);
        Assert.DoesNotContain(false, voiced[first..last]);
        Assert.DoesNotContain(true, voiced[(last + 25)..]); // 250 ms hold, then the fade
    }

    [Fact]
    public void AllocatesNothingPerFrame()
    {
        var focus = new VoiceFocus();
        var sound = new float[3 * Rate];
        Speak(sound, new Random(16), start: 0.2, count: 10, gap: 0.05);
        Scale(sound, -20);
        var frame = new float[Frame * 2];
        void Pass()
        {
            for (var f = 0; f + Frame <= sound.Length; f += Frame)
            {
                for (var n = 0; n < Frame; n++) frame[2 * n] = frame[2 * n + 1] = sound[f + n];
                focus.Process(frame, automatic: true, threshold: -60, level: true);
            }
        }
        Pass();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Pass();
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Theory]
    [InlineData("exo", true)]
    [InlineData(null, false)]
    [InlineData("custom", true)]
    public void CallsNeverRunVoiceFocusWhateverTheOldSettings(string? profile, bool setting) =>
        Assert.False(VoiceProcessing.From(new AppSettings { DiscordInputProfile = profile, DiscordVoiceFocus = setting }).Focus);

    // Through Voice Focus as the call does (stereo frames, automatic sensitivity), any delay it has taken back out:
    // the mono result, and whether each frame that came out was said to hold voice.
    private static (float[] Output, bool[] Voiced) Run(float[] mono, bool level = false)
    {
        var focus = new VoiceFocus();
        var frames = mono.Length / Frame;
        var stream = new float[(frames + 1) * Frame];
        var voiced = new bool[frames];
        var frame = new float[Frame * 2];
        for (var f = 0; f <= frames; f++)
        {
            for (var n = 0; n < Frame; n++) frame[2 * n] = frame[2 * n + 1] = f < frames ? mono[f * Frame + n] : 0;
            var open = focus.Process(frame, automatic: true, threshold: -60, level);
            if (f < frames) voiced[f] = open;
            for (var n = 0; n < Frame; n++) stream[f * Frame + n] = frame[2 * n];
        }
        return (stream[VoiceFocus.DelaySamples..(VoiceFocus.DelaySamples + mono.Length)], voiced);
    }

    // Syllables of a voice: a pulse train at 110-220 Hz with a falling tilt, through three formants of
    // one of five vowels, eased in over 15 ms and out over 40 ms. Their sample ranges.
    private static List<(int From, int To)> Speak(float[] x, Random rng, double start, int count, double gap)
    {
        double[][] vowels = [[730, 1090, 2440], [270, 2290, 3010], [300, 870, 2240], [530, 1840, 2480], [570, 840, 2410]];
        var ranges = new List<(int, int)>();
        var at = (int)(start * Rate);
        for (var s = 0; s < count && at < x.Length; s++)
        {
            var length = (int)((0.15 + 0.2 * rng.NextDouble()) * Rate);
            var formants = vowels[rng.Next(vowels.Length)];
            var f0 = 110 + 110 * rng.NextDouble();
            var y = new double[3, 2];
            double phase = 1, tilt = 0;
            var end = Math.Min(x.Length, at + length);
            for (var n = at; n < end; n++)
            {
                var t = n - at;
                phase += f0 * (1 + 0.1 * t / length) / Rate;
                var pulse = phase >= 1 ? 1.0 : 0;
                if (phase >= 1) phase -= 1;
                tilt = tilt * 0.9 + pulse;
                var v = tilt;
                for (var k = 0; k < 3; k++)
                {
                    var r = Math.Exp(-Math.PI * (80 + 40 * k) / Rate);
                    var out0 = v + 2 * r * Math.Cos(2 * Math.PI * formants[k] / Rate) * y[k, 0] - r * r * y[k, 1];
                    y[k, 1] = y[k, 0];
                    y[k, 0] = out0;
                    v = out0 * (1 - r);
                }
                var envelope = Math.Min(1, Math.Min(t / (0.015 * Rate), (end - n) / (0.04 * Rate)));
                x[n] += (float)(v * (0.5 - 0.5 * Math.Cos(Math.PI * envelope)));
            }
            ranges.Add((at, end));
            at = end + (int)(gap * Rate);
        }
        return ranges;
    }

    // A vowel that starts at full level: a pulse train at f0 through two formants, with no easing in.
    private static float[] Vowel(int length, double f0)
    {
        var x = new float[length];
        double phase = 1, y0 = 0, y1 = 0, z0 = 0, z1 = 0;
        for (var n = 0; n < length; n++)
        {
            phase += f0 / Rate;
            var pulse = phase >= 1 ? 1.0 : 0;
            if (phase >= 1) phase -= 1;
            double r = Math.Exp(-Math.PI * 90 / Rate), q = Math.Exp(-Math.PI * 120 / Rate);
            var a = pulse + 2 * r * Math.Cos(2 * Math.PI * 700 / Rate) * y0 - r * r * y1;
            (y1, y0) = (y0, a);
            var b = a * (1 - r) + 2 * q * Math.Cos(2 * Math.PI * 1800 / Rate) * z0 - q * q * z1;
            (z1, z0) = (z0, b);
            x[n] = (float)(a * (1 - r) + b * (1 - q));
        }
        return x;
    }

    // The normalized correlation of the output, lag samples later, with the input over from..to.
    private static double Correlate(float[] input, float[] output, int from, int to, int lag)
    {
        double xy = 0, xx = 0, yy = 0;
        for (var n = from; n < to; n++)
        {
            double x = input[n], y = output[n + lag];
            xy += x * y; xx += x * x; yy += y * y;
        }
        return xy / Math.Sqrt(xx * yy + 1e-30);
    }

    private static float[] Noise(int length, double db, Random rng)
    {
        var amplitude = Math.Pow(10, db / 20) * Math.Sqrt(3);
        return [.. Enumerable.Range(0, length).Select(_ => (float)((rng.NextDouble() * 2 - 1) * amplitude))];
    }

    // To an RMS of db over the parts that sound.
    private static void Scale(Span<float> x, double db)
    {
        double sum = 0;
        var count = 0;
        foreach (var s in x) if (Math.Abs(s) > 1e-6) { sum += (double)s * s; count++; }
        var gain = (float)(Math.Pow(10, db / 20) / Math.Sqrt(sum / count));
        foreach (ref var s in x) s *= gain;
    }

    private static double Db(float[] x, int from, int to)
    {
        double sum = 0;
        for (var n = from; n < to; n++) sum += (double)x[n] * x[n];
        return 10 * Math.Log10(sum / (to - from) + 1e-20);
    }

    // The level of one frequency (Goertzel), dB.
    private static double Tone(float[] x, int from, int to, double hz)
    {
        var w = 2 * Math.PI * hz / Rate;
        double s1 = 0, s2 = 0;
        for (var n = from; n < to; n++) (s1, s2) = (x[n] + 2 * Math.Cos(w) * s1 - s2, s1);
        return 10 * Math.Log10((s1 * s1 + s2 * s2 - 2 * Math.Cos(w) * s1 * s2) / ((double)(to - from) * (to - from)) + 1e-30);
    }
}
