using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// Pure helpers for heroic-gogdl argv, owned-library JSON, and progress lines.
/// https://github.com/Heroic-Games-Launcher/heroic-gogdl
/// gogdl itself has no stable "list owned" CLI — library JSON comes from Heroic cache,
/// ExoLauncher cache, or a GOG API dump written after auth.
/// </summary>
public static partial class GogdlCli
{
    public const string LoginUrl =
        "https://auth.gog.com/auth?client_id=46899977096215655&redirect_uri=https%3A%2F%2Fembed.gog.com%2Fon_login_success%3Forigin%3Dclient&response_type=code&layout=galaxy";

    public sealed record OwnedGame(
        string Id,
        string Title,
        string? InstallPath,
        bool Installed,
        string? CoverUrl = null);

    /// <summary>
    /// Credentials emitted by heroic-gogdl. The on-disk file is normally keyed
    /// by GOG client id, but Heroic and older gogdl builds have also wrapped the
    /// payload in client / credentials / token objects.
    /// </summary>
    public sealed record AuthCredentials(
        string AccessToken,
        string UserId,
        string? RefreshToken,
        DateTimeOffset? ExpiresAtUtc)
    {
        public bool IsExpired(DateTimeOffset now, TimeSpan? clockSkew = null) =>
            ExpiresAtUtc is { } expires && expires <= now + (clockSkew ?? TimeSpan.FromMinutes(2));
    }

    public static string[] AuthArgs() => ["auth"];

    public static string[] AuthStatusArgs(string authConfigPath) =>
        WithAuthConfig(authConfigPath, AuthArgs());

    public static string[] AuthCodeArgs(string authConfigPath, string authorizationCode) =>
        WithAuthConfig(authConfigPath, ["auth", "--code", authorizationCode]);

    public static string[] WithAuthConfig(string authConfigPath, IReadOnlyList<string> commandArgs)
    {
        if (string.IsNullOrWhiteSpace(authConfigPath))
            throw new ArgumentException("GOG auth config path is required.", nameof(authConfigPath));
        return ["--auth-config-path", authConfigPath, .. commandArgs];
    }

    public static bool TryExtractAuthorizationCode(string? callbackUrl, out string authorizationCode)
    {
        authorizationCode = string.Empty;
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("embed.gog.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.AbsolutePath.TrimEnd('/').Equals("/on_login_success", StringComparison.OrdinalIgnoreCase))
            return false;

        string? origin = null;
        string? code = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = System.Net.WebUtility.UrlDecode(separator >= 0 ? pair[..separator] : pair);
            var rawValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            var decoded = System.Net.WebUtility.UrlDecode(rawValue);
            if (string.Equals(name, "origin", StringComparison.OrdinalIgnoreCase)) origin = decoded;
            if (string.Equals(name, "code", StringComparison.OrdinalIgnoreCase)) code = decoded;
        }

        if (!string.Equals(origin, "client", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(code))
            return false;
        authorizationCode = code;
        return true;
    }

    public static bool HasAuthenticatedCredentials(string? json)
    {
        return TryReadCredentials(json, out var credentials) &&
               !string.IsNullOrWhiteSpace(credentials.RefreshToken);
    }

    /// <summary>
    /// Recursively finds the first usable token payload without ever returning
    /// or logging the source JSON. Traversal is bounded to reject pathological
    /// config files and supports snake_case and camelCase gogdl variants.
    /// </summary>
    public static bool TryReadCredentials(string? json, out AuthCredentials credentials)
    {
        credentials = null!;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            return TryFindCredentials(doc.RootElement, null, null, null, null, 0, out credentials);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindCredentials(
        JsonElement value,
        string? inheritedUserId,
        string? inheritedRefreshToken,
        double? inheritedLoginTime,
        double? inheritedExpiresIn,
        int depth,
        out AuthCredentials credentials)
    {
        credentials = null!;
        if (depth > 12) return false;

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                if (TryFindCredentials(
                        child,
                        inheritedUserId,
                        inheritedRefreshToken,
                        inheritedLoginTime,
                        inheritedExpiresIn,
                        depth + 1,
                        out credentials))
                    return true;
            }
            return false;
        }

        if (value.ValueKind != JsonValueKind.Object) return false;
        if (TryGetBoolean(value, "error", out var isError) && isError) return false;

        var userId = GetString(value, "user_id", "userId", "account_id", "accountId")
                     ?? inheritedUserId;
        var refreshToken = GetString(value, "refresh_token", "refreshToken")
                           ?? inheritedRefreshToken;
        var loginTime = GetNumber(value, "loginTime", "login_time") ?? inheritedLoginTime;
        var expiresIn = GetNumber(value, "expires_in", "expiresIn") ?? inheritedExpiresIn;
        var accessToken = GetString(value, "access_token", "accessToken");

        // Some wrappers use { user_id, refresh_token, token: "..." }.
        if (string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(userId))
            accessToken = GetString(value, "token");

        if (!string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(userId))
        {
            DateTimeOffset? expiresAtUtc = null;
            var explicitExpiry = GetNumber(value, "expires_at", "expiresAt");
            if (explicitExpiry is > 0)
                expiresAtUtc = FromUnixTimeSecondsOrNull(explicitExpiry.Value);
            else if (loginTime is > 0 && expiresIn is > 0)
                expiresAtUtc = FromUnixTimeSecondsOrNull(loginTime.Value + expiresIn.Value);

            credentials = new AuthCredentials(accessToken!, userId!, refreshToken, expiresAtUtc);
            return true;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                continue;
            if (TryFindCredentials(
                    property.Value,
                    userId,
                    refreshToken,
                    loginTime,
                    expiresIn,
                    depth + 1,
                    out credentials))
                return true;
        }
        return false;
    }

