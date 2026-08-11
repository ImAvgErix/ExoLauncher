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
        IEnumerable<GameEntry> eglInstalled)
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
            // Prefer EGL path when Legendary claims not installed, or has no path.
            if (cur.Installed && !string.IsNullOrWhiteSpace(cur.Path))
                continue;

            games[idx] = new GameEntry
            {
                Id = cur.Id,
                Title = PreferTitle(cur.Title, g.Title),
                Store = StoreKind.Epic,
                Installed = true,
                Owned = true,
                UpdateAvailable = cur.UpdateAvailable,
                CanInstall = false,
                Path = g.Path ?? cur.Path,
                CoverUrl = cur.CoverUrl ?? g.CoverUrl,
                CoverSource = cur.CoverSource ?? g.CoverSource,
                PlaytimeMinutes = cur.PlaytimeMinutes ?? g.PlaytimeMinutes,
                SizeBytes = g.SizeBytes ?? cur.SizeBytes,
                Status = "Ready",
                Deps = cur.Deps.Count > 0 ? cur.Deps : g.Deps,
                LaunchNote = "Installed via Epic Games Launcher. Launches via Legendary when available.",
                LaunchTarget = cur.LaunchTarget ?? g.LaunchTarget,
                LastPlayedUtc = cur.LastPlayedUtc ?? g.LastPlayedUtc,
                IsFavorite = cur.IsFavorite || g.IsFavorite,
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

    internal static string PreferTitle(string current, string? egl)
    {
        if (string.IsNullOrWhiteSpace(egl)) return current;
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

    public static string NormalizeEpicTitle(string? displayName, string? appName)
    {
        var title = displayName?.Trim() ?? "";
        if (string.Equals(appName, "Sugar", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Rocket League", StringComparison.OrdinalIgnoreCase))
            return "Rocket League";
        return title;
    }
}
