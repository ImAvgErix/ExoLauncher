using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace ExoLauncher.Services;

/// <summary>
/// The sign-in browser for store accounts (Epic via Legendary, GOG via gogdl).
/// It is a plain browser on purpose: no ad blocker or extensions (Epic and GOG
/// sign-in depend on bot-protection and captcha scripts that blockers break),
/// the stock Edge user agent, and the store plus the identity providers it
/// federates to ("Sign in with Google/Apple/Xbox/…"). Anything else stays out.
/// Provider popups open in a second window on the same browser environment so
/// <c>window.opener</c> hand-backs work, and close when the provider is done.
/// </summary>
internal sealed class StoreAuthWindow
{
    internal sealed record Options(
        string LogName,
        string Title,
        string UserDataFolder,
        string StartUrl,
        Func<Uri, bool> IsAllowedNavigation,
        Func<string, bool>? InterceptNavigation = null,
        Func<WebView2, Task>? PageLoaded = null);

    private readonly Options _options;
    private readonly List<Window> _popups = new();
    private CoreWebView2Environment? _environment;
    private Window? _window;
    private WebView2? _web;
    private bool _closing;
    private string? _lastLoggedHost;

    internal StoreAuthWindow(Options options) => _options = options;

    /// <summary>Raised on the UI thread when the user closes the main sign-in window.</summary>
    internal event Action? Closed;

    /// <summary>Must run on the UI thread.</summary>
    internal async Task StartAsync()
    {
        var web = new WebView2();
        var window = new Window
        {
            Title = _options.Title,
            Content = new Grid
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                Children = { web },
            },
        };
        _window = window;
        _web = web;
        window.Closed += (_, _) => OnMainClosed();
        PlaceAndShow(window, 1000, 820);
        AppLog.Info($"{_options.LogName}: sign-in window shown.");

