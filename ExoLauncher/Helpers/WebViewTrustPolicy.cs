namespace ExoLauncher.Helpers;

/// <summary>
/// Defines the only origin allowed to host Exo's privileged WebView bridge.
/// </summary>
internal static class WebViewTrustPolicy
{
    internal const string TrustedAppHost = "app.exo-launcher.local";
    internal const string TrustedAppStartUri = "https://app.exo-launcher.local/index.html";

    internal static bool IsTrustedAppUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.IdnHost, TrustedAppHost, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo);
    }
}
