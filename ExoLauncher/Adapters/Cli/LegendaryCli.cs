using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Models;

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
        bool Installed);

    public static string[] AuthArgs() => ["auth"];

    public static string[] ListInstalledArgs(bool json = true) =>
        json ? ["list-installed", "--json"] : ["list-installed"];

    public static string[] ListOwnedArgs(bool json = true) =>
        json ? ["list", "--json"] : ["list"];

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

    public static string[] LaunchArgs(string appName) =>
        ["launch", appName, "--skip-version-check"];

    public static string[] UninstallArgs(string appName) => ["uninstall", appName, "-y"];

    /// <summary>
    /// Parse a single Legendary DLManager progress line.
    /// Sample: "[DLManager] INFO: = Progress: 45.23%, Running for 00:02:15, ETA: 00:03:00"
    /// Sample speed: "[DLManager] INFO:  - Download	- 12.34 MiB/s (raw)"
    /// </summary>
    public static bool TryParseProgressLine(string line, out double? percent, out double? bytesPerSecond, out string status)
    {
        percent = null;
        bytesPerSecond = null;
        status = line.Trim();

        if (string.IsNullOrWhiteSpace(line))
            return false;

        var pctMatch = ProgressPercentRegex().Match(line);
        if (pctMatch.Success &&
            double.TryParse(pctMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
        {
            percent = Math.Clamp(p, 0, 100);
        }

        var speedMatch = SpeedRegex().Match(line);
        if (speedMatch.Success &&
            double.TryParse(speedMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) &&
            speedMatch.Groups[2].Success)
        {
            var unit = speedMatch.Groups[2].Value.ToLowerInvariant();
            bytesPerSecond = unit switch
            {
                "kib" or "kb" => speed * 1024,
                "mib" or "mb" => speed * 1024 * 1024,
                "gib" or "gb" => speed * 1024 * 1024 * 1024,
                "b" => speed,
                _ => speed * 1024 * 1024,
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

    /// <summary>
    /// Merge owned + installed rows: installed wins path/size; uninstalled owned stay installable.
    /// </summary>
    public static IReadOnlyList<GameRow> MergeOwnedAndInstalled(
        IEnumerable<GameRow> owned,
        IEnumerable<GameRow> installed)
    {
        var map = new Dictionary<string, GameRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in owned)
        {
            map[o.AppName] = o with { Installed = false, InstallPath = null };
        }
        foreach (var i in installed)
        {
            map[i.AppName] = i with { Installed = true };
        }
        return map.Values.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
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
            if (el.TryGetProperty("install_size", out var s) && s.TryGetInt64(out var sv)) size = sv;
            else if (el.TryGetProperty("installSize", out var s2) && s2.TryGetInt64(out var sv2)) size = sv2;

            var installed = forceInstalled
                || (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath));

            return new GameRow(appName!, title!, installPath, size, installed);
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"Progress:\s*([0-9]+(?:\.[0-9]+)?)\s*%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgressPercentRegex();

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*(KiB|MiB|GiB|KB|MB|GB|B)/s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeedRegex();
}
