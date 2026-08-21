using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamAccountLibraryTests
{
    [Fact]
    public void ListCacheAppIds_ReadsNumericJsonFilenames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ExoLauncherTests", "steam-libcache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "1085660.json"), "[]");
        File.WriteAllText(Path.Combine(dir, "not-an-app.json"), "[]");
        File.WriteAllText(Path.Combine(dir, "730.json"), "[]");

        var ids = SteamAccountLibrary.ListCacheAppIds(dir);

        Assert.Contains("1085660", ids);
        Assert.Contains("730", ids);
        Assert.DoesNotContain("not-an-app", ids);
    }

    [Fact]
    public void UninstalledOwnedGames_DoesNotPromoteLibraryCacheWithoutCurrentOwnership()
    {
        var names = new Dictionary<string, SteamAppInfoNames.Entry>(StringComparer.Ordinal)
        {
            ["1085660"] = new("Destiny 2", "game"),
            ["123"] = new("Cosmetic Pack", "dlc"),
            ["730"] = new("Counter-Strike 2", "game"),
        };
        var present = new HashSet<string>(StringComparer.Ordinal) { "730" };
        var cache = new[] { "1085660", "123", "730", "999" };

        var games = SteamAccountLibrary.UninstalledOwnedGames(cache, present, names);

        Assert.Empty(games);
    }

    [Fact]
    public void UninstalledOwnedGames_AddsOnlyTitlesInAuthoritativeSnapshot()
    {
        var names = new Dictionary<string, SteamAppInfoNames.Entry>(StringComparer.Ordinal)
        {
            ["1085660"] = new("Destiny 2", "game"),
            ["123"] = new("Cosmetic Pack", "dlc"),
            ["730"] = new("Counter-Strike 2", "game"),
        };
        var present = new HashSet<string>(StringComparer.Ordinal) { "730" };
        var cache = new[] { "1085660", "123", "730", "999" };
        var authoritative = new HashSet<string>(StringComparer.Ordinal) { "1085660" };

        var games = SteamAccountLibrary.UninstalledOwnedGames(
            cache,
            present,
            names,
            authoritative);

        var destiny = Assert.Single(games);
        Assert.Equal("steam:1085660", destiny.Id);
        Assert.Equal("Destiny 2", destiny.Title);
        Assert.False(destiny.Installed);
        Assert.True(destiny.Owned);
        Assert.True(destiny.CanInstall);
        Assert.Equal("install", destiny.PrimaryAction);
    }
}
