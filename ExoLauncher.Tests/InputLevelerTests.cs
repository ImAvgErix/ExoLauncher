using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Tests;

/// <summary>Gain control ahead of noise suppression: a quiet mic is brought up to a speaking level, never a loud one down.</summary>
public sealed class InputLevelerTests
{
    // A voice: a 150 Hz tone at the level given, speaking 300 ms then pausing 200 ms, over a room 25 dB under it.
    private static float[] Frame(int index, float voiceDb, Random random)
    {
        var frame = new float[960 * 2];
        var talking = index % 25 < 15;
        var voice = MathF.Pow(10, voiceDb / 20) * MathF.Sqrt(2);
        var room = MathF.Pow(10, (voiceDb - 25) / 20);
        for (var i = 0; i < 960; i++)
        {
            var t = (index * 960 + i) / 48000f;
            var x = (talking ? voice * MathF.Sin(2 * MathF.PI * 150 * t) : 0) + room * (float)(random.NextDouble() * 2 - 1);
            frame[i * 2] = frame[i * 2 + 1] = x;
        }
        return frame;
    }

    private static float Db(float[] frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        return 10 * MathF.Log10((float)(sum / frame.Length) + 1e-10f);
    }

    [Fact]
    public void AQuietMicReachesASpeakingLevelWithinSeconds()
    {
        var leveler = new InputLeveler(null);
        var random = new Random(1);
        float last = -100;
        for (var i = 0; i < 150; i++) // three seconds
        {
            var frame = Frame(i, -50, random);
            leveler.Process(frame);
            if (i % 25 == 10) last = Db(frame);
        }
        Assert.True(last > InputLeveler.TargetDb - 3 && last < InputLeveler.TargetDb + 3 && leveler.GainDb is > 20 and < 27, $"level {last}, gain {leveler.GainDb}");
    }

    [Fact]
    public void ALoudMicIsNeverTurnedDownAndNeverClips()
    {
        var leveler = new InputLeveler(null);
        var random = new Random(2);
        for (var i = 0; i < 150; i++)
        {
            var frame = Frame(i, -12, random);
            var before = Db(frame);
            leveler.Process(frame);
            Assert.True(Db(frame) >= before - 0.01f);
            Assert.All(frame, s => Assert.InRange(s, -1f, 1f));
        }
        Assert.Equal(0, leveler.GainDb);
    }

    [Fact]
    public void ARoomAloneIsNotBroughtUp()
    {
        var leveler = new InputLeveler(null);
        var random = new Random(3);
        for (var i = 0; i < 150; i++)
        {
            var frame = new float[1920];
            for (var j = 0; j < 960; j++) frame[j * 2] = frame[j * 2 + 1] = 0.0005f * (float)(random.NextDouble() * 2 - 1);
            leveler.Process(frame);
        }
        Assert.Equal(0, leveler.GainDb);
    }
}
