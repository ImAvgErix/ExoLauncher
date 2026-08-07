using System.Globalization;
using System.Text.RegularExpressions;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// Pure helpers for heroic-gogdl argv and progress lines.
/// https://github.com/Heroic-Games-Launcher/heroic-gogdl
/// </summary>
public static partial class GogdlCli
{
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

    [GeneratedRegex(@"(?:Progress:\s*)?\[?([0-9]+(?:\.[0-9]+)?)\s*%\]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*(KiB|MiB|GiB|KB|MB|GB)/s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeedRegex();
}
