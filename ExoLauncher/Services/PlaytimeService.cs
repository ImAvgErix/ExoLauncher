using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

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

    public static void BeginSession(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        lock (FileGate)
        {
            EnsureExoLoaded_NoLock();
            var now = DateTimeOffset.UtcNow;
            ActiveSessions[gameId] = now;
            SessionCheckpoints[gameId] = now;
            // Last played is the instant play starts. Waiting for EndSession left
            // a just-launched card showing an older reading — its own store's
            // previous session, or a sibling store's, whichever was newer.
            _exoLastPlayed![gameId] = now.ToString("O", CultureInfo.InvariantCulture);
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

        var steamRoot = ResolveSteamRoot();
        var steamSnapshot = steamRoot is null ? null : SteamPlaytime.LoadActiveAccount(steamRoot);
        IReadOnlyDictionary<string, SteamPlaytime.Entry> steam =
            steamSnapshot is null
                ? new Dictionary<string, SteamPlaytime.Entry>()
                : steamSnapshot.Entries;
        var gog = GogPlaytime.LoadAll();
        var gogLast = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        // One Galaxy database read for both readings. GameTimes carries the last
        // session too, and dropping it used to hide a GOG copy that was the most
        // recent one played on a card shared with another store.
        foreach (var row in GogGalaxySqlite.LoadAll())
        {
            if (row.Minutes > 0 &&
                (!gog.TryGetValue(row.ProductId, out var currentMinutes) || row.Minutes > currentMinutes))
                gog[row.ProductId] = row.Minutes;
            if (row.LastPlayedUtc is { } last &&
                (!gogLast.TryGetValue(row.ProductId, out var currentLast) || last > currentLast))
                gogLast[row.ProductId] = last;
        }
        var riotLast = RiotLastPlayed.LoadAll();
        var epicLast = EpicEglLastPlayed.LoadAll();
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
                if (gogId is not null && gogLast.TryGetValue(gogId, out var gl) &&
                    (storeLast is null || gl > storeLast))
                    storeLast = gl;
            }
            else if (g.Store == StoreKind.Riot)
            {
                foreach (var product in RiotProductKeys(g))
                {
                    if (riotLast.TryGetValue(product, out var rl) &&
                        (storeLast is null || rl > storeLast))
                        storeLast = rl;
                }
            }
            else if (g.Store == StoreKind.Epic)
            {
                foreach (var key in EpicPlaytime.ArtifactKeys(g))
                {
                    if (epicLast.TryGetValue(key, out var el) &&
                        (storeLast is null || el > storeLast))
                        storeLast = el;
                }
            }

            // Epic's launcher writes local wall-clock with a Z suffix, so east
            // of UTC last-played can arrive a few hours in the future. Drop only
            // impossible stamps; clamp a small skew to now so the UI is not blank.
            if (storeLast > now.AddHours(36)) storeLast = null;
            else if (storeLast > now) storeLast = now;

            exo.TryGetValue(g.Id, out var exoMins);
            DateTimeOffset? exoPlayed = null;
            if (exoLast.TryGetValue(g.Id, out var raw) &&
                DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                exoPlayed = parsed;

            return new LocalPlaytimeRow(g, storeMins, storeLast, exoMins, exoPlayed);
        }).ToList();

        return rows.Select(row =>
        {
            // Every row stays store-local: one store's counter is never copied
            // onto another store's entry. Combining the same game across stores
            // is the grouped card's job (LibraryService.GroupVariants), which
            // reads these store-local values and never feeds its total back in.
            var nativeMinutes = row.StoreMinutes.GetValueOrDefault();
            var importedMinutes = row.Game.Store == StoreKind.Riot && importedLifetime is not null
                ? importedLifetime.MinutesByGameId.GetValueOrDefault(row.Game.Id)
                : 0;
            nativeMinutes = Math.Max(nativeMinutes, importedMinutes);
            var hasNativeLifetime = nativeMinutes > 0 &&
                (IsNativeLifetimeStore(row.Game.Store) || importedMinutes > 0);

            var exoMinutes = row.ExoMinutes;
            if (hasNativeLifetime)
            {
                // Only a migrated Riot import has an explicit session baseline
                // that can safely be added. Live vendor counters already include
                // play recorded in that vendor and must not be inflated.
                exoMinutes = importedMinutes > 0 && importedLifetime is not null
                    ? Math.Max(0, exoMinutes - importedLifetime.ExoSessionBaselineMinutesByGameId
                        .GetValueOrDefault(row.Game.Id, exoMinutes))
                    : 0;
            }
            var localSeconds = checked((long)Math.Max(0, nativeMinutes + exoMinutes) * 60L);
            var localMinutes = localSeconds > 0
                ? (int)Math.Min(localSeconds / 60L, int.MaxValue)
                : 0;
            int? best = localMinutes > 0
                ? localMinutes
                : null;
            var effectiveLast = new[] { row.StoreLastPlayed, row.ExoLastPlayed }
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .DefaultIfEmpty()
                .Max();
            DateTimeOffset? effectiveLastPlayed = effectiveLast == default ? null : effectiveLast;

            if (best == row.Game.PlaytimeMinutes && effectiveLastPlayed == row.Game.LastPlayedUtc)
                return row.Game;

            return Clone(row.Game, best, effectiveLastPlayed);
        }).ToList();
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

    private static IEnumerable<string> RiotProductKeys(GameEntry g)
    {
        var product = ExtractRiotProduct(g);
        if (string.IsNullOrWhiteSpace(product)) yield break;
        yield return product;
        if (product is "league_of_legends")
        {
            yield return "leagueoflegends";
            yield return "lol";
        }
    }

    private static string? ExtractEpicApp(GameEntry g)
    {
        if (!string.IsNullOrWhiteSpace(g.LaunchTarget))
            return g.LaunchTarget.Trim();
        if (g.Id.StartsWith("epic:", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = g.Id["epic:".Length..].Trim();
            return suffix.Length > 0 ? suffix : null;
        }
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
        EntitlementState = g.EntitlementState,
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
        CanonicalTitleKey = g.CanonicalTitleKey,
        SelectedVariantId = g.SelectedVariantId,
        Variants = g.Variants,
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
                ReadLastSessionBlock(text, map);
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
        yield return Path.Combine(local, "Riot Games", "League of Legends", "Config", "LeagueClientSettings.yaml");
    }

    /// <summary>Public for tests — any friend's Riot YAML shape, quoted or not.</summary>
    internal static void ReadLastSessionBlock(string text, IDictionary<string, DateTimeOffset> map)
    {
        var blockIdx = text.IndexOf("last-session-timestamp:", StringComparison.OrdinalIgnoreCase);
        if (blockIdx < 0) return;

        var lineStart = blockIdx;
        while (lineStart > 0 && text[lineStart - 1] is not '\n' and not '\r')
            lineStart--;
        var keyIndent = LeadingIndent(text.AsSpan(lineStart, blockIdx - lineStart));

        var afterKey = text.IndexOf('\n', blockIdx);
        if (afterKey < 0) return;
        var cursor = afterKey + 1;
        var body = new System.Text.StringBuilder();
        while (cursor < text.Length)
        {
            var end = text.IndexOf('\n', cursor);
            if (end < 0) end = text.Length;
            var line = text[cursor..end];
            var trimmed = line.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(trimmed) && LeadingIndent(trimmed) <= keyIndent)
                break;
            body.Append(trimmed);
            body.Append('\n');
            cursor = end + 1;
        }

        var block = body.ToString();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     block,
                     @"(?im)^\s*([a-z0-9_]+)\.(?:live|pbe)\s*:\s*[""']?(\d{9,})[""']?"))
            AddLastSession(map, m.Groups[1].Value, m.Groups[2].Value);

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     block,
                     @"(?im)^\s*([a-z0-9_]+)\s*:\s*(?:#.*)?\r?\n[ \t]+(?:live|pbe)\s*:\s*[""']?(\d{9,})[""']?"))
            AddLastSession(map, m.Groups[1].Value, m.Groups[2].Value);
    }

    private static void AddLastSession(
        IDictionary<string, DateTimeOffset> map, string productRaw, string digits)
    {
        var product = productRaw.Trim().ToLowerInvariant();
        if (!TryUnixTimestamp(digits, out var when)) return;
        if (!map.TryGetValue(product, out var cur) || when > cur)
            map[product] = when;
    }

    private static bool TryUnixTimestamp(string digits, out DateTimeOffset when)
    {
        when = default;
        if (!long.TryParse(digits, out var unix) || unix <= 0) return false;
        try
        {
            when = unix > 9_999_999_999L
                ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                : DateTimeOffset.FromUnixTimeSeconds(unix);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int LeadingIndent(ReadOnlySpan<char> line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 2;
            else break;
        }
        return n;
    }
}

