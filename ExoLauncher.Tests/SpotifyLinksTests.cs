using System.Net;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a shared Spotify link could fail to open in Exo's Spotify room:
/// - a real Jam link is refused: a spotify.link short link, ?si= tracking, a locale or a spotify: URI;
/// - a short link's redirect is not followed, or is followed off Spotify's short-link hosts;
/// - the share page Branch answers instead of a redirect is not read (a real spotify.link does this);
/// - the open.spotify.com page a short link names is fetched (it is only read from the redirect or page);
/// - an id with a slash, colon or query in it reaches Spotify's join or play call;
/// - a look-alike host (open.spotify.com.evil.example) passes as Spotify's.
/// </summary>
public sealed class SpotifyLinksTests
{
    private const string Track = "4uLU6hMCjMI75M1A2tKUQC";

    [Theory]
    [InlineData("https://open.spotify.com/socialsession/AbC123_xyz-9", "spotify:socialsession:AbC123_xyz-9")]
    [InlineData("https://open.spotify.com/socialsession/AbC123_xyz-9?si=6f1c2d3e4a5b&utm_source=copy-link", "spotify:socialsession:AbC123_xyz-9")]
    [InlineData("  open.spotify.com/socialsession/AbC123  ", "spotify:socialsession:AbC123")]
    [InlineData("http://open.spotify.com/socialsession/AbC123/", "spotify:socialsession:AbC123")]
    [InlineData("https://open.spotify.com/intl-de/socialsession/AbC123#x", "spotify:socialsession:AbC123")]
    [InlineData("spotify:socialsession:AbC123", "spotify:socialsession:AbC123")]
    [InlineData("https://open.spotify.com/track/" + Track + "?si=abc", "spotify:track:" + Track)]
    [InlineData("https://open.spotify.com/intl-fr/album/" + Track, "spotify:album:" + Track)]
    [InlineData("https://open.spotify.com/playlist/" + Track + "?si=x&pt=y", "spotify:playlist:" + Track)]
    [InlineData("https://open.spotify.com/embed/artist/" + Track, "spotify:artist:" + Track)]
    [InlineData("https://open.spotify.com/user/someone/playlist/" + Track, "spotify:playlist:" + Track)]
    [InlineData("https://play.spotify.com/episode/" + Track, "spotify:episode:" + Track)]
    [InlineData("spotify:track:" + Track, "spotify:track:" + Track)]
    public void ParsesEveryDirectForm(string link, string? uri) => Assert.Equal(uri, SpotifyLinks.Parse(link));

    [Theory]
    [InlineData("https://open.spotify.com.evil.example/socialsession/AbC123")]
    [InlineData("https://open.spotify.com/socialsession/Ab")]
    [InlineData("https://open.spotify.com/socialsession/Ab:C/123")]
    [InlineData("https://open.spotify.com/track/short")]
    [InlineData("https://open.spotify.com/")]
    [InlineData("https://open.spotify.com/search/hello")]
    [InlineData("spotify:user:someone")]
    [InlineData("https://spotify.link/aB3dE5gH7jK")]
    public void RefusesWhatIsNotAPageOrJam(string link) => Assert.Null(SpotifyLinks.Parse(link));

    [Theory]
    [InlineData("https://spotify.link/aB3dE5gH7jK", true)]
    [InlineData("https://spotify.app.link/aB3dE5gH7jK?_p=c1", true)]
    [InlineData("https://spotify-alternate.app.link/aB3dE5gH7jK", true)]
    [InlineData("http://spotify.link/aB3dE5gH7jK", false)]
    [InlineData("https://spotify.link/", false)]
    [InlineData("https://spotify.link/a/b", false)]
    [InlineData("https://evil.app.link/aB3dE5gH7jK", false)]
    public void KnowsSpotifysShortLinks(string link, bool shortLink) => Assert.Equal(shortLink, SpotifyLinks.IsShortLink(new Uri(link)));

    [Fact]
    public async Task ShortJamLinkFollowsItsRedirect()
    {
        var asked = new List<HttpRequestMessage>();
        using var http = Stub(asked, request => request.RequestUri!.Host == "spotify.link"
            ? Redirect("https://open.spotify.com/socialsession/AbC123_xyz?si=1a2b&$web_only=true")
            : throw new InvalidOperationException("only the short link is asked"));
        Assert.Equal("spotify:socialsession:AbC123_xyz", await SpotifyLinks.ResolveAsync("https://spotify.link/aB3dE5gH7jK", http, default));
        var request = Assert.Single(asked);
        Assert.False(request.Headers.Contains("Cookie"));
    }

    // The shape of a real Jam invite (spotify.link/7dNeUX7Wm7b, October 2026), id and tracking values replaced.
    private const string JamId = "5FV0aBcDeFgHiJkLmNoPqR";
    private const string JamPage = "https://open.spotify.com/socialsession/" + JamId + "?si=0123456789abcdef&amp;utm_source=copy-link&amp;utm_medium=copy-link&amp;ssp=1";

