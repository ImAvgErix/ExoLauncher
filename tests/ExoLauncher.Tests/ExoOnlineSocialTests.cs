using System.Net;
using System.Text;
using System.Text.Json;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ExoOnlineSocialTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task HealthReportsProviderAndFeatureCapabilitiesWithFreshnessDiagnostics()
    {
        var root = TempRoot();
        try
        {
            var handler = new OnlineHandler();
            using var client = new ExoOnlineClient(
                SessionStore(root),
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787");

            var live = await client.GetHealthAsync();
            Assert.True(live.Ok);
            Assert.False(live.Value?.Capabilities.Providers.Google);
            Assert.False(live.Value?.Capabilities.Providers.Email);
            Assert.True(live.Value?.Capabilities.Providers.Password);
            Assert.True(live.Value?.Capabilities.Profiles);
            Assert.True(live.Value?.Capabilities.Friends);
            Assert.True(live.Value?.Capabilities.Media);
            Assert.True(live.Value?.Capabilities.Presence);
            Assert.Equal(ExoOnlineSources.Live, live.Diagnostics.Source);
            Assert.NotNull(live.Diagnostics.LastSuccessfulSync);

            handler.ThrowTransient = true;
            var cached = await client.GetHealthAsync();
            Assert.True(cached.Ok);
            Assert.Equal(ExoOnlineSources.Cache, cached.Diagnostics.Source);
            Assert.True(cached.Diagnostics.Retryable);
            Assert.Equal(live.Diagnostics.LastSuccessfulSync, cached.Diagnostics.LastSuccessfulSync);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Friends_UsesBearerCachesLastGoodAndDoesNotLieSignedOutOnTransientFailure()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root);
            const string token = "online-secret-token-fixture";
            store.Save(Session(token));
            var handler = new OnlineHandler
            {
                FriendsJson =
                    """{"friends":[{"userId":"peer-immutable-id","handle":{"display":"Peer","normalized":"peer"},"sources":["direct","steam"],"connectedAt":"2026-08-19T20:00:00.000Z"}],"nextCursor":null}""",
            };
            var stamp = new DateTimeOffset(2026, 8, 19, 20, 30, 0, TimeSpan.Zero);
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787",
                utcNow: () => stamp);

            var live = await client.GetFriendsAsync();

            Assert.True(live.Ok);
            Assert.NotNull(live.Value);
            Assert.Equal("peer-immutable-id", Assert.Single(live.Value!.Friends).UserId);
            Assert.Equal(["direct", "steam"], live.Value.Friends[0].Sources);
            Assert.True(live.Diagnostics.Configured);
            Assert.True(live.Diagnostics.SignedIn);
            Assert.Equal(ExoOnlineSources.Live, live.Diagnostics.Source);
            Assert.Equal(stamp, live.Diagnostics.LastSuccessfulSync);
            Assert.False(live.Diagnostics.Retryable);
            Assert.Null(live.Diagnostics.Error);
            Assert.Equal("Bearer", handler.AuthorizationScheme);
            Assert.Equal(token, handler.AuthorizationParameter);
            Assert.Equal(ExoIdContract.FriendsPath, handler.LastPath);
            Assert.DoesNotContain(token, JsonSerializer.Serialize(live, JsonOpts), StringComparison.Ordinal);

            handler.ThrowTransient = true;
            var cached = await client.GetFriendsAsync();

            Assert.True(cached.Ok);
            Assert.NotNull(cached.Value);
            Assert.Equal("peer-immutable-id", Assert.Single(cached.Value!.Friends).UserId);
            Assert.Null(cached.Diagnostics.SignedIn);
            Assert.Equal(ExoOnlineSources.Cache, cached.Diagnostics.Source);
            Assert.Equal(stamp, cached.Diagnostics.LastSuccessfulSync);
            Assert.True(cached.Diagnostics.Retryable);
            Assert.Equal("NETWORK_UNAVAILABLE", cached.Diagnostics.Error?.Code);
            Assert.NotNull(store.TryLoad());
            Assert.DoesNotContain(token, JsonSerializer.Serialize(cached, JsonOpts), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Unauthorized_ClearsTheProtectedSessionAndNeverReturnsCachedPrivateState()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root);
            store.Save(Session("expired-secret-token"));
            var handler = new OnlineHandler { Unauthorized = true };
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787");

            var result = await client.GetFriendsAsync();

            Assert.False(result.Ok);
            Assert.Null(result.Value);
            Assert.False(result.Diagnostics.SignedIn);
            Assert.Equal(ExoOnlineSources.Unavailable, result.Diagnostics.Source);
            Assert.Equal("UNAUTHENTICATED", result.Diagnostics.Error?.Code);
            Assert.Null(store.TryLoad());
            Assert.False(File.Exists(store.Path));
            Assert.DoesNotContain("expired-secret-token", JsonSerializer.Serialize(result, JsonOpts), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PublicProfiles_ParseOnlySafeProjectionAndSearchReturnsImmutableUserIds()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root);
            store.Save(Session("profile-secret-token"));
            var handler = new OnlineHandler
            {
                ProfileJson =
                    """{"userId":"peer-id-1","handle":{"display":"Peer","normalized":"peer"},"profile":{"displayName":"Peer Name","bio":"hello","showLevel":true,"machinePath":"C:\\Secret"},"email":"must-not-pass@example.test"}""",
                SearchJson =
                    """{"profiles":[{"userId":"peer-id-1","handle":{"display":"Peer","normalized":"peer"},"profile":{"displayName":"Peer Name","statusText":"around"}}],"nextCursor":"next_1"}""",
            };
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787");

            var profile = await client.GetPublicProfileAsync("Peer");
            var search = await client.SearchProfilesAsync("pe", limit: 20);

            Assert.True(profile.Ok);
            Assert.Equal("peer-id-1", profile.Value?.UserId);
            Assert.Equal("Peer Name", profile.Value?.Profile["displayName"].GetString());
            Assert.False(profile.Value?.Profile.ContainsKey("machinePath"));
            Assert.False(profile.Value?.Profile.ContainsKey("showLevel"));
            Assert.Equal(ExoOnlineSources.Live, profile.Diagnostics.Source);
            Assert.True(search.Ok);
            Assert.Equal("peer-id-1", Assert.Single(search.Value!.Profiles).UserId);
            Assert.Equal("next_1", search.Value.NextCursor);
            Assert.Contains("q=pe", handler.RequestUris[1].Query, StringComparison.Ordinal);
            Assert.Contains("limit=20", handler.RequestUris[1].Query, StringComparison.Ordinal);

            var serialized = JsonSerializer.Serialize(new { profile, search }, JsonOpts);
            Assert.DoesNotContain("profile-secret-token", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-pass@example.test", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\Secret", serialized, StringComparison.OrdinalIgnoreCase);

            var viewerScoped = await client.GetPublicProfileAsync("Peer", "peer-id-1");
            Assert.True(viewerScoped.Ok);
            handler.ProfileNotFound = true;
            var denied = await client.GetPublicProfileAsync("Peer", "peer-id-1");
            Assert.False(denied.Ok);
            Assert.Equal(ExoOnlineSources.Unavailable, denied.Diagnostics.Source);

            handler.ProfileNotFound = false;
            handler.ThrowTransient = true;
            var afterAuthoritativeDeny = await client.GetPublicProfileAsync("Peer", "peer-id-1");
            Assert.False(afterAuthoritativeDeny.Ok);
            Assert.Equal(ExoOnlineSources.Unavailable, afterAuthoritativeDeny.Diagnostics.Source);

            handler.ThrowTransient = false;
            Assert.True((await client.GetPublicProfileAsync("Peer", "peer-id-1")).Ok);
            store.Save(new ExoSession
            {
                AccessToken = "viewer-b-token",
                AccountId = "viewer-b-id",
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            });
            handler.ThrowTransient = true;
            var otherViewer = await client.GetPublicProfileAsync("Peer", "peer-id-1");
            Assert.False(otherViewer.Ok);
            Assert.Equal(ExoOnlineSources.Unavailable, otherViewer.Diagnostics.Source);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Privacy_GetAndPutUseTheCompleteBoundedPolicyDto()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root);
            store.Save(Session("privacy-secret-token"));
            var handler = new PrivacyHandler();
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787");

            var current = await client.GetPrivacyAsync();
            var changed = await client.SetPrivacyAsync(new ExoProfilePrivacy
            {
                ProfileVisibility = "public",
                Searchable = true,
                RequestPolicy = "none",
                ActivityVisibility = "private",
            });

            Assert.True(current.Ok);
            Assert.Equal("friends", current.Value?.ProfileVisibility);
            Assert.False(current.Value?.Searchable);
            Assert.True(changed.Ok);
            Assert.Equal("public", changed.Value?.ProfileVisibility);
            Assert.True(changed.Value?.Searchable);
            Assert.Equal("none", changed.Value?.RequestPolicy);
            Assert.Equal("private", changed.Value?.ActivityVisibility);
            Assert.Equal(ExoOnlineSources.Live, changed.Diagnostics.Source);

            using var request = JsonDocument.Parse(handler.PutBody!);
            var body = request.RootElement;
            Assert.Equal(4, body.EnumerateObject().Count());
            Assert.Equal("public", body.GetProperty("profileVisibility").GetString());
            Assert.True(body.GetProperty("searchable").GetBoolean());
            Assert.Equal("none", body.GetProperty("requestPolicy").GetString());
            Assert.Equal("private", body.GetProperty("activityVisibility").GetString());
            Assert.False(body.TryGetProperty("updatedAt", out _));
            Assert.DoesNotContain("privacy-secret-token", JsonSerializer.Serialize(changed, JsonOpts), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task FriendRequestsAndBlocksUseImmutableUserIdsAndTypedDtos()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root);
            const string token = "social-secret-token";
            store.Save(Session(token));
            var handler = new SocialMutationHandler();
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787");

            var requests = await client.GetFriendRequestsAsync();
            var sent = await client.SendFriendRequestAsync("Peer");
            var accepted = await client.AcceptFriendRequestAsync(SocialMutationHandler.RequestId);
            var declined = await client.DeclineFriendRequestAsync(SocialMutationHandler.RequestId);
            var removed = await client.RemoveFriendAsync("peer-id-1");
            var blocks = await client.GetBlocksAsync();
            var blocked = await client.BlockAsync("peer-id-1");
            var unblocked = await client.UnblockAsync("peer-id-1");

            Assert.True(requests.Ok);
            Assert.Equal("peer-id-1", Assert.Single(requests.Value!.Incoming).User.UserId);
            Assert.Equal("peer-id-1", sent.Value?.User.UserId);
            Assert.Equal("pending", sent.Value?.Status);
            Assert.Equal("accepted", accepted.Value?.Status);
            Assert.Equal("declined", declined.Value?.Status);
            Assert.True(removed.Value?.Ok);
            Assert.Equal("peer-id-1", Assert.Single(blocks.Value!.Blocks).UserId);
            Assert.Equal("peer-id-1", blocked.Value?.UserId);
            Assert.True(unblocked.Value?.Ok);

            var sentRequest = Assert.Single(handler.Requests, request =>
                request.Method == HttpMethod.Post && request.Path == ExoIdContract.FriendRequestsPath);
            using (var document = JsonDocument.Parse(sentRequest.Body!))
                Assert.Equal("Peer", document.RootElement.GetProperty("handle").GetString());
            Assert.Contains(handler.Requests, request =>
                request.Method == HttpMethod.Delete &&
                request.Path == ExoIdContract.FriendsPath + "/peer-id-1");
            Assert.Contains(handler.Requests, request =>
                request.Method == HttpMethod.Put &&
                request.Path == ExoIdContract.BlocksPath + "/peer-id-1");
            Assert.DoesNotContain(handler.Requests, request => request.Path.Contains("/Peer", StringComparison.Ordinal));

            var serialized = JsonSerializer.Serialize(
                new { requests, sent, accepted, declined, removed, blocks, blocked, unblocked },
                JsonOpts);
            Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task StoreLinksAcquireOneShotTokensNativelyAndNeverReturnThem()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root);
            const string accountToken = "account-bearer-secret";
            const string storeToken = "epic-one-shot-secret";
            store.Save(Session(accountToken));
            var handler = new LinksHandler();
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787",
                storeTokens: new FakeStoreTokenSource(storeToken));

            var links = await client.GetLinksAsync();
            var discovery = await client.SetDiscoveryAsync(enabled: false);
            var linked = await client.LinkStoreAsync(ExoLinkedStore.Epic);
            var matched = await client.MatchStoreFriendsAsync(
                ExoLinkedStore.Epic,
                ExoStoreRelationship.Mutual,
                ["epic-peer-native-id"]);
            var unlinked = await client.UnlinkStoreAsync(ExoLinkedStore.Epic);

            Assert.True(links.Ok);
            Assert.Equal("self-steam-id", Assert.Single(links.Value!.Links).ExternalId);
            Assert.Equal("peer-user-id", Assert.Single(links.Value.Connections).UserId);
            Assert.True(discovery.Ok);
            Assert.False(discovery.Value?.Enabled);
            Assert.True(linked.Ok);
            Assert.Equal("epic", linked.Value?.Store);
            Assert.Equal("peer-user-id", Assert.Single(matched.Value!.Matches).UserId);
            Assert.True(unlinked.Value?.Ok);

            var linkRequest = Assert.Single(handler.Requests, request =>
                request.Method == HttpMethod.Post && request.Path == ExoIdContract.LinksEpicPath);
            using (var document = JsonDocument.Parse(linkRequest.Body!))
                Assert.Equal(storeToken, document.RootElement.GetProperty("accessToken").GetString());
            var matchRequest = Assert.Single(handler.Requests, request => request.Path == ExoIdContract.LinksMatchPath);
            using (var document = JsonDocument.Parse(matchRequest.Body!))
            {
                Assert.Equal("epic", document.RootElement.GetProperty("store").GetString());
                Assert.Equal("mutual", document.RootElement.GetProperty("relationship").GetString());
                Assert.Equal("epic-peer-native-id",
                    Assert.Single(document.RootElement.GetProperty("ids").EnumerateArray()).GetString());
            }

            handler.EchoExternalId = storeToken;
            var echoed = await client.LinkStoreAsync(ExoLinkedStore.Epic);
            Assert.False(echoed.Ok);
            Assert.Null(echoed.Value);

            var serialized = JsonSerializer.Serialize(new { links, discovery, linked, matched, unlinked, echoed }, JsonOpts);
            Assert.DoesNotContain(accountToken, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(storeToken, serialized, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SteamLinkUsesSystemBrowserAndExactLoopbackCallbackWithoutReturningStoreIdentity()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root);
            const string token = "steam-link-account-secret";
            store.Save(Session(token));
            var handler = new SteamLinkHandler();
            string? opened = null;
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787",
                openBrowser: url =>
                {
                    opened = url;
                    return true;
                },
                startListener: ExoLoopbackListener.Start);

            var result = await client.LinkSteamAsync();

            Assert.True(result.Ok);
            Assert.NotNull(opened);
            Assert.StartsWith("https://steamcommunity.com/openid/login", opened, StringComparison.Ordinal);
            Assert.Equal(ExoIdContract.CallbackPath, new Uri(handler.RedirectUri!).AbsolutePath);
            Assert.Equal("127.0.0.1", new Uri(handler.RedirectUri!).Host);
            Assert.Equal("peer-user-id", Assert.Single(result.Value!.Connections).UserId);
            var serialized = JsonSerializer.Serialize(result, JsonOpts);
            Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("765611", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("authorizationUrl", serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SessionsExportAndRevokeAllStayTypedAndRemoveTheProtectedSession()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root);
            const string token = "session-api-secret";
            store.Save(Session(token));
            var handler = new AccountDataHandler();
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787");

            var sessions = await client.GetSessionsAsync();
            var exported = await client.ExportAccountAsync();
            var revoked = await client.RevokeSessionAsync("session-other");
            var revokedAll = await client.RevokeAllSessionsAsync();

            Assert.True(sessions.Ok);
            Assert.Equal(2, sessions.Value?.Sessions.Count);
            Assert.True(sessions.Value?.Sessions.Single(item => item.Id == "session-current").Current);
            Assert.True(exported.Ok);
            Assert.Equal("self-immutable-id", exported.Value?.Account.Id);
            Assert.Equal("Local", exported.Value?.Profile["displayName"].GetString());
            Assert.False(exported.Value?.Profile.ContainsKey("machinePath"));
            Assert.False(exported.Value?.Preferences.ContainsKey("defaultInstallRoot"));
            Assert.True(revoked.Value?.Ok);
            Assert.True(revokedAll.Value?.Ok);
            Assert.False(File.Exists(store.Path));
            Assert.Null(store.TryLoad());

            var revoke = Assert.Single(handler.Requests, request => request.Path == ExoIdContract.SessionsRevokePath);
            using (var body = JsonDocument.Parse(revoke.Body!))
                Assert.Equal("session-other", body.RootElement.GetProperty("sessionId").GetString());
            var serialized = JsonSerializer.Serialize(new { sessions, exported, revoked, revokedAll }, JsonOpts);
            Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("accessToken", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"D:\Games", serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DeleteAccountReportsWhenTheDeadSessionBlobCannotBeRemoved()
    {
        var root = TempRoot();
        FileStream? lockStream = null;
        try
        {
            var store = SessionStore(root);
            store.Save(Session("delete-account-secret"));
            var handler = new DeleteAccountHandler(() =>
                lockStream = new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.None));
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787");

            var result = await client.DeleteAccountAsync();

            Assert.False(result.Ok);
            Assert.True(result.Value?.Ok);
            Assert.False(result.Diagnostics.SignedIn);
            Assert.Equal("SESSION_DELETE_FAILED", result.Diagnostics.Error?.Code);
            Assert.True(File.Exists(store.Path));
            Assert.DoesNotContain("delete-account-secret", JsonSerializer.Serialize(result, JsonOpts), StringComparison.Ordinal);
        }
        finally
        {
            lockStream?.Dispose();
            try { SessionStore(root).Delete(); } catch { /* test cleanup */ }
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RevokingTheCurrentSessionUsesLiveInventoryAndDeletesTheBearer()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root);
            store.Save(Session("current-session-secret"));
            using var client = new ExoOnlineClient(
                store,
                new AccountDataHandler(),
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787",
                mediaCache: new ExoProfileMediaCache(Path.Combine(root, "media-cache")));

            var result = await client.RevokeSessionAsync("session-current");

            Assert.True(result.Ok);
            Assert.False(result.Diagnostics.SignedIn);
            Assert.False(File.Exists(store.Path));
            Assert.Null(store.TryLoad());
            Assert.DoesNotContain("current-session-secret", JsonSerializer.Serialize(result, JsonOpts), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ProfileMediaKeepsNativeSourcePrivateAndFallsBackToValidatedLocalCache()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root);
            const string token = "media-account-secret";
            store.Save(Session(token));
            var bytes = new byte[136];
            new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(bytes, 0);
            var sourcePath = Path.Combine(root, "private-source-secret.png");
            File.WriteAllBytes(sourcePath, bytes);
            var handler = new MediaHandler(bytes);
            var mediaCache = new ExoProfileMediaCache(Path.Combine(root, "media-cache"));
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787",
                mediaCache: mediaCache);

            var uploaded = await client.UploadProfileMediaFileAsync("avatar", sourcePath);
            var downloaded = await client.DownloadProfileMediaAsync("self-immutable-id", uploaded.Value!);

            Assert.True(uploaded.Ok);
            Assert.Equal("avatar", uploaded.Value?.Kind);
            Assert.Equal(bytes, handler.UploadedBytes);
            Assert.Equal("image/png", handler.UploadContentType);
            Assert.True(downloaded.Ok);
            Assert.NotNull(downloaded.Value);
            Assert.StartsWith(ExoProfileMediaCache.VirtualHostOrigin + "/", downloaded.Value!.Url, StringComparison.Ordinal);
            Assert.NotNull(mediaCache.ResolvePath(downloaded.Value.FileName));
            Assert.Equal(ExoOnlineSources.Live, downloaded.Diagnostics.Source);

            handler.ThrowTransient = true;
            var cached = await client.DownloadProfileMediaAsync("self-immutable-id", uploaded.Value!);
            Assert.True(cached.Ok);
            Assert.Equal(downloaded.Value, cached.Value);
            Assert.Equal(ExoOnlineSources.Cache, cached.Diagnostics.Source);
            Assert.Null(cached.Diagnostics.SignedIn);
            Assert.True(cached.Diagnostics.Retryable);

            var deleted = await client.DeleteProfileMediaAsync("avatar");
            Assert.False(deleted.Ok); // handler is still transient; local cached media remains fail-open.
            Assert.NotNull(mediaCache.ResolvePath(downloaded.Value.FileName));

            client.ClearLocalCaches();
            Assert.Null(mediaCache.ResolvePath(downloaded.Value.FileName));
            var afterClear = await client.DownloadProfileMediaAsync("self-immutable-id", uploaded.Value!);
            Assert.False(afterClear.Ok);
            Assert.Equal(ExoOnlineSources.Unavailable, afterClear.Diagnostics.Source);

            var serialized = JsonSerializer.Serialize(new { uploaded, downloaded, cached, deleted, afterClear }, JsonOpts);
            Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(sourcePath, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-source-secret", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/v1/media/self-immutable-id", JsonSerializer.Serialize(downloaded, JsonOpts), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "media-cache"), "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PresenceRestFallbackIsAuthenticatedCachedAndMapsUnavailableToUnknown()
    {
        var root = TempRoot();
        try
        {
            var store = SessionStore(root);
            store.Save(Session("presence-rest-secret"));
            var handler = new PresenceRestHandler();
            using var client = new ExoOnlineClient(
                store,
                handler,
                new ExoOnlineCache(Path.Combine(root, "online-cache")),
                origin: "http://127.0.0.1:8787");

            var live = await client.GetPresenceAsync();

            Assert.True(live.Ok);
            Assert.True(live.Value?.Unavailable);
            Assert.Collection(
                live.Value!.Friends,
                playing => Assert.Equal("ingame", playing.Status),
                unavailable =>
                {
                    Assert.Equal("unknown", unavailable.Status);
                    Assert.False(unavailable.Available);
                    Assert.Null(unavailable.GameId);
                });
            Assert.Equal(ExoIdContract.PresencePath, handler.LastPath);
            Assert.Equal("Bearer", handler.AuthorizationScheme);

            handler.ThrowTransient = true;
            var cached = await client.GetPresenceAsync();
            Assert.True(cached.Ok);
            Assert.True(cached.Value?.Unavailable);
            Assert.Equal(ExoOnlineSources.Cache, cached.Diagnostics.Source);
            Assert.Null(cached.Diagnostics.SignedIn);
            Assert.DoesNotContain("presence-rest-secret", JsonSerializer.Serialize(cached, JsonOpts), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-online-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ExoSessionStore SessionStore(string root) =>
        new(Path.Combine(root, ExoSessionStore.FileName));

    private static ExoSession Session(string token) => new()
    {
        AccessToken = token,
        AccountId = "self-immutable-id",
        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
    };

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* test cleanup */ }
    }

    private sealed class OnlineHandler : HttpMessageHandler
    {
        public bool ThrowTransient { get; set; }
        public bool Unauthorized { get; init; }
        public bool ProfileNotFound { get; set; }
        public string FriendsJson { get; init; } = """{"friends":[],"nextCursor":null}""";
        public string ProfileJson { get; init; } =
            """{"userId":"peer-id","handle":{"display":"Peer","normalized":"peer"},"profile":{}}""";
        public string SearchJson { get; init; } = """{"profiles":[],"nextCursor":null}""";
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? LastPath { get; private set; }
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            if (request.RequestUri is not null)
                RequestUris.Add(request.RequestUri);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            if (ThrowTransient)
                throw new HttpRequestException("fixture transport failure");
            if (Unauthorized)
                return Task.FromResult(Json(HttpStatusCode.Unauthorized,
                    """{"error":{"code":"UNAUTHENTICATED","message":"Sign in required."}}"""));
            if (LastPath == ExoIdContract.FriendsPath)
                return Task.FromResult(Json(HttpStatusCode.OK, FriendsJson));
            if (LastPath == ExoIdContract.HealthPath)
                return Task.FromResult(Json(HttpStatusCode.OK,
                    """{"ok":true,"service":"exo-id","capabilities":{"providers":{"google":false,"email":false,"password":true},"profiles":true,"friends":true,"media":true,"presence":true}}"""));
            if (LastPath == ExoIdContract.ProfilesSearchPath)
                return Task.FromResult(Json(HttpStatusCode.OK, SearchJson));
            if (LastPath?.StartsWith(ExoIdContract.ProfilesPrefix + "/", StringComparison.Ordinal) == true)
                return Task.FromResult(ProfileNotFound
                    ? Json(HttpStatusCode.NotFound,
                        """{"error":{"code":"NOT_FOUND","message":"Not found."}}""")
                    : Json(HttpStatusCode.OK, ProfileJson));
            return Task.FromResult(Json(HttpStatusCode.NotFound,
                """{"error":{"code":"NOT_FOUND","message":"Not found."}}"""));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class PrivacyHandler : HttpMessageHandler
    {
        public string? PutBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ExoIdContract.ProfilePrivacyPath, request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK,
                    """{"privacy":{"profileVisibility":"friends","searchable":false,"requestPolicy":"anyone","activityVisibility":"friends","updatedAt":null}}""");
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            PutBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json(HttpStatusCode.OK,
                """{"privacy":{"profileVisibility":"public","searchable":true,"requestPolicy":"none","activityVisibility":"private","updatedAt":"2026-08-19T20:45:00.000Z"}}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class SocialMutationHandler : HttpMessageHandler
    {
        public const string RequestId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, path, body));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);

            if (request.Method == HttpMethod.Get && path == ExoIdContract.FriendRequestsPath)
            {
                return Json(HttpStatusCode.OK,
                    "{\"incoming\":[" + Request("incoming", "pending") +
                    "],\"outgoing\":[],\"nextIncomingCursor\":null,\"nextOutgoingCursor\":null}");
            }
            if (request.Method == HttpMethod.Post && path == ExoIdContract.FriendRequestsPath)
                return Json(HttpStatusCode.OK, "{\"request\":" + Request("outgoing", "pending") + "}");
            if (request.Method == HttpMethod.Post && path.EndsWith("/accept", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"request\":" + Request("incoming", "accepted") + "}");
            if (request.Method == HttpMethod.Post && path.EndsWith("/decline", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"request\":" + Request("incoming", "declined") + "}");
            if (request.Method == HttpMethod.Delete && path.StartsWith(ExoIdContract.FriendsPath + "/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"ok":true}""");
            if (request.Method == HttpMethod.Get && path == ExoIdContract.BlocksPath)
                return Json(HttpStatusCode.OK, "{\"blocks\":[" + Block() + "],\"nextCursor\":null}");
            if (request.Method == HttpMethod.Put && path.StartsWith(ExoIdContract.BlocksPath + "/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"block\":" + Block() + "}");
            if (request.Method == HttpMethod.Delete && path.StartsWith(ExoIdContract.BlocksPath + "/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"ok":true}""");
            return Json(HttpStatusCode.NotFound,
                """{"error":{"code":"NOT_FOUND","message":"Not found."}}""");
        }

        private static string Request(string direction, string status) =>
            "{\"id\":\"" + RequestId + "\",\"direction\":\"" + direction +
            "\",\"user\":{\"userId\":\"peer-id-1\",\"handle\":{\"display\":\"Peer\",\"normalized\":\"peer\"}}," +
            "\"status\":\"" + status +
            "\",\"createdAt\":\"2026-08-19T20:00:00.000Z\",\"updatedAt\":\"2026-08-19T20:01:00.000Z\"}";

        private static string Block() =>
            """{"userId":"peer-id-1","handle":{"display":"Peer","normalized":"peer"},"createdAt":"2026-08-19T20:02:00.000Z"}""";

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        public sealed record CapturedRequest(HttpMethod Method, string Path, string? Body);
    }

    private sealed class FakeStoreTokenSource(string token) : IExoStoreTokenSource
    {
        public ValueTask<string?> GetAccessTokenAsync(
            ExoLinkedStore store,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(store is ExoLinkedStore.Epic or ExoLinkedStore.Gog);
            return ValueTask.FromResult<string?>(token);
        }
    }

    private sealed class LinksHandler : HttpMessageHandler
    {
        public List<SocialMutationHandler.CapturedRequest> Requests { get; } = [];
        public string? EchoExternalId { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SocialMutationHandler.CapturedRequest(request.Method, path, body));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);

            if (request.Method == HttpMethod.Get && path == ExoIdContract.LinksPath)
            {
                return Json(HttpStatusCode.OK,
                    """{"discovery":{"enabled":true,"updatedAt":null},"links":[{"store":"steam","externalId":"self-steam-id","verified":true,"verifiedAt":"2026-08-19T20:00:00.000Z"}],"connections":[{"userId":"peer-user-id","handle":{"display":"Peer","normalized":"peer"},"store":"steam","createdAt":"2026-08-19T20:01:00.000Z"}]}""");
            }
            if (request.Method == HttpMethod.Patch && path == ExoIdContract.LinksDiscoveryPath)
                return Json(HttpStatusCode.OK,
                    """{"discovery":{"enabled":false,"updatedAt":"2026-08-19T20:02:00.000Z"}}""");
            if (request.Method == HttpMethod.Post && path == ExoIdContract.LinksEpicPath)
                return Json(HttpStatusCode.OK,
                    "{\"link\":{\"store\":\"epic\",\"externalId\":\"" +
                    (EchoExternalId ?? "self-epic-id") +
                    "\",\"verified\":true,\"verifiedAt\":\"2026-08-19T20:03:00.000Z\"}}");
            if (request.Method == HttpMethod.Post && path == ExoIdContract.LinksMatchPath)
                return Json(HttpStatusCode.OK,
                    """{"matches":[{"userId":"peer-user-id","handle":{"display":"Peer","normalized":"peer"},"store":"epic","createdAt":"2026-08-19T20:04:00.000Z"}]}""");
            if (request.Method == HttpMethod.Delete && path == ExoIdContract.LinksEpicPath)
                return Json(HttpStatusCode.OK, """{"ok":true}""");
            return Json(HttpStatusCode.NotFound,
                """{"error":{"code":"NOT_FOUND","message":"Not found."}}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class SteamLinkHandler : HttpMessageHandler
    {
        public string? RedirectUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Post && path == ExoIdContract.LinksSteamStartPath)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                RedirectUri = body.RootElement.GetProperty("redirectUri").GetString();
                var state = body.RootElement.GetProperty("state").GetString();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(60);
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                    await http.GetAsync(
                        RedirectUri + "?state=" + Uri.EscapeDataString(state!) + "&link=ok",
                        CancellationToken.None);
                });
                return Json(HttpStatusCode.OK,
                    """{"linkId":"abcdef0123456789","expiresIn":600,"authorizationUrl":"https://steamcommunity.com/openid/login?openid.ns=test"}""");
            }
            if (request.Method == HttpMethod.Get && path == ExoIdContract.LinksPath)
            {
                return Json(HttpStatusCode.OK,
                    """{"discovery":{"enabled":true,"updatedAt":null},"links":[],"connections":[{"userId":"peer-user-id","handle":{"display":"Peer","normalized":"peer"},"store":"steam","createdAt":"2026-08-19T20:01:00.000Z"}]}""");
            }
            return Json(HttpStatusCode.NotFound,
                """{"error":{"code":"NOT_FOUND","message":"Not found."}}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class AccountDataHandler : HttpMessageHandler
    {
        public List<SocialMutationHandler.CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SocialMutationHandler.CapturedRequest(request.Method, path, body));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Get && path == ExoIdContract.SessionsPath)
            {
                return Json(HttpStatusCode.OK,
                    """{"sessions":[{"id":"session-current","current":true,"createdAt":"2026-08-19T19:00:00.000Z","updatedAt":"2026-08-19T20:00:00.000Z","expiresAt":"2026-08-26T20:00:00.000Z","userAgent":"exo-launcher"},{"id":"session-other","current":false,"createdAt":"2026-08-18T19:00:00.000Z","updatedAt":"2026-08-18T20:00:00.000Z","expiresAt":"2026-08-25T20:00:00.000Z","userAgent":null}]}""");
            }
            if (request.Method == HttpMethod.Get && path == ExoIdContract.MeExportPath)
            {
                return Json(HttpStatusCode.OK,
                    """{"exportedAt":"2026-08-19T20:00:00.000Z","account":{"id":"self-immutable-id","name":"Local","email":"local@example.test","emailVerified":true,"createdAt":"2026-08-18T20:00:00.000Z","updatedAt":"2026-08-19T20:00:00.000Z","providers":["google"]},"handle":{"display":"Local","normalized":"local"},"profile":{"displayName":"Local","machinePath":"D:\\Games"},"preferences":{"sortMode":"recent","defaultInstallRoot":"D:\\Games"},"sessions":[],"discovery":{"enabled":true,"updatedAt":null},"links":[],"connections":[],"accessToken":"malicious-server-token"}""");
            }
            if (request.Method == HttpMethod.Post &&
                (path == ExoIdContract.SessionsRevokePath || path == ExoIdContract.SessionsRevokeAllPath))
                return Json(HttpStatusCode.OK, """{"ok":true}""");
            return Json(HttpStatusCode.NotFound,
                """{"error":{"code":"NOT_FOUND","message":"Not found."}}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class DeleteAccountHandler(Action beforeResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal(ExoIdContract.MePath, request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            beforeResponse();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"ok":true,"handleHeldUntil":"2027-08-19T20:00:00.000Z"}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class MediaHandler(byte[] image) : HttpMessageHandler
    {
        private readonly string _version = new('a', 64);
        private readonly string _sha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant();

        public bool ThrowTransient { get; set; }
        public byte[]? UploadedBytes { get; private set; }
        public string? UploadContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (ThrowTransient)
                throw new HttpRequestException("fixture media transport failure");
            var path = request.RequestUri?.AbsolutePath ?? "";
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Put && path == ExoIdContract.ProfileMediaPath("avatar"))
            {
                UploadedBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                UploadContentType = request.Content.Headers.ContentType?.MediaType;
                return Json(HttpStatusCode.OK,
                    "{\"media\":{\"kind\":\"avatar\",\"version\":\"" + _version +
                    "\",\"url\":\"/v1/media/self-immutable-id/avatar/" + _version +
                    "\",\"contentType\":\"image/png\",\"size\":" + image.Length +
                    ",\"width\":256,\"height\":256,\"sha256\":\"" + _sha256 +
                    "\",\"updatedAt\":\"2026-08-19T21:00:00.000Z\"}}");
            }
            if (request.Method == HttpMethod.Get &&
                path == "/v1/media/self-immutable-id/avatar/" + _version)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(image),
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                response.Content.Headers.ContentLength = image.Length;
                return response;
            }
            if (request.Method == HttpMethod.Delete && path == ExoIdContract.ProfileMediaPath("avatar"))
                return Json(HttpStatusCode.OK, """{"ok":true}""");
            return Json(HttpStatusCode.NotFound,
                """{"error":{"code":"NOT_FOUND","message":"Not found."}}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class PresenceRestHandler : HttpMessageHandler
    {
        public bool ThrowTransient { get; set; }
        public string? LastPath { get; private set; }
        public string? AuthorizationScheme { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            if (ThrowTransient)
                throw new HttpRequestException("fixture presence REST failure");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"friends":[{"userId":"peer-playing","status":"in_game","gameId":"steam:10","gameTitle":"Game","lastSeen":"2026-08-19T21:00:00.000Z","availability":"available"},{"userId":"peer-unavailable","status":"online","gameId":"private","gameTitle":"Private","lastSeen":null,"availability":"unavailable"}],"unavailable":false}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
