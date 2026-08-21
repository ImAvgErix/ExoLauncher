using System.Diagnostics;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class GogGalaxyFriendsTests
{
    [Fact]
    public void ParseCreateTable_ReadsColumnNamesAndIntegerPrimaryKey()
    {
        var columns = GogGalaxySqlite.ParseCreateTable("""
            CREATE TABLE Users (
              id INTEGER PRIMARY KEY,
              username TEXT,
              userId TEXT,
              CONSTRAINT users_pk PRIMARY KEY (id)
            )
            """);

        Assert.Equal(["id", "username", "userId"], columns.Select(column => column.Name).ToArray());
        Assert.True(columns[0].IntegerPrimaryKey);
        Assert.False(columns[1].IntegerPrimaryKey);
    }

    [Fact]
    public void MapPresence_InGameWins_UnknownStaysUnknown()
    {
        Assert.Equal(("ingame", null, true), GogGalaxyFriends.MapPresence("offline", "Hades", "1145360"));
        Assert.Equal(("online", null, true), GogGalaxyFriends.MapPresence("online", null, null));
        Assert.Equal(("unknown", null, false), GogGalaxyFriends.MapPresence(null, null, null));
        Assert.Equal(("offline", null, true), GogGalaxyFriends.MapPresence("0", null, null));
    }

    [Fact]
    public void TrySteamId64_AcceptsAPublicSteamId_RejectsAName()
    {
        Assert.Equal("76561197960361544", GogGalaxyFriends.TrySteamId64("steam_76561197960361544", null));
        Assert.Equal("76561197960361544", GogGalaxyFriends.TrySteamId64("76561197960361544", null));
        Assert.Null(GogGalaxyFriends.TrySteamId64("Ketchup", null));
    }

    [Fact]
    public void SamePerson_JoinsOnSteamId_NeverOnName()
    {
        var galaxy = new GogGalaxyFriends.Friend(
            "galaxy:steam:1", "Ketchup", "steam", "unknown", null, null, null, null,
            "76561197960361544", null, false);

        Assert.True(GogGalaxyFriends.SamePerson(galaxy, "76561197960361544", epicId: null));
        Assert.False(GogGalaxyFriends.SamePerson(galaxy, "76561198000000000", epicId: null));
        Assert.False(GogGalaxyFriends.SamePerson(
            galaxy with { SteamId64 = null }, "76561197960361544", epicId: null));
    }

    [Fact]
    public void MapRow_WithoutGalaxyRunning_IsLastKnown_NeverOnline()
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["username"] = "Ketchup",
            ["userId"] = "76561197960361544",
            ["platform"] = "steam",
            ["presence_state"] = "online",
            ["game_title"] = "Hades",
            ["game_id"] = "1145360",
        };

        var parked = GogGalaxyFriends.MapRow(row, galaxyRunning: false, writtenUtc: "2026-08-18T00:00:00Z");
        Assert.NotNull(parked);
        Assert.Equal("unknown", parked!.Status);
        Assert.False(parked.Fresh);
        Assert.Null(parked.PlayingTitle);
        Assert.Equal("Last in Hades", parked.StatusText);
        Assert.Equal("76561197960361544", parked.SteamId64);

        var live = GogGalaxyFriends.MapRow(row, galaxyRunning: true, writtenUtc: null);
        Assert.NotNull(live);
        Assert.Equal("ingame", live!.Status);
        Assert.True(live.Fresh);
        Assert.Equal("Hades", live.PlayingTitle);
        Assert.Equal("steam:1145360", live.PlayingId);
    }

    [Fact]
    public void ReadCopy_MissingFriendTables_IsEmptyNotAnError()
    {
        var path = WriteDatabase("""
            import sqlite3, sys
            db = sqlite3.connect(sys.argv[1])
            db.execute("CREATE TABLE GameTimes (releaseKey TEXT, minutes INTEGER)")
            db.execute("INSERT INTO GameTimes VALUES ('gog_1', 12)")
            db.commit()
            db.close()
            """);

        var snapshot = GogGalaxyFriends.ReadCopy(path, writtenUtc: null);
        Assert.Empty(snapshot.Friends);
        Assert.False(snapshot.Live);
        Assert.Equal(GogGalaxyFriends.EmptyNote, snapshot.Note);
    }

    [Fact]
    public void ReadCopy_ReadsUsersFriendsAndUserPresence()
    {
        var path = WriteDatabase("""
            import sqlite3, sys
            db = sqlite3.connect(sys.argv[1])
            db.execute("CREATE TABLE Users (id INTEGER PRIMARY KEY, username TEXT, userId TEXT)")
            db.execute("CREATE TABLE Friends (userId INTEGER, friendId INTEGER)")
            db.execute("CREATE TABLE UserPresence (userId INTEGER, presence_state TEXT, game_id TEXT, game_title TEXT)")
            db.execute("INSERT INTO Users(id, username, userId) VALUES (1, 'Self', '1')")
            db.execute("INSERT INTO Users(id, username, userId) VALUES (2, 'Ketchup', '76561197960361544')")
            db.execute("INSERT INTO Friends VALUES (1, 2)")
            db.execute("INSERT INTO UserPresence VALUES (2, 'online', '1145360', 'Hades')")
            db.commit()
            db.close()
            """);

        var schema = GogGalaxySqlite.ReadSchema(path);
        Assert.Contains(schema, table => table.Name.Equals("Friends", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(schema, table => table.Name.Equals("UserPresence", StringComparison.OrdinalIgnoreCase));
        var users = GogGalaxySqlite.ReadTable(path, "Users");
        Assert.Equal(2, users.Count);
        Assert.Contains(users, row => row.TryGetValue("username", out var name) && name == "Ketchup");
        var links = GogGalaxySqlite.ReadTable(path, "Friends");
        Assert.Single(links);
        var tables = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Users"] = users,
            ["Friends"] = links,
            ["UserPresence"] = GogGalaxySqlite.ReadTable(path, "UserPresence"),
        };
        var mapped = GogGalaxyFriends.MapFriends(tables, galaxyRunning: false, writtenUtc: "2026-08-18T00:00:00Z");
        Assert.Contains(mapped, row => row.Name == "Ketchup");

        var snapshot = GogGalaxyFriends.ReadCopy(path, DateTimeOffset.Parse("2026-08-18T00:00:00Z"));
        var friend = Assert.Single(snapshot.Friends, row => row.Name == "Ketchup");
        Assert.Equal("Ketchup", friend.Name);
        Assert.Equal("steam", friend.Store);
        Assert.Equal("76561197960361544", friend.SteamId64);
        if (snapshot.Live)
        {
            Assert.True(friend.Fresh);
            Assert.Equal("ingame", friend.Status);
        }
        else
        {
            Assert.False(friend.Fresh);
            Assert.Equal("unknown", friend.Status);
            Assert.Equal(GogGalaxyFriends.LastKnownNote, snapshot.Note);
        }
    }

    [Fact]
    public void Load_WithNoGalaxyInstall_IsNone()
    {
        if (GogGalaxyFriends.DatabasePresent()) return;
        var snapshot = GogGalaxyFriends.Load();
        Assert.Empty(snapshot.Friends);
        Assert.Null(snapshot.Note);
        Assert.False(snapshot.Live);
    }

    private static string WriteDatabase(string python)
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-galaxy-friends-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "galaxy-2.0.db");
        var script = Path.Combine(root, "make.py");
        File.WriteAllText(script, python);
        var start = new ProcessStartInfo
        {
            FileName = "python",
            ArgumentList = { script, db },
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start);
        Assert.NotNull(process);
        process!.WaitForExit(15_000);
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        Assert.True(File.Exists(db));
        return db;
    }
}
