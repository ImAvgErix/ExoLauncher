using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace ExoLauncher.Services;

/// <summary>
/// Pre-warmed trophy overlay. The visible banner is the same React
/// <c>TrophyBanner</c> as settings. The host is a Win32 layered popup with a
/// WebView2 controller — not the WinUI WebView2 control, which cannot do
/// real transparency. HWND_TOPMOST covers borderless fullscreen; exclusive fullscreen cannot be covered.
/// </summary>
internal sealed class TrophyNotificationPresenter : IDisposable
{
    internal const string OverlayDocument = "trophy.html";
    internal const string OverlayStartUri = "https://" + WebViewTrustPolicy.TrustedAppHost + "/" + OverlayDocument;
    private const string OverlayClassName = "ExoLauncherTrophyOverlay";
    private const string TrophyIconHost = "trophy-icons.exo-launcher.local";

    private const int GwlExStyle = -20;
    private const int GwlpUserData = -21;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExTopmost = 0x00000008;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceDonotRound = 1;
    private const int DwmBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint DwmBbEnable = 0x1;
    private const uint DwmBbBlurRegion = 0x2;
    private const int HwndTopmost = -1;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const int SwShowNoActivate = 4;
    private const int SwHide = 0;
    private const int WmSize = 0x0005;
    private const int WmDestroy = 0x0002;
    private const int WmDpiChanged = 0x02E0;
    private const int ErrorClassAlreadyExists = 1410;

    private static readonly ConcurrentDictionary<nint, TrophyNotificationPresenter> Hosts = new();
    private static readonly WndProc OverlayWndProc = OnOverlayMessage;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static bool _classRegistered;

    private readonly Queue<(TrophyNotificationPayload Payload, TrophyNotificationOptions Options, Action? OnPresented)> _queue = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IntPtr _hwnd;
    private GCHandle _selfHandle;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _web;
    private DispatcherQueueTimer? _timer;
    private bool _visible;
    private bool _closing;
    private bool _disposed;
    private bool _warming;
    private bool _pageReady;
    private int _warmAttempts;

