using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using ExoLauncher.Services;

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

    /// <summary>
    /// Steam Family titles: the running client licenses every family title to
    /// the account (with a purchase time, so ISteamApps cannot tell them
    /// apart), while the Web API's owned list holds only the account's own
    /// licenses, free ones included. What the client licenses beyond that list
    /// is the family's. Empty without both answers or outside a family.
    /// </summary>
    internal static IReadOnlySet<string> FamilyAppIds(
        bool inFamily,
        IReadOnlySet<string>? clientLicensed,
        IReadOnlySet<string>? webApiOwned)
    {
        if (!inFamily || clientLicensed is null || webApiOwned is null) return new HashSet<string>();
        return clientLicensed.Where(id => !webApiOwned.Contains(id)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// An app manifest's <c>LastOwner</c> (SteamID64 of the license Steam
    /// installs with) names another member of the account's family. Steam
    /// writes it when the download starts, so this holds mid-download too.
    /// </summary>
    internal static bool LastOwnerIsFamilyMember(
        string? lastOwner,
        string? selfSteamId64,
        IReadOnlySet<string>? familyMemberIds)
    {
        if (familyMemberIds is not { Count: > 0 } ||
            !ulong.TryParse(lastOwner, out var owner) || owner == 0 ||
            string.Equals(lastOwner, selfSteamId64, StringComparison.Ordinal))
            return false;
        var accountId = (owner & 0xFFFFFFFF).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return familyMemberIds.Contains(accountId);
    }

    public static IReadOnlyList<GameEntry> UninstalledOwnedGames(
        IEnumerable<string> cacheAppIds,
        IReadOnlySet<string> presentAppIds,
        IReadOnlyDictionary<string, SteamAppInfoNames.Entry> names,
        IReadOnlySet<string>? authoritativeOwnedAppIds = null,
        IReadOnlySet<string>? familyAppIds = null)
    {
        ArgumentNullException.ThrowIfNull(cacheAppIds);
        ArgumentNullException.ThrowIfNull(presentAppIds);
        ArgumentNullException.ThrowIfNull(names);

        // librarycache filenames are account-local UI history, not a current
        // license list. Without an authoritative snapshot (Web API or the
        // signed-in Steam client via SteamClientIpc.QueryOwnedAsync) the
        // entitlement is unknown, so an uninstalled row must not become
        // Install/Download.
        if (authoritativeOwnedAppIds is null)
            return Array.Empty<GameEntry>();

        var games = new List<GameEntry>();
        foreach (var appId in cacheAppIds.Distinct(StringComparer.Ordinal))
        {
            if (!SteamProtocol.IsValidAppId(appId) || presentAppIds.Contains(appId))
                continue;
            if (!authoritativeOwnedAppIds.Contains(appId))
                continue;
            // Games only: type exactly "game", no parent (DLC, soundtrack,
            // bonus content, mod uploader), not a Steamworks/test app, and
            // not an extras title. Show tools never widens this shelf.
            if (!names.TryGetValue(appId, out var info) ||
                string.IsNullOrWhiteSpace(info.Name) ||
                !CatalogHygiene.IsOwnedUninstalledSteamGame(appId, info.Name, info.Type, info.ParentAppId))
                continue;

            var family = familyAppIds?.Contains(appId) == true;
            games.Add(new GameEntry
            {
                Id = "steam:" + appId,
                Title = info.Name.Trim(),
                Store = StoreKind.Steam,
                Installed = false,
                Owned = true,
                FamilyShared = family,
                EntitlementState = EntitlementState.Owned,
                CanInstall = true,
                UpdateAvailable = false,
                LaunchTarget = appId,
                ParentAppId = string.IsNullOrWhiteSpace(info.ParentAppId) ? null : info.ParentAppId,
                SteamAppType = "game",
                CoverSource = "steam",
                Status = "Not installed",
                Deps = new[] { "Steam client" },
                LaunchNote = family
                    ? "Shared through Steam Family. Installs through Steam."
                    : "Installs through Steam.",
            });
        }

        return games;
    }
}
