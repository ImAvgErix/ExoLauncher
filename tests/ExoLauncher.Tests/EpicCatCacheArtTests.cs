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
    public void TitleLookupKeys_AliasFortniteBattleRoyale()
    {
        Assert.Contains("fortnite", EpicCatCacheArt.TitleLookupKeys("fortnite battle royale"));
        Assert.Contains("fortnite battle royale", EpicCatCacheArt.TitleLookupKeys("fortnite"));
    }

    [Fact]
    public void CatalogElement_IndexesReleaseAppId()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        EpicCatCacheArt.IndexCatalogElement(map, """
            {
              "title": "Rocket League",
              "keyImages": [
                { "type": "DieselGameBoxTall", "url": "https://cdn1.epicgames.com/rl-tall.jpg" }
              ],
              "releaseInfo": [ { "appId": "Sugar" } ]
            }
            """);

        Assert.Equal("https://cdn1.epicgames.com/rl-tall.jpg?h=900&w=600&resize=1&quality=high", map["sugar"]);
        Assert.Equal(map["sugar"], map["rocket league"]);
    }

    [Fact]
    public void PortraitLookupKeys_IncludeLaunchTargetAndEntitlementName()
    {
        var keys = EpicCatCacheArt.PortraitLookupKeys(
            "Fortnite Battle Royale", "Fortnite", "4fe75bbc5a674f4f9b356b5c90567da5").ToList();

        Assert.Contains("fortnite", keys);
        Assert.Contains("fortnite battle royale", keys);
        Assert.Contains("4fe75bbc5a674f4f9b356b5c90567da5", keys);
    }

    [Fact]
    public void FindPortraitUrl_DoesNotMatchEngineNoise()
    {
        var hit = EpicCatCacheArt.FindPortraitUrl("Unreal Engine Blueprint Toolkit Sample");
        Assert.Null(hit);
    }
}
