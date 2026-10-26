namespace ExoLauncher.Tests;

/// <summary>
/// Ways Capture settings' storage limit (in the Captures sheet) could fail:
/// - it removes the user's own videos and pictures in a shared folder (Videos, OneDrive) that Exo Capture never saved;
/// - those files count toward the size limit, so Exo's own clips go first to make room for them;
/// - a favourite is removed;
/// - it removes more than it needs to get under the size, or the newest before the oldest.
/// </summary>
public sealed class CaptureLimitTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private const long Gb = 1024L * 1024 * 1024;

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void FilesExoDidNotSaveAreNeverRemovedOrCounted()
    {
        var files = new[]
        {
            (@"Holiday\beach.mp4", Now.AddDays(-400), 50 * Gb),
            (@"Game\clip.mp4", Now.AddDays(-1), 1 * Gb),
        };
        var removed = MainWindow.CapturesPastLimit(files, Set(@"Game\clip.mp4"), Set(), keepDays: 30, keepGb: 2, Now);
        Assert.Empty(removed);
    }

    [Fact]
    public void OldestIndexedGoFirstUntilUnderTheSize()
    {
        var files = new[]
        {
            (@"Game\c.mp4", Now.AddDays(-1), 1 * Gb),
            (@"Game\a.mp4", Now.AddDays(-3), 1 * Gb),
            (@"Game\b.mp4", Now.AddDays(-2), 1 * Gb),
        };
        var removed = MainWindow.CapturesPastLimit(files, Set(@"Game\a.mp4", @"Game\b.mp4", @"game\C.MP4"), Set(), keepDays: 0, keepGb: 2, Now);
        Assert.Equal(new[] { @"Game\a.mp4" }, removed);
    }

    [Fact]
    public void FavouritesStayEvenWhenOld()
    {
        var files = new[] { (@"Game\keep.png", Now.AddDays(-90), 10L), (@"Game\old.png", Now.AddDays(-90), 10L) };
        var removed = MainWindow.CapturesPastLimit(files, Set(@"Game\keep.png", @"Game\old.png"), Set(@"Game\keep.png"), keepDays: 30, keepGb: 0, Now);
        Assert.Equal(new[] { @"Game\old.png" }, removed);
    }
}
