using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// One-way privacy migration for account/profile surfaces removed from Launcher.
/// The allow-list is intentionally exact; store credentials, achievements,
/// playtime, settings, and unrelated WebView data are never touched.
/// </summary>
internal static class LegacyProfileDataCleanup
{
    private static readonly string[] LegacyFiles =
    [
        "exo-profile-state.json",
        // Per-PC id sent with the retired library activity upload.
        "device-id",
        // Devices room (mouse/HID profiles), retired.
        "mouse-profiles.json",
        "device-profiles.json",
        "devices-last-good.json",
        // Discover room (store catalog) and its GG.deals key, retired.
        "discover-us-v1.json",
        "gg-deals.bin",
        "store-metadata.json",
        // IsThereAnyDeal key; that integration was removed.
        "itad.bin",
        // Exo's copy of the OpenMouse game catalog for its retired bridge, and
        // that bridge's interrupted profile write (the profiles themselves go
        // back to the OpenMouse Bridge app in MouseBridgeRetirement).
        "openmouse-games.json",
        "openmouse-bridge.json.tmp",
        // Mini-player library saves (Spotify app id + tokens, Cider token), retired.
        "spotify-client-id.bin",
        "spotify-tokens.bin",
        "cider-api.bin",
        // Player names for tracker.gg Siege and Destiny 2 hours, retired (Steam reports those).
        "tracker-id-r6siege.txt",
        "tracker-id-destiny2.txt",
        // Exo account, retired: the DPAPI session (session token,
        // account id, handle, email) and its interrupted write. Local only;
        // the account service is never contacted.
        ExoAccountSession,
        ExoAccountSession + ".tmp",
    ];
    private const string ExoAccountSession = "auth.bin";
    // Devices room archives written before generated profiles; exact prefix only.
    private const string LegacyMouseArchivePrefix = "mouse-profiles.json.before-generated-archive-";
    // achievement-icons held art only for the retired unlock pop-up.
    // online-cache held the Exo account's DPAPI response cache.
    private static readonly string[] LegacyDirectories = ["tracker-gg-webview", "achievement-icons", "online-cache"];

    public static void Run()
    {
        var roamingRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ExoLauncher");
        foreach (var root in new[] { PathHelper.AppDataDir, roamingRoot }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CleanupRoot(root);
        }
    }

    internal static void CleanupRoot(string root)
    {
        string fullRoot;
        try { fullRoot = Path.GetFullPath(root); }
        catch { return; }

        foreach (var name in LegacyFiles)
        {
            try
            {
                var path = Path.Combine(fullRoot, name);
                if (!File.Exists(path)) continue;
                File.Delete(path);
                if (name == ExoAccountSession) AppLog.Info("Removed the retired Exo account sign-in from this PC.");
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Legacy profile file cleanup skipped: {ex.Message}");
            }
        }

        try
        {
            if (Directory.Exists(fullRoot))
            {
                foreach (var path in Directory.EnumerateFiles(fullRoot, LegacyMouseArchivePrefix + "*.json"))
                {
                    var name = Path.GetFileName(path);
                    if (!name.StartsWith(LegacyMouseArchivePrefix, StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { File.Delete(path); }
                    catch (Exception ex) { AppLog.Debug($"Legacy profile file cleanup skipped: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Legacy profile file cleanup skipped: {ex.Message}");
        }

        foreach (var name in LegacyDirectories)
        {
            try
            {
                var path = Path.Combine(fullRoot, name);
                if (!Directory.Exists(path)) continue;
                var isReparsePoint = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
                Directory.Delete(path, recursive: !isReparsePoint);
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Legacy profile directory cleanup skipped: {ex.Message}");
            }
        }
    }
}
