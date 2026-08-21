using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
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
/// Thin native shell: default 1400×900 AMOLED window + WebView2 product UI.
/// The window is resizable and maximizable, with a 1100×700 floor.
/// Launch/discovery stay in C# via <see cref="WebHostBridge"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWindowWidth = 1400;
    private const int DefaultWindowHeight = 900;
    private const int MinWindowWidth = 1100;
    private const int MinWindowHeight = 700;
    private const int TitleBarDragDip = 52;
    private const int TitleBarLogoPassthroughDip = 280;
    private const int TitleBarActionsPassthroughDip = 176;
    // The search pill is centered in the titlebar, so it needs its own
    // passthrough band. Without it the caption sink eats clicks on the field.
    // Covers the focused/query width; the resting web pill is intentionally narrower.
    private const int TitleBarSearchPassthroughDip = 184;
    private const int TitleBarSearchHeightDip = 32;

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
            ApplyWindowChrome();
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

        try { AppTitleBar.IsHitTestVisible = false; } catch { }
        try { UpdateCaptionDragRegions(); } catch { }

        try { AppWindow.Changed += OnAppWindowChanged; } catch { }

        // Install the native minimize hook immediately so taskbar/keyboard
        // minimize follows the same notification-area behavior as the web UI.
        try { EnsureNotificationAreaIcon(); } catch { }
        try
        {
            _trophyPresenter = new TrophyNotificationPresenter(DispatcherQueue);
            App.Services.TrophyNotifications.Requested += OnTrophyNotificationRequested;
            TryCaptureTrophyBanners();
            // The native presenter is ready. Pending deliveries will refresh
            // and replay only for the currently verified provider account.
            _ = Task.Run(App.Services.ReplayPendingAchievementNotificationsAsync);
        }
        catch { }

        RootGrid.Loaded += async (_, _) =>
        {
            ApplyWindowChrome();
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
            try { AppWindow.Changed -= OnAppWindowChanged; } catch { }
            try { _notificationAreaIcon?.Dispose(); } catch { }
            try { App.Services.TrophyNotifications.Requested -= OnTrophyNotificationRequested; } catch { }
            try { _trophyPresenter?.Dispose(); } catch { }
            try { _bridge?.Detach(); } catch { }
            try { App.Services.Shutdown(); } catch { }
            App.Services.Settings.Flush();
            App.MainAppWindow = null;
        };
    }

    private void TryCaptureTrophyBanners()
    {
        var request = Environment.GetEnvironmentVariable("EXO_TROPHY_CAPTURE");
        if (string.IsNullOrWhiteSpace(request)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var part in request.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var rarity = TrophyBannerDesign.ParseRarity(part);
                if (rarity == TrophyRarity.Unknown) continue;
                App.Services.TrophyNotifications.Preview(null, null, null, rarity, null);
            }
        });
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
                var webViewEnvironment = await WebViewEnvironmentFactory.GetAsync();
                await WebHost.EnsureCoreWebView2Async(webViewEnvironment);
                LogStartupMilestone("webview-core-ready");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"webview-init-failed: {ex.Message}");
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
            try { core.Settings.IsPasswordAutosaveEnabled = false; } catch { }
            try { core.Settings.IsWebMessageEnabled = true; } catch { }
            try { core.Settings.AreHostObjectsAllowed = false; } catch { }
            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
#if DEBUG
            try { core.Settings.AreDevToolsEnabled = true; } catch { }
#else
            core.Settings.AreDevToolsEnabled = false;
