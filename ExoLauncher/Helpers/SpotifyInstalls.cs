using Microsoft.Win32;

namespace ExoLauncher.Helpers;

/// <summary>
/// Every way Spotify is installed on Windows. The classic app (Spotify's own
/// installer, winget's Spotify.Spotify and Chocolatey all run it) lives in
/// %APPDATA%\Spotify; a moved or redirected AppData shows in its uninstall
/// entry, and other copies (Scoop) in the exe its spotify: link opens. Exo
/// hosts that one: it starts Spotify.exe with a private DevTools pipe and
/// patches Apps\xpui.spa beside it. The Microsoft Store edition
/// (<see cref="StoreFamily"/>, also what winget's msstore source installs) runs
/// from the read-only WindowsApps folder under its package identity, so
/// neither works for it: Exo says so and offers to switch to the classic app.
/// There is no per-machine Spotify; an install made as another Windows user
/// lives in that user's AppData and is not this user's.
/// </summary>
internal static class SpotifyInstalls
{
    internal const string StoreFamily = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0";

    /// <summary>Where the classic Spotify.exe may be, most usual first; never a WindowsApps path.</summary>
    internal static IEnumerable<string> ClassicCandidates(string roaming, string? uninstallLocation, string? uninstallIcon,
        string? protocolCommand, string userProfile)
    {
        var candidates = new List<string?>
        {
            Path.Combine(roaming, "Spotify", "Spotify.exe"),
            string.IsNullOrWhiteSpace(uninstallLocation) ? null : Path.Combine(uninstallLocation.Trim().Trim('"'), "Spotify.exe"),
            PathHelper.CommandExe(uninstallIcon),
            PathHelper.CommandExe(protocolCommand),
            Path.Combine(userProfile, "scoop", "apps", "spotify", "current", "Spotify.exe"),
        };
        return candidates
            .Where(path => path is not null && Path.IsPathFullyQualified(path) && !IsPackaged(path) &&
                           string.Equals(Path.GetFileName(path), "Spotify.exe", StringComparison.OrdinalIgnoreCase))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A path inside a packaged app's install folder.</summary>
    internal static bool IsPackaged(string path) =>
        path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The classic Spotify.exe on this PC for this user, or null.</summary>
    internal static string? FindClassic(string roaming)
    {
        string? location = null, icon = null, protocol = null;
        try
        {
            using (var uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Spotify"))
            {
                location = uninstall?.GetValue("InstallLocation") as string;
                icon = uninstall?.GetValue("DisplayIcon") as string;
            }
            using var open = Registry.CurrentUser.OpenSubKey(@"Software\Classes\spotify\shell\open\command");
            protocol = open?.GetValue(null) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ClassicCandidates(roaming, location, icon, protocol, profile).FirstOrDefault(File.Exists);
    }

    /// <summary>The Microsoft Store edition is registered for this user.</summary>
    internal static bool StoreEditionInstalled()
    {
        try { return new Windows.Management.Deployment.PackageManager().FindPackagesForUser("", StoreFamily).Any(); }
        catch (Exception ex)
        {
            AppLog.Debug("Spotify: Store edition check failed: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Removes the Store edition for this user once the classic app is in place
    /// (no admin rights needed; Windows closes it first). Its downloaded songs go
    /// with it; the account, playlists and library are Spotify's own and stay.
    /// </summary>
    internal static async Task<bool> RemoveStoreEditionAsync(CancellationToken cancellation)
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser("", StoreFamily))
            {
                var result = await manager.RemovePackageAsync(package.Id.FullName).AsTask(cancellation).ConfigureAwait(false);
                if (result.ExtendedErrorCode is { } error)
                {
                    AppLog.Warn("Spotify: the Store edition could not be removed: 0x" + error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                    return false;
                }
            }
            AppLog.Info("Spotify: the Store edition was removed; Exo hosts the classic app.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("Spotify: the Store edition could not be removed: " + ex.GetType().Name);
            return false;
        }
    }
}
