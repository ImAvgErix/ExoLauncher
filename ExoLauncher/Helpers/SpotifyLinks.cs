namespace ExoLauncher.Helpers;

/// <summary>
/// What a Spotify link someone shares points to, as a spotify: URI Exo's Spotify room opens:
/// an open.spotify.com page (with or without a locale, embed or ?si= tracking), a spotify: URI,
/// or a share short link (spotify.link, spotify.app.link). Spotify's client joins a Jam by the
/// token after socialsession (xpui's join dialog: getSessionInfo, then joinSession with it).
/// A short link is Branch's. Asked without cookies and without a browser's user agent, spotify.link
/// answers its share page, whose og:url is the open.spotify.com page (for a Jam:
/// open.spotify.com/socialsession/&lt;22-character id&gt;?si=…&amp;utm_source=…&amp;utm_medium=…&amp;ssp=…);
/// a browser's agent gets a 307 to spotify.app.link, whose page carries the same link. Either way the
/// open.spotify.com link is read from the redirect or the page and never fetched.
/// </summary>
internal static class SpotifyLinks
{
    private static readonly string[] ShortHosts = ["spotify.link", "spotify.app.link", "spotify-alternate.app.link"];
    private static readonly string[] Pages = ["track", "album", "playlist", "artist", "show", "episode"];
    private const int MaxPageBytes = 256 * 1024;
    private static readonly Lazy<HttpClient> Http = new(() => ExoHttp.Create(TimeSpan.FromSeconds(8),
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }));

    /// The spotify: URI a link names, without the network; null when it is not a page or Jam Exo opens.
    internal static string? Parse(string? text)
    {
        var value = WithScheme(text);
        if (value is not { Length: > 8 and <= 500 }) return null;
        if (value.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            return FromParts(value[8..].Split('?', '#')[0].Split(':', StringSplitOptions.RemoveEmptyEntries));
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") ||
            url.Host is not ("open.spotify.com" or "play.spotify.com")) return null;
        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).AsSpan();
        if (parts.Length > 0 && parts[0].StartsWith("intl-", StringComparison.Ordinal)) parts = parts[1..];
        if (parts.Length > 0 && parts[0] == "embed") parts = parts[1..];
        return FromParts(parts);
    }

    private static string? FromParts(ReadOnlySpan<string> parts)
    {
        // An old playlist address: user/<name>/playlist/<id>.
        if (parts.Length >= 4 && parts[0] == "user") parts = parts[2..];
        if (parts.Length < 2) return null;
        var (kind, id) = (parts[0].ToLowerInvariant(), parts[1]);
        if (kind == "socialsession")
            return id.Length is >= 4 and <= 80 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? "spotify:socialsession:" + id : null;
        return Pages.Contains(kind) && id.Length == 22 && id.All(char.IsAsciiLetterOrDigit) ? $"spotify:{kind}:{id}" : null;
    }

    /// A link pasted without https:// (open.spotify.com/…, spotify.link/…) still is one.
    private static string? WithScheme(string? text)
    {
        var value = text?.Trim();
        return value is null || value.Contains("://", StringComparison.Ordinal) || value.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase) ? value : "https://" + value;
    }

    internal static bool IsShortLink(Uri url) =>
        url.Scheme == "https" && ShortHosts.Contains(url.Host) && url.AbsolutePath.Trim('/') is { Length: > 0 and < 64 } path && !path.Contains('/');

    /// Parse, or for a short link follow its redirects (at most four, each to a short-link host)
    /// until a redirect or a share page names a page or a Jam.
    internal static async Task<string?> ResolveAsync(string? text, HttpMessageInvoker http, CancellationToken cancellationToken)
    {
        if (Parse(text) is { } direct) return direct;
        if (!Uri.TryCreate(WithScheme(text), UriKind.Absolute, out var url) || !IsShortLink(url)) return null;
        for (var hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url.GetLeftPart(UriPartial.Path)));
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Headers.Location is not { } next)
                return response.IsSuccessStatusCode ? FromPage(await ReadPageAsync(response.Content, cancellationToken).ConfigureAwait(false)) : null;
            if (!next.IsAbsoluteUri) next = new Uri(url, next);
            if (Parse(next.OriginalString) is { } found) return found;
            if (!IsShortLink(next)) return null;
            url = next;
        }
        return null;
    }

    /// The first open.spotify.com link in a share page (its og:url, then the page's own links) that names a page or a Jam.
    internal static string? FromPage(string? html)
    {
        if (html is null) return null;
        foreach (System.Text.RegularExpressions.Match link in System.Text.RegularExpressions.Regex.Matches(html, @"https://open\.spotify\.com/[^""'\s<>\\]{1,400}"))
            if (Parse(System.Net.WebUtility.HtmlDecode(link.Value)) is { } uri) return uri;
        return null;
    }

    private static async Task<string?> ReadPageAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxPageBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxPageBytes];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0) length += read;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    /// spotify.link: the page or Jam a link opens, for the Jam panel and links clicked in Discord.
    internal static async Task<object> LinkAsync(string? url)
    {
        string? uri;
        try { uri = await ResolveAsync(url, Http.Value, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            AppLog.Warn("Spotify link did not resolve: " + error.GetType().Name);
            return new { ok = false, message = "Spotify did not answer. Try again." };
        }
        return uri is null ? new { ok = false, message = "That is not a Spotify link." } : new { ok = true, uri };
    }
}
