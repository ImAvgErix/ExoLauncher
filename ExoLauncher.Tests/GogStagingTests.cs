using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways pausing a GOG download could cost it: a new staging folder on every start (the paused one is orphaned and the game
/// begins at zero), or one that is not under the library root where the cleanup refuses to look.
/// </summary>
public sealed class GogStagingTests
{
    [Fact]
    public void TheStagingFolderIsTheSameEveryTimeForAProduct()
    {
        var root = Path.Combine(Path.GetTempPath(), "GOG Games");
        var first = GogAdapter.CreateManagedInstallStagingPath(root, "Witcher3");
        Assert.Equal(first, GogAdapter.CreateManagedInstallStagingPath(root, "Witcher3"));
        Assert.NotEqual(first, GogAdapter.CreateManagedInstallStagingPath(root, "Cyberpunk"));
    }

    [Fact]
    public void ItSitsDirectlyUnderTheRootWithTheStagingPrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "GOG Games");
        var path = GogAdapter.CreateManagedInstallStagingPath(root, "Witcher3");
        Assert.Equal(root, Path.GetDirectoryName(path));
        Assert.StartsWith(GogAdapter.ManagedInstallStagingPrefix, Path.GetFileName(path));
    }
}