#endif

            core.SetVirtualHostNameToFolderMapping(
                WebViewTrustPolicy.TrustedAppHost,
                www,
                CoreWebView2HostResourceAccessKind.DenyCors);

            core.NavigationStarting += OnWebNavigationStarting;
            core.NewWindowRequested += OnWebNewWindowRequested;

            // Virtual-folder mapping is the fast path. The explicit handler is
            // retained only as a startup fallback; intercepting every mapped
            // image would synchronously read cover bytes on the UI thread.
            var coverVirtualHostMapped = false;
            try
            {
                Directory.CreateDirectory(Services.CoverArtService.CacheRoot);
                core.SetVirtualHostNameToFolderMapping(
                    Services.CoverArtService.VirtualHost,
                    Services.CoverArtService.CacheRoot,
                    CoreWebView2HostResourceAccessKind.DenyCors);
                coverVirtualHostMapped = true;
            }
            catch { /* handler below still serves covers */ }

            if (!coverVirtualHostMapped)
            {
                try
                {
                    core.AddWebResourceRequestedFilter(
                        $"https://{Services.CoverArtService.VirtualHost}/*",
                        CoreWebView2WebResourceContext.All);
                    core.WebResourceRequested += CoverResourceRequested;
                }
                catch { /* virtual host alone may still work */ }
            }

            try { _bridge?.Detach(); } catch { }
            _bridge = new WebHostBridge(App.Services, DispatcherQueue);
            _bridge.Attach(core);
            _bridge.NotifyWindowState(IsMaximized);
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
        UpdateCaptionDragRegions();
        LogStartupMilestone("webview-visible");
        App.Services.StartDeferredServices();
        _trophyPresenter?.Warm();
    }

    private void LogStartupMilestone(string milestone) =>
        AppLog.Info($"PERF startup milestone={milestone} elapsedMs={_startupStopwatch.ElapsedMilliseconds}");

    private void ShowWebFallback()
    {
        WebBootPanel.Visibility = Visibility.Collapsed;
        WebHost.Visibility = Visibility.Collapsed;
        WebViewFallback.Visibility = Visibility.Visible;
        App.Services.StartDeferredServices();
        WebViewRestartButton.Focus(FocusState.Programmatic);
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
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) ||
                !string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(u.IdnHost, Services.CoverArtService.VirtualHost, StringComparison.OrdinalIgnoreCase) ||
                u.Port != 443 || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Fragment))
                return;
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
                : path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif"
                : "image/jpeg";

            e.Response = sender.Environment.CreateWebResourceResponse(
                ms.AsRandomAccessStream(),
                200,
                "OK",
                $"Content-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: public, max-age=86400\r\n");
        }
        catch
        {
            /* monogram fallback in UI */
        }
    }

    public bool IsMaximized =>
        AppWindow.Presenter is OverlappedPresenter presenter &&
        presenter.State == OverlappedPresenterState.Maximized;

    public bool ToggleMaximize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return false;
        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
            return false;
        }
        presenter.Maximize();
        return true;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange && !args.DidPositionChange && !args.DidSizeChange) return;

        // AppWindow minimums are physical pixels. Moving between monitors can
        // change DPI without changing the logical 1100×700 product floor.
        ApplyWindowMinimumSize();
        if (args.DidPresenterChange || args.DidSizeChange)
            _bridge?.NotifyWindowState(IsMaximized);
        if (args.DidPositionChange || args.DidSizeChange)
            UpdateCaptionDragRegions();
    }

    private void ApplyWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
            presenter.IsMinimizable = true;
            ApplyWindowMinimumSize();
            try { presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false); }
            catch { }
            try
            {
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
            }
            catch { }
        }
        UpdateCaptionDragRegions();
    }

    private void ApplyWindowMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var scale = GetWindowScale();
        presenter.PreferredMinimumWidth = Math.Max(1, (int)Math.Round(MinWindowWidth * scale));
        presenter.PreferredMinimumHeight = Math.Max(1, (int)Math.Round(MinWindowHeight * scale));
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

            var w = (int)Math.Round(DefaultWindowWidth * scale);
            var h = (int)Math.Round(DefaultWindowHeight * scale);

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
        ApplyWindowMinimumSize();
        UpdateCaptionDragRegions();
    }

    /// <summary>
    /// WinUI WebView2 does not honor CSS app-region. Caption rects make the
    /// empty titlebar (beside search, above/below the pill) a real drag target
    /// without covering the logo, search field, or window buttons.
    /// </summary>
    private void UpdateCaptionDragRegions()
    {
        try
        {
            var widthDip = RootGrid.ActualWidth;
            if (widthDip <= 0) return;
            var scale = GetWindowScale();
            // Region rects are physical pixels. AppWindow.ClientSize is the only
            // authority for that width: RootGrid.ActualWidth x scale drifts from
            // it under some DPI states, and the right-anchored band then lands
            // left of the real button cluster, so the settings, profile, and
            // window buttons stop taking mouse clicks entirely.
            var clientWidth = 0;
            try { clientWidth = AppWindow.ClientSize.Width; } catch { }
            var width = clientWidth > 0
                ? clientWidth
                : Math.Max(1, (int)Math.Round(widthDip * scale));
            var titleH = Math.Max(1, (int)Math.Round(TitleBarDragDip * scale));
            var logoW = Math.Max(1, (int)Math.Round(TitleBarLogoPassthroughDip * scale));
            var actionsW = Math.Max(1, (int)Math.Round(TitleBarActionsPassthroughDip * scale));
            var searchW = Math.Max(1, (int)Math.Round(TitleBarSearchPassthroughDip * scale));
            if (logoW + actionsW + searchW >= width) return;

            var searchX = Math.Max(logoW, (width - searchW) / 2);
            var searchRight = Math.Min(width - actionsW, searchX + searchW);
            searchW = Math.Max(1, searchRight - searchX);
            var searchPillH = Math.Min(titleH, Math.Max(1, (int)Math.Round(TitleBarSearchHeightDip * scale)));
            var pillTop = Math.Max(0, (titleH - searchPillH) / 2);
            var pillBottom = pillTop + searchPillH;

            var rects = new List<RectInt32>();
            var leftW = searchX - logoW;
            if (leftW > 8)
                rects.Add(new RectInt32(logoW, 0, leftW, titleH));
            var rightX = searchRight;
            var rightW = width - actionsW - rightX;
            if (rightW > 8)
                rects.Add(new RectInt32(rightX, 0, rightW, titleH));
            if (searchW > 8 && pillTop > 0)
                rects.Add(new RectInt32(searchX, 0, searchW, pillTop));
            var belowH = titleH - pillBottom;
            if (searchW > 8 && belowH > 0)
                rects.Add(new RectInt32(searchX, pillBottom, searchW, belowH));

            if (rects.Count == 0) return;
            var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            source.SetRegionRects(NonClientRegionKind.Caption, rects.ToArray());
            // Caption exclusion is not enough: ExtendsContentIntoTitleBar still
            // sinks clicks in the top strip. Passthrough hands logo, search,
            // and the settings/window cluster back to WebView2.
            source.SetRegionRects(NonClientRegionKind.Passthrough, [
                new RectInt32(0, 0, logoW, titleH),
                new RectInt32(searchX, pillTop, searchW, searchPillH),
                new RectInt32(width - actionsW, 0, actionsW, titleH),
            ]);
        }
        catch
        {
            /* caption regions are best-effort; SetTitleBar remains the fallback */
        }
    }

    private double GetWindowScale()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        catch { }
        return 1.0;
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
