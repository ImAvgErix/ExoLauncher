using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways making a recording smaller could cost the user a clip, or a game its frames:
/// - a recording still being written or open elsewhere is re-encoded, or one from before the setting began is changed unasked;
/// - the add-on's own output, or a clip it already shrank or refused, is asked again and again;
/// - it runs during a game or a recording;
/// - the swap loses the clip between the original moving aside and the new file arriving, or the new file shows as a brand new
///   clip (its date) or is larger than the original;
/// - a refusal that is only "not now" is remembered for good, or a final one is retried forever.
/// </summary>
public sealed class CaptureShrinkTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-shrink-" + Guid.NewGuid().ToString("N"));

    public CaptureShrinkTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static (string, DateTime, long, string?) Video(string name, double ageMinutes, long bytes = 100, string? mark = null) => (name, Now.AddMinutes(-ageMinutes), bytes, mark);

    [Fact]
    public void OnlySettledUnshrunkRecordingsAreAskedForBiggestFirst()
    {
        var pending = CaptureShrink.Pending(
        [
            Video(@"Gamesmall.mp4", 10, 50),
            Video(@"Gameig-old.mp4", 60 * 24 * 90, 900),      // from long ago: shrunk too
            Video(@"Gamesettling.mp4", 1, 5000),              // may still be open in an editor
            Video(@"Gamedone.mp4", 200, 800, CaptureShrink.DoneMark),
            Video(@"Gameefused.mp4", 200, 800, "not-smaller"),
            Video(@"Gameclip.shrink.mp4", 200, 800),          // the add-on's own output
            Video(@"Gameshot.png", 200, 800),
            Video(@"Gamemedium.mp4", 300, 400),
        ], Now);
        Assert.Equal([@"Gameig-old.mp4", @"Gamemedium.mp4", @"Gamesmall.mp4"], pending);
    }

    [Fact]
    public void AUnattendedRunWaitsForAGameARecordingOrABattery()
    {
        Assert.NotNull(CaptureShrink.BlockedUnattended(true, false, false));
        Assert.NotNull(CaptureShrink.BlockedUnattended(false, true, false));
        Assert.NotNull(CaptureShrink.BlockedUnattended(false, false, true));
        Assert.Null(CaptureShrink.BlockedUnattended(false, false, false));
    }

    [Fact]
    public void NothingRunsDuringAGameOrARecording()
    {
        Assert.NotNull(CaptureShrink.Blocked(gameRunning: true, recording: false));
        Assert.NotNull(CaptureShrink.Blocked(gameRunning: false, recording: true));
        Assert.Null(CaptureShrink.Blocked(false, false));
    }

    [Theory]
    [InlineData("busy", true)]
    [InlineData("recording-in-progress", true)]
    [InlineData("cancelled", true)]
    [InlineData("not-smaller", false)]
    [InlineData("quality-loss", false)]
    [InlineData("hdr-unsupported", false)]
    [InlineData(null, false)]
    public void OnlyARefusalAboutTheMomentIsAskedAgain(string? skipped, bool later) =>
        Assert.Equal(later, CaptureShrink.Later(skipped));

    [Fact]
    public void EveryRefusalHasWordsForTheUser()
    {
        foreach (var skipped in new[] { "not-smaller", "quality-loss", "hdr-unsupported", "audio-passthrough", "no-encoder", "source-unreadable", "busy", "cancelled", "frame-count", "x" })
            Assert.False(string.IsNullOrWhiteSpace(CaptureShrink.Say(skipped)));
    }

    private (string Original, string Smaller) Files(int originalBytes, int smallerBytes)
    {
        var original = Path.Combine(_dir, "clip.mp4");
        var smaller = Path.Combine(_dir, "clip.shrink.mp4");
        File.WriteAllBytes(original, new byte[originalBytes]);
        File.WriteAllBytes(smaller, new byte[smallerBytes]);
        File.SetCreationTimeUtc(original, Now.AddDays(-3));
        File.SetLastWriteTimeUtc(original, Now.AddDays(-2));
        return (original, smaller);
    }

    [Fact]
    public void TheSmallerFileTakesTheOriginalsNameAndDateAndTheOriginalIsRecycled()
    {
        var (original, smaller) = Files(1000, 300);
        string? recycled = null;
        Assert.Null(CaptureShrink.Replace(original, smaller, path => { recycled = path; File.Delete(path); return true; }));
        Assert.Equal(300, new FileInfo(original).Length);
        Assert.False(File.Exists(smaller));
        Assert.Equal(original + ".exo-orig", recycled);
        Assert.Equal(Now.AddDays(-3), File.GetCreationTimeUtc(original));
        Assert.Equal(Now.AddDays(-2), File.GetLastWriteTimeUtc(original));
    }

    [Theory]
    [InlineData(1000, 0)]      // nothing written
    [InlineData(1000, 1000)]   // not smaller
    [InlineData(1000, 2000)]   // larger
    public void AFileThatIsNotSmallerNeverReplacesTheOriginal(int originalBytes, int smallerBytes)
    {
        var (original, smaller) = Files(originalBytes, smallerBytes);
        Assert.NotNull(CaptureShrink.Replace(original, smaller, _ => throw new InvalidOperationException("must not recycle")));
        Assert.Equal(originalBytes, new FileInfo(original).Length);
        Assert.True(File.Exists(smaller));
    }

    [Fact]
    public void AnOriginalOpenElsewhereStaysWhereItIs()
    {
        var (original, smaller) = Files(1000, 300);
        using var open = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.NotNull(CaptureShrink.Replace(original, smaller, _ => throw new InvalidOperationException("must not recycle")));
        Assert.Equal(1000, new FileInfo(original).Length);
        Assert.False(File.Exists(original + ".exo-orig"));
    }

    [Fact]
    public void AMissingOriginalIsReportedNotInvented()
    {
        var (original, smaller) = Files(1000, 300);
        File.Delete(original);
        Assert.NotNull(CaptureShrink.Replace(original, smaller, _ => true));
        Assert.False(File.Exists(original));
    }

    [Fact]
    public void ABinThatWontTakeTheOriginalLeavesItBesideTheNewFile()
    {
        var (original, smaller) = Files(1000, 300);
        Assert.Null(CaptureShrink.Replace(original, smaller, _ => false));
        Assert.Equal(300, new FileInfo(original).Length);
        Assert.True(File.Exists(original + ".exo-orig"));
    }
}
