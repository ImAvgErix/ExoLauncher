using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// Pure helpers for Legendary argv, library JSON, and stdout progress.
/// Tests drive these without network or a live binary.
/// </summary>
public static partial class LegendaryCli
{
    public sealed record GameRow(
        string AppName,
        string Title,
        string? InstallPath,
        long? SizeBytes,
        bool Installed)
    {
        /// <summary>Catalog categories, when Legendary exposes them.</summary>
        public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
        /// <summary>Official tall key art from the Legendary catalog row.</summary>
        public string? CoverUrl { get; init; }
        /// <summary>False for an owned title with no Windows download on Epic (activated on another store).</summary>
        public bool Installable { get; init; } = true;
        /// <summary>The store that runs this title (ThirdPartyManagedApp), when Epic names one.</summary>
        public string? ThirdPartyStore { get; init; }
        /// <summary>An install's build version and executable (relative to its folder), from installed.json.</summary>
        public string? Version { get; init; }
        public string? Executable { get; init; }
        /// <summary>When the installed.json this row came from last changed.</summary>
        public DateTime ChangedUtc { get; init; }
    }

    /// <summary>
    /// Official Legendary / Heroic Epic Games client id. Used only to open Epic's
    /// documented authorization-code redirect; Exo never stores the token.
    /// </summary>
    public const string AuthorizationClientId = "34a02cf8f4414e29b15921876da36f9a";

    public static readonly string LoginUrl =
        "https://www.epicgames.com/id/login?redirectUrl=" +
        Uri.EscapeDataString(
            "https://www.epicgames.com/id/api/redirect?clientId=" +
            AuthorizationClientId +
            "&responseType=code");

    public static string[] AuthCodeArgs(string authorizationCode)
    {
        if (!IsAuthorizationCode(authorizationCode))
            throw new ArgumentException("Epic authorization code is invalid.", nameof(authorizationCode));
        return ["auth", "--code", authorizationCode];
    }

    /// <summary>Legendary's own sign-out.</summary>
    public static string[] AuthDeleteArgs() => ["auth", "--delete"];

    /// <summary>Imports the Epic Games Launcher's sign-in (Legendary 0.21: "logs out of EGL").</summary>
    public static string[] AuthImportArgs() => ["auth", "--import"];

    /// <summary>Signs in with the saved session (refreshing and saving it) and reports status; never interactive.</summary>
    public static string[] StatusJsonArgs() => ["status", "--json"];

    public static bool IsAuthorizationResultUri(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            (!uri.Host.Equals("www.epicgames.com", StringComparison.OrdinalIgnoreCase)
             && !uri.Host.Equals("epicgames.com", StringComparison.OrdinalIgnoreCase)) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.AbsolutePath.Equals("/id/api/redirect", StringComparison.OrdinalIgnoreCase))
            return false;