    public TrophyNotificationPresenter(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Warms the overlay after the main shell is visible. A real queued trophy
    /// still starts warming immediately through <see cref="Enqueue"/>.
    /// </summary>
    public void Warm()
    {
        if (_disposed || _web is not null || _warming) return;
        EnqueueWarm();
    }

    public void Enqueue(
        TrophyNotificationPayload payload,
        AppSettings settings,
        Action? onPresented = null)
    {
        if (_disposed) return;
        _queue.Enqueue((payload, TrophyNotificationOptions.From(settings), onPresented));
        if (!_visible && !_closing) ShowNext();
    }

    private void EnqueueWarm()
    {
        if (_dispatcher.HasThreadAccess) _ = WarmAsync();
        else _dispatcher.TryEnqueue(() => _ = WarmAsync());
    }

    private async Task WarmAsync()
    {
        if (_disposed || _web is not null || _warming) return;
        if (_warmAttempts >= 3) return;
        _warmAttempts++;
        _warming = true;
        try
        {
            var spec = TrophyBannerDesign.Current;
            EnsureWindowClass();
            var hwnd = CreateOverlayWindow(spec);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("Trophy overlay window was not created.");
            _hwnd = hwnd;
            _selfHandle = GCHandle.Alloc(this);
            SetWindowLongPtr(hwnd, GwlpUserData, GCHandle.ToIntPtr(_selfHandle));
            Hosts[hwnd] = this;
            ApplyOverlayChrome(hwnd);

            var www = ResolveWwwRoot();
            if (www is null || !File.Exists(Path.Combine(www, OverlayDocument)))
                throw new InvalidOperationException("Trophy overlay document is missing. Expected wwwroot/" + OverlayDocument + ".");

            var environment = await WebViewEnvironmentFactory.GetAsync();

            var parent = CoreWebView2ControllerWindowReference.CreateFromWindowHandle(unchecked((ulong)hwnd.ToInt64()));
            CoreWebView2ControllerOptions? controllerOptions = null;
            try
            {
                // Transparent corners are per-controller, not per-environment.
                controllerOptions = environment.CreateCoreWebView2ControllerOptions();
                controllerOptions.DefaultBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            }
            catch { /* Older runtimes still accept DefaultBackgroundColor on the controller. */ }

            _controller = controllerOptions is null
                ? await environment.CreateCoreWebView2ControllerAsync(parent)
                : await environment.CreateCoreWebView2ControllerAsync(parent, controllerOptions);
            _controller.DefaultBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            _controller.IsVisible = false;
            try { _controller.ShouldDetectMonitorScaleChanges = true; } catch { }
            try { _controller.AllowExternalDrop = false; } catch { }
            _controller.RasterizationScale = DpiScale(hwnd);
            SyncControllerBounds();

            var web = _controller.CoreWebView2
                ?? throw new InvalidOperationException("Trophy overlay WebView2 core was not created.");
            _web = web;
            web.Settings.IsStatusBarEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.IsZoomControlEnabled = false;
            try { web.Settings.AreBrowserAcceleratorKeysEnabled = false; } catch { }
            try { web.Settings.IsWebMessageEnabled = true; } catch { }
            try { web.Settings.AreHostObjectsAllowed = false; } catch { }
            try { web.Settings.AreDevToolsEnabled = OverlayCdpRequested(); } catch { }
            try { web.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }

            web.SetVirtualHostNameToFolderMapping(
                WebViewTrustPolicy.TrustedAppHost,
                www,
                CoreWebView2HostResourceAccessKind.DenyCors);
            try
            {
                Directory.CreateDirectory(CoverArtService.CacheRoot);
                web.SetVirtualHostNameToFolderMapping(
                    CoverArtService.VirtualHost,
                    CoverArtService.CacheRoot,
                    CoreWebView2HostResourceAccessKind.DenyCors);
            }
            catch { }
            try
            {
                var icons = Path.Combine(PathHelper.AppDataDir, "achievement-icons");
                Directory.CreateDirectory(icons);
                web.SetVirtualHostNameToFolderMapping(
                    TrophyIconHost,
                    icons,
                    CoreWebView2HostResourceAccessKind.DenyCors);
            }
            catch { }

            web.NavigationStarting += OnNavigationStarting;
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.WebMessageReceived += OnWebMessage;

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                web.NavigationCompleted -= OnLoaded;
                if (args.IsSuccess) loaded.TrySetResult(true);
                else loaded.TrySetException(new InvalidOperationException("Trophy overlay failed to load."));
            }
            web.NavigationCompleted += OnLoaded;
            web.Navigate(OverlayStartUri);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(12));
            var pageDeadline = DateTime.UtcNow.AddMilliseconds(1500);
            while (!_pageReady && DateTime.UtcNow < pageDeadline)
                await Task.Delay(40);
            HideOverlay();
            _ready.TrySetResult(true);
            ShowNext();
        }
        catch (Exception ex)
        {
            AppLog.Error("Trophy overlay failed to pre-warm", ex);
            DestroyOverlay();
        }
        finally
        {
            _warming = false;
        }
    }

    private void ShowNext()
    {
        if (_disposed || _visible || _closing || _queue.Count == 0) return;
        if (_web is null)
        {
            if (_warmAttempts >= 3 && _ready.Task.IsCompleted) return;
            EnqueueWarm();
            _ = WaitThenShow();
            return;
        }

        var (payload, options, onPresented) = _queue.Dequeue();
        try
        {
            var spec = TrophyBannerDesign.Current;
            var rarity = payload.Rarity != TrophyRarity.Unknown
                ? payload.Rarity
                : payload.IsPerfect ? TrophyRarity.Platinum
                : payload.IsRare ? TrophyRarity.Gold
                : TrophyRarity.Bronze;
            var reduced = !AnimationsEnabled();
            PostJson(new Dictionary<string, object?>
            {
                ["type"] = "show",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["tier"] = TrophyBannerDesign.Key(rarity),
                ["name"] = (payload.AchievementName ?? "").Trim(),
                ["detail"] = OverlayDetail(payload),
                ["game"] = (payload.GameTitle ?? "").Trim(),
                ["iconUrl"] = OverlayIconUrl(payload.IconUrl),
                ["reducedMotion"] = reduced,
            });
            PlaceOverlay(options.PositionX, options.PositionY, spec);
            ShowOverlay();
            _visible = true;
            _closing = false;
            try { onPresented?.Invoke(); }
            catch (Exception ex) { AppLog.Debug("Trophy presentation acknowledgement failed: " + ex.Message); }
            TrophySoundPlayer.Play(rarity);

            var arrivalMs = reduced
                ? Math.Max(0, spec.Motion.ReducedFadeMs)
                : Math.Max(0, spec.Tier(rarity).EnterMs + spec.Tier(rarity).SettleMs);
            // Duration is the readable hold after arrival, not the entire lifecycle.
            var holdMs = arrivalMs + Math.Max(options.DurationSeconds, 1) * 1000;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXO_TROPHY_CAPTURE")))
                holdMs = Math.Max(holdMs, 16_000);
            _timer = _dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(holdMs);
            _timer.IsRepeating = false;
            _timer.Tick += OnTimer;
            _timer.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("Trophy notification display failed", ex);
            ParkCurrent();
            ShowNext();
        }
    }

    private async Task WaitThenShow()
    {
        try { await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { return; }
        if (_dispatcher.HasThreadAccess) ShowNext();
        else _dispatcher.TryEnqueue(ShowNext);
    }

    private void BeginCloseCurrent()
    {
        if (_closing || !_visible) return;
        _closing = true;
        StopTimer();
        var exitMs = AnimationsEnabled() ? Math.Max(0, TrophyBannerDesign.Current.Motion.ExitMs) : TrophyBannerDesign.Current.Motion.ReducedFadeMs;
        try { PostJson(new Dictionary<string, object?> { ["type"] = "hide" }); }
        catch { exitMs = 0; }

        if (exitMs <= 0)
        {
            ParkCurrent();
            ShowNext();
            return;
        }

        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(exitMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            try { timer.Stop(); } catch { }
            ParkCurrent();
            if (!_disposed) ShowNext();
        };
        timer.Start();
    }

    private void ParkCurrent()
    {
        StopTimer();
        try { PostJson(new Dictionary<string, object?> { ["type"] = "clear" }); } catch { }
        HideOverlay();
        _visible = false;
        _closing = false;
    }

    private void PostJson(Dictionary<string, object?> payload)
    {
        if (_web is null) return;
        _web.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private void PlaceOverlay(double positionX, double positionY, TrophyBannerSpec spec)
    {
        if (_hwnd == IntPtr.Zero) return;
        var display = ResolveDisplay(_hwnd);
        var pad = (int)Math.Round(spec.OverlayPad * display.Scale);
        var width = (int)Math.Round(spec.Width * display.Scale) + (pad * 2);
        var height = (int)Math.Round(spec.Height * display.Scale) + (pad * 2);
        var card = TrophyNotificationLayout.Calculate(
            display.WorkArea.X,
            display.WorkArea.Y,
            display.WorkArea.Width,
            display.WorkArea.Height,
            width - (pad * 2),
            height - (pad * 2),
            positionX,
            positionY,
            (int)Math.Round(24 * display.Scale));
        var left = card.Left - pad;
        var top = card.Top - pad;
        if (_controller is not null)
        {
            try { _controller.RasterizationScale = display.Scale; } catch { }
        }
        _ = SetWindowPos(
            _hwnd,
            new IntPtr(HwndTopmost),
            left,
            top,
            width,
            height,
            SwpNoActivate | SwpShowWindow);
        SyncControllerBounds();
    }

    private void ShowOverlay()
    {
        if (_hwnd == IntPtr.Zero) return;
        _ = ShowWindow(_hwnd, SwShowNoActivate);
        _ = SetWindowPos(
            _hwnd,
            new IntPtr(HwndTopmost),
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
        if (_controller is not null)
        {
            try { _controller.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
            try { _controller.IsVisible = true; } catch { }
        }
    }

    private void HideOverlay()
    {
        if (_hwnd == IntPtr.Zero) return;
        _ = SetWindowPos(
            _hwnd,
            new IntPtr(HwndTopmost),
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpHideWindow);
        _ = ShowWindow(_hwnd, SwHide);
        if (_controller is not null)
        {
            try { _controller.IsVisible = false; } catch { }
            try { _controller.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
        }
    }

    private void SyncControllerBounds()
    {
        if (_controller is null || _hwnd == IntPtr.Zero) return;
        if (!GetClientRect(_hwnd, out var rect)) return;
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        _controller.Bounds = new Rect(0, 0, width, height);
    }

    private void OnTimer(DispatcherQueueTimer sender, object args) => BeginCloseCurrent();

    private void StopTimer()
    {
        if (_timer is null) return;
        try { _timer.Stop(); } catch { }
        _timer.Tick -= OnTimer;
        _timer = null;
    }

    private static void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (WebViewTrustPolicy.IsTrustedAppUri(e.Uri)
            && e.Uri.Contains(OverlayDocument, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        e.Cancel = true;
        AppLog.Warn("Blocked an untrusted trophy overlay navigation.");
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = e.WebMessageAsJson;
            if (string.IsNullOrWhiteSpace(raw) || raw.IndexOf("ready", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            _pageReady = true;
        }
        catch { }
    }

    private static string OverlayDetail(TrophyNotificationPayload payload)
    {
        var gameTitle = (payload.GameTitle ?? "").Trim();
        var detail = (payload.Detail ?? "").Trim();
        if (string.Equals(detail, gameTitle, StringComparison.OrdinalIgnoreCase)) return "";
        return detail;
    }

    private static string? OverlayIconUrl(string? value)
    {
        if (!TryGetSafeIconUri(value, out var uri)) return null;
        if (IsTrustedVirtualIconUri(uri)) return uri.AbsoluteUri;
        if (!uri.IsFile) return null;

        var path = Path.GetFullPath(uri.LocalPath);
        var icons = Path.GetFullPath(Path.Combine(PathHelper.AppDataDir, "achievement-icons"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (path.StartsWith(icons, StringComparison.OrdinalIgnoreCase))
            return "https://" + TrophyIconHost + "/" + Path.GetFileName(path);

        var covers = Path.GetFullPath(CoverArtService.CacheRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (path.StartsWith(covers, StringComparison.OrdinalIgnoreCase))
            return CoverArtService.VirtualHostOrigin + "/" + Path.GetFileName(path);
        return null;
    }

    private static bool TryGetSafeIconUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            if (IsTrustedVirtualIconUri(parsed))
            {
                uri = parsed;
                return true;
            }

            if (parsed.IsFile && File.Exists(parsed.LocalPath))
            {
                uri = parsed;
                return true;
            }
        }

        try
        {
            if (Path.IsPathFullyQualified(value) && File.Exists(value))
            {
                uri = new Uri(Path.GetFullPath(value));
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsTrustedVirtualIconUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.IdnHost.Equals(TrophyIconHost, StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.Equals(CoverArtService.VirtualHost, StringComparison.OrdinalIgnoreCase));

    private static bool AnimationsEnabled()
    {
        try { return new UISettings().AnimationsEnabled; }
        catch { return true; }
    }

    private static bool OverlayCdpRequested()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXO_TROPHY_CAPTURE")))
            return true;
        var cdp = Environment.GetEnvironmentVariable("EXO_CDP")
            ?? Environment.GetEnvironmentVariable("EXOOS_CDP");
        return string.Equals(cdp, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cdp, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveWwwRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "wwwroot"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "wwwroot")),
        };
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, OverlayDocument)))
                return candidate;
        }
        return null;
    }

    private static TrophyDisplay ResolveDisplay(IntPtr notificationHwnd)
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                var foregroundId = Win32Interop.GetWindowIdFromWindow(foreground);
                var foregroundDisplay = DisplayArea.GetFromWindowId(foregroundId, DisplayAreaFallback.Nearest);
                if (foregroundDisplay is not null)
                    return new TrophyDisplay(foregroundDisplay.WorkArea, DpiScale(foreground));
            }
        }
        catch { }

        try
        {
            var id = Win32Interop.GetWindowIdFromWindow(notificationHwnd);
            var display = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
            if (display is not null)
                return new TrophyDisplay(display.WorkArea, DpiScale(notificationHwnd));
        }
        catch { }

        return new TrophyDisplay(new RectInt32(0, 0, 1920, 1080), DpiScale(notificationHwnd));
    }

    private static double DpiScale(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return Math.Clamp(dpi / 96d, 0.75d, 4d);
        }
        catch { }
        return 1d;
    }

    private static void EnsureWindowClass()
    {
        if (_classRegistered) return;
        var wnd = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(OverlayWndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = OverlayClassName,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
        };
        var atom = RegisterClassEx(ref wnd);
        if (atom == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorClassAlreadyExists)
                throw new InvalidOperationException("Trophy overlay class failed to register (" + error.ToString(CultureInfo.InvariantCulture) + ").");
        }
        _classRegistered = true;
    }

    private static IntPtr CreateOverlayWindow(TrophyBannerSpec spec)
    {
        var width = spec.Width + (spec.OverlayPad * 2);
        var height = spec.Height + (spec.OverlayPad * 2);
        var exStyle = WsExToolWindow | WsExNoActivate | WsExLayered | WsExTransparent | WsExTopmost;
        return CreateWindowEx(
            exStyle,
            OverlayClassName,
            "Achievement notification",
            WsPopup,
            -32000,
            -32000,
            width,
            height,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
    }

    private static void ApplyOverlayChrome(IntPtr hwnd)
    {
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(
            hwnd,
            GwlExStyle,
            new IntPtr(exStyle | WsExToolWindow | WsExNoActivate | WsExLayered | WsExTransparent | WsExTopmost));
        var margins = new Margins(-1, -1, -1, -1);
        try { _ = DwmExtendFrameIntoClientArea(hwnd, ref margins); }
        catch { }
        var cornerPreference = DwmWindowCornerPreferenceDonotRound;
        try { _ = DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref cornerPreference, sizeof(int)); }
        catch { }
        var noBorder = DwmColorNone;
        try { _ = DwmSetWindowAttributeU32(hwnd, DwmBorderColor, ref noBorder, sizeof(uint)); }
        catch { }
        EnableTransparentFrame(hwnd);
    }

    private static void EnableTransparentFrame(IntPtr hwnd)
    {
        var empty = IntPtr.Zero;
        try
        {
            empty = CreateRectRgn(0, 0, 0, 0);
            var blur = new DwmBlurBehind
            {
                DwFlags = DwmBbEnable | DwmBbBlurRegion,
                FEnable = 1,
                HRgnBlur = empty,
            };
            _ = DwmEnableBlurBehindWindow(hwnd, ref blur);
        }
        catch { }
        finally
        {
            if (empty != IntPtr.Zero)
            {
                try { _ = DeleteObject(empty); } catch { }
            }
        }
    }

    private static IntPtr OnOverlayMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Hosts.TryGetValue(hwnd, out var presenter))
        {
            if (msg == WmSize || msg == WmDpiChanged)
            {
                try { presenter.SyncControllerBounds(); } catch { }
                if (msg == WmDpiChanged && presenter._controller is not null)
                {
                    try { presenter._controller.RasterizationScale = DpiScale(hwnd); } catch { }
                }
            }
            else if (msg == WmDestroy)
            {
                Hosts.TryRemove(hwnd, out _);
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void DestroyOverlay()
    {
        StopTimer();
        var web = _web;
        if (web is not null)
        {
            try { web.NavigationStarting -= OnNavigationStarting; } catch { }
            try { web.WebMessageReceived -= OnWebMessage; } catch { }
        }
        _web = null;
        if (_controller is not null)
        {
            try { _controller.Close(); } catch { }
            _controller = null;
        }
        if (_hwnd != IntPtr.Zero)
        {
            Hosts.TryRemove(_hwnd, out _);
            try { _ = DestroyWindow(_hwnd); } catch { }
            _hwnd = IntPtr.Zero;
        }
        if (_selfHandle.IsAllocated)
        {
            try { _selfHandle.Free(); } catch { }
        }
        _visible = false;
        _closing = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Clear();
        if (_dispatcher.HasThreadAccess) DestroyOverlay();
        else _dispatcher.TryEnqueue(DestroyOverlay);
    }

    private sealed record TrophyNotificationOptions(
        double PositionX,
        double PositionY,
        int DurationSeconds)
    {
        public static TrophyNotificationOptions From(AppSettings settings) => new(
            settings.TrophyNotificationPositionX,
            settings.TrophyNotificationPositionY,
            settings.TrophyNotificationDurationSeconds);
    }

    private readonly record struct TrophyDisplay(RectInt32 WorkArea, double Scale);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectWin
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;

        public Margins(int left, int right, int top, int bottom)
        {
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint DwFlags;
        public int FEnable;
        public IntPtr HRgnBlur;
        public int FTransitionOnMaximized;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RectWin lpRect);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern int DeleteObject(IntPtr ho);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeU32(IntPtr hwnd, int attribute, ref uint value, int valueSize);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
}
