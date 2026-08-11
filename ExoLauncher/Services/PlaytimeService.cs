using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed record LocalPlaytimeObservation(
    string GameKey,
    string Source,
    string CoverageKey,
    long TotalSeconds,
    DateTimeOffset ObservedAt,
    string? CatalogProvider = null,
    string? SourceGameId = null,
    string? DisplayName = null,
    string? ArtworkUrl = null);

/// <summary>
/// Playtime for every library title: native lifetime readings (Steam, Epic,
/// and GOG) plus Exo sessions as an offline fallback. A frozen imported lifetime
/// may also carry a persisted session baseline so only play recorded after that
/// snapshot is added; live native readings are never blindly double-counted.
/// </summary>
public static class PlaytimeService
{
    private sealed record LocalPlaytimeRow(
        GameEntry Game,
        string GameKey,
        int? StoreMinutes,
        DateTimeOffset? StoreLastPlayed,
        int ExoMinutes,
        DateTimeOffset? ExoLastPlayed);

    private sealed record ImportedLifetimeSnapshot(
        string AccountKey,
        DateTimeOffset ObservedAt,
        IReadOnlyDictionary<string, int> MinutesByGameId,
        IReadOnlyDictionary<string, int> ExoSessionBaselineMinutesByGameId);

    private sealed record NeutralImportedLifetimeDocument(
        string AccountKey,
        DateTimeOffset ObservedAt,
        IReadOnlyDictionary<string, int> Minutes,
        IReadOnlyDictionary<string, int>? ExoSessionBaselineMinutes);

    private static readonly object FileGate = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ActiveSessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> SessionCheckpoints = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? _checkpointTimer;
    private static Dictionary<string, int>? _exoMinutes;
    private static Dictionary<string, string>? _exoLastPlayed;
    private static IReadOnlyList<LocalPlaytimeObservation> _lastObservations = [];
    private static readonly JsonSerializerOptions ImportedLifetimeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static string StorePath => Path.Combine(PathHelper.AppDataDir, "playtime.json");
    private static string NeutralImportedLifetimePath =>
        Path.Combine(PathHelper.AppDataDir, "exo-imported-lifetime.json");
    private static string LegacyImportedLifetimePath =>
        Path.Combine(PathHelper.AppDataDir, "tracker-gg-playtime.json");

    /// <summary>Returns the raw cumulative readings most recently observed on
    /// this PC. Exo-session coverage is scoped to the supplied stable device id
    /// so the server can sum distinct PCs but de-duplicate repeated syncs.</summary>
    public static IReadOnlyList<LocalPlaytimeObservation> SnapshotObservations(string deviceId)
    {
        var safeDevice = SlugComponent(deviceId);
        lock (FileGate)
        {
            return _lastObservations
                .Select(value => value.Source == "exo_session"
                    ? value with { CoverageKey = $"device:{safeDevice}" }
                    : value)
                .ToList();
        }
    }

