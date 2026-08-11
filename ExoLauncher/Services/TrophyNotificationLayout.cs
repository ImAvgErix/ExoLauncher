namespace ExoLauncher.Services;

/// <summary>
/// Converts one of the canonical nine display anchors into work-area pixels.
/// Keeping the math independent of WinUI makes it deterministic and testable
/// across taskbar layouts, resolutions, and out-of-range legacy settings.
/// </summary>
public static class TrophyNotificationLayout
{
    public static TrophyNotificationBounds Calculate(
        int workX,
        int workY,
        int workWidth,
        int workHeight,
        int requestedWidth,
        int requestedHeight,
        double normalizedX,
        double normalizedY,
        int requestedMargin = 24)
    {
        var safeWorkWidth = Math.Max(1, workWidth);
        var safeWorkHeight = Math.Max(1, workHeight);
        var width = Math.Clamp(requestedWidth, 1, safeWorkWidth);
        var height = Math.Clamp(requestedHeight, 1, safeWorkHeight);
        var margin = Math.Clamp(
            requestedMargin,
            0,
            Math.Min(Math.Max(0, (safeWorkWidth - width) / 2), Math.Max(0, (safeWorkHeight - height) / 2)));
        var x = QuantizeAnchor(normalizedX, 1d);
        var y = QuantizeAnchor(normalizedY, 1d);
        var availableWidth = Math.Max(0, safeWorkWidth - width - (margin * 2));
        var availableHeight = Math.Max(0, safeWorkHeight - height - (margin * 2));
        var left = workX + margin + (int)Math.Round(availableWidth * x, MidpointRounding.AwayFromZero);
        var top = workY + margin + (int)Math.Round(availableHeight * y, MidpointRounding.AwayFromZero);
        return new TrophyNotificationBounds(left, top, width, height);
    }

    private static double QuantizeAnchor(double value, double fallback)
    {
        var normalized = double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : fallback;
        return normalized < 0.25d ? 0d : normalized < 0.75d ? 0.5d : 1d;
    }
}

public readonly record struct TrophyNotificationBounds(int Left, int Top, int Width, int Height);
