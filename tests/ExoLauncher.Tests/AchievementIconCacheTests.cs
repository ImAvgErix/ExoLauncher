using System.Net;
using System.Net.Http;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class AchievementIconCacheTests
{
    [Theory]
    [InlineData("https://cdn.example.test/icon.png", true)]
    [InlineData("http://cdn.example.test/icon.png", false)]
    [InlineData("file:///C:/icon.png", false)]
    [InlineData("not a uri", false)]
    public void OnlyHttpsArtworkUrisAreEligible(string value, bool expected)
    {
        Assert.Equal(expected, AchievementIconCache.TryGetHttpsUri(value, out _));
    }

    [Fact]
    public async Task ValidPngIsCachedLocallyAndSubsequentUseAvoidsAnotherRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-icon-cache-tests", Guid.NewGuid().ToString("N"));
        var handler = new CountingHandler(TinyPng());
        using var http = new HttpClient(handler);
        var cache = new AchievementIconCache(root, http);
        try
        {
            var first = await cache.CacheAsync("https://cdn.example.test/achievement.png");
            var second = await cache.CacheAsync("https://cdn.example.test/achievement.png");

            Assert.NotNull(first);
            Assert.Equal(first, second);
            Assert.True(File.Exists(first));
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ImageValidationRejectsNonImagesAndOversizedDimensions()
    {
        Assert.False(AchievementIconCache.TryValidateImage("not-an-image"u8, out _));
        Assert.False(AchievementIconCache.TryValidateImage(TinyPng()[..24], out _));
        var oversized = TinyPng();
        oversized[16] = 0x00; oversized[17] = 0x00; oversized[18] = 0x13; oversized[19] = 0x88;
        Assert.False(AchievementIconCache.TryValidateImage(oversized, out _));
        Assert.True(AchievementIconCache.TryValidateImage(TinyPng(), out var extension));
        Assert.Equal(".png", extension);
    }

    [Fact]
    public async Task CorruptCachedIconIsDiscardedAndFetchedAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-icon-cache-tests", Guid.NewGuid().ToString("N"));
        var handler = new CountingHandler(TinyPng());
        using var http = new HttpClient(handler);
        var cache = new AchievementIconCache(root, http);
        try
        {
            var first = Assert.IsType<string>(await cache.CacheAsync("https://cdn.example.test/retry.png"));
            await File.WriteAllBytesAsync(first, "truncated"u8.ToArray());

            var repaired = Assert.IsType<string>(await cache.CacheAsync("https://cdn.example.test/retry.png"));

            Assert.Equal(first, repaired);
            Assert.Equal(2, handler.Calls);
            Assert.True(AchievementIconCache.TryValidateImage(await File.ReadAllBytesAsync(repaired), out _));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TruncatedDownloadIsRejectedBeforeItCanReachTheCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-icon-cache-tests", Guid.NewGuid().ToString("N"));
        var handler = new CountingHandler(TinyPng()[..24]);
        using var http = new HttpClient(handler);
        var cache = new AchievementIconCache(root, http);
        try
        {
            var cached = await cache.CacheAsync("https://cdn.example.test/truncated.png");

            Assert.Null(cached);
            Assert.Equal(1, handler.Calls);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==");

    private sealed class CountingHandler(byte[] body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request,
            });
        }
    }
}
