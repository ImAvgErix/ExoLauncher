using System.Globalization;
using System.Text.RegularExpressions;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Read-only Steam extras: library folders, screenshots, categories, workshop
/// folders, cloud last-sync, and family-share hints. Opens official
/// <c>steam://</c> URIs only.
/// </summary>
internal static class SteamLibraryExtras
{
    public sealed record Snapshot(
        IReadOnlyList<string> LibraryFolders,
        string? ScreenshotsPath,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> WorkshopItems,
        IReadOnlyList<string> NonSteamShortcuts,
        DateTimeOffset? CloudLastSyncUtc,
        bool FamilyShared,
        string? NewsUri,
        string? WorkshopUri,
        string? ScreenshotsUri,
        string? StorageSettingsUri);

    public static Snapshot Describe(GameEntry game, string? steamRoot)
    {
        var appId = game.LaunchTarget;
        var valid = SteamProtocol.IsValidAppId(appId);
        var folders = steamRoot is null ? Array.Empty<string>() : CollectLibraries(steamRoot);
        var account = steamRoot is null ? null : SteamPlaytime.LoadActiveAccount(steamRoot);
        return new Snapshot(
            LibraryFolders: folders,
            ScreenshotsPath: steamRoot is null ? null : FindScreenshotsPath(steamRoot),
            Categories: valid && steamRoot is not null ? ReadCategories(steamRoot, appId!) : Array.Empty<string>(),
            WorkshopItems: valid && steamRoot is not null ? ListWorkshopItems(folders, appId!) : Array.Empty<string>(),
            NonSteamShortcuts: steamRoot is null ? Array.Empty<string>() : ListNonSteamShortcutNames(steamRoot),
            CloudLastSyncUtc: valid && steamRoot is not null ? ReadCloudLastSync(steamRoot, appId!) : null,
            FamilyShared: valid && steamRoot is not null && IsFamilyShared(folders, appId!, account?.AccountKey),
            NewsUri: valid ? SteamProtocol.NewsUri(appId!) : null,
            WorkshopUri: valid ? SteamProtocol.WorkshopUri(appId!) : null,
            ScreenshotsUri: SteamProtocol.ScreenshotsUri(),
            StorageSettingsUri: SteamProtocol.StorageSettingsUri());
    }

    internal static string? FindScreenshotsPath(string steamRoot)
    {
        try
        {
            var userdata = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdata)) return null;
            foreach (var account in Directory.EnumerateDirectories(userdata))
            {
                var remote = Path.Combine(account, "760", "remote");
                if (Directory.Exists(remote)) return remote;
                var shots = Path.Combine(account, "760", "screenshots");
                if (Directory.Exists(shots)) return shots;
            }
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    internal static IReadOnlyList<string> ReadCategories(string steamRoot, string appId)
    {
        var tags = new List<string>();
        try
        {
            var userdata = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdata)) return tags;
            foreach (var account in Directory.EnumerateDirectories(userdata))
            {
                var shared = Path.Combine(account, "7", "remote", "sharedconfig.vdf");
                if (!File.Exists(shared)) continue;
                var text = File.ReadAllText(shared);
                var block = SliceAppBlock(text, appId);
                if (block is null) continue;
                foreach (Match match in Regex.Matches(block, "\"\\d+\"\\s+\"([^\"]+)\""))
                {
                    var tag = match.Groups[1].Value.Trim();
                    if (tag.Length is > 0 and < 64 &&
                        !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        tags.Add(tag);
                }
            }
        }
        catch
        {
            /* ignore */
        }

