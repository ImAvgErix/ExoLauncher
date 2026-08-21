using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.Achievements;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class StoreAchievementCoverageTests
{
    [Theory]
    [InlineData(StoreKind.Xbox, "Xbox Live")]
    [InlineData(StoreKind.Ea, "EA App")]
    [InlineData(StoreKind.Ubisoft, "Ubisoft")]
    [InlineData(StoreKind.BattleNet, "Battle.net")]
    [InlineData(StoreKind.Amazon, "Amazon")]
    [InlineData(StoreKind.Riot, "Vanguard")]
    [InlineData(StoreKind.Itch, "itch")]
    [InlineData(StoreKind.Minecraft, "per-world")]
    [InlineData(StoreKind.Roblox, "Roblox")]
    [InlineData(StoreKind.Paradox, "Paradox")]
    [InlineData(StoreKind.Wargaming, "Wargaming")]
    [InlineData(StoreKind.Rockstar, "Rockstar")]
    [InlineData(StoreKind.Local, "folders")]
    public async Task UnsupportedStores_AreExplicitEvenWhenTheClientIsAbsent(
        StoreKind store, string expectedPhrase)
    {
        var provider = UnsupportedStoreAchievementProvider.For(store);
        var game = new GameEntry
        {
            Id = store.ToString().ToLowerInvariant() + ":demo",
            Title = "Demo",
            Store = store,
            Installed = false,
            LaunchTarget = "demo",
        };

        Assert.True(provider.Supports(game));
        Assert.False(provider.CanObserveUnlocks);
        Assert.Contains(expectedPhrase, provider.CoverageMessage, StringComparison.OrdinalIgnoreCase);

        var snapshot = await provider.GetSnapshotAsync(game);
        Assert.Equal(AchievementCoverageStatus.Unsupported, snapshot.Coverage);
        Assert.Contains(expectedPhrase, snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AchievementService_SurfacesUnsupportedCoverageWithoutARefresh()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "exo-achievement-tests", Guid.NewGuid().ToString("N"), "achievements.json");
        try
        {
            using var service = new AchievementService(
                UnsupportedStoreAchievementProvider.All(), path, TimeSpan.FromHours(1));
            var game = new GameEntry
            {
                Id = "riot:valorant",
                Title = "VALORANT",
                Store = StoreKind.Riot,
                Installed = true,
                LaunchTarget = "valorant",
            };

            var coverage = service.GetCoverage(game);
            Assert.Equal(AchievementCoverageStatus.Unsupported, coverage.Status);
            Assert.Contains("Vanguard", coverage.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void GalaxySqlite_DoesNotTreatPlaytimeTablesAsAchievements()
    {
        Assert.False(GogGalaxySqlite.IsAchievementTableName("GameTimes"));
        Assert.False(GogGalaxySqlite.IsAchievementTableName("LibraryItems"));
        Assert.False(GogGalaxySqlite.IsAchievementTableName("Users"));
        Assert.True(GogGalaxySqlite.IsAchievementTableName("GameAchievements"));
        Assert.True(GogGalaxySqlite.IsAchievementTableName("game_achievements"));
        Assert.False(GogGalaxySqlite.IsAchievementTableName(""));
    }

    [Fact]
    public void DefaultProviders_CoverEveryStoreKindExactlyOnceForWatchableStores()
    {
        var providers = AchievementService.CreateDefaultProviders();
        Assert.Contains(providers, row => row is SteamLibraryCacheAchievementProvider);
        Assert.Contains(providers, row => row is EpicLegendaryAchievementProvider);
        Assert.Contains(providers, row => row is GogGameplayAchievementProvider);
        foreach (var store in Enum.GetValues<StoreKind>())
        {
            if (store is StoreKind.Steam or StoreKind.Epic or StoreKind.Gog)
                continue;
            Assert.Contains(providers, row =>
                row is UnsupportedStoreAchievementProvider && row.Store == store && !row.CanObserveUnlocks);
        }

        Assert.Equal(Enum.GetValues<StoreKind>().Length, providers.Length);
        Assert.Equal(providers.Length, providers.Select(row => row.Store).Distinct().Count());
    }
}
