using System.Net.Http.Headers;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Epic's own launcher playtime service. Legendary already stores the user's
/// Epic OAuth session locally; Exo only reads it in memory and never logs or
/// persists account IDs or tokens.
/// </summary>
internal static class EpicPlaytime
{
    private const string PlaytimeBaseUrl =
        "https://library-service.live.use1a.on.epicgames.com/library/api/public/playtime/account/";
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly EpicPlaytimeCache Cache = new(
        FetchForCacheAsync,
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(1));

    internal sealed record Session(string AccountId, string AccessToken);

    /// <summary>
    /// The library must not wait on Epic's remote playtime service before its
    /// first paint. Return the last verified snapshot immediately and refresh
    /// it in the background instead.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> GetCachedMinutes() => Cache.Snapshot();

    internal static void RefreshCachedMinutes() => _ = Cache.RefreshIfStaleAsync();

    internal static event Action? CachedMinutesUpdated
    {
        add => Cache.Updated += value;
        remove => Cache.Updated -= value;
    }

    public static async Task<IReadOnlyDictionary<string, int>> FetchAllMinutesAsync(
        CancellationToken ct = default)
    {
        var result = await FetchForCacheAsync(ct).ConfigureAwait(false);
        return result.Minutes;
    }

    private static async Task<EpicPlaytimeFetchResult> FetchForCacheAsync(CancellationToken ct)
    {
        var userPath = ResolveLegendaryUserPath();
        if (userPath is null) return EpicPlaytimeFetchResult.Failed;

        try
        {
            string sessionJson;
            await using (var stream = new FileStream(
                             userPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                sessionJson = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            var session = ParseSessionJson(sessionJson);
            if (session is null) return EpicPlaytimeFetchResult.Failed;

            var account = Uri.EscapeDataString(session.AccountId);
            using var request = new HttpRequestMessage(
                HttpMethod.Get, PlaytimeBaseUrl + account + "/all");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", session.AccessToken);

            using var response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Debug($"Epic playtime query returned HTTP {(int)response.StatusCode}.");
                return EpicPlaytimeFetchResult.Failed;
            }

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                return EpicPlaytimeFetchResult.Failed;
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, ct).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // A 2xx response can still be an Epic error document or a changed
            // payload. Treat it as a failed refresh rather than overwriting the
            // last verified playtime map with an empty one.
            return TryParseMinutesJson(json, out var minutes)
                ? new EpicPlaytimeFetchResult(true, minutes)
                : EpicPlaytimeFetchResult.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Playtime is enrichment. A service outage must not hide the library.
            AppLog.Debug("Epic playtime query unavailable: " + ex.GetType().Name);
            return EpicPlaytimeFetchResult.Failed;
        }
    }

    internal static Session? ParseSessionJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;
            var accountId = root.TryGetProperty("account_id", out var account)
                ? account.GetString()?.Trim()
                : null;
            var accessToken = root.TryGetProperty("access_token", out var token)
                ? token.GetString()?.Trim()
                : null;
            var tokenType = root.TryGetProperty("token_type", out var type)
                ? type.GetString()?.Trim()
                : "bearer";