    public static void BeginSession(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        lock (FileGate)
        {
            EnsureExoLoaded_NoLock();
            var now = DateTimeOffset.UtcNow;
            ActiveSessions[gameId] = now;
            SessionCheckpoints[gameId] = now;
            _checkpointTimer ??= new Timer(
                _ => CheckpointActiveSessions(),
                null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1));
            SaveExo_NoLock();
        }
    }

    public static void EndSession(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        lock (FileGate)
        {
            if (!ActiveSessions.TryRemove(gameId, out var start)) return;
            SessionCheckpoints.TryRemove(gameId, out _);
            EnsureExoLoaded_NoLock();
            CreditElapsed_NoLock(gameId, start, DateTimeOffset.UtcNow);
            SaveExo_NoLock();
        }
    }

    /// <summary>Drop an open session without crediting (helper / protocol PIDs that exit immediately).</summary>
    public static void CancelSession(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        lock (FileGate)
        {
            if (!ActiveSessions.TryRemove(gameId, out _)) return;
            SessionCheckpoints.TryRemove(gameId, out _);
            EnsureExoLoaded_NoLock();
            SaveExo_NoLock();
        }
    }

    /// <summary>Credits active sessions through shutdown and stops the checkpoint timer.</summary>
    public static void FlushActiveSessions()
    {
        foreach (var gameId in ActiveSessions.Keys.ToArray())
            EndSession(gameId);
        lock (FileGate)
        {
            _checkpointTimer?.Dispose();
            _checkpointTimer = null;
        }
    }

    private static void CheckpointActiveSessions()
    {
        lock (FileGate)
        {
            if (ActiveSessions.IsEmpty) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var gameId in ActiveSessions.Keys)
                SessionCheckpoints[gameId] = now;
            EnsureExoLoaded_NoLock();
            SaveExo_NoLock();
        }
    }

    public static void AddExoMinutes(string gameId, int minutes)
    {
        if (minutes <= 0 || string.IsNullOrWhiteSpace(gameId)) return;
        lock (FileGate)
        {
            EnsureExoLoaded_NoLock();
            _exoMinutes![gameId] = (_exoMinutes.TryGetValue(gameId, out var cur) ? cur : 0) + minutes;
            _exoLastPlayed![gameId] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            SaveExo_NoLock();
        }
    }

    /// <summary>Attach the best known playtime / last-played to each game.</summary>
    public static IReadOnlyList<GameEntry> Enrich(IReadOnlyList<GameEntry> games)
    {
        if (games.Count == 0) return games;

        SteamPlaytime.Invalidate(); // pick up Steam’s VDF after play
        var steamRoot = ResolveSteamRoot();
        var steamSnapshot = steamRoot is null ? null : SteamPlaytime.LoadActiveAccount(steamRoot);
        IReadOnlyDictionary<string, SteamPlaytime.Entry> steam =
            steamSnapshot is null
                ? new Dictionary<string, SteamPlaytime.Entry>()
                : steamSnapshot.Entries;
        var steamAccountKey = steamSnapshot?.AccountKey;
        var gog = GogPlaytime.LoadAll();
        var riotLast = RiotLastPlayed.LoadAll();
        var importedLifetime = LoadImportedLifetime();
        Dictionary<string, int> exo;
        Dictionary<string, string> exoLast;
        lock (FileGate)
        {
            EnsureExoLoaded_NoLock();
            exo = new Dictionary<string, int>(_exoMinutes!, StringComparer.OrdinalIgnoreCase);
            exoLast = new Dictionary<string, string>(_exoLastPlayed!, StringComparer.OrdinalIgnoreCase);
        }

        var now = DateTimeOffset.UtcNow;
        var rows = games.Select(g =>
        {
            int? storeMins = g.PlaytimeMinutes;
            DateTimeOffset? storeLast = g.LastPlayedUtc;

            if (g.Store == StoreKind.Steam &&
                !string.IsNullOrWhiteSpace(g.LaunchTarget) &&
                steam.TryGetValue(g.LaunchTarget, out var se))
            {
                if (se.Minutes > 0) storeMins = se.Minutes;
                if (se.LastPlayedUtc is not null) storeLast = se.LastPlayedUtc;
            }
            else if (g.Store == StoreKind.Gog)
            {
                var gogId = ExtractGogId(g);
                if (gogId is not null && gog.TryGetValue(gogId, out var gm) && gm > 0)
                    storeMins = gm;
            }
            else if (g.Store == StoreKind.Riot)
            {
                var product = ExtractRiotProduct(g);
                if (product is not null && riotLast.TryGetValue(product, out var rl))
                    storeLast = rl;
            }

            exo.TryGetValue(g.Id, out var exoMins);
            DateTimeOffset? exoPlayed = null;
            if (exoLast.TryGetValue(g.Id, out var raw) &&
                DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                exoPlayed = parsed;

            return new LocalPlaytimeRow(g, GameKey(g), storeMins, storeLast, exoMins, exoPlayed);
        }).ToList();

        var native = rows
            .Where(row => row.StoreMinutes is > 0 &&
                          IsNativeLifetimeStore(row.Game.Store) &&
                          (row.Game.Store != StoreKind.Steam || steamAccountKey is not null))
            .Select(row => new LocalPlaytimeObservation(
                row.GameKey,
                row.Game.Store.ToString().ToLowerInvariant(),
                NativeCoverageKey(row.Game, steamAccountKey),
                checked((long)row.StoreMinutes!.Value * 60L),
                row.StoreLastPlayed ?? now,
                row.Game.Store.ToString().ToLowerInvariant(),
                CatalogSourceGameId(row.Game),
                row.Game.Title,
                row.Game.CoverUrl))
            .ToList();

        // Preserve the user's last successful pre-Exo lifetime import. Riot's
        // local client exposes timestamps but no lifetime counter, so dropping
        // this already-observed value would make valid hours disappear. The
        // one-way account key lets Exo deduplicate the same import across PCs
        // without uploading the account identifier itself.
        if (importedLifetime is not null)
        {
            foreach (var row in rows.Where(value => value.Game.Store == StoreKind.Riot))
            {
                if (!importedLifetime.MinutesByGameId.TryGetValue(row.Game.Id, out var minutes) || minutes <= 0)
                    continue;
                native.Add(new LocalPlaytimeObservation(
                    row.GameKey,
                    "imported_lifetime",
                    $"imported:{importedLifetime.AccountKey}:{SlugComponent(row.Game.Id)}",
                    checked((long)minutes * 60L),
                    importedLifetime.ObservedAt,
                    "riot",
                    CatalogSourceGameId(row.Game),
                    row.Game.Title,
                    row.Game.CoverUrl));
            }
        }

        // Steam still exposes the pre-free-to-play Rocket League lifetime even
        // when that title is no longer returned as an installed Steam card.
        var rocketLeagueRow = rows.FirstOrDefault(row => IsRocketLeague(row.Game));
        if (steamAccountKey is not null &&
            rocketLeagueRow is not null &&
            steam.TryGetValue("252950", out var rocketLeagueSteam) &&
            rocketLeagueSteam.Minutes > 0)
        {
            native.Add(new LocalPlaytimeObservation(
                "rocket-league",
                "steam",
                $"steam:{steamAccountKey}:252950",
                checked((long)rocketLeagueSteam.Minutes * 60L),
                rocketLeagueSteam.LastPlayedUtc ?? now,
                "steam",
                "252950",
                rocketLeagueRow.Game.Title,
                rocketLeagueRow.Game.CoverUrl));
        }

        // A repeated reading for one native account/product is overlapping,
        // while different stores are genuinely distinct purchases/histories.
        native = native
            .GroupBy(value => (value.GameKey, value.CoverageKey))
            .Select(group => group
                .OrderByDescending(value => value.TotalSeconds)
                .ThenByDescending(value => value.ObservedAt)
                .First())
            .ToList();
        var nativeTotals = native
            .GroupBy(value => value.GameKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(0L, (total, value) => total + value.TotalSeconds),
                StringComparer.Ordinal);

        var exoFallback = rows
            .Where(row => row.ExoMinutes > 0)
            .GroupBy(row => row.GameKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var representative = group
                    .OrderBy(row => row.Game.Store == StoreKind.Local)
                    .ThenBy(row => row.Game.Id, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new LocalPlaytimeObservation(
                    group.Key,
                    "exo_session",
                    "device",
                    group.GroupBy(row => row.Game.Id, StringComparer.OrdinalIgnoreCase)
                        .Sum(entries => (long)entries.Max(row => row.ExoMinutes) * 60L),
                    group.Select(row => row.ExoLastPlayed ?? now).Max(),
                    representative.Game.Store.ToString().ToLowerInvariant(),
                    CatalogSourceGameId(representative.Game),
                    representative.Game.Title,
                    representative.Game.CoverUrl);
            })
            // Exo sessions are an offline fallback, not an independent store
            // lifetime. Uploading them beside a real lifetime source gives the
            // server two distinct coverage keys and inflates the aggregate.
            .Where(value => !nativeTotals.ContainsKey(value.GameKey))
            .ToList();

        // A one-time import is not a live native counter. Preserve its value,
        // but add only the cumulative Exo minutes recorded after the import's
        // persisted baseline. Live Steam/Epic/GOG readings still use Exo solely
        // as a fallback and therefore never enter this path.
        var importedSessionDeltas = importedLifetime is null
            ? []
            : rows
                .Where(row =>
                    row.Game.Store == StoreKind.Riot &&
                    row.ExoMinutes > 0 &&
                    importedLifetime.MinutesByGameId.ContainsKey(row.Game.Id))
                .GroupBy(row => row.GameKey, StringComparer.Ordinal)
                .Select(group =>
                {
                    var representative = group
                        .OrderBy(row => row.Game.Id, StringComparer.OrdinalIgnoreCase)
                        .First();
                    var deltaMinutes = group
                        .GroupBy(row => row.Game.Id, StringComparer.OrdinalIgnoreCase)
                        .Sum(entries =>
                        {
                            var current = entries.Max(row => row.ExoMinutes);
                            var baseline = importedLifetime.ExoSessionBaselineMinutesByGameId
                                .GetValueOrDefault(entries.Key, current);
                            return Math.Max(0, current - baseline);
                        });
                    return new LocalPlaytimeObservation(
                        group.Key,
                        "exo_session",
                        "device",
                        checked((long)deltaMinutes * 60L),
                        group.Select(row => row.ExoLastPlayed ?? now).Max(),
                        "riot",
                        CatalogSourceGameId(representative.Game),
                        representative.Game.Title,
                        representative.Game.CoverUrl);
                })
                .Where(value => value.TotalSeconds > 0 && nativeTotals.ContainsKey(value.GameKey))
                .ToList();

        var exoObservations = exoFallback.Concat(importedSessionDeltas).ToList();
        var exoTotals = exoObservations.ToDictionary(
            value => value.GameKey,
            value => value.TotalSeconds,
            StringComparer.Ordinal);

        lock (FileGate)
        {
            _lastObservations = native.Concat(exoObservations).ToList();
        }

        var lastPlayedByGame = rows
            .GroupBy(row => row.GameKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(row => new[] { row.StoreLastPlayed, row.ExoLastPlayed })
                    .Where(value => value is not null)
                    .Select(value => value!.Value)
                    .DefaultIfEmpty()
                    .Max(),
                StringComparer.Ordinal);

        return rows.Select(row =>
        {
            nativeTotals.TryGetValue(row.GameKey, out var nativeSeconds);
            exoTotals.TryGetValue(row.GameKey, out var exoSeconds);
            var localSeconds = checked(nativeSeconds + exoSeconds);
            var localMinutes = localSeconds > 0
                ? (int)Math.Min(localSeconds / 60L, int.MaxValue)
                : 0;
            int? best = localMinutes > 0
                ? localMinutes
                : null;
            var last = lastPlayedByGame.GetValueOrDefault(row.GameKey);
            DateTimeOffset? effectiveLast = last == default ? null : last;

            if (best == row.Game.PlaytimeMinutes && effectiveLast == row.Game.LastPlayedUtc)
                return row.Game;

            return Clone(row.Game, best, effectiveLast);
        }).ToList();
    }

    internal static string GameKey(GameEntry game)
    {
        if (IsRocketLeague(game)) return "rocket-league";
        var product = ExtractRiotProduct(game);
        if (product is "valorant" or "valorant_live") return "valorant";
        if (product is "league_of_legends" or "lion") return "league-of-legends";
        return SlugComponent(game.Title);
    }

    private static ImportedLifetimeSnapshot? LoadImportedLifetime()
    {
        lock (FileGate)
        {
            // Once a neutral Exo import exists, it is authoritative. Retrying
            // the legacy deletion here also handles a transient file lock from
            // the first successful migration.
            if (TryLoadNeutralImportedLifetime(out var neutral))
            {
                TryDeleteLegacyImportedLifetime();
                return neutral;
            }

            var legacy = TryLoadLegacyImportedLifetime();
            if (legacy is null) return null;

            // Promote a same-directory temp file atomically, then parse the
            // promoted document before removing the only raw legacy copy.
            _ = TrySaveNeutralImportedLifetime(legacy);
            if (TryLoadNeutralImportedLifetime(out var confirmed))
            {
                TryDeleteLegacyImportedLifetime();
                return confirmed;
            }

            // A failed save must never discard valid lifetime totals or the
            // source needed to retry migration on the next load.
            return legacy;
        }
    }

    private static ImportedLifetimeSnapshot? TryLoadLegacyImportedLifetime()
    {
        try
        {
            if (!File.Exists(LegacyImportedLifetimePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(LegacyImportedLifetimePath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("minutes", out var minutesNode) ||
                minutesNode.ValueKind != JsonValueKind.Object)
                return null;

            var minutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in minutesNode.EnumerateObject())
            {
                if (value.Value.ValueKind == JsonValueKind.Number &&
                    value.Value.TryGetInt32(out var count) && count > 0)
                    minutes[value.Name] = count;
            }
            if (minutes.Count == 0) return null;

            var account = root.TryGetProperty("accountId", out var accountNode)
                ? accountNode.GetString()
                : null;
            account = string.IsNullOrWhiteSpace(account) ? "local-import" : account.Trim();
            var accountHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(account.ToLowerInvariant())))
                .ToLowerInvariant()[..20];
            var observedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(LegacyImportedLifetimePath));
            return new ImportedLifetimeSnapshot(
                accountHash,
                observedAt,
                minutes,
                CaptureImportedSessionBaseline(minutes.Keys, observedAt, allowPostObservedSessions: true));
        }
        catch (Exception ex)
        {
            AppLog.Debug("Legacy lifetime import parse failed: " + ex.Message);
            return null;
        }
    }

    private static bool TryLoadNeutralImportedLifetime(out ImportedLifetimeSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            if (!File.Exists(NeutralImportedLifetimePath)) return false;
            var document = JsonSerializer.Deserialize<NeutralImportedLifetimeDocument>(
                File.ReadAllText(NeutralImportedLifetimePath),
                ImportedLifetimeJsonOptions);
            if (document is null ||
                !IsHashedImportedAccountKey(document.AccountKey) ||
                document.ObservedAt == default ||
                document.Minutes is null)
                return false;

            var positiveMinutes = document.Minutes
                .Where(value => !string.IsNullOrWhiteSpace(value.Key) && value.Value > 0)
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
            if (positiveMinutes.Count == 0) return false;

            var baseline = document.ExoSessionBaselineMinutes is null
                // Compatibility with a short-lived neutral document version:
                // treat all current sessions as overlapping rather than risk a
                // surprise increase. New migrations persist explicit zeros.
                ? CaptureImportedSessionBaseline(
                    positiveMinutes.Keys,
                    document.ObservedAt,
                    allowPostObservedSessions: false)
                : positiveMinutes.Keys.ToDictionary(
                    gameId => gameId,
                    gameId => Math.Max(
                        0,
                        document.ExoSessionBaselineMinutes.GetValueOrDefault(
                            gameId,
                            CurrentExoMinutes_NoLock(gameId))),
                    StringComparer.OrdinalIgnoreCase);

            snapshot = new ImportedLifetimeSnapshot(
                document.AccountKey.ToLowerInvariant(),
                document.ObservedAt,
                positiveMinutes,
                baseline);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Neutral lifetime import parse failed: " + ex.Message);
            return false;
        }
    }

    private static bool TrySaveNeutralImportedLifetime(ImportedLifetimeSnapshot snapshot)
    {
        var temporaryPath = NeutralImportedLifetimePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(NeutralImportedLifetimePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var document = new NeutralImportedLifetimeDocument(
                snapshot.AccountKey.ToLowerInvariant(),
                snapshot.ObservedAt,
                snapshot.MinutesByGameId
                    .Where(value => !string.IsNullOrWhiteSpace(value.Key) && value.Value > 0)
                    .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase),
                snapshot.MinutesByGameId.Keys.ToDictionary(
                    gameId => gameId,
                    gameId => Math.Max(
                        0,
                        snapshot.ExoSessionBaselineMinutesByGameId.GetValueOrDefault(gameId)),
                    StringComparer.OrdinalIgnoreCase));

            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, ImportedLifetimeJsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(NeutralImportedLifetimePath))
            {
                File.Replace(
                    temporaryPath,
                    NeutralImportedLifetimePath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, NeutralImportedLifetimePath);
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Neutral lifetime import save failed: " + ex.Message);
            return false;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch { /* best effort: only the exact unique temp is eligible */ }
        }
    }

    private static void TryDeleteLegacyImportedLifetime()
    {
        try { File.Delete(LegacyImportedLifetimePath); }
        catch (Exception ex) { AppLog.Debug("Legacy lifetime import cleanup failed: " + ex.Message); }
    }

    private static IReadOnlyDictionary<string, int> CaptureImportedSessionBaseline(
        IEnumerable<string> gameIds,
        DateTimeOffset observedAt,
        bool allowPostObservedSessions)
    {
        EnsureExoLoaded_NoLock();
        return gameIds.ToDictionary(
            gameId => gameId,
            gameId =>
            {
                var current = CurrentExoMinutes_NoLock(gameId);
                if (!allowPostObservedSessions || current <= 0) return current;
                if (!_exoLastPlayed!.TryGetValue(gameId, out var raw) ||
                    !DateTimeOffset.TryParse(
                        raw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var lastPlayed))
                    return current;
                return lastPlayed > observedAt ? 0 : current;
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static int CurrentExoMinutes_NoLock(string gameId)
    {
        EnsureExoLoaded_NoLock();
        return _exoMinutes!.GetValueOrDefault(gameId);
    }

    private static bool IsHashedImportedAccountKey(string? value) =>
        value is { Length: 20 } &&
        value.AsSpan().IndexOfAnyExcept("0123456789abcdefABCDEF".AsSpan()) < 0;

    private static bool IsNativeLifetimeStore(StoreKind store) =>
        store is StoreKind.Steam or StoreKind.Epic or StoreKind.Gog;

    internal static string NativeCoverageKey(GameEntry game, string? steamAccountKey = null)
    {
        var source = game.Store.ToString().ToLowerInvariant();
        var identity = !string.IsNullOrWhiteSpace(game.LaunchTarget)
            ? game.LaunchTarget!
            : game.Id.Contains(':', StringComparison.Ordinal)
                ? game.Id[(game.Id.IndexOf(':') + 1)..]
                : game.Title;
        return game.Store == StoreKind.Steam
            ? $"steam:{SlugComponent(steamAccountKey ?? "unknown-account")}:{SlugComponent(identity)}"
            : $"{source}:{SlugComponent(identity)}";
    }

    private static string CatalogSourceGameId(GameEntry game)
    {
        var value = !string.IsNullOrWhiteSpace(game.LaunchTarget)
            ? game.LaunchTarget!.Trim()
            : game.Id.Contains(':', StringComparison.Ordinal)
                ? game.Id[(game.Id.IndexOf(':') + 1)..].Trim()
                : game.Title.Trim();
        if (value.Length is > 0 and <= 128 &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.Skip(1).All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or ':' or '-'))
            return value;
        return SlugComponent(value);
    }

    private static string SlugComponent(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(Math.Min(normalized.Length, 96));
        var pendingSeparator = false;
        foreach (var ch in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) ==
                System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            if (ch <= 127 && char.IsLetterOrDigit(ch))
            {
                if (pendingSeparator && builder.Length > 0) builder.Append('-');
                builder.Append(char.ToLowerInvariant(ch));
                pendingSeparator = false;
            }
            else if (builder.Length > 0)
            {
                pendingSeparator = true;
            }
            if (builder.Length >= 96) break;
        }
        if (builder.Length > 0) return builder.ToString().TrimEnd('-');
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();
        return "game-" + hash[..16];
    }

    internal static int? CombineRocketLeagueStoreMinutes(
        IReadOnlyList<GameEntry> games,
        IReadOnlyDictionary<string, SteamPlaytime.Entry> steam)
    {
        var epic = games
            .Where(game => game.Store == StoreKind.Epic && IsRocketLeague(game))
            .Select(game => game.PlaytimeMinutes.GetValueOrDefault())
            .DefaultIfEmpty()
            .Max();

        var steamMinutes = steam.TryGetValue("252950", out var entry)
            ? entry.Minutes
            : games
                .Where(game => game.Store == StoreKind.Steam && IsRocketLeague(game))
                .Select(game => game.PlaytimeMinutes.GetValueOrDefault())
                .DefaultIfEmpty()
                .Max();

        var total = (long)Math.Max(0, epic) + Math.Max(0, steamMinutes);
        return total > 0 ? (int)Math.Min(total, int.MaxValue) : null;
    }

    internal static bool IsRocketLeague(GameEntry game)
    {
        if (string.Equals(game.LaunchTarget, "Sugar", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(game.LaunchTarget, "252950", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(game.Id, "epic:Sugar", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(game.Id, "steam:252950", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(game.Title.Trim(), "Rocket League", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(game.Title.Trim(), "Rocket League®", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractGogId(GameEntry g)
    {
        if (g.Id.StartsWith("gog:", StringComparison.OrdinalIgnoreCase))
            return g.Id["gog:".Length..];
        return null;
    }

    private static string? ExtractRiotProduct(GameEntry g)
    {
        if (!string.IsNullOrWhiteSpace(g.LaunchTarget))
            return g.LaunchTarget.Trim().ToLowerInvariant();
        if (g.Id.StartsWith("riot:", StringComparison.OrdinalIgnoreCase))
            return g.Id["riot:".Length..].Trim().ToLowerInvariant();
        return null;
    }

    private static string? ResolveSteamRoot()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                return path.Replace('/', Path.DirectorySeparatorChar);
        }
        catch { /* */ }
        return new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        }.FirstOrDefault(Directory.Exists);
    }

    private static void EnsureExoLoaded_NoLock()
    {
        if (_exoMinutes is not null && _exoLastPlayed is not null) return;
        _exoMinutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _exoLastPlayed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(StorePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(StorePath));
            if (doc.RootElement.TryGetProperty("minutes", out var mins) &&
                mins.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in mins.EnumerateObject())
                {
                    if (p.Value.TryGetInt32(out var n) && n > 0)
                        _exoMinutes[p.Name] = n;
                }
            }
            if (doc.RootElement.TryGetProperty("lastPlayed", out var last) &&
                last.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in last.EnumerateObject())
                {
                    var s = p.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        _exoLastPlayed[p.Name] = s;
                }
            }
            var recovered = false;
            if (doc.RootElement.TryGetProperty("activeSessions", out var active) &&
                active.ValueKind == JsonValueKind.Object)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var session in active.EnumerateObject())
                {
                    if (session.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!session.Value.TryGetProperty("startUtc", out var startValue) ||
                        !session.Value.TryGetProperty("checkpointUtc", out var checkpointValue) ||
                        !DateTimeOffset.TryParse(startValue.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var start) ||
                        !DateTimeOffset.TryParse(checkpointValue.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var checkpoint))
                        continue;
                    // A checkpoint is the last time Exo actually observed the
                    // session. Never credit the unknown period after a crash.
                    if (checkpoint < start || checkpoint > now.AddMinutes(5) || checkpoint - start > TimeSpan.FromDays(7))
                        continue;
                    CreditElapsed_NoLock(session.Name, start, checkpoint);
                    recovered = true;
                }
            }
            if (recovered)
                SaveExo_NoLock();
        }
        catch (Exception ex)
        {
            AppLog.Debug("playtime.json load failed: " + ex.Message);
        }
    }

    private static void SaveExo_NoLock()
    {
        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var payload = new
            {
                minutes = _exoMinutes,
                lastPlayed = _exoLastPlayed,
                activeSessions = ActiveSessions.ToDictionary(
                    pair => pair.Key,
                    pair => new
                    {
                        startUtc = pair.Value.ToString("O", CultureInfo.InvariantCulture),
                        checkpointUtc = SessionCheckpoints
                            .GetValueOrDefault(pair.Key, pair.Value)
                            .ToString("O", CultureInfo.InvariantCulture),
                    },
                    StringComparer.OrdinalIgnoreCase),
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Debug("playtime.json save failed: " + ex.Message);
        }
    }

    private static void CreditElapsed_NoLock(
        string gameId,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var elapsed = end - start;
        var minutes = (int)elapsed.TotalMinutes;
        // Short sessions still count as a minute once past 30s.
        if (minutes < 1 && elapsed.TotalSeconds >= 30) minutes = 1;
        if (minutes <= 0) return;
        _exoMinutes![gameId] = (_exoMinutes.TryGetValue(gameId, out var current) ? current : 0) + minutes;
        _exoLastPlayed![gameId] = end.ToString("O", CultureInfo.InvariantCulture);
    }

    private static GameEntry Clone(GameEntry g, int? minutes, DateTimeOffset? last) => new()
    {
        Id = g.Id,
        Title = g.Title,
        Store = g.Store,
        Installed = g.Installed,
        Owned = g.Owned,
        UpdateAvailable = g.UpdateAvailable,
        CanInstall = g.CanInstall,
        Path = g.Path,
        CoverUrl = g.CoverUrl,
        CoverSource = g.CoverSource,
        PlaytimeMinutes = minutes,
        SizeBytes = g.SizeBytes,
        Status = g.Status,
        Deps = g.Deps,
        LaunchNote = g.LaunchNote,
        LaunchTarget = g.LaunchTarget,
        LastPlayedUtc = last,
        IsFavorite = g.IsFavorite,
    };
}

