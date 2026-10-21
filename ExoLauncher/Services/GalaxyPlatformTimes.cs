using System.Globalization;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Hours GOG Galaxy 2.0 holds for games on other platforms it is connected to
/// (Steam, Xbox, Battle.net, Ubisoft, EA, Amazon, Rockstar…): the GameTimes
/// rows whose release key is not gog_. Each figure is what Galaxy's
/// integration for that platform reported. Read-only, through the same
/// bounded snapshot as GOG's own hours; GOG's rows stay with GogPlaytime.
/// </summary>
internal static class GalaxyPlatformTimes
{
    /// <param name="Platform">Release-key prefix, lower case (steam, xboxone, battlenet…).</param>
    /// <param name="Key">Release key after the prefix, as Galaxy wrote it.</param>
    internal sealed record Row(string Platform, string Key, int Minutes, DateTimeOffset? LastPlayedUtc, IReadOnlyList<string> Titles);

    internal sealed record Match(int Minutes, DateTimeOffset? LastPlayedUtc);

    private static readonly object CacheGate = new();
    private static IReadOnlyList<Row>? _cached;
    private static string? _cacheKey;
    private static long _cacheUntil;

    internal static IReadOnlyList<Row> Load()
    {
        var databases = GogGalaxySqlite.CandidateDatabasePaths().ToArray();
        static string Stamp(string path)
        {
            try { var file = new FileInfo(path); return file.Exists ? $"{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : path + ":missing"; }
            catch { return path + ":unavailable"; }
        }
        var key = string.Join('|', databases.SelectMany(path => new[] { Stamp(path), Stamp(path + "-wal") }));
        var now = Environment.TickCount64;
        lock (CacheGate)
        {
            if (_cached is not null && _cacheKey == key && now < _cacheUntil) return _cached;
            var rows = new List<Row>();
            foreach (var path in databases)
            {
                try
                {
                    if (File.Exists(path)) rows.AddRange(Read(path));
                }
                catch (Exception ex)
                {
                    AppLog.Debug("Galaxy platform hours unreadable: " + ex.GetType().Name);
                }
            }
            _cacheKey = key;
            _cacheUntil = now + 30_000;
            return _cached = rows;
        }
    }

    internal static IReadOnlyList<Row> Read(string databasePath)
    {
        using var snapshot = GogGalaxySqlite.OpenSnapshot(databasePath);
        var minutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in snapshot.ReadTable("GameTimes"))
        {
            var releaseKey = GogGalaxySqlite.Value(row, "releaseKey", "release_key", "gameReleaseKey");
            if (!TryParseReleaseKey(releaseKey, out var platform, out _) || platform == "gog") continue;
            var text = GogGalaxySqlite.Value(row, "minutesInGame", "minutes", "time");
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
                value is <= 0 or >= 100_000_000) continue;
            // One row per Galaxy user signed in on this PC; the largest is the account that played.
            if (!minutes.TryGetValue(releaseKey!, out var current) || value > current) minutes[releaseKey!] = value;
        }
        if (minutes.Count == 0) return Array.Empty<Row>();

