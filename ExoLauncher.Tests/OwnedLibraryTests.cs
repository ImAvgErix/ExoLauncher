using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways an owned-but-not-installed list can come up short or wrong:
/// - Steam: candidates past a fixed cap are never asked about; one slow batch throws away every answer;
///   a later failed batch is read as "not owned" and prunes titles verified before; a failed first batch
///   is read as an empty library; an unchecked id Exo never verified is promoted to owned;
/// - Steam store types: a DLC (it names its full game) or a delisted app is typed a game; a rate-limited
///   or malformed answer is remembered as "not a game";
/// - dedupe: two apps with one title (Prey 2006, Prey 2017) collapse into one; one id appears twice;
/// - Epic: an edition SKU (mainGameItem, in "games") is dropped as an add-on; a real add-on is kept;
///   titles Epic does not deliver are never asked for, or are offered as a Legendary install;
///   the cache forgets which titles are not installable; the session probe pays for the longer list.
/// </summary>
public sealed class OwnedLibraryTests
{
    private static List<string> Ids(int count) =>
        Enumerable.Range(1, count).Select(i => (i * 10).ToString()).ToList();

    [Fact]
    public async Task EveryCandidateIsAskedInBatches()
    {
        var ids = Ids(25_000);
        var batches = new List<int>();
        var answer = await SteamClientIpc.QueryOwnedInBatchesAsync(ids, batch =>
        {
            batches.Add(batch.Count);
            return Task.FromResult<IReadOnlySet<string>?>(batch.Where(id => id.EndsWith("00", StringComparison.Ordinal)).ToHashSet());
        });

        Assert.NotNull(answer);
        Assert.Equal([10_000, 10_000, 5_000], batches);
        Assert.Empty(answer.Unchecked);
        Assert.Equal(2_500, answer.Owned.Count);
        Assert.Contains("250000", answer.Owned); // past the old 20,000 cap
    }

    [Fact]
    public async Task NoFirstAnswerIsNoAnswer()
    {
        var answer = await SteamClientIpc.QueryOwnedInBatchesAsync(Ids(15_000),
            _ => Task.FromResult<IReadOnlySet<string>?>(null));
        Assert.Null(answer);
    }

    [Fact]
    public async Task LaterFailureKeepsAnsweredBatchesAndStops()
    {
        var ids = Ids(30_000);
        var calls = 0;
        var answer = await SteamClientIpc.QueryOwnedInBatchesAsync(ids, batch =>
            Task.FromResult<IReadOnlySet<string>?>(++calls == 1 ? new HashSet<string> { batch[0] } : null));

        Assert.NotNull(answer);
        Assert.Equal(2, calls);
        Assert.Equal(["10"], answer.Owned);
        Assert.Equal(20_000, answer.Unchecked.Count);
        Assert.Equal(ids[10_000], answer.Unchecked[0]);
    }

    [Fact]
    public void UncheckedIdsStayOwnedOnlyWhenVerifiedBefore()
    {
        var answer = new SteamClientIpc.OwnedAnswer(new HashSet<string> { "10" }, ["30", "40"]);
        var owned = SteamAdapter.WithPreviouslyVerified(answer, () => ["20", "30"]);

        // 20 was checked and is not owned; 30 was not checked and was owned before; 40 was never verified.
        Assert.Equal(["10", "30"], owned.Order(StringComparer.Ordinal));
        Assert.Same(answer.Owned, SteamAdapter.WithPreviouslyVerified(answer with { Unchecked = [] }, () => ["20"]));
    }

    [Theory]
    [InlineData("""{"400":{"success":true,"data":{"type":"game","name":"Portal"}}}""", "game")]
    [InlineData("""{"400":{"success":true,"data":{"type":"dlc","fullgame":{"appid":"10"}}}}""", "")]
    [InlineData("""{"400":{"success":true,"data":{"type":"game","fullgame":{"appid":"10"}}}}""", "")]
    [InlineData("""{"400":{"success":false}}""", "")]
    [InlineData("null", null)]
    [InlineData("""{"401":{"success":true}}""", null)]
    [InlineData("<html>", null)]
    public void StoreTypeIsTheStoresOwnAnswer(string json, string? expected) =>
        Assert.Equal(expected, SteamStoreAppTypes.ParseAppDetailsType(json, "400"));

    private static GameEntry Row(string id, string title, bool installed = false) => new()
    {
        Id = id,
        Title = title,
        Store = id.StartsWith("epic:", StringComparison.Ordinal) ? StoreKind.Epic : StoreKind.Steam,
        Installed = installed,
    };

