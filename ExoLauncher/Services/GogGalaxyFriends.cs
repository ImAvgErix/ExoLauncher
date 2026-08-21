using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Friends Galaxy already gathered from its integration plugins and wrote
/// into galaxy-2.0.db. Exo only copies that file and reads the copy.
/// Missing tables, missing columns, and a missing Galaxy install are all
/// silence — not errors.
///
/// Presence is last-known unless GalaxyClient is running. A parked database
/// is never dressed up as live.
/// </summary>
internal static class GogGalaxyFriends
{
    internal const string LastKnownNote =
        "GOG Galaxy last-known friends, as fresh as the last time Galaxy ran. Not live.";
    internal const string LiveNote =
        "GOG Galaxy is running. Presence is what its integrations last wrote.";
    internal const string EmptyNote =
        "GOG Galaxy is on this PC but has no friends tables to read.";

    internal static bool DatabasePresent()
    {
        foreach (var path in GogGalaxySqlite.CandidateDatabasePaths())
        {
            try { if (File.Exists(path)) return true; }
            catch { /* ordinary miss */ }
        }

        return false;
    }

    private static readonly string[] FriendTables =
        ["Friends", "Users", "UserPresence", "UsersPresence", "FriendPresence", "Presence"];

    internal sealed record Friend(
        string Id,
        string Name,
        string Store,
        string Status,
        string? StatusText,
        string? PlayingId,
        string? PlayingTitle,
        string? LastSeenUtc,
        string? SteamId64,
        string? EpicId,
        bool Fresh);

    internal sealed record Snapshot(IReadOnlyList<Friend> Friends, bool Live, string? Note, DateTimeOffset? WrittenUtc)
    {
        public static Snapshot None { get; } = new(Array.Empty<Friend>(), false, null, null);
    }

    internal static Snapshot Load()
    {
        foreach (var path in GogGalaxySqlite.CandidateDatabasePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                DateTimeOffset? written = null;
                try { written = File.GetLastWriteTimeUtc(path); }
                catch { /* stamp is optional */ }

                var copy = GogGalaxySqlite.CopyUnlocked(path);
                if (copy is null) continue;
                try
                {
                    var snapshot = ReadCopy(copy, written);
                    if (snapshot.Friends.Count > 0 || snapshot.Note is not null)
                        return snapshot;
                }
                finally
                {
                    TryDelete(copy);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("GOG Galaxy friends read failed: " + ex.GetType().Name);
            }
        }

        return Snapshot.None;
    }

    internal static Snapshot ReadCopy(string databasePath, DateTimeOffset? writtenUtc)
    {
        IReadOnlyList<GogGalaxySqlite.TableInfo> schema;
        try { schema = GogGalaxySqlite.ReadSchema(databasePath); }
        catch { return Snapshot.None; }

        var names = schema
            .Select(table => table.Name)
            .Where(name => FriendTables.Any(known => name.Equals(known, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (names.Count == 0)
            return new Snapshot(Array.Empty<Friend>(), false, EmptyNote, writtenUtc);

        var tables = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            try { tables[name] = GogGalaxySqlite.ReadTable(databasePath, name); }
            catch { /* one missing table is not fatal */ }
        }

        var live = ProcessHelper.IsProcessRunning("GalaxyClient");
        var seen = writtenUtc?.ToString("o");
        var friends = MapFriends(tables, live, seen);
        return new Snapshot(
            friends,
            live && friends.Exists(friend => friend.Fresh),
            friends.Count == 0 ? EmptyNote : live ? LiveNote : LastKnownNote,
            writtenUtc);
    }

    internal static List<Friend> MapFriends(
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> tables,
        bool galaxyRunning,
        string? writtenUtc)
    {
        var users = IndexById(PickTable(tables, "Users"));
        var presence = IndexById(PickTable(tables, "UserPresence", "UsersPresence", "FriendPresence", "Presence"));
        var listed = PickTable(tables, "Friends");

        var rows = new List<IReadOnlyDictionary<string, string?>>();
        if (listed.Count > 0)
        {
            foreach (var link in listed)
            {
                var friendKey = First(link, "friendId", "friend_id", "friendUserId", "userId2");
                var user = Lookup(users, friendKey) ?? link;
                var extra = Lookup(presence, friendKey) ?? Lookup(presence, First(user, "id", "userId", "user_id"));
                rows.Add(MergeRows(user, extra, link));
            }
        }
        else
        {
            foreach (var user in users.Values)
            {
                var key = First(user, "id", "userId", "user_id");
                rows.Add(MergeRows(user, Lookup(presence, key), null));
            }
        }

        var friends = new List<Friend>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var mapped = MapRow(row, galaxyRunning, writtenUtc);
            if (mapped is null || !seen.Add(mapped.Id)) continue;
            friends.Add(mapped);
        }

        return friends;
    }

    internal static Friend? MapRow(
        IReadOnlyDictionary<string, string?> row,
        bool galaxyRunning,
        string? writtenUtc)
    {
        var name = First(row, "username", "user_name", "userName", "name", "personaName", "nickname");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var userId = First(row, "userId", "user_id", "id", "friendId", "friend_id") ?? name;
        var store = MapStore(First(row, "platform", "platformId", "platform_id", "store"));
        var steamId = TrySteamId64(userId, First(row, "steamId", "steamid", "steam_id"));
        if (steamId is not null) store = "steam";
        var epicId = store == "epic" ? userId : First(row, "epicId", "epic_id", "accountId");
        if (!string.IsNullOrWhiteSpace(First(row, "epicId", "epic_id")) && store == "gog")
        {
            store = "epic";
            epicId = First(row, "epicId", "epic_id");
        }

        var gameTitle = First(row, "game_title", "gameTitle", "game_name", "gameName", "title");
        var gameId = First(row, "game_id", "gameId", "gameid");
        var state = First(row, "presence_state", "presenceState", "state", "status", "personaState");
        var stamp = First(row, "updatedAt", "updated_at", "updated", "timestamp", "lastUpdated", "last_seen")
                    ?? writtenUtc;

        string status;
        string? statusText = null;
        var fresh = false;
        if (!galaxyRunning)
        {
            status = "unknown";
            statusText = string.IsNullOrWhiteSpace(gameTitle) ? "Galaxy last known" : "Last in " + gameTitle.Trim();
        }
        else
        {
            (status, statusText, fresh) = MapPresence(state, gameTitle, gameId);
        }

        var playingId = !string.IsNullOrWhiteSpace(gameId) && store == "steam" && gameId.All(char.IsDigit)
            ? "steam:" + gameId
            : null;

        return new Friend(
            "galaxy:" + store + ":" + userId,
            name.Trim(),
            store,
            status,
            statusText,
            fresh ? playingId : null,
            fresh && !string.IsNullOrWhiteSpace(gameTitle) ? gameTitle.Trim() : null,
            stamp,
            steamId,
            string.IsNullOrWhiteSpace(epicId) ? null : epicId,
            fresh);
    }

    internal static (string Status, string? StatusText, bool Fresh) MapPresence(
        string? state, string? gameTitle, string? gameId)
    {
        var inGame = !string.IsNullOrWhiteSpace(gameTitle) ||
                     (!string.IsNullOrWhiteSpace(gameId) && gameId != "0");
        if (inGame) return ("ingame", null, true);

        var key = (state ?? "").Trim().ToLowerInvariant();
        return key switch
        {
            "online" or "1" => ("online", null, true),
            "away" or "idle" or "snooze" or "3" or "4" => ("away", null, true),
            "dnd" or "busy" or "2" => ("dnd", null, true),
            "offline" or "0" => ("offline", null, true),
            "unknown" or "" => ("unknown", null, false),
            _ => ("unknown", null, false),
        };
    }

    internal static string? TrySteamId64(string? userId, string? explicitId)
    {
        foreach (var raw in new[] { explicitId, userId })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var value = raw.Trim();
            if (value.StartsWith("steam_", StringComparison.OrdinalIgnoreCase))
                value = value[6..];
            if (value.Length == 17 && value.All(char.IsDigit) && value.StartsWith("7656", StringComparison.Ordinal))
                return value;
        }

        return null;
    }

