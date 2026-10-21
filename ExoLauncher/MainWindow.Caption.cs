using ExoLauncher.Ui;
using Microsoft.UI.Input;
using Windows.Graphics;

namespace ExoLauncher;

/// <summary>
/// The title band's hit-testing. WinUI's WebView2 ignores CSS app-region, so
/// the shell measures its controls (<c>shell.setChromeLayout</c>) and the host
/// turns the rest of the 52 DIP band into a native caption: drag, double-click
/// to maximize, Aero Snap and Win+arrows all come from Windows. The maximize
/// button is the system's maximize region so hovering it opens the Windows 11
/// snap layouts flyout; its hover state is forwarded to the shell.
/// </summary>
public sealed partial class MainWindow
{
    private RectInt32[]? _captionInteractiveRegions;
    private RectInt32? _captionMaximizeButton;
    private RectInt32[] _appliedCaption = [];
    private InputNonClientPointerSource? _captionInput;
    private bool? _maximizePressedFrom;
    private long _captionRestoreWindowStart;
    private int _captionRestores;

    internal void SetChromeLayout(RectInt32[]? interactiveRegions, RectInt32? maximizeButton)
    {
        _captionInteractiveRegions = interactiveRegions;
        _captionMaximizeButton = maximizeButton;
        UpdateCaptionDragRegions();
    }

    private void AttachCaptionInput()
    {
        var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _captionInput = source;
        // The XAML window keeps a default title-bar caption of its own and
        // rewrites the caption rects on some layout passes; left alone, the
        // band would drag only where that default strip happens to be.
        source.RegionsChanged += (_, _) => DispatcherQueue.TryEnqueue(RestoreCaptionIfReplaced);
        source.PointerEntered += (_, e) =>
        {
            if (e.RegionKind == NonClientRegionKind.Maximize) _bridge?.NotifyMaximizeHover(IsMaximized, true);
        };
        source.PointerExited += (_, e) =>
        {
            if (e.RegionKind == NonClientRegionKind.Maximize) _bridge?.NotifyMaximizeHover(IsMaximized, false);
        };
        source.PointerPressed += (_, e) =>
        {
            if (e.RegionKind == NonClientRegionKind.Maximize) _maximizePressedFrom = IsMaximized;
        };
        source.PointerReleased += (_, e) =>
        {
            if (e.RegionKind != NonClientRegionKind.Maximize || _maximizePressedFrom is not { } before) return;
            _maximizePressedFrom = null;
            if (!e.IsPointInRegion) return;
            // Windows normally performs the click on its own maximize region.
            // Should it not have, the button still does what it says.
            var check = DispatcherQueue.CreateTimer();
            check.Interval = TimeSpan.FromMilliseconds(200);
            check.IsRepeating = false;
            check.Tick += (_, _) =>
            {
                check.Stop();
                if (_closingWindow || IsMaximized != before) return;
                ToggleMaximize();
                _bridge?.NotifyWindowState(IsMaximized);
            };
            check.Start();
        };
    }

    private void RestoreCaptionIfReplaced()
    {
        if (_closingWindow || _captionInput is not { } source || _appliedCaption.Length == 0) return;
        RectInt32[] current;
        try { current = source.GetRegionRects(NonClientRegionKind.Caption); }
        catch { return; }
        if (current.AsSpan().SequenceEqual(_appliedCaption)) return;
        // Bounded: two writers must never ping-pong on the UI thread.
        var now = Environment.TickCount64;
        if (now - _captionRestoreWindowStart > 1000) { _captionRestoreWindowStart = now; _captionRestores = 0; }
        if (++_captionRestores > 8) return;
        UpdateCaptionDragRegions();
    }

    /// Region rects are physical client pixels; the shell's rects are DIPs.
    private void UpdateCaptionDragRegions()
    {
        try
        {
            var scale = GetWindowScale();
            // AppWindow.ClientSize is the only authority for the physical
            // width: RootGrid.ActualWidth x scale drifts from it under some
            // DPI states and would shift the right-hand holes off the buttons.
            var width = 0;
            try { width = AppWindow.ClientSize.Width; } catch { }
            if (width <= 0) width = (int)Math.Round(RootGrid.ActualWidth * scale);
            CaptionDragRegions.Build(width, scale, _captionInteractiveRegions, _captionMaximizeButton,
                out var caption, out var passthrough, out var maximize);
            if (caption.Length == 0) return;
            var source = _captionInput ?? InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            _appliedCaption = caption;
            source.SetRegionRects(NonClientRegionKind.Caption, caption);
            source.SetRegionRects(NonClientRegionKind.Passthrough, passthrough);
            if (maximize.Length > 0) source.SetRegionRects(NonClientRegionKind.Maximize, maximize);
            else source.ClearRegionRects(NonClientRegionKind.Maximize);
        }
        catch
        {
            /* best effort: the next layout, size or DPI change applies them again */
        }
    }
}
