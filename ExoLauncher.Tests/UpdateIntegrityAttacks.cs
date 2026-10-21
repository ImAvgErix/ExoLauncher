using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Attack 4 — update and download integrity, driven through the real classes
/// with a stubbed HttpMessageHandler (no network). The upscaler/tool path is
/// <see cref="VerifiedGitHubReleaseDownloader"/>; the app updater's host/version
/// gates are <see cref="AppUpdateService"/>.
/// </summary>
public class UpdateIntegrityAttacks
{
    // A GitHub-release stub that serves whatever body each URL asks for.
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int AssetRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Contains("githubusercontent", StringComparison.OrdinalIgnoreCase)
                || request.RequestUri.AbsolutePath.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref AssetRequests);
            return Task.FromResult(respond(request));
        }
    }

    private static readonly byte[] GoodBytes = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
    private static string GoodSha => Convert.ToHexString(SHA256.HashData(GoodBytes)).ToLowerInvariant();

    private static GitHubReleaseAsset Asset(string sha, long size) =>
        new("vendorX", "repoY", "v1.0.0", "tool.exe", size, sha);

    private static HttpResponseMessage MetadataFor(GitHubReleaseAsset a, string? downloadUrl = null, string? digest = null, long? size = null)
    {
        downloadUrl ??= $"https://github.com/{a.Owner}/{a.Repository}/releases/download/{a.Tag}/{a.AssetName}";
        var json = new
        {
            tag_name = a.Tag,
            assets = new[]
            {
                new { name = a.AssetName, state = "uploaded", size = size ?? a.ExpectedSize, digest = digest ?? ("sha256:" + a.ExpectedSha256), browser_download_url = downloadUrl },
            },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage Bytes(byte[] body)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        return r;
    }

    private static string TempDest() => Path.Combine(Path.GetTempPath(), "exo-upd-" + Guid.NewGuid().ToString("N"), "tool.exe");

    [Fact]
    public async Task Verified_download_accepts_matching_pin()
    {
        var asset = Asset(GoodSha, GoodBytes.Length);
        var handler = new StubHandler(req =>
            req.RequestUri!.Host == "api.github.com" ? MetadataFor(asset) : Bytes(GoodBytes));
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            var path = await dl.DownloadPinnedAsync(asset, dest, _ => true, CancellationToken.None);
            Assert.True(File.Exists(path));
            Assert.Equal(GoodSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Verified_download_rejects_wrong_sha256()
    {
        // Pin a SHA that does not match the served bytes.
        var wrongSha = new string('a', 64);
        var asset = Asset(wrongSha, GoodBytes.Length);
        var handler = new StubHandler(req =>
            req.RequestUri!.Host == "api.github.com" ? MetadataFor(asset) : Bytes(GoodBytes));
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => dl.DownloadPinnedAsync(asset, dest, _ => true, CancellationToken.None));
            Assert.False(File.Exists(dest), "a hash-mismatched asset was promoted to its destination");
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Verified_download_rejects_wrong_size()
    {
        // Metadata advertises a size different from the pinned one.
        var asset = Asset(GoodSha, GoodBytes.Length);
        var handler = new StubHandler(req =>
            req.RequestUri!.Host == "api.github.com" ? MetadataFor(asset, size: GoodBytes.Length + 10) : Bytes(GoodBytes));
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => dl.DownloadPinnedAsync(asset, dest, _ => true, CancellationToken.None));
            Assert.False(File.Exists(dest));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Verified_download_rejects_missing_digest()
    {
        var asset = Asset(GoodSha, GoodBytes.Length);
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.Host != "api.github.com") return Bytes(GoodBytes);
            // metadata with NO digest field
            var json = new { tag_name = asset.Tag, assets = new[] { new { name = asset.AssetName, state = "uploaded", size = asset.ExpectedSize, browser_download_url = $"https://github.com/{asset.Owner}/{asset.Repository}/releases/download/{asset.Tag}/{asset.AssetName}" } } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(json)) };
        });
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => dl.DownloadPinnedAsync(asset, dest, _ => true, CancellationToken.None));
            Assert.False(File.Exists(dest));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Verified_download_rejects_non_github_asset_url()
    {
        var asset = Asset(GoodSha, GoodBytes.Length);
        var handler = new StubHandler(req =>
            req.RequestUri!.Host == "api.github.com"
                ? MetadataFor(asset, downloadUrl: "https://evil.example.com/tool.exe")
                : Bytes(GoodBytes));
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => dl.DownloadPinnedAsync(asset, dest, _ => true, CancellationToken.None));
            Assert.Equal(0, handler.AssetRequests); // never even fetched the evil host
            Assert.False(File.Exists(dest));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Verified_download_rejects_redirect_outside_allowlist()
    {
        var asset = Asset(GoodSha, GoodBytes.Length);
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.Host == "api.github.com") return MetadataFor(asset);
            // The asset URL 302s to an attacker host.
            if (req.RequestUri.AbsolutePath.Contains("/releases/download/"))
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://evil.example.com/tool.exe");
                return r;
            }
            return Bytes(GoodBytes);
        });
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            await Assert.ThrowsAnyAsync<SecurityException>(() => dl.DownloadPinnedAsync(asset, dest, _ => true, CancellationToken.None));
            Assert.False(File.Exists(dest));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Verified_download_rejects_executable_validation_failure()
    {
        // Bytes + sha match but the executable validator says no (e.g. wrong PE
        // machine). The file must not be promoted.
        var asset = Asset(GoodSha, GoodBytes.Length);
        var handler = new StubHandler(req =>
            req.RequestUri!.Host == "api.github.com" ? MetadataFor(asset) : Bytes(GoodBytes));
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => dl.DownloadPinnedAsync(asset, dest, _ => false, CancellationToken.None));
            Assert.False(File.Exists(dest));
        }
        finally { Cleanup(dest); }
    }

    // ---- URL/digest predicates ----

    [Theory]
    [InlineData("https://github.com/vendorX/repoY/releases/download/v1.0.0/tool.exe", true)]
    [InlineData("https://github.com/vendorX/repoY/releases/download/v1.0.0/other.exe", false)]
    [InlineData("https://github.com/evil/repoY/releases/download/v1.0.0/tool.exe", false)]
    [InlineData("https://github.com/vendorX/repoY/releases/download/v1.0.0/tool.exe?x=1", false)]
    [InlineData("http://github.com/vendorX/repoY/releases/download/v1.0.0/tool.exe", false)]
    [InlineData("https://github.com.evil.com/vendorX/repoY/releases/download/v1.0.0/tool.exe", false)]
    public void IsExpectedAssetDownloadUri_is_strict(string url, bool expected)
    {
        var asset = Asset(GoodSha, 1);
        Assert.Equal(expected, VerifiedGitHubReleaseDownloader.IsExpectedAssetDownloadUri(new Uri(url), asset));
    }

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://evil.githubusercontent.com.evil.com/x", false)]
    [InlineData("http://objects.githubusercontent.com/x", false)]
    public void IsAllowedRedirectUri_is_strict(string url, bool expected) =>
        Assert.Equal(expected, VerifiedGitHubReleaseDownloader.IsAllowedRedirectUri(new Uri(url)));

    [Theory]
    [InlineData(null, false)]
    [InlineData("md5:abc", false)]
    [InlineData("sha256:xyz", false)]
    [InlineData("sha256:" + "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", true)]
    public void ParseSha256Digest_only_accepts_valid_sha256(string? digest, bool valid) =>
        Assert.Equal(valid, VerifiedGitHubReleaseDownloader.ParseSha256Digest(digest) is not null);

    // ---- app updater host + version gates ----

    [Theory]
    [InlineData("https://github.com/ImAvgErix/ExoLauncher/releases/tag/3.6.0", "3.6.0")]
    [InlineData("https://evil.com/ImAvgErix/ExoLauncher/releases/tag/3.6.0", null)]
    [InlineData("https://github.com/someoneelse/ExoLauncher/releases/tag/3.6.0", null)]
    public void TagFromReleasesUrl_only_trusts_official_repo(string url, string? expected) =>
        Assert.Equal(expected, AppUpdateService.TagFromReleasesUrl(new Uri(url)));

    [Fact]
    public void App_updater_refuses_downgrade()
    {
        // Updater picks up a lower remote version than local: it reports
        // AlreadyLatest and offers no update. Served through the real class.
        var handler = new StubHandler(_ =>
        {
            var json = new { tag_name = "v1.0.0", assets = Array.Empty<object>() };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(json)) };
        });
#pragma warning disable RS0030 // Stub handler: no request leaves the process.
        using var http = new HttpClient(handler);
#pragma warning restore RS0030
        var updatesDir = Path.Combine(Path.GetTempPath(), "exo-updtest-" + Guid.NewGuid().ToString("N"));
        var svc = new AppUpdateService(http, updatesDir, TimeProvider.System, allowCurlFallback: false);
        try
        {
            var result = svc.CheckAsync("9.9.9").GetAwaiter().GetResult();
            Assert.False(result.UpdateAvailable, "updater offered a downgrade");
            Assert.True(result.AlreadyLatest);
        }
        finally { try { Directory.Delete(updatesDir, true); } catch { } }
    }

    private static void Cleanup(string dest)
    {
        try
        {
            var dir = Path.GetDirectoryName(dest);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }
}
