using System.Globalization;
using System.Text.RegularExpressions;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// Reads the progress lines Legendary, gogdl and Nile share (one DLManager
/// lineage):
/// <code>
/// = Progress: 45.23% (8/1693), Running for 00:00:03, ETA: 00:11:50   (Legendary)
/// = Progress: 45.23 8/1693, Running for: 00:00:03, ETA: 00:11:50     (gogdl, Nile: no '%')
///  + Download - 1.51 MiB/s (raw) / 5.14 MiB/s (decompressed)
///  + Disk - 5.14 MiB/s (write) / 0.00 MiB/s (read)
/// </code>
/// The speed is the raw network figure on the Download line; the Disk line is
/// write speed and is ignored. The lines themselves (which name the install
/// path) never reach the UI.
/// </summary>
internal static partial class CliProgress
{
    internal static double? Percent(string line)
    {
        var match = PercentRegex().Match(line);
        return match.Success &&
               double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 100)
            : null;
    }

    internal static double? DownloadBytesPerSecond(string line)
    {
        var download = DownloadLineRegex().Match(line);
        if (!download.Success) return null;
        var speed = SpeedRegex().Match(line, download.Index + download.Length);
        if (!speed.Success ||
            !double.TryParse(speed.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        return value * speed.Groups[2].Value.ToLowerInvariant() switch
        {
            "kib" or "kb" => 1024d,
            "mib" or "mb" => 1024d * 1024,
            "gib" or "gb" => 1024d * 1024 * 1024,
            _ => 1d,
        };
    }

    [GeneratedRegex(@"Progress:\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    // " + Download\t- 1.51 MiB/s", never " - Downloaded: 4.53 MiB".
    [GeneratedRegex(@"[+-]\s*Download\b",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DownloadLineRegex();

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*(KiB|MiB|GiB|KB|MB|GB|B)/s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeedRegex();
}
