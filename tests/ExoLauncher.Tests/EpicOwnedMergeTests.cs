using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class EpicOwnedMergeTests
{
    [Fact]
    public void MergeOwned_AddsUninstalledTitlesAndKeepsInstalled()
    {
        var installed = new[]
        {
            EpicAdapter.MapInstalledRow(
                new LegendaryCli.GameRow("Fortnite", "Fortnite", @"D:\Epic\Fortnite", 1, true),
                hasLegendary: true),
        };
        var owned = new[]
        {
            new LegendaryCli.GameRow("Fortnite", "Fortnite", @"D:\Epic\Fortnite", 1, false),
            new LegendaryCli.GameRow("Control", "Control", null, null, false),
        };

        var merged = EpicAdapter.MergeOwned(installed, owned, hasLegendary: true);

        var fortnite = Assert.Single(merged, game => game.Id == "epic:Fortnite");
        Assert.True(fortnite.Installed);
        Assert.True(fortnite.Owned);
        Assert.False(fortnite.CanInstall);
        Assert.Contains("last verified", fortnite.LaunchNote, StringComparison.OrdinalIgnoreCase);
        var control = Assert.Single(merged, game => game.Id == "epic:Control");
        Assert.False(control.Installed);
        Assert.True(control.Owned);
        Assert.True(control.CanInstall);
        Assert.Equal("install", control.PrimaryAction);
    }

    [Fact]
    public void OwnedCache_IsQuarantinedByOpaqueAccountScope()
    {
        const string json = """
            {"schemaVersion":2,"accountScope":"scope-a","verifiedAtUtc":"2026-08-20T00:00:00Z","provenance":"legendary-authenticated-list","games":[{"app_name":"Control","app_title":"Control","title":"Control"}]}
            """;

        Assert.Single(EpicAdapter.ParseOwnedCache(json, "scope-a"));
        Assert.Empty(EpicAdapter.ParseOwnedCache(json, "scope-b"));
        Assert.Empty(EpicAdapter.ParseOwnedCache(json, null));
    }

    [Fact]
    public void OwnedCache_RejectsLegacyCacheWithoutVerifiedProvenance()
    {
        const string legacy = """
            {"schemaVersion":1,"accountScope":"scope-a","verifiedAtUtc":"2026-08-20T00:00:00Z","games":[{"app_name":"Control","title":"Control"}]}
            """;

        Assert.Empty(EpicAdapter.ParseOwnedCache(legacy, "scope-a"));
    }

    [Fact]
    public void SameAccountOffline_UsesVerifiedCacheButDifferentOrUnknownAccountCannot()
    {
        const string json = """
            {"schemaVersion":2,"accountScope":"scope-a","verifiedAtUtc":"2026-08-20T00:00:00Z","provenance":"legendary-authenticated-list","games":[{"app_name":"Control","app_title":"Control","title":"Control"}]}
            """;

        var sameAccount = EpicAdapter.ParseOwnedCache(json, "scope-a");
        var merged = EpicAdapter.MergeOwned(Array.Empty<ExoLauncher.Models.GameEntry>(), sameAccount, hasLegendary: true);

        var control = Assert.Single(merged);
        Assert.True(control.Owned);
        Assert.True(control.CanInstall);
        Assert.Contains("last verified", control.LaunchNote, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(EpicAdapter.ParseOwnedCache(json, "scope-b"));
        Assert.Empty(EpicAdapter.ParseOwnedCache(json, null));
    }

    [Fact]
    public void MergeOwned_MatchesEpicIdWhenMachineRowHasNoLaunchTarget()
    {
        var machineRow = new ExoLauncher.Models.GameEntry
        {
            Id = "epic:Control",
            Title = "Control",
            Store = ExoLauncher.Models.StoreKind.Epic,
            Installed = true,
            Owned = false,
            CanInstall = false,
        };
        var owned = new[]
        {
            new LegendaryCli.GameRow("Control", "Control", null, null, false),
        };

        var merged = EpicAdapter.MergeOwned(new[] { machineRow }, owned, hasLegendary: true);

        var control = Assert.Single(merged);
        Assert.True(control.Installed);
        Assert.True(control.Owned);
        Assert.False(control.CanInstall);
    }

    [Fact]
    public void ActualShapedCache_WithNullInstallSize_PromotesRocketLeagueThroughLibraryPipeline()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "accountScope": "scope-a",
              "verifiedAtUtc": "2026-08-21T05:31:30.0000000+00:00",
              "provenance": "legendary-authenticated-list",
              "games": [
                {
                  "app_name": "Sugar",
                  "app_title": "Rocket League®",
                  "title": "Rocket League®",
                  "install_size": null
                }
              ]
            }
            """;
        var machineRow = new GameEntry
        {
            Id = "epic:Sugar",
            Title = "Rocket League",
            Store = StoreKind.Epic,
            Installed = true,
            Owned = false,
            EntitlementState = EntitlementState.Unverified,
            CanInstall = false,
            Path = @"C:\Program Files\Epic Games\rocketleague",
            LaunchTarget = "Sugar",
            Status = "Ready",
        };

        var cached = EpicAdapter.ParseOwnedCache(json, "scope-a");
        var merged = EpicAdapter.MergeOwned([machineRow], cached, hasLegendary: true);
        var timed = EpicPlaytime.Apply(
            merged,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Sugar"] = 1 });
        var enriched = PlaytimeService.Enrich(timed);
        var covered = CoverArtService.WithCovers(enriched);
        var grouped = LibraryService.GroupVariants(covered);

        var rocketLeague = Assert.Single(grouped);
        Assert.True(rocketLeague.Installed);
        Assert.True(rocketLeague.Owned);
        Assert.Equal(EntitlementState.Owned, rocketLeague.EntitlementState);
        Assert.False(rocketLeague.CanInstall);
        Assert.Equal("play", rocketLeague.PrimaryAction);
    }

    [Fact]
    public void OwnedRefresh_PublishesOnlyAfterSuccessfulWriteForStillActiveScope()
    {
        var rows = new[]
        {
            new LegendaryCli.GameRow("Sugar", "Rocket League®", null, null, false),
        };
        var notifications = 0;
        var writes = 0;
        void OnUpdated() => notifications++;

        EpicAdapter.OwnedLibraryCacheUpdated += OnUpdated;
        try
        {
            Assert.False(EpicAdapter.CommitOwnedLibraryRefresh(
                "scope-a",
                rows,
                () => "scope-b",
                (_, _) => { writes++; return true; }));
            Assert.Equal(0, writes);
            Assert.Equal(0, notifications);

            Assert.False(EpicAdapter.CommitOwnedLibraryRefresh(
                "scope-a",
                rows,
                () => "scope-a",
                (_, _) => { writes++; return false; }));
            Assert.Equal(1, writes);
            Assert.Equal(0, notifications);

            var activeScopes = new Queue<string?>(["scope-a", "scope-b"]);
            Assert.False(EpicAdapter.CommitOwnedLibraryRefresh(
                "scope-a",
                rows,
                activeScopes.Dequeue,
                (_, _) => { writes++; return true; }));
            Assert.Equal(2, writes);
            Assert.Equal(0, notifications);

            Assert.True(EpicAdapter.CommitOwnedLibraryRefresh(
                "scope-a",
                rows,
                () => "scope-a",
                (_, _) => { writes++; return true; }));
            Assert.Equal(3, writes);
            Assert.Equal(1, notifications);
        }
        finally
        {
            EpicAdapter.OwnedLibraryCacheUpdated -= OnUpdated;
        }
    }
}
