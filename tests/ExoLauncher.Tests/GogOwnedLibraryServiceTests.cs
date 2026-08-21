using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class GogOwnedLibraryServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "ExoLauncherGogTests",
        Guid.NewGuid().ToString("N"));

    public GogOwnedLibraryServiceTests() => Directory.CreateDirectory(_tempDir);

    [Theory]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"user_id\":\"42\"}")]
    [InlineData("{\"46899977096215655\":{\"access_token\":\"a\",\"refresh_token\":\"r\",\"user_id\":\"42\"}}")]
    [InlineData("{\"client\":{\"userId\":\"42\",\"refreshToken\":\"r\",\"token\":{\"accessToken\":\"a\"}}}")]
    [InlineData("{\"credentials\":[{\"accountId\":\"42\",\"refreshToken\":\"r\",\"accessToken\":\"a\"}]}")]
    public void CredentialParser_AcceptsKnownNestedShapes(string json)
    {
        Assert.True(GogdlCli.TryReadCredentials(json, out var credentials));
        Assert.Equal("a", credentials.AccessToken);
        Assert.Equal("r", credentials.RefreshToken);
        Assert.Equal("42", credentials.UserId);
        Assert.True(GogdlCli.HasAuthenticatedCredentials(json));
    }

    [Fact]
    public void CredentialParser_ComputesExpiry_AndRejectsPartialPayloads()
    {
        const string json = """
            {"client":{"loginTime":1000,"expires_in":3600,"token":{
              "access_token":"a","refresh_token":"r","user_id":"42"
            }}}
            """;

        Assert.True(GogdlCli.TryReadCredentials(json, out var credentials));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4600), credentials.ExpiresAtUtc);
        Assert.True(credentials.IsExpired(DateTimeOffset.FromUnixTimeSeconds(5000), TimeSpan.Zero));
        Assert.False(GogdlCli.TryReadCredentials("{\"access_token\":\"a\"}", out _));
        Assert.False(GogdlCli.HasAuthenticatedCredentials(
            "{\"access_token\":\"a\",\"user_id\":\"42\"}"));
    }

    [Fact]
    public async Task Refresh_PaginatesFiltersAndCachesMetadata_WithBoundedConcurrency()
    {
        var metadataRequests = 0;
        var activeMetadata = 0;
        var maxActiveMetadata = 0;
        var pageTokens = new List<string?>();
        var handler = new StubHandler(async request =>
        {
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "secret"), request.Headers.Authorization);
            if (request.RequestUri!.Host == "galaxy-library.gog.com")
            {
                var token = ParseQueryValue(request.RequestUri.Query, "page_token");
                lock (pageTokens) pageTokens.Add(token);
                if (token is null)
                {
                    return Json(HttpStatusCode.OK, """
                        {"total_count":10,"next_page_token":"next/+ token","items":[
                          {"platform_id":"gog","external_id":"101","owned":true,"certificate":"cert-101"},
                          {"platform_id":"gog","external_id":"102","owned":true,"certificate":"cert-102"},
                          {"platform_id":"gog","external_id":"103","owned":true,"certificate":"cert-103"},
                          {"platform_id":"gog","external_id":"104","owned":true,"certificate":"cert-104"},
                          {"platform_id":"gog","external_id":"105","owned":true,"certificate":"cert-105"},
                          {"platform_id":"gog","external_id":"106","owned":true,"certificate":"cert-106"},
                          {"platform_id":"gog","external_id":"107","owned":true,"certificate":"cert-107"},
                          {"platform_id":"gog","external_id":"108","owned":true,"certificate":"cert-108"},
                          {"platform_id":"steam","external_id":"999","owned":true,"certificate":"ignored"}
                        ]}
                        """);
                }

                Assert.Equal("next/+ token", token);
                return Json(HttpStatusCode.OK, """
                    {"total_count":10,"items":[
                      {"platform_id":"gog","external_id":"109","owned":true,"certificate":"cert-109"},
                      {"platform_id":"gog","external_id":"110","owned":false,"certificate":"ignored"}
                    ]}
                    """);
            }

            if (request.RequestUri.Host == "gamesdb.gog.com")
            {
                Interlocked.Increment(ref metadataRequests);
                var active = Interlocked.Increment(ref activeMetadata);
                UpdateMaximum(ref maxActiveMetadata, active);
                try
                {
                    await Task.Delay(20);
                    var id = request.RequestUri.Segments[^1].Trim('/');
                    Assert.True(request.Headers.TryGetValues("X-GOG-Library-Cert", out var values));
                    Assert.Equal("cert-" + id, Assert.Single(values));
                    return Json(HttpStatusCode.OK, $$$"""
                        {"type":"game","title":{"*":"Title {{{id}}}"},"game":{
                          "visible_in_library":true,
                          "vertical_cover":{"url_format":"https://images.gog-statics.com/{{{id}}}{formatter}.{ext}"}
                        }}
                        """);
                }
                finally
                {
                    Interlocked.Decrement(ref activeMetadata);
                }
            }

            throw new InvalidOperationException("Unexpected request " + request.RequestUri);
        });

        using var http = new HttpClient(handler);
        var cache = Path.Combine(_tempDir, "gog-owned.json");
        using var service = new GogOwnedLibraryService(http, cache);
        var credentials = new GogdlCli.AuthCredentials("secret", "user/42", "refresh", null);

        var first = await service.RefreshAsync(credentials, force: true);

        Assert.True(first.Ok, first.Message);
        Assert.True(first.Updated);
        Assert.Equal(9, first.GameCount);
        Assert.Equal(9, metadataRequests);
        Assert.InRange(maxActiveMetadata, 2, 6);
        Assert.Equal([null, "next/+ token"], pageTokens);
        Assert.True(File.Exists(cache));
        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp-*"));

        var cacheText = await File.ReadAllTextAsync(cache);
        Assert.DoesNotContain(credentials.UserId, cacheText, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.AccessToken, cacheText, StringComparison.Ordinal);
        var cachedGames = GogdlCli.ParseOwnedLibraryJson(cacheText);
        Assert.Equal(9, cachedGames.Count);
        Assert.Equal(9, service.LoadCachedOwnedGames(credentials.UserId).Count);
        Assert.Equal("Title 101", cachedGames.Single(game => game.Id == "101").Title);
        Assert.Equal(
            "https://images.gog-statics.com/101_glx_vertical_cover.jpg",
            cachedGames.Single(game => game.Id == "101").CoverUrl);

        var second = await service.RefreshAsync(credentials, force: true);
        Assert.True(second.Ok, second.Message);
        Assert.Equal(9, metadataRequests); // 30-day metadata cache avoided another fan-out.
    }

    [Fact]
    public async Task Refresh_UsesProductMetadataFallback()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "galaxy-library.gog.com")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"total_count\":1,\"items\":[{\"platform_id\":\"gog\",\"external_id\":\"123\"}]}"));
            if (request.RequestUri.Host == "gamesdb.gog.com")
                return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
            if (request.RequestUri.Host == "api.gog.com")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"title\":\"Fallback title\",\"images\":{\"logo\":\"https://images.gog-statics.com/fallback.jpg\"}}"));
            throw new InvalidOperationException();
        });
        using var http = new HttpClient(handler);
        using var service = new GogOwnedLibraryService(http, Path.Combine(_tempDir, "fallback.json"));

        var result = await service.RefreshAsync(
            new GogdlCli.AuthCredentials("secret", "42", "refresh", null),
            force: true);

        Assert.True(result.Ok, result.Message);
        var game = Assert.Single(GogdlCli.ParseOwnedLibraryJson(
            await File.ReadAllTextAsync(service.CachePath)));
        Assert.Equal("Fallback title", game.Title);
        Assert.Equal("https://images.gog-statics.com/fallback.jpg", game.CoverUrl);
    }

    [Fact]
    public async Task Refresh_FreshCacheSkipsNetwork_AndFailurePreservesPreviousCache()
    {
        var cache = Path.Combine(_tempDir, "preserved.json");
        var original = AccountCache("42", "[{\"id\":\"77\",\"title\":\"Existing\"}]");
        await File.WriteAllTextAsync(cache, original);
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}"));
        });
        using var http = new HttpClient(handler);
        using var service = new GogOwnedLibraryService(http, cache, cacheMaxAge: TimeSpan.FromDays(1));
        var credentials = new GogdlCli.AuthCredentials("secret", "42", "refresh", null);

        var fresh = await service.RefreshAsync(credentials);
        Assert.True(fresh.Ok);
        Assert.False(fresh.Updated);
        Assert.Equal(0, requests);

        var failed = await service.RefreshAsync(credentials, force: true);
        Assert.False(failed.Ok);
        Assert.Equal(1, requests);
        Assert.Equal(original, await File.ReadAllTextAsync(cache));
    }

    [Fact]
    public async Task Refresh_UnauthorizedIsReportedWithoutReplacingCache()
    {
        var cache = Path.Combine(_tempDir, "unauthorized.json");
        var original = AccountCache("42", "[]");
        await File.WriteAllTextAsync(cache, original);
        var handler = new StubHandler(_ =>
            Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}")));
        using var http = new HttpClient(handler);
        using var service = new GogOwnedLibraryService(http, cache);

        var result = await service.RefreshAsync(
            new GogdlCli.AuthCredentials("secret", "42", "refresh", null),
            force: true);

        Assert.False(result.Ok);
        Assert.True(result.Unauthorized);
        Assert.Equal(original, await File.ReadAllTextAsync(cache));
    }

    [Fact]
    public async Task AccountSwitch_NeverUsesOrReturnsThePreviousUsersFreshCache()
    {
        var cache = Path.Combine(_tempDir, "account-switch.json");
        var prior = AccountCache("old-user", "[{\"id\":\"77\",\"title\":\"Old account game\"}]");
        await File.WriteAllTextAsync(cache, prior);
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}"));
        });
        using var http = new HttpClient(handler);
        using var service = new GogOwnedLibraryService(http, cache, cacheMaxAge: TimeSpan.FromDays(1));

        Assert.Single(service.LoadCachedOwnedGames("old-user"));
        Assert.Empty(service.LoadCachedOwnedGames("new-user"));
        Assert.False(service.IsCacheFresh("new-user"));

        var result = await service.RefreshAsync(
            new GogdlCli.AuthCredentials("new-token", "new-user", "refresh", null));

        Assert.False(result.Ok);
        Assert.Equal(1, requests);
        Assert.Empty(service.LoadCachedOwnedGames("new-user"));
        Assert.Equal(prior, await File.ReadAllTextAsync(cache));
        Assert.DoesNotContain("old-user", prior, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string AccountCache(string userId, string gamesJson) =>
        $"{{\"schemaVersion\":2,\"accountKey\":\"{GogOwnedLibraryService.AccountKeyForUser(userId)}\",\"syncedAtUtc\":\"2026-01-01T00:00:00Z\",\"games\":{gamesJson}}}";

    private static string? ParseQueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            if (!key.Equals(name, StringComparison.Ordinal)) continue;
            return Uri.UnescapeDataString(separator < 0 ? string.Empty : pair[(separator + 1)..]);
        }
        return null;
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref target);
            if (candidate <= observed || Interlocked.CompareExchange(ref target, candidate, observed) == observed)
                return;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request);
    }
}
