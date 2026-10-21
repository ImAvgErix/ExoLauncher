namespace ExoLauncher.Helpers;

/// <summary>
/// The motion rules every hosted web UI gets from Exo's optimization
/// (Spotify's xpui, YouTube Music's page). Animations jump to their last frame and loops stop after
/// one pass.
///
/// Transitions are left alone on purpose. transition-property defaults to
/// "all", so a transition-duration on every element gives every element a
/// transition on every property that changes: one zoom or text-size change
/// in Discord's Accessibility settings then started a CSS transition for
/// each length of each element (108,005 for four text-size steps over 18,000
/// elements in a Chromium test page, 24 times slower than with no rule),
/// and a hidden window that draws no frames held them all in memory. Only
/// elements that declare a transition have one, and they keep their own.
///
/// The durations stay just above zero on purpose: menus, toasts and modals
/// that wait for animationend before they close or unmount still get the
/// event ("animation: none" would leave them stuck), and a fade-in lands on
/// its visible end frame instead of its invisible first one
/// ("animation-play-state: paused" would). An element without an
/// animation-name runs no animation, so these rules cost nothing there.
/// Loading spinners and progress bars (the element and the levels below it
/// that carry their motion) keep moving: a frozen spinner reads as a hung
/// app. filter is never touched: blur is how spoilers and flagged images
/// stay hidden.
/// </summary>
internal static class StaticMotion
{
    private const string Exempt =
        """[class*="spinner" i], [class*="spinner" i] > *, [class*="spinner" i] > * > *, [class*="spinner" i] > * > * > *, [role="progressbar"], [role="progressbar"] > *, [role="progressbar"] > * > *""";

    internal const string Css =
        "*:not(" + Exempt + "), *:not(" + Exempt + ")::before, *:not(" + Exempt + """
)::after {
  animation-duration: 0.01ms !important;
  animation-delay: 0s !important;
  animation-iteration-count: 1 !important;
  scroll-behavior: auto !important;
}
""";
}
