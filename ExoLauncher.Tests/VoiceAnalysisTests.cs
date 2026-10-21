using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class VoiceAnalysisTests
{
    private const int Rate = VoiceAnalysis.Rate;

    [Fact]
    public void HearsTheRoomTheVoiceAndTheHeadroom()
    {
        var report = VoiceAnalysis.Analyze(Take(pitch: 120, roomDb: -62));
        Assert.True(report.Heard);
        Assert.InRange(report.RoomDb, -65, -59);
        Assert.Null(report.HumHz);
        Assert.InRange(report.PitchHz, 110, 130);
        Assert.InRange(report.SpeechDb, -36, -20);
        Assert.InRange(report.LoudDb, -10, -6);
        Assert.Equal(0, report.Clipped);
        Assert.False(report.BandLimited);
        // The loudest words land near -6 dBFS.
        Assert.InRange(report.LoudDb + report.Tuning.GainDb, -6.5f, -5.5f);
    }

    [Fact]
    public void FindsMainsHumAndItsFrequency()
    {
        Assert.Equal(60, VoiceAnalysis.Analyze(Take(pitch: 120, roomDb: -62, humHz: 60)).HumHz);
        Assert.Equal(50, VoiceAnalysis.Analyze(Take(pitch: 120, roomDb: -62, humHz: 50)).HumHz);
    }

    [Fact]
    public void HearsADeepVoiceAndAHighOne()
    {
        Assert.InRange(VoiceAnalysis.Analyze(Take(pitch: 90, roomDb: -62)).PitchHz, 80, 100);
        Assert.InRange(VoiceAnalysis.Analyze(Take(pitch: 220, roomDb: -62)).PitchHz, 200, 240);
    }

    [Fact]
    public void AClippingMicIsTurnedDown()
    {
        var report = VoiceAnalysis.Analyze(Take(pitch: 120, roomDb: -62, loudGain: 3f));
        Assert.True(report.Clipped > 0);
        Assert.True(report.Tuning.GainDb <= -6);
    }

    [Fact]
    public void AQuietMicIsTurnedUp()
    {
        // The same voice 20 dB quieter at the mic: Keep turns it up about those 20 dB (at most 20).
        var take = Take(pitch: 120, roomDb: -62);
        var quiet = new TuneTake(Scale(take.Quiet, -20), Scale(take.Reading, -20), Scale(take.Loud, -20), Scale(take.Soft, -20), Scale(take.Typing, -20));
        Assert.True(VoiceAnalysis.Analyze(quiet).Tuning.GainDb > 15);
    }

    [Fact]
    public void ATurnedDownMicStillOpensTheGateForSoftWords()
    {
        // A mic that clips on loud words is turned down by the kept gain; its soft words then reach Voice Focus
        // lower, and its gate must still open for them (the call would otherwise drop them).
        var take = Take(pitch: 120, roomDb: -62, loudGain: 3f);
        var tuning = VoiceAnalysis.Analyze(take).Tuning;
        Assert.True(tuning.GainDb <= -6);
        var gain = Amp(tuning.GainDb);
        var focus = new VoiceFocus();
        var frame = new float[Opus.FrameSamples * 2];
        int frames = take.Soft.Length / Opus.FrameSamples, open = 0;
        for (var f = 0; f < frames; f++)
        {
            for (var i = 0; i < Opus.FrameSamples; i++) frame[2 * i] = frame[2 * i + 1] = take.Soft[f * Opus.FrameSamples + i] * gain;
            if (focus.Process(frame, automatic: true, threshold: -60, level: false)) open++;
        }
        // Syllables fill about three quarters of the soft part; the gate holds through the gaps between them.
        Assert.True(open > frames / 2, $"{open} of {frames} soft frames sent");
    }

    [Fact]
    public void ABandLimitedMicIsNamed()
    {
        var take = Take(pitch: 120, roomDb: -62);
        var narrow = take with { Reading = LowPass(take.Reading, 7000) };
        Assert.True(VoiceAnalysis.Analyze(narrow).BandLimited);
    }

    // Noise suppression without a limit takes parts of the voice with the noise (muffled): even the noisiest room
    // gets at most its most, and a quiet one less.
    [Fact]
    public void AQuietRoomIsCutLessThanANoisyOneAndNeitherWithoutALimit()
    {
        var quiet = VoiceAnalysis.Analyze(Take(pitch: 120, roomDb: -80));
        var noisy = VoiceAnalysis.Analyze(Take(pitch: 120, roomDb: -45, readingPeakDb: -27));
        Assert.True(quiet.Tuning.SuppressionLimitDb < noisy.Tuning.SuppressionLimitDb);
        Assert.Equal(NoiseSuppressor.MostDb, noisy.Tuning.SuppressionLimitDb);
    }

    [Fact]
    public void AVoiceJustOverANoisyRoomIsStillHeard()
    {
        // A fan-loud room at -45 dB and a voice whose peaks reach -27 dB: its active level only about 10 dB over the room.
        var report = VoiceAnalysis.Analyze(Take(pitch: 120, roomDb: -45, readingPeakDb: -27));
        Assert.True(report.Heard);
        Assert.InRange(report.PitchHz, 110, 130);
        Assert.True(report.SpeechDb > report.RoomDb + 5);
    }

    [Fact]
    public void AKeptTuningReadsBackTheSame()
    {
        var tuning = new VoiceTuning(15, -3.5f, [-6, 1, 0, -5, -9, -12, -15]);
        var back = VoiceTune.Parse(VoiceTune.Format(tuning))!;
        Assert.Equal((tuning.SuppressionLimitDb, tuning.GainDb), (back.SuppressionLimitDb, back.GainDb));
        Assert.Equal(tuning.Spectrum, back.Spectrum);
        Assert.Null(VoiceTune.Parse("limit=x;gain=0"));
        // A key given twice (a hand-edited settings file) takes the last instead of throwing on the audio thread.
        Assert.Equal(12, VoiceTune.Parse("limit=18;gain=0;limit=12")!.SuppressionLimitDb);
        // One kept before 3.11: its gate, low cut and hum are let go of, and "all the way" is read as the most.
        var old = VoiceTune.Parse("hp=70;gate=-52.5;limit=100;hum=60;gain=-3;spec=-6,1,0,-5,-9,-12,-15")!;
        Assert.Equal((NoiseSuppressor.MostDb, -3f), (old.SuppressionLimitDb, old.GainDb));
    }

    [Fact]
    public void SilenceIsNotAVoice()
    {
        var silent = new float[Rate * 5];
        var report = VoiceAnalysis.Analyze(new TuneTake(Noise(Rate * 5, -62, 1), Noise(Rate * 20, -62, 2), silent, silent, silent));
        Assert.False(report.Heard);
    }

    // A take as Voice Tune records it: room noise throughout, a speech-like voice (glottal pulses through two
    // formants, syllables four times a second) reading with peaks at -16 dBFS, louder (-8) and softer (-30), and keyboard clicks.
    private static TuneTake Take(float pitch, float roomDb, int? humHz = null, float loudGain = 1f, float readingPeakDb = -16f)
    {
        float[] Room(int n, int seed)
        {
            var x = Noise(n, roomDb, seed);
            if (humHz is { } hz) for (var i = 0; i < n; i++) for (var k = 1; k <= 3; k++) x[i] += Amp(roomDb + 6 - 4 * k) * MathF.Sin(2 * MathF.PI * hz * k * i / Rate);
            return x;
        }
        float[] Voice(int seconds, float peakDb, int seed)
        {
            var x = Room(seconds * Rate, seed);
            var v = Speech(seconds * Rate, pitch);
            var scale = Amp(peakDb);
            for (var i = 0; i < x.Length; i++) x[i] = Math.Clamp(x[i] + v[i] * scale, -1f, 1f);
            return x;
        }
        var typing = Room(Rate * 5, 9);
        var random = new Random(3);
        for (var c = 0; c < 20; c++)
        {
            var at = random.Next(Rate * 5 - 200);
            for (var i = 0; i < 120; i++) typing[at + i] += 0.05f * (float)(random.NextDouble() * 2 - 1) * MathF.Exp(-i / 30f);
        }
        var loud = Voice(5, -8, 5);
        for (var i = 0; i < loud.Length; i++) loud[i] = Math.Clamp(loud[i] * loudGain, -1f, 1f);
        return new TuneTake(Room(Rate * 5, 1), Voice(20, readingPeakDb, 2), loud, Voice(5, -30, 6), typing);
    }

    private static float[] Speech(int n, float pitch)
    {
        var pulses = new float[n];
        var period = Rate / pitch;
        for (var t = 0f; t < n; t += period) pulses[(int)t] = 1;
        var x = Resonate(Resonate(pulses, 700, 90), 1200, 120);
        for (var i = 0; i < n; i++)
        {
            var syllable = MathF.Sin(MathF.PI * (i % (Rate / 4)) / (Rate / 4));
            x[i] *= (i / (Rate / 2)) % 4 == 3 ? 0 : syllable * syllable;
        }
        var peak = x.Max(MathF.Abs);
        for (var i = 0; i < n; i++) x[i] /= peak;
        return x;
    }

    private static float[] Resonate(float[] x, float hz, float bandwidth)
    {
        var r = MathF.Exp(-MathF.PI * bandwidth / Rate);
        var c = 2 * r * MathF.Cos(2 * MathF.PI * hz / Rate);
        var y = new float[x.Length];
        for (var i = 0; i < x.Length; i++) y[i] = x[i] + c * (i > 0 ? y[i - 1] : 0) - r * r * (i > 1 ? y[i - 2] : 0);
        return y;
    }

    private static float[] LowPass(float[] x, float hz)
    {
        var y = x;
        for (var stage = 0; stage < 6; stage++)
        {
            var a = MathF.Exp(-2 * MathF.PI * hz / Rate);
            var z = new float[y.Length];
            for (var i = 0; i < y.Length; i++) z[i] = (1 - a) * y[i] + a * (i > 0 ? z[i - 1] : 0);
            y = z;
        }
        return y;
    }

    private static float[] Noise(int n, float db, int seed)
    {
        var random = new Random(seed);
        var x = new float[n];
        var scale = Amp(db) * MathF.Sqrt(3);
        for (var i = 0; i < n; i++) x[i] = scale * (float)(random.NextDouble() * 2 - 1);
        return x;
    }

    private static float[] Scale(float[] x, float db) => x.Select(v => v * Amp(db)).ToArray();

    private static float Amp(float db) => MathF.Pow(10, db / 20);
}