    [Fact]
    public void SameTitleTwoIdsAreTwoGames()
    {
        var kept = CatalogHygiene.DeduplicateById(
        [
            Row("steam:3970", "Prey"),
            Row("steam:480490", "Prey"),
            Row("epic:Prey", "Prey"),
            Row("steam:480490", "Prey", installed: true),
            Row("steam:1", " "),
        ]);

        Assert.Equal(3, kept.Count);
        Assert.True(kept.Single(row => row.Id == "steam:480490").Installed);
        Assert.Contains(kept, row => row.Id == "steam:3970");
        Assert.Contains(kept, row => row.Id == "epic:Prey");
    }

    private const string EpicList = """
        [
          {"app_name":"Deluxe","app_title":"Control Ultimate Edition","asset_infos":{"Windows":{}},
           "metadata":{"categories":[{"path":"games"},{"path":"applications"}],"mainGameItem":{"id":"base"}}},
          {"app_name":"Pack","app_title":"Control Expansion Pass","asset_infos":{"Windows":{}},
           "metadata":{"categories":[{"path":"addons"},{"path":"applications"}],"mainGameItem":{"id":"base"}}},
          {"app_name":"Skin","app_title":"Control Outfit","asset_infos":{"Windows":{}},
           "metadata":{"categories":[{"path":"applications"}],"mainGameItem":{"id":"base"}}},
          {"app_name":"Ea","app_title":"Battlefield 2042","asset_infos":{},
           "metadata":{"categories":[{"path":"games"}],"customAttributes":{"ThirdPartyManagedApp":{"type":"STRING","value":"Origin"}}}},
          {"app_name":"Plain","app_title":"Alan Wake 2","asset_infos":{"Windows":{}},
           "metadata":{"categories":[{"path":"games"}]}}
        ]
        """;

    [Fact]
    public void EditionSkuIsKeptAndAddOnsAreNot()
    {
        var rows = LegendaryCli.ParseLibraryJson(EpicList, forceInstalled: false).ToDictionary(row => row.AppName);

        Assert.True(CatalogHygiene.KeepEpicRow(rows["Deluxe"].Title, rows["Deluxe"].Categories));
        Assert.False(CatalogHygiene.KeepEpicRow(rows["Pack"].Title, rows["Pack"].Categories));
        Assert.False(CatalogHygiene.KeepEpicRow(rows["Skin"].Title, rows["Skin"].Categories));
        Assert.True(CatalogHygiene.KeepEpicRow(rows["Ea"].Title, rows["Ea"].Categories));
    }

    [Fact]
    public void TitleEpicDoesNotDeliverIsOwnedButNotALegendaryInstall()
    {
        var rows = LegendaryCli.ParseLibraryJson(EpicList, forceInstalled: false).ToDictionary(row => row.AppName);
        Assert.False(rows["Ea"].Installable);
        Assert.Equal("Origin", rows["Ea"].ThirdPartyStore);
        Assert.True(rows["Plain"].Installable);
        Assert.Null(rows["Plain"].ThirdPartyStore);

        var elsewhere = EpicAdapter.MapOwnedRow(rows["Ea"], hasLegendary: true);
        Assert.True(elsewhere.Owned);
        Assert.Equal(["Epic Games Launcher"], elsewhere.Deps);
        Assert.Contains("the EA app", elsewhere.LaunchNote, StringComparison.Ordinal);
        Assert.Equal(["Legendary"], EpicAdapter.MapOwnedRow(rows["Plain"], hasLegendary: true).Deps);

        var cached = EpicAdapter.ParseOwnedCache(
            EpicAdapter.SerializeOwnedCache("scope", rows.Values.ToList()), "scope").ToDictionary(row => row.AppName);
        Assert.False(cached["Ea"].Installable);
        Assert.Equal("Origin", cached["Ea"].ThirdPartyStore);
        Assert.True(cached["Plain"].Installable);
        Assert.Equal(rows["Deluxe"].Categories, cached["Deluxe"].Categories);
    }

    [Fact]
    public void OwnedListAsksForTitlesEpicDoesNotDeliver()
    {
        Assert.Equal(["list", "--json", "--include-non-installable"], LegendaryCli.ListOwnedArgs());
        Assert.Equal(["list", "--json"], LegendaryCli.ListOwnedArgs(includeNonInstallable: false));
    }
}
