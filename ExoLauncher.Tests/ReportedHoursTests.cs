using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Microsoft.Data.Sqlite;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways reported hours can come out wrong:
/// - Galaxy's GameTimes column (minutesInGame) is not read, so GOG and every connected platform show nothing;
/// - a Galaxy release key is split wrong, or a platform maps to the wrong store, or gog_ rows leak into it;
/// - a Galaxy figure lands on another store's game with the same title, or on a different game;
/// - a lower source shows when a higher one has hours (Galaxy over Steam's own, tracker over Galaxy);
/// - a store's own figure gets a "From" note, or a borrowed one does not;
/// - a tracker.gg stat unit is misread, or Riot playlists are summed alongside an overview;
/// - a private tracker.gg profile reads as zero hours instead of private;
/// - Xbox MinutesPlayed is misparsed (wrapped or bare answers, missing stats read as zero, apps counted);
/// - Xbox hours land on a game from another store.
/// </summary>
public sealed class ReportedHoursTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-hours-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    private static GameEntry Game(string id, string title, StoreKind store, string? launchTarget = null) =>
        new() { Id = id, Title = title, Store = store, LaunchTarget = launchTarget };

    // Galaxy 2.0's own table shapes (galaxy-2.0.db), with rows for several platforms.
    private string GalaxyDatabase()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "galaxy-2.0.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE GameTimes (userId INTEGER NOT NULL, releaseKey TEXT NOT NULL, minutesInGame INTEGER NOT NULL, PRIMARY KEY (userId, releaseKey));
            CREATE TABLE LastPlayedDates (gameReleaseKey TEXT NOT NULL, lastPlayedDate TEXT, userId INTEGER NOT NULL);
            CREATE TABLE GamePieceTypes (id INTEGER PRIMARY KEY, type TEXT);
            CREATE TABLE GamePieces (releaseKey TEXT NOT NULL, gamePieceTypeId INTEGER NOT NULL, userId INTEGER NOT NULL, value TEXT);
            INSERT INTO GamePieceTypes VALUES (1, 'title'), (2, 'originalTitle'), (3, 'meta');
            INSERT INTO GameTimes VALUES
              (1, 'gog_1207658924', 600),
              (1, 'steam_570', 1200),
              (1, 'xboxone_9NBLGGH4QWLZ', 300),
              (2, 'xboxone_9NBLGGH4QWLZ', 450),
              (1, 'battlenet_5272175', 900),
              (1, 'uplay_635', 0),
              (1, 'psn_CUSA00001', 77);
            INSERT INTO LastPlayedDates VALUES ('battlenet_5272175', '2026-09-30 18:00:00', 1), ('gog_1207658924', '2026-08-01 12:00:00', 1);
            INSERT INTO GamePieces VALUES
              ('xboxone_9NBLGGH4QWLZ', 1, 1, '{"title":"Forza Horizon 5"}'),
              ('battlenet_5272175', 2, 1, '{"title":"Overwatch® 2"}'),
              ('battlenet_5272175', 3, 1, '{"title":"not a title"}'),
              ('steam_570', 1, 1, '{"title":"Dota 2"}');
            """;
        command.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void GalaxyReleaseKeysSplitIntoPlatformAndId()
    {
        Assert.True(GalaxyPlatformTimes.TryParseReleaseKey("steam_570", out var platform, out var id));
        Assert.Equal(("steam", "570"), (platform, id));
        Assert.True(GalaxyPlatformTimes.TryParseReleaseKey("xboxone_9NBLGGH4QWLZ", out platform, out id));
        Assert.Equal(("xboxone", "9NBLGGH4QWLZ"), (platform, id));
        Assert.True(GalaxyPlatformTimes.TryParseReleaseKey("epic_Fortnite_Live", out platform, out id));
        Assert.Equal(("epic", "Fortnite_Live"), (platform, id));
        Assert.False(GalaxyPlatformTimes.TryParseReleaseKey("noprefix", out _, out _));
        Assert.False(GalaxyPlatformTimes.TryParseReleaseKey("steam_", out _, out _));
        Assert.False(GalaxyPlatformTimes.TryParseReleaseKey("_570", out _, out _));
    }

    [Theory]
    [InlineData("steam", StoreKind.Steam)]
    [InlineData("xboxone", StoreKind.Xbox)]
    [InlineData("battlenet", StoreKind.BattleNet)]
    [InlineData("uplay", StoreKind.Ubisoft)]
    [InlineData("origin", StoreKind.Ea)]
    [InlineData("amazon", StoreKind.Amazon)]
    [InlineData("rockstar", StoreKind.Rockstar)]
    public void GalaxyPlatformsMapToTheirStore(string platform, StoreKind store) =>
        Assert.Equal(store, GalaxyPlatformTimes.StoreFor(platform));

    [Fact]
    public void GalaxyPlatformsWithoutAnExoStoreMapToNothing()
    {
        Assert.Null(GalaxyPlatformTimes.StoreFor("psn"));
        Assert.Null(GalaxyPlatformTimes.StoreFor("gog"));
        Assert.Null(GalaxyPlatformTimes.StoreFor("generic"));
    }

    [Fact]
    public void GalaxyDatabaseIsReadForOtherPlatformsOnly()
    {
        var rows = GalaxyPlatformTimes.Read(GalaxyDatabase());
        Assert.DoesNotContain(rows, row => row.Platform == "gog");
        Assert.DoesNotContain(rows, row => row.Platform == "uplay"); // zero minutes is not a reading
        var xbox = Assert.Single(rows, row => row.Platform == "xboxone");
        Assert.Equal(450, xbox.Minutes); // the Galaxy user who played it, not the sum of users
        Assert.Equal(["Forza Horizon 5"], xbox.Titles);
        var battlenet = Assert.Single(rows, row => row.Platform == "battlenet");
        Assert.Equal(["Overwatch® 2"], battlenet.Titles);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero), battlenet.LastPlayedUtc);
    }

    [Fact]
    public void GogOwnHoursReadGalaxysMinutesInGameColumn()
    {
        var gog = GogGalaxySqlite.ReadGameTimes(GalaxyDatabase());
        var row = Assert.Single(gog);
        Assert.Equal(("1207658924", 600), (row.ProductId, row.Minutes));
        // Galaxy 2.0 keeps last played in LastPlayedDates, not GameTimes.
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), row.LastPlayedUtc);
    }

    [Fact]
    public void GalaxyHoursMatchByIdThenExactTitleWithinTheStore()
    {
        var rows = GalaxyPlatformTimes.Read(GalaxyDatabase());
        Assert.Equal(1200, GalaxyPlatformTimes.Find(Game("steam:570", "Dota 2 (renamed)", StoreKind.Steam, "570"), rows)?.Minutes);
        Assert.Equal(900, GalaxyPlatformTimes.Find(Game("battlenet:prometheus", "Overwatch 2", StoreKind.BattleNet, "prometheus"), rows)?.Minutes);
        Assert.Equal(450, GalaxyPlatformTimes.Find(Game("xbox:forza", "Forza Horizon 5", StoreKind.Xbox), rows)?.Minutes);
    }

    [Fact]
    public void GalaxyHoursNeverCrossStoresOrTitles()
    {
        var rows = GalaxyPlatformTimes.Read(GalaxyDatabase());
        // Same title, other store: Steam's copy of Forza must not take Xbox's hours.
        Assert.Null(GalaxyPlatformTimes.Find(Game("steam:1551360", "Forza Horizon 5", StoreKind.Steam, "1551360"), rows));
        Assert.Null(GalaxyPlatformTimes.Find(Game("xbox:forza4", "Forza Horizon 4", StoreKind.Xbox), rows));
        Assert.Null(GalaxyPlatformTimes.Find(Game("ubisoft:635", "Rainbow Six Siege", StoreKind.Ubisoft, "635"), rows));
    }

    [Fact]
    public void StoreFigureBeatsGalaxyBeatsTracker()
    {
        var store = new ReportedPlaytime.Reading(100, ReportedPlaytime.Source.Store);
        var galaxy = new ReportedPlaytime.Reading(900, ReportedPlaytime.Source.Galaxy);
        var tracker = new ReportedPlaytime.Reading(5000, ReportedPlaytime.Source.Tracker);
        Assert.Equal(store, ReportedPlaytime.Pick(store, galaxy, tracker));
        Assert.Equal(galaxy, ReportedPlaytime.Pick(null, galaxy, tracker));
        Assert.Equal(galaxy, ReportedPlaytime.Pick(store with { Minutes = 0 }, galaxy, tracker));
        Assert.Equal(tracker, ReportedPlaytime.Pick(null, null, tracker));
        Assert.Null(ReportedPlaytime.Pick(null, null, null));
    }

    private static string Profile(params object[] segments) =>
        JsonSerializer.Serialize(new { data = new { segments } });

    [Theory]
    [InlineData(7200, "TimeSeconds", 120)]
    [InlineData(7_200_000, "TimeMilliseconds", 120)]
    [InlineData(120, "TimeMinutes", 120)]
    [InlineData(2, "Hours", 120)]
    public void TrackerStatUnitsAreConvertedFromRawValues(double value, string unit, int minutes)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { value, displayValue = "9h", displayType = unit }));
        Assert.Equal(minutes, TrackerGgPlaytime.StatMinutes(document.RootElement));
    }

    [Fact]
    public void TrackerStatWithAnUnknownUnitIsNotGuessed()
    {
        using var document = JsonDocument.Parse("""{ "value": 500, "displayType": "Number" }""");
        Assert.Null(TrackerGgPlaytime.StatMinutes(document.RootElement));
    }

    [Fact]
    public void RiotPlaylistsAreSummedOnlyWithoutAnOverview()
    {
        var playlists = Profile(
            new { type = "playlist", metadata = new { name = "Competitive" }, stats = new { timePlayed = new { value = 3600, displayType = "TimeSeconds" } } },
            new { type = "playlist", metadata = new { name = "Unrated" }, stats = new { timePlayed = new { value = 1800, displayType = "TimeSeconds" } } });
        Assert.Equal(90, TrackerGgPlaytime.ParseBestMinutes(playlists));
    }

    [Fact]
    public void PrivateProfileIsPrivateNotZero()
    {
        const string json = """{ "errors": [ { "code": "CollectorResultStatus::Private", "message": "This profile is private." } ] }""";
        Assert.True(TrackerGgPlaytime.IsPrivateProfile(json));
        Assert.Null(TrackerGgPlaytime.ParseBestMinutes(json));
    }

    [Fact]
    public void XboxAnswersParseWrappedOrBare()
    {
        Assert.Equal("2533274792093122", XboxPlaytime.ParseXuid("""{ "content": { "profileUsers": [ { "id": "2533274792093122" } ] }, "code": 200 }"""));
        Assert.Equal("2533274792093122", XboxPlaytime.ParseXuid("""{ "profileUsers": [ { "id": "2533274792093122" } ] }"""));
        Assert.Null(XboxPlaytime.ParseXuid("""{ "content": "Unauthorized", "code": 401 }"""));
        Assert.Equal(401, XboxPlaytime.ErrorCode("""{ "content": "Unauthorized", "code": 401 }"""));
        Assert.Null(XboxPlaytime.ErrorCode("""{ "content": {}, "code": 200 }"""));
    }

    [Fact]
    public void XboxTitleHistoryKeepsGamesOnly()
    {
        var titles = XboxPlaytime.ParseTitles("""
            { "content": { "xuid": "1", "titles": [
              { "titleId": "1144039928", "name": "Halo: The Master Chief Collection", "type": "Game", "pfn": "Microsoft.Chelan_8wekyb3d8bbwe",
                "titleHistory": { "lastTimePlayed": "2025-11-26T02:34:30.2229199Z" } },
              { "titleId": "1667986082", "name": "Xbox App", "type": "App" },
              { "titleId": "not-a-number", "name": "Broken", "type": "Game" },
              { "titleId": "1144039928", "name": "Duplicate", "type": "Game" }
            ] }, "code": 200 }
            """);
        var title = Assert.Single(titles);
        Assert.Equal(("1144039928", "Microsoft.Chelan_8wekyb3d8bbwe"), (title.TitleId, title.Pfn));
        Assert.Equal(new DateOnly(2025, 11, 26), DateOnly.FromDateTime(title.LastPlayedUtc!.Value.UtcDateTime));
    }

    [Fact]
    public void XboxMinutesPlayedIsReadPerTitleAndMissingIsAbsent()
    {
        var minutes = XboxPlaytime.ParseMinutesPlayed("""
            { "content": { "groups": [], "statlistscollection": [ { "arrangebyfield": "xuid", "stats": [
              { "xuid": "1", "titleid": "1144039928", "name": "MinutesPlayed", "type": "Integer", "value": "10219" },
              { "xuid": "1", "titleid": "219630713", "name": "MinutesPlayed", "type": "Integer", "value": "0" },
              { "xuid": "1", "titleid": "1667986082", "name": "Kills", "type": "Integer", "value": "55" }
            ] } ] }, "code": 200 }
            """);
        Assert.Equal(10219, Assert.Single(minutes).Value);
        Assert.Equal("1144039928", minutes.Keys.Single());
        Assert.Equal(5, XboxPlaytime.ParseMinutesPlayed("""{ "statlistscollection": [ { "stats": [ { "titleid": "7", "name": "minutesplayed", "value": "5" } ] } ] }""")["7"]);
    }

    [Fact]
    public void XboxHoursGoToXboxAndMinecraftEntriesOnly()
    {
        List<XboxPlaytime.TitleHours> titles =
        [
            new("1144039928", "Halo: The Master Chief Collection", "Microsoft.Chelan_8wekyb3d8bbwe", 600, null),
            new("1828326430", "Minecraft for Windows", ReportedPlaytime.MinecraftBedrockPfn, 900, null),
            new("2030093255", "Forza Horizon 5", null, 120, null),
        ];
        Assert.Equal(600, ReportedPlaytime.XboxFor(Game("xbox:mcc", "MCC (renamed)", StoreKind.Xbox), titles, "Microsoft.Chelan")?.Minutes);
        Assert.Equal(120, ReportedPlaytime.XboxFor(Game("xbox:forza", "Forza Horizon 5", StoreKind.Xbox), titles, null)?.Minutes);
        Assert.Equal(900, ReportedPlaytime.XboxFor(Game("minecraft:bedrock", "Minecraft", StoreKind.Minecraft), titles, null)?.Minutes);
        Assert.Null(ReportedPlaytime.XboxFor(Game("minecraft:java", "Minecraft for Windows", StoreKind.Minecraft), titles, null));
        Assert.Null(ReportedPlaytime.XboxFor(Game("steam:1551360", "Forza Horizon 5", StoreKind.Steam, "1551360"), titles, null));
    }
}
