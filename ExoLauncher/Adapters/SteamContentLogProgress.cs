using System.Text.RegularExpressions;
using ExoLauncher.Adapters.Cli;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam's content_log records a job snapshot when an update starts:
/// download done/total and stage done/total. That line is not a live ticker —
/// Steam does not rewrite it as bytes move. Use it for the job size, then
/// prefer ACF counters or the downloading folder for how far the job has got.
/// </summary>
internal static class SteamContentLogProgress
{
    internal readonly record struct Job(
        long BytesDownloaded,
        long BytesToDownload,
        long BytesStaged,
        long BytesToStage);

    public static Job? TryReadLatest(string? steamRoot, string appId)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || !SteamProtocol.IsValidAppId(appId))
            return null;
        var path = Path.Combine(steamRoot, "logs", "content_log.txt");
        if (!File.Exists(path))
            return null;
        try
        {
            return TryParseLatest(ReadTail(path, 96 * 1024), appId);
        }
        catch
        {
            return null;
        }
    }

    public static Job? TryParseLatest(string text, string appId)
    {
        if (string.IsNullOrEmpty(text) || !SteamProtocol.IsValidAppId(appId))
            return null;

        var pattern = @"AppID " + Regex.Escape(appId) +
                      @" update started : download (\d+)/(\d+), store \d+/\d+, reuse \d+/\d+, delta \d+/\d+, stage (\d+)/(\d+)";
        Job? latest = null;
        foreach (Match m in Regex.Matches(text, pattern, RegexOptions.CultureInvariant))
        {
            if (!long.TryParse(m.Groups[1].Value, out var downloaded) ||
                !long.TryParse(m.Groups[2].Value, out var toDownload) ||
                !long.TryParse(m.Groups[3].Value, out var staged) ||
                !long.TryParse(m.Groups[4].Value, out var toStage) ||
                toDownload <= 0)
                continue;
            latest = new Job(downloaded, toDownload, staged, toStage);
        }

        return latest;
    }

    /// <summary>
    /// True when any Steam library has a non-empty <c>steamapps/downloading</c>
    /// folder. Used to refuse closing Steam mid-transfer. Does not walk file
    /// sizes.
    /// </summary>
    public static bool AnyDownloadingFolder(string? steamRoot = null)
    {
        steamRoot ??= TrySteamRoot();
        if (string.IsNullOrWhiteSpace(steamRoot)) return false;
        foreach (var lib in LibraryFolders(steamRoot))
        {
            var dir = Path.Combine(lib, "steamapps", "downloading");
            try
            {
                if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                    return true;
            }
            catch
            {
                /* one library failed */
            }
        }

        return false;
    }

    private static string? TrySteamRoot()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var root = path.Replace('/', Path.DirectorySeparatorChar);
                if (Directory.Exists(root)) return root;
            }
        }
        catch { /* */ }

        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
                 })
        {
            if (Directory.Exists(root)) return root;
        }

        return null;
    }

    /// <summary>
    /// True while Steam still has an in-flight staging folder for this app.
    /// <c>workshop/content</c> is the installed destination and stays after
    /// the job finishes, so it is not a live-transfer signal.
    /// </summary>
    public static bool HasLiveTransferFolder(string? steamRoot, string appId)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || !SteamProtocol.IsValidAppId(appId))
            return false;
        foreach (var lib in LibraryFolders(steamRoot))
        {
            foreach (var dir in new[]
                     {
                         Path.Combine(lib, "steamapps", "downloading", appId),
                         Path.Combine(lib, "steamapps", "workshop", "downloads", appId),
                     })
            {
                try
                {
                    if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                        return true;
                }
                catch
                {
                    /* one library failed */
                }
            }
        }

        return false;
    }

    private static readonly Regex StampedLine = new(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] (.*)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex MovingRate = new(@"(?:Current download rate: |, now )(\d+(?:\.\d+)?)",
        RegexOptions.CultureInvariant);
    private static readonly object OfflineGate = new();
    private static long _offlineCheckedAt;
    private static bool _offline;

    /// <summary>
    /// True when Steam's last two minutes of download log are connection
    /// failures ("No Connection", "Connection timeout") with no data moving
    /// since. Steam's own progress counters say nothing about why a download
    /// sits still; this does. Read at most every three seconds.
    /// </summary>
    public static bool CantReachServers(string? steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return false;
        lock (OfflineGate)
        {
            var now = Environment.TickCount64;
            if (now - _offlineCheckedAt < 3000) return _offline;
            _offlineCheckedAt = now;
            try
            {
                var path = Path.Combine(steamRoot, "logs", "content_log.txt");
                _offline = File.Exists(path) && LooksOffline(ReadTail(path, 32 * 1024), DateTime.Now);
            }
            catch { _offline = false; }
            return _offline;
        }
    }

    internal static bool LooksOffline(string text, DateTime localNow)
    {
        DateTime? failedAt = null, movedAt = null;
        foreach (Match m in StampedLine.Matches(text))
        {
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at))
                continue;
            var line = m.Groups[2].Value;
            if (line.Contains("No Connection", StringComparison.Ordinal) ||
                line.Contains("Connection timeout", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Failed to issue HTTP request", StringComparison.Ordinal))
                failedAt = at;
            else if (MovingRate.Match(line) is { Success: true } rate &&
                     double.TryParse(rate.Groups[1].Value, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var mbps) && mbps > 0.5)
                movedAt = at;
        }
        return failedAt is { } failed && localNow - failed < TimeSpan.FromMinutes(2) &&
               (movedAt is null || movedAt < failed);
    }

    private static IEnumerable<string> LibraryFolders(string steamRoot)
    {
        yield return steamRoot;
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
        {
            var p = m.Groups[1].Value.Replace("\\\\", "\\");
            if (Directory.Exists(p) &&
                !p.Equals(steamRoot, StringComparison.OrdinalIgnoreCase))
                yield return p;
        }
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= maxBytes)
        {
            using var full = new StreamReader(stream);
            return full.ReadToEnd();
        }

        stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