/// <summary>
/// Last-launched stamp from Epic Games Launcher's GameUserSettings.ini.
/// Lifetime hours still come from Epic's playtime API once a session exists.
/// </summary>
internal static class EpicEglLastPlayed
{
    public static Dictionary<string, DateTimeOffset> LoadAll()
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        // testhost must not inherit this machine's EGL last-played into fixtures.
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (entry is not null &&
            entry.Contains("testhost", StringComparison.OrdinalIgnoreCase))
            return map;
        foreach (var path in EpicPlaytime.EglRememberMeIniCandidates())
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("LastPlayedGame=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var value = trimmed["LastPlayedGame=".Length..].Trim().Trim('"');
                    if (!TryParseLastPlayedGame(value, out var app, out var when)) continue;
                    foreach (var key in EpicLastPlayedKeys(value, app))
                    {
                        if (!map.TryGetValue(key, out var cur) || when > cur)
                            map[key] = when;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Epic EGL last-played parse failed: " + ex.Message);
            }
        }
        return map;
    }

    internal static bool TryParseLastPlayedGame(string? value, out string app, out DateTimeOffset when)
    {
        app = "";
        when = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var comma = value.LastIndexOf(',');
        if (comma <= 0 || comma >= value.Length - 1) return false;
        var left = value[..comma].Trim();
        var stamp = value[(comma + 1)..].Trim();
        var colon = left.LastIndexOf(':');
        app = (colon >= 0 ? left[(colon + 1)..] : left).Trim();
        if (string.IsNullOrWhiteSpace(app) || app.Length > 128) return false;
        return DateTimeOffset.TryParse(
            stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when);
    }

    internal static IEnumerable<string> EpicLastPlayedKeys(string raw, string app)
    {
        yield return app;
        var comma = raw.LastIndexOf(',');
        var left = comma > 0 ? raw[..comma].Trim() : raw.Trim();
        foreach (var part in left.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > 0 && part.Length <= 128) yield return part;
        }
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
