using System.Diagnostics;

namespace ExoLauncher.Ui;

/// <summary>
/// Same scheme gate as the leftover React <c>shell.openUrl</c> path.
/// Native Settings / Discover / keys must not Process.Start an arbitrary URI.
/// </summary>
internal static class ExoUrlLaunch
{
    public static bool IsAllowed(Uri uri)
    {
        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.Scheme.Equals("steam", StringComparison.OrdinalIgnoreCase)) return IsPlainSteam(uri);
        return uri.Scheme.Equals("ms-windows-store", StringComparison.OrdinalIgnoreCase)
            && uri.Host is "pdp" or "search";
    }

    // Steam's harmless forms only: its store, client pages and https pages inside it, and install or run by app id alone.
    // steam://run/<id>//<args> passes launch arguments to the game, and console commands go to the client: never from a link.
    private static bool IsPlainSteam(Uri uri)
    {
        var rest = uri.AbsolutePath.Trim('/');
        return uri.Host.ToLowerInvariant() switch
        {
            "store" or "nav" or "open" => true,
            "openurl" => uri.OriginalString.Length > "steam://openurl/".Length
                && Uri.TryCreate(uri.OriginalString["steam://openurl/".Length..], UriKind.Absolute, out var inner) && inner.Scheme == Uri.UriSchemeHttps,
            "install" or "rungameid" => rest.Length is > 0 and <= 20 && rest.All(char.IsAsciiDigit) && uri.Query.Length == 0,
            _ => false,
        };
    }

    public static bool IsEpicLogin(Uri uri) =>
        Httpish(uri) && (HostEnds(uri.Host, "epicgames.com") || HostEnds(uri.Host, "unrealengine.com"));

    public static bool IsGogLogin(Uri uri) =>
        Httpish(uri) && HostEnds(uri.Host, "gog.com");

    private static bool Httpish(Uri uri) =>
        uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
        || uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase);

    private static bool HostEnds(string host, string suffix) =>
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    public static bool TryStart(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowed(uri))
            return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
