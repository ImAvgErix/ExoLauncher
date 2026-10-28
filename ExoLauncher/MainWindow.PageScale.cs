using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.UI.Windowing;

namespace ExoLauncher;

/// <summary>
/// One layout on every screen. Exo's page is laid out in CSS pixels, so a 1440p or 4K screen (at 100% to 150%) gave
/// it more room than a 1080p one: more cards in a row, the Store's and Devices' panels arranged another way. Now the
/// page is laid out as on a 1080p screen and drawn larger to fill the window, with Chromium's own device emulation
/// (the one DevTools' device mode uses: input is mapped back), at the window's size over the zoom, so resizing the
/// window still reflows the page. The zoom brings the screen, in Windows' scaled pixels, down to 1080p's size either
/// way up (<see cref="PageScale.ZoomFor"/>): 1 on a 1080p screen at 100%, lying down or stood on end, 1.33 on 1440p
/// at 100% or 4K at 150%, never below 1. What the host places from the page's coordinates (the Bar's drag regions,
/// Music's hosted app) and the window's smallest size take the same zoom (<see cref="PageZoom"/>).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The page's CSS pixels to the window's scaled pixels: 1 unless the screen is bigger than 1080p.</summary>
    internal static double PageZoom { get; private set; } = 1;

    private (int Width, int Height, double Zoom) _pageScaled = (0, 0, 1);

    /// <summary>On a size, screen or scale change and after the page loads: the emulated size, when it changed.</summary>
    private void ApplyPageScale(bool again = false)
    {
        if (WebHost?.CoreWebView2 is not { } core) return;
        double zoom = 1;
        (double Width, double Height) screen = default;
        try
        {
            var bounds = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).OuterBounds;
            var scale = GetWindowScale();
            screen = (bounds.Width / scale, bounds.Height / scale);
            // EXO_PAGE_SCREEN=1440 (or 1080x1920) tries another screen's zoom on this one (a 1440p or 4K-at-150% screen's).
            if (PageScale.TryScreen(Environment.GetEnvironmentVariable("EXO_PAGE_SCREEN"), out var tried))
                screen = tried;
            zoom = PageScale.ZoomFor(screen.Width, screen.Height);
        }
        catch { /* no screen to read while the window closes: the page stays as it is */ }
        var width = (int)Math.Round(WebHost.ActualWidth / zoom);
        var height = (int)Math.Round(WebHost.ActualHeight / zoom);
        if (width <= 0 || height <= 0) return;
        var was = _pageScaled.Zoom;
        if (!again && (width, height, zoom) == _pageScaled) return;
        _pageScaled = (width, height, zoom);
        PageZoom = zoom;
        if (zoom == 1 && was == 1 && !again) return;
        if (zoom != was)
        {
            AppLog.Info($"Page scale: {zoom:0.###}, the page laid out at {width}x{height} on a screen of {screen.Width:0}x{screen.Height:0} scaled pixels.");
            ApplyWindowMinimumSize();
        }
        // dontSetVisibleSize: the view stays the window's own size and the zoomed page fills it. Without it Chromium
        // shrank the view to the layout size, so on a 4K screen at 150% only the top-left three quarters of the page
        // showed, cut off at the right and bottom (the page's own screenshots looked whole, the screen didn't).
        var call = zoom == 1
            ? core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}")
            : core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                JsonSerializer.Serialize(new { width, height, deviceScaleFactor = 0, mobile = false, scale = zoom, dontSetVisibleSize = true }));
        _ = call.AsTask().ContinueWith(task => AppLog.Warn("Page scale: " + task.Exception?.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted);
        if (zoom != was) UpdateCaptionDragRegions();
    }
}
