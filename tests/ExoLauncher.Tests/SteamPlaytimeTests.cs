using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamPlaytimeTests
{
    [Fact]
    public void MergeFile_ReadsNestedPlaytimeAndLastPlayed()
    {
        const string vdf = """
            "UserLocalConfigStore"
            {
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "apps"
                            {
                                "730"
                                {
                                    "LastPlayed"        "1785089911"
                                    "Playtime"          "25336"
                                    "cloud"
                                    {
                                        "last_sync_state"    "synchronized"
                                    }
                                }
                                "2001760"
                                {
                                    "LastPlayed"        "1785000000"
                                    "Playtime"          "13"
                                }
                            }
                        }
                    }
                }
            }
            """;

        var map = new Dictionary<string, SteamPlaytime.Entry>(StringComparer.Ordinal);
        SteamPlaytime.MergeFile(map, vdf);

        Assert.True(map.ContainsKey("730"));
        Assert.Equal(25336, map["730"].Minutes);
        Assert.NotNull(map["730"].LastPlayedUtc);
        Assert.Equal(13, map["2001760"].Minutes);
    }

    [Fact]
    public void ParseAppTickets_UsesOnlyTheActiveAccountsTicketSection()
    {
        const string vdf = """
            "UserLocalConfigStore"
            {
                "apptickets"
                {
                    "1620730" "50000000aabbccdd"
                    "1817070" "320000001122aaff"
                    "not-an-app" "ffffffff"
                }
                "Software"
                {
                    "apps"
                    {
                        "252950" { "Playtime" "500" }
                    }
                }
            }
            "1620730" "outside-the-ticket-section"
            """;

        var tickets = SteamPlaytime.ParseAppTickets(vdf);

        Assert.Equal(2, tickets.Count);
        Assert.Contains("1620730", tickets);
        Assert.Contains("1817070", tickets);
        Assert.DoesNotContain("252950", tickets);
    }

    [Fact]
    public void MergeFile_KeepsHigherPlaytimeAcrossUsers()
    {
        var map = new Dictionary<string, SteamPlaytime.Entry>(StringComparer.Ordinal);
        SteamPlaytime.MergeFile(map, """
            "apps" { "10" { "Playtime" "100" "LastPlayed" "100" } }
            """);
        SteamPlaytime.MergeFile(map, """
            "apps" { "10" { "Playtime" "50" "LastPlayed" "200" } }
            """);

        Assert.Equal(100, map["10"].Minutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), map["10"].LastPlayedUtc);
    }

    [Fact]
    public void ActiveAccountLoader_DoesNotMergeOtherSteamUsers()
    {
        var root = CreateSteamRoot(
            ("111", "10", 100),
            ("222", "10", 900));
        try
        {
            SteamPlaytime.Invalidate();
            var active = SteamPlaytime.LoadAccount(root, "111");

            Assert.NotNull(active);
            Assert.Equal(100, active!.Entries["10"].Minutes);
            Assert.DoesNotContain("111", active.AccountKey, StringComparison.Ordinal);
            Assert.DoesNotContain("222", active.AccountKey, StringComparison.Ordinal);
            Assert.Equal(32, active.AccountKey.Length);
        }
        finally
        {
            SteamPlaytime.Invalidate();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ActiveAccountResolution_FailsClosedWhenSeveralUsersHaveNoActiveMatch()
    {
        var root = CreateSteamRoot(
            ("111", "10", 100),
            ("222", "10", 900));
        try
        {
            Assert.Null(SteamPlaytime.ResolveActiveAccountId(root, registryAccountId: null));
            Assert.Null(SteamPlaytime.ResolveActiveAccountId(root, registryAccountId: "333"));
            Assert.Equal("222", SteamPlaytime.ResolveActiveAccountId(root, registryAccountId: "222"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SteamCoverage_ContainsOnlyHashedAccountProvenance()
    {
        var game = new GameEntry
        {
            Id = "steam:252950",
            Title = "Rocket League",
            Store = StoreKind.Steam,
            LaunchTarget = "252950",
        };

        var coverage = PlaytimeService.NativeCoverageKey(
            game,
            "7ce84b31d013d2e7b45cc6593db1f73f");

        Assert.Equal("steam:7ce84b31d013d2e7b45cc6593db1f73f:252950", coverage);
        Assert.DoesNotContain("765611", coverage, StringComparison.Ordinal);
    }

    private static string CreateSteamRoot(params (string Account, string App, int Minutes)[] rows)
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-playtime-" + Guid.NewGuid().ToString("N"));
        foreach (var row in rows)
        {
            var directory = Path.Combine(root, "userdata", row.Account, "config");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "localconfig.vdf"),
                $"\"apps\" {{ \"{row.App}\" {{ \"Playtime\" \"{row.Minutes}\" }} }}");
        }
        return root;
    }
}
