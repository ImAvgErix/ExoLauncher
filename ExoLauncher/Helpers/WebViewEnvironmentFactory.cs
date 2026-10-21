using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Helpers;

/// <summary>
/// One <see cref="CoreWebView2Environment"/> for every MainWindow surface:
/// the shell on the Default profile, music providers on the shared
/// "embedded" profile. GOG/Epic login create their own short-lived environments.
/// </summary>
internal static class WebViewEnvironmentFactory
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CoreWebView2Environment? _environment;

    /// <summary>
    /// Drops a cached environment whose browser could not be started so the
    /// next <see cref="GetAsync"/> builds a fresh one (used by init retry).
    /// </summary>
    internal static void Reset(CoreWebView2Environment? failed)
    {
        if (failed is not null && ReferenceEquals(_environment, failed)) _environment = null;
    }

    /// <param name="explicitInstalledRuntime">
    /// Retry path after a file-not-found failure: pass the discovered
    /// installed runtime folder explicitly instead of letting the loader
    /// resolve evergreen from the registry.
    /// </param>
    internal static async Task<CoreWebView2Environment> GetAsync(bool explicitInstalledRuntime = false)
    {
        if (_environment is not null) return _environment;
        await Gate.WaitAsync();
        try
        {
            if (_environment is not null) return _environment;

            // Keep WebView state outside the replaceable application tree.
            // Otherwise a short-lived Edge child can hold the previous
            // version's app folder open during an atomic installer swap.
            var folder = WebViewHostProfile.UserDataFolder;
            // Recursive deletes stay off the UI thread; the environment itself
            // is still created on the caller's thread after this await.
            await Task.Run(() =>
            {
                WebViewCacheCleaner.RetireLegacyUserDataFolders();
                WebViewCacheCleaner.RunIfVersionChanged(ExoLauncher.Services.AppServices.RunningVersion);
                Directory.CreateDirectory(folder);
            });
            // Every surface starts black, not the runtime's default, before its page has drawn.
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "FF000000");
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = WebViewHostProfile.AdditionalBrowserArguments(),
                ExclusiveUserDataFolderAccess = true,
                AreBrowserExtensionsEnabled = true,
            };
            // null = healthy evergreen. A path is used when the Clients pv still
            // names a version whose Application folder was emptied.
            var browser = explicitInstalledRuntime
                ? WebViewRuntimeLocator.ResolveExplicitInstalledFolder() ?? WebViewRuntimeLocator.ResolveBrowserExecutableFolder()
                : WebViewRuntimeLocator.ResolveBrowserExecutableFolder();
            if (browser is not null)
                AppLog.Info("webview-runtime-folder=" + browser + (explicitInstalledRuntime ? " (explicit retry)" : string.Empty));
            _environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: browser,
                userDataFolder: folder,
                options: options);
            return _environment;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Controller options that place a view on the shared named profile used
    /// by every music provider. One profile means one uBlock instance and one
    /// renderer per site instead of one per surface.
    /// </summary>
    internal static CoreWebView2ControllerOptions EmbeddedControllerOptions(CoreWebView2Environment environment)
    {
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = WebViewHostProfile.EmbeddedProfileName;
        options.IsInPrivateModeEnabled = false;
        return options;
    }

    private static int _embeddedProfileLogged;

    internal static void LogEmbeddedProfileOnce(CoreWebView2 core)
    {
        if (Interlocked.Exchange(ref _embeddedProfileLogged, 1) == 0)
            AppLog.Info("embedded-webview-profile=" + core.Profile.ProfilePath);
    }
}
