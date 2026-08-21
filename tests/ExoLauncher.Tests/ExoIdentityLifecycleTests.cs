using System.Net;
using System.Text;
using System.Text.Json;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ExoIdentityLifecycleTests
{
    [Fact]
    public async Task Remote401_CompletesTheBridgeSignOutLifecycleExactlyOnce()
    {
        var root = TempRoot();
        try
        {
            var sessionStore = new ExoSessionStore(Path.Combine(root, "auth.bin"));
            sessionStore.Save(new ExoSession
            {
                AccessToken = "expired-bridge-secret",
                AccountId = "self-user",
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            });
            var onlineCache = new ExoOnlineCache(Path.Combine(root, "online-cache"));
            Assert.True(onlineCache.Write(
                "self-user",
                "friends:50:",
                new ExoFriendPage(),
                DateTimeOffset.UtcNow));
            var mediaRoot = Path.Combine(root, "online-media");
            Directory.CreateDirectory(mediaRoot);
            var cachedMedia = Path.Combine(mediaRoot, "profile-" + new string('a', 64) + ".png");
            await File.WriteAllBytesAsync(cachedMedia, [0x89, 0x50, 0x4e, 0x47]);
            var mediaCache = new ExoProfileMediaCache(mediaRoot);

            var mappedMedia = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["self-user:avatar"] = "mapped",
            };
            var presenceStops = 0;
            var published = new List<string>();
            var bridge = new ExoBridgeSessionCoordinator(
                clearMappedMedia: mappedMedia.Clear,
                stopPresenceAsync: () =>
                {
                    presenceStops++;
                    return Task.CompletedTask;
                },
                signedOutAccount: () => new ExoAccountState
                {
                    Ok = true,
                    SignedIn = false,
                    Configured = true,
                    Providers = ["password"],
                },
                profileSnapshot: () => new { ok = true, name = "Local profile" },
                publishEvent: (name, data) => published.Add(ExoBridgeProtocol.SerializeEvent(name, data)));
            var lifecycle = new ExoIdentityLifecycle(
                sessionStore,
                onlineCache,
                mediaCache,
                bridge.CompleteSignedOutAsync);
            lifecycle.MarkSignedIn();

            using var client = new ExoOnlineClient(
                sessionStore,
                new UnauthorizedHandler(),
                onlineCache,
                origin: "http://127.0.0.1:8787",
                mediaCache: mediaCache,
                lifecycle: lifecycle);

            var result = await client.GetFriendsAsync();
            await lifecycle.EndSessionAsync(ExoSessionEndReason.RemoteUnauthorized);

            Assert.False(result.Ok);
            Assert.Equal("UNAUTHENTICATED", result.Diagnostics.Error?.Code);
            Assert.Null(sessionStore.TryLoad());
            Assert.False(File.Exists(sessionStore.Path));
            Assert.False(onlineCache.TryRead<ExoFriendPage>(
                "self-user", "friends:50:", out _, out _));
            Assert.False(File.Exists(cachedMedia));
            Assert.Empty(mappedMedia);
            Assert.Equal(1, presenceStops);
            Assert.Collection(
                published,
                accountEvent =>
                {
                    Assert.Contains("\"event\":\"account.updated\"", accountEvent, StringComparison.Ordinal);
                    Assert.Contains("\"signedIn\":false", accountEvent, StringComparison.Ordinal);
                },
                profileEvent => Assert.Contains(
                    "\"event\":\"profile.updated\"", profileEvent, StringComparison.Ordinal));
            Assert.DoesNotContain(
                "expired-bridge-secret",
                string.Join(string.Empty, published),
                StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task AccountRefresh401_UsesTheSameAwaitedBridgeLifecycle()
    {
        var root = TempRoot();
        try
        {
            var sessionStore = new ExoSessionStore(Path.Combine(root, "auth.bin"));
            sessionStore.Save(new ExoSession
            {
                AccessToken = "expired-account-secret",
                AccountId = "self-user",
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            });
            var onlineCache = new ExoOnlineCache(Path.Combine(root, "online-cache"));
            var mediaCache = new ExoProfileMediaCache(Path.Combine(root, "online-media"));
            var presenceStopped = false;
            var events = new List<string>();
            var bridge = new ExoBridgeSessionCoordinator(
                clearMappedMedia: () => { },
                stopPresenceAsync: () =>
                {
                    presenceStopped = true;
                    return Task.CompletedTask;
                },
                signedOutAccount: () => new ExoAccountState
                {
                    Ok = true,
                    SignedIn = false,
                    Configured = true,
                    Providers = ["password"],
                },
                profileSnapshot: () => new { ok = true },
                publishEvent: (name, _) => events.Add(name));
            var lifecycle = new ExoIdentityLifecycle(
                sessionStore,
                onlineCache,
                mediaCache,
                bridge.CompleteSignedOutAsync);
            lifecycle.MarkSignedIn();

            using var service = new ExoAccountService(
                sessionStore,
                new AccountUnauthorizedHandler(),
                openBrowser: _ => false,
                startListener: () => throw new InvalidOperationException("not used"),
                origin: "http://127.0.0.1:8787",
                lifecycle: lifecycle);

            var account = await service.GetAccountAsync();

            Assert.False(account.SignedIn);
            Assert.Null(sessionStore.TryLoad());
            Assert.True(presenceStopped);
            Assert.Equal(["account.updated", "profile.updated"], events);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void BridgeProtocol_ParsesRequestsAndSerializesStableResultAndEventSchemas()
    {
        Assert.True(ExoBridgeProtocol.TryParseRequest(
            """{"id":"rpc-1","method":"online.profiles.get","params":{"handle":"Erix"}}""",
            out var request));
        Assert.Equal("rpc-1", request.Id);
        Assert.Equal("online.profiles.get", request.Method);
        Assert.True(request.HasParams);
        Assert.Equal("Erix", request.Params.GetProperty("handle").GetString());

        var response = ExoBridgeProtocol.SerializeResponse(
            "rpc-1",
            ok: true,
            result: new ExoOnlineResult<ExoPublicProfile>(
                true,
                new ExoPublicProfile
                {
                    UserId = "peer-user",
                    Profile = new Dictionary<string, JsonElement>
                    {
                        ["displayName"] = JsonSerializer.SerializeToElement("Peer"),
                    },
                },
                new ExoOnlineDiagnostics(true, true, "live", null, false, null)),
            error: null);
        using var responseDocument = JsonDocument.Parse(response);
        var responseRoot = responseDocument.RootElement;
        Assert.Equal(["id", "ok", "result"], responseRoot.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Peer", responseRoot.GetProperty("result").GetProperty("value")
            .GetProperty("profile").GetProperty("displayName").GetString());
        Assert.False(responseRoot.GetProperty("result").GetProperty("value")
            .GetProperty("profile").TryGetProperty("showLevel", out _));

        var eventJson = ExoBridgeProtocol.SerializeEvent(
            "online.presence",
            new ExoBridgePresenceEvent
            {
                Kind = "presence",
                Presence = new ExoBridgePresenceEntry
                {
                    UserId = "peer-user",
                    Status = "unknown",
                    Available = false,
                },
            });
        using var eventDocument = JsonDocument.Parse(eventJson);
        var eventRoot = eventDocument.RootElement;
        Assert.Equal(["event", "data"], eventRoot.EnumerateObject().Select(p => p.Name));
        Assert.Equal("online.presence", eventRoot.GetProperty("event").GetString());
        Assert.Equal("unknown", eventRoot.GetProperty("data").GetProperty("presence")
            .GetProperty("status").GetString());
        Assert.False(eventRoot.GetProperty("data").GetProperty("presence")
            .GetProperty("available").GetBoolean());
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-identity-lifecycle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }

    private sealed class UnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"error":{"code":"UNAUTHENTICATED","message":"Sign in required."}}""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class AccountUnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(path == ExoIdContract.HealthPath
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"ok":true,"service":"exo-id","capabilities":{"providers":{"google":false,"email":false,"password":true}}}""",
                        Encoding.UTF8,
                        "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        """{"error":{"code":"UNAUTHENTICATED","message":"Sign in required."}}""",
                        Encoding.UTF8,
                        "application/json"),
                });
        }
    }
}