    private static string? GetString(JsonElement value, params string[] names)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String)
                continue;
            var text = property.Value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    private static double? GetNumber(JsonElement value, params string[] names)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number))
                return number;
            if (property.Value.ValueKind == JsonValueKind.String &&
                double.TryParse(property.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return number;
        }
        return null;
    }

    private static bool TryGetBoolean(JsonElement value, string name, out bool result)
    {
        result = false;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.True) { result = true; return true; }
            if (property.Value.ValueKind == JsonValueKind.False) { result = false; return true; }
        }
        return false;
    }

    private static DateTimeOffset? FromUnixTimeSecondsOrNull(double seconds)
    {
        if (!double.IsFinite(seconds) ||
            seconds < -62_135_596_800d ||
            seconds > 253_402_300_799d)
            return null;
        return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
    }

    public static string[] ImportArgs(string path) => ["import", path];

    /// <summary>Download / install an owned GOG title by id into platform path.</summary>
    public static string[] DownloadArgs(string gameId, string path, string platform = "windows")
    {
        return
        [
            "download", gameId,
            "--platform", platform,
            "--path", path,
        ];
    }

    public static string[] RepairArgs(string gameId, string path, string platform = "windows") =>
    [
        "repair", gameId,
        "--platform", platform,
        "--path", path,
    ];

    public static string[] LaunchArgs(string gameId, string path, string platform = "windows", string? extraArgs = null)
    {
        var args = new List<string> { "launch", path, gameId, "--platform", platform };
        if (!string.IsNullOrWhiteSpace(extraArgs))
        {
            args.Add("--");
            args.AddRange(LegendaryCli.SplitExtraArgs(extraArgs));
        }

        return args.ToArray();
    }

    public static string[] InfoArgs(string gameId) => ["info", gameId];

    internal static string HeroicLibraryCachePath(string roamingAppData) =>
        Path.Combine(roamingAppData, "heroic", "store_cache", "gog_library.json");

    /// <summary>
    /// gogdl / aria-style progress, e.g. "Progress: 12.5%" or "[12%] downloading".
    /// </summary>
    public static bool TryParseProgressLine(string line, out double? percent, out double? bytesPerSecond, out string status)
    {
        percent = null;
        bytesPerSecond = null;
        status = line.Trim();
        if (string.IsNullOrWhiteSpace(line)) return false;

        var pct = PercentRegex().Match(line);
        if (pct.Success &&
            double.TryParse(pct.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            percent = Math.Clamp(p, 0, 100);

        var speed = SpeedRegex().Match(line);
        if (speed.Success &&
            double.TryParse(speed.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            var unit = speed.Groups[2].Success ? speed.Groups[2].Value.ToLowerInvariant() : "mib";
            bytesPerSecond = unit switch
            {
                "kib" or "kb" => s * 1024,
                "mib" or "mb" => s * 1024 * 1024,
                "gib" or "gb" => s * 1024 * 1024 * 1024,
                _ => s * 1024 * 1024,
            };
        }

        return percent is not null || bytesPerSecond is not null;
    }

    public static InstallProgress ToProgress(string gameId, string line, InstallPhase phase = InstallPhase.Downloading)
    {
        TryParseProgressLine(line, out var pct, out var bps, out var status);
        TryParseBytePair(line, out var downloaded, out var toDownload);
        return new InstallProgress
        {
            GameId = gameId,
            Phase = phase,
            Percent = pct,
            BytesPerSecond = bps,
            BytesDownloaded = downloaded,
            BytesToDownload = toDownload,
            Status = string.IsNullOrWhiteSpace(status) ? phase.ToString() : status,
            CanCancel = true,
        };
    }

    /// <summary>
    /// Parse owned-library JSON (Heroic store_cache/gog_library.json, legacy gog_store/library.json,
    /// Exo cache, or GOG products array).
    /// Accepts array of objects or <c>{ "games": [...] }</c> / <c>{ "products": [...] }</c>.
    /// </summary>
    public static IReadOnlyList<OwnedGame> ParseOwnedLibraryJson(string json)
    {
        var list = new List<OwnedGame>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        IEnumerable<JsonElement> items = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray(),
            JsonValueKind.Object when root.TryGetProperty("games", out var g) && g.ValueKind == JsonValueKind.Array
                => g.EnumerateArray(),
            JsonValueKind.Object when root.TryGetProperty("products", out var p) && p.ValueKind == JsonValueKind.Array
                => p.EnumerateArray(),
            JsonValueKind.Object when root.TryGetProperty("library", out var lib) && lib.ValueKind == JsonValueKind.Array
                => lib.EnumerateArray(),
            _ => Array.Empty<JsonElement>(),
        };

        foreach (var el in items)
        {
            var row = MapOwned(el);
            if (row is not null) list.Add(row);
        }

        return list;
    }

    /// <summary>Merge owned (not necessarily installed) with registry/installed rows by GOG id.</summary>
    public static IReadOnlyList<OwnedGame> MergeOwnedAndInstalled(
        IEnumerable<OwnedGame> owned,
        IEnumerable<OwnedGame> installed)
    {
        var map = new Dictionary<string, OwnedGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in owned)
            map[o.Id] = o with { Installed = false, InstallPath = null };
        foreach (var i in installed)
        {
            map.TryGetValue(i.Id, out var ownedRow);
            map[i.Id] = i with
            {
                Installed = true,
                CoverUrl = i.CoverUrl ?? ownedRow?.CoverUrl,
            };
        }
        return map.Values.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static OwnedGame? MapOwned(JsonElement el)
    {
        try
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (el.TryGetProperty("visible", out var visible) &&
                visible.ValueKind == JsonValueKind.False)
                return null;

            string? id = null;
            if (el.TryGetProperty("id", out var idEl)) id = idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetRawText()
                : idEl.GetString();
            if (string.IsNullOrWhiteSpace(id) && el.TryGetProperty("app_name", out var an)) id = an.GetString();
            if (string.IsNullOrWhiteSpace(id) && el.TryGetProperty("game_id", out var gid)) id = gid.GetString();
            if (string.IsNullOrWhiteSpace(id) && el.TryGetProperty("productId", out var pid))
                id = pid.ValueKind == JsonValueKind.Number ? pid.GetRawText() : pid.GetString();

            var title = el.TryGetProperty("title", out var t) ? t.GetString()
                : el.TryGetProperty("name", out var n) ? n.GetString()
                : el.TryGetProperty("gameName", out var gn) ? gn.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                return null;

            string? path = null;
            if (el.TryGetProperty("install_path", out var ip)) path = ip.GetString();
            else if (el.TryGetProperty("installPath", out var ip2)) path = ip2.GetString();
            else if (el.TryGetProperty("path", out var p)) path = p.GetString();

            var installed = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            if (el.TryGetProperty("is_installed", out var ii) && ii.ValueKind is JsonValueKind.True or JsonValueKind.False)
                installed = ii.GetBoolean();
            if (el.TryGetProperty("installed", out var inst) && inst.ValueKind is JsonValueKind.True or JsonValueKind.False)
                installed = inst.GetBoolean();

            var coverUrl = el.TryGetProperty("coverUrl", out var cover) && cover.ValueKind == JsonValueKind.String
                ? cover.GetString()
                : el.TryGetProperty("art_square", out var artSquare) && artSquare.ValueKind == JsonValueKind.String
                    ? artSquare.GetString()
                : el.TryGetProperty("art_cover", out var artCover) && artCover.ValueKind == JsonValueKind.String
                    ? artCover.GetString()
                : null;

            return new OwnedGame(id!, title!, path, installed, coverUrl);
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?:Progress:\s*)?\[?([0-9]+(?:\.[0-9]+)?)\s*%\]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*(KiB|MiB|GiB|KB|MB|GB)/s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeedRegex();

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*/\s*([0-9]+(?:\.[0-9]+)?)\s*(KiB|MiB|GiB|KB|MB|GB)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BytePairRegex();

    private static bool TryParseBytePair(string line, out long? downloaded, out long? toDownload)
    {
        downloaded = null;
        toDownload = null;
        var match = BytePairRegex().Match(line);
        if (!match.Success) return false;
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var done))
            return false;
        if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
            return false;
        var unit = match.Groups[3].Value.ToLowerInvariant();
        var scale = unit switch
        {
            "kib" or "kb" => 1024d,
            "mib" or "mb" => 1024d * 1024,
            "gib" or "gb" => 1024d * 1024 * 1024,
            _ => 1d,
        };
        var down = (long)Math.Round(done * scale);
        var to = (long)Math.Round(total * scale);
        if (down <= 0 || to <= 0) return false;
        downloaded = down;
        toDownload = to;
        return true;
    }
}
