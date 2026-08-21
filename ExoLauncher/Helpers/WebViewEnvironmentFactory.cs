using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Helpers;

/// <summary>
/// One <see cref="CoreWebView2Environment"/> for the shell WebView and the
/// trophy overlay controller. Controller options (transparent background)
/// stay per-surface.
/// </summary>
internal static class WebViewEnvironmentFactory
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CoreWebView2Environment? _environment;

    internal static async Task<CoreWebView2Environment> GetAsync()
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
            Directory.CreateDirectory(folder);
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = WebViewHostProfile.AdditionalBrowserArguments(),
            };
            _environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: folder,
                options: options);
            return _environment;
        }
        finally
        {
            Gate.Release();
        }
    }
}
