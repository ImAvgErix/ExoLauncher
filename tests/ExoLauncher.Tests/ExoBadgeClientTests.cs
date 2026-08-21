using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ExoBadgeClientTests
{
    [Fact]
    public void BadgeCatalog_DropsFutureKeysButRejectsKnownKeyMasquerading()
    {
        var badges = new List<ExoProfileBadge>
        {
            new()
            {
                Key = "future_badge",
                Label = "Future badge",
                Description = "Added by a newer service",
                Tone = "future",
            },
            new()
            {
                Key = "contributor",
                Label = "Contributor",
                Description = "Contributed to Exo",
                Tone = "community",
            },
        };

        Assert.True(ExoBadgeCatalog.SanitizeBadgeSet(badges));
        Assert.Equal("contributor", Assert.Single(badges).Key);

        badges =
        [
            new ExoProfileBadge
            {
                Key = "contributor",
                Label = "Founder",
                Description = "Founder of Exo",
                Tone = "community",
            },
        ];
        Assert.False(ExoBadgeCatalog.SanitizeBadgeSet(badges));
    }

    [Fact]
    public async Task AdminBadgeClient_UsesBearerExactRoutesAndKnownBadgeKeys()
    {
        var root = TempRoot();
        try
        {
            const string token = "badge-test-secret";
            var store = SessionStore(root, token);
            var handler = new BadgeHandler();
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787");

            var loaded = await client.GetManagedBadgesAsync("Target_User");
            var granted = await client.GrantManagedBadgeAsync("Target_User", "contributor");
            var revoked = await client.RevokeManagedBadgeAsync("Target_User", "contributor");

            Assert.True(loaded.Ok);
            Assert.Equal("Target_User", loaded.Value?.Handle.Display);
            Assert.Equal("contributor", Assert.Single(loaded.Value!.Badges).Key);
            Assert.True(granted.Ok);
            Assert.True(revoked.Ok);
            Assert.Equal([HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete], handler.Methods);
            Assert.Contains("handle=Target_User", handler.Uris[0].Query, StringComparison.Ordinal);
            Assert.All(handler.Authorization, header =>
            {
                Assert.Equal("Bearer", header?.Scheme);
                Assert.Equal(token, header?.Parameter);
            });
            Assert.Contains("\"handle\":\"Target_User\"", handler.Bodies[0], StringComparison.Ordinal);
            Assert.Contains("\"badge\":\"contributor\"", handler.Bodies[0], StringComparison.Ordinal);

            var requestsBeforeInvalid = handler.Methods.Count;
            var invalid = await client.GrantManagedBadgeAsync("Target_User", "custom_html_badge");
            Assert.False(invalid.Ok);
            Assert.Equal("INVALID_REQUEST", invalid.Diagnostics.Error?.Code);
            Assert.Equal(requestsBeforeInvalid, handler.Methods.Count);

            var reserved = await client.GrantManagedBadgeAsync("Target_User", "founder");
            Assert.False(reserved.Ok);
            Assert.Equal("INVALID_REQUEST", reserved.Diagnostics.Error?.Code);
            Assert.Equal(requestsBeforeInvalid, handler.Methods.Count);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task BadgeClient_RejectsUnknownToneAndPublicProfileKeepsSafeBadges()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root, "badge-validation-secret");
            var handler = new BadgeHandler { InvalidTone = true };
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787");

            var invalid = await client.GetManagedBadgesAsync("Target_User");
            Assert.False(invalid.Ok);
            Assert.Equal("INVALID_RESPONSE", invalid.Diagnostics.Error?.Code);

            handler.InvalidTone = false;
            handler.IncludeFutureBadge = true;
            var profile = await client.GetPublicProfileAsync("Target_User");
            Assert.True(profile.Ok);
            var badge = Assert.Single(profile.Value!.Badges);
            Assert.Equal("contributor", badge.Key);
            Assert.Equal("community", badge.Tone);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task AccountProjection_RequiresKnownRolesAndServerPermissionAgreement()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root, "account-authority-secret");
            var handler = new AccountHandler();
            var lifecycle = new ExoIdentityLifecycle(
                store,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                new ExoProfileMediaCache(Path.Combine(root, "media-cache")));
            using var service = new ExoAccountService(
                store,
                handler,
                static _ => false,
                static () => throw new InvalidOperationException("Browser flow is not used by this test."),
                origin: "http://127.0.0.1:8787",
                lifecycle: lifecycle);

            var account = await service.GetAccountAsync();

            Assert.True(account.SignedIn);
            Assert.Equal(["owner", "developer"], account.Roles);
            Assert.True(account.CanManageBadges);
            Assert.Equal("founder", Assert.Single(account.Badges).Key);

            handler.MeJson =
                """{"id":"account-id","name":"Owner","email":"owner@example.test","handle":{"display":"Owner","normalized":"owner"},"roles":["superadmin"],"canManageBadges":true,"badges":[],"session":{"id":"session-id","expiresAt":"2099-01-01T00:00:00Z"}}""";
            var rejected = await service.GetAccountAsync();

            Assert.Empty(rejected.Roles);
            Assert.Empty(rejected.Badges);
            Assert.False(rejected.CanManageBadges);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static ExoSessionStore SessionStore(string root, string token)
    {
        var store = new ExoSessionStore(Path.Combine(root, "auth.bin"));
        store.Save(new ExoSession
        {
            V = 1,
            AccessToken = token,
            AccountId = "account-id",
            Handle = "Owner",
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
        });
        return store;
    }

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "exo-badge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }

    private sealed class BadgeHandler : HttpMessageHandler
    {
        public bool InvalidTone { get; set; }
        public bool IncludeFutureBadge { get; set; }
        public List<HttpMethod> Methods { get; } = [];
        public List<Uri> Uris { get; } = [];
        public List<AuthenticationHeaderValue?> Authorization { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Uris.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization);
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

            var tone = InvalidTone ? "rainbow" : "community";
            var future = IncludeFutureBadge
                ? ",{\"key\":\"future_badge\",\"label\":\"Future badge\",\"description\":\"Added by a newer service\",\"tone\":\"future\"}"
                : string.Empty;
            var badges = $"[{{\"key\":\"contributor\",\"label\":\"Contributor\",\"description\":\"Contributed to Exo\",\"tone\":\"{tone}\"}}{future}]";
            var json = request.RequestUri!.AbsolutePath.StartsWith("/v1/profiles/", StringComparison.Ordinal)
                ? $"{{\"userId\":\"target-id\",\"handle\":{{\"display\":\"Target_User\",\"normalized\":\"target_user\"}},\"profile\":{{}},\"media\":{{}},\"badges\":{badges}}}"
                : $"{{\"handle\":{{\"display\":\"Target_User\",\"normalized\":\"target_user\"}},\"badges\":{badges}}}";
            return Json(json);
        }
    }

    private sealed class AccountHandler : HttpMessageHandler
    {
        public string MeJson { get; set; } =
            """{"id":"account-id","name":"Owner","email":"owner@example.test","handle":{"display":"Owner","normalized":"owner"},"roles":["owner","developer"],"canManageBadges":true,"badges":[{"key":"founder","label":"Founder","description":"Founder of Exo","tone":"founder"}],"session":{"id":"session-id","expiresAt":"2099-01-01T00:00:00Z"}}""";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = request.RequestUri?.AbsolutePath == ExoIdContract.HealthPath
                ? """{"ok":true,"service":"exo-id","capabilities":{"providers":{"google":false,"email":false,"password":true},"profiles":true,"friends":true,"media":true,"presence":true}}"""
                : MeJson;
            return Task.FromResult(Json(json));
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
