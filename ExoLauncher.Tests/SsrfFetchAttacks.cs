using System.Net;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Attack 2 — fetching from attacker-chosen URLs (desktop SSRF). Store data,
/// catalogs and manifests feed URLs to Exo's image/asset downloaders. The gate
/// is <see cref="CoverArtService.IsApprovedArtworkDownloadUrl"/> /
/// <see cref="CoverArtService.IsAllowlistedCdnCover"/> for cover art,
/// <see cref="DlssSwapService.IsAllowedDownloadUrl"/> for upscalers, and
/// <see cref="AppUpdateService.IsAllowedDownloadUri"/> for updates.
///
/// The decisive proof is a REAL local listener: DownloadValidatedImageAsync is
/// pointed at a crafted http://127.0.0.1 URL and we assert the listener never
/// receives a connection — the gate rejects before any socket opens.
/// </summary>
public class SsrfFetchAttacks
{
    public static TheoryData<string> SsrfTargets() =>
    [
        "http://localhost/cover.jpg",
        "http://127.0.0.1/cover.jpg",
        "http://127.0.0.1:8080/cover.jpg",
        "http://169.254.169.254/latest/meta-data/",   // cloud metadata
        "http://169.254.169.254/cover.jpg",
        "http://[::1]/cover.jpg",
        "http://10.0.0.5/internal",
        "http://192.168.1.1/admin",
        "file:///C:/Windows/win.ini",
        "file://localhost/etc/passwd",
        "ftp://127.0.0.1/x",
        "gopher://127.0.0.1:70/x",
        "http://store-images.s-microsoft.com/image",  // right host, WRONG (non-https) scheme
        "https://store-images.s-microsoft.com@127.0.0.1/image", // userinfo smuggling
        "https://127.0.0.1/image",
        "https://evil.com/image",
        "https://cdn.steamgriddb.com.evil.com/image", // suffix spoof
        "https://images.igdb.com:8443/image",          // non-default port
    ];

    [Theory]
    [MemberData(nameof(SsrfTargets))]
    public void Cover_gate_rejects_ssrf_targets(string url)
    {
        Assert.False(CoverArtService.IsApprovedArtworkDownloadUrl(url),
            $"cover download gate approved SSRF/non-https target: {url}");
        Assert.False(CoverArtService.IsAllowlistedCdnCover(url),
            $"cover CDN allowlist approved SSRF/non-https target: {url}");
    }

    [Theory]
    [MemberData(nameof(SsrfTargets))]
    public void Update_gate_rejects_ssrf_targets(string url)
    {
        var ok = Uri.TryCreate(url, UriKind.Absolute, out var uri) && AppUpdateService.IsAllowedDownloadUri(uri);
        Assert.False(ok, $"update download gate approved SSRF/non-https target: {url}");
    }

    [Theory]
    [MemberData(nameof(SsrfTargets))]
    public void Upscaler_gate_rejects_ssrf_targets(string url)
    {
        Assert.False(DlssSwapService.IsAllowedDownloadUrl(url),
            $"upscaler download gate approved SSRF/non-pinned target: {url}");
    }

    [Fact]
    public async Task DownloadValidatedImage_never_connects_to_local_listener()
    {
        // A real listener on loopback. If the gate were bypassable, a GET would
        // arrive here. We assert it never does.
        var hit = false;
        using var listener = new HttpListener();
        var port = GetFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                hit = true;
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch { /* listener stopped */ }
        });

#pragma warning disable RS0030 // A plain client aimed only at the local listener above: the attack is that it never connects.
        using var http = new HttpClient();
#pragma warning restore RS0030
        var dest = Path.Combine(Path.GetTempPath(), "exo-ssrf-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            foreach (var target in new[]
                     {
                         $"http://127.0.0.1:{port}/cover.jpg",
                         $"https://127.0.0.1:{port}/cover.jpg",
                         $"https://store-images.s-microsoft.com@127.0.0.1:{port}/cover.jpg",
                     })
            {
                var result = await CoverArtService.DownloadValidatedImageAsync(
                    http, target, dest, minimumBytes: 1, maximumBytes: 1024, CancellationToken.None);
                Assert.False(result, $"download reported success for {target}");
            }
        }
        finally
        {
            listener.Stop();
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
        }

        // Give any erroneously-dispatched request a brief moment to land.
        await Task.WhenAny(serve, Task.Delay(500));
        Assert.False(hit, "a request reached the local listener — the SSRF gate was bypassed");
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
