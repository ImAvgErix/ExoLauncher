namespace ExoLauncher.Services;

/// <summary>
/// Exact host policy for provider-owned achievement art. Untrusted values
/// become null before persistence or WebView projection.
/// </summary>
public static class AchievementIconCache
{
    private static readonly HashSet<string> ApprovedProviderImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.akamai.steamstatic.com",
        "cdn.cloudflare.steamstatic.com",
        "shared.steamstatic.com",
        "shared.akamai.steamstatic.com",
        "shared.cloudflare.steamstatic.com",
        "steamcdn-a.akamaihd.net",
        "shared-static-prod.epicgames.com",
        "cdn1.epicgames.com",
        "cdn2.unrealengine.com",
        "images.gog.com",
        "images.gog-statics.com",
    };

    internal static bool TryGetHttpsUri(string? source, out Uri uri)
    {
        if (Uri.TryCreate(source?.Trim(), UriKind.Absolute, out var parsed) &&
            parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            parsed.IsDefaultPort &&
            string.IsNullOrEmpty(parsed.UserInfo) &&
            ApprovedProviderImageHosts.Contains(parsed.IdnHost))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    /// <summary>
    /// Canonicalizes provider-owned achievement art. Untrusted legacy values
    /// become null before persistence or WebView projection.
    /// </summary>
    internal static string? SanitizeProviderImageUrl(string? source) =>
        source is { Length: <= 2_048 } && TryGetHttpsUri(source, out var uri)
            ? uri.AbsoluteUri
            : null;
}
