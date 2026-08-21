using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam's per-account librarycache filenames plus appinfo names. Cache files
/// are name/history hints only; callers must supply a current authoritative
/// ownership snapshot before this helper emits an installable title.
/// </summary>
internal static class SteamAccountLibrary
{
    public static IReadOnlyList<string> ListCacheAppIds(string? cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
            return Array.Empty<string>();

        var ids = new List<string>();
        foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (SteamProtocol.IsValidAppId(id))
                ids.Add(id);
        }

        return ids;
    }

    public static bool HasCache(string? cacheDirectory, string? appId) =>
        SteamProtocol.IsValidAppId(appId) &&
        !string.IsNullOrWhiteSpace(cacheDirectory) &&
        File.Exists(Path.Combine(cacheDirectory, appId + ".json"));

    public static IReadOnlyList<GameEntry> UninstalledOwnedGames(
        IEnumerable<string> cacheAppIds,
        IReadOnlySet<string> presentAppIds,
        IReadOnlyDictionary<string, SteamAppInfoNames.Entry> names,
        IReadOnlySet<string>? authoritativeOwnedAppIds = null)
    {
        ArgumentNullException.ThrowIfNull(cacheAppIds);
        ArgumentNullException.ThrowIfNull(presentAppIds);
        ArgumentNullException.ThrowIfNull(names);

        // librarycache filenames are account-local UI history, not a current
        // license list. Without an authoritative snapshot the entitlement is
        // unknown, so an uninstalled row must not become Install/Download.
        if (authoritativeOwnedAppIds is null)
            return Array.Empty<GameEntry>();

        var games = new List<GameEntry>();
        foreach (var appId in cacheAppIds.Distinct(StringComparer.Ordinal))
        {
            if (!SteamProtocol.IsValidAppId(appId) || presentAppIds.Contains(appId))
                continue;
            if (!authoritativeOwnedAppIds.Contains(appId))
                continue;
            if (!names.TryGetValue(appId, out var info) ||
                string.IsNullOrWhiteSpace(info.Name) ||
                !info.IsPlayableTitle)
                continue;

            games.Add(new GameEntry
            {
                Id = "steam:" + appId,
                Title = info.Name.Trim(),
                Store = StoreKind.Steam,
                Installed = false,
                Owned = true,
                CanInstall = true,
                UpdateAvailable = false,
                LaunchTarget = appId,
                CoverSource = "steam",
                Status = "Not installed",
                Deps = new[] { "Steam client" },
                LaunchNote = "Installs through Steam.",
            });
        }

        return games;
    }
}