    [Fact]
    public async Task RealJamShortLinkReadsItsSharePage()
    {
        // Without a browser's user agent, spotify.link answers its share page; og:url is the Jam.
        var asked = new List<HttpRequestMessage>();
        using var http = Stub(asked, _ => Page(
            "<meta property=\"og:image\" content=\"https://shareables.scdn.co/publish/socialsession/" + JamId + "\" />" +
            "<meta property=\"og:url\" content=\"" + JamPage + "\" />"));
        Assert.Equal("spotify:socialsession:" + JamId, await SpotifyLinks.ResolveAsync("https://spotify.link/7dNeUX7Wm7b", http, default));
        Assert.Single(asked);
    }

    [Fact]
    public async Task RealJamThroughAppLinkReadsItsPage()
    {
        // A browser's user agent gets a 307 to spotify.app.link, whose page sends the browser on with script.
        var asked = new List<HttpRequestMessage>();
        using var http = Stub(asked, request => request.RequestUri!.Host == "spotify.link"
            ? Redirect("https://spotify.app.link/7dNeUX7Wm7b?_p=c11433dc9d0")
            : Page("<a class=\"action\" href=\"spotify://socialsession/" + JamId + "\">Open</a>" +
                   "<script>window.top.location = validateProtocol(\"" + JamPage.Replace("&amp;", "&", StringComparison.Ordinal) + "&_branch_match_id=1\");</script>"));
        Assert.Equal("spotify:socialsession:" + JamId, await SpotifyLinks.ResolveAsync("https://spotify.link/7dNeUX7Wm7b", http, default));
        Assert.Equal(["spotify.link", "spotify.app.link"], asked.Select(r => r.RequestUri!.Host));
    }

    [Fact]
    public async Task UnknownShortLinkPageIsNotAJam()
    {
        // Branch's page for a code it does not know names only the app.
        using var http = Stub([], _ => Page("<meta property=\"al:ios:url\" content=\"spotify://open?link_click_id&#x3D;1\" />"));
        Assert.Null(await SpotifyLinks.ResolveAsync("https://spotify.link/aaaaaaaaaaa", http, default));
    }

    [Fact]
    public async Task ShortLinkThroughAppLinkToATrack()
    {
        var asked = new List<HttpRequestMessage>();
        using var http = Stub(asked, request => request.RequestUri!.Host == "spotify.link"
            ? Redirect("https://spotify.app.link/aB3dE5gH7jK?_p=c")
            : Redirect("https://open.spotify.com/track/" + Track + "?si=x"));
        Assert.Equal("spotify:track:" + Track, await SpotifyLinks.ResolveAsync("spotify.link/aB3dE5gH7jK", http, default));
        Assert.Equal(["spotify.link", "spotify.app.link"], asked.Select(r => r.RequestUri!.Host));
        Assert.Equal("", asked[1].RequestUri!.Query); // Branch's own tracking is not sent on
    }

    [Fact]
    public async Task RedirectOffSpotifyStops()
    {
        var asked = new List<HttpRequestMessage>();
        using var http = Stub(asked, _ => Redirect("https://evil.example/aB3dE5gH7jK"));
        Assert.Null(await SpotifyLinks.ResolveAsync("https://spotify.link/aB3dE5gH7jK", http, default));
        Assert.Single(asked);
    }

    [Fact]
    public async Task ExpiredShortLinkIsNotAJam()
    {
        // A short link Branch does not know redirects to Spotify's home page.
        using var http = Stub([], _ => Redirect("https://open.spotify.com/"));
        Assert.Null(await SpotifyLinks.ResolveAsync("https://spotify.link/aaaaaaaaaaa", http, default));
    }

    [Fact]
    public async Task ShortLinkLoopStopsAfterFourHops()
    {
        var asked = new List<HttpRequestMessage>();
        using var http = Stub(asked, _ => Redirect("https://spotify.app.link/again"));
        Assert.Null(await SpotifyLinks.ResolveAsync("https://spotify.link/aB3dE5gH7jK", http, default));
        Assert.Equal(4, asked.Count);
    }

    [Fact]
    public async Task DirectLinkNeedsNoNetwork()
    {
        using var http = Stub([], _ => throw new InvalidOperationException("no request for a direct link"));
        Assert.Equal("spotify:socialsession:AbC123", await SpotifyLinks.ResolveAsync("https://open.spotify.com/socialsession/AbC123?si=z", http, default));
        Assert.Null(await SpotifyLinks.ResolveAsync("https://example.com/socialsession/AbC123", http, default));
    }

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri(location) } };

    private static HttpResponseMessage Page(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent("<html><head>" + html + "</head></html>", System.Text.Encoding.UTF8, "text/html") };

    private static HttpMessageInvoker Stub(List<HttpRequestMessage> asked, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(request => { asked.Add(request); return respond(request); }));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
