namespace ExoLauncher.Helpers;

/// <summary>
/// Maps window visibility and covered content to WebView2 memory priority. Low keeps
/// scripts and network connections running, so background music is not suspended.
/// A window that is only inactive stays Normal: it is still on screen, and Low makes
/// Chromium drop decoded images and GPU tiles the user is looking at, which blanked
/// and garbled library covers whenever focus moved to another window or a hosted client.
/// </summary>
internal static class WebViewMemoryPolicy
{
    internal static bool UseLowTarget(bool windowHiddenOrMinimized, bool contentCovered) =>
        windowHiddenOrMinimized || contentCovered;
}