        var lastPlayed = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in snapshot.ReadTable("LastPlayedDates"))
        {
            var releaseKey = GogGalaxySqlite.Value(row, "gameReleaseKey", "releaseKey");
            var stamp = GogGalaxySqlite.Value(row, "lastPlayedDate", "lastPlayed");
            if (releaseKey is null || !minutes.ContainsKey(releaseKey) || !TryParseStamp(stamp, out var when)) continue;
            if (!lastPlayed.TryGetValue(releaseKey, out var current) || when > current) lastPlayed[releaseKey] = when;
        }

        var titles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (releaseKey, value) in snapshot.ReadTitlePieces())
        {
            if (!minutes.ContainsKey(releaseKey) || ParseTitle(value) is not { } title) continue;
            if (!titles.TryGetValue(releaseKey, out var list)) titles[releaseKey] = list = [];
            if (!list.Contains(title, StringComparer.Ordinal)) list.Add(title);
        }

        return minutes.Select(pair =>
        {
            TryParseReleaseKey(pair.Key, out var platform, out var id);
            return new Row(platform, id, pair.Value,
                lastPlayed.TryGetValue(pair.Key, out var last) ? last : null,
                titles.TryGetValue(pair.Key, out var list) ? list : Array.Empty<string>());
        }).ToList();
    }

    /// <summary>"steam_570" → ("steam", "570"). The prefix is Galaxy's platform id.</summary>
    internal static bool TryParseReleaseKey(string? raw, out string platform, out string id)
    {
        platform = id = "";
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 256) return false;
        var value = raw.Trim();
        var split = value.IndexOf('_');
        if (split is <= 0 or > 24 || split == value.Length - 1) return false;
        var prefix = value[..split];
        if (!prefix.All(char.IsAsciiLetterOrDigit)) return false;
        platform = prefix.ToLowerInvariant();
        id = value[(split + 1)..];
        return true;
    }

    /// <summary>The Exo store a Galaxy platform id stands for; null for platforms Exo has no store for.</summary>
    internal static StoreKind? StoreFor(string platform) => platform switch
    {
        "steam" => StoreKind.Steam,
        "epic" => StoreKind.Epic,
        "xboxone" or "xbox" => StoreKind.Xbox,
        "battlenet" => StoreKind.BattleNet,
        "uplay" or "ubisoft" => StoreKind.Ubisoft,
        "origin" or "ea" => StoreKind.Ea,
        "amazon" => StoreKind.Amazon,
        "rockstar" => StoreKind.Rockstar,
        "riot" => StoreKind.Riot,
        "itch" => StoreKind.Itch,
        "paradox" => StoreKind.Paradox,
        "wargaming" => StoreKind.Wargaming,
        _ => null,
    };

    /// <summary>
    /// The Galaxy figure for one library entry: a row of the same store whose
    /// key is the entry's id or launch target, else one whose title is the
    /// entry's exact title (letters and digits, case and accents ignored).
    /// Never crosses stores.
    /// </summary>
    internal static Match? Find(GameEntry game, IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0) return null;
        var sameStore = rows.Where(row => StoreFor(row.Platform) == game.Store).ToList();
        if (sameStore.Count == 0) return null;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var colon = game.Id.IndexOf(':');
        if (colon >= 0 && colon < game.Id.Length - 1) ids.Add(game.Id[(colon + 1)..]);
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget) && game.LaunchTarget.Length <= 256 &&
            !game.LaunchTarget.Contains('\\') && !game.LaunchTarget.Contains('/'))
            ids.Add(game.LaunchTarget.Trim());
        var hits = sameStore.Where(row => ids.Contains(row.Key) || ids.Contains(SanitizedId(row.Key))).ToList();
        if (hits.Count == 0)
        {
            var title = CatalogHygiene.NormalizeTitleKey(game.Title);
            if (title.Length == 0) return null;
            hits = sameStore.Where(row => row.Titles.Any(t => CatalogHygiene.NormalizeTitleKey(t) == title)).ToList();
        }
        if (hits.Count == 0) return null;
        var best = hits.MaxBy(row => row.Minutes)!;
        return new Match(best.Minutes, hits.Max(row => row.LastPlayedUtc));
    }

    // Exo's adapters lower-case ids and turn other punctuation into '-'.
    private static string SanitizedId(string value)
    {
        var chars = value.Trim().ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] is not '-' and not '_' and not '.')
                chars[i] = '-';
        }
        return new string(chars).Trim('-');
    }

    internal static string? ParseTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) return null;
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            {
                var text = title.GetString()?.Trim();
                return string.IsNullOrEmpty(text) || text.Length > 300 ? null : text;
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static bool TryParseStamp(string? stamp, out DateTimeOffset when)
    {
        when = default;
        if (string.IsNullOrWhiteSpace(stamp)) return false;
        if (long.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            if (unix is <= 1_000_000_000 or >= 4_102_444_800) return false;
            when = DateTimeOffset.FromUnixTimeSeconds(unix);
            return true;
        }
        if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when)) return false;
        return when.Year is > 2000 and < 2100;
    }
}
