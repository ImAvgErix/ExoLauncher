using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam's per-account librarycache filenames plus appinfo names. This is how
/// Exo lists owned titles that have no appmanifest yet (never installed, or
/// uninstalled). It is not a store catalog scrape.
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
        IReadOnlyDictionary<string, SteamAppInfoNames.Entry> names)
    {
        ArgumentNullException.ThrowIfNull(cacheAppIds);
        ArgumentNullException.ThrowIfNull(presentAppIds);
        ArgumentNullException.ThrowIfNull(names);

        var games = new List<GameEntry>();
        foreach (var appId in cacheAppIds)
        {
            if (!SteamProtocol.IsValidAppId(appId) || presentAppIds.Contains(appId))
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
                LaunchNote = "Installs through Steam quietly — Steam stays a backend, not a window you use.",
            });
        }

        return games;
    }
}
