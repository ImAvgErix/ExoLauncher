using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;
// DataWriter / InMemoryRandomAccessStream for cover resource handler

namespace ExoLauncher;

/// <summary>
/// Thin native shell: fixed 1400×900 AMOLED window + WebView2 product UI.
/// Launch/discovery stay in C# via <see cref="WebHostBridge"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int FixedWindowWidth = 1400;
    private const int FixedWindowHeight = 900;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
    private WebHostBridge? _bridge;
    private NotificationAreaIcon? _notificationAreaIcon;
    private TrophyNotificationPresenter? _trophyPresenter;
    private bool _movingToNotificationArea;
    private bool _webReady;
    private Task? _ensureWebTask;

    public MainWindow()
    {
        InitializeComponent();
        App.MainAppWindow = this;

        try
        {
            ApplyFixedChrome();
            ApplyInitialWindowBounds();
            TryCenterOnScreen();
            TrySetWindowIcon();
        }
        catch { }

        ExtendsContentIntoTitleBar = true;
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
                op.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }
        catch { }

        try
        {
            var tb = AppWindow.TitleBar;
            tb.ExtendsContentIntoTitleBar = true;
            tb.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
            var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            var black = Windows.UI.Color.FromArgb(255, 0, 0, 0);
            var ink = Windows.UI.Color.FromArgb(255, 233, 233, 236);
            var dim = Windows.UI.Color.FromArgb(255, 106, 106, 112);
            var hover = Windows.UI.Color.FromArgb(255, 26, 26, 31);
            tb.BackgroundColor = black;
            tb.InactiveBackgroundColor = black;
            tb.ForegroundColor = ink;
            tb.InactiveForegroundColor = dim;
            tb.ButtonBackgroundColor = transparent;
            tb.ButtonInactiveBackgroundColor = transparent;
            tb.ButtonForegroundColor = ink;
            tb.ButtonInactiveForegroundColor = dim;
            tb.ButtonHoverBackgroundColor = hover;
            tb.ButtonHoverForegroundColor = ink;
            tb.ButtonPressedBackgroundColor = hover;
            tb.ButtonPressedForegroundColor = ink;
        }
        catch { }

        try { SetTitleBar(AppTitleBar); } catch { }

        // Install the native minimize hook immediately so taskbar/keyboard
        // minimize follows the same notification-area behavior as the web UI.
        try { EnsureNotificationAreaIcon(); } catch { }
        try
        {
            _trophyPresenter = new TrophyNotificationPresenter(DispatcherQueue);
            App.Services.TrophyNotifications.Requested += OnTrophyNotificationRequested;
            // The native presenter is ready. Pending deliveries will refresh
            // and replay only for the currently verified provider account.
            _ = Task.Run(App.Services.ReplayPendingAchievementNotificationsAsync);
        }
        catch { }

        RootGrid.Loaded += async (_, _) =>
        {
            ApplyFixedChrome();
            await EnsureWebAsync();
        };
        LogStartupMilestone("window-constructed");
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                _ = EnsureWebAsync();
        };
        Closed += (_, _) =>
        {
            _lifetimeCts.Cancel();
            try { _notificationAreaIcon?.Dispose(); } catch { }
            try { App.Services.TrophyNotifications.Requested -= OnTrophyNotificationRequested; } catch { }
            try { _trophyPresenter?.Dispose(); } catch { }
            try { _bridge?.Detach(); } catch { }
            try { App.Services.Shutdown(); } catch { }
            App.Services.Settings.Flush();
            App.MainAppWindow = null;
        };
    }

    private void OnTrophyNotificationRequested(TrophyNotificationRequest request)
    {
        void Show() => _trophyPresenter?.Enqueue(
            request.Payload,
            App.Services.Settings.Current,
            request.OnPresented);
        if (!DispatcherQueue.HasThreadAccess) DispatcherQueue.TryEnqueue(Show);
        else Show();
    }

    public void HideForGameplay()
    {
        if (_movingToNotificationArea) return;
        _movingToNotificationArea = true;
        try
        {
            EnsureNotificationAreaIcon();
            if (!_notificationAreaIcon!.Show())
            {
                if (AppWindow.Presenter is OverlappedPresenter fallbackPresenter)
                    fallbackPresenter.Minimize();
                return;
            }

            AppWindow.IsShownInSwitchers = false;
            AppWindow.Hide();
        }
        catch
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.Minimize();
            }
            catch { }
        }
        finally
        {
            _movingToNotificationArea = false;
        }
    }

    private void EnsureNotificationAreaIcon()
    {
        if (_notificationAreaIcon is not null) return;
        var hwnd = WindowNative.GetWindowHandle(this);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ExoLauncher.ico");
        _notificationAreaIcon = new NotificationAreaIcon(
            hwnd,
            iconPath,
            RestoreFromNotificationArea,
            HideForGameplay);
    }

    private void RestoreFromNotificationArea()
        => RestoreAndActivate();

    public void RestoreAndActivate()
    {
        try
        {
            _notificationAreaIcon?.Hide();
            AppWindow.IsShownInSwitchers = true;
            AppWindow.Show();
            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Restore();
            Activate();
        }
        catch { }
    }

    private Task EnsureWebAsync()
    {
        if (_webReady) return Task.CompletedTask;
        return _ensureWebTask ??= EnsureWebCoreAsync();
    }

    private async Task EnsureWebCoreAsync()
    {
        if (_webReady) return;
        ShowBootPanel("Starting Exo Launcher…");
        LogStartupMilestone("webview-init-start");
        try
        {
            try
            {
                // Opt-in DOM eyes for Aether: EXO_CDP=1 → WebView2 --remote-debugging-port=9229
                var cdp = Environment.GetEnvironmentVariable("EXO_CDP")
                    ?? Environment.GetEnvironmentVariable("EXOOS_CDP")
                    ?? Environment.GetEnvironmentVariable("AETHER_CDP");
                if (string.Equals(cdp, "1", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cdp, "true", StringComparison.OrdinalIgnoreCase))
                {
                    var args = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") ?? "";
                    if (!args.Contains("remote-debugging-port", StringComparison.OrdinalIgnoreCase))
                    {
                        var port = Environment.GetEnvironmentVariable("EXO_CDP_PORT") ?? "9229";
                        var add = $"--remote-debugging-port={port}";
                        Environment.SetEnvironmentVariable(
                            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                            string.IsNullOrWhiteSpace(args) ? add : $"{args} {add}");
                    }
                }

                // Keep WebView state outside the replaceable application tree.
                // Otherwise a short-lived Edge child can hold the previous
                // version's app folder open during an atomic installer swap.
                var webViewUserData = Path.Combine(PathHelper.AppDataDir, "webview");
                var webViewEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(
                    browserExecutableFolder: null,
                    userDataFolder: webViewUserData,
                    options: new CoreWebView2EnvironmentOptions());
                await WebHost.EnsureCoreWebView2Async(webViewEnvironment);
                LogStartupMilestone("webview-core-ready");
            }
            catch
            {
                ShowWebFallback();
                _ensureWebTask = null;
                return;
            }

            var core = WebHost.CoreWebView2;
            if (core is null)
            {
                ShowWebFallback();
                _ensureWebTask = null;
                return;
            }

            core.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    LogStartupMilestone("webview-navigation-complete");
                    RevealWeb();
                }
                else ShowWebFallback();
            };

            var www = ResolveWwwRoot();
            if (www is null)
            {
                core.NavigateToString(
                    "<html><body style='background:#000;color:#fff;font-family:Segoe UI;padding:24px'>" +
                    "<h2>Exo Launcher UI not built</h2><p>Run: <code>cd ui &amp;&amp; npm ci &amp;&amp; npm run build</code></p></body></html>");
                _webReady = true;
                return;
            }

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            try { core.Settings.IsWebMessageEnabled = true; } catch { }
            try { core.Settings.AreHostObjectsAllowed = false; } catch { }
