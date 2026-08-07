using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using WinRT.Interop;

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
    private WebHostBridge? _bridge;
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

        RootGrid.Loaded += async (_, _) =>
        {
            ApplyFixedChrome();
            await EnsureWebAsync();
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                _ = EnsureWebAsync();
        };
        Closed += (_, _) =>
        {
            _lifetimeCts.Cancel();
            try { _bridge?.Detach(); } catch { }
            App.Services.Settings.Flush();
            App.MainAppWindow = null;
        };
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
        try
        {
            try
            {
                await WebHost.EnsureCoreWebView2Async();
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
                if (args.IsSuccess) RevealWeb();
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
#if DEBUG
            try { core.Settings.AreDevToolsEnabled = true; } catch { }
#else
            core.Settings.AreDevToolsEnabled = false;
#endif

            core.SetVirtualHostNameToFolderMapping(
                "app.exo-launcher.local",
                www,
                CoreWebView2HostResourceAccessKind.Allow);

            try { _bridge?.Detach(); } catch { }
            _bridge = new WebHostBridge(App.Services, DispatcherQueue);
            _bridge.Attach(core);

            WebHost.Source = new Uri("https://app.exo-launcher.local/index.html");
            _webReady = true;
        }
        catch
        {
            _ensureWebTask = null;
            ShowWebFallback();
        }
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
    }

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

    private void ApplyFixedChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
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
            try { presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false); }
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
