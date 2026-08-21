using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Epic's own launcher playtime service. Legendary already stores the user's
/// Epic OAuth session locally; Exo only reads it in memory and never logs or
/// persists account IDs or tokens.
/// </summary>
internal static partial class EpicPlaytime
{
    private const string PlaytimeBaseUrl =
        "https://library-service.live.use1a.on.epicgames.com/library/api/public/playtime/account/";
    private const string OauthTokenUrl =
        "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token";
    /// <summary>Public EGL/Legendary user-token client (Basic id:secret). Never logged.</summary>
    private const string OauthBasic =
        "MzRhMDJjZjhmNDQxNGUyOWIxNTkyMTg3NmRhMzZmOWE6ZGFhZmJjY2M3Mzc3NDUwMzlkZmZlNTNkOTRmYzc2Y2Y=";
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly EpicPlaytimeCache Cache = new(
        FetchForCacheAsync,
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(1));

    internal sealed record Session(
        string AccountId,
        string AccessToken,
        string? RefreshToken = null,
        DateTimeOffset? ExpiresAt = null);

    /// <summary>
    /// The library must not wait on Epic's remote playtime service before its
    /// first paint. Return the last verified snapshot immediately and refresh
    /// it in the background instead.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> GetCachedMinutes()
    {
        var scope = GetActiveAccountScope();
        if (string.IsNullOrWhiteSpace(scope))
        {
            Cache.Clear();
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        var live = Cache.Snapshot(scope);
        if (live.Count > 0) return live;
        return LoadPersistedMinutes(scope);
    }

    internal static void RefreshCachedMinutes()
    {
        var scope = GetActiveAccountScope();
        if (string.IsNullOrWhiteSpace(scope)) { Cache.Clear(); return; }
        _ = Cache.RefreshIfStaleAsync(scope);
    }

    /// <summary>
    /// Reads only the active Legendary session and returns an opaque, one-way
    /// tag. It is used to quarantine in-memory cache entries on shared PCs;
    /// the raw Epic account id never reaches a model, log, or bridge payload.
    /// </summary>
    internal static string? GetActiveAccountScope()
    {
        var userPath = ResolveLegendaryUserPath();
        if (userPath is not null)
        {
            try
            {
                var session = ParseSessionJson(File.ReadAllText(userPath));
                if (session is not null) return AccountScopeFor(session.AccountId);
            }
            catch { /* try EGL remember-me next */ }
        }

        foreach (var iniPath in EglRememberMeIniCandidates())
        {
            try
            {
                if (!File.Exists(iniPath)) continue;
                if (!TryReadRememberMeFromIni(File.ReadAllText(iniPath), out var email, out var refresh))
                    continue;
                if (!string.IsNullOrWhiteSpace(email))
                    return AccountScopeFor("egl:" + email.Trim().ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(refresh))
                    return AccountScopeFor("egl:remember-me");
            }
            catch { /* next ini */ }
        }

        return null;
    }

    private static string AccountScopeFor(string accountId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("epic\0" + accountId));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..20];
    }

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
        var scope = GetActiveAccountScope();
        if (string.IsNullOrWhiteSpace(scope)) return EpicPlaytimeFetchResult.Failed;

        try
        {
            var session = await ResolveSessionAsync(ct).ConfigureAwait(false);
            if (session is null) return EpicPlaytimeFetchResult.Failed;

            var minutes = await QueryPlaytimeAsync(session, ct).ConfigureAwait(false);
            if (minutes is null && !string.IsNullOrWhiteSpace(session.RefreshToken))
            {
                var refreshed = await RefreshSessionAsync(session.RefreshToken, ct).ConfigureAwait(false);
                if (refreshed is not null)
                    minutes = await QueryPlaytimeAsync(refreshed, ct).ConfigureAwait(false);
            }

            if (minutes is null) return EpicPlaytimeFetchResult.Failed;
            PersistMinutes(scope, minutes);
            return new EpicPlaytimeFetchResult(true, minutes, scope);
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

    private static async Task<IReadOnlyDictionary<string, int>?> QueryPlaytimeAsync(
        Session session, CancellationToken ct)
    {
        var account = Uri.EscapeDataString(session.AccountId);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, PlaytimeBaseUrl + account + "/all");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", session.AccessToken);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit");

        using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Debug($"Epic playtime query returned HTTP {(int)response.StatusCode}.");
            return null;
        }

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            return null;
        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // A 2xx response can still be an Epic error document or a changed
        // payload. Treat it as a failed refresh rather than overwriting the
        // last verified playtime map with an empty one.
        return TryParseMinutesJson(json, out var minutes) ? minutes : null;
    }

    /// <summary>
    /// The one Epic session in the app. <see cref="EpicFriends"/> borrows it so
    /// there is never a second token store.
    /// </summary>
    internal static async Task<Session?> ResolveSessionAsync(CancellationToken ct)
    {
        var userPath = ResolveLegendaryUserPath();
        if (userPath is not null)
        {
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
                if (session is not null)
                {
                    if (!AccessTokenNeedsRefresh(session, DateTimeOffset.UtcNow) ||
                        string.IsNullOrWhiteSpace(session.RefreshToken))
                        return session;
                    var refreshed = await RefreshSessionAsync(session.RefreshToken, ct)
                        .ConfigureAwait(false);
                    return refreshed ?? session;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A locked user.json must not hide an EGL remember-me session.
            }
        }

        foreach (var iniPath in EglRememberMeIniCandidates())
        {
            try
            {
                if (!File.Exists(iniPath)) continue;
                if (!TryReadRememberMeFromIni(await File.ReadAllTextAsync(iniPath, ct).ConfigureAwait(false),
                        out _, out var refresh) ||
                    string.IsNullOrWhiteSpace(refresh))
                    continue;
                var refreshed = await RefreshSessionAsync(refresh, ct).ConfigureAwait(false);
                if (refreshed is not null) return refreshed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Encrypted or unreadable remember-me is normal on newer EGL.
            }
        }

        return null;
    }

    private static async Task<Session?> RefreshSessionAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 16_384 ||
            refreshToken.ContainsAny('\r', '\n'))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OauthTokenUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", OauthBasic);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["token_type"] = "eg1",
            });

            using var response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Debug($"Epic token refresh returned HTTP {(int)response.StatusCode}.");
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                return null;
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, ct).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseSessionJson(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Epic token refresh unavailable: " + ex.GetType().Name);
            return null;
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
            var accountId = ReadSessionString(root, "account_id", "accountId");
            var accessToken = ReadSessionString(root, "access_token", "accessToken");
            var refreshToken = ReadSessionString(root, "refresh_token", "refreshToken");
            var tokenType = ReadSessionString(root, "token_type", "tokenType") ?? "bearer";
            var expiresRaw = ReadSessionString(root, "expires_at", "expiresAt");
            DateTimeOffset? expiresAt = null;
            if (!string.IsNullOrWhiteSpace(expiresRaw) &&
                DateTimeOffset.TryParse(
                    expiresRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedExpiry))
                expiresAt = parsedExpiry;

            if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 128 ||
                string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 16_384 ||
                !(string.Equals(tokenType, "bearer", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(tokenType, "eg1", StringComparison.OrdinalIgnoreCase)) ||
                accountId.ContainsAny('\r', '\n') || accessToken.ContainsAny('\r', '\n') ||
                (refreshToken is not null &&
                 (refreshToken.Length > 16_384 || refreshToken.ContainsAny('\r', '\n'))))
                return null;

            return new Session(accountId, accessToken, refreshToken, expiresAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadSessionString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        return null;
    }

    internal static bool AccessTokenNeedsRefresh(Session session, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(session.AccessToken)) return true;
        if (session.ExpiresAt is null) return false;
        return session.ExpiresAt.Value <= now.AddMinutes(2);
    }

    internal static IEnumerable<string> EglRememberMeIniCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) yield break;
        yield return Path.Combine(
            local, "EpicGamesLauncher", "Saved", "Config", "WindowsEditor", "GameUserSettings.ini");
        yield return Path.Combine(
            local, "EpicGamesLauncher", "Saved", "Config", "Windows", "GameUserSettings.ini");
    }

    internal static bool TryParseRememberMeData(string? raw, out string? email, out string? refreshToken)
    {
        email = null;
        refreshToken = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim().Trim('"');
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    if (TryReadRememberMeObject(el, out email, out refreshToken))
                        return true;
                }
                return false;
            }

            return TryReadRememberMeObject(root, out email, out refreshToken);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryReadRememberMeFromIni(string text, out string? email, out string? refreshToken)
    {
        email = null;
        refreshToken = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var section = text.IndexOf("[RememberMe]", StringComparison.OrdinalIgnoreCase);
        if (section < 0) return false;
        var slice = text[section..];
        var match = RememberMeDataLineRegex().Match(slice);
        return match.Success &&
               TryParseRememberMeData(match.Groups[1].Value, out email, out refreshToken);
    }

    private static bool TryReadRememberMeObject(
        JsonElement el, out string? email, out string? refreshToken)
    {
        email = ReadSessionString(el, "email", "Email");
        refreshToken = ReadSessionString(el, "token", "Token", "refresh_token", "refreshToken");
        return !string.IsNullOrWhiteSpace(refreshToken) &&
               refreshToken.Length <= 16_384 &&
               !refreshToken.ContainsAny('\r', '\n');
    }

    [GeneratedRegex(@"^\s*Data\s*=\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RememberMeDataLineRegex();

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
            if (game.Store != StoreKind.Epic)
                return game;

            if (!TryMinutesForEpicGame(game, minutesByArtifact, out var minutes) ||
                minutes <= 0 || game.PlaytimeMinutes >= minutes)
                return game;

            return CloneWithPlaytime(game, minutes);
        }).ToList();
    }

    private static bool TryMinutesForEpicGame(
        GameEntry game,
        IReadOnlyDictionary<string, int> minutesByArtifact,
        out int minutes)
    {
        minutes = 0;
        foreach (var key in EpicArtifactKeys(game))
        {
            if (minutesByArtifact.TryGetValue(key, out var value) && value > minutes)
                minutes = value;
        }

        return minutes > 0;
    }

    internal static IEnumerable<string> ArtifactKeys(GameEntry game) => EpicArtifactKeys(game);

    private static IEnumerable<string> EpicArtifactKeys(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget))
            yield return game.LaunchTarget.Trim();
        if (game.Id.StartsWith("epic:", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = game.Id["epic:".Length..].Trim();
            if (suffix.Length > 0)
                yield return suffix;
        }
        if (!string.IsNullOrWhiteSpace(game.Title))
            yield return game.Title.Trim();
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

    /// <summary>
    /// Every Windows/Linux place Legendary or Heroic writes <c>user.json</c>.
    /// Existence is not required — callers decide which file to open.
    /// </summary>
    internal static IEnumerable<string> LegendaryUserJsonCandidates()
    {
        var custom = Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(custom))
            yield return Path.Combine(custom, "user.json");

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            yield return Path.Combine(xdg, "legendary", "user.json");
            yield return Path.Combine(xdg, "heroic", "legendaryConfig", "legendary", "user.json");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            yield return Path.Combine(profile, ".config", "legendary", "user.json");
            yield return Path.Combine(profile, ".config", "heroic", "legendaryConfig", "legendary", "user.json");
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roaming))
        {
            yield return Path.Combine(roaming, "legendary", "user.json");
            yield return Path.Combine(roaming, "heroic", "legendaryConfig", "legendary", "user.json");
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            yield return Path.Combine(local, "legendary", "user.json");
            yield return Path.Combine(local, "heroic", "legendaryConfig", "legendary", "user.json");
            yield return Path.Combine(local, "ExoLauncher", "legendary", "user.json");
            yield return Path.Combine(local, "ExoLauncher", "tools", "legendary", "user.json");
        }
    }

    private static string? ResolveLegendaryUserPath()
    {
        foreach (var candidate in LegendaryUserJsonCandidates())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // A locked or unreadable candidate must not hide the next path.
            }
        }

        return null;
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

    private static string PersistPath =>
        Path.Combine(PathHelper.AppDataDir, "epic-playtime.json");

    internal static void PersistMinutes(string scope, IReadOnlyDictionary<string, int> minutes)
    {
        if (string.IsNullOrWhiteSpace(scope) || minutes.Count == 0) return;
        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var positive = minutes
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (positive.Count == 0) return;
            var json = JsonSerializer.Serialize(new PersistedPlaytimeDocument(scope, positive));
            var tmp = PersistPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, PersistPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Epic playtime persist failed: " + ex.GetType().Name);
        }
    }

    internal static IReadOnlyDictionary<string, int> LoadPersistedMinutes(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(PersistPath))
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var document = JsonSerializer.Deserialize<PersistedPlaytimeDocument>(
                File.ReadAllText(PersistPath));
            if (document is null ||
                !string.Equals(document.Scope, scope, StringComparison.Ordinal) ||
                document.Minutes is null)
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            return document.Minutes
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Epic playtime persist load failed: " + ex.GetType().Name);
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed record PersistedPlaytimeDocument(
        string Scope,
        Dictionary<string, int> Minutes);

    private static GameEntry CloneWithPlaytime(GameEntry game, int minutes) => new()
    {
        Id = game.Id,
        Title = game.Title,
        Store = game.Store,
        Installed = game.Installed,
        Owned = game.Owned,
        EntitlementState = game.EntitlementState,
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
        // A raised Epic reading must not cost a grouped card its exact store
        // sources; dropping them collapsed a cross-store card to Epic alone.
        CanonicalTitleKey = game.CanonicalTitleKey,
        SelectedVariantId = game.SelectedVariantId,
        Variants = game.Variants,
    };
}

