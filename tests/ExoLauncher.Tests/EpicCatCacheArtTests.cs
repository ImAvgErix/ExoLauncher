using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class EpicCatCacheArtTests
{
    [Fact]
    public void FindPortraitUrl_ReturnsTallWhenCatCachePresent()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");
        if (!File.Exists(path))
            return; // machine without EGL catalog — skip silently

        // Fortnite is almost always in a live EGL cache; Rocket League when owned.
        var fortnite = EpicCatCacheArt.FindPortraitUrl("Fortnite");
        if (fortnite is null) return;

        Assert.Contains("epicgames.com", fortnite, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1200x1600", fortnite, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindPortraitUrl_DoesNotMatchEngineNoise()
    {
        var hit = EpicCatCacheArt.FindPortraitUrl("Unreal Engine Blueprint Toolkit Sample");
        Assert.Null(hit);
    }
}
