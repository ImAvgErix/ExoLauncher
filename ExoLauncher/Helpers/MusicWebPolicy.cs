namespace ExoLauncher.Helpers;

/// <summary>
/// Allow-lists for the music web players hosted on WebView2: YouTube Music
/// (music.youtube.com) and Apple Music (music.apple.com). Both run hidden as
/// the engine behind Exo's own player; only their own sign-in pages ever show,
/// in Exo's sign-in window. The view runs uBlock Origin
/// (WebAdBlocker) and the bundled extensions (WebExtensions); Exo
/// always optimizes YouTube Music, which also hides its Premium upsells
/// (EmbeddedClientPresentation).
/// </summary>
internal static class MusicWebPolicy
{
    internal static bool IsService(string? service) =>
        service is "spotify" or "apple" or "youtube";

    /// Providers whose engine is their own web player in Exo's WebView2.
    internal static bool IsWeb(string? service) => service is "youtube" or "apple";

    /// Provider display label for statuses and errors.
    internal static string Label(string service) => service switch
    {
        "spotify" => "Spotify",
        "apple" => "Apple Music",
        "youtube" => "YouTube Music",
        _ => "Music",
    };

    /// Where a web player starts. Apple Music opens on its search page, the
    /// lightest page of its web player (about 90 MB less than its New page);
    /// it redirects to the user's own storefront.
    internal static string StartUri(string service) =>
        service == "apple" ? "https://music.apple.com/search" : "https://music.youtube.com";

    /// Documents that may load inside the player's own surface. Sign-in
    /// continues through the provider's own account hosts inside the player
    /// or its popup.
    internal static bool IsServiceDocument(string? service, string? uri)
    {
        if (!IsWeb(service) || uri is null) return false;
        if (!IsHttps(uri)) return uri == "about:blank";
        var host = Host(uri);
        if (service == "apple")
            return host is "music.apple.com" or "authorize.music.apple.com" or "idmsa.apple.com" or "appleid.apple.com";
        return host == "music.youtube.com" ||
            host is "accounts.google.com" or "myaccount.google.com" or "consent.google.com" or
                "consent.youtube.com" or "accounts.youtube.com" ||
            (host == "www.youtube.com" && new Uri(uri).AbsolutePath.TrimEnd('/') == "/signin");
    }

    /// True once the player shows its own app surface.
    internal static bool IsPlayerDocument(string? service, string? uri) =>
        IsWeb(service) && uri is not null && IsHttps(uri) &&
        Host(uri) == (service == "apple" ? "music.apple.com" : "music.youtube.com");

    /// Strict https: the default port only, and never a URI carrying
    /// credentials — a userinfo host could smuggle a lookalike past the check.
    internal static bool IsHttps(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        parsed.Scheme == Uri.UriSchemeHttps && parsed.Port == 443 &&
        string.IsNullOrEmpty(parsed.UserInfo);

    private static string Host(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : "";
}
