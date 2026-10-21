using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services.Achievements;

/// <summary>
/// Read-only Steam provider. The local library cache is always consulted for
/// progress bars. When the user has stored a Steam Web API key (the same
/// opt-in key Friends uses), GetPlayerAchievements is the account authority:
/// Valve updates that endpoint within a few seconds of StoreStats. The
/// librarycache JSON is a library-UI snapshot, not a StoreStats bus — on this
/// machine a populated cache sat unchanged for hours while localconfig.vdf
/// kept writing.
/// </summary>
public sealed class SteamLibraryCacheAchievementProvider : IAchievementProvider
{
    private const long MaxPayloadBytes = 8 * 1024 * 1024;
    private const int MaxAchievements = 10_000;
    private static readonly TimeSpan WebApiTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WebApiPromotionBudget = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan[] LocalCacheRetryDelays =
    [
        TimeSpan.FromMilliseconds(120),
        TimeSpan.FromMilliseconds(320),
    ];
    private static readonly HttpClient WebApiClient = CreateWebApiClient();
    private readonly Func<string?> _resolveSteamRoot;
    private readonly Func<string, string?> _resolveAccountId;
    private readonly Func<string?> _resolveWebApiKey;
    private readonly Func<Uri, CancellationToken, Task<string?>> _fetchWebApi;
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyDictionary<string, AchievementDefinition> Schema)> _schemaCache =
        new(StringComparer.Ordinal);
    private static readonly TimeSpan SchemaTtl = TimeSpan.FromHours(6);
    // Session polls reread an unchanged librarycache file every few seconds;
    // reuse the parsed result until Steam rewrites it.
    private readonly Dictionary<string, (long Length, DateTime WriteUtc, string CoverageKey, AchievementSnapshot Snapshot)> _localCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _refreshGate = new();
    private readonly Dictionary<string, SharedRefresh> _refreshes = new(StringComparer.Ordinal);
    private string? _refreshAccount;
    private sealed class SharedRefresh
    {
        public Task<AchievementSnapshot?> Task { get; set; } = null!;
        public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.MaxValue;
    }

    public SteamLibraryCacheAchievementProvider()
        : this(ResolveSteamRoot, ResolveAccountId, ReadInstalledWebApiKey)
    {
    }

    internal SteamLibraryCacheAchievementProvider(
        Func<string?> resolveSteamRoot,
        Func<string, string?> resolveAccountId,
        Func<string?>? resolveWebApiKey = null,
        Func<Uri, CancellationToken, Task<string?>>? fetchWebApi = null)
    {
        _resolveSteamRoot = resolveSteamRoot;
        _resolveAccountId = resolveAccountId;
        // Tests that do not pass a key stay on the local cache path.
        _resolveWebApiKey = resolveWebApiKey ?? (() => null);
        _fetchWebApi = fetchWebApi ?? FetchWebApiAsync;
    }

    public string Id => "steam";
    public StoreKind Store => StoreKind.Steam;
    public AchievementProviderCapabilities Capabilities =>
        AchievementProviderCapabilities.Snapshot |
        AchievementProviderCapabilities.Progress |
        AchievementProviderCapabilities.CompleteCatalog;

    public bool Supports(GameEntry game) =>
        game.Store == StoreKind.Steam &&
        (IsSteamAppId(game.LaunchTarget?.Trim()) ||
         game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase));

    public string? GetCurrentCoverageKey(GameEntry game)
    {
        if (!Supports(game)) return null;
        var root = _resolveSteamRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var accountId = _resolveAccountId(root);
        return string.IsNullOrWhiteSpace(accountId)
            ? null
            : AchievementCoverageKeys.FromAccount("steam", accountId);
    }

    public async Task<AchievementSnapshot> GetSnapshotAsync(
        GameEntry game,
        CancellationToken cancellationToken = default)
    {
        var sourceGameId = SourceGameId(game);
        if (game.Store != StoreKind.Steam || sourceGameId is null)
            return Unavailable(sourceGameId ?? string.Empty, "This entry has no valid Steam app id.");

        var root = _resolveSteamRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Unavailable(sourceGameId, "Steam is not installed.");
        var accountId = _resolveAccountId(root);
        if (string.IsNullOrWhiteSpace(accountId))
            return Unavailable(sourceGameId, "No unambiguous active Steam account was found.");

        var coverageKey = AchievementCoverageKeys.FromAccount("steam", accountId);
        // Active Steam account only. Another userdata folder is a different person.
        var cachePath = FindSteamLibraryCachePath(root, accountId, sourceGameId);
        var observedAtUtc = DateTimeOffset.UtcNow;
        // One DPAPI read per refresh; the shared web task and poll interval reuse it.
        string? key;
        try { key = _resolveWebApiKey(); } catch { key = null; }
        // Steam's one-file summary of every game's counts is the cheap first
        // source. The per-game file still wins when it reads cleanly (it has
        // the rows); with the summary in hand a torn per-game file is not retried.
        var summary = ReadProgressSummary(root, accountId, sourceGameId, coverageKey, observedAtUtc);
        var localTask = summary is null
            ? ReadLocalCacheWithRetryAsync(cachePath, sourceGameId, coverageKey, observedAtUtc, cancellationToken)
            : ReadLocalCacheAsync(cachePath, sourceGameId, coverageKey, observedAtUtc, cancellationToken);
        var webApiTask = SharedWebRefresh(key, root, accountId, sourceGameId, coverageKey, observedAtUtc);
        var local = await localTask.ConfigureAwait(false);
        if (summary is not null && local.Coverage is not (AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete))
            local = summary;
        AchievementSnapshot? webApi;
        if (local.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete)
        {
            webApi = await AwaitWithinAsync(
                webApiTask, WebApiPromotionBudget, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            webApi = await webApiTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsCurrentAccount(root, accountId))
            return Unavailable(sourceGameId,
                "Steam account changed during achievement refresh.");

        if (webApi is not null &&
            webApi.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete)
            return MergeLocalProgress(webApi, local);

        if (!IsUncorroboratedLocalZero(local)) return local;
        // Steam Store appdetails/category ids are not a documented achievement
        // authority. Only a complete, account-matched Web API schema may prove
        // a genuinely empty catalog.
        return cachePath is null
            ? Unavailable(sourceGameId,
                "Steam has not provided current local achievement progress for this game.", coverageKey)
            : Unavailable(sourceGameId,
                "Steam local achievement cache is still catching up.", coverageKey);
    }

    private static async Task<AchievementSnapshot?> AwaitWithinAsync(
        Task<AchievementSnapshot?> task,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        if (task.IsCompletedSuccessfully)
            return await task.ConfigureAwait(false);
        if (task.IsCompleted)
            return null;

        var delay = Task.Delay(budget, cancellationToken);
        var winner = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (winner != task || !task.IsCompletedSuccessfully)
            return null;
        return await task.ConfigureAwait(false);
    }

    private Task<AchievementSnapshot?> SharedWebRefresh(string? key, string root, string accountId, string sourceGameId,
        string coverageKey, DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<AchievementSnapshot?>(null);
        var account = root + "|" + coverageKey + "|" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        lock (_refreshGate)
        {
            if (!string.Equals(_refreshAccount, account, StringComparison.Ordinal))
            {
                _refreshAccount = account;
                _refreshes.Clear();
            }
            if (_refreshes.TryGetValue(sourceGameId, out var current) &&
                (!current.Task.IsCompleted || current.Task.IsCompletedSuccessfully && current.Task.Result is not null &&
                 DateTimeOffset.UtcNow - current.CompletedAt < TimeSpan.FromSeconds(4)))
                return current.Task;
            if (!_refreshes.ContainsKey(sourceGameId) && _refreshes.Count >= 64)
            {
                var victim = _refreshes.Where(pair => pair.Value.Task.IsCompleted).MinBy(pair => pair.Value.CompletedAt);
                if (victim.Key is null) return Task.FromResult<AchievementSnapshot?>(null);
                _refreshes.Remove(victim.Key);
            }
            var entry = new SharedRefresh();
            _refreshes[sourceGameId] = entry;
            entry.Task = Task.Run(async () =>
            {
                AchievementSnapshot? result = null;
                try
                {
                    result = await ReadWebApiAsync(key, accountId, sourceGameId, coverageKey, observedAtUtc, CancellationToken.None).ConfigureAwait(false);
                    // Every caller re-checks the active account after awaiting;
                    // a newer account or key replaces _refreshAccount.
                    lock (_refreshGate) { if (_refreshAccount != account) result = null; }
                }
                catch { /* Bounded provider failure leaves the local baseline usable. */ }
                finally { lock (_refreshGate) entry.CompletedAt = DateTimeOffset.UtcNow; }
                return result;
            });
            return entry.Task;
        }
    }

    /// <summary>
    /// Steam writes librarycache JSON in place. A detail click can therefore
    /// observe the file between truncate and replace; retry only that local
    /// read, with a short bounded backoff, before falling back to honest
    /// unavailable coverage.
    /// </summary>
    private async Task<AchievementSnapshot> ReadLocalCacheWithRetryAsync(
        string? cachePath,
        string sourceGameId,
        string coverageKey,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadLocalCacheAsync(
            cachePath, sourceGameId, coverageKey, observedAtUtc, cancellationToken).ConfigureAwait(false);
        if (cachePath is null || snapshot.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete)
            return snapshot;

        foreach (var delay in LocalCacheRetryDelays)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            snapshot = await ReadLocalCacheAsync(
                cachePath, sourceGameId, coverageKey, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            if (snapshot.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete)
                return snapshot;
        }

        return snapshot;
    }

    private async Task<AchievementSnapshot> ReadLocalCacheAsync(
        string? cachePath,
        string sourceGameId,
        string coverageKey,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            if (cachePath is null)
                return Unavailable(sourceGameId,
                    "Steam has not provided current local achievement progress for this game.", coverageKey);
            var info = new FileInfo(cachePath);
            if (info.Length is <= 0 or > MaxPayloadBytes)
                return Unavailable(sourceGameId,
                    "Steam has not provided current local achievement progress for this game.", coverageKey);
            var length = info.Length;
            var writeUtc = info.LastWriteTimeUtc;
            lock (_localCache)
            {
                if (_localCache.TryGetValue(cachePath, out var cached) && cached.Length == length &&
                    cached.WriteUtc == writeUtc && cached.CoverageKey == coverageKey)
                    return Restamp(cached.Snapshot, observedAtUtc);
            }

            await using var stream = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (json.Length > MaxPayloadBytes)
                return Unavailable(sourceGameId,
                    "Steam has not provided current local achievement progress for this game.", coverageKey);
            var snapshot = ParseSnapshotJson(json, sourceGameId, coverageKey, observedAtUtc);
            // Only a consistent parse is reused; a torn in-place write must be reread.
            if (snapshot.Coverage == AchievementCoverageStatus.Partial)
            {
                lock (_localCache)
                {
                    if (_localCache.Count >= 32 && !_localCache.ContainsKey(cachePath)) _localCache.Clear();
                    _localCache[cachePath] = (length, writeUtc, coverageKey, snapshot);
                }
            }
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable(sourceGameId,
                "Steam has not provided current local achievement progress for this game.", coverageKey);
        }
    }

    private static AchievementSnapshot Restamp(AchievementSnapshot snapshot, DateTimeOffset observedAtUtc) =>
        snapshot with
        {
            ObservedAtUtc = observedAtUtc,
            Entries = snapshot.Entries
                .Select(row => row with { State = row.State with { ObservedAtUtc = observedAtUtc } })
                .ToArray(),
        };

    private async Task<AchievementSnapshot?> ReadWebApiAsync(
        string key,
        string accountId,
        string sourceGameId,
        string coverageKey,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!SteamWebApiAchievementParser.TrySteamId64(accountId, out var steamId64))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WebApiTimeout);
        try
        {
            var playerUri = new Uri(
                SteamWebApiAchievementParser.PlayerAchievementsUri(key, steamId64, sourceGameId),
                UriKind.Absolute);
            var playerJson = await _fetchWebApi(playerUri, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(playerJson)) return null;

            var schema = await ReadSchemaAsync(key, sourceGameId, timeout.Token).ConfigureAwait(false);
            var snapshot = SteamWebApiAchievementParser.ParsePlayerAchievements(
                playerJson, schema, sourceGameId, coverageKey, observedAtUtc,
                expectedSteamId64: steamId64);
            if (snapshot.Coverage == AchievementCoverageStatus.Unavailable &&
                (snapshot.Message?.Contains("schema", StringComparison.OrdinalIgnoreCase) == true ||
                 snapshot.Message?.Contains("did not match", StringComparison.OrdinalIgnoreCase) == true))
            {
                schema = await ReadSchemaAsync(
                    key, sourceGameId, timeout.Token, forceRefresh: true).ConfigureAwait(false);
                snapshot = SteamWebApiAchievementParser.ParsePlayerAchievements(
                    playerJson, schema, sourceGameId, coverageKey, observedAtUtc,
                    expectedSteamId64: steamId64);
            }
            return snapshot.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete
                ? snapshot
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, AchievementDefinition>?> ReadSchemaAsync(
        string key,
        string sourceGameId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        lock (_schemaCache)
        {
            if (forceRefresh)
            {
                _schemaCache.Remove(sourceGameId);
            }
            else if (_schemaCache.TryGetValue(sourceGameId, out var cached) &&
                DateTimeOffset.UtcNow - cached.At < SchemaTtl)
                return cached.Schema;
        }

        try
        {
            var uri = new Uri(SteamWebApiAchievementParser.SchemaUri(key, sourceGameId), UriKind.Absolute);
            var json = await _fetchWebApi(uri, cancellationToken).ConfigureAwait(false);
            if (!SteamWebApiAchievementParser.TryParseCompleteSchema(
                    json, sourceGameId, out var schema))
                return null;
            lock (_schemaCache)
            {
                if (!_schemaCache.ContainsKey(sourceGameId) && _schemaCache.Count >= 256)
                    _schemaCache.Remove(_schemaCache.MinBy(pair => pair.Value.At).Key);
                _schemaCache[sourceGameId] = (DateTimeOffset.UtcNow, schema);
            }
            return schema;
        }
        catch
        {
            return null;
        }
    }

    private static AchievementSnapshot MergeLocalProgress(AchievementSnapshot webApi, AchievementSnapshot local)
    {
        if (local.Coverage is not (AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete) ||
            local.Entries.Count == 0)
            return webApi;

        var progress = new Dictionary<string, AchievementEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in local.Entries)
            progress[row.Definition.ExternalId] = row;

        var merged = new List<AchievementEntry>(webApi.Entries.Count);
        foreach (var row in webApi.Entries)
        {
            if (!progress.TryGetValue(row.Definition.ExternalId, out var localRow) ||
                (localRow.State.ProgressCurrent is null && localRow.State.ProgressTarget is null))
            {
                merged.Add(row);
                continue;
            }

            merged.Add(row with
            {
                State = row.State with
                {
                    ProgressCurrent = row.State.ProgressCurrent ?? localRow.State.ProgressCurrent,
                    ProgressTarget = row.State.ProgressTarget ?? localRow.State.ProgressTarget,
                },
            });
        }

        return webApi with { Entries = merged };
    }

    private static string? ReadInstalledWebApiKey()
    {
        try { return ExoLauncher.Services.SteamWebApiKeyStore.TryRead(); }
        catch { return null; }
    }

    private readonly object _summaryGate = new();
    private (string Path, long Length, DateTime WriteUtc, IReadOnlyDictionary<string, (int Unlocked, int Total)> Map)? _summary;

    /// <summary>
    /// Counts for one game from <c>librarycache/achievement_progress.json</c>
    /// (<c>{nVersion, mapCache:[[appid,{unlocked,total,percentage,all_unlocked,cache_time,vetted}]]}</c>),
    /// re-parsed only when Steam rewrites it. Null when the game is not in it
    /// or has no achievements: a 0 / 0 summary proves nothing.
    /// </summary>
    private AchievementSnapshot? ReadProgressSummary(
        string steamRoot, string accountId, string appId, string coverageKey, DateTimeOffset observedAtUtc)
    {
        try
        {
            var path = Path.Combine(steamRoot, "userdata", accountId, "config", "librarycache", "achievement_progress.json");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaxPayloadBytes) return null;
            IReadOnlyDictionary<string, (int Unlocked, int Total)> map;
            lock (_summaryGate)
            {
                if (_summary is { } cached && cached.Path == path && cached.Length == info.Length &&
                    cached.WriteUtc == info.LastWriteTimeUtc)
                {
                    map = cached.Map;
                }
                else
                {
                    var json = SteamPlaytime.ReadSharedText(path);
                    if (json is null) return null;
                    map = ParseProgressSummary(json);
                    // An empty parse may be a torn write; read it again next time.
                    _summary = map.Count > 0 ? (path, info.Length, info.LastWriteTimeUtc, map) : null;
                }
            }
            if (!map.TryGetValue(appId, out var counts) || counts.Total <= 0) return null;
            return new AchievementSnapshot
            {
                ProviderId = "steam",
                SourceGameId = appId,
                CoverageKey = coverageKey,
                Coverage = AchievementCoverageStatus.Partial,
                Capabilities = AchievementProviderCapabilities.Snapshot,
                ReportedTotal = counts.Total,
                ReportedUnlocked = counts.Unlocked,
                ObservedAtUtc = observedAtUtc,
                Message = "Steam local achievement summary.",
            };
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam achievement summary could not be read: " + ex.GetType().Name);
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, (int Unlocked, int Total)> ParseProgressSummary(string json)
    {
        var map = new Dictionary<string, (int Unlocked, int Total)>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("mapCache", out var cache) ||
                cache.ValueKind != JsonValueKind.Array)
                return map;
            foreach (var row in cache.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2) continue;
                var id = row[0].ValueKind switch
                {
                    JsonValueKind.Number when row[0].TryGetUInt32(out var n) && n > 0 => n.ToString(CultureInfo.InvariantCulture),
                    JsonValueKind.String when IsSteamAppId(row[0].GetString()) => row[0].GetString()!,
                    _ => null,
                };
                var body = row[1];
                if (id is null || body.ValueKind != JsonValueKind.Object) continue;
                var total = ReadInt(body, "total");
                var unlocked = ReadInt(body, "unlocked");
                if (total is null or < 0 or > MaxAchievements || unlocked is null or < 0 || unlocked > total) continue;
                map[id] = (unlocked.Value, total.Value);
            }
        }
        catch (JsonException)
        {
            /* torn or foreign file: no summary */
        }
        return map;
    }

    /// <summary>Active Steam account librarycache only.</summary>
    private static string? FindSteamLibraryCachePath(string steamRoot, string preferredAccountId, string appId)
    {
        try
        {
            var preferred = Path.Combine(
                steamRoot, "userdata", preferredAccountId, "config", "librarycache", appId + ".json");
            return File.Exists(preferred) ? preferred : null;
        }
        catch { return null; }
    }

    internal static AchievementSnapshot ParseSnapshotJson(
        string? json,
        string sourceGameId,
        string coverageKey,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPayloadBytes)
            return Unavailable(sourceGameId, "Steam returned no usable cached achievement data.", coverageKey, observedAtUtc);

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                return Unavailable(sourceGameId, "Steam returned no usable cached achievement data.", coverageKey, observedAtUtc);

            var hasAchievementSection = TryGetSection(root, "achievements", out var achievementSection);
            if (!hasAchievementSection || achievementSection.ValueKind != JsonValueKind.Object)
                return Unavailable(sourceGameId, "Steam returned no usable local achievement progress.", coverageKey, observedAtUtc);

            // nTotal/nAchieved are Steam's account-scoped summary. Vectors are
            // frequently only highlights, so never infer the displayed count
            // from them and never substitute a public catalog's locked rows.
            var total = ReadInt(achievementSection, "nTotal");
            var unlocked = ReadInt(achievementSection, "nAchieved");
            if (total is null or < 0 || unlocked is null or < 0 || unlocked > total)
                return Unavailable(sourceGameId, "Steam returned no usable local achievement progress.", coverageKey, observedAtUtc);

            // Only Steam's account achievement vectors can establish unlock state.
            // `achievementmap` is a catalog/metadata blob and may contain entries
            // for other app ids, stale data, or a contradictory bAchieved field.
            // Never let it add or change account state.
            var entries = new Dictionary<string, AchievementEntry>(StringComparer.OrdinalIgnoreCase);
            if (!CollectAccountEntries(achievementSection, entries, sourceGameId, observedAtUtc))
                return Unavailable(sourceGameId, "Steam returned contradictory local achievement progress.", coverageKey, observedAtUtc);
            if (TryGetSection(root, "achievementmap", out var mapSection))
                EnrichAchievementMapMetadata(mapSection, entries);
            if (entries.Count > total.Value)
                return Unavailable(sourceGameId, "Steam returned inconsistent local achievement progress.", coverageKey, observedAtUtc);
            if (entries.Values.Count(row => row.State.Unlocked) > unlocked.Value)
                return Unavailable(sourceGameId, "Steam returned inconsistent local achievement progress.", coverageKey, observedAtUtc);

            if (total == 0)
            {
                if (unlocked != 0 || entries.Count != 0)
                    return Unavailable(sourceGameId, "Steam returned inconsistent local achievement progress.", coverageKey, observedAtUtc);
                return new AchievementSnapshot
                {
                    ProviderId = "steam",
                    SourceGameId = sourceGameId,
                    CoverageKey = coverageKey,
                    Coverage = AchievementCoverageStatus.Unavailable,
                    Capabilities = AchievementProviderCapabilities.Snapshot,
                    ReportedTotal = 0,
                    ReportedUnlocked = 0,
                    ObservedAtUtc = observedAtUtc,
                    Message = "Steam's local 0 / 0 cache requires separate catalog verification.",
                };
            }

            return new AchievementSnapshot
            {
                ProviderId = "steam",
                SourceGameId = sourceGameId,
                CoverageKey = coverageKey,
                Coverage = AchievementCoverageStatus.Partial,
                Capabilities = AchievementProviderCapabilities.Snapshot |
                               AchievementProviderCapabilities.Progress,
                ReportedTotal = total,
                ReportedUnlocked = unlocked,
                ObservedAtUtc = observedAtUtc,
                Entries = entries.Values.OrderBy(row => row.Definition.ExternalId, StringComparer.Ordinal).ToArray(),
                Message = "Steam local achievement progress.",
            };
        }
        catch (JsonException)
        {
            return Unavailable(sourceGameId, "Steam returned no usable cached achievement data.", coverageKey, observedAtUtc);
        }
    }

    private static bool IsUncorroboratedLocalZero(AchievementSnapshot snapshot) =>
        snapshot.Coverage == AchievementCoverageStatus.Unavailable &&
        snapshot.ReportedTotal == 0 &&
        snapshot.ReportedUnlocked == 0 &&
        snapshot.Entries.Count == 0;

    private static HttpClient CreateWebApiClient()
    {
        var client = ExoHttp.Create(Timeout.InfiniteTimeSpan);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        return client;
    }

    private static async Task<string?> FetchWebApiAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await WebApiClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is > MaxPayloadBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (memory.Length + read > MaxPayloadBytes) return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static bool TryGetSection(
        JsonElement root,
        string sectionName,
        out JsonElement section)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(sectionName, out var property))
        {
            section = UnwrapData(property);
            return true;
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var pair in root.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array) continue;
                var values = pair.EnumerateArray();
                if (!values.MoveNext() || values.Current.ValueKind != JsonValueKind.String ||
                    !string.Equals(values.Current.GetString(), sectionName, StringComparison.OrdinalIgnoreCase) ||
                    !values.MoveNext())
                    continue;
                section = UnwrapData(values.Current);
                return true;
            }
        }

        section = default;
        return false;
    }

    private static JsonElement UnwrapData(JsonElement section)
    {
        if (section.ValueKind == JsonValueKind.Object &&
            section.TryGetProperty("data", out var data))
            return data;
        return section;
    }

    private static bool CollectAccountEntries(
        JsonElement achievementSection,
        IDictionary<string, AchievementEntry> entries,
        string sourceGameId,
        DateTimeOffset observedAtUtc)
    {
        // Steam has used these vector names across library-cache formats. Do
        // not recursively scrape the enclosing object: nearby metadata can
        // resemble an achievement row without being account state.
        foreach (var vectorName in new[]
                 {
                     "vecHighlight",
                     "vecUnachieved",
                     "vecAchievedHidden",
                     "vecAchieved",
                 })
        {
            if (!achievementSection.TryGetProperty(vectorName, out var vector)) continue;
            if (vector.ValueKind != JsonValueKind.Array) return false;
            foreach (var row in vector.EnumerateArray())
            {
                if (entries.Count >= MaxAchievements) return false;
                var entry = MapEntry(row, sourceGameId, observedAtUtc);
                if (entry is null) continue;
                if (!TryAddAccountEntry(entries, entry)) return false;
            }
        }
        return true;
    }

    private static bool TryAddAccountEntry(
        IDictionary<string, AchievementEntry> entries,
        AchievementEntry incoming)
    {
        var externalId = incoming.Definition.ExternalId;
        if (!entries.TryGetValue(externalId, out var existing))
        {
            entries[externalId] = incoming;
            return true;
        }

        // The same account row cannot report both locked and unlocked. Treat
        // that as bad source data instead of choosing a value or emitting an
        // achievement notification from it.
        if (existing.State.Unlocked != incoming.State.Unlocked) return false;
        entries[existing.Definition.ExternalId] = MergeAccountDuplicate(existing, incoming);
        return true;
    }

    private static void EnrichAchievementMapMetadata(
        JsonElement section,
        IDictionary<string, AchievementEntry> entries)
    {
        if (section.ValueKind == JsonValueKind.String)
        {
            var nestedJson = section.GetString();
            if (string.IsNullOrWhiteSpace(nestedJson) || nestedJson.Length > MaxPayloadBytes) return;
            try
            {
                using var nested = JsonDocument.Parse(nestedJson, new JsonDocumentOptions { MaxDepth = 64 });
                EnrichAchievementMapMetadata(nested.RootElement, entries, depth: 0);
            }
            catch (JsonException)
            {
                // Metadata is optional; a corrupt map cannot invalidate the
                // independently verified account vectors.
            }
            return;
        }

        EnrichAchievementMapMetadata(section, entries, depth: 0);
    }

    private static void EnrichAchievementMapMetadata(
        JsonElement element,
        IDictionary<string, AchievementEntry> entries,
        int depth)
    {
        if (depth > 32) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    EnrichAchievementMapMetadata(item, entries, depth + 1);
                break;
            case JsonValueKind.Object:
                var externalId = ReadText(element, "strID", 512);
                if (!string.IsNullOrWhiteSpace(externalId) && entries.TryGetValue(externalId, out var existing))
                    entries[existing.Definition.ExternalId] = MergeMapMetadata(existing, element);
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("strDescription") || property.NameEquals("strName")) continue;
                    EnrichAchievementMapMetadata(property.Value, entries, depth + 1);
                }
                break;
        }
    }

    private static AchievementEntry? MapEntry(
        JsonElement row,
        string sourceGameId,
        DateTimeOffset observedAtUtc)
    {
        var externalId = ReadText(row, "strID", 512);
        if (string.IsNullOrWhiteSpace(externalId)) return null;

        var hidden = ReadBool(row, "bHidden") ?? false;
        var unlocked = ReadBool(row, "bAchieved") ?? false;
        var name = ReadText(row, "strName", 512);
        var description = ReadText(row, "strDescription", 4_096) ?? string.Empty;
        if (hidden && !unlocked)
        {
            name = "Hidden achievement";
            description = string.Empty;
        }

        var current = ReadDouble(row, "flCurrentProgress");
        var target = ReadDouble(row, "flMaxProgress");
        if (current is < 0) current = null;
        if (target is <= 0) target = null;
        if (current is not null && target is not null)
            current = Math.Min(current.Value, target.Value);

        DateTimeOffset? unlockedAtUtc = null;
        var unlockedUnix = ReadLong(row, "rtUnlocked");
        if (unlocked && unlockedUnix is > 0)
        {
            try { unlockedAtUtc = DateTimeOffset.FromUnixTimeSeconds(unlockedUnix.Value); }
            catch { /* invalid provider timestamp */ }
        }

        return new AchievementEntry
        {
            Definition = new AchievementDefinition
            {
                ProviderId = "steam",
                SourceGameId = sourceGameId,
                ExternalId = externalId,
                Name = string.IsNullOrWhiteSpace(name) ? (hidden ? "Hidden achievement" : externalId) : name,
                Description = description,
                Hidden = hidden,
                IconUnlockedUrl = hidden && !unlocked ? null : ReadHttpsUrl(row, "strImage"),
            },
            State = new AchievementState
            {
                ExternalId = externalId,
                Unlocked = unlocked,
                UnlockedAtUtc = unlockedAtUtc,
                ProgressCurrent = current,
                ProgressTarget = target,
                ObservedAtUtc = observedAtUtc,
            },
        };
    }

    private static AchievementEntry MergeAccountDuplicate(AchievementEntry existing, AchievementEntry incoming)
    {
        // The caller has already verified the two account rows agree on
        // unlocked state. Preserve that state while retaining richer optional
        // metadata when Steam repeats the same row in multiple vectors.
        var hidden = existing.Definition.Hidden || incoming.Definition.Hidden;
        return existing with
        {
            Definition = existing.Definition with
            {
                Name = hidden && !existing.State.Unlocked
                    ? "Hidden achievement"
                    : PreferredText(existing.Definition.Name, incoming.Definition.Name),
                Description = hidden && !existing.State.Unlocked
                    ? string.Empty
                    : PreferredText(existing.Definition.Description, incoming.Definition.Description),
                Hidden = hidden,
                IconUnlockedUrl = hidden && !existing.State.Unlocked
                    ? null
                    : existing.Definition.IconUnlockedUrl ?? incoming.Definition.IconUnlockedUrl,
                IconLockedUrl = hidden && !existing.State.Unlocked
                    ? null
                    : existing.Definition.IconLockedUrl ?? incoming.Definition.IconLockedUrl,
            },
            State = existing.State with
            {
                UnlockedAtUtc = existing.State.UnlockedAtUtc ?? incoming.State.UnlockedAtUtc,
                ProgressCurrent = Max(existing.State.ProgressCurrent, incoming.State.ProgressCurrent),
                ProgressTarget = Max(existing.State.ProgressTarget, incoming.State.ProgressTarget),
            },
        };
    }

    private static AchievementEntry MergeMapMetadata(AchievementEntry accountEntry, JsonElement mapRow)
    {
        var mapHidden = ReadBool(mapRow, "bHidden") ?? false;
        var hidden = accountEntry.Definition.Hidden || mapHidden;
        var mapName = ReadText(mapRow, "strName", 512) ?? string.Empty;
        var mapDescription = ReadText(mapRow, "strDescription", 4_096) ?? string.Empty;
        return accountEntry with
        {
            Definition = accountEntry.Definition with
            {
                Name = hidden && !accountEntry.State.Unlocked
                    ? "Hidden achievement"
                    : PreferredMapName(accountEntry.Definition.Name, mapName,
                        accountEntry.Definition.ExternalId),
                Description = hidden && !accountEntry.State.Unlocked
                    ? string.Empty
                    : PreferredText(accountEntry.Definition.Description, mapDescription),
                Hidden = hidden,
                IconUnlockedUrl = hidden && !accountEntry.State.Unlocked
                    ? null
                    : accountEntry.Definition.IconUnlockedUrl ?? ReadHttpsUrl(mapRow, "strImage"),
                IconLockedUrl = hidden && !accountEntry.State.Unlocked
                    ? null
                    : accountEntry.Definition.IconLockedUrl,
            },
            // The map is never account authority. In particular, ignore its
            // bAchieved, timestamps, and progress fields.
            State = accountEntry.State,
        };
    }

    private static string PreferredText(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) || preferred == "Hidden achievement"
            ? fallback
            : preferred;

    private static string PreferredMapName(string existing, string mapped, string externalId) =>
        string.IsNullOrWhiteSpace(mapped)
            ? existing
            : string.IsNullOrWhiteSpace(existing) || existing == "Hidden achievement" ||
              string.Equals(existing, externalId, StringComparison.Ordinal)
                ? mapped
                : existing;

    private static string? SourceGameId(GameEntry game)
    {
        var target = game.LaunchTarget?.Trim();
        var hasSteamId = game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase);
        var canonicalId = hasSteamId ? game.Id[6..].Trim() : null;
        if (hasSteamId && !IsSteamAppId(canonicalId)) return null;
        if (!string.IsNullOrWhiteSpace(target) && !IsSteamAppId(target)) return null;
        if (canonicalId is not null && !string.IsNullOrWhiteSpace(target) &&
            !string.Equals(canonicalId, target, StringComparison.Ordinal))
            return null;
        return canonicalId ?? (IsSteamAppId(target) ? target : null);
    }

    private static bool IsSteamAppId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 20 && value.All(char.IsDigit);

    private static string? ResolveSteamRoot()
    {
        var candidates = new List<string?>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            candidates.Add(key?.GetValue("SteamPath") as string);
            candidates.Add(key?.GetValue("InstallPath") as string);
        }
        catch { /* registry is best-effort */ }
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace('/', Path.DirectorySeparatorChar).TrimEnd('\\', '/'))
            .FirstOrDefault(Directory.Exists);
    }

    private static string? ResolveAccountId(string steamRoot)
    {
        string? registryAccount = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            var value = key?.GetValue("ActiveUser");
            uint account = value switch
            {
                int signed => unchecked((uint)signed),
                uint unsigned => unsigned,
                long wide when wide is > 0 and <= uint.MaxValue => (uint)wide,
                string text when uint.TryParse(text, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0,
            };
            if (account > 0) registryAccount = account.ToString(CultureInfo.InvariantCulture);
        }
        catch { /* ambiguous account will fail closed below */ }
        return SteamPlaytime.ResolveActiveAccountId(steamRoot, registryAccount);
    }

    private bool IsCurrentAccount(string steamRoot, string expectedAccountId)
    {
        try
        {
            return string.Equals(_resolveAccountId(steamRoot), expectedAccountId,
                StringComparison.Ordinal);
        }
        catch
        {
            // If active-account resolution becomes unavailable, treating the
            // just-read cache as current would be an unsafe assumption.
            return false;
        }
    }

    private AchievementSnapshot Unavailable(string sourceGameId, string message, string? coverageKey = null) =>
        Unavailable(sourceGameId, message, coverageKey ?? "steam:unavailable", DateTimeOffset.UtcNow);

    private static AchievementSnapshot Unavailable(
        string sourceGameId,
        string message,
        string coverageKey,
        DateTimeOffset observedAtUtc) => new()
    {
        ProviderId = "steam",
        SourceGameId = sourceGameId,
        CoverageKey = coverageKey,
        Coverage = AchievementCoverageStatus.Unavailable,
        Capabilities = AchievementProviderCapabilities.Snapshot |
                       AchievementProviderCapabilities.Progress,
        ObservedAtUtc = observedAtUtc,
        Message = message,
    };

    private static string? ReadText(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > maxLength ? null : text;
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        var value = ReadLong(element, property);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static long? ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static double? ReadDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
               double.IsFinite(number)
            ? number
            : null;
    }

    private static bool? ReadBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number != 0;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (bool.TryParse(value.GetString(), out var parsed)) return parsed;
            if (int.TryParse(value.GetString(), out number)) return number != 0;
        }
        return null;
    }

    private static string? ReadHttpsUrl(JsonElement element, string property)
    {
        var text = ReadText(element, property, 2_048);
        return AchievementIconCache.SanitizeProviderImageUrl(text);
    }

    private static double? Max(double? left, double? right) =>
        left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);
}
