using ExoLauncher.Services;
using ExoLauncher.Models;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// The catalog layer is keyed by the store's product id. It must stay a
/// details-only lookup and must never guess text the store did not send.
/// </summary>
public sealed class StoreMetadataServiceTests
{
    [Fact]
    public void Key_OnlyMatchesSteamProductIds()
    {
        Assert.Equal("steam:1091500", Key("steam:1091500"));
        Assert.Equal("steam:570", Key("steam:570:variant"));
        Assert.Null(Key("epic:fortnite"));
        Assert.Null(Key("gog:1207658930"));
        Assert.Null(Key("local:c-games-doom"));
        Assert.Null(Key(null));
        Assert.Null(Key(""));
    }

    [Fact]
    public void Parse_ReadsGenreYearAndShortDescription()
    {
        const string json = """
        {"1091500":{"success":true,"data":{
          "short_description":"Cyberpunk 2077 is an open-world adventure.",
          "genres":[{"id":"25","description":"Adventure"},{"id":"3","description":"RPG"}],
          "release_date":{"coming_soon":false,"date":"10 Dec, 2020"}}}}
        """;

        var parsed = Parse(json, "1091500");

        Assert.NotNull(parsed);
        Assert.Equal("Adventure", Genre(parsed!));
        Assert.Equal(2020, Year(parsed!));
        Assert.Equal("Cyberpunk 2077 is an open-world adventure.", Description(parsed!));
    }

    [Fact]
    public void Parse_RefusesFailedOrEmptyPayloads()
    {
        Assert.Null(Parse("""{"1091500":{"success":false}}""", "1091500"));
        Assert.Null(Parse("""{"1091500":{"success":true,"data":{}}}""", "1091500"));
        Assert.Null(Parse("""{"999":{"success":true,"data":{"genres":[]}}}""", "1091500"));
        Assert.Null(Parse("not json", "1091500"));
        Assert.Null(Parse(null, "1091500"));
    }

    [Fact]
    public void Parse_LeavesUnknownFieldsNullRatherThanGuessing()
    {
        const string json = """
        {"440":{"success":true,"data":{"genres":[{"description":"Action"}]}}}
        """;

        var parsed = Parse(json, "440");

        Assert.NotNull(parsed);
        Assert.Equal("Action", Genre(parsed!));
        Assert.Null(Year(parsed!));
        Assert.Null(Description(parsed!));
    }

    [Fact]
    public void DetailsCard_ShowsCatalogTextOnlyWhenTheStoreSentIt()
    {
        var page = ReadRepoFile("ui", "src", "components", "GamePage.tsx");

        // Opened details own store, genre and year. Home cards do not.
        Assert.Contains("host.gameMetadata(selected.id)", page, StringComparison.Ordinal);
        Assert.Contains("[storeLabel(selected.store), metadata?.genre, metadata?.year].filter(Boolean)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata?.description", page, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-game-description", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_KeepsCatalogTextOffTheLibraryScan()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");

        Assert.Contains("\"game.metadata\" =>", bridge, StringComparison.Ordinal);
        Assert.Contains("_metadata.Peek(game)", bridge, StringComparison.Ordinal);

        // library.get must never fan out one store request per tile.
        var start = bridge.IndexOf("private async Task<object> LibraryGetAsync", StringComparison.Ordinal);
        var end = bridge.IndexOf("private async Task<object> GameGetAsync", start, StringComparison.Ordinal);
        Assert.True(end > start);
        Assert.DoesNotContain("_metadata", bridge[start..end], StringComparison.Ordinal);
    }

    [Fact]
    public void CrossStoreMetadata_ReusesAnEstablishedSteamCatalogIdentityWithoutChangingOwnership()
    {
        var epic = new GameEntry
        {
            Id = "epic:rocket-league",
            Title = "Rocket League",
            Store = StoreKind.Epic,
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/252950/library_600x900.jpg",
            LaunchTarget = "rocket-league",
        };

        Assert.Equal("252950", CoverArtService.MetadataSteamAppId(epic));
        Assert.True(epic.Owned);
        Assert.Equal(StoreKind.Epic, epic.Store);
    }

    [Theory]
    [InlineData("VALORANT", StoreKind.Riot, "Tactical shooter", 2020)]
    [InlineData("League of Legends", StoreKind.Riot, "MOBA", 2009)]
    [InlineData("Deadlock", StoreKind.Steam, "Action", 2024)]
    public void MissingFirstPartyCatalogFields_HaveExactProductFallbacks(
        string title, StoreKind store, string genre, int year)
    {
        var game = new GameEntry
        {
            Id = title == "Deadlock" ? "steam:1422450" : $"riot:{title}",
            Title = title,
            Store = store,
            LaunchTarget = title == "Deadlock" ? "1422450" : title,
        };

        var metadata = StoreMetadataService.BuiltIn(game);

        Assert.NotNull(metadata);
        Assert.Equal(genre, metadata!.Genre);
        Assert.Equal(year, metadata.Year);
    }

    private static string? Key(string? gameId) => StoreMetadataService.Key(gameId);

    private static StoreMetadataService.StoreMetadata? Parse(string? json, string appId) =>
        StoreMetadataService.Parse(json, appId);

    private static string? Genre(StoreMetadataService.StoreMetadata metadata) => metadata.Genre;
    private static int? Year(StoreMetadataService.StoreMetadata metadata) => metadata.Year;
    private static string? Description(StoreMetadataService.StoreMetadata metadata) => metadata.Description;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
}
