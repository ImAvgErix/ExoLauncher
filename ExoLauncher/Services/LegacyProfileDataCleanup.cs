using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// One-way privacy migration for account/profile surfaces removed from Launcher.
/// The allow-list is intentionally exact; store credentials, achievements,
/// playtime, settings, and unrelated WebView data are never touched.
/// </summary>
internal static class LegacyProfileDataCleanup
{
    private static readonly string[] LegacyFiles = ["exo-profile-state.json"];
    private static readonly string[] LegacyDirectories = ["tracker-gg-webview"];

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
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Legacy profile file cleanup skipped: {ex.Message}");
            }
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
