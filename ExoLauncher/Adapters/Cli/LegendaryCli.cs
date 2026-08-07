using System.Globalization;
using System.Text.RegularExpressions;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// Pure helpers for Legendary argv and stdout progress.
/// Tests drive these without network or a live binary.
/// </summary>
public static partial class LegendaryCli
{
    public static string[] AuthArgs() => ["auth"];

    public static string[] ListInstalledArgs(bool json = true) =>
        json ? ["list-installed", "--json"] : ["list-installed"];

    public static string[] ListOwnedArgs(bool json = true) =>
        json ? ["list", "--json"] : ["list"];

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

    public static string[] LaunchArgs(string appName) => ["launch", appName];

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

    [GeneratedRegex(@"Progress:\s*([0-9]+(?:\.[0-9]+)?)\s*%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgressPercentRegex();

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*(KiB|MiB|GiB|KB|MB|GB|B)/s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeedRegex();
}
