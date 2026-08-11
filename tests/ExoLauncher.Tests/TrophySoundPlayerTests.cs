using System.Text;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class TrophySoundPlayerTests
{
    [Fact]
    public void BundledCue_IsShortPcmWithSafeHeadroomAndCleanTail()
    {
        var wave = TrophySoundPlayer.CueForTests.ToArray();

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
        Assert.InRange(durationSeconds, 1.15, 1.21);

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
        Assert.InRange(peak, 20_000, 24_000);
        Assert.InRange(rms, 3_500d, 5_000d);

        // The final 5 ms stays below roughly -66 dBFS and the final frame lands
        // on zero, so Windows playback cannot end on an audible click.
        var tailPeak = 0;
        var tailBytes = 48_000 / 200 * channels * sizeof(short);
        for (var offset = data.Offset + data.Length - tailBytes; offset < data.Offset + data.Length; offset += sizeof(short))
            tailPeak = Math.Max(tailPeak, Math.Abs((int)BitConverter.ToInt16(wave, offset)));
        Assert.InRange(tailPeak, 0, 16);
        Assert.Equal((short)0, BitConverter.ToInt16(wave, data.Offset + data.Length - sizeof(short)));
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

        var project = File.ReadAllText(FindRepositoryFile("ExoLauncher", "ExoLauncher.csproj"));
        Assert.Contains("Assets\\**\\*", project, StringComparison.Ordinal);
        var installer = File.ReadAllText(FindRepositoryFile("tools", "ExoLauncher.nsi"));
        Assert.Contains("File /r \"${PAYLOAD_DIR}\\*.*\"", installer, StringComparison.Ordinal);
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
