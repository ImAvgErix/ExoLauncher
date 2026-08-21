using System.Net;
using System.Net.Http;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class AchievementIconCacheTests
{
    [Theory]
    [InlineData("https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/10/icon.jpg", true)]
    [InlineData("https://shared.steamstatic.com/store_item_assets/steam/apps/10/icon.jpg", true)]
    [InlineData("https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/10/icon.jpg", true)]
    [InlineData("https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/10/icon.jpg", true)]
    [InlineData("https://shared-static-prod.epicgames.com/epic-achievements/icon.png", true)]
    [InlineData("https://cdn1.epicgames.com/epic-achievements/icon.png", true)]
    [InlineData("https://cdn2.unrealengine.com/epic-achievements/icon.png", true)]
    [InlineData("https://images.gog.com/icon_gac_60.jpg", true)]
    [InlineData("https://images.gog-statics.com/icon_gac_60.jpg", true)]
    [InlineData("https://images.gog.com:443/icon_gac_60.jpg", true)]
    [InlineData("https://cdn.example.test/icon.png", false)]
    [InlineData("http://cdn.akamai.steamstatic.com/icon.png", false)]
    [InlineData("https://cdn.akamai.steamstatic.com:444/icon.png", false)]
    [InlineData("https://cdn.akamai.steamstatic.com.attacker.test/icon.png", false)]
    [InlineData("https://images.gog.com.attacker.test/icon.png", false)]
    [InlineData("https://cdn1.epicgames.com@attacker.test/icon.png", false)]
    [InlineData("file:///C:/icon.png", false)]
    [InlineData("not a uri", false)]
    public void OnlyApprovedProviderHttpsArtworkUrisAreEligible(string value, bool expected)
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
            var first = await cache.CacheAsync("https://cdn.akamai.steamstatic.com/achievement.png");
            var second = await cache.CacheAsync("https://cdn.akamai.steamstatic.com/achievement.png");

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
    public async Task UnapprovedSourceNeverReachesTheNetworkOrCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-icon-cache-tests", Guid.NewGuid().ToString("N"));
        var handler = new CountingHandler(TinyPng());
        using var http = new HttpClient(handler);
        var cache = new AchievementIconCache(root, http);
        try
        {
            var cached = await cache.CacheAsync("https://attacker.test/achievement.png");

            Assert.Null(cached);
            Assert.Equal(0, handler.Calls);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FinalResponseUriMustRemainOnTheProviderAllowlist()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-icon-cache-tests", Guid.NewGuid().ToString("N"));
        var handler = new CountingHandler(TinyPng(), "https://attacker.test/final.png");
        using var http = new HttpClient(handler);
        var cache = new AchievementIconCache(root, http);
        try
        {
            var cached = await cache.CacheAsync("https://cdn1.epicgames.com/achievement.png");

            Assert.Null(cached);
            Assert.Equal(1, handler.Calls);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RedirectResponsesAreNotFollowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-icon-cache-tests", Guid.NewGuid().ToString("N"));
        var handler = new RedirectHandler("https://images.gog.com/redirected.png");
        using var http = new HttpClient(handler);
        var cache = new AchievementIconCache(root, http);
        try
        {
            var cached = await cache.CacheAsync("https://images.gog.com/achievement.png");

            Assert.Null(cached);
            Assert.Equal(1, handler.Calls);
            Assert.False(Directory.Exists(root));
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
            var first = Assert.IsType<string>(await cache.CacheAsync("https://images.gog.com/retry.png"));
            await File.WriteAllBytesAsync(first, "truncated"u8.ToArray());

            var repaired = Assert.IsType<string>(await cache.CacheAsync("https://images.gog.com/retry.png"));

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
            var cached = await cache.CacheAsync("https://shared-static-prod.epicgames.com/truncated.png");

            Assert.Null(cached);
            Assert.Equal(1, handler.Calls);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NotificationDeliveryNeverFallsBackToArbitraryRemoteArtwork()
    {
        var appServices = File.ReadAllText(Path.Combine(
            RepoRoot(), "ExoLauncher", "Services", "AppServices.cs"));
        var presenter = File.ReadAllText(Path.Combine(
            RepoRoot(), "ExoLauncher", "Services", "TrophyNotificationPresenter.cs"));
        var trophyDocument = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "trophy.html"));

        Assert.DoesNotContain("CacheAsync(iconUrl).ConfigureAwait(false) ?? iconUrl", appServices, StringComparison.Ordinal);
        Assert.DoesNotContain("CacheAsync(coverUrl).ConfigureAwait(false) ?? coverUrl", appServices, StringComparison.Ordinal);
        Assert.Contains("IsTrustedVirtualIconUri", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("img-src 'self' data: https:;", trophyDocument, StringComparison.Ordinal);
        Assert.Contains("https://trophy-icons.exo-launcher.local", trophyDocument, StringComparison.Ordinal);
        Assert.Contains("https://covers.exo-launcher.local", trophyDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvidersAndBridgeDtosUseTheSharedProviderImagePolicy()
    {
        var root = RepoRoot();
        foreach (var relativePath in new[]
                 {
                     Path.Combine("ExoLauncher", "Services", "Achievements", "EpicLegendaryAchievementProvider.cs"),
                     Path.Combine("ExoLauncher", "Services", "Achievements", "GogGameplayAchievementProvider.cs"),
                     Path.Combine("ExoLauncher", "Services", "Achievements", "SteamLibraryCacheAchievementProvider.cs"),
                     Path.Combine("ExoLauncher", "Services", "Achievements", "SteamWebApiAchievementParser.cs"),
                     Path.Combine("ExoLauncher", "Services", "WebHostBridge.cs"),
                     Path.Combine("ExoLauncher", "Services", "ShellController.cs"),
                 })
        {
            var source = File.ReadAllText(Path.Combine(root, relativePath));
            Assert.Contains("AchievementIconCache.SanitizeProviderImageUrl", source,
                StringComparison.Ordinal);
        }

        foreach (var relativePath in new[]
                 {
                     Path.Combine("ExoLauncher", "Services", "WebHostBridge.cs"),
                     Path.Combine("ExoLauncher", "Services", "ShellController.cs"),
                 })
        {
            var source = File.ReadAllText(Path.Combine(root, relativePath));
            Assert.DoesNotContain(
                "iconUrl = redact ? null : entry.Definition.IconUnlockedUrl",
                source,
                StringComparison.Ordinal);
        }
    }

    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class CountingHandler(byte[] body, string? finalUri = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = finalUri is null
                    ? request
                    : new HttpRequestMessage(HttpMethod.Get, finalUri),
            });
        }
    }

    private sealed class RedirectHandler(string location) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                RequestMessage = request,
            };
            response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        }
    }
}