        return tags;
    }

    internal static IReadOnlyList<string> ListWorkshopItems(IEnumerable<string> libraries, string appId)
    {
        var names = new List<string>();
        foreach (var library in libraries)
        {
            var dir = Path.Combine(library, "steamapps", "workshop", "content", appId);
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var item in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(item);
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                    if (names.Count >= 40) return names;
                }
            }
            catch
            {
                /* skip library */
            }
        }

        return names;
    }

    internal static IReadOnlyList<string> ListNonSteamShortcutNames(string steamRoot)
    {
        var names = new List<string>();
        try
        {
            var userdata = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdata)) return names;
            foreach (var account in Directory.EnumerateDirectories(userdata))
            {
                var shortcuts = Path.Combine(account, "config", "shortcuts.vdf");
                if (!File.Exists(shortcuts)) continue;
                var bytes = File.ReadAllBytes(shortcuts);
                foreach (var name in ExtractUtf8Labels(bytes, "appname"))
                {
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                    if (names.Count >= 40) return names;
                }
            }
        }
        catch
        {
            /* ignore */
        }

        return names;
    }

    internal static DateTimeOffset? ReadCloudLastSync(string steamRoot, string appId)
    {
        DateTimeOffset? best = null;
        try
        {
            var userdata = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdata)) return null;
            foreach (var account in Directory.EnumerateDirectories(userdata))
            {
                var cache = Path.Combine(account, appId, "remotecache.vdf");
                if (!File.Exists(cache)) continue;
                var write = File.GetLastWriteTimeUtc(cache);
                var stamp = new DateTimeOffset(DateTime.SpecifyKind(write, DateTimeKind.Utc));
                if (best is null || stamp > best) best = stamp;
            }
        }
        catch
        {
            /* ignore */
        }

        return best;
    }

    internal static bool IsFamilyShared(IEnumerable<string> libraries, string appId, string? activeAccountKey)
    {
        foreach (var library in libraries)
        {
            var acf = Path.Combine(library, "steamapps", "appmanifest_" + appId + ".acf");
            if (!File.Exists(acf)) continue;
            try
            {
                var text = File.ReadAllText(acf);
                var owner = SteamProtocol.MatchAcfField(text, "LastOwner")
                            ?? SteamProtocol.MatchAcfField(text, "OwnerAccount");
                if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(activeAccountKey))
                    continue;
                // LastOwner is a SteamID64 / account id, AccountKey is a one-way
                // hash. Treat SharedDepotIds / borrowed bits as the only claim.
                if (text.Contains("SharedDepotIds", StringComparison.OrdinalIgnoreCase) &&
                    text.Contains(appId, StringComparison.Ordinal))
                    return true;
                var bytesToDownload = SteamProtocol.MatchAcfField(text, "BytesToDownload");
                _ = bytesToDownload;
            }
            catch
            {
                /* ignore */
            }
        }

        return false;
    }

    private static IReadOnlyList<string> CollectLibraries(string steamRoot)
    {
        var list = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return list;
        try
        {
            var text = File.ReadAllText(vdf);
            foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var path = match.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
                    list.Add(path);
            }
        }
        catch
        {
            /* keep root */
        }

        return list;
    }

    private static string? SliceAppBlock(string text, string appId)
    {
        var needle = "\"" + appId + "\"";
        var start = text.IndexOf(needle, StringComparison.Ordinal);
        if (start < 0) return null;
        var open = text.IndexOf('{', start);
        if (open < 0) return null;
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text[open..(i + 1)];
            }
        }

        return null;
    }

    private static IEnumerable<string> ExtractUtf8Labels(byte[] bytes, string key)
    {
        var needle = System.Text.Encoding.UTF8.GetBytes(key);
        for (var i = 0; i < bytes.Length - needle.Length - 2; i++)
        {
            if (!bytes.AsSpan(i, needle.Length).SequenceEqual(needle)) continue;
            var start = i + needle.Length;
            if (start < bytes.Length && bytes[start] == 0) start++;
            var end = start;
            while (end < bytes.Length && bytes[end] != 0) end++;
            if (end <= start) continue;
            var label = System.Text.Encoding.UTF8.GetString(bytes, start, end - start).Trim();
            if (label.Length is > 0 and < 80 &&
                label.Any(char.IsLetter) &&
                !label.Equals(key, StringComparison.OrdinalIgnoreCase))
                yield return label;
        }
    }
}
