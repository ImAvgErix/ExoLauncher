using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Adapters.Cli;
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
    internal static IReadOnlyDictionary<string, int> GetCachedMinutes() =>
        GetCachedMinutes(Services.SettingsService.CountHoursAcrossAccounts);

    /// <summary>
    /// The active account's reading, or with <paramref name="allAccounts"/> the
    /// sum per game of every Epic account's last reading on this PC: the active
    /// one live, the others as last fetched (their own folders or an earlier sign-in).
    /// </summary>
    internal static IReadOnlyDictionary<string, int> GetCachedMinutes(bool allAccounts)
    {
        var scope = GetActiveAccountScope();
        IReadOnlyDictionary<string, int> active;
        if (string.IsNullOrWhiteSpace(scope))
        {
            Cache.Clear();
            active = Empty();
        }
        else
        {
            var live = Cache.Snapshot(scope);
            active = live.Count > 0 ? live : LoadPersistedMinutes(scope);
        }
        return allAccounts ? HoursFor(scope, active, LoadAllPersistedMinutes(), allAccounts) : active;
    }

    /// <summary>
    /// The active account's minutes; with <paramref name="allAccounts"/> the sum of every
    /// account's, the active one's live reading standing in for its saved one.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> HoursFor(
        string? activeScope,
        IReadOnlyDictionary<string, int> active,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> saved,
        bool allAccounts)
    {
        if (!allAccounts) return active;
        var scopes = new Dictionary<string, IReadOnlyDictionary<string, int>>(saved, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(activeScope)) scopes[activeScope] = active;
        return SumScopes(scopes.Values);
    }

    /// <summary>Per artifact: the sum of each account's minutes.</summary>
    internal static IReadOnlyDictionary<string, int> SumScopes(IEnumerable<IReadOnlyDictionary<string, int>> scopes)
    {
        var sum = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var minutes in scopes)
            foreach (var (artifact, value) in minutes)
                if (value > 0)
                    sum[artifact] = (int)Math.Min(int.MaxValue, (long)sum.GetValueOrDefault(artifact) + value);
        return sum;
    }

    /// <summary>
    /// The scope Epic's reading belongs to: the active account, or every account
    /// while hours count across accounts (so a reading baseline restarts on a toggle).
    /// </summary>
    internal static string? GetHoursScope()
    {
        var scope = GetActiveAccountScope();
        return scope is not null && Services.SettingsService.CountHoursAcrossAccounts ? "all:" + scope : scope;
    }

    internal static void RefreshCachedMinutes()
    {
        if (Services.SettingsService.CountHoursAcrossAccounts) EpicAccounts.RefreshAddedInBackground();
        var scope = GetActiveAccountScope();
        if (string.IsNullOrWhiteSpace(scope)) { Cache.Clear(); return; }
        _ = Cache.RefreshIfStaleAsync(scope);
    }

    /// <summary>Epic's playtime for one session. Its token is refreshed by Legendary beforehand, never here.</summary>
    internal static Task<IReadOnlyDictionary<string, int>?> FetchMinutesAsync(Session session, CancellationToken ct) =>
        QueryPlaytimeAsync(session, ct);

    /// <summary>The account id the Epic Games Launcher last signed in with (HKCU Unreal Engine Identifiers), if readable.</summary>
    internal static string? ReadEglAccountId()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Identifiers");
            var value = (key?.GetValue("AccountId") as string)?.Trim();
            return value is { Length: 32 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
        }
        catch { return null; }
    }

    /// <summary>The Epic Games Launcher keeps a remember-me sign-in that Legendary can import.</summary>
    internal static bool HasEglRememberMe()
    {
        foreach (var iniPath in EglRememberMeIniCandidates())
        {
            try
            {
                if (!File.Exists(iniPath)) continue;
                var text = File.ReadAllText(iniPath);
                var section = text.IndexOf("[RememberMe]", StringComparison.OrdinalIgnoreCase);
                if (section >= 0 && RememberMeDataLineRegex().Match(text[section..]) is { Success: true } data &&
                    data.Groups[1].Value.Trim('"', ' ').Length > 0)
                    return true;
            }
            catch { /* next ini */ }
        }
        return false;
    }

    /// <summary>The display name and account id in a Legendary user.json. Tokens are not read.</summary>
    internal static (string? AccountId, string? DisplayName) ReadUserIdentity(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            var id = ReadSessionString(doc.RootElement, "account_id", "accountId");
            var name = ReadSessionString(doc.RootElement, "displayName", "display_name");
            return (id is { Length: <= 128 } ? id : null, name is { Length: <= 128 } ? name : null);
        }
        catch (JsonException) { return (null, null); }
    }

    /// <summary>Legendary's user.json Exo signs in with: the first one that exists.</summary>
    internal static string? LegendaryUserPath() => ResolveLegendaryUserPath();

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

        // The launcher's own account id keeps the same scope Legendary would
        // use for that account, so its readings never count twice.
        var eglAccount = ReadEglAccountId();
        foreach (var iniPath in EglRememberMeIniCandidates())
        {
            try
            {
                if (!File.Exists(iniPath)) continue;
                if (!TryReadRememberMeFromIni(File.ReadAllText(iniPath), out var email, out var refresh))
                    continue;
                if (eglAccount is not null)
                    return AccountScopeFor(eglAccount);
                if (!string.IsNullOrWhiteSpace(email))
                    return AccountScopeFor("egl:" + email.Trim().ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(refresh))
                    return AccountScopeFor("egl:remember-me");
            }
            catch { /* next ini */ }
        }

        return null;
    }

    internal static string AccountScopeFor(string accountId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("epic\0" + accountId));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..20];
    }

    private static Action? _otherAccountsUpdated;

    internal static event Action? CachedMinutesUpdated
    {
        add { Cache.Updated += value; _otherAccountsUpdated += value; }
        remove { Cache.Updated -= value; _otherAccountsUpdated -= value; }
    }

    /// <summary>Another account's reading changed (an added account was fetched).</summary>
    internal static void RaiseOtherAccountsUpdated()
    {
        try { _otherAccountsUpdated?.Invoke(); }
        catch { /* listeners only request a best-effort derived refresh */ }
    }

    private static async Task<EpicPlaytimeFetchResult> FetchForCacheAsync(CancellationToken ct)
    {
        var scope = GetActiveAccountScope();
        if (string.IsNullOrWhiteSpace(scope)) return EpicPlaytimeFetchResult.Failed;

        try
        {
            var session = await ResolveSessionAsync(ct).ConfigureAwait(false);
            if (session is null) return EpicPlaytimeFetchResult.Failed;

            var minutes = await FetchMinutesAsync(session, ct).ConfigureAwait(false);
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
    /// Exo's own Epic session, read from Legendary's user.json. An expiring token
    /// is refreshed by Legendary itself (<c>legendary status --json</c>), which
    /// saves the new one; Exo never spends a refresh token, Legendary's or the
    /// Epic Games Launcher's, because Epic retires it once used.
    /// </summary>
    internal static async Task<Session?> ResolveSessionAsync(CancellationToken ct)
    {
        var userPath = ResolveLegendaryUserPath();
        if (userPath is null) return null;
        try
        {
            var session = ParseSessionJson(await ReadSharedAsync(userPath, ct).ConfigureAwait(false));
            if (session is null || !AccessTokenNeedsRefresh(session, DateTimeOffset.UtcNow) ||
                EpicAdapter.ResolveLegendary() is not { } legendary)
                return session;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                await CliRunner.RunJsonAsync(legendary, LegendaryCli.StatusJsonArgs(), null, timeout.Token,
                        EpicAccounts.EnvironmentFor(Path.GetDirectoryName(userPath)!))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                AppLog.Debug("Epic session refresh through Legendary: " + ex.GetType().Name);
            }
            return ParseSessionJson(await ReadSharedAsync(userPath, ct).ConfigureAwait(false)) ?? session;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A locked or unreadable user.json reads as no session this round.
            return null;
        }
    }

    private static async Task<string> ReadSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
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

            // Account playtime must not be attached to a game whose active
            // account is unknown or does not own it. Machine install and any
            // Exo-tracked sessions are left alone.
            if (game.EntitlementState is EntitlementState.Unverified or EntitlementState.NotOwned)
                return game;

            if (!TryMinutesForEpicGame(game, minutesByArtifact, out var minutes) ||
                minutes <= 0 || game.PlaytimeMinutes >= minutes)
                return game;

            return game.WithPlaytime(minutes, game.LastPlayedUtc);
        }).ToList();
    }

    /// <summary>Epic's own reading for <paramref name="game"/>, its modes included; 0 when it has none.</summary>
    internal static int MinutesFor(GameEntry game, IReadOnlyDictionary<string, int> minutesByArtifact) =>
        TryMinutesForEpicGame(game, minutesByArtifact, out var minutes) ? minutes : 0;

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
        // A game's modes are separate rows on Epic's side; their hours are the game's too.
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget) && Modes.TryGetValue(game.LaunchTarget.Trim(), out var modes))
            foreach (var mode in modes)
                if (minutesByArtifact.TryGetValue(mode, out var value))
                    minutes = (int)Math.Min(int.MaxValue, (long)minutes + value);

        return minutes > 0;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>> Modes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The artifacts of a game's modes (folded into its card), whose hours count for it.</summary>
    internal static void RegisterModes(string mainArtifact, IReadOnlyList<string> modeArtifacts) => Modes[mainArtifact] = modeArtifacts;

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
        foreach (var key in new[] { game.LaunchTarget, game.Id })
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var slash = key.LastIndexOf('_');
            var start = key.StartsWith("epic:", StringComparison.OrdinalIgnoreCase) ? 5 : 0;
            if (slash <= start || slash >= key.Length - 1) continue;
            var stem = key[start..slash].Trim();
            if (stem.Length > 0) yield return stem;
        }
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

    /// <summary>
    /// Legendary's own user.json holds a refresh token that has not expired, so
    /// Legendary can renew the session by itself. A local read: the old check
    /// ran a full <c>legendary list</c>, which a large library could not finish
    /// in time and so looked signed out.
    /// </summary>
    internal static bool HasUnexpiredLegendarySession(DateTimeOffset now)
    {
        var path = ResolveLegendaryUserPath();
        if (path is null) return false;
        try
        {
            var json = File.ReadAllText(path);
            if (ParseSessionJson(json) is not { RefreshToken: { Length: > 0 } }) return false;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("refresh_expires_at", out var value) &&
                   value.ValueKind == JsonValueKind.String &&
                   DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind, out var expires) &&
                   expires > now.AddMinutes(5);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
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
        var http = ExoHttp.Create(TimeSpan.FromSeconds(8), new HttpClientHandler
        {
            // Never forward the bearer session to a redirected host.
            AllowAutoRedirect = false,
        });
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        return http;
    }

    private static IReadOnlyDictionary<string, int> Empty() =>
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static string PersistPath =>
        Path.Combine(PathHelper.AppDataDir, "epic-playtime.json");

    private static readonly object PersistGate = new();
    private const int MaxPersistedScopes = 16;

    /// <summary>
    /// Keeps each Epic account's last reading under its one-way scope, so a
    /// reading survives switching accounts. Only hashed scopes are written.
    /// </summary>
    internal static void PersistMinutes(string scope, IReadOnlyDictionary<string, int> minutes)
    {
        if (string.IsNullOrWhiteSpace(scope) || minutes.Count == 0) return;
        try
        {
            var positive = minutes
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (positive.Count == 0) return;
            lock (PersistGate)
            {
                Directory.CreateDirectory(PathHelper.AppDataDir);
                var scopes = ReadPersistedScopes();
                scopes.Remove(scope);
                // Newest last: the oldest reading goes first when the file is full.
                while (scopes.Count >= MaxPersistedScopes) scopes.Remove(scopes.Keys.First());
                scopes[scope] = positive;
                var json = JsonSerializer.Serialize(new PersistedPlaytimeStore(scopes));
                var tmp = PersistPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, PersistPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Epic playtime persist failed: " + ex.GetType().Name);
        }
    }

    internal static IReadOnlyDictionary<string, int> LoadPersistedMinutes(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return Empty();
        lock (PersistGate)
            return ReadPersistedScopes().TryGetValue(scope, out var minutes) ? minutes : Empty();
    }

    /// <summary>Every account's last reading on this PC, by one-way scope.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> LoadAllPersistedMinutes()
    {
        lock (PersistGate)
            return ReadPersistedScopes().ToDictionary(
                pair => pair.Key, pair => (IReadOnlyDictionary<string, int>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>Forgets one account's reading (an added account was removed).</summary>
    internal static void ForgetPersistedMinutes(string scope)
    {
        try
        {
            lock (PersistGate)
            {
                var scopes = ReadPersistedScopes();
                if (!scopes.Remove(scope)) return;
                var tmp = PersistPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new PersistedPlaytimeStore(scopes)));
                File.Move(tmp, PersistPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Epic playtime forget failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Reads both the per-account file and the older single-account one ({Scope, Minutes}).</summary>
    internal static Dictionary<string, Dictionary<string, int>> ParsePersistedScopes(string json)
    {
        var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;
            void Add(string? scope, JsonElement minutes)
            {
                if (string.IsNullOrWhiteSpace(scope) || scope.Length > 64 || minutes.ValueKind != JsonValueKind.Object) return;
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in minutes.EnumerateObject())
                    if (row.Name.Length is > 0 and <= 256 && row.Value.TryGetInt32(out var value) && value > 0)
                        map[row.Name] = value;
                if (map.Count > 0) result[scope] = map;
            }
            if (root.TryGetProperty("Scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Object)
                foreach (var scope in scopes.EnumerateObject()) Add(scope.Name, scope.Value);
            else if (root.TryGetProperty("Scope", out var single) && single.ValueKind == JsonValueKind.String &&
                     root.TryGetProperty("Minutes", out var legacy))
                Add(single.GetString(), legacy);
        }
        catch (JsonException) { /* unreadable: start over */ }
        return result;
    }

    private static Dictionary<string, Dictionary<string, int>> ReadPersistedScopes()
    {
        try
        {
            return File.Exists(PersistPath)
                ? ParsePersistedScopes(File.ReadAllText(PersistPath))
                : new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Epic playtime persist load failed: " + ex.GetType().Name);
            return new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        }
    }

    private sealed record PersistedPlaytimeStore(Dictionary<string, Dictionary<string, int>> Scopes);
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
