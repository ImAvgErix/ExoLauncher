using System.Text;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class TrophySoundPlayerTests
{
    [Theory]
    [InlineData(TrophyRarity.Bronze, 0.50, 0.62)]
    [InlineData(TrophyRarity.Silver, 0.70, 0.82)]
    [InlineData(TrophyRarity.Gold, 0.96, 1.08)]
    [InlineData(TrophyRarity.Platinum, 1.10, 1.22)]
    public void RarityCues_AreDistinctShortPcmWithSafeHeadroomAndCleanTail(
        TrophyRarity rarity, double minSeconds, double maxSeconds)
    {
        var wave = TrophySoundPlayer.BuildWave(rarity);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal(wave.Length - 8, BitConverter.ToInt32(wave, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wave, 8, 4));
        var format = FindChunk(wave, "fmt ");
        var data = FindChunk(wave, "data");
        Assert.Equal((short)1, BitConverter.ToInt16(wave, format.Offset));
        Assert.Equal((short)2, BitConverter.ToInt16(wave, format.Offset + 2));
        Assert.Equal(48_000, BitConverter.ToInt32(wave, format.Offset + 4));
        Assert.Equal((short)16, BitConverter.ToInt16(wave, format.Offset + 14));

        var channels = BitConverter.ToInt16(wave, format.Offset + 2);
        var durationSeconds = data.Length / (48_000d * channels * sizeof(short));
        Assert.InRange(durationSeconds, minSeconds, maxSeconds);

        var peak = 0;
        var sumSquares = 0d;
        var sampleCount = data.Length / sizeof(short);
        for (var offset = data.Offset; offset < data.Offset + data.Length; offset += sizeof(short))
        {
            var sample = BitConverter.ToInt16(wave, offset);
            peak = Math.Max(peak, Math.Abs((int)sample));
            sumSquares += (double)sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        Assert.InRange(peak, 6_000, 24_000);
        Assert.InRange(rms, 1_400d, 5_500d);

        var tailPeak = 0;
        var tailBytes = 48_000 / 200 * channels * sizeof(short);
        for (var offset = data.Offset + data.Length - tailBytes; offset < data.Offset + data.Length; offset += sizeof(short))
            tailPeak = Math.Max(tailPeak, Math.Abs((int)BitConverter.ToInt16(wave, offset)));
        Assert.InRange(tailPeak, 0, 16);
        Assert.Equal((short)0, BitConverter.ToInt16(wave, data.Offset + data.Length - sizeof(short)));
    }

    [Fact]
    public void RarityCues_AreNotTheSameWave()
    {
        var bronze = TrophySoundPlayer.BuildWave(TrophyRarity.Bronze);
        var silver = TrophySoundPlayer.BuildWave(TrophyRarity.Silver);
        var gold = TrophySoundPlayer.BuildWave(TrophyRarity.Gold);
        var platinum = TrophySoundPlayer.BuildWave(TrophyRarity.Platinum);
        Assert.NotEqual(bronze.Length, silver.Length);
        Assert.NotEqual(silver.Length, gold.Length);
        Assert.NotEqual(gold.Length, platinum.Length);

        if (Environment.GetEnvironmentVariable("EXO_WRITE_TROPHY_CUES") == "1")
        {
            var audio = Path.GetDirectoryName(FindRepositoryFile("ExoLauncher", "Assets", "Audio", "AUDIO-NOTICE.txt"))!;
            File.WriteAllBytes(Path.Combine(audio, "exo-trophy-bronze.wav"), bronze);
            File.WriteAllBytes(Path.Combine(audio, "exo-trophy-silver.wav"), silver);
            File.WriteAllBytes(Path.Combine(audio, "exo-trophy-gold.wav"), gold);
            File.WriteAllBytes(Path.Combine(audio, "exo-trophy-platinum.wav"), platinum);
            File.WriteAllBytes(Path.Combine(audio, "exo-achievement-unlock.wav"), gold);
        }
    }

    [Fact]
    public void PlaybackContract_LoadsBundledAssetAndPreventsOverlappingRequests()
    {
        var sourcePath = FindRepositoryFile("ExoLauncher", "Services", "TrophySoundPlayer.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("AppContext.BaseDirectory", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange", source, StringComparison.Ordinal);
        Assert.Contains("SndMemory", source, StringComparison.Ordinal);
        Assert.Contains("SndNodefault", source, StringComparison.Ordinal);
        Assert.Contains("Play(TrophyRarity rarity)", source, StringComparison.Ordinal);
        Assert.Contains("exo-trophy-bronze.wav", source, StringComparison.Ordinal);
        Assert.Contains("exo-trophy-platinum.wav", source, StringComparison.Ordinal);

        var project = File.ReadAllText(FindRepositoryFile("ExoLauncher", "ExoLauncher.csproj"));
        Assert.Contains("Assets\\**\\*", project, StringComparison.Ordinal);
        var installer = File.ReadAllText(FindRepositoryFile("tools", "ExoLauncher.nsi"));
        Assert.Contains("File /r \"${PAYLOAD_DIR}\\*.*\"", installer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("exo-trophy-bronze.wav", 0.50, 0.62)]
    [InlineData("exo-trophy-silver.wav", 0.70, 0.82)]
    [InlineData("exo-trophy-gold.wav", 0.96, 1.08)]
    [InlineData("exo-trophy-platinum.wav", 1.10, 1.22)]
    [InlineData("exo-achievement-unlock.wav", 0.96, 1.08)]
    public void BundledCues_HaveSafeLevelsAndASoftStart(
        string fileName, double minSeconds, double maxSeconds)
    {
        var path = FindRepositoryFile("ExoLauncher", "Assets", "Audio", fileName);
        var wave = File.ReadAllBytes(path);
        var data = FindChunk(wave, "data");
        var format = FindChunk(wave, "fmt ");
        Assert.Equal(48_000, BitConverter.ToInt32(wave, format.Offset + 4));
        Assert.Equal((short)2, BitConverter.ToInt16(wave, format.Offset + 2));

        var sampleCount = data.Length / sizeof(short);
        var peak = 0;
        var sumSquares = 0d;
        var clipped = 0;
        var dc = 0d;
        for (var offset = data.Offset; offset < data.Offset + data.Length; offset += sizeof(short))
        {
            var sample = BitConverter.ToInt16(wave, offset);
            var abs = Math.Abs((int)sample);
            peak = Math.Max(peak, abs);
            sumSquares += (double)sample * sample;
            dc += sample;
            if (sample is short.MinValue or short.MaxValue) clipped++;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        var duration = data.Length / (48_000d * 2 * sizeof(short));
        Assert.InRange(duration, minSeconds, maxSeconds);
        Assert.Equal(0, clipped);
        Assert.InRange(peak, 6_000, 24_000);
        Assert.InRange(rms, 1_400d, 5_500d);
        Assert.InRange(Math.Abs(dc / sampleCount), 0, 8);
        Assert.Equal((short)0, BitConverter.ToInt16(wave, data.Offset + data.Length - sizeof(short)));
        Assert.Equal((short)0, BitConverter.ToInt16(wave, data.Offset));

        var headBytes = 48_000 / 100 * 2 * sizeof(short);
        var headPeak = 0;
        for (var offset = data.Offset; offset < data.Offset + headBytes; offset += sizeof(short))
            headPeak = Math.Max(headPeak, Math.Abs((int)BitConverter.ToInt16(wave, offset)));
        Assert.True(headPeak < peak, "A 10ms slam at the start is the click the cues must not have.");
    }

    [Fact]
    public void BundledCues_KeepPerceivedLoudnessWithinANarrowBand()
    {
        var rms = new[] { "bronze", "silver", "gold", "platinum" }
            .Select(name => RmsOf(FindRepositoryFile("ExoLauncher", "Assets", "Audio", "exo-trophy-" + name + ".wav")))
            .ToArray();
        var max = rms.Max();
        var min = rms.Min();
        Assert.True(max / min < 1.25, $"RMS spread was {min:0}–{max:0}; tiers must not escalate by volume.");
    }

    [Fact]
    public void BundledGoldCue_IsCopiedToTheLegacyUnlockFilename()
    {
        var gold = File.ReadAllBytes(FindRepositoryFile(
            "ExoLauncher", "Assets", "Audio", "exo-trophy-gold.wav"));
        var legacy = File.ReadAllBytes(FindRepositoryFile(
            "ExoLauncher", "Assets", "Audio", "exo-achievement-unlock.wav"));
        Assert.Equal(gold, legacy);
    }

    [Fact]
    public void TrophyCueGenerator_StaysInTheRepo()
    {
        var script = File.ReadAllText(FindRepositoryFile("tools", "make-trophy-cues.py"));
        Assert.Contains("TARGET_RMS", script, StringComparison.Ordinal);
        Assert.Contains("SAMPLE_RATE = 48_000", script, StringComparison.Ordinal);
        Assert.Contains("raised_cosine", script, StringComparison.Ordinal);
        var notice = File.ReadAllText(FindRepositoryFile("ExoLauncher", "Assets", "Audio", "AUDIO-NOTICE.txt"));
        Assert.Contains("make-trophy-cues.py", notice, StringComparison.Ordinal);
        Assert.Contains("no third-party samples", notice, StringComparison.OrdinalIgnoreCase);
    }

    private static double RmsOf(string path)
    {
        var wave = File.ReadAllBytes(path);
        var data = FindChunk(wave, "data");
        var sumSquares = 0d;
        var count = 0;
        for (var offset = data.Offset; offset < data.Offset + data.Length; offset += sizeof(short))
        {
            var sample = BitConverter.ToInt16(wave, offset);
            sumSquares += (double)sample * sample;
            count++;
        }
        return Math.Sqrt(sumSquares / count);
    }

    private static (int Offset, int Length) FindChunk(byte[] wave, string id)
    {
        for (var offset = 12; offset <= wave.Length - 8;)
        {
            var chunkLength = BitConverter.ToInt32(wave, offset + 4);
            if (chunkLength < 0 || offset + 8 > wave.Length - chunkLength)
                break;
            if (Encoding.ASCII.GetString(wave, offset, 4) == id)
                return (offset + 8, chunkLength);
            offset += 8 + chunkLength + (chunkLength & 1);
        }

        throw new InvalidDataException($"Missing WAV chunk '{id}'.");
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = directory.FullName;
            foreach (var segment in relativeSegments)
                candidate = Path.Combine(candidate, segment);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Could not locate repository file: " + Path.Combine(relativeSegments));
    }
}
