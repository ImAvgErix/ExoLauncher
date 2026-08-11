using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class VerifiedGitHubReleaseDownloaderTests : IDisposable
{
    private const string AssetName = "legendary_windows_x64.exe";
    private static readonly byte[] DefaultPayload = Encoding.UTF8.GetBytes("trusted payload");
    private static readonly GitHubReleaseAsset Asset = CreateAsset(DefaultPayload);

    private readonly string _fixture = Path.Combine(
        Path.GetTempPath(),
        "exo-verified-release-test-" + Guid.NewGuid().ToString("N"));

    public VerifiedGitHubReleaseDownloaderTests() => Directory.CreateDirectory(_fixture);

    public void Dispose()
    {
        try { Directory.Delete(_fixture, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/file")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/file")]
    public void RedirectHostsAllowOnlyPinnedHttpsOrigins(string value) =>
        Assert.True(VerifiedGitHubReleaseDownloader.IsAllowedRedirectUri(new Uri(value)));

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/file")]
    [InlineData("https://evilrelease-assets.githubusercontent.com/file")]
    [InlineData("https://raw.githubusercontent.com/derrod/legendary/main/legendary.exe")]
    [InlineData("https://release-assets.githubusercontent.com:444/file")]
    public void RedirectHostsRejectNonHttpsLookalikesAndUnexpectedOrigins(string value) =>
        Assert.False(VerifiedGitHubReleaseDownloader.IsAllowedRedirectUri(new Uri(value)));

    [Theory]
    [InlineData("https://github.com/derrod/legendary/releases/download/0.21.0/legendary_windows_x64.exe", true)]
    [InlineData("https://github.com/derrod/legendary/releases/download/0.21.0/Legendary_windows_x64.exe", false)]
    [InlineData("https://github.com/derrod/other/releases/download/0.21.0/legendary_windows_x64.exe", false)]
    [InlineData("https://github.com/attacker/legendary/releases/download/0.21.0/legendary_windows_x64.exe", false)]
    [InlineData("https://github.com/derrod/legendary/releases/download/latest/legendary_windows_x64.exe", false)]
    public void InitialAssetUrlMustMatchRepositoryPinnedTagAndExactAsset(
        string value,
        bool expected) =>
        Assert.Equal(
            expected,
            VerifiedGitHubReleaseDownloader.IsExpectedAssetDownloadUri(new Uri(value), Asset));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("md5:0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void DigestParserRejectsMissingOrMalformedSha256(string? value) =>
        Assert.Null(VerifiedGitHubReleaseDownloader.ParseSha256Digest(value));

    [Fact]
    public async Task ExactExpectedAssetNameIsMandatory()
    {
        using var downloader = CreateDownloader((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com"
                ? JsonResponse(ReleaseJson(Asset, assetName: "Legendary_windows_x64.exe"))
                : throw new InvalidOperationException("An unexpected asset download was attempted.")));
        var destination = Path.Combine(_fixture, "legendary.exe");

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadPinnedAsync(
            Asset,
            destination,
            _ => true,
            CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256:not-a-digest")]
    public async Task MissingOrInvalidReleaseDigestFailsClosed(string? digest)
    {
        using var downloader = CreateDownloader((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com"
                ? JsonResponse(ReleaseJson(Asset, digest: digest))
                : throw new InvalidOperationException("An unverified asset download was attempted.")));
        var destination = Path.Combine(_fixture, "legendary.exe");

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadPinnedAsync(
            Asset,
            destination,
            _ => true,
            CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task ReleaseDigestMustEqualPinnedDigest()
    {
        var otherDigest = "sha256:" + new string('0', 64);
        using var downloader = CreateDownloader((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com"
                ? JsonResponse(ReleaseJson(Asset, digest: otherDigest))
                : throw new InvalidOperationException("A drifted asset download was attempted.")));

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadPinnedAsync(
            Asset,
            Path.Combine(_fixture, "legendary.exe"),
            _ => true,
            CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseMetadataTagMustEqualPinnedTag()
    {
        using var downloader = CreateDownloader((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com"
                ? JsonResponse(ReleaseJson(Asset, tag: "0.22.0"))
                : throw new InvalidOperationException("An unpinned release download was attempted.")));

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadPinnedAsync(
            Asset,
            Path.Combine(_fixture, "legendary.exe"),
            _ => true,
            CancellationToken.None));
    }

    [Fact]
    public async Task RedirectOutsideAllowlistIsRejectedBeforeRequest()
    {
        var evilRequested = false;
        using var downloader = CreateDownloader((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com")
                return Task.FromResult(JsonResponse(ReleaseJson(Asset)));
            if (uri.Host == "github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://downloads.evil.example/legendary.exe");
                return Task.FromResult(redirect);
            }

            evilRequested = true;
            return Task.FromResult(BinaryResponse(DefaultPayload));
        });

        await Assert.ThrowsAsync<SecurityException>(() => downloader.DownloadPinnedAsync(
            Asset,
            Path.Combine(_fixture, "legendary.exe"),
            _ => true,
            CancellationToken.None));

        Assert.False(evilRequested);
    }

    [Fact]
    public async Task DownloadHashMismatchNeverPromotesAndPreservesExistingFile()
    {
        var corrupted = DefaultPayload.ToArray();
        corrupted[^1] ^= 0x5a;
        var existing = DefaultPayload.ToArray();
        existing[0] ^= 0x1;
        using var downloader = CreateDownloader((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com"
                ? JsonResponse(ReleaseJson(Asset))
                : BinaryResponse(corrupted)));
        var destination = Path.Combine(_fixture, "legendary.exe");
        await File.WriteAllBytesAsync(destination, existing);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadPinnedAsync(
            Asset,
            destination,
            _ => true,
            CancellationToken.None));

        Assert.Equal(existing, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(_fixture, "*.download"));
    }

    [Fact]
    public async Task MetadataSizeMustEqualPinnedSize()
    {
        using var downloader = CreateDownloader((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com"
                ? JsonResponse(ReleaseJson(Asset, declaredSize: Asset.ExpectedSize + 1))
                : throw new InvalidOperationException("A size-drifted asset download was attempted.")));
        var destination = Path.Combine(_fixture, "legendary.exe");

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadPinnedAsync(
            Asset,
            destination,
            _ => true,
            CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Amd64LookingWrongManagedCacheIsRejectedAndReplaced()
    {
        var trusted = BuildAmd64LookingPayload(4096, 0x42);
        var wrongCache = trusted.ToArray();
        wrongCache[^1] ^= 0x7f;
        var asset = CreateAsset(trusted);
        var networkRequests = 0;
        using var downloader = CreateDownloader((request, _) =>
        {
            Interlocked.Increment(ref networkRequests);
            if (request.RequestUri!.Host == "api.github.com")
                Assert.Equal(
                    "/repos/derrod/legendary/releases/tags/0.21.0",
                    request.RequestUri.AbsolutePath);
            return Task.FromResult(request.RequestUri!.Host == "api.github.com"
                ? JsonResponse(ReleaseJson(asset))
                : BinaryResponse(trusted));
        });
        var destination = Path.Combine(_fixture, "legendary.exe");
        await File.WriteAllBytesAsync(destination, wrongCache);

        var result = await downloader.DownloadPinnedAsync(
            asset,
            destination,
            LooksLikeAmd64Pe,
            CancellationToken.None);

        Assert.Equal(destination, result);
        Assert.Equal(2, networkRequests);
        Assert.Equal(trusted, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task ValidPinnedManagedCacheReturnsWithoutNetwork()
    {
        var trusted = BuildAmd64LookingPayload(4096, 0x24);
        var asset = CreateAsset(trusted);
        var networkRequests = 0;
        using var downloader = CreateDownloader((_, _) =>
        {
            Interlocked.Increment(ref networkRequests);
            throw new InvalidOperationException("Valid pinned cache must not use the network.");
        });
        var destination = Path.Combine(_fixture, "legendary.exe");
        await File.WriteAllBytesAsync(destination, trusted);

        var result = await downloader.DownloadPinnedAsync(
            asset,
            destination,
            LooksLikeAmd64Pe,
            CancellationToken.None);

        Assert.Equal(destination, result);
        Assert.Equal(0, networkRequests);
    }

    [Fact]
    public async Task ConcurrentRequestsForSameDestinationShareOneVerifiedPromotion()
    {
        var payload = Encoding.UTF8.GetBytes("downloaded payload");
        var asset = CreateAsset(payload);
        var apiRequests = 0;
        var assetRequests = 0;
        using var downloader = CreateDownloader(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                Interlocked.Increment(ref apiRequests);
                return JsonResponse(ReleaseJson(asset));
            }

            Interlocked.Increment(ref assetRequests);
            await Task.Delay(75, ct);
            return BinaryResponse(payload);
        });
        var destination = Path.Combine(_fixture, "legendary.exe");
        bool Validator(string path) =>
            File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(payload);

        var first = downloader.DownloadPinnedAsync(asset, destination, Validator, CancellationToken.None);
        var second = downloader.DownloadPinnedAsync(asset, destination, Validator, CancellationToken.None);
        var paths = await Task.WhenAll(first, second);

        Assert.All(paths, path => Assert.Equal(destination, path));
        Assert.Equal(1, apiRequests);
        Assert.Equal(1, assetRequests);
        Assert.True(Validator(destination));
        Assert.Empty(Directory.EnumerateFiles(_fixture, "*.download"));
    }

    [Fact]
    public void TemporaryPathsAreUniqueAndRemainBesideDestination()
    {
        var destination = Path.Combine(_fixture, "legendary.exe");

        var first = VerifiedGitHubReleaseDownloader.CreateUniqueTemporaryPath(destination);
        var second = VerifiedGitHubReleaseDownloader.CreateUniqueTemporaryPath(destination);

        Assert.NotEqual(first, second);
        Assert.Equal(Path.GetFullPath(_fixture), Path.GetDirectoryName(first), ignoreCase: true);
        Assert.Equal(Path.GetFullPath(_fixture), Path.GetDirectoryName(second), ignoreCase: true);
        Assert.EndsWith(".download", first, StringComparison.Ordinal);
        Assert.EndsWith(".download", second, StringComparison.Ordinal);
    }

    [Fact]
    public void EpicAndGogAdaptersPinAndVerifyManagedCaches()
    {
        var epic = File.ReadAllText(FindRepoFile("ExoLauncher", "Adapters", "EpicAdapter.cs"));
        var gog = File.ReadAllText(FindRepoFile("ExoLauncher", "Adapters", "GogAdapter.cs"));

        Assert.Contains("VerifiedGitHubReleaseDownloader.Shared", epic, StringComparison.Ordinal);
        Assert.Contains("VerifiedGitHubReleaseDownloader.Shared", gog, StringComparison.Ordinal);
        Assert.Contains("IsPinnedAssetFile", epic, StringComparison.Ordinal);
        Assert.Contains("IsPinnedAssetFile", gog, StringComparison.Ordinal);
        Assert.Contains("0.21.0", epic, StringComparison.Ordinal);
        Assert.Contains("17_610_944", epic, StringComparison.Ordinal);
        Assert.Contains("4c01a14c0acb0c46069b197ae7212ea4ea6b861661126ca0593cdac31658fb01", epic, StringComparison.Ordinal);
        Assert.Contains("v1.3.0", gog, StringComparison.Ordinal);
        Assert.Contains("12_304_645", gog, StringComparison.Ordinal);
        Assert.Contains("69ea54467371803f681d6c39805992e3a4b8ddccb44ac8a1de7b1e3c80deaeec", gog, StringComparison.Ordinal);
        Assert.DoesNotContain("releases/latest", epic, StringComparison.Ordinal);
        Assert.DoesNotContain("releases/latest", gog, StringComparison.Ordinal);
    }

    private static GitHubReleaseAsset CreateAsset(byte[] payload) => new(
        "derrod",
        "legendary",
        "0.21.0",
        AssetName,
        payload.LongLength,
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());

    private static VerifiedGitHubReleaseDownloader CreateDownloader(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) =>
        new(new DelegateHandler(responseFactory));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage BinaryResponse(byte[] payload) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(payload),
    };

    private static string ReleaseJson(
        GitHubReleaseAsset asset,
        string? assetName = null,
        string? digest = "valid",
        long? declaredSize = null,
        string? tag = null)
    {
        var resolvedName = assetName ?? asset.AssetName;
        var resolvedTag = tag ?? asset.Tag;
        var resolvedDigest = digest == "valid" ? "sha256:" + asset.ExpectedSha256 : digest;
        return JsonSerializer.Serialize(new
        {
            tag_name = resolvedTag,
            assets = new[]
            {
                new
                {
                    name = resolvedName,
                    state = "uploaded",
                    size = declaredSize ?? asset.ExpectedSize,
                    digest = resolvedDigest,
                    browser_download_url =
                        $"https://github.com/{asset.Owner}/{asset.Repository}/releases/download/{resolvedTag}/{resolvedName}",
                },
            },
        });
    }

    private static byte[] BuildAmd64LookingPayload(int length, byte fill)
    {
        var payload = Enumerable.Repeat(fill, length).ToArray();
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(payload, 0x3c);
        payload[0x80] = (byte)'P';
        payload[0x81] = (byte)'E';
        payload[0x82] = 0;
        payload[0x83] = 0;
        BitConverter.GetBytes((ushort)0x8664).CopyTo(payload, 0x84);
        return payload;
    }

    private static bool LooksLikeAmd64Pe(string path)
    {
        var payload = File.ReadAllBytes(path);
        if (payload.Length < 0x86 || payload[0] != (byte)'M' || payload[1] != (byte)'Z') return false;
        var peOffset = BitConverter.ToInt32(payload, 0x3c);
        return peOffset >= 0
               && peOffset <= payload.Length - 6
               && payload[peOffset] == (byte)'P'
               && payload[peOffset + 1] == (byte)'E'
               && BitConverter.ToUInt16(payload, peOffset + 4) == 0x8664;
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Repository file not found.", Path.Combine(parts));
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request, cancellationToken);
    }
}