            if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 128 ||
                string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 16_384 ||
                !string.Equals(tokenType, "bearer", StringComparison.OrdinalIgnoreCase) ||
                accountId.ContainsAny('\r', '\n') || accessToken.ContainsAny('\r', '\n'))
                return null;

            return new Session(accountId, accessToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, int> ParseMinutesJson(string? json) =>
        TryParseMinutesJson(json, out var minutes) ? minutes : Empty();

    internal static bool TryParseMinutesJson(string? json, out IReadOnlyDictionary<string, int> minutes)
    {
        var parsedMinutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        minutes = parsedMinutes;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                ReadRows(root, parsedMinutes);
                return true;
            }
            if (root.ValueKind == JsonValueKind.Object)
            {
                // The per-artifact endpoint returns one object. Some launcher
                // builds wrap the all-items response in a playtimeList array.
                if (root.TryGetProperty("playtimeList", out var list) &&
                    list.ValueKind == JsonValueKind.Array)
                {
                    ReadRows(list, parsedMinutes);
                    return true;
                }
                if (root.TryGetProperty("artifactId", out _) &&
                    root.TryGetProperty("totalTime", out _))
                {
                    ReadRow(root, parsedMinutes);
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Treat a changed/error payload as unavailable.
        }

        minutes = Empty();
        return false;
    }

    public static IReadOnlyList<GameEntry> Apply(
        IReadOnlyList<GameEntry> games,
        IReadOnlyDictionary<string, int> minutesByArtifact)
    {
        if (games.Count == 0 || minutesByArtifact.Count == 0) return games;

        return games.Select(game =>
        {
            if (game.Store != StoreKind.Epic ||
                string.IsNullOrWhiteSpace(game.LaunchTarget) ||
                !minutesByArtifact.TryGetValue(game.LaunchTarget, out var minutes) ||
                minutes <= 0 || game.PlaytimeMinutes >= minutes)
                return game;

            return CloneWithPlaytime(game, minutes);
        }).ToList();
    }

    private static void ReadRows(JsonElement rows, IDictionary<string, int> output)
    {
        foreach (var row in rows.EnumerateArray()) ReadRow(row, output);
    }

    private static void ReadRow(JsonElement row, IDictionary<string, int> output)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("artifactId", out var artifactElement) ||
            !row.TryGetProperty("totalTime", out var totalElement))
            return;

        var artifact = artifactElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(artifact) || artifact.Length > 256) return;

        long seconds;
        if (totalElement.ValueKind == JsonValueKind.Number)
        {
            if (!totalElement.TryGetInt64(out seconds)) return;
        }
        else if (totalElement.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(totalElement.GetString(), out seconds)) return;
        }
        else
        {
            return;
        }

        if (seconds <= 0) return;
        var value = (int)Math.Min(int.MaxValue, Math.Max(1, seconds / 60));
        if (!output.TryGetValue(artifact, out var current) || value > current)
            output[artifact] = value;
    }

    private static string? ResolveLegendaryUserPath()
    {
        var custom = Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var candidate = Path.Combine(custom, "user.json");
            if (File.Exists(candidate)) return candidate;
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            var candidate = Path.Combine(xdg, "legendary", "user.json");
            if (File.Exists(candidate)) return candidate;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) return null;
        var fallback = Path.Combine(profile, ".config", "legendary", "user.json");
        return File.Exists(fallback) ? fallback : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            // Never forward the bearer session to a redirected host.
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        return http;
    }

    private static IReadOnlyDictionary<string, int> Empty() =>
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static GameEntry CloneWithPlaytime(GameEntry game, int minutes) => new()
    {
        Id = game.Id,
        Title = game.Title,
        Store = game.Store,
        Installed = game.Installed,
        Owned = game.Owned,
        UpdateAvailable = game.UpdateAvailable,
        CanInstall = game.CanInstall,
        Path = game.Path,
        CoverUrl = game.CoverUrl,
        CoverSource = game.CoverSource,
        PlaytimeMinutes = minutes,
        SizeBytes = game.SizeBytes,
        Status = game.Status,
        Deps = game.Deps,
        LaunchNote = game.LaunchNote,
        LaunchTarget = game.LaunchTarget,
        LastPlayedUtc = game.LastPlayedUtc,
        IsFavorite = game.IsFavorite,
    };
}

internal readonly record struct EpicPlaytimeFetchResult(
    bool Succeeded,
    IReadOnlyDictionary<string, int> Minutes)
{
    public static EpicPlaytimeFetchResult Failed { get; } = new(
        false,
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Thread-safe, last-good cache for a nonessential remote enrichment. A failed
/// refresh keeps its old snapshot and backs off briefly instead of making every
/// library scan wait for the same unavailable service.
/// </summary>
internal sealed class EpicPlaytimeCache
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<EpicPlaytimeFetchResult>> _loader;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _failureRetry;
    private IReadOnlyDictionary<string, int> _minutes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _freshAt = DateTimeOffset.MinValue;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private Task? _refresh;

    public EpicPlaytimeCache(
        Func<CancellationToken, Task<EpicPlaytimeFetchResult>> loader,
        TimeSpan ttl,
        TimeSpan failureRetry,
        Func<DateTimeOffset>? utcNow = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _ttl = ttl;
        _failureRetry = failureRetry;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public event Action? Updated;

    public IReadOnlyDictionary<string, int> Snapshot()
    {
        lock (_gate) return _minutes;
    }

    public Task RefreshIfStaleAsync()
    {
        lock (_gate)
        {
            var now = _utcNow();
            if (_refresh is { IsCompleted: false }) return _refresh;
            if (_freshAt != DateTimeOffset.MinValue && now - _freshAt < _ttl)
                return Task.CompletedTask;
            if (now < _retryAfter) return Task.CompletedTask;
            _refresh = Task.Run(RefreshAsync);
            return _refresh;
        }
    }

    private async Task RefreshAsync()
    {
        EpicPlaytimeFetchResult result;
        try
        {
            result = await _loader(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Epic playtime background refresh unavailable: " + ex.GetType().Name);
            result = EpicPlaytimeFetchResult.Failed;
        }

        var notify = false;
        lock (_gate)
        {
            var now = _utcNow();
            if (!result.Succeeded)
            {
                _retryAfter = now + _failureRetry;
                return;
            }

            var next = new Dictionary<string, int>(result.Minutes, StringComparer.OrdinalIgnoreCase);
            notify = !SameMinutes(_minutes, next);
            _minutes = next;
            _freshAt = now;
            _retryAfter = DateTimeOffset.MinValue;
        }

        if (notify)
        {
            try { Updated?.Invoke(); }
            catch { /* listeners only request a best-effort derived refresh */ }
        }
    }

    private static bool SameMinutes(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
            if (!right.TryGetValue(pair.Key, out var value) || value != pair.Value)
                return false;
        return true;
    }
}

internal static class EpicPlaytimeStringExtensions
{
    public static bool ContainsAny(this string value, params char[] chars) =>
        value.IndexOfAny(chars) >= 0;
}
