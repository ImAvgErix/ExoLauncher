namespace ExoLauncher.Helpers;

/// <summary>
/// Removes disposable WebView2 cache folders when the running app version
/// changes, so an update does not inherit stale HTTP or GPU caches. V8's
/// Code Cache is keyed by script content and is kept.
/// Safe folders only — never the profile, cookies, or local storage.
/// </summary>
internal static class WebViewCacheCleaner
{
    /// <summary>
    /// Cache and temporary subdirectories under each WebView2 profile
    /// (<c>EBWebView/&lt;profile&gt;/…</c>) that can be safely rebuilt on the
    /// next navigation. Never cookies, storage, Local Storage, or IndexedDB.
    /// </summary>
    internal static readonly string[] DisposableSubdirs = BuildDisposableSubdirs();

    private static string[] BuildDisposableSubdirs()
    {
        var caches = new[]
        {
            "Cache",
            "DawnCache",
            "GPUCache",
        };
        var list = new List<string>(caches.Length * 2 + 1);
        foreach (var profile in new[] { "Default", "WV2Profile_" + WebViewHostProfile.EmbeddedProfileName })
            foreach (var cache in caches)
                list.Add($"EBWebView/{profile}/{cache}");
        list.Add("EBWebView/Default/Storage/ext");
        return list.ToArray();
    }

    /// <summary>
    /// Per-surface user data folders retired by the shared embedded profile.
    /// Cookies are DPAPI-bound to a UDF so they cannot be migrated; the
    /// folders are deleted best-effort on startup.
    /// </summary>
    private static readonly string[] RetiredUserDataFolders =
    [
        "discord-web",
        "apple-music-web",
        "spotify-web",
        "youtube-music-web",
        "music-webview",
    ];

    internal static void RetireLegacyUserDataFolders()
    {
        var removed = 0;
        foreach (var name in RetiredUserDataFolders)
        {
            var full = Path.Combine(PathHelper.AppDataDir, name);
            if (!Directory.Exists(full)) continue;
            try
            {
                Directory.Delete(full, recursive: true);
                removed++;
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Retired WebView2 folder kept {name}: {ex.GetType().Name}");
            }
        }
        if (removed > 0)
            AppLog.Info($"Removed {removed} retired WebView2 profile folders; embedded apps now share the '{WebViewHostProfile.EmbeddedProfileName}' profile.");
    }

    internal static void RunIfVersionChanged(string appVersion)
    {
        try
        {
            var folder = WebViewHostProfile.UserDataFolder;
            if (!Directory.Exists(folder)) return;

            var marker = Path.Combine(folder, "last-exo-version.txt");
            var last = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            if (string.Equals(last, appVersion, StringComparison.OrdinalIgnoreCase)) return;

            foreach (var sub in DisposableSubdirs)
            {
                var full = Path.Combine(folder, sub);
                if (Directory.Exists(full))
                {
                    try { Directory.Delete(full, recursive: true); }
                    catch (Exception ex)
                    {
                        AppLog.Debug($"WebView2 cache cleanup skipped {sub}: {ex.GetType().Name}");
                    }
                }
            }

            try { File.WriteAllText(marker, appVersion); }
            catch { /* marker is advisory */ }

            if (last is not null)
            {
                AppLog.Info($"Cleared WebView2 cache folders after app update ({last} -> {appVersion}).");
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("WebView2 cache cleanup failed: " + ex.GetType().Name);
        }
    }
}