/// <summary>
/// Riot Client only publishes last-session timestamps locally (no lifetime minutes).
/// </summary>
internal static class RiotLastPlayed
{
    public static Dictionary<string, DateTimeOffset> LoadAll()
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in CandidateSettings())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                // Only the last-session-timestamp block (not patch-notes / affinity).
                var blockIdx = text.IndexOf("last-session-timestamp:", StringComparison.OrdinalIgnoreCase);
                if (blockIdx < 0) continue;
                var block = text[blockIdx..];
                // Drop the header line, then cut at the next sibling key (~4-space indent).
                var nl = block.IndexOf('\n');
                if (nl > 0) block = block[(nl + 1)..];
                var cut = System.Text.RegularExpressions.Regex.Match(
                    block, @"(?m)^ {0,4}[a-zA-Z0-9_.-]+:");
                if (cut.Success) block = block[..cut.Index];

                //     valorant.live: 1785745323
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(
                             block,
                             @"(?im)^\s*([a-z0-9_]+)\.(?:live|pbe)\s*:\s*(\d{9,})"))
                {
                    var product = m.Groups[1].Value.Trim().ToLowerInvariant();
                    if (!long.TryParse(m.Groups[2].Value, out var unix) || unix <= 0) continue;
                    DateTimeOffset when;
                    try { when = DateTimeOffset.FromUnixTimeSeconds(unix); }
                    catch { continue; }
                    if (!map.TryGetValue(product, out var cur) || when > cur)
                        map[product] = when;
                    // lion is TFT / League alias used by some installs
                    if (product is "league_of_legends")
                    {
                        if (!map.TryGetValue("lion", out var lion) || when > lion)
                            map["lion"] = when;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Riot last-played parse failed: " + ex.Message);
            }
        }
        return map;
    }

    private static IEnumerable<string> CandidateSettings()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Riot Games", "Riot Client", "Config", "RiotClientSettings.yaml");
        yield return Path.Combine(local, "Riot Games", "VALORANT", "Config", "RiotClientSettings.yaml");
        yield return Path.Combine(local, "Riot Games", "League of Legends", "Config", "RiotClientSettings.yaml");
    }
}

