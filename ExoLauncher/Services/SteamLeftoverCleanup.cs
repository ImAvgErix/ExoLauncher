using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Removes leftover <c>steamapps/downloading/&lt;appId&gt;</c> folders after a
/// finished uninstall, or when they are stale and have no matching appmanifest.
/// Never deletes a folder Steam has written to recently.
/// </summary>
internal static class SteamLeftoverCleanup
{
    internal static readonly TimeSpan DefaultStaleAge = TimeSpan.FromHours(48);

    public static int CleanAfterUninstall(string? steamRoot, string? appId)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || !SteamProtocolSafe(appId))
            return 0;
        return RemoveAppDownloadFolders(steamRoot, appId!, minAge: TimeSpan.Zero);
    }

    public static int CleanStale(string? steamRoot, TimeSpan? minAge = null)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot))
            return 0;

        var age = minAge ?? DefaultStaleAge;
        var removed = 0;
        foreach (var library in CollectLibraries(steamRoot))
        {
            var downloading = Path.Combine(library, "steamapps", "downloading");
            if (!Directory.Exists(downloading)) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(downloading); }
            catch { continue; }

            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (!SteamProtocolSafe(name)) continue;
                if (HasAppManifest(library, name)) continue;
                if (!IsOlderThan(dir, age)) continue;
                if (TryDeleteDownloadFolder(downloading, dir))
                    removed++;
            }
        }

        return removed;
    }

    internal static bool IsDownloadFolder(string downloadingRoot, string candidate)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(downloadingRoot));
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
                return false;
            var name = Path.GetFileName(full);
            return SteamProtocolSafe(name);
        }
        catch
        {
            return false;
        }
    }

    private static int RemoveAppDownloadFolders(string steamRoot, string appId, TimeSpan minAge)
    {
        var removed = 0;
        foreach (var library in CollectLibraries(steamRoot))
        {
            var downloading = Path.Combine(library, "steamapps", "downloading");
            var dir = Path.Combine(downloading, appId);
            if (!Directory.Exists(dir)) continue;
            if (!IsDownloadFolder(downloading, dir)) continue;
            if (minAge > TimeSpan.Zero && !IsOlderThan(dir, minAge)) continue;
            if (TryDeleteDownloadFolder(downloading, dir))
                removed++;
        }

        return removed;
    }

    private static bool TryDeleteDownloadFolder(string downloadingRoot, string dir)
    {
        try
        {
            if (!IsDownloadFolder(downloadingRoot, dir)) return false;
            if (IsReparsePoint(dir)) return false;
            Directory.Delete(dir, recursive: true);
            AppLog.Info("Removed leftover Steam download folder: " + dir);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam leftover cleanup skipped: " + ex.Message);
            return false;
        }
    }

    private static bool HasAppManifest(string library, string appId)
    {
        var acf = Path.Combine(library, "steamapps", "appmanifest_" + appId + ".acf");
        return File.Exists(acf);
    }

    internal static bool IsOlderThan(string dir, TimeSpan age)
    {
        try
        {
            // Directory mtime only. Walking every file in a leftover download
            // made library scans stall on cancelled multi-GB jobs.
            var stamp = Directory.GetLastWriteTimeUtc(dir);
            return DateTime.UtcNow - stamp >= age;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static IEnumerable<string> CollectLibraries(string steamRoot)
    {
        var list = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return list;
        try
        {
            var text = File.ReadAllText(vdf);
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var path = match.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
                    list.Add(path);
            }
        }
        catch
        {
            /* keep the primary library */
        }

        return list;
    }

    private static bool SteamProtocolSafe(string? appId) =>
        appId is { Length: >= 1 and <= 10 } &&
        appId.All(char.IsAsciiDigit) &&
        ulong.TryParse(appId, out var parsed) &&
        parsed > 0;
}
