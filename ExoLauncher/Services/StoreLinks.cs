using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// The Store's Get buttons open the game in its own launcher, not the browser:
/// Steam at <c>steam://store/{appId}</c>, the Epic Games Launcher at
/// <c>com.epicgames.launcher://store/p/{slug}</c>, GOG Galaxy at
/// <c>goggalaxy://openStoreUrl/{page}</c>, each only when that launcher is
/// installed. Other stores' offers are CheapShark redirects, which answer
/// only a real browser, so a hidden, private WebView2 follows the redirect
/// and stops at the first store page; nothing loads past that. A page with no
/// app link (Ubisoft) or a launcher that isn't installed opens in the browser.
/// </summary>
internal static class StoreLinks
{
    private static readonly Dictionary<string, Uri> Resolved = new(StringComparer.Ordinal);

    /// <summary>The launcher link for a store page, or null when it has none or its launcher is not installed.</summary>
    internal static string? AppUri(Uri page)
    {
        var host = page.Host.ToLowerInvariant();
        var parts = page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (host == "store.steampowered.com")
        {
            var app = Array.IndexOf(parts, "app");
            if (app >= 0 && app + 1 < parts.Length && parts[app + 1].All(char.IsAsciiDigit) && Registered("steam"))
                return "steam://store/" + parts[app + 1];
            return null;
        }
        if (host is "store.epicgames.com" or "www.epicgames.com")
        {
            var at = Array.FindIndex(parts, part => part is "p" or "product");
            if (at >= 0 && at + 1 < parts.Length && Registered("com.epicgames.launcher"))
                return "com.epicgames.launcher://store/p/" + Uri.EscapeDataString(parts[at + 1]);
            return null;
        }
        if ((host == "gog.com" || host.EndsWith(".gog.com", StringComparison.Ordinal)) && Registered("goggalaxy"))
            return "goggalaxy://openStoreUrl/" + page.Host + page.AbsolutePath;
        return null;
    }

    private static bool Registered(string scheme)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(scheme);
            return key?.GetValue("URL Protocol") is not null;
        }
        catch { return false; }
    }

    private static bool IsRedirect(Uri uri) => uri.Host.EndsWith("cheapshark.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Follows a CheapShark redirect to the store page it leads to. Must run on
    /// the UI thread. Null when it doesn't get there in ten seconds.
    /// </summary>
    internal static async Task<Uri?> ResolveAsync(Uri url, nint owner, CancellationToken ct)
    {
        if (!IsRedirect(url)) return url;
        lock (Resolved) if (Resolved.TryGetValue(url.AbsoluteUri, out var known)) return known;

        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await WebViewEnvironmentFactory.GetAsync();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)owner), options);
            controller.IsVisible = false;
            var core = controller.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            var landed = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var next) || IsRedirect(next)) return;
                e.Cancel = true;
                landed.TrySetResult(next.Scheme is "https" or "http" ? next : null);
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.Navigate(url.AbsoluteUri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var page = await landed.Task.WaitAsync(timeout.Token);
            if (page is not null) lock (Resolved) Resolved[url.AbsoluteUri] = page;
            return page;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            AppLog.Debug("Store link not resolved: " + ex.Message);
            return null;
        }
        finally
        {
            controller?.Close();
        }
    }

    /// <summary>Opens a launcher link without a console window; Steam's goes through the bridge's own Steam handoff.</summary>
    internal static void StartApp(string uri) => ProcessHelper.StartProtocol(uri);
}
