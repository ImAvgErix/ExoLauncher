using Windows.Graphics;

namespace ExoLauncher.Ui;

/// <summary>
/// Caption, passthrough and maximize rects for title-band hit-testing, in
/// physical pixels. The whole 52 DIP band drags except the controls the shell
/// measured (DIP rects); the maximize button becomes the system's maximize
/// region so Windows 11 shows its snap layouts flyout over it.
/// Independent of InputNonClientPointerSource so it stays a pure function.
/// </summary>
internal static class CaptionDragRegions
{
    internal const int TitleBarDragDip = 52;
    /// Until the shell has measured its controls only a thin top strip drags,
    /// so nothing clickable is ever covered by a caption.
    internal const int UnmeasuredDragDip = 6;

    internal static void Build(
        int width,
        double scale,
        RectInt32[]? interactiveRegions,
        RectInt32? maximizeButton,
        out RectInt32[] caption,
        out RectInt32[] passthrough,
        out RectInt32[] maximize)
    {
        caption = [];
        passthrough = [];
        maximize = [];
        if (width <= 0) return;

        var titleH = Dip(TitleBarDragDip, scale);
        if (interactiveRegions is not { Length: > 0 })
        {
            var dragH = Dip(UnmeasuredDragDip, scale);
            caption = [new(0, 0, width, dragH)];
            passthrough = [new(0, dragH, width, titleH - dragH)];
            return;
        }

        var caps = new List<RectInt32> { new(0, 0, width, titleH) };
        var passes = new List<RectInt32>();
        foreach (var area in interactiveRegions)
            if (ToPixels(area, width, titleH, scale) is { } hole)
            {
                passes.Add(hole);
                caps = Subtract(caps, hole);
            }
        if (maximizeButton is { } button && ToPixels(button, width, titleH, scale) is { } max)
        {
            caps = Subtract(caps, max);
            passes = Subtract(passes, max);
            maximize = [max];
        }
        caption = [.. caps];
        passthrough = [.. passes];
    }

    private static RectInt32? ToPixels(RectInt32 area, int width, int titleH, double scale)
    {
        if (area.Width <= 0 || area.Height <= 0) return null;
        var left = Math.Clamp(Dip(area.X, scale), 0, width);
        var right = Math.Clamp(Dip(area.X + area.Width, scale), left, width);
        var top = Math.Clamp(Dip(area.Y, scale), 0, titleH);
        var bottom = Math.Clamp(Dip(area.Y + area.Height, scale), top, titleH);
        return right <= left || bottom <= top ? null : new RectInt32(left, top, right - left, bottom - top);
    }

    /// Every rect of <paramref name="rects"/> minus <paramref name="hole"/>, as
    /// at most four bands per rect (above, below, left, right).
    private static List<RectInt32> Subtract(List<RectInt32> rects, RectInt32 hole)
    {
        var remaining = new List<RectInt32>();
        void Add(int x, int y, int w, int h) { if (w > 0 && h > 0) remaining.Add(new(x, y, w, h)); }
        foreach (var rect in rects)
        {
            var x1 = Math.Max(rect.X, hole.X); var y1 = Math.Max(rect.Y, hole.Y);
            var x2 = Math.Min(rect.X + rect.Width, hole.X + hole.Width); var y2 = Math.Min(rect.Y + rect.Height, hole.Y + hole.Height);
            if (x2 <= x1 || y2 <= y1) { remaining.Add(rect); continue; }
            Add(rect.X, rect.Y, rect.Width, y1 - rect.Y);
            Add(rect.X, y2, rect.Width, rect.Y + rect.Height - y2);
            Add(rect.X, y1, x1 - rect.X, y2 - y1);
            Add(x2, y1, rect.X + rect.Width - x2, y2 - y1);
        }
        return remaining;
    }

    private static int Dip(int value, double scale) =>
        value <= 0 ? 0 : Math.Max(1, (int)Math.Round(value * scale));
}
