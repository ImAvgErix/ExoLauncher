using ExoLauncher.Models;
using ExoLauncher.Services.Achievements;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class AchievementProviderTests
{
    [Fact]
    public void EpicParser_NormalizesCompleteLegendarySnapshot()
    {
        const string json = """
            {
              "total_achievements": 3,
              "user_unlocked": 1,
              "completed": [
                {
                  "name": "FIRST_WIN",
                  "is_base": true,
                  "hidden": false,
                  "xp": 25,
                  "unlocked": true,
                  "progress": 100,
                  "unlock_date": "2026-08-10T01:02:03Z",
                  "display_name": "First Win",
                  "description": "Win one match.",
                  "icon_link": "https://shared-static-prod.epicgames.com/first.png",
                  "tier": "bronze",
                  "rarity": 12.5
                }
              ],
              "in_progress": [
                {
                  "name": "PLAY_TEN",
                  "hidden": false,
                  "xp": 50,
                  "unlocked": false,
                  "progress": 40,
                  "display_name": "Getting Started",
                  "description": "Play ten matches.",
                  "icon_link": "https://attacker.test/icon.png",
                  "tier": "silver",
                  "rarity": "8.25"
                }
              ],
              "uninitiated": [],
              "hidden": [
                {
                  "name": "SECRET",
                  "hidden": true,
                  "xp": 100,
                  "unlocked": false,
                  "progress": 0,
                  "display_name": "",
                  "description": ""
                }
              ]
            }
            """;
        var observed = DateTimeOffset.Parse("2026-08-10T02:00:00Z");

        var snapshot = EpicLegendaryAchievementProvider.ParseSnapshotJson(
            json, "Sugar", "epic:hashed-account", observed);

        Assert.Equal(AchievementCoverageStatus.Complete, snapshot.Coverage);
        Assert.Equal(AchievementProviderCapabilities.Snapshot |
                     AchievementProviderCapabilities.Progress |
                     AchievementProviderCapabilities.Rarity |
                     AchievementProviderCapabilities.CompleteCatalog,
            snapshot.Capabilities);
        Assert.Equal(3, snapshot.ReportedTotal);
        Assert.Equal(1, snapshot.ReportedUnlocked);
        Assert.Equal(3, snapshot.Entries.Count);

        var unlocked = Assert.Single(snapshot.Entries, row => row.Definition.ExternalId == "FIRST_WIN");
        Assert.True(unlocked.State.Unlocked);
        Assert.Equal(DateTimeOffset.Parse("2026-08-10T01:02:03Z"), unlocked.State.UnlockedAtUtc);
        Assert.Equal(25, unlocked.Definition.Points);
        Assert.Equal(12.5, unlocked.Definition.GlobalUnlockPercent);
        Assert.Equal("https://shared-static-prod.epicgames.com/first.png", unlocked.Definition.IconUnlockedUrl);

        var progress = Assert.Single(snapshot.Entries, row => row.Definition.ExternalId == "PLAY_TEN");
        Assert.Equal(40, progress.State.ProgressCurrent);
        Assert.Equal(100, progress.State.ProgressTarget);
        Assert.Null(progress.Definition.IconUnlockedUrl);

        var hidden = Assert.Single(snapshot.Entries, row => row.Definition.ExternalId == "SECRET");
        Assert.True(hidden.Definition.Hidden);
        Assert.Equal("Hidden achievement", hidden.Definition.Name);
    }

    [Fact]
    public async Task EpicProvider_UsesArgumentListAndReturnsUnavailableForMissingBackend()
    {
        IReadOnlyList<string>? captured = null;
        var provider = new EpicLegendaryAchievementProvider(
            () => "legendary.exe",
            (_, args, _) =>
            {
                captured = args.ToArray();
                return Task.FromResult((0, "{\"total_achievements\":0,\"user_unlocked\":0,\"completed\":[],\"in_progress\":[],\"uninitiated\":[],\"hidden\":[]}", ""));
            },
            () => "epic:hashed-account");
        var game = EpicGame();

        var snapshot = await provider.GetSnapshotAsync(game);

        Assert.Equal(new[] { "achievements", "Sugar", "--hidden", "--json" }, captured);
        Assert.Equal(AchievementCoverageStatus.Complete, snapshot.Coverage);

        var missing = new EpicLegendaryAchievementProvider(
            () => null,
            (_, _, _) => throw new InvalidOperationException("must not run"),
            () => "epic:hashed-account");
        var unavailable = await missing.GetSnapshotAsync(game);
        Assert.Equal(AchievementCoverageStatus.Unavailable, unavailable.Coverage);
        Assert.Contains("Legendary", unavailable.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EpicProvider_FailsClosedWhenAccountChangesDuringCliRefresh()
    {
        var calls = 0;
        var provider = new EpicLegendaryAchievementProvider(
            () => "legendary.exe",
            (_, _, _) => Task.FromResult((0,
                "{\"total_achievements\":0,\"user_unlocked\":0,\"completed\":[],\"in_progress\":[],\"uninitiated\":[],\"hidden\":[]}",
                "")),
            () => ++calls == 1
                ? "epic:0123456789abcdef0123456789abcdef"
                : "epic:fedcba9876543210fedcba9876543210");

        var snapshot = await provider.GetSnapshotAsync(EpicGame());

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Null(snapshot.ReportedTotal);
        Assert.Contains("changed", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-1", "0")]
    [InlineData("3", "-1")]
    [InlineData("3", "99")]
    [InlineData("\"not-a-number\"", "0")]
    [InlineData("3", "\"not-a-number\"")]
    public void EpicParser_FailsClosedOnContradictorySummaries(string total, string unlocked)
    {
        var json = $$"""
            {
              "total_achievements": {{total}},
              "user_unlocked": {{unlocked}},
              "completed": [],
              "in_progress": [],
              "uninitiated": [],
              "hidden": []
            }
            """;

        var snapshot = EpicLegendaryAchievementProvider.ParseSnapshotJson(
            json, "Sugar", "epic:hashed-account", DateTimeOffset.UtcNow);

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Contains("contradictory", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EpicParser_FailsClosedWhenReportedTotalIsSmallerThanCatalog()
    {
        const string json = """
            {
              "total_achievements": 0,
              "user_unlocked": 0,
              "completed": [{ "name": "FIRST_WIN", "unlocked": true }],
              "in_progress": [],
              "uninitiated": [],
              "hidden": []
            }
            """;

        var snapshot = EpicLegendaryAchievementProvider.ParseSnapshotJson(
            json, "Sugar", "epic:hashed-account", DateTimeOffset.UtcNow);

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
    }

    [Fact]
    public void EpicParser_FailsClosedOnEmptyCatalogWithoutExplicitTotal()
    {
        const string json = """
            { "completed": [], "in_progress": [], "uninitiated": [], "hidden": [] }
            """;

        var snapshot = EpicLegendaryAchievementProvider.ParseSnapshotJson(
            json, "Sugar", "epic:hashed-account", DateTimeOffset.UtcNow);

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
    }

    [Theory]
    [InlineData("{ \"total_achievements\": 1, \"completed\": [], \"in_progress\": [], \"uninitiated\": [], \"hidden\": [] }")]
    [InlineData("{ \"user_unlocked\": 0, \"completed\": [], \"in_progress\": [], \"uninitiated\": [], \"hidden\": [] }")]
    public void EpicParser_FailsClosedWhenEitherAccountSummaryIsMissing(string json)
    {
        var snapshot = EpicLegendaryAchievementProvider.ParseSnapshotJson(
            json, "Sugar", "epic:hashed-account", DateTimeOffset.UtcNow);

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Contains("totals", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EpicParser_FailsClosedOnCaseInsensitiveDuplicateAchievementIds()
    {
        const string json = """
            {
              "total_achievements": 2,
              "user_unlocked": 1,
              "completed": [{ "name": "FIRST_WIN", "unlocked": true }],
              "in_progress": [{ "name": "first_win", "unlocked": false }],
              "uninitiated": [],
              "hidden": []
            }
            """;

        var snapshot = EpicLegendaryAchievementProvider.ParseSnapshotJson(
            json, "Sugar", "epic:hashed-account", DateTimeOffset.UtcNow);

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Contains("duplicate", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EpicProvider_RejectsMismatchedLibraryArtifactAndLaunchTarget()
    {
        var called = false;
        var provider = new EpicLegendaryAchievementProvider(
            () => "legendary.exe",
            (_, _, _) =>
            {
                called = true;
                return Task.FromResult((0, "{}", ""));
            },
            () => "epic:hashed-account");
        var game = new GameEntry
        {
            Id = "epic:Sugar",
            Title = "Rocket League",
            Store = StoreKind.Epic,
            Installed = true,
            LaunchTarget = "NotSugar",
        };

        // The provider remains selected so the UI reports unavailable source
        // data instead of mislabelling a corrupt Epic mapping as unsupported.
        Assert.True(provider.Supports(game));
        var snapshot = await provider.GetSnapshotAsync(game);

        Assert.False(called);
        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Contains("disagree", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EpicParser_FailsClosedWhenCompleteRowsContradictUnlockedSummary()
    {
        const string json = """
            {
              "total_achievements": 1,
              "user_unlocked": 0,
              "completed": [{ "name": "FIRST_WIN", "unlocked": true }],
              "in_progress": [],
              "uninitiated": [],
              "hidden": []
            }
            """;

        var snapshot = EpicLegendaryAchievementProvider.ParseSnapshotJson(
            json, "Sugar", "epic:hashed-account", DateTimeOffset.UtcNow);

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
    }

    [Fact]
    public void SteamParser_ReportsBestEffortPartialCoverage()
    {
        const string json = """
            {
              "achievements": {
                "nAchieved": 1,
                "nTotal": 40,
                "vecHighlight": [
                  {
                    "strID": "ACH_WIN",
                    "strName": "Winner",
                    "strDescription": "Win a match.",
                    "bAchieved": true,
                    "rtUnlocked": 1786323723,
                    "flCurrentProgress": 1,
                    "flMaxProgress": 1,
                    "strImage": "https://cdn.akamai.steamstatic.com/win.png"
                  }
                ],
                "vecUnachieved": [
                  {
                    "strID": "ACH_GRIND",
                    "strName": "Keep Going",
                    "strDescription": "Play 100 matches.",
                    "bAchieved": false,
                    "flCurrentProgress": 32,
                    "flMaxProgress": 100
                  },
                  {
                    "strID": "ACH_SECRET",
                    "bAchieved": false
                  }
                ]
              },
              "achievementmap": {
                "ACH_SECRET": {
                  "strID": "ACH_SECRET",
                  "strName": "Spoiler name",
                  "strDescription": "Spoiler description",
                  "strImage": "https://attacker.test/spoiler.png",
                  "bHidden": true,
                  "bAchieved": false
                }
              }
            }
            """;

        var snapshot = SteamLibraryCacheAchievementProvider.ParseSnapshotJson(
            json,
            "252950",
            "steam:hashed-account",
            DateTimeOffset.Parse("2026-08-10T02:00:00Z"));

        Assert.Equal(AchievementCoverageStatus.Partial, snapshot.Coverage);
        Assert.False(snapshot.Capabilities.HasFlag(AchievementProviderCapabilities.CompleteCatalog));
        Assert.Equal(40, snapshot.ReportedTotal);
        Assert.Equal(1, snapshot.ReportedUnlocked);
        Assert.Equal(3, snapshot.Entries.Count);

        var win = Assert.Single(snapshot.Entries, row => row.Definition.ExternalId == "ACH_WIN");
        Assert.True(win.State.Unlocked);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786323723), win.State.UnlockedAtUtc);
        Assert.Equal("https://cdn.akamai.steamstatic.com/win.png", win.Definition.IconUnlockedUrl);

        var grind = Assert.Single(snapshot.Entries, row => row.Definition.ExternalId == "ACH_GRIND");
        Assert.Equal(32, grind.State.ProgressCurrent);
        Assert.Equal(100, grind.State.ProgressTarget);

        var secret = Assert.Single(snapshot.Entries, row => row.Definition.ExternalId == "ACH_SECRET");
        Assert.True(secret.Definition.Hidden);
        Assert.Equal("Hidden achievement", secret.Definition.Name);
        Assert.Empty(secret.Definition.Description);
        Assert.Null(secret.Definition.IconUnlockedUrl);
        Assert.Null(secret.Definition.IconLockedUrl);
    }

    [Fact]
    public void SteamParser_ReadsLiveLibraryCacheTupleEnvelopeAndNestedMapJson()
    {
        const string json = """
            [
              ["badge", {"version": 1, "data": {"strName": "ignored"}}],
              ["achievements", {
                "version": 1,
                "data": {
                  "nAchieved": 1,
                  "nTotal": 40,
                   "vecHighlight": [
                    {
                      "strID": "ACH_WIN",
                      "strName": "Winner",
                      "bAchieved": true,
                      "rtUnlocked": 1786323723
                    }
                   ],
                   "vecUnachieved": [
                     {"strID":"ACH_MAP","bAchieved":false}
                   ],
                  "vecAchievedHidden": []
                }
              }],
              ["achievementmap", {
                "version": 1,
                 "data": "[[252950,[[\"ACH_MAP\",{\"strID\":\"ach_map\",\"strName\":\"Mapped\",\"strDescription\":\"From nested JSON\",\"strImage\":\"https://attacker.test/mapped.png\"}]]]]"
              }]
            ]
            """;

        var snapshot = SteamLibraryCacheAchievementProvider.ParseSnapshotJson(
            json,
            "252950",
            "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.Parse("2026-08-10T02:00:00Z"));

        Assert.Equal(AchievementCoverageStatus.Partial, snapshot.Coverage);
        Assert.Equal(40, snapshot.ReportedTotal);
        Assert.Equal(1, snapshot.ReportedUnlocked);
        Assert.Contains(snapshot.Entries, row => row.Definition.ExternalId == "ACH_WIN");
        var mapped = Assert.Single(snapshot.Entries, row => row.Definition.ExternalId == "ACH_MAP");
        Assert.Equal("Mapped", mapped.Definition.Name);
        Assert.Null(mapped.Definition.IconUnlockedUrl);
    }

    [Fact]
    public void SteamParser_OnlyUsesAccountVectorsForStateAndMapOnlyEnrichesKnownIds()
    {
        const string json = """
            {
              "achievements": {
                "nAchieved": 0,
                "nTotal": 1,
                "vecUnachieved": [{"strID":"ACH_OWN","bAchieved":false}]
              },
              "achievementmap": {
                "foreign": {"strID":"ACH_FOREIGN","strName":"Wrong game","bAchieved":true},
                "own": {"strID":"ach_own","strName":"Correct metadata","bAchieved":true}
              }
            }
            """;

        var snapshot = SteamLibraryCacheAchievementProvider.ParseSnapshotJson(
            json, "252950", "steam:0123456789abcdef0123456789abcdef", DateTimeOffset.UtcNow);

        Assert.Equal(AchievementCoverageStatus.Partial, snapshot.Coverage);
        var own = Assert.Single(snapshot.Entries);
        Assert.Equal("ACH_OWN", own.Definition.ExternalId);
        Assert.Equal("Correct metadata", own.Definition.Name);
        Assert.False(own.State.Unlocked);
    }

    [Fact]
    public void SteamParser_FailsClosedForContradictoryDuplicateAccountRows()
    {
        const string json = """
            {"achievements":{"nAchieved":1,"nTotal":1,
              "vecHighlight":[{"strID":"ACH_ONE","bAchieved":true}],
              "vecUnachieved":[{"strID":"ach_one","bAchieved":false}]}}
            """;

        var snapshot = SteamLibraryCacheAchievementProvider.ParseSnapshotJson(
            json, "252950", "steam:0123456789abcdef0123456789abcdef", DateTimeOffset.UtcNow);

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Contains("contradictory", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SteamParser_FailsClosedWhenAccountRowsExceedUnlockedHeader()
    {
        const string json = """
            {"achievements":{"nAchieved":0,"nTotal":1,
              "vecHighlight":[{"strID":"ACH_ONE","bAchieved":true}]}}
            """;

        var snapshot = SteamLibraryCacheAchievementProvider.ParseSnapshotJson(
            json, "252950", "steam:0123456789abcdef0123456789abcdef", DateTimeOffset.UtcNow);

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Contains("inconsistent", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SteamProvider_RejectsMismatchedSteamIdAndLaunchTarget()
    {
        var provider = new SteamLibraryCacheAchievementProvider(
            () => throw new InvalidOperationException("must not resolve Steam for inconsistent ids"),
            _ => throw new InvalidOperationException("must not resolve an account for inconsistent ids"));

        var snapshot = await provider.GetSnapshotAsync(new GameEntry
        {
            Id = "steam:252950",
            Title = "Rocket League",
            Store = StoreKind.Steam,
            LaunchTarget = "1110910",
        });

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Contains("valid Steam app id", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SteamParser_LeavesAnExplicitLocalZeroUnverified()
    {
        const string json = """
            [["achievements",{"version":2,"data":{
              "vecHighlight":[],
              "vecUnachieved":[],
              "vecAchievedHidden":[],
              "nTotal":0,
              "nAchieved":0
            }}]]
            """;

        var snapshot = SteamLibraryCacheAchievementProvider.ParseSnapshotJson(
            json,
            "1422450",
            "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.Parse("2026-08-10T02:00:00Z"));

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Equal(0, snapshot.ReportedTotal);
        Assert.Equal(0, snapshot.ReportedUnlocked);
        Assert.Empty(snapshot.Entries);
        Assert.Contains("requires separate catalog verification", snapshot.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SteamProvider_DoesNotUseStoreCategoriesToConfirmZero()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-achievements-" + Guid.NewGuid().ToString("N"));
        var cacheDirectory = Path.Combine(root, "userdata", "1162499906", "config", "librarycache");
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "1422450.json"), """
            [["achievements",{"version":2,"data":{
              "vecHighlight":[],"vecUnachieved":[],"vecAchievedHidden":[],"nTotal":0,"nAchieved":0
            }}]]
            """);
        try
        {
            var provider = new SteamLibraryCacheAchievementProvider(
                () => root,
                _ => "1162499906");
            var snapshot = await provider.GetSnapshotAsync(new GameEntry
            {
                Id = "steam:1422450",
                Title = "Deadlock",
                Store = StoreKind.Steam,
                LaunchTarget = "1422450",
            });

            Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
            Assert.Null(snapshot.ReportedTotal);
            Assert.Null(snapshot.ReportedUnlocked);
            Assert.Empty(snapshot.Entries);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SteamProvider_HasNoUndocumentedStoreCategoryProof()
    {
        var source = File.ReadAllText(FindRepoFile(
            "ExoLauncher", "Services", "Achievements", "SteamLibraryCacheAchievementProvider.cs"));
        Assert.DoesNotContain("store.steampowered.com/api/appdetails", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("id == 22", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamStoreAchievementCatalogStatus", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SteamProvider_LeavesLocalZeroUnavailableWithoutAccountApiProof()
    {
        var root = await WriteSteamZeroCacheAsync("1110910");
        try
        {
            var provider = new SteamLibraryCacheAchievementProvider(
                () => root,
                _ => "1162499906");

            var snapshot = await provider.GetSnapshotAsync(SteamGame("1110910", "Mortal Shell"));

            // Uncorroborated local zero is not a progress row.
            Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Message));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamProvider_FailsHonestWhenTheLocalCacheIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-achievements-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var provider = new SteamLibraryCacheAchievementProvider(
                () => root,
                _ => "1162499906");
            var snapshot = await provider.GetSnapshotAsync(new GameEntry
            {
                Id = "steam:252950",
                Title = "Rocket League",
                Store = StoreKind.Steam,
                LaunchTarget = "252950",
            });

            Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
            Assert.Null(snapshot.ReportedTotal);
            Assert.Null(snapshot.ReportedUnlocked);
            Assert.Empty(snapshot.Entries);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamProvider_RetriesAnInPlaceCacheWriteBeforeReturningUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-achievements-" + Guid.NewGuid().ToString("N"));
        var cacheDirectory = Path.Combine(root, "userdata", "1162499906", "config", "librarycache");
        Directory.CreateDirectory(cacheDirectory);
        var cachePath = Path.Combine(cacheDirectory, "1903340.json");
        await File.WriteAllTextAsync(cachePath, "{");
        try
        {
            var valid = """
                [["achievements",{"version":2,"data":{
                  "vecHighlight":[],
                  "vecUnachieved":[{"strID":"EXPEDITION_ONE","strName":"First step","bAchieved":false}],
                  "vecAchievedHidden":[],"nTotal":55,"nAchieved":47
                }}]]
                """;
            _ = Task.Run(async () =>
            {
                await Task.Delay(160);
                await File.WriteAllTextAsync(cachePath, valid);
            });

            var provider = new SteamLibraryCacheAchievementProvider(() => root, _ => "1162499906");
            var snapshot = await provider.GetSnapshotAsync(SteamGame("1903340", "Clair Obscur: Expedition 33"));

            Assert.Equal(AchievementCoverageStatus.Partial, snapshot.Coverage);
            Assert.Equal(55, snapshot.ReportedTotal);
            Assert.Equal(47, snapshot.ReportedUnlocked);
            Assert.Single(snapshot.Entries);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SteamCommunityParser_RejectsDtdPayloads()
    {
        const string xml = """
            <!DOCTYPE profile [<!ENTITY payload "unsafe">]>
            <profile><achievements><achievement><apiname>ONE</apiname><name>&payload;</name></achievement></achievements></profile>
            """;

        var snapshot = SteamLibraryCacheAchievementProvider.ParseCommunitySnapshotXml(
            xml,
            "1110910",
            "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.Parse("2026-08-10T02:00:00Z"));

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public void SteamCommunityParser_NeverTreatsCatalogRowsAsAccountProgress()
    {
        const string xml = """
            <profile><achievements>
              <achievement closed="1"><apiname>ONE</apiname><name>One</name><unlockTimestamp>1786323723</unlockTimestamp></achievement>
              <achievement closed="0"><apiname>TWO</apiname><name>Two</name></achievement>
            </achievements></profile>
            """;

        var snapshot = SteamLibraryCacheAchievementProvider.ParseCommunitySnapshotXml(
            xml,
            "1110910",
            "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.Parse("2026-08-10T02:00:00Z"));

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Null(snapshot.ReportedTotal);
        Assert.Null(snapshot.ReportedUnlocked);
        Assert.Empty(snapshot.Entries);
        Assert.Contains("cannot verify", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SteamParser_UsesHeaderCountsWhenSteamOnlySuppliesHighlights()
    {
        const string json = """
            [["achievements",{"version":2,"data":{
              "vecHighlight":[],"vecUnachieved":[],"vecAchievedHidden":[],"nTotal":37,"nAchieved":1
            }}]]
            """;

        var snapshot = SteamLibraryCacheAchievementProvider.ParseSnapshotJson(
            json,
            "1110910",
            "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.Parse("2026-08-10T02:00:00Z"));

        Assert.Equal(AchievementCoverageStatus.Partial, snapshot.Coverage);
        Assert.Equal(37, snapshot.ReportedTotal);
        Assert.Equal(1, snapshot.ReportedUnlocked);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public async Task SteamProvider_ReadsOnlyResolvedAccountCacheAndHashesCoverage()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-achievements-" + Guid.NewGuid().ToString("N"));
        var cacheDirectory = Path.Combine(root, "userdata", "111", "config", "librarycache");
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "252950.json"), """
            [["achievements",{"version":1,"data":{
              "nAchieved":0,
              "nTotal":1,
              "vecHighlight":[],
              "vecUnachieved":[{"strID":"ACH_ONE","strName":"One","bAchieved":false}],
              "vecAchievedHidden":[]
            }}]]
            """);
        try
        {
            var provider = new SteamLibraryCacheAchievementProvider(() => root, _ => "111");
            var game = new GameEntry
            {
                Id = "steam:252950",
                Title = "Rocket League",
                Store = StoreKind.Steam,
                LaunchTarget = "252950",
            };

            var snapshot = await provider.GetSnapshotAsync(game);

            Assert.Equal(AchievementCoverageStatus.Partial, snapshot.Coverage);
            Assert.Single(snapshot.Entries);
            Assert.StartsWith("steam:", snapshot.CoverageKey, StringComparison.Ordinal);
            Assert.Equal(38, snapshot.CoverageKey.Length);
            Assert.DoesNotContain("111", snapshot.CoverageKey, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamProvider_KeepsUsableCacheWhenActiveAccountChurnsDuringRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-achievements-" + Guid.NewGuid().ToString("N"));
        var cacheDirectory = Path.Combine(root, "userdata", "111", "config", "librarycache");
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "252950.json"), """
            [["achievements",{"version":1,"data":{
              "nAchieved":0,"nTotal":1,"vecHighlight":[],"vecUnachieved":[],"vecAchievedHidden":[]
            }}]]
            """);
        try
        {
            var calls = 0;
            var provider = new SteamLibraryCacheAchievementProvider(
                () => root,
                _ => ++calls == 1 ? "111" : "222");

            var snapshot = await provider.GetSnapshotAsync(SteamGame("252950", "Rocket League"));

            // Account changed while reading — do not keep the first account's row.
            Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
            Assert.Contains("account changed", snapshot.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"achievements\":{\"nTotal\":40}}")]
    public void ProviderParsers_FailClosedOnMissingAchievementRows(string json)
    {
        var observed = DateTimeOffset.Parse("2026-08-10T02:00:00Z");

        var epic = EpicLegendaryAchievementProvider.ParseSnapshotJson(
            json, "Sugar", "epic:hash", observed);
        var steam = SteamLibraryCacheAchievementProvider.ParseSnapshotJson(
            json, "252950", "steam:hash", observed);
        var gog = GogGameplayAchievementProvider.ParseSnapshotJson(
            json, "1423049311", "gog:hash", observed);

        Assert.Equal(AchievementCoverageStatus.Unavailable, epic.Coverage);
        Assert.Equal(AchievementCoverageStatus.Unavailable, steam.Coverage);
        Assert.Equal(AchievementCoverageStatus.Unavailable, gog.Coverage);
        Assert.Empty(epic.Entries);
        Assert.Empty(steam.Entries);
        Assert.Empty(gog.Entries);
    }

    private static GameEntry EpicGame() => new()
    {
        Id = "epic:Sugar",
        Title = "Rocket League",
        Store = StoreKind.Epic,
        Installed = true,
        LaunchTarget = "Sugar",
    };

    [Fact]
    public async Task SteamProvider_DoesNotReadAnotherAccountsLibraryCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-achievements-" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(root, "userdata", "222", "config", "librarycache");
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(other, "252950.json"), """
            [["achievements",{"version":1,"data":{
              "nAchieved":12,"nTotal":40,"vecHighlight":[],"vecUnachieved":[],"vecAchievedHidden":[]
            }}]]
            """);
        try
        {
            var provider = new SteamLibraryCacheAchievementProvider(() => root, _ => "111");
            var snapshot = await provider.GetSnapshotAsync(SteamGame("252950", "Rocket League"));
            Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static GameEntry SteamGame(string appId, string title) => new()
    {
        Id = "steam:" + appId,
        Title = title,
        Store = StoreKind.Steam,
        Installed = true,
        LaunchTarget = appId,
    };

    private static async Task<string> WriteSteamZeroCacheAsync(string appId)
    {
        var root = Path.Combine(Path.GetTempPath(),
            "exo-steam-achievements-" + Guid.NewGuid().ToString("N"));
        var cacheDirectory = Path.Combine(root, "userdata", "1162499906", "config", "librarycache");
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, appId + ".json"), """
            [["achievements",{"version":2,"data":{
              "vecHighlight":[],"vecUnachieved":[],"vecAchievedHidden":[],"nTotal":0,"nAchieved":0
            }}]]
            """);
        return root;
    }

    [Fact]
    public void SteamWebApiParser_UsesAchievedFlagNotUnlockTimeAlone()
    {
        const string player = """
            {"playerstats":{"steamID":"76561199122765634","success":true,"achievements":[
              {"apiname":"ACH_WIN","achieved":1,"unlocktime":1786323723},
              {"apiname":"ACH_GRIND","achieved":0,"unlocktime":1786323723}
            ]}}
            """;
        const string schema = """
            {"game":{"availableGameStats":{"achievements":[
              {"name":"ACH_WIN","displayName":"Winner","description":"Win.","hidden":0,
               "icon":"https://cdn.akamai.steamstatic.com/win.png","icongray":"https://cdn.akamai.steamstatic.com/win-off.png"},
              {"name":"ACH_GRIND","displayName":"Keep Going","description":"Play.","hidden":0,
               "icon":"https://attacker.test/grind.png"}
            ]}}}
            """;

        var snapshot = SteamWebApiAchievementParser.ParsePlayerAchievements(
            player, schema, "252950", "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.Parse("2026-08-10T02:00:00Z"),
            expectedSteamId64: "76561199122765634");

        Assert.Equal(AchievementCoverageStatus.Complete, snapshot.Coverage);
        Assert.Equal(2, snapshot.ReportedTotal);
        Assert.Equal(1, snapshot.ReportedUnlocked);
        var win = Assert.Single(snapshot.Entries, row => row.Definition.ExternalId == "ACH_WIN");
        Assert.True(win.State.Unlocked);
        Assert.Equal("Winner", win.Definition.Name);
        Assert.Equal("https://cdn.akamai.steamstatic.com/win.png", win.Definition.IconUnlockedUrl);
        var grind = Assert.Single(snapshot.Entries, row => row.Definition.ExternalId == "ACH_GRIND");
        Assert.False(grind.State.Unlocked);
        Assert.Null(grind.State.UnlockedAtUtc);
        Assert.Null(grind.Definition.IconUnlockedUrl);
    }

    [Fact]
    public void SteamWebApiParser_FailsClosedWhenAchievedFlagIsMissing()
    {
        const string player = """
            {"playerstats":{"steamID":"76561199122765634","success":true,"achievements":[{"apiname":"ACH_WIN","unlocktime":1}]}}
            """;
        const string schema = """
            {"game":{"availableGameStats":{"achievements":[
              {"name":"ACH_WIN","displayName":"Winner","hidden":0}
            ]}}}
            """;

        var snapshot = SteamWebApiAchievementParser.ParsePlayerAchievements(
            player, schema, "252950", "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.UtcNow, expectedSteamId64: "76561199122765634");

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Contains("unlock flag", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SteamWebApiParser_RequiresACompleteMatchingSchema()
    {
        const string player = """
            {"playerstats":{"steamID":"76561199122765634","success":true,"achievements":[
              {"apiname":"ACH_ONE","achieved":0,"unlocktime":0}
            ]}}
            """;
        const string mismatchedSchema = """
            {"game":{"availableGameStats":{"achievements":[
              {"name":"ACH_OTHER","displayName":"Other","hidden":0}
            ]}}}
            """;

        var missing = SteamWebApiAchievementParser.ParsePlayerAchievements(
            player, null, "252950", "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.UtcNow, expectedSteamId64: "76561199122765634");
        var mismatched = SteamWebApiAchievementParser.ParsePlayerAchievements(
            player, mismatchedSchema, "252950", "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.UtcNow, expectedSteamId64: "76561199122765634");

        Assert.Equal(AchievementCoverageStatus.Unavailable, missing.Coverage);
        Assert.Equal(AchievementCoverageStatus.Unavailable, mismatched.Coverage);
        Assert.Empty(missing.Entries);
        Assert.Empty(mismatched.Entries);
    }

    [Fact]
    public void SteamWebApiParser_RejectsTheWrongSteamAccount()
    {
        const string player = """
            {"playerstats":{"steamID":"76561198000000000","success":true,"achievements":[
              {"apiname":"ACH_ONE","achieved":0,"unlocktime":0}
            ]}}
            """;
        const string schema = """
            {"game":{"availableGameStats":{"achievements":[
              {"name":"ACH_ONE","displayName":"One","hidden":0}
            ]}}}
            """;

        var snapshot = SteamWebApiAchievementParser.ParsePlayerAchievements(
            player, schema, "252950", "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.UtcNow, expectedSteamId64: "76561199122765634");

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public void SteamWebApiParser_RedactsLockedHiddenMetadataAndArt()
    {
        const string player = """
            {"playerstats":{"steamID":"76561199122765634","success":true,"achievements":[
              {"apiname":"ACH_SECRET","achieved":0,"unlocktime":0}
            ]}}
            """;
        const string schema = """
            {"game":{"availableGameStats":{"achievements":[
              {"name":"ACH_SECRET","displayName":"Spoiler","description":"Secret details.","hidden":1,
               "icon":"https://attacker.test/secret.png","icongray":"https://attacker.test/secret-off.png"}
            ]}}}
            """;

        var snapshot = SteamWebApiAchievementParser.ParsePlayerAchievements(
            player, schema, "252950", "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.UtcNow, expectedSteamId64: "76561199122765634");

        var hidden = Assert.Single(snapshot.Entries);
        Assert.Equal("Hidden achievement", hidden.Definition.Name);
        Assert.Empty(hidden.Definition.Description);
        Assert.Null(hidden.Definition.IconUnlockedUrl);
        Assert.Null(hidden.Definition.IconLockedUrl);
    }

    [Fact]
    public void SteamWebApiParser_DoesNotTreatMissingPlayerRowsAsAConfirmedEmptyCatalog()
    {
        const string player = """
            {"playerstats":{"steamID":"76561199122765634","success":true}}
            """;
        const string emptySchema = """
            {"game":{"availableGameStats":{"achievements":[]}}}
            """;

        var snapshot = SteamWebApiAchievementParser.ParsePlayerAchievements(
            player, emptySchema, "252950", "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.UtcNow, expectedSteamId64: "76561199122765634");

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public void SteamWebApiParser_ConfirmsEmptyOnlyFromMatchingEmptyPlayerRowsAndSchema()
    {
        const string player = """
            {"playerstats":{"steamID":"76561199122765634","success":true,"achievements":[]}}
            """;
        const string schema = """
            {"game":{"availableGameStats":{"achievements":[]}}}
            """;

        var snapshot = SteamWebApiAchievementParser.ParsePlayerAchievements(
            player, schema, "252950", "steam:0123456789abcdef0123456789abcdef",
            DateTimeOffset.UtcNow, expectedSteamId64: "76561199122765634");

        Assert.Equal(AchievementCoverageStatus.Complete, snapshot.Coverage);
        Assert.Equal(0, snapshot.ReportedTotal);
        Assert.Equal(0, snapshot.ReportedUnlocked);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public void SteamId64_AddsTheKnownUniverseOffset()
    {
        Assert.True(SteamWebApiAchievementParser.TrySteamId64("1162499906", out var id));
        Assert.Equal("76561199122765634", id);
        Assert.False(SteamWebApiAchievementParser.TrySteamId64("0", out _));
        Assert.False(SteamWebApiAchievementParser.TrySteamId64("not-a-number", out _));
    }

    [Fact]
    public async Task SteamProvider_PrefersWebApiCatalogOverStaleLocalZero()
    {
        var root = await WriteSteamZeroCacheAsync("1110910");
        try
        {
            var provider = new SteamLibraryCacheAchievementProvider(
                () => root,
                _ => "1162499906",
                () => "0123456789abcdef0123",
                (uri, _) =>
                {
                    if (uri.AbsolutePath.Contains("GetPlayerAchievements", StringComparison.Ordinal))
                    {
                        return Task.FromResult<string?>("""
                            {"playerstats":{"steamID":"76561199122765634","success":true,"achievements":[
                              {"apiname":"ACH_ONE","achieved":1,"unlocktime":1786323723}
                            ]}}
                            """);
                    }
                    return Task.FromResult<string?>("""
                        {"game":{"availableGameStats":{"achievements":[
                          {"name":"ACH_ONE","displayName":"One","hidden":0}
                        ]}}}
                        """);
                });

            var snapshot = await provider.GetSnapshotAsync(SteamGame("1110910", "Mortal Shell"));

            Assert.Equal(AchievementCoverageStatus.Complete, snapshot.Coverage);
            Assert.Equal(1, snapshot.ReportedUnlocked);
            Assert.Equal("One", Assert.Single(snapshot.Entries).Definition.Name);
            Assert.Contains("Web API", snapshot.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GogParser_NormalizesGameplayApiSnapshot()
    {
        const string json = """
            {"items":[
              {
                "achievement_id":"1",
                "achievement_key":"FIRST_WIN",
                "visible":true,
                "name":"First Win",
                "description":"Win one match.",
                "image_url_unlocked":"https://images.gog.com/first.png",
                "image_url_locked":"https://attacker.test/icon.png",
                "rarity":12.5,
                "date_unlocked":"2026-08-10T01:02:03+00:00",
                "rarity_level_slug":"common"
              },
              {
                "achievement_id":"2",
                "achievement_key":"SECRET",
                "visible":false,
                "name":"Secret",
                "description":"Hidden.",
                "rarity":1.0,
                "date_unlocked":null
              }
            ]}
            """;

        var snapshot = GogGameplayAchievementProvider.ParseSnapshotJson(
            json, "1423049311", "gog:hashed-account", DateTimeOffset.Parse("2026-08-10T02:00:00Z"));

        Assert.Equal(AchievementCoverageStatus.Complete, snapshot.Coverage);
        Assert.Equal(2, snapshot.ReportedTotal);
        Assert.Equal(1, snapshot.ReportedUnlocked);
        var win = Assert.Single(snapshot.Entries, row => row.Definition.ExternalId == "FIRST_WIN");
        Assert.True(win.State.Unlocked);
        Assert.Equal("https://images.gog.com/first.png", win.Definition.IconUnlockedUrl);
        Assert.Null(win.Definition.IconLockedUrl);
        var secret = Assert.Single(snapshot.Entries, row => row.Definition.ExternalId == "SECRET");
        Assert.True(secret.Definition.Hidden);
        Assert.Equal("Hidden achievement", secret.Definition.Name);
        Assert.False(secret.State.Unlocked);
    }

    [Fact]
    public async Task GogProvider_ReturnsUnavailableWhenUnsignedAndWhenProductIdIsMissing()
    {
        var called = false;
        var missingSession = new GogGameplayAchievementProvider(
            () => null,
            (_, _, _) =>
            {
                called = true;
                return Task.FromResult<string?>("{}");
            });
        var game = new GameEntry
        {
            Id = "gog:1423049311",
            Title = "Celeste",
            Store = StoreKind.Gog,
            Installed = true,
            LaunchTarget = "1423049311",
        };

        var unsigned = await missingSession.GetSnapshotAsync(game);
        Assert.False(called);
        Assert.Equal(AchievementCoverageStatus.Unavailable, unsigned.Coverage);
        Assert.Contains("not signed in", unsigned.Message, StringComparison.OrdinalIgnoreCase);

        var signed = new GogGameplayAchievementProvider(
            () => ("user-1", "token"),
            (_, _, _) => throw new InvalidOperationException("must not run"));
        var mismatch = await signed.GetSnapshotAsync(new GameEntry
        {
            Id = "gog:1423049311",
            Title = "Celeste",
            Store = StoreKind.Gog,
            Installed = true,
            LaunchTarget = "999",
        });
        Assert.Equal(AchievementCoverageStatus.Unavailable, mismatch.Coverage);
        Assert.Contains("valid GOG product id", mismatch.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GogProvider_FailsClosedWhenAccountChangesDuringRefresh()
    {
        var calls = 0;
        var provider = new GogGameplayAchievementProvider(
            () => (++calls == 1 ? ("user-a", "token") : ("user-b", "token")),
            (_, _, _) => Task.FromResult<string?>("""{"items":[]}"""));

        var snapshot = await provider.GetSnapshotAsync(new GameEntry
        {
            Id = "gog:1423049311",
            Title = "Celeste",
            Store = StoreKind.Gog,
            LaunchTarget = "1423049311",
        });

        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Contains("changed", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GogParser_DoesNotInventAnUnlockFromATimestampWhenExplicitlyLocked()
    {
        const string json = """
            {"items":[{
              "achievement_key":"LEFTOVER",
              "unlocked":false,
              "date_unlocked":"2026-08-10T01:02:03+00:00"
            }]}
            """;

        var snapshot = GogGameplayAchievementProvider.ParseSnapshotJson(
            json, "1423049311", "gog:hashed-account", DateTimeOffset.UtcNow);

        var row = Assert.Single(snapshot.Entries);
        Assert.False(row.State.Unlocked);
        Assert.Null(row.State.UnlockedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"items\":[{\"achievement_key\":\"A\"},{\"achievement_key\":\"A\"}]}")]
    public void GogParser_FailsClosedOnMissingOrDuplicateRows(string json)
    {
        var snapshot = GogGameplayAchievementProvider.ParseSnapshotJson(
            json, "1423049311", "gog:hashed-account", DateTimeOffset.UtcNow);
        Assert.Equal(AchievementCoverageStatus.Unavailable, snapshot.Coverage);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public void GogProvider_BuildsTheOfficialGameplayUri()
    {
        Assert.Equal(
            "https://gameplay.gog.com/clients/1423049311/users/user-1/achievements",
            GogGameplayAchievementProvider.AchievementsUri("user-1", "1423049311"));
    }

    [Fact]
    public void SteamProvider_PollsFasterOnlyWhenAWebApiKeyIsPresent()
    {
        var noKey = new SteamLibraryCacheAchievementProvider(
            () => @"C:\missing-steam", _ => "1", () => null);
        Assert.Equal(TimeSpan.FromSeconds(12), noKey.SuggestedPollInterval);

        var keyed = new SteamLibraryCacheAchievementProvider(
            () => @"C:\missing-steam", _ => "1", () => "0123456789abcdef0123");
        Assert.Equal(TimeSpan.FromSeconds(8), keyed.SuggestedPollInterval);
    }

    [Fact]
    public void SteamProvider_ReadsTheInstalledWebApiKeyStore()
    {
        var source = File.ReadAllText(FindRepoFile(
            "ExoLauncher", "Services", "Achievements", "SteamLibraryCacheAchievementProvider.cs"));
        Assert.Contains("SteamWebApiKeyStore.TryRead()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.get", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SteamProvider_WithoutKey_DoesNotCallWebApi()
    {
        var root = await WriteSteamZeroCacheAsync("252950");
        var cacheDirectory = Path.Combine(root, "userdata", "1162499906", "config", "librarycache");
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "252950.json"), """
            [["achievements",{"version":1,"data":{
              "nAchieved":0,"nTotal":1,
              "vecHighlight":[],
              "vecUnachieved":[{"strID":"ACH_ONE","bAchieved":false}],
              "vecAchievedHidden":[]
            }}]]
            """);
        var webCalls = 0;
        try
        {
            var provider = new SteamLibraryCacheAchievementProvider(
                () => root,
                _ => "1162499906",
                () => null,
                (_, _) =>
                {
                    Interlocked.Increment(ref webCalls);
                    return Task.FromResult<string?>("must-not-run");
                });

            var snapshot = await provider.GetSnapshotAsync(SteamGame("252950", "Rocket League"));

            Assert.Equal(0, webCalls);
            Assert.Equal(AchievementCoverageStatus.Partial, snapshot.Coverage);
            Assert.Equal("Steam local achievement progress.", snapshot.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamProvider_FallsBackToLocalCacheWhenWebApiFails()
    {
        var root = await WriteSteamZeroCacheAsync("252950");
        var cacheDirectory = Path.Combine(root, "userdata", "1162499906", "config", "librarycache");
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "252950.json"), """
            [["achievements",{"version":1,"data":{
              "nAchieved":0,"nTotal":1,
              "vecHighlight":[],
              "vecUnachieved":[{"strID":"ACH_ONE","bAchieved":false}],
              "vecAchievedHidden":[]
            }}]]
            """);
        try
        {
            var provider = new SteamLibraryCacheAchievementProvider(
                () => root,
                _ => "1162499906",
                () => "0123456789abcdef0123",
                (_, _) => Task.FromResult<string?>(null));

            var snapshot = await provider.GetSnapshotAsync(SteamGame("252950", "Rocket League"));

            Assert.Equal(AchievementCoverageStatus.Partial, snapshot.Coverage);
            Assert.Contains("local", snapshot.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamProvider_ReturnsACompletedLocalBaselineWhenTheWebApiMissesTheCallerBudget()
    {
        var root = await WriteSteamZeroCacheAsync("252950");
        var cacheDirectory = Path.Combine(root, "userdata", "1162499906", "config", "librarycache");
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "252950.json"), """
            [["achievements",{"version":1,"data":{
              "nAchieved":0,"nTotal":1,
              "vecHighlight":[],
              "vecUnachieved":[{"strID":"ACH_ONE","bAchieved":false}],
              "vecAchievedHidden":[]
            }}]]
            """);
        try
        {
            var provider = new SteamLibraryCacheAchievementProvider(
                () => root,
                _ => "1162499906",
                () => "0123456789abcdef0123",
                async (_, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return null;
                });
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
            var started = System.Diagnostics.Stopwatch.StartNew();

            var snapshot = await provider.GetSnapshotAsync(
                SteamGame("252950", "Rocket League"), cts.Token);

            Assert.Equal(AchievementCoverageStatus.Partial, snapshot.Coverage);
            Assert.Equal(1, snapshot.ReportedTotal);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamProvider_RecoversAfterAnEmptyPlayerResponseAndInvalidSchema()
    {
        var root = await WriteSteamZeroCacheAsync("252950");
        var cacheDirectory = Path.Combine(root, "userdata", "1162499906", "config", "librarycache");
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "252950.json"), """
            [["achievements",{"version":1,"data":{
              "nAchieved":0,"nTotal":1,
              "vecHighlight":[],
              "vecUnachieved":[{"strID":"ACH_ONE","bAchieved":false}],
              "vecAchievedHidden":[]
            }}]]
            """);
        var playerCalls = 0;
        var schemaCalls = 0;
        try
        {
            var provider = new SteamLibraryCacheAchievementProvider(
                () => root,
                _ => "1162499906",
                () => "0123456789abcdef0123",
                (uri, _) =>
                {
                    if (uri.AbsolutePath.Contains("GetPlayerAchievements", StringComparison.Ordinal))
                    {
                        if (Interlocked.Increment(ref playerCalls) == 1)
                            return Task.FromResult<string?>(null);
                        return Task.FromResult<string?>("""
                            {"playerstats":{"steamID":"76561199122765634","success":true,"achievements":[
                              {"apiname":"ACH_ONE","achieved":1,"unlocktime":1786323723}
                            ]}}
                            """);
                    }

                    return Task.FromResult<string?>(Interlocked.Increment(ref schemaCalls) == 1
                        ? "{}"
                        : """
                          {"game":{"availableGameStats":{"achievements":[
                            {"name":"ACH_ONE","displayName":"One","hidden":0}
                          ]}}}
                          """);
                });

            var first = await provider.GetSnapshotAsync(SteamGame("252950", "Rocket League"));
            var recovered = await provider.GetSnapshotAsync(SteamGame("252950", "Rocket League"));

            Assert.Equal(AchievementCoverageStatus.Partial, first.Coverage);
            Assert.Equal(AchievementCoverageStatus.Complete, recovered.Coverage);
            Assert.Equal(1, recovered.ReportedUnlocked);
            Assert.True(schemaCalls >= 2);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EpicProvider_PollsSlowerThanAFileReadBecauseLegendaryIsAProcess()
    {
        var provider = new EpicLegendaryAchievementProvider(
            () => null,
            (_, _, _) => throw new InvalidOperationException("must not run"),
            () => "epic:hashed-account");
        Assert.Equal(TimeSpan.FromSeconds(20), provider.SuggestedPollInterval);
    }

    [Fact]
    public void SteamId64_UriEscapesTheKey()
    {
        var uri = SteamWebApiAchievementParser.PlayerAchievementsUri(
            "0123456789abcdef0123", "76561199122765634", "730");
        Assert.StartsWith("https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/", uri);
        Assert.Contains("key=0123456789abcdef0123", uri, StringComparison.Ordinal);
        Assert.Contains("steamid=76561199122765634", uri, StringComparison.Ordinal);
        Assert.Contains("appid=730", uri, StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] relativeSegments)
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
