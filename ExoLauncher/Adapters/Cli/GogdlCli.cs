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
    public sealed record OwnedGame(string Id, string Title, string? InstallPath, bool Installed);

    public static string[] AuthArgs() => ["auth"];

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

    public static string[] LaunchArgs(string gameId, string path) =>
        ["launch", "--path", path, gameId];

    public static string[] InfoArgs(string gameId) => ["info", gameId];

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
        return new InstallProgress
        {
            GameId = gameId,
            Phase = phase,
            Percent = pct,
            BytesPerSecond = bps,
            Status = string.IsNullOrWhiteSpace(status) ? phase.ToString() : status,
            CanCancel = true,
        };
    }

    /// <summary>
    /// Parse owned-library JSON (Heroic gog_store/library.json, Exo cache, or GOG products array).
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
            map[i.Id] = i with { Installed = true };
        return map.Values.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static OwnedGame? MapOwned(JsonElement el)
    {
        try
        {
            if (el.ValueKind != JsonValueKind.Object) return null;

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

            return new OwnedGame(id!, title!, path, installed);
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
}