    internal static string MapStore(string? raw)
    {
        var key = (raw ?? "").Trim().ToLowerInvariant();
        return key switch
        {
            "steam" or "1" => "steam",
            "epic" or "epicgames" or "3" => "epic",
            "xbox" or "xboxone" or "xbox_one" or "xboxplayanywhere" => "xbox",
            "origin" or "ea" or "eaapp" => "ea",
            "uplay" or "ubisoft" or "ubi" => "ubisoft",
            "battlenet" or "battle.net" or "blizzard" => "battlenet",
            "amazon" or "twitch" or "nile" => "amazon",
            "rockstar" => "rockstar",
            "gog" or "2" or "" => "gog",
            _ => key.Length > 0 && key.All(char.IsLetter) ? key : "gog",
        };
    }

    /// <summary>
    /// Same human only when a store account id matches. A shared display name
    /// is not a match.
    /// </summary>
    internal static bool SamePerson(Friend galaxy, string? steamId64, string? epicId)
    {
        if (galaxy.SteamId64 is not null &&
            steamId64 is not null &&
            galaxy.SteamId64.Equals(steamId64, StringComparison.Ordinal))
            return true;
        return galaxy.EpicId is not null &&
               epicId is not null &&
               galaxy.EpicId.Equals(epicId, StringComparison.Ordinal);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> PickTable(
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> tables,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (tables.TryGetValue(name, out var rows) && rows.Count > 0)
                return rows;
        }

        return Array.Empty<IReadOnlyDictionary<string, string?>>();
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string?>> IndexById(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows)
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var column in new[] { "id", "userId", "user_id", "friendId" })
            {
                if (row.TryGetValue(column, out var key) &&
                    !string.IsNullOrWhiteSpace(key) &&
                    !map.ContainsKey(key))
                    map[key] = row;
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string?>? Lookup(
        Dictionary<string, IReadOnlyDictionary<string, string?>> index, string? key) =>
        !string.IsNullOrWhiteSpace(key) && index.TryGetValue(key, out var row) ? row : null;

    private static IReadOnlyDictionary<string, string?> MergeRows(
        IReadOnlyDictionary<string, string?> primary,
        IReadOnlyDictionary<string, string?>? extra,
        IReadOnlyDictionary<string, string?>? link)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { link, extra, primary })
        {
            if (source is null) continue;
            foreach (var pair in source)
                map[pair.Key] = pair.Value;
        }

        return map;
    }

    private static string? First(IReadOnlyDictionary<string, string?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* temp copy */ }
    }
}
