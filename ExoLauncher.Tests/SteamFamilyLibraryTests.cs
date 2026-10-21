using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Steam Family titles can go missing or be shown wrong:
/// - localconfig FamilyGroup: members outside the block count; group 0 (no family) still reads as a
///   family; an unclosed block throws; the account's own id is missing from the set;
/// - family set: titles the account owns itself are labeled family (owned must win); a client-only
///   answer (no Web API list) labels every licensed title family; no family still yields family titles;
/// - manifest LastOwner: the account itself, a stranger, or a malformed id reads as a family lender;
/// - rows: a family DLC, soundtrack or child app becomes a row; a family title already present from
///   its manifest is listed twice; a family row cannot install or is not marked family;
/// - reconcile: a not-installed or downloading title loses its appinfo type, so hygiene drops it.
/// </summary>
public sealed class SteamFamilyLibraryTests
{
    // The shape of localconfig.vdf's FamilyGroup block on a real PC; ids replaced.
    private const string LocalConfig = """
        "UserLocalConfigStore"
        {
          "WebStorage"
          {
            "HintAppsToPreload" ""
          }
          "FamilyGroup"
          {
            "version"   "1"
            "groupid"   "10001"
            "name"    "Family"
            "country"   ""
            "role"    "1"
            "members"
            {
              "0"
              {
                "accountid"   "100"
                "role"    "1"
              }
              "1"
              {
                "accountid"   "200"
                "role"    "1"
              }
              "2"
              {
                "accountid"   "300"
                "role"    "2"
              }
            }
          }
          "friends" { "accountid" "999" }
        }
        """;

    private const string Self = "76561197960265828"; // account 100
    private const string Lender = "76561197960265928"; // account 200

    [Fact]
    public void FamilyMembersAreTheFamilyGroupBlocksOnly()
    {
        Assert.Equal(["100", "200", "300"], SteamPlaytime.ParseFamilyMembers(LocalConfig).Order(StringComparer.Ordinal));
        Assert.Empty(SteamPlaytime.ParseFamilyMembers(LocalConfig.Replace("\"10001\"", "\"0\"", StringComparison.Ordinal)));
        Assert.Empty(SteamPlaytime.ParseFamilyMembers("\"UserLocalConfigStore\" { \"apps\" { } }"));
        Assert.Empty(SteamPlaytime.ParseFamilyMembers("\"FamilyGroup\" { \"groupid\" \"5\" \"members\" {"));
    }

    [Fact]
    public void FamilyTitlesAreLicensedButNotOwnedAndOwnedWins()
    {
        var client = new HashSet<string> { "10", "20", "30" };
        var webApi = new HashSet<string> { "10", "40" };

        Assert.Equal(["20", "30"], SteamAccountLibrary.FamilyAppIds(true, client, webApi).Order(StringComparer.Ordinal));
        Assert.Empty(SteamAccountLibrary.FamilyAppIds(false, client, webApi));
        Assert.Empty(SteamAccountLibrary.FamilyAppIds(true, client, null));
        Assert.Empty(SteamAccountLibrary.FamilyAppIds(true, null, webApi));
    }

    [Theory]
    [InlineData(Lender, true)]
    [InlineData(Self, false)]
    [InlineData("76561197960266128", false)] // account 400, not in the family
    [InlineData("0", false)]
    [InlineData("not-an-id", false)]
    [InlineData(null, false)]
    public void LastOwnerIsALenderOnlyWhenAnotherMember(string? lastOwner, bool expected) =>
        Assert.Equal(expected, SteamAccountLibrary.LastOwnerIsFamilyMember(
            lastOwner, Self, SteamPlaytime.ParseFamilyMembers(LocalConfig)));

    [Fact]
    public void FamilyRowsAreGamesOnlyAndInstallThroughSteam()
    {
        var names = new Dictionary<string, SteamAppInfoNames.Entry>
        {
            ["10"] = new("Owned Game", "game"),
            ["20"] = new("Family Game", "game"),
            ["30"] = new("Family Game Soundtrack", "music"),
            ["40"] = new("Family Game Expansion", "dlc", "20"),
            ["50"] = new("Family Game Bonus", "game", "20"),
            ["60"] = new("Installed Family Game", "game"),
        };
        var licensed = new HashSet<string> { "10", "20", "30", "40", "50", "60" };
        var family = SteamAccountLibrary.FamilyAppIds(true, licensed, new HashSet<string> { "10" });

        var rows = SteamAccountLibrary.UninstalledOwnedGames(
            licensed, new HashSet<string> { "60" }, names, licensed, family);

        Assert.Equal(["steam:10", "steam:20"], rows.Select(row => row.Id).Order(StringComparer.Ordinal));
        var owned = rows.Single(row => row.Id == "steam:10");
        var shared = rows.Single(row => row.Id == "steam:20");
        Assert.False(owned.FamilyShared);
        Assert.True(shared.FamilyShared);
        Assert.True(shared is { Installed: false, Owned: true, CanInstall: true, EntitlementState: EntitlementState.Owned });
        Assert.Equal("20", shared.LaunchTarget);
        Assert.StartsWith("Shared through Steam Family.", shared.LaunchNote, StringComparison.Ordinal);
        Assert.True(CatalogHygiene.KeepLibraryEntry(shared, showTools: true));
    }

    [Fact]
    public void ReconcileKeepsTheTypeSoNotInstalledRowsSurviveHygiene()
    {
        var downloading = new GameEntry
        {
            Id = "steam:20",
            Title = "Family Game",
            Store = StoreKind.Steam,
            Installed = false,
            Owned = true,
            FamilyShared = true,
            EntitlementState = EntitlementState.Owned,
            CanInstall = true,
            LaunchTarget = "20",
            SteamAppType = "game",
            LaunchNote = "Shared through Steam Family. Launches through Steam.",
        };

        var row = Assert.Single(LibraryService.ReconcileSteamOwnership([downloading], new HashSet<string> { "20" }));

        Assert.Equal("game", row.SteamAppType);
        Assert.True(row.FamilyShared);
        Assert.Equal(downloading.LaunchNote, row.LaunchNote);
        Assert.True(CatalogHygiene.KeepLibraryEntry(row, showTools: false));
    }
}