        _environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            WebViewRuntimeLocator.ResolveBrowserExecutableFolder(),
            _options.UserDataFolder,
            new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false });
        await web.EnsureCoreWebView2Async(_environment);
        var core = web.CoreWebView2 ?? throw new InvalidOperationException("The sign-in browser did not start.");
        Configure(core, web, popupWindow: null);
        AppLog.Info($"{_options.LogName}: browser ready (WebView2 {_environment.BrowserVersionString}); UA \"{core.Settings.UserAgent}\".");
        core.Navigate(_options.StartUrl);
    }

    /// <summary>Close every sign-in window. Safe to call more than once; UI thread only.</summary>
    internal void Close()
    {
        _closing = true;
        foreach (var popup in _popups.ToArray())
        {
            try { popup.Close(); } catch { /* already closed */ }
        }
        _popups.Clear();
        try { _web?.Close(); } catch { /* best effort */ }
        try { _window?.Close(); } catch { /* best effort */ }
        _web = null;
        _window = null;
    }

    private void OnMainClosed()
    {
        var userClosed = !_closing;
        Close();
        if (!userClosed) return;
        AppLog.Info($"{_options.LogName}: sign-in window closed by the user.");
        try { Closed?.Invoke(); } catch { /* listener failure must not escape the UI thread */ }
    }

    private void Configure(CoreWebView2 core, WebView2 web, Window? popupWindow)
    {
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        // WebView2's default UA already matches Edge. Strip any embedding token
        // a future runtime might add; identity providers refuse "embedded" UAs.
        var userAgent = settings.UserAgent ?? "";
        var cleaned = CleanUserAgent(userAgent);
        if (!string.Equals(cleaned, userAgent, StringComparison.Ordinal))
            settings.UserAgent = cleaned;

        var where = popupWindow is null ? "" : " (popup)";
        core.NavigationStarting += (_, e) =>
        {
            if (_options.InterceptNavigation?.Invoke(e.Uri) == true)
            {
                e.Cancel = true;
                return;
            }
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !IsAllowed(uri))
            {
                e.Cancel = true;
                AppLog.Info($"{_options.LogName}: navigation blocked{where} to {Describe(e.Uri)}.");
                return;
            }
            if (!string.Equals(_lastLoggedHost, uri.Host + where, StringComparison.OrdinalIgnoreCase))
            {
                _lastLoggedHost = uri.Host + where;
                AppLog.Info($"{_options.LogName}: navigating{where} to {Describe(e.Uri)}.");
            }
        };
        core.NavigationCompleted += (sender, e) =>
        {
            string title;
            try { title = core.DocumentTitle ?? ""; } catch { title = ""; }
            var source = Describe(SafeSource(core));
            if (!e.IsSuccess)
            {
                // Cancelled navigations (blocked or intercepted) also land here.
                if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    AppLog.Warn($"{_options.LogName}: page failed{where} ({e.WebErrorStatus}) at {source}.");
                return;
            }
            AppLog.Info($"{_options.LogName}: loaded{where} {source} (title \"{Shorten(title)}\").");
            if (popupWindow is null && _options.PageLoaded is { } loaded)
                _ = loaded(web);
        };
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += (_, e) =>
            AppLog.Warn($"{_options.LogName}: browser process failed{where} ({e.ProcessFailedKind}).");
        if (popupWindow is not null)
        {
            core.WindowCloseRequested += (_, _) =>
            {
                AppLog.Info($"{_options.LogName}: provider popup finished.");
                try { popupWindow.Close(); } catch { /* already closed */ }
            };
        }
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var target = e.Uri;
        var blank = string.IsNullOrWhiteSpace(target) ||
                    target.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
        if (!blank && (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !IsAllowed(uri)))
        {
            e.Handled = true;
            AppLog.Info($"{_options.LogName}: popup blocked to {Describe(target)}.");
            return;
        }

        var environment = _environment;
        if (environment is null || _closing)
        {
            e.Handled = true;
            return;
        }

        var deferral = e.GetDeferral();
        Window? popupWindow = null;
        try
        {
            var popupWeb = new WebView2();
            popupWindow = new Window
            {
                Title = _options.Title,
                Content = new Grid { Children = { popupWeb } },
            };
            var popup = popupWindow;
            _popups.Add(popup);
            popup.Closed += (_, _) =>
            {
                _popups.Remove(popup);
                try { popupWeb.Close(); } catch { /* best effort */ }
            };
            PlaceAndShow(popup, 620, 760);
            await popupWeb.EnsureCoreWebView2Async(environment);
            var popupCore = popupWeb.CoreWebView2 ?? throw new InvalidOperationException("Popup browser did not start.");
            Configure(popupCore, popupWeb, popup);
            e.NewWindow = popupCore;
            e.Handled = true;
            AppLog.Info($"{_options.LogName}: opened provider popup for {Describe(target)}.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{_options.LogName}: provider popup failed: {ex.GetType().Name}: {ex.Message}");
            e.Handled = true;
            try { popupWindow?.Close(); } catch { /* best effort */ }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private bool IsAllowed(Uri uri)
    {
        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return _options.IsAllowedNavigation(uri);
    }

    private static string SafeSource(CoreWebView2 core)
    {
        try { return core.Source ?? ""; }
        catch { return ""; }
    }

    /// <summary>Scheme, host and path only: queries carry codes and tokens.</summary>
    internal static string Describe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "<blank>";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "<invalid url>";
        if (uri.Scheme is not ("https" or "http")) return uri.Scheme + ":";
        var path = uri.AbsolutePath;
        if (path.Length > 80) path = path[..80] + "…";
        return uri.Scheme + "://" + uri.Host + path;
    }

    private static string Shorten(string text) =>
        text.Length <= 80 ? text : text[..80] + "…";

    internal static string CleanUserAgent(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return userAgent;
        var parts = userAgent.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("WebView2", StringComparison.OrdinalIgnoreCase) &&
                           !part.StartsWith("WebView/", StringComparison.OrdinalIgnoreCase) &&
                           !part.StartsWith("ExoLauncher", StringComparison.OrdinalIgnoreCase));
        return string.Join(' ', parts);
    }

    /// <summary>Real size for the monitor's DPI, centred on the work area, brought to the front.</summary>
    private static void PlaceAndShow(Window window, int widthDip, int heightDip)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
            var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var width = Math.Min((int)(widthDip * scale), Math.Max(480, area.Width - 40));
            var height = Math.Min((int)(heightDip * scale), Math.Max(480, area.Height - 40));
            window.AppWindow.MoveAndResize(new RectInt32(
                area.X + (area.Width - width) / 2,
                area.Y + (area.Height - height) / 2,
                width,
                height));
        }
        catch { /* default placement is still usable */ }

        window.Activate();
        try
        {
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = true;
                presenter.IsMaximizable = true;
                // A process started minimized hands SW_SHOWMINIMIZED to its
                // next new window; a sign-in window must never open iconic.
                if (presenter.State == OverlappedPresenterState.Minimized)
                    presenter.Restore(activateWindow: true);
            }
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            SetForegroundWindow(hwnd);
        }
        catch { /* Activate already requested focus */ }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);

    internal static bool HostIs(Uri uri, string suffix) =>
        uri.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Identity providers and captcha hosts a store login may put in the top
    /// frame. Hosts only; each is the provider's own sign-in origin.
    /// </summary>
    internal static bool IsIdentityProvider(Uri uri) =>
        // Google
        uri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("accounts.youtube.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("gds.google.com", StringComparison.OrdinalIgnoreCase) ||
        // Apple
        uri.Host.Equals("appleid.apple.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("idmsa.apple.com", StringComparison.OrdinalIgnoreCase) ||
        // Microsoft / Xbox
        uri.Host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("account.live.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("account.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("account.xbox.com", StringComparison.OrdinalIgnoreCase) ||
        HostIs(uri, "xboxlive.com") ||
        // PlayStation
        HostIs(uri, "account.sony.com") ||
        HostIs(uri, "sonyentertainmentnetwork.com") ||
        // Steam OpenID
        (uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) &&
         (uri.AbsolutePath.StartsWith("/openid", StringComparison.OrdinalIgnoreCase) ||
          uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase))) ||
        uri.Host.Equals("login.steampowered.com", StringComparison.OrdinalIgnoreCase) ||
        // Nintendo
        HostIs(uri, "accounts.nintendo.com") ||
        // Facebook
        HostIs(uri, "facebook.com") ||
        // LEGO
        HostIs(uri, "lego.com") ||
        // Disney
        uri.Host.Equals("registerdisney.go.com", StringComparison.OrdinalIgnoreCase) ||
        HostIs(uri, "disney.com") ||
        HostIs(uri, "disneyplus.com") ||
        // Discord (GOG)
        (uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) &&
         (uri.AbsolutePath.StartsWith("/oauth2", StringComparison.OrdinalIgnoreCase) ||
          uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase))) ||
        // Captcha / bot protection, should one take the top frame.
        HostIs(uri, "hcaptcha.com") ||
        HostIs(uri, "arkoselabs.com") ||
        HostIs(uri, "funcaptcha.com") ||
        HostIs(uri, "recaptcha.net") ||
        uri.Host.Equals("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase);
}