        string? clientId = null;
        string? responseType = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(separator >= 0 ? pair[..separator] : pair);
            var value = separator >= 0 ? Uri.UnescapeDataString(pair[(separator + 1)..]) : string.Empty;
            if (name.Equals("clientId", StringComparison.OrdinalIgnoreCase)) clientId = value;
            if (name.Equals("responseType", StringComparison.OrdinalIgnoreCase)) responseType = value;
        }

        if (clientId is not null &&
            !clientId.Equals(AuthorizationClientId, StringComparison.Ordinal))
            return false;
        if (responseType is not null &&
            !responseType.Equals("code", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public static bool TryParseAuthorizationCode(string? payload, out string authorizationCode)
    {
        authorizationCode = string.Empty;
        if (string.IsNullOrWhiteSpace(payload)) return false;

        var trimmed = payload.Trim();
        if (IsAuthorizationCode(trimmed))
        {
            authorizationCode = trimmed;
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("authorizationCode", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.String)
                return false;

            var code = codeElement.GetString();
            if (!IsAuthorizationCode(code)) return false;
            authorizationCode = code!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsAuthorizationCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length is >= 8 and <= 128
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string[] ListInstalledArgs(bool json = true) =>
        json ? ["list-installed", "--json"] : ["list-installed"];

    /// <summary>
    /// <c>--include-non-installable</c> (Legendary 0.21.0, also <c>-T</c>) adds
    /// owned titles with no Epic download, such as ones activated on the EA app.
    /// The session probe leaves it off: it only needs any answer.
    /// </summary>
    public static string[] ListOwnedArgs(bool includeNonInstallable = true) =>
        includeNonInstallable ? ["list", "--json", "--include-non-installable"] : ["list", "--json"];

    /// <summary>
    /// A successful <c>legendary list --json</c> response is the session probe.
    /// Requiring both exit code zero and the expected JSON collection shape avoids
    /// treating a cancelled login or a textual error as authenticated.
    /// </summary>
    public static bool IsAuthenticatedLibraryResponse(int exitCode, string stdout)
    {
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return false;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind == JsonValueKind.Array) return true;
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            if (doc.RootElement.TryGetProperty("games", out var games))
                return games.ValueKind == JsonValueKind.Array;

            // Older Legendary builds may emit an object keyed by app name. An
            // empty or arbitrary object is not enough evidence of a session.
            var properties = doc.RootElement.EnumerateObject().ToList();
            return properties.Count > 0 && properties.All(
                property => property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string[] InstallArgs(string appName, string? basePath = null)
    {
        var args = new List<string> { "install", appName, "-y" };
        if (!string.IsNullOrWhiteSpace(basePath))
        {
            args.Add("--base-path");
            args.Add(basePath);
        }
        return args.ToArray();
    }

    public static string[] UpdateArgs(string appName) =>
        ["install", appName, "-y", "--update-only"];

    public static string[] LaunchArgs(string appName, string? extraArgs = null)
    {
        var args = new List<string> { "launch", appName, "--skip-version-check" };
        foreach (var extra in SplitExtraArgs(extraArgs))
        {
            if (args.Count == 3) args.Add("--");
            args.Add(extra);
        }

        return args.ToArray();
    }

    public static string[] UninstallArgs(string appName) => ["uninstall", appName, "-y"];

    public static string[] RepairArgs(string appName) => ["install", appName, "-y", "--repair"];

    public static string[] EglImportOnlyArgs() => ["egl-sync", "--one-shot", "--import-only"];

    /// <summary>Registers an existing install (an Epic Games Launcher folder) with Legendary, without downloading it.</summary>
    public static string[] ImportArgs(string appName, string path) => ["import", appName, path];

    /// <summary>
    /// Legendary refused because it does not track this install: "Update requested for X, but app not installed!", or
    /// "Game X not installed, cannot uninstall!", which it says with exit code 0.
    /// </summary>
    public static bool NotInstalled(string? stderr) =>
        stderr is not null && (stderr.Contains("but app not installed", StringComparison.OrdinalIgnoreCase) ||
                               stderr.Contains("not installed, cannot uninstall", StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<string> SplitExtraArgs(string? extraArgs)
        => WindowsCommandLine.Parse(extraArgs);

    /// <summary>One Legendary DLManager line as Exo progress; <paramref name="status"/> is Exo's own wording.</summary>
    public static InstallProgress ToProgress(string gameId, string line, string status, InstallPhase phase = InstallPhase.Downloading)
    {
        TryParseBytePair(line, out var downloaded, out var toDownload);
        return new InstallProgress
        {
            GameId = gameId,
            Phase = phase,
            Percent = CliProgress.Percent(line),
            BytesPerSecond = CliProgress.DownloadBytesPerSecond(line),
            BytesDownloaded = downloaded,
            BytesToDownload = toDownload,
            Status = status,
            CanCancel = true,
        };
    }

    /// <summary>
    /// Parse <c>legendary list --json</c> or <c>list-installed --json</c> output.
    /// When <paramref name="forceInstalled"/> is true, rows are treated as installed.
    /// </summary>
    public static IReadOnlyList<GameRow> ParseLibraryJson(string json, bool forceInstalled)
    {
        var rows = new List<GameRow>();
        if (string.IsNullOrWhiteSpace(json)) return rows;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var row = MapRow(el, key: null, forceInstalled);
                if (row is not null) rows.Add(row);
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            // Object map keyed by app name, or a wrapper with "games"/"installed".
            if (doc.RootElement.TryGetProperty("games", out var gamesEl) && gamesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in gamesEl.EnumerateArray())
                {
                    var row = MapRow(el, null, forceInstalled);
                    if (row is not null) rows.Add(row);
                }
            }
            else
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        var row = MapRow(prop.Value, prop.Name, forceInstalled);
                        if (row is not null) rows.Add(row);
                    }
                }
            }
        }

        return rows;
    }

    private static GameRow? MapRow(JsonElement el, string? key, bool forceInstalled)
    {
        try
        {
            if (el.ValueKind != JsonValueKind.Object) return null;

            var appName = el.TryGetProperty("app_name", out var a) ? a.GetString()
                : el.TryGetProperty("appName", out var a2) ? a2.GetString()
                : el.TryGetProperty("AppName", out var a3) ? a3.GetString()
                : key;
            var title = el.TryGetProperty("title", out var t) ? t.GetString()
                : el.TryGetProperty("app_title", out var t2) ? t2.GetString()
                : el.TryGetProperty("AppTitle", out var t3) ? t3.GetString()
                : appName;
            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(title))
                return null;

            string? installPath = null;
            if (el.TryGetProperty("install_path", out var p)) installPath = p.GetString();
            else if (el.TryGetProperty("installPath", out var p2)) installPath = p2.GetString();

            long? size = null;
            // Owned-library rows legitimately carry a JSON null here. Calling
            // TryGetInt64 on a non-number throws; MapRow then swallowed that and
            // dropped the entire entitlement (including Rocket League / Sugar).
            if (el.TryGetProperty("install_size", out var s) &&
                s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sv))
                size = sv;
            else if (el.TryGetProperty("installSize", out var s2) &&
                     s2.ValueKind == JsonValueKind.Number && s2.TryGetInt64(out var sv2))
                size = sv2;

            var installed = forceInstalled
                || (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath));

            return new GameRow(appName!, title!, installPath, size, installed)
            {
                Categories = ReadCategories(el),
                CoverUrl = ReadTallKeyImage(el),
                Installable = installed || ReadInstallable(el),
                ThirdPartyStore = ReadThirdPartyStore(el),
                Version = el.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null,
                Executable = el.TryGetProperty("executable", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadCategories(JsonElement el)
    {
        JsonElement source = el;
        if (el.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            source = metadata;
        if (!source.TryGetProperty("categories", out var categories) ||
            categories.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var category in categories.EnumerateArray())
        {
            var value = category.ValueKind == JsonValueKind.String ? category.GetString()
                : category.ValueKind == JsonValueKind.Object && category.TryGetProperty("path", out var path)
                    ? path.GetString()
                    : null;
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
        }
        // Epic marks DLC and add-ons with mainGameItem (the base game). Tag
        // them so the games-only owned shelf drops them; an edition SKU
        // (Deluxe, GOTY) also names its base game but is itself in "games".
        if (source.TryGetProperty("mainGameItem", out var mainGame) &&
            mainGame.ValueKind == JsonValueKind.Object &&
            !result.Contains("addons", StringComparer.OrdinalIgnoreCase) &&
            !result.Contains("games", StringComparer.OrdinalIgnoreCase))
            result.Add("addons");
        return result;
    }

    /// <summary>
    /// Exo's cache stores <c>installable</c>; Legendary's own row has
    /// <c>asset_infos</c>, which lacks "Windows" for a title Epic does not
    /// deliver. A row with neither is installable, as before.
    /// </summary>
    private static bool ReadInstallable(JsonElement el)
    {
        if (el.TryGetProperty("installable", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return flag.GetBoolean();
        return !el.TryGetProperty("asset_infos", out var assets) ||
               assets.ValueKind != JsonValueKind.Object ||
               assets.TryGetProperty("Windows", out _);
    }

    private static string? ReadThirdPartyStore(JsonElement el)
    {
        if (el.TryGetProperty("third_party_store", out var cached) && cached.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(cached.GetString()) ? null : cached.GetString()!.Trim();
        if (el.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("customAttributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object &&
            attributes.TryGetProperty("ThirdPartyManagedApp", out var app) && app.ValueKind == JsonValueKind.Object &&
            app.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!.Trim();
        return null;
    }

    private static string? ReadTallKeyImage(JsonElement el)
    {
        if (!el.TryGetProperty("keyImages", out var images) &&
            !(el.TryGetProperty("metadata", out var metadata) &&
              metadata.ValueKind == JsonValueKind.Object &&
              metadata.TryGetProperty("keyImages", out images)))
            return null;
        if (images.ValueKind != JsonValueKind.Array) return null;
        foreach (var image in images.EnumerateArray())
        {
            var type = image.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            var url = image.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(url) ||
                !type.Contains("Tall", StringComparison.OrdinalIgnoreCase))
                continue;
            if (CoverArtService.IsOfficialEpicPortraitCdn(url))
                return url;
        }
        return null;
    }

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
        var scale = ByteUnitScale(match.Groups[3].Value);
        var down = (long)Math.Round(done * scale);
        var to = (long)Math.Round(total * scale);
        if (down <= 0 || to <= 0) return false;
        downloaded = down;
        toDownload = to;
        return true;
    }

    private static double ByteUnitScale(string unit) => unit.ToLowerInvariant() switch
    {
        "kib" or "kb" => 1024,
        "mib" or "mb" => 1024 * 1024,
        "gib" or "gb" => 1024d * 1024 * 1024,
        _ => 1,
    };
}