internal readonly record struct EpicPlaytimeFetchResult(
    bool Succeeded,
    IReadOnlyDictionary<string, int> Minutes,
    string? AccountScope = null)
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
    private string? _accountScope;

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

    public IReadOnlyDictionary<string, int> Snapshot(string? accountScope = null)
    {
        lock (_gate)
        {
            return string.IsNullOrWhiteSpace(accountScope) ||
                   string.Equals(_accountScope, accountScope, StringComparison.Ordinal)
                ? _minutes
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public Task RefreshIfStaleAsync(string? accountScope = null)
    {
        lock (_gate)
        {
            if (!string.Equals(_accountScope, accountScope, StringComparison.Ordinal))
            {
                // Never carry a last-good map from one Epic user into another
                // user's first paint. An in-flight old-user fetch is discarded
                // below when its scope no longer matches this request.
                _accountScope = accountScope;
                _minutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _freshAt = DateTimeOffset.MinValue;
                _retryAfter = DateTimeOffset.MinValue;
            }
            var now = _utcNow();
            if (_refresh is { IsCompleted: false }) return _refresh;
            if (_freshAt != DateTimeOffset.MinValue && now - _freshAt < _ttl)
                return Task.CompletedTask;
            if (now < _retryAfter) return Task.CompletedTask;
            _refresh = Task.Run(() => RefreshAsync(accountScope));
            return _refresh;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _accountScope = null;
            _minutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _freshAt = DateTimeOffset.MinValue;
            _retryAfter = DateTimeOffset.MinValue;
        }
    }

    private async Task RefreshAsync(string? expectedAccountScope)
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
            if (!string.Equals(_accountScope, expectedAccountScope, StringComparison.Ordinal) ||
                (expectedAccountScope is not null &&
                 !string.Equals(result.AccountScope, expectedAccountScope, StringComparison.Ordinal)))
                return;

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
