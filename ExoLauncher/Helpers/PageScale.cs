using System.Globalization;

namespace ExoLauncher.Helpers;

/// <summary>The page's zoom for a screen (MainWindow.PageScale): the layout a 1080p screen gets, drawn larger on a bigger one.</summary>
internal static class PageScale
{
    /// <summary>The screen size, in Windows' scaled pixels, the page is laid out for: 1080p's, either way up.</summary>
    internal const double DesignWidth = 1920, DesignHeight = 1080;

    /// <summary>
    /// The zoom for a screen this many scaled pixels wide and tall: what brings it down to 1080p's size, its long side
    /// to 1920 and its short side to 1080, whichever is less. A screen stood on end gets the zoom it has lying down (by
    /// its height alone, a 1080p screen on end was zoomed 1.78 and laid out 607 pixels wide), and a 16:10 one keeps
    /// 1080p's width. 1 up to 1080p (and just past it), at most 3.
    /// </summary>
    internal static double ZoomFor(double screenWidth, double screenHeight)
    {
        var zoom = Math.Min(Math.Max(screenWidth, screenHeight) / DesignWidth, Math.Min(screenWidth, screenHeight) / DesignHeight);
        return zoom >= 1.05 ? Math.Min(Math.Round(zoom, 3), 3) : 1;
    }

    /// <summary>EXO_PAGE_SCREEN, another screen to try on this one: "1440" (a 16:9 screen that tall) or "1080x1920".</summary>
    internal static bool TryScreen(string? text, out (double Width, double Height) screen)
    {
        screen = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('x', 'X');
        var numbers = parts.Select(part => double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0).ToArray();
        if (numbers.Any(n => n <= 0)) return false;
        screen = numbers.Length switch
        {
            1 => (Math.Round(numbers[0] * 16 / 9), numbers[0]),
            2 => (numbers[0], numbers[1]),
            _ => default,
        };
        return screen != default;
    }
}