#if DEBUG
            try { core.Settings.AreDevToolsEnabled = true; } catch { }
#else
            core.Settings.AreDevToolsEnabled = false;
#endif

            core.SetVirtualHostNameToFolderMapping(
                WebViewTrustPolicy.TrustedAppHost,
                www,
                CoreWebView2HostResourceAccessKind.Allow);

            core.NavigationStarting += OnWebNavigationStarting;
            core.NewWindowRequested += OnWebNewWindowRequested;

            // Local covers via virtual host + explicit resource handler (belt and suspenders).
            try
            {
                Directory.CreateDirectory(Services.CoverArtService.CacheRoot);
                core.SetVirtualHostNameToFolderMapping(
                    Services.CoverArtService.VirtualHost,
                    Services.CoverArtService.CacheRoot,
                    CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { /* handler below still serves covers */ }

            try
            {
                core.AddWebResourceRequestedFilter(
                    $"https://{Services.CoverArtService.VirtualHost}/*",
                    CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += CoverResourceRequested;
            }
            catch { /* virtual host alone may still work */ }

            try { _bridge?.Detach(); } catch { }
            _bridge = new WebHostBridge(App.Services, DispatcherQueue);
            _bridge.Attach(core);
            LogStartupMilestone("bridge-attached");

            WebHost.Source = new Uri(WebViewTrustPolicy.TrustedAppStartUri);
            LogStartupMilestone("webview-navigation-start");
            _webReady = true;
        }
        catch
        {
            _ensureWebTask = null;
            ShowWebFallback();
        }
    }

    private static void OnWebNavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (WebViewTrustPolicy.IsTrustedAppUri(e.Uri)) return;

        e.Cancel = true;
        AppLog.Warn("Blocked an untrusted main-frame WebView navigation.");
    }

    private static void OnWebNewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        // The shell has an allowlisted native open-url RPC. No document should
        // create another privileged or unmanaged WebView window directly.
        e.Handled = true;
        AppLog.Warn("Blocked a WebView new-window request.");
    }

    private void ShowBootPanel(string text)
    {
        WebBootText.Text = text;
        WebBootPanel.Visibility = Visibility.Visible;
        WebViewFallback.Visibility = Visibility.Collapsed;
        WebHost.Visibility = Visibility.Collapsed;
    }

    private void RevealWeb()
    {
        WebBootPanel.Visibility = Visibility.Collapsed;
        WebViewFallback.Visibility = Visibility.Collapsed;
        WebHost.Visibility = Visibility.Visible;
        LogStartupMilestone("webview-visible");
    }

    private void LogStartupMilestone(string milestone) =>
        AppLog.Info($"PERF startup milestone={milestone} elapsedMs={_startupStopwatch.ElapsedMilliseconds}");

    private void ShowWebFallback()
    {
        WebBootPanel.Visibility = Visibility.Collapsed;
        WebHost.Visibility = Visibility.Collapsed;
        WebViewFallback.Visibility = Visibility.Visible;
    }

    private void WebViewRestartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { }
        try { Application.Current?.Exit(); } catch { }
    }

    private void WebViewFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703")
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private static string? ResolveWwwRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "wwwroot"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "wwwroot")),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "index.html")))
                return c;
        }
        return null;
    }

    /// <summary>
    /// Serve cover files from disk for https://covers.exo-launcher.local/*.
    /// Synchronous in-memory body so WebView2 always gets a complete response.
    /// </summary>
    private void CoverResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = e.Request.Uri;
            if (string.IsNullOrWhiteSpace(uri) ||
                !uri.Contains(Services.CoverArtService.VirtualHost, StringComparison.OrdinalIgnoreCase))
                return;

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return;
            var name = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/'));
            if (string.IsNullOrWhiteSpace(name) || name.Contains("..", StringComparison.Ordinal) ||
                name.Contains('/') || name.Contains('\\'))
                return;

            var path = Path.GetFullPath(Path.Combine(Services.CoverArtService.CacheRoot, name));
            var root = Path.GetFullPath(Services.CoverArtService.CacheRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                return;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < Services.CoverArtService.MinCoverBytes) return;

            // Write into a MemoryStream and expose as IRandomAccessStream via AsRandomAccessStream.
            var ms = new MemoryStream(bytes, writable: false);
            var contentType = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                : path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
                : "image/jpeg";

            e.Response = sender.Environment.CreateWebResourceResponse(
                ms.AsRandomAccessStream(),
                200,
                "OK",
                $"Content-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=86400\r\n");
        }
        catch
        {
            /* monogram fallback in UI */
        }
    }

    private void ApplyFixedChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Fixed shell — resize is not a user option.
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
            presenter.IsMinimizable = true;
            presenter.PreferredMinimumWidth = FixedWindowWidth;
            presenter.PreferredMinimumHeight = FixedWindowHeight;
            try
            {
                presenter.PreferredMaximumWidth = FixedWindowWidth;
                presenter.PreferredMaximumHeight = FixedWindowHeight;
            }
            catch { }
            // Thin system border only — no double chrome with custom titlebar.
            try { presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false); }
            catch { }
            try
            {
                // Prefer a clean dark edge on Windows 11 when available.
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
            }
            catch { }
        }
    }

    private void ApplyInitialWindowBounds()
    {
        try
        {
            var scale = 1.0;
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) scale = dpi / 96.0;
            }
            catch { }

            var w = (int)Math.Round(FixedWindowWidth * scale);
            var h = (int)Math.Round(FixedWindowHeight * scale);

            try
            {
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
                if (area is not null)
                {
                    w = Math.Min(w, area.WorkArea.Width);
                    h = Math.Min(h, area.WorkArea.Height);
                }
            }
            catch { }

            AppWindow.Resize(new SizeInt32(w, h));
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void TryCenterOnScreen()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            var display = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Nearest);
            if (display is null) return;
            var work = display.WorkArea;
            var x = work.X + (work.Width - appWindow.Size.Width) / 2;
            var y = work.Y + (work.Height - appWindow.Size.Height) / 2;
            appWindow.Move(new PointInt32(x, y));
        }
        catch { }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            ContentHost.ClearValue(FrameworkElement.WidthProperty);
            ContentHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        catch { }
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "ExoLauncher.ico");
            if (File.Exists(ico))
                AppWindow.SetIcon(ico);
        }
        catch { }
    }
}
