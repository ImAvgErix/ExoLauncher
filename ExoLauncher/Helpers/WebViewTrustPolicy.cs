namespace ExoLauncher.Helpers;

/// <summary>
/// Defines the only origin allowed to host Exo's privileged WebView bridge.
/// A Debug build may point the shell at the Vite dev server instead, so UI
/// edits reload in place without a 45-second host rebuild:
/// <c>EXO_UI_DEV_URL=http://localhost:5174</c> (loopback only, never shipped).
/// </summary>
internal static class WebViewTrustPolicy
{
    internal const string TrustedAppHost = "app.exo-launcher.local";
    internal const string TrustedAppStartUri = "https://app.exo-launcher.local/index.html";
    internal const string DevUrlVariable = "EXO_UI_DEV_URL";
    /// <summary>Exo Mouse's own origin (wwwroot/mouse), so its frame cannot reach into the shell or its bridge.</summary>
    internal const string MouseHost = "mouse.exo-launcher.local";
    /// <summary>The card form's own origin (wwwroot/pay): the only page that loads Stripe, so card fields never share Exo's.</summary>
    internal const string PayHost = "pay.exo-launcher.local";

    /// <summary>Where the shell navigates on start: the dev server in Debug when set, else the bundled UI.</summary>
    internal static string StartUri => DevServerUri?.ToString() ?? TrustedAppStartUri;

    internal static Uri? DevServerUri
    {
        get
        {
#if DEBUG
            var raw = Environment.GetEnvironmentVariable(DevUrlVariable);
            if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
            return IsLoopbackDev(uri) ? uri : null;
#else
            return null;
#endif
        }
    }

    internal static bool IsTrustedAppUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (DevServerUri is { } dev && IsLoopbackDev(uri) && uri.Port == dev.Port) return true;

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.IdnHost, TrustedAppHost, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    /// <summary>
    /// Bridge messages come from the shell document only. Exo Mouse runs on its own host
    /// (MouseHost) and talks to the shell through mouseRelay's allow-list; its files are
    /// also under the app host's /mouse/, and a message from there is refused too.
    /// </summary>
    internal static bool IsTrustedBridgeSource(string? value) =>
        IsTrustedAppUri(value)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && !Uri.UnescapeDataString(uri.AbsolutePath).Replace('\\', '/')
            .StartsWith("/mouse/", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopbackDev(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        && uri.IsLoopback
        && string.IsNullOrEmpty(uri.UserInfo);
}
