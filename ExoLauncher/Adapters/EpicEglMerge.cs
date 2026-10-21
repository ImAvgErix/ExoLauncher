using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Merge Epic Games Launcher install records onto Legendary-owned library rows.
/// Legendary <c>list</c> often returns owned titles with Installed=false while EGL
/// already has the game on disk — without this overlay, Exo's installed-only library hides them.
/// </summary>
public static class EpicEglMerge
{
    public static List<GameEntry> ApplyInstalledOverlays(
        IEnumerable<GameEntry> legendaryOrOwned,
        IEnumerable<GameEntry> eglInstalled,
        bool preferEglInstallPath = false)
    {
        var games = legendaryOrOwned.ToList();
        foreach (var g in eglInstalled)
        {
            if (string.IsNullOrWhiteSpace(g.LaunchTarget) && string.IsNullOrWhiteSpace(g.Id))
                continue;

            var idx = games.FindIndex(x => SameEpicGame(x, g));
            if (idx < 0)
            {
                games.Add(g);
                continue;
            }

            if (!g.Installed)
                continue;

            var cur = games[idx];
            var updateAvailable = cur.UpdateAvailable || g.UpdateAvailable;
            // Follow the backend that will launch. An old Legendary copy can
            // still exist after EGL moves or reinstalls the same game.
            if (cur.Installed && !string.IsNullOrWhiteSpace(cur.Path) &&
                !(preferEglInstallPath && !string.IsNullOrWhiteSpace(g.Path)))
            {
                if (updateAvailable != cur.UpdateAvailable)
                {
                    games[idx] = new GameEntry
                    {
                        Id = cur.Id,
                        Title = cur.Title,
                        Store = StoreKind.Epic,
                        Installed = cur.Installed,
                        Owned = cur.Owned,
                        EntitlementState = cur.EntitlementState,
                        UpdateAvailable = updateAvailable,
                        CanInstall = cur.CanInstall,
                        Path = cur.Path,
                        CoverUrl = cur.CoverUrl,
                        CoverSource = cur.CoverSource,
                        PlaytimeMinutes = cur.PlaytimeMinutes,
                        SizeBytes = cur.SizeBytes,
                        Status = updateAvailable ? "Update" : cur.Status,
                        Deps = cur.Deps,
                        LaunchNote = cur.LaunchNote,
                        LaunchTarget = cur.LaunchTarget,
                        LastPlayedUtc = cur.LastPlayedUtc,
                    };
                }
                continue;
            }

            games[idx] = new GameEntry
            {
                Id = cur.Id,
                Title = PreferTitle(cur.Title, g.Title, cur.LaunchTarget ?? g.LaunchTarget),
                Store = StoreKind.Epic,
                Installed = true,
                // The EGL path proves installation only. Preserve the ownership
                // state established by the account-scoped Legendary snapshot.
                Owned = cur.Owned,
                EntitlementState = cur.EntitlementState,
                UpdateAvailable = updateAvailable,
                CanInstall = false,
                Path = g.Path ?? cur.Path,
                CoverUrl = cur.CoverUrl ?? g.CoverUrl,
                CoverSource = cur.CoverSource ?? g.CoverSource,
                PlaytimeMinutes = cur.PlaytimeMinutes ?? g.PlaytimeMinutes,
                SizeBytes = g.SizeBytes ?? cur.SizeBytes,
                Status = updateAvailable ? "Update" : "Ready",
                Deps = cur.Deps.Count > 0 ? cur.Deps : g.Deps,
                LaunchNote = "Installed via Epic Games Launcher.",
                LaunchTarget = cur.LaunchTarget ?? g.LaunchTarget,
                LastPlayedUtc = cur.LastPlayedUtc ?? g.LastPlayedUtc,
            };
        }

        return games;
    }

    internal static bool SameEpicGame(GameEntry a, GameEntry b)
    {
        if (!string.IsNullOrWhiteSpace(a.LaunchTarget) && !string.IsNullOrWhiteSpace(b.LaunchTarget)
            && string.Equals(a.LaunchTarget, b.LaunchTarget, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(b.Id)
            && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    internal static string PreferTitle(string current, string? egl, string? appName = null)
    {
        if (string.IsNullOrWhiteSpace(egl) || IsAppIdTitle(egl, appName)) return current;
        // A record's app id is no title, however long (32 hex digits beat "Fortnite" on length).
        if (IsAppIdTitle(current, appName)) return egl;
        // EGL sometimes ships truncated/odd DisplayNames; keep Legendary title if longer & sane.
        if (egl.Length < 3) return current;
        if (string.Equals(current, egl, StringComparison.OrdinalIgnoreCase)) return current;
        // Known EGL oddity
        if (egl.StartsWith("Rocket League", StringComparison.OrdinalIgnoreCase))
            return "Rocket League";
        if (string.IsNullOrWhiteSpace(current) || current.Length < egl.Length)
            return egl;
        return current;
    }

    /// <summary>The title is only the game's app id: what a record with no name of its own falls back to.</summary>
    internal static bool IsAppIdTitle(string? title, string? appName) =>
        EpicLauncherV2.IsHexId(title) ||
        (!string.IsNullOrWhiteSpace(appName) && string.Equals(title?.Trim(), appName.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string NormalizeEpicTitle(string? displayName, string? appName)
    {
        var title = displayName?.Trim() ?? "";
        if (string.Equals(appName, "Sugar", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Rocket League", StringComparison.OrdinalIgnoreCase))
            return "Rocket League";
        return title;
    }
}