/// <summary>
/// GOG Galaxy playtime from ProductSettings JSON blobs when present.
/// No SQLite dependency — Galaxy also mirrors times into per-product JSON under storage.
/// </summary>
internal static class GogPlaytime
{
    public static Dictionary<string, int> LoadAll()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var root in CandidateRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        // Keep the walk cheap — Galaxy storage can be large.
                        var info = new FileInfo(file);
                        if (info.Length is < 32 or > 2_000_000) continue;
                        var text = File.ReadAllText(file);
                        if (!text.Contains("timeSpentInGame", StringComparison.OrdinalIgnoreCase) &&
                            !text.Contains("playtime", StringComparison.OrdinalIgnoreCase))
                            continue;
                        TryParseFile(map, text, file);
                    }
                    catch { /* skip */ }
                }
            }
            catch { /* skip root */ }
        }
        return map;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var prog = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Path.Combine(prog, "GOG.com", "Galaxy", "storage");
        yield return Path.Combine(local, "GOG.com", "Galaxy", "storage");
        yield return Path.Combine(local, "GOG.com", "Galaxy", "webcache");
    }

    private static void TryParseFile(Dictionary<string, int> map, string text, string path)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        // Heroic / Galaxy shapes: { "productId": "123", "playtime": 3600 } (seconds)
        // or nested timeSpentInGame in seconds.
        var id = GuessProductId(root, path);
        var seconds = ReadSeconds(root);
        if (id is null || seconds is null or <= 0) return;
        var mins = (int)Math.Max(1, seconds.Value / 60);
        if (!map.TryGetValue(id, out var cur) || mins > cur)
            map[id] = mins;
    }

    private static string? GuessProductId(JsonElement root, string path)
    {
        foreach (var key in new[] { "productId", "product_id", "gameId", "game_id", "id" })
        {
            if (root.TryGetProperty(key, out var el))
            {
                var s = el.ValueKind == JsonValueKind.Number
                    ? el.GetRawText()
                    : el.GetString();
                if (!string.IsNullOrWhiteSpace(s) && s.All(char.IsDigit))
                    return s;
            }
        }
        var name = Path.GetFileNameWithoutExtension(path);
        return name.All(char.IsDigit) ? name : null;
    }

    private static long? ReadSeconds(JsonElement root)
    {
        foreach (var key in new[] { "timeSpentInGame", "playtime", "playTime", "totalPlayTime" })
        {
            if (!root.TryGetProperty(key, out var el)) continue;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n) && n > 0)
                return n;
            if (el.ValueKind == JsonValueKind.String &&
                long.TryParse(el.GetString(), out n) && n > 0)
                return n;
        }
        return null;
    }
}
