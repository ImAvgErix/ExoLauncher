namespace ExoLauncher.Helpers;

/// <summary>The page's zoom for a screen (MainWindow.PageScale): the layout a 1080p screen gets, drawn larger on a bigger one.</summary>
internal static class PageScale
{
    /// <summary>The screen height, in Windows' scaled pixels, the page is laid out for.</summary>
    internal const double DesignHeight = 1080;

    /// <summary>The zoom for a screen this many scaled pixels tall: 1 up to 1080 (and just past it), at most 3.</summary>
    internal static double ZoomFor(double screenHeight) =>
        screenHeight / DesignHeight is var zoom && zoom >= 1.05 ? Math.Min(Math.Round(zoom, 3), 3) : 1;
}
