using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// Optional online identity/social HTTP module. It owns authentication,
/// bounded parsing, last-good cache fallback, and stable diagnostics so its
/// callers never handle bearer tokens or raw server bodies. It is never used
/// by library, install, update, or launch paths.
/// </summary>
internal sealed class ExoOnlineClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex StableCode = new(
        "^[A-Z][A-Z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> PublicProfileKeys = new(StringComparer.Ordinal)
    {
        "displayName", "pronouns", "statusText", "bio", "accent", "layout",
        "bannerHeight", "showcaseStyle", "sections", "hiddenSections",
        "showcase", "avatarGameId", "bannerGameId",
    };

    private readonly ExoSessionStore _store;
    private readonly ExoOnlineCache _cache;
    private readonly HttpClient _http;
    private readonly string? _origin;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly IExoStoreTokenSource? _storeTokens;
    private readonly ExoProfileMediaCache _mediaCache;
    private readonly ExoIdentityLifecycle _lifecycle;
    private readonly Func<string, bool> _openBrowser;
    private readonly Func<ExoLoopbackListener> _startListener;
    private readonly SemaphoreSlim _interactiveGate = new(1, 1);
    private readonly object _healthGate = new();
    private ExoOnlineHealth? _lastHealth;
    private DateTimeOffset? _lastHealthSync;
    private bool _disposed;

    public ExoOnlineClient()
        : this(
            new ExoSessionStore(),
            CreateHandler(),
            new ExoOnlineCache(),
            origin: null)
    {
    }

    public ExoOnlineClient(IExoStoreTokenSource storeTokens)
        : this(
            new ExoSessionStore(),
            CreateHandler(),
            new ExoOnlineCache(),
            origin: null,
            storeTokens: storeTokens)
    {
    }

    internal ExoOnlineClient(
        ExoSessionStore store,
        HttpMessageHandler handler,
        ExoOnlineCache cache,
        string? origin,
        Func<DateTimeOffset>? utcNow = null,
        IExoStoreTokenSource? storeTokens = null,
        Func<string, bool>? openBrowser = null,
        Func<ExoLoopbackListener>? startListener = null,
        ExoProfileMediaCache? mediaCache = null,
        ExoIdentityLifecycle? lifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(cache);
        _store = store;
        _cache = cache;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _storeTokens = storeTokens;
        _mediaCache = mediaCache ?? new ExoProfileMediaCache();
        _lifecycle = lifecycle ?? new ExoIdentityLifecycle(store, cache, _mediaCache);
        _openBrowser = openBrowser ?? ExoAccountService.OpenSystemBrowser;
        _startListener = startListener ?? ExoLoopbackListener.Start;
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = ExoIdContract.HttpTimeout,
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ExoLauncher");
        try { _origin = ExoIdContract.ResolveOrigin(origin); }
        catch { _origin = null; }
    }

    public Task<ExoOnlineResult<ExoFriendPage>> GetFriendsAsync(
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50 || !IsSafeCursor(cursor))
            return Task.FromResult(Invalid<ExoFriendPage>("INVALID_REQUEST", "The friends page request is invalid."));
        var path = Query(ExoIdContract.FriendsPath, ("limit", limit.ToString()), ("cursor", cursor));
        var cacheKey = $"friends:{limit}:{cursor ?? ""}";
        return GetAuthenticatedAsync<ExoFriendPage>(
            path,
            cacheKey,
            page => page.Friends.Count <= limit && page.Friends.All(ValidateFriend) && IsSafeCursor(page.NextCursor),
            cancellationToken);
    }

    public async Task<ExoOnlineResult<ExoOnlineHealth>> GetHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync<ExoOnlineHealth>(
                ExoIdContract.HealthPath,
                cacheKey: "health:uncached",
                requireAuthentication: false,
                cacheBySession: false,
                fallbackCacheScope: null,
                cacheScopeForLive: null,
                validate: static health =>
                    health.Ok && string.Equals(health.Service, "exo-id", StringComparison.Ordinal),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok && result.Value is not null)
        {
            lock (_healthGate)
            {
                _lastHealth = result.Value;
                _lastHealthSync = result.Diagnostics.LastSuccessfulSync;
            }
            return result;
        }

        if (result.Diagnostics.Retryable)
        {
            lock (_healthGate)
            {
                if (_lastHealth is not null)
                {
                    return new ExoOnlineResult<ExoOnlineHealth>(
                        true,
                        _lastHealth,
                        result.Diagnostics with
                        {
                            Source = ExoOnlineSources.Cache,
                            LastSuccessfulSync = _lastHealthSync,
                        });
                }
            }
        }
        return result;
    }

    public Task<ExoOnlineResult<ExoPublicProfile>> GetPublicProfileAsync(
        string? handle,
        string? knownUserId = null,
        CancellationToken cancellationToken = default)
    {
        var clean = (handle ?? string.Empty).Trim();
        if (!IsHandleShape(clean) || knownUserId is not null && !IsSafeId(knownUserId))
            return Task.FromResult(Invalid<ExoPublicProfile>("INVALID_REQUEST", "That profile handle is invalid."));
        var path = ExoIdContract.ProfilesPrefix + "/" + Uri.EscapeDataString(clean);
        var canCache = knownUserId is not null;
        return GetJsonAsync<ExoPublicProfile>(
            path,
            cacheKey: canCache ? "public-profile:" + knownUserId : "public-profile:uncached",
            requireAuthentication: false,
            cacheBySession: canCache,
            fallbackCacheScope: null,
            cacheScopeForLive: null,
            validate: profile =>
                SanitizePublicProfile(profile) &&
                (knownUserId is null || string.Equals(profile.UserId, knownUserId, StringComparison.Ordinal)),
            cancellationToken);
    }

    public Task<ExoOnlineResult<ExoPublicProfilePage>> SearchProfilesAsync(
        string? query,
        int limit = 20,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var clean = (query ?? string.Empty).Trim();
        if (clean.Length is < 1 or > 24 ||
            !clean.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') ||
            limit is < 1 or > 50 ||
            !IsSafeCursor(cursor))
        {
            return Task.FromResult(Invalid<ExoPublicProfilePage>(
                "INVALID_REQUEST",
                "The profile search request is invalid."));
        }

        var path = Query(
            ExoIdContract.ProfilesSearchPath,
            ("q", clean),
            ("limit", limit.ToString()),
            ("cursor", cursor));
        return GetJsonAsync<ExoPublicProfilePage>(
            path,
            cacheKey: $"profile-search:{clean.ToLowerInvariant()}:{limit}:{cursor ?? ""}",
            requireAuthentication: false,
            cacheBySession: true,
            fallbackCacheScope: null,
            cacheScopeForLive: null,
            validate: page =>
                page.Profiles.Count <= limit &&
                IsSafeCursor(page.NextCursor) &&
                page.Profiles.All(SanitizePublicProfile),
            cancellationToken);
    }

    public Task<ExoOnlineResult<ExoAdminBadgeState>> GetManagedBadgesAsync(
        string? handle,
        CancellationToken cancellationToken = default)
    {
        var clean = (handle ?? string.Empty).Trim();
        if (!IsHandleShape(clean))
            return Task.FromResult(Invalid<ExoAdminBadgeState>(
                "INVALID_REQUEST", "That profile handle is invalid."));
        var path = Query(ExoIdContract.AdminBadgesPath, ("handle", clean));
        return GetJsonAsync<ExoAdminBadgeState>(
            path,
            cacheKey: "admin-badges:uncached",
            requireAuthentication: true,
            cacheBySession: false,
            fallbackCacheScope: null,
            cacheScopeForLive: null,
            validate: state => ValidateAdminBadgeState(state, clean),
            cancellationToken);
    }

    public Task<ExoOnlineResult<ExoAdminBadgeState>> GrantManagedBadgeAsync(
        string? handle,
        string? badge,
        CancellationToken cancellationToken = default) =>
        MutateManagedBadgeAsync(HttpMethod.Post, handle, badge, cancellationToken);

    public Task<ExoOnlineResult<ExoAdminBadgeState>> RevokeManagedBadgeAsync(
        string? handle,
        string? badge,
        CancellationToken cancellationToken = default) =>
        MutateManagedBadgeAsync(HttpMethod.Delete, handle, badge, cancellationToken);

    public async Task<ExoOnlineResult<ExoProfilePrivacy>> GetPrivacyAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await GetAuthenticatedAsync<ExoProfilePrivacyEnvelope>(
                ExoIdContract.ProfilePrivacyPath,
                cacheKey: "privacy",
                static envelope => IsValidPrivacy(envelope.Privacy),
                cancellationToken)
            .ConfigureAwait(false);
        return Map(result, static envelope => envelope.Privacy);
    }

    public async Task<ExoOnlineResult<ExoProfilePrivacy>> SetPrivacyAsync(
        ExoProfilePrivacy? privacy,
        CancellationToken cancellationToken = default)
    {
        if (privacy is null || !IsValidPrivacy(privacy))
            return Invalid<ExoProfilePrivacy>("INVALID_REQUEST", "The privacy settings are invalid.");
        var result = await SendAuthenticatedJsonAsync<ExoProfilePrivacyEnvelope>(
                HttpMethod.Put,
                ExoIdContract.ProfilePrivacyPath,
                new
                {
                    profileVisibility = privacy.ProfileVisibility,
                    searchable = privacy.Searchable,
                    requestPolicy = privacy.RequestPolicy,
                    activityVisibility = privacy.ActivityVisibility,
                },
                static envelope => IsValidPrivacy(envelope.Privacy),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok && result.Value is not null)
        {
            var session = _store.TryLoad();
            if (IsSafeId(session?.AccountId) && result.Diagnostics.LastSuccessfulSync is DateTimeOffset stamp)
                _cache.Write(session!.AccountId!, "privacy", result.Value, stamp);
        }
        return Map(result, static envelope => envelope.Privacy);
    }

    public Task<ExoOnlineResult<ExoFriendRequestPage>> GetFriendRequestsAsync(
        int limit = 20,
        string? incomingCursor = null,
        string? outgoingCursor = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50 || !IsSafeCursor(incomingCursor) || !IsSafeCursor(outgoingCursor))
        {
            return Task.FromResult(Invalid<ExoFriendRequestPage>(
                "INVALID_REQUEST",
                "The friend request page is invalid."));
        }
        var path = Query(
            ExoIdContract.FriendRequestsPath,
            ("limit", limit.ToString()),
            ("incomingCursor", incomingCursor),
            ("outgoingCursor", outgoingCursor));
        return GetAuthenticatedAsync<ExoFriendRequestPage>(
            path,
            $"friend-requests:{limit}:{incomingCursor ?? ""}:{outgoingCursor ?? ""}",
            page => ValidateFriendRequestPage(page, limit),
            cancellationToken);
    }

    public async Task<ExoOnlineResult<ExoFriendRequest>> SendFriendRequestAsync(
        string? handle,
        CancellationToken cancellationToken = default)
    {
        var clean = (handle ?? string.Empty).Trim();
        if (!IsHandleShape(clean))
            return Invalid<ExoFriendRequest>("INVALID_REQUEST", "That profile handle is invalid.");
        var result = await SendAuthenticatedJsonAsync<ExoFriendRequestEnvelope>(
                HttpMethod.Post,
                ExoIdContract.FriendRequestsPath,
                new { handle = clean },
                static envelope => ValidateFriendRequest(envelope.Request),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok) InvalidateSocialCache();
        return Map(result, static envelope => envelope.Request);
    }

    public Task<ExoOnlineResult<ExoFriendRequest>> AcceptFriendRequestAsync(
        string? requestId,
        CancellationToken cancellationToken = default) =>
        UpdateFriendRequestAsync(requestId, "accept", cancellationToken);

    public Task<ExoOnlineResult<ExoFriendRequest>> DeclineFriendRequestAsync(
        string? requestId,
        CancellationToken cancellationToken = default) =>
        UpdateFriendRequestAsync(requestId, "decline", cancellationToken);

    public async Task<ExoOnlineResult<ExoMutationAck>> RemoveFriendAsync(
        string? userId,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeId(userId))
            return Invalid<ExoMutationAck>("INVALID_REQUEST", "That user id is invalid.");
        var result = await SendAuthenticatedJsonAsync<ExoMutationAck>(
                HttpMethod.Delete,
                ExoIdContract.FriendsPath + "/" + Uri.EscapeDataString(userId!),
                body: null,
                static ack => ack.Ok,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok)
        {
            InvalidateSocialCache();
            InvalidateViewerProfileCache(userId!);
        }
        return result;
    }

    public Task<ExoOnlineResult<ExoBlockPage>> GetBlocksAsync(
        int limit = 20,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50 || !IsSafeCursor(cursor))
            return Task.FromResult(Invalid<ExoBlockPage>("INVALID_REQUEST", "The block page is invalid."));
        var path = Query(ExoIdContract.BlocksPath, ("limit", limit.ToString()), ("cursor", cursor));
        return GetAuthenticatedAsync<ExoBlockPage>(
            path,
            $"blocks:{limit}:{cursor ?? ""}",
            page => page.Blocks.Count <= limit && IsSafeCursor(page.NextCursor) && page.Blocks.All(ValidateBlock),
            cancellationToken);
    }

    public async Task<ExoOnlineResult<ExoBlock>> BlockAsync(
        string? userId,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeId(userId))
            return Invalid<ExoBlock>("INVALID_REQUEST", "That user id is invalid.");
        var result = await SendAuthenticatedJsonAsync<ExoBlockEnvelope>(
                HttpMethod.Put,
                ExoIdContract.BlocksPath + "/" + Uri.EscapeDataString(userId!),
                body: null,
                static envelope => ValidateBlock(envelope.Block),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok)
        {
            InvalidateSocialCache();
            InvalidateViewerProfileCache(userId!);
        }
        return Map(result, static envelope => envelope.Block);
    }

    public async Task<ExoOnlineResult<ExoMutationAck>> UnblockAsync(
        string? userId,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeId(userId))
            return Invalid<ExoMutationAck>("INVALID_REQUEST", "That user id is invalid.");
        var result = await SendAuthenticatedJsonAsync<ExoMutationAck>(
                HttpMethod.Delete,
                ExoIdContract.BlocksPath + "/" + Uri.EscapeDataString(userId!),
                body: null,
                static ack => ack.Ok,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok)
        {
            InvalidateSocialCache();
            InvalidateViewerProfileCache(userId!);
        }
        return result;
    }

    public Task<ExoOnlineResult<ExoLinkState>> GetLinksAsync(
        CancellationToken cancellationToken = default) =>
        GetAuthenticatedAsync<ExoLinkState>(
            ExoIdContract.LinksPath,
            cacheKey: "links",
            ValidateLinkState,
            cancellationToken);

    public async Task<ExoOnlineResult<ExoDiscovery>> SetDiscoveryAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAuthenticatedJsonAsync<ExoDiscoveryEnvelope>(
                HttpMethod.Patch,
                ExoIdContract.LinksDiscoveryPath,
                new { enabled },
                static envelope => envelope.Discovery.UpdatedAt is not null,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok) InvalidateLinkCache();
        return Map(result, static envelope => envelope.Discovery);
    }

    public async Task<ExoOnlineResult<ExoVerifiedStoreLink>> LinkStoreAsync(
        ExoLinkedStore store,
        CancellationToken cancellationToken = default)
    {
        if (!IsKnownStore(store))
            return Invalid<ExoVerifiedStoreLink>("INVALID_REQUEST", "That store is not valid.");
        if (store == ExoLinkedStore.Steam)
        {
            return Invalid<ExoVerifiedStoreLink>(
                "INVALID_REQUEST",
                "Steam linking uses the system-browser flow.");
        }
        if (_storeTokens is null)
        {
            return Invalid<ExoVerifiedStoreLink>(
                "LINK_TOKEN_UNAVAILABLE",
                "The store session is not available on this PC.");
        }

        string? accessToken;
        try { accessToken = await _storeTokens.GetAccessTokenAsync(store, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Unavailable<ExoVerifiedStoreLink>(
                _store.TryLoad() is null ? false : null,
                "CANCELLED",
                "The online request was cancelled.",
                retryable: true);
        }
        catch
        {
            accessToken = null;
        }
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 8192)
        {
            return Invalid<ExoVerifiedStoreLink>(
                "LINK_TOKEN_UNAVAILABLE",
                "The store session is not available on this PC.");
        }

        var result = await SendAuthenticatedJsonAsync<ExoStoreLinkEnvelope>(
                HttpMethod.Post,
                StorePath(store),
                new { accessToken },
                envelope =>
                    ValidateStoreLink(envelope.Link, StoreName(store)) &&
                    !ContainsSecret(envelope.Link.ExternalId, accessToken),
                cancellationToken)
            .ConfigureAwait(false);
        accessToken = null;
        if (result.Ok) InvalidateLinkCache();
        return Map(result, static envelope => envelope.Link);
    }

    public async Task<ExoOnlineResult<ExoMutationAck>> UnlinkStoreAsync(
        ExoLinkedStore store,
        CancellationToken cancellationToken = default)
    {
        if (!IsKnownStore(store))
            return Invalid<ExoMutationAck>("INVALID_REQUEST", "That store is not valid.");
        var result = await SendAuthenticatedJsonAsync<ExoMutationAck>(
                HttpMethod.Delete,
                StorePath(store),
                body: null,
                static ack => ack.Ok,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok)
        {
            InvalidateLinkCache();
            InvalidateSocialCache();
        }
        return result;
    }

    public async Task<ExoOnlineResult<ExoMatchEnvelope>> MatchStoreFriendsAsync(
        ExoLinkedStore store,
        ExoStoreRelationship relationship,
        IReadOnlyCollection<string>? externalIds,
        CancellationToken cancellationToken = default)
    {
        if (!IsKnownStore(store) ||
            relationship is not (ExoStoreRelationship.Mutual or ExoStoreRelationship.OneSided))
        {
            return Invalid<ExoMatchEnvelope>("INVALID_REQUEST", "The store match request is invalid.");
        }
        if (externalIds is null || externalIds.Count > 200 ||
            externalIds.Any(id => !IsSafeExternalId(id)))
        {
            return Invalid<ExoMatchEnvelope>(
                "MATCH_TOO_LARGE",
                "Send at most 200 valid store friend ids at a time.");
        }
        var ids = externalIds.Distinct(StringComparer.Ordinal).ToArray();
        var result = await SendAuthenticatedJsonAsync<ExoMatchEnvelope>(
                HttpMethod.Post,
                ExoIdContract.LinksMatchPath,
                new
                {
                    store = StoreName(store),
                    relationship = relationship == ExoStoreRelationship.Mutual ? "mutual" : "onesided",
                    ids,
                },
                envelope =>
                    envelope.Matches.Count <= 200 &&
                    envelope.Matches.All(connection => ValidateConnection(connection, StoreName(store))) &&
                    envelope.Matches.Select(connection => connection.UserId)
                        .Distinct(StringComparer.Ordinal).Count() == envelope.Matches.Count,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok)
        {
            InvalidateLinkCache();
            InvalidateSocialCache();
        }
        return result;
    }

    public async Task<ExoOnlineResult<ExoLinkState>> LinkSteamAsync(
        CancellationToken cancellationToken = default)
    {
        try { await _interactiveGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Unavailable<ExoLinkState>(
                _store.TryLoad() is null ? false : null,
                "CANCELLED",
                "Steam account linking was cancelled.",
                retryable: false);
        }
        ExoLoopbackListener? listener = null;
        try
        {
            if (_disposed)
                return Unavailable<ExoLinkState>(null, "DISPOSED", "Online services are not available.", false);
            if (string.IsNullOrEmpty(_origin))
            {
                return Unavailable<ExoLinkState>(
                    _store.TryLoad() is null ? false : true,
                    "NOT_CONFIGURED",
                    "Online services are not configured.",
                    false);
            }

            try { listener = _startListener(); }
            catch
            {
                return Unavailable<ExoLinkState>(
                    _store.TryLoad() is null ? false : true,
                    "LOOPBACK_UNAVAILABLE",
                    "Steam account linking could not start.",
                    true);
            }
            var state = ExoPkce.CreateState();
            var start = await SendAuthenticatedJsonAsync<ExoSteamLinkStart>(
                    HttpMethod.Post,
                    ExoIdContract.LinksSteamStartPath,
                    new { redirectUri = listener.RedirectUriString, state },
                    static value =>
                        value.ExpiresIn is > 0 and <= 600 &&
                        value.LinkId.Length is >= 1 and <= 128 &&
                        value.LinkId.All(ch => char.IsAsciiLetterOrDigit(ch)) &&
                        ExoIdContract.IsTrustedSteamAuthorizationUrl(value.AuthorizationUrl),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!start.Ok || start.Value is null)
                return FailureAs<ExoSteamLinkStart, ExoLinkState>(start);
            if (!_openBrowser(start.Value.AuthorizationUrl))
            {
                return Unavailable<ExoLinkState>(
                    true,
                    "BROWSER_UNAVAILABLE",
                    "Could not open the system browser.",
                    false);
            }

            var callback = await listener.WaitForLinkCallbackAsync(
                    state,
                    ExoIdContract.PendingLoginLifetime,
                    cancellationToken)
                .ConfigureAwait(false);
            listener = null;
            if (!callback.Ok)
            {
                var code = StableCode.IsMatch(callback.Error ?? "")
                    ? callback.Error!
                    : callback.StateMismatch ? "STATE_MISMATCH" : "LINK_INCOMPLETE";
                return Unavailable<ExoLinkState>(
                    _store.TryLoad() is null ? false : true,
                    code,
                    string.IsNullOrWhiteSpace(callback.Message)
                        ? "Steam account linking did not complete."
                        : callback.Message,
                    code is "LINK_VERIFY_FAILED" or "INTERNAL");
            }

            InvalidateLinkCache();
            return await GetLinksAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { listener?.Stop(); } catch { /* loopback cleanup */ }
            _interactiveGate.Release();
        }
    }

    public Task<ExoOnlineResult<ExoSessionPage>> GetSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var secret = _store.TryLoad()?.AccessToken;
        return GetAuthenticatedAsync<ExoSessionPage>(
            ExoIdContract.SessionsPath,
            cacheKey: "sessions",
            page => SanitizeSessions(page, secret),
            cancellationToken);
    }

    public async Task<ExoOnlineResult<ExoMutationAck>> RevokeSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeOpaqueId(sessionId, 256))
            return Invalid<ExoMutationAck>("INVALID_REQUEST", "That session id is invalid.");
        var inventory = await GetSessionsAsync(cancellationToken).ConfigureAwait(false);
        if (!inventory.Ok || inventory.Value is null ||
            inventory.Diagnostics.Source != ExoOnlineSources.Live)
            return FailureAs<ExoSessionPage, ExoMutationAck>(inventory);
        var target = inventory.Value.Sessions.SingleOrDefault(
            session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));
        if (target is null)
            return Invalid<ExoMutationAck>("NOT_FOUND", "That session is already gone.");
        var result = await SendAuthenticatedJsonAsync<ExoMutationAck>(
                HttpMethod.Post,
                ExoIdContract.SessionsRevokePath,
                new { sessionId },
                static ack => ack.Ok,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok && target.Current)
        {
            var ended = await _lifecycle.EndSessionAsync(ExoSessionEndReason.CurrentSessionRevoked)
                .ConfigureAwait(false);
            return ended.SessionDeleted ? SignedOutAfterMutation(result) : SessionDeletionFailure(result);
        }
        if (result.Ok) InvalidateSessionCache();
        return result;
    }

    public async Task<ExoOnlineResult<ExoMutationAck>> RevokeAllSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAuthenticatedJsonAsync<ExoMutationAck>(
                HttpMethod.Post,
                ExoIdContract.SessionsRevokeAllPath,
                body: null,
                static ack => ack.Ok,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Ok)
            return result;
        var ended = await _lifecycle.EndSessionAsync(ExoSessionEndReason.AllSessionsRevoked)
            .ConfigureAwait(false);
        return ended.SessionDeleted ? SignedOutAfterMutation(result) : SessionDeletionFailure(result);
    }

    public Task<ExoOnlineResult<ExoAccountExport>> ExportAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var session = _store.TryLoad();
        var expectedUserId = session?.AccountId;
        var secret = session?.AccessToken;
        return GetJsonAsync<ExoAccountExport>(
            ExoIdContract.MeExportPath,
            cacheKey: "account-export",
            requireAuthentication: true,
            cacheBySession: false,
            fallbackCacheScope: null,
            cacheScopeForLive: null,
            validate: export => SanitizeExport(export, expectedUserId, secret),
            cancellationToken);
    }

    public async Task<ExoOnlineResult<ExoAccountDeleteResult>> DeleteAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAuthenticatedJsonAsync<ExoAccountDeleteResult>(
                HttpMethod.Delete,
                ExoIdContract.MePath,
                body: null,
                static deleted => deleted.Ok,
                cancellationToken,
                enforceSessionContinuity: false)
            .ConfigureAwait(false);
        if (!result.Ok)
            return result;
        var ended = await _lifecycle.EndSessionAsync(ExoSessionEndReason.AccountDeleted)
            .ConfigureAwait(false);
        return ended.SessionDeleted ? SignedOutAfterMutation(result) : SessionDeletionFailure(result);
    }

    public async Task<ExoOnlineResult<ExoProfileMediaMetadata>> UploadProfileMediaFileAsync(
        string? kind,
        string? nativePath,
        CancellationToken cancellationToken = default)
    {
        var contentType = Path.GetExtension(nativePath ?? string.Empty).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null,
        };
        if (contentType is null || string.IsNullOrWhiteSpace(nativePath))
            return Invalid<ExoProfileMediaMetadata>("MEDIA_UNSUPPORTED", "Use a PNG, JPEG, WebP, or GIF image.");
        try
        {
            await using var stream = new FileStream(
                nativePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await UploadProfileMediaAsync(kind, stream, contentType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Unavailable<ExoProfileMediaMetadata>(
                _store.TryLoad() is null ? false : null,
                "CANCELLED",
                "The media upload was cancelled.",
                true);
        }
        catch
        {
            return Invalid<ExoProfileMediaMetadata>("MEDIA_READ_FAILED", "That image could not be read.");
        }
    }

    public async Task<ExoOnlineResult<ExoProfileMediaMetadata>> UploadProfileMediaAsync(
        string? kind,
        Stream? nativeStream,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        var normalizedKind = NormalizeMediaKind(kind);
        var normalizedContentType = NormalizeMediaContentType(contentType);
        if (normalizedKind is null || normalizedContentType is null || nativeStream is null || !nativeStream.CanRead)
            return Invalid<ExoProfileMediaMetadata>("MEDIA_UNSUPPORTED", "Use a PNG, JPEG, or WebP image.");
        var limit = MediaLimit(normalizedKind);
        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(nativeStream, limit, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaTooLargeException)
        {
            return Invalid<ExoProfileMediaMetadata>(
                "MEDIA_TOO_LARGE",
                normalizedKind == "avatar"
                    ? "Avatar must be 4 MiB or smaller."
                    : "Banner must be 8 MiB or smaller.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Unavailable<ExoProfileMediaMetadata>(
                _store.TryLoad() is null ? false : null,
                "CANCELLED",
                "The media upload was cancelled.",
                true);
        }
        catch
        {
            return Invalid<ExoProfileMediaMetadata>("MEDIA_READ_FAILED", "That image could not be read.");
        }
        if (bytes.Length == 0)
            return Invalid<ExoProfileMediaMetadata>("MEDIA_INVALID", "The image is empty.");

        var session = _store.TryLoad();
        var expectedUserId = session?.AccountId;
        var result = await SendAuthenticatedBytesAsync<ExoProfileMediaEnvelope>(
                HttpMethod.Put,
                ExoIdContract.ProfileMediaPath(normalizedKind),
                bytes,
                normalizedContentType,
                envelope => ValidateMediaMetadata(envelope.Media, expectedUserId, normalizedKind),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok && IsSafeId(expectedUserId))
        {
            _cache.RemoveByPrefix(
                expectedUserId!,
                $"media:{expectedUserId}:{normalizedKind}:");
            _mediaCache.Clear();
        }
        return Map(result, static envelope => envelope.Media);
    }

    public async Task<ExoOnlineResult<ExoMutationAck>> DeleteProfileMediaAsync(
        string? kind,
        CancellationToken cancellationToken = default)
    {
        var normalizedKind = NormalizeMediaKind(kind);
        if (normalizedKind is null)
            return Invalid<ExoMutationAck>("MEDIA_INVALID", "Media kind must be avatar or banner.");
        var result = await SendAuthenticatedJsonAsync<ExoMutationAck>(
                HttpMethod.Delete,
                ExoIdContract.ProfileMediaPath(normalizedKind),
                body: null,
                static ack => ack.Ok,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok)
        {
            var session = _store.TryLoad();
            if (IsSafeId(session?.AccountId))
            {
                _cache.RemoveByPrefix(
                    session!.AccountId!,
                    $"media:{session.AccountId}:{normalizedKind}:");
                _mediaCache.Clear();
            }
        }
        return result;
    }

    public async Task<ExoOnlineResult<ExoProfileMediaLocalRef>> DownloadProfileMediaAsync(
        string? immutableUserId,
        ExoProfileMediaMetadata? metadata,
        CancellationToken cancellationToken = default)
    {
        if (metadata is null)
            return Invalid<ExoProfileMediaLocalRef>("MEDIA_INVALID", "The media reference is invalid.");
        var kind = NormalizeMediaKind(metadata.Kind);
        if (!IsSafeId(immutableUserId) || kind is null ||
            !ValidateMediaMetadata(metadata, immutableUserId, kind))
        {
            return Invalid<ExoProfileMediaLocalRef>("MEDIA_INVALID", "The media reference is invalid.");
        }
        var expectedPath = ExoIdContract.MediaPath(immutableUserId!, kind, metadata.Version);
        if (!string.Equals(metadata.Url, expectedPath, StringComparison.Ordinal))
            return Invalid<ExoProfileMediaLocalRef>("MEDIA_INVALID", "The media reference is invalid.");

        var session = await LoadUsableSessionAsync().ConfigureAwait(false);
        if (session is null)
            return SignedOut<ExoProfileMediaLocalRef>();
        if (!IsSafeId(session.AccountId))
            return Unavailable<ExoProfileMediaLocalRef>(false, "INVALID_SESSION", "Sign in again.", false);
        if (string.IsNullOrEmpty(_origin))
            return Unavailable<ExoProfileMediaLocalRef>(true, "NOT_CONFIGURED", "Online services are not configured.", false);
        var viewerId = session.AccountId!;
        var cacheKey = $"media:{immutableUserId}:{kind}:{metadata.Version}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(expectedPath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!SessionStillCurrent(session))
                return AuthChanged<ExoProfileMediaLocalRef>();
            var status = (int)response.StatusCode;
            if (status == 401)
                return await UnauthorizedAsync<ExoProfileMediaLocalRef>().ConfigureAwait(false);
            var contentType = NormalizeMediaContentType(response.Content.Headers.ContentType?.MediaType);
            var expectedType = NormalizeMediaContentType(metadata.ContentType);
            var length = response.Content.Headers.ContentLength;
            if (status != 200 ||
                !string.Equals(contentType, expectedType, StringComparison.Ordinal) ||
                (length is long declared && declared != metadata.Size))
            {
                var error = ReadError(document: null, status, ReadRetryAfter(response));
                if (status is 403 or 404)
                {
                    _cache.RemoveByPrefix(viewerId, cacheKey);
                    _mediaCache.Clear();
                }
                return IsRetryable(status)
                    ? CachedMediaOrUnavailable(
                        viewerId,
                        kind,
                        cacheKey,
                        metadata,
                        error.Code,
                        error.Message,
                        retryable: true)
                    : Unavailable<ExoProfileMediaLocalRef>(
                        true,
                        error.Code,
                        error.Message,
                        retryable: false);
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var stored = await _mediaCache.TryStoreAsync(
                    viewerId,
                    kind,
                    metadata.Version,
                    content,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!SessionStillCurrent(session))
            {
                _mediaCache.Clear();
                return AuthChanged<ExoProfileMediaLocalRef>();
            }
            if (stored is null)
            {
                return Unavailable<ExoProfileMediaLocalRef>(
                    true,
                    "INVALID_MEDIA_RESPONSE",
                    "The identity service returned invalid media.",
                    retryable: false);
            }
            var stamp = _utcNow().ToUniversalTime();
            _cache.Write(viewerId, cacheKey, stored, stamp);
            if (!SessionStillCurrent(session))
                return AuthChanged<ExoProfileMediaLocalRef>();
            return Live(stored, stamp, signedIn: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Unavailable<ExoProfileMediaLocalRef>(
                null,
                "CANCELLED",
                "The media download was cancelled.",
                retryable: false);
        }
        catch
        {
            if (!SessionStillCurrent(session))
                return AuthChanged<ExoProfileMediaLocalRef>();
            return CachedMediaOrUnavailable(
                viewerId,
                kind,
                cacheKey,
                metadata,
                "NETWORK_UNAVAILABLE",
                "Online services could not be reached.",
                true);
        }
    }

    public async Task<ExoOnlineResult<ExoPresenceRoster>> GetPresenceAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50)
            return Invalid<ExoPresenceRoster>("INVALID_REQUEST", "Presence limit must be between 1 and 50.");
        var path = Query(ExoIdContract.PresencePath, ("limit", limit.ToString()));
        var result = await GetAuthenticatedAsync<ExoPresenceWireRoster>(
                path,
                $"presence:{limit}",
                roster => ValidatePresenceRoster(roster, limit),
                cancellationToken)
            .ConfigureAwait(false);
        return Map(result, ToPresenceRoster);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    public void ClearLocalCaches()
    {
        _cache.Clear();
        _mediaCache.Clear();
        lock (_healthGate)
        {
            _lastHealth = null;
            _lastHealthSync = null;
        }
    }

    private async Task<ExoOnlineResult<T>> GetAuthenticatedAsync<T>(
        string path,
        string cacheKey,
        Func<T, bool> validate,
        CancellationToken cancellationToken)
        where T : class
        => await GetJsonAsync(
                path,
                cacheKey,
                requireAuthentication: true,
                cacheBySession: true,
                fallbackCacheScope: null,
                cacheScopeForLive: null,
                validate,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<ExoOnlineResult<T>> SendAuthenticatedJsonAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        Func<T, bool> validate,
        CancellationToken cancellationToken,
        bool enforceSessionContinuity = true)
        where T : class
    {
        if (_disposed)
            return Unavailable<T>(null, "DISPOSED", "Online services are not available.", retryable: false);
        var session = await LoadUsableSessionAsync().ConfigureAwait(false);
        if (session is null)
            return SignedOut<T>();
        if (string.IsNullOrEmpty(_origin))
            return Unavailable<T>(true, "NOT_CONFIGURED", "Online services are not configured.", retryable: false);

        byte[]? payload = null;
        try
        {
            if (body is not null)
                payload = JsonSerializer.SerializeToUtf8Bytes(body, JsonOpts);
        }
        catch (Exception)
        {
            return Invalid<T>("INVALID_REQUEST", "That request could not be encoded.");
        }
        if (payload?.Length > ExoIdContract.MaxJsonResponseBytes)
            return Invalid<T>("INVALID_REQUEST", "That request is too large.");

        using var request = new HttpRequestMessage(method, Url(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        if (payload is not null)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = Encoding.UTF8.WebName,
            };
        }
        HttpResult response;
        try { response = await SendAsync(request, cancellationToken).ConfigureAwait(false); }
        finally
        {
            if (payload is not null)
                CryptographicOperations.ZeroMemory(payload);
        }
        if (enforceSessionContinuity && !SessionStillCurrent(session))
        {
            response.Document?.Dispose();
            return AuthChanged<T>();
        }
        using (response.Document)
        {
            if (response.Status == 401)
                return await UnauthorizedAsync<T>().ConfigureAwait(false);
            if (response.Status is >= 200 and < 300 && response.Document is not null)
            {
                T? value;
                try { value = response.Document.RootElement.Deserialize<T>(JsonOpts); }
                catch (JsonException) { value = null; }
                if (value is not null && validate(value) &&
                    !ContainsSecretInJson(value, session.AccessToken))
                {
                    if (enforceSessionContinuity && !SessionStillCurrent(session))
                        return AuthChanged<T>();
                    return Live(value, _utcNow().ToUniversalTime(), signedIn: true);
                }
                return Unavailable<T>(
                    true,
                    "INVALID_RESPONSE",
                    "The identity service returned an invalid response.",
                    retryable: true);
            }

            var error = ReadError(response.Document, response.Status, response.RetryAfter);
            return Unavailable<T>(
                IsRetryable(response.Status) ? null : true,
                error.Code,
                error.Message,
                IsRetryable(response.Status));
        }
    }

    private async Task<ExoOnlineResult<T>> SendAuthenticatedBytesAsync<T>(
        HttpMethod method,
        string path,
        byte[] payload,
        string contentType,
        Func<T, bool> validate,
        CancellationToken cancellationToken)
        where T : class
    {
        var session = await LoadUsableSessionAsync().ConfigureAwait(false);
        if (session is null)
        {
            CryptographicOperations.ZeroMemory(payload);
            return SignedOut<T>();
        }
        if (string.IsNullOrEmpty(_origin))
        {
            CryptographicOperations.ZeroMemory(payload);
            return Unavailable<T>(true, "NOT_CONFIGURED", "Online services are not configured.", false);
        }
        using var request = new HttpRequestMessage(method, Url(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        HttpResult response;
        try { response = await SendAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(payload); }
        if (!SessionStillCurrent(session))
        {
            response.Document?.Dispose();
            return AuthChanged<T>();
        }
        using (response.Document)
        {
            if (response.Status == 401)
                return await UnauthorizedAsync<T>().ConfigureAwait(false);
            if (response.Status is >= 200 and < 300 && response.Document is not null)
            {
                T? value;
                try { value = response.Document.RootElement.Deserialize<T>(JsonOpts); }
                catch (JsonException) { value = null; }
                if (value is not null && validate(value) &&
                    !ContainsSecretInJson(value, session.AccessToken))
                {
                    if (!SessionStillCurrent(session))
                        return AuthChanged<T>();
                    return Live(value, _utcNow().ToUniversalTime(), true);
                }
                return Unavailable<T>(true, "INVALID_RESPONSE", "The identity service returned an invalid response.", true);
            }
            var error = ReadError(response.Document, response.Status, response.RetryAfter);
            return Unavailable<T>(
                IsRetryable(response.Status) ? null : true,
                error.Code,
                error.Message,
                IsRetryable(response.Status));
        }
    }

    private async Task<ExoOnlineResult<T>> GetJsonAsync<T>(
        string path,
        string cacheKey,
        bool requireAuthentication,
        bool cacheBySession,
        string? fallbackCacheScope,
        Func<T, string?>? cacheScopeForLive,
        Func<T, bool> validate,
        CancellationToken cancellationToken)
        where T : class
    {
        if (_disposed)
            return Unavailable<T>(null, "DISPOSED", "Online services are not available.", retryable: false);
        var session = await LoadUsableSessionAsync().ConfigureAwait(false);
        if (requireAuthentication && session is null)
            return SignedOut<T>();
        if (string.IsNullOrEmpty(_origin))
            return Unavailable<T>(
                session is null ? false : true,
                "NOT_CONFIGURED",
                "Online services are not configured.",
                retryable: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(path));
        if (session is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (session is not null && !SessionStillCurrent(session))
        {
            response.Document?.Dispose();
            return AuthChanged<T>();
        }
        using (response.Document)
        {
            if (response.Status == 401)
                return await UnauthorizedAsync<T>().ConfigureAwait(false);
            if (response.Status is >= 200 and < 300 && response.Document is not null)
            {
                T? value;
                try { value = response.Document.RootElement.Deserialize<T>(JsonOpts); }
                catch (JsonException) { value = null; }
                if (value is not null && validate(value) &&
                    !ContainsSecretInJson(value, session?.AccessToken))
                {
                    if (session is not null && !SessionStillCurrent(session))
                        return AuthChanged<T>();
                    var stamp = _utcNow().ToUniversalTime();
                    var liveScope = cacheScopeForLive?.Invoke(value) ??
                                    (cacheBySession && IsSafeId(session?.AccountId)
                                        ? session!.AccountId
                                        : fallbackCacheScope);
                    if (IsSafeId(liveScope))
                        _cache.Write(liveScope!, cacheKey, value, stamp);
                    if (session is not null && !SessionStillCurrent(session))
                        return AuthChanged<T>();
                    return Live(value, stamp, session is not null);
                }

                return Unavailable<T>(
                    session is null ? false : true,
                    "INVALID_RESPONSE",
                    "The identity service returned an invalid response.",
                    retryable: false);
            }

            var error = ReadError(response.Document, response.Status, response.RetryAfter);
            var retryable = IsRetryable(response.Status);
            var cacheScope = CacheScope(session, cacheBySession, fallbackCacheScope);
            if (response.Status is 403 or 404 && IsSafeId(cacheScope))
                _cache.RemoveByPrefix(cacheScope!, cacheKey);
            return retryable
                ? CachedOrUnavailable<T>(
                    cacheScope,
                    session is null ? false : null,
                    cacheKey,
                    error.Code,
                    error.Message,
                    retryable: true)
                : Unavailable<T>(
                    session is null ? false : true,
                    error.Code,
                    error.Message,
                    retryable: false);
        }
    }

    private ExoOnlineResult<T> CachedOrUnavailable<T>(
        string? cacheScope,
        bool? signedIn,
        string cacheKey,
        string code,
        string message,
        bool retryable)
        where T : class
    {
        if (IsSafeId(cacheScope) &&
            _cache.TryRead<T>(cacheScope!, cacheKey, out var value, out var lastSuccessful) &&
            value is not null)
        {
            return new ExoOnlineResult<T>(
                true,
                value,
                new ExoOnlineDiagnostics(
                    Configured: true,
                    SignedIn: null,
                    Source: ExoOnlineSources.Cache,
                    LastSuccessfulSync: lastSuccessful,
                    Retryable: retryable,
                    Error: new ExoOnlineError(code, message)));
        }

        return Unavailable<T>(signedIn, code, message, retryable);
    }

    private async Task<ExoSession?> LoadUsableSessionAsync()
    {
        var session = _store.TryLoad();
        if (session is null)
        {
            await _lifecycle.EndSessionAsync(ExoSessionEndReason.SignedOut).ConfigureAwait(false);
            return null;
        }
        if (session.ExpiresUtc > _utcNow())
            return session;
        await _lifecycle.EndSessionAsync(ExoSessionEndReason.Expired).ConfigureAwait(false);
        return null;
    }

    private async Task<ExoOnlineResult<T>> UnauthorizedAsync<T>() where T : class
    {
        var ended = await _lifecycle.EndSessionAsync(ExoSessionEndReason.RemoteUnauthorized)
            .ConfigureAwait(false);
        return new ExoOnlineResult<T>(
            false,
            null,
            new ExoOnlineDiagnostics(
                Configured: !string.IsNullOrEmpty(_origin),
                SignedIn: false,
                Source: ExoOnlineSources.Unavailable,
                LastSuccessfulSync: null,
                Retryable: false,
                Error: ended.SessionDeleted
                    ? new ExoOnlineError("UNAUTHENTICATED", "You are signed out.")
                    : new ExoOnlineError(
                        "SESSION_DELETE_FAILED",
                        "Exo could not remove the protected session data from this PC.")));
    }

    private bool SessionStillCurrent(ExoSession expected)
    {
        var current = _store.TryLoad();
        return current is not null &&
               string.Equals(current.AccountId, expected.AccountId, StringComparison.Ordinal) &&
               ExoPkce.FixedEquals(expected.AccessToken, current.AccessToken);
    }

    private ExoOnlineResult<T> AuthChanged<T>() where T : class
    {
        var signedIn = _store.TryLoad() is not null;
        _cache.Clear();
        _mediaCache.Clear();
        return new ExoOnlineResult<T>(
            false,
            null,
            new ExoOnlineDiagnostics(
                Configured: !string.IsNullOrEmpty(_origin),
                SignedIn: signedIn,
                Source: ExoOnlineSources.Unavailable,
                LastSuccessfulSync: null,
                Retryable: false,
                Error: new ExoOnlineError("AUTH_CHANGED", "The signed-in account changed. Try again.")));
    }

    private ExoOnlineResult<T> SignedOut<T>() where T : class => new(
        false,
        null,
        new ExoOnlineDiagnostics(
            Configured: !string.IsNullOrEmpty(_origin),
            SignedIn: false,
            Source: ExoOnlineSources.Unavailable,
            LastSuccessfulSync: null,
            Retryable: false,
            Error: new ExoOnlineError("SIGNED_OUT", "Sign in to use online services.")));

    private ExoOnlineResult<T> Live<T>(T value, DateTimeOffset stamp, bool signedIn) where T : class => new(
        true,
        value,
        new ExoOnlineDiagnostics(
            Configured: true,
            SignedIn: signedIn,
            Source: ExoOnlineSources.Live,
            LastSuccessfulSync: stamp,
            Retryable: false,
            Error: null));

    private ExoOnlineResult<T> Unavailable<T>(
        bool? signedIn,
        string code,
        string message,
        bool retryable)
        where T : class => new(
        false,
        null,
        new ExoOnlineDiagnostics(
            Configured: !string.IsNullOrEmpty(_origin),
            SignedIn: signedIn,
            Source: ExoOnlineSources.Unavailable,
            LastSuccessfulSync: null,
            Retryable: retryable,
            Error: new ExoOnlineError(code, message)));

    private ExoOnlineResult<T> Invalid<T>(string code, string message) where T : class =>
        Unavailable<T>(_store.TryLoad() is null ? false : true, code, message, retryable: false);

    private static ExoOnlineResult<TTarget> Map<TSource, TTarget>(
        ExoOnlineResult<TSource> result,
        Func<TSource, TTarget> map)
        where TSource : class
        where TTarget : class => new(
        result.Ok,
        result.Value is null ? null : map(result.Value),
        result.Diagnostics,
        result.Queued);

    private static ExoOnlineResult<TTarget> FailureAs<TSource, TTarget>(ExoOnlineResult<TSource> result)
        where TSource : class
        where TTarget : class => new(false, null, result.Diagnostics, result.Queued);

    private ExoOnlineResult<T> SignedOutAfterMutation<T>(ExoOnlineResult<T> result) where T : class => new(
        true,
        result.Value,
        result.Diagnostics with { SignedIn = false },
        result.Queued);

    private ExoOnlineResult<T> SessionDeletionFailure<T>(ExoOnlineResult<T> result) where T : class => new(
        false,
        result.Value,
        new ExoOnlineDiagnostics(
            Configured: !string.IsNullOrEmpty(_origin),
            SignedIn: false,
            Source: result.Diagnostics.Source,
            LastSuccessfulSync: result.Diagnostics.LastSuccessfulSync,
            Retryable: false,
            Error: new ExoOnlineError(
                "SESSION_DELETE_FAILED",
                "Exo could not remove the protected session data from this PC.")),
        result.Queued);

    private ExoOnlineResult<ExoProfileMediaLocalRef> CachedMediaOrUnavailable(
        string viewerId,
        string kind,
        string cacheKey,
        ExoProfileMediaMetadata metadata,
        string code,
        string message,
        bool retryable)
    {
        if (_cache.TryRead<ExoProfileMediaLocalRef>(
                viewerId,
                cacheKey,
                out _,
                out var lastSuccessful) &&
            _mediaCache.TryGet(viewerId, kind, metadata.Version, metadata) is { } cached)
        {
            return new ExoOnlineResult<ExoProfileMediaLocalRef>(
                true,
                cached,
                new ExoOnlineDiagnostics(
                    Configured: true,
                    SignedIn: null,
                    Source: ExoOnlineSources.Cache,
                    LastSuccessfulSync: lastSuccessful,
                    Retryable: retryable,
                    Error: new ExoOnlineError(code, message)));
        }
        return Unavailable<ExoProfileMediaLocalRef>(null, code, message, retryable);
    }

    private async Task<ExoOnlineResult<ExoFriendRequest>> UpdateFriendRequestAsync(
        string? requestId,
        string action,
        CancellationToken cancellationToken)
    {
        if (!IsRequestId(requestId) || action is not ("accept" or "decline"))
            return Invalid<ExoFriendRequest>("INVALID_REQUEST", "That friend request id is invalid.");
        var path = ExoIdContract.FriendRequestsPath + "/" + requestId + "/" + action;
        var result = await SendAuthenticatedJsonAsync<ExoFriendRequestEnvelope>(
                HttpMethod.Post,
                path,
                body: null,
                static envelope => ValidateFriendRequest(envelope.Request),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok) InvalidateSocialCache();
        return Map(result, static envelope => envelope.Request);
    }

    private async Task<ExoOnlineResult<ExoAdminBadgeState>> MutateManagedBadgeAsync(
        HttpMethod method,
        string? handle,
        string? badge,
        CancellationToken cancellationToken)
    {
        var cleanHandle = (handle ?? string.Empty).Trim();
        var cleanBadge = (badge ?? string.Empty).Trim();
        if (!IsHandleShape(cleanHandle) || !ExoBadgeCatalog.ManageableKeys.Contains(cleanBadge))
            return Invalid<ExoAdminBadgeState>("INVALID_REQUEST", "That badge request is invalid.");

        var result = await SendAuthenticatedJsonAsync<ExoAdminBadgeState>(
                method,
                ExoIdContract.AdminBadgesPath,
                new { handle = cleanHandle, badge = cleanBadge },
                state => ValidateAdminBadgeState(state, cleanHandle),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok)
            InvalidateBadgeCaches();
        return result;
    }

    private void InvalidateSocialCache()
    {
        var session = _store.TryLoad();
        if (!IsSafeId(session?.AccountId))
            return;
        foreach (var prefix in new[] { "friends:", "friend-requests:", "blocks:", "profile-search:" })
            _cache.RemoveByPrefix(session!.AccountId!, prefix);
    }

    private void InvalidateBadgeCaches()
    {
        var session = _store.TryLoad();
        if (!IsSafeId(session?.AccountId))
            return;
        _cache.RemoveByPrefix(session!.AccountId!, "public-profile:");
        _cache.RemoveByPrefix(session.AccountId!, "profile-search:");
    }

    private void InvalidateViewerProfileCache(string targetUserId)
    {
        var session = _store.TryLoad();
        if (IsSafeId(session?.AccountId))
            _cache.RemoveByPrefix(session!.AccountId!, "public-profile:" + targetUserId);
    }

    private void InvalidateLinkCache()
    {
        var session = _store.TryLoad();
        if (IsSafeId(session?.AccountId))
            _cache.RemoveByPrefix(session!.AccountId!, "links");
    }

    private void InvalidateSessionCache()
    {
        var session = _store.TryLoad();
        if (IsSafeId(session?.AccountId))
            _cache.RemoveByPrefix(session!.AccountId!, "sessions");
    }

    private async Task<HttpResult> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var retryAfter = ReadRetryAfter(response);
            var declared = response.Content.Headers.ContentLength;
            if (declared is > ExoIdContract.MaxJsonResponseBytes)
                return new HttpResult(status, null, retryAfter);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var sink = new MemoryStream(
                declared is > 0 and <= ExoIdContract.MaxJsonResponseBytes ? (int)declared.Value : 0);
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (sink.Length + read > ExoIdContract.MaxJsonResponseBytes)
                    return new HttpResult(status, null, retryAfter);
                sink.Write(buffer, 0, read);
            }

            if (sink.Length == 0)
                return new HttpResult(status, null, retryAfter);
            try
            {
                var bytes = sink.ToArray();
                try
                {
                    using var jsonStream = new MemoryStream(bytes, writable: false);
                    return new HttpResult(status, JsonDocument.Parse(jsonStream), retryAfter);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            catch (JsonException)
            {
                return new HttpResult(status, null, retryAfter);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new HttpResult(-2, null, null);
        }
        catch
        {
            return new HttpResult(-1, null, null);
        }
    }

    private static ExoOnlineError ReadError(JsonDocument? document, int status, int? retryAfter)
    {
        string? code = null;
        if (document is not null &&
            document.RootElement.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("code", out var codeElement) &&
            codeElement.ValueKind == JsonValueKind.String)
            code = codeElement.GetString();
        if (string.IsNullOrEmpty(code) || !StableCode.IsMatch(code))
            code = status switch
            {
                -2 => "CANCELLED",
                -1 => "NETWORK_UNAVAILABLE",
                429 => "RATE_LIMITED",
                >= 500 => "SERVICE_UNAVAILABLE",
                _ => "REMOTE_ERROR",
            };
        var message = code switch
        {
            "CANCELLED" => "The online request was cancelled.",
            "NETWORK_UNAVAILABLE" => "Online services could not be reached.",
            "RATE_LIMITED" => ExoIdErrors.RateLimited(retryAfter),
            "NOT_FOUND" => "Not found.",
            "INVALID_REQUEST" => "That request was not valid.",
            "SERVICE_UNAVAILABLE" or "INTERNAL" => "Online services could not complete that request.",
            _ => ExoIdErrors.UserMessage(code) ?? "Online services could not complete that request.",
        };
        return new ExoOnlineError(code, message);
    }

    private static bool IsRetryable(int status) => status is -1 or 408 or 429 or >= 500;

    private Uri Url(string path) => new(ExoIdContract.Combine(_origin!, path), UriKind.Absolute);

    private static string Query(string path, params (string Name, string? Value)[] values)
    {
        var query = values
            .Where(value => !string.IsNullOrEmpty(value.Value))
            .Select(value => Uri.EscapeDataString(value.Name) + "=" + Uri.EscapeDataString(value.Value!));
        var suffix = string.Join("&", query);
        return suffix.Length == 0 ? path : path + "?" + suffix;
    }

    private static bool IsSafeCursor(string? cursor) =>
        cursor is null || cursor.Length <= 2048 &&
        cursor.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static bool IsSafeId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(ch => ch >= 0x21 && ch <= 0x7e && ch is not '/' and not '\\');

    private static string? CacheScope(
        ExoSession? session,
        bool cacheBySession,
        string? fallbackCacheScope) =>
        cacheBySession && IsSafeId(session?.AccountId) ? session!.AccountId : fallbackCacheScope;

    private bool SanitizePublicProfile(ExoPublicProfile profile)
    {
        if (!IsSafeId(profile.UserId) || profile.Handle is null || !IsSafeHandle(profile.Handle))
            return false;
        if (!ExoBadgeCatalog.SanitizeBadgeSet(profile.Badges))
            return false;
        foreach (var key in profile.Profile.Keys.Where(key => !PublicProfileKeys.Contains(key)).ToArray())
            profile.Profile.Remove(key);
        if (profile.Profile.Any(pair => !ValidateProfileField(pair.Key, pair.Value)))
            return false;
        foreach (var key in profile.Media.Keys.Where(key => NormalizeMediaKind(key) is null).ToArray())
            profile.Media.Remove(key);
        foreach (var pair in profile.Media)
        {
            if (pair.Value is not null && !ValidateMediaMetadata(pair.Value, profile.UserId, pair.Key))
                return false;
        }
        return true;
    }

    private static bool ValidateAdminBadgeState(ExoAdminBadgeState state, string requestedHandle) =>
        IsSafeHandle(state.Handle) &&
        (string.Equals(state.Handle.Display, requestedHandle, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(state.Handle.Normalized, requestedHandle, StringComparison.OrdinalIgnoreCase)) &&
        ExoBadgeCatalog.SanitizeBadgeSet(state.Badges);

    private static bool IsSafeHandle(ExoHandleSummary handle) =>
        IsHandleShape(handle.Display) &&
        IsHandleShape(handle.Normalized) &&
        string.Equals(handle.Normalized, handle.Normalized.ToLowerInvariant(), StringComparison.Ordinal);

    private static bool IsHandleShape(string value) =>
        value.Length is >= 3 and <= 24 &&
        value.Any(char.IsAsciiLetter) &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private static bool IsValidPrivacy(ExoProfilePrivacy value) =>
        value.ProfileVisibility is "public" or "friends" or "private" &&
        value.RequestPolicy is "anyone" or "none" &&
        value.ActivityVisibility is "friends" or "private";

    private static bool ValidateFriendRequestPage(ExoFriendRequestPage page, int limit) =>
        page.Incoming.Count <= limit &&
        page.Outgoing.Count <= limit &&
        IsSafeCursor(page.NextIncomingCursor) &&
        IsSafeCursor(page.NextOutgoingCursor) &&
        page.Incoming.All(ValidateFriendRequest) &&
        page.Outgoing.All(ValidateFriendRequest);

    private static bool ValidateFriendRequest(ExoFriendRequest request) =>
        IsRequestId(request.Id) &&
        request.Direction is "incoming" or "outgoing" &&
        request.Status is "pending" or "accepted" or "declined" &&
        IsSafeId(request.User.UserId) &&
        (request.User.Handle is null || IsSafeHandle(request.User.Handle));

    private static bool ValidateBlock(ExoBlock block) =>
        IsSafeId(block.UserId) && (block.Handle is null || IsSafeHandle(block.Handle));

    private static bool ValidateFriend(ExoFriend friend) =>
        IsSafeId(friend.UserId) &&
        (friend.Handle is null || IsSafeHandle(friend.Handle)) &&
        (friend.Avatar is null || ValidateMediaMetadata(friend.Avatar, friend.UserId, "avatar")) &&
        friend.Sources.Count is >= 1 and <= 4 &&
        friend.Sources.Distinct(StringComparer.Ordinal).Count() == friend.Sources.Count &&
        friend.Sources.All(source => source is "direct" or "steam" or "epic" or "gog");

    private static bool IsRequestId(string? value) =>
        value is { Length: 48 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidateLinkState(ExoLinkState state) =>
        state.Links.All(link => ValidateStoreLink(link)) &&
        state.Connections.All(connection => ValidateConnection(connection));

    private static bool ValidateStoreLink(ExoVerifiedStoreLink link, string? expectedStore = null) =>
        IsStore(link.Store) &&
        (expectedStore is null || string.Equals(link.Store, expectedStore, StringComparison.Ordinal)) &&
        IsSafeExternalId(link.ExternalId) &&
        link.Verified;

    private static bool ValidateConnection(ExoConnection connection, string? expectedStore = null) =>
        IsSafeId(connection.UserId) &&
        IsStore(connection.Store) &&
        (expectedStore is null || string.Equals(connection.Store, expectedStore, StringComparison.Ordinal)) &&
        (connection.Handle is null || IsSafeHandle(connection.Handle));

    private static bool IsSafeExternalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);

    private static bool IsSafeOpaqueId(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl);

    private static bool SanitizeSessions(ExoSessionPage page, string? secret)
    {
        foreach (var session in page.Sessions)
        {
            if (!IsSafeOpaqueId(session.Id, 256))
                return false;
            if (session.UserAgent is { Length: > 512 } || session.UserAgent?.Any(char.IsControl) == true)
                session.UserAgent = null;
            if (ContainsSecret(session.UserAgent, secret))
                session.UserAgent = null;
        }
        return page.Sessions.Select(session => session.Id).Distinct(StringComparer.Ordinal).Count() ==
               page.Sessions.Count;
    }

    private bool SanitizeExport(ExoAccountExport export, string? expectedUserId, string? secret)
    {
        if (!IsSafeId(export.Account.Id) ||
            IsSafeId(expectedUserId) && !string.Equals(export.Account.Id, expectedUserId, StringComparison.Ordinal) ||
            !SanitizeSessions(new ExoSessionPage { Sessions = export.Sessions }, secret) ||
            export.Handle is not null && !IsSafeHandle(export.Handle) ||
            !ExoBadgeCatalog.ValidateRoleSet(export.Roles) ||
            !ExoBadgeCatalog.SanitizeBadgeSet(export.Badges) ||
            !IsValidPrivacy(export.Privacy) ||
            export.Links.Any(link => !ValidateStoreLink(link) || ContainsSecret(link.ExternalId, secret)) ||
            export.Connections.Any(connection => !ValidateConnection(connection)) ||
            export.DirectFriends.Any(friend => !IsSafeId(friend.UserId)) ||
            export.FriendRequests.Any(request =>
                !IsRequestId(request.Id) ||
                !IsSafeId(request.SenderId) ||
                !IsSafeId(request.RecipientId) ||
                request.Status is not ("pending" or "accepted" or "declined")) ||
            export.Blocks.Any(block => !IsSafeId(block.UserId)) ||
            export.Suppressions.Any(suppression =>
                !IsSafeId(suppression.UserId) ||
                !IsSafeOpaqueId(suppression.Reason, 64) ||
                ContainsSecret(suppression.Reason, secret)) ||
            export.Presence is not null && !ValidateExportPresence(export.Presence, export.Account.Id, secret))
            return false;
        if (ContainsSecret(export.Account.Name, secret)) export.Account.Name = null;
        if (ContainsSecret(export.Account.Email, secret)) export.Account.Email = null;
        foreach (var provider in export.Account.Providers)
        {
            if (!IsSafeOpaqueId(provider, 64) || ContainsSecret(provider, secret))
                return false;
        }
        FilterExportMap(export.Profile, PublicProfileKeys, secret);
        FilterExportMap(export.Preferences, new HashSet<string>(ExoSyncedSettings.SyncKeys, StringComparer.Ordinal), secret);
        if (export.Profile.Any(pair => !ValidateProfileField(pair.Key, pair.Value)))
            return false;
        foreach (var key in export.Media.Keys.Where(key => NormalizeMediaKind(key) is null).ToArray())
            export.Media.Remove(key);
        foreach (var pair in export.Media)
        {
            if (pair.Value is not null && !ValidateMediaMetadata(pair.Value, export.Account.Id, pair.Key))
                return false;
        }
        return true;
    }

    private static bool ValidateExportPresence(
        ExoExportPresence presence,
        string expectedUserId,
        string? secret) =>
        string.Equals(presence.UserId, expectedUserId, StringComparison.Ordinal) &&
        presence.Status is "online" or "away" or "in_game" or "offline" &&
        (presence.GameId is null || presence.GameId.Length <= ExoPresenceClient.MaxGameIdLength &&
            !presence.GameId.Any(char.IsControl) && !ContainsSecret(presence.GameId, secret)) &&
        (presence.GameTitle is null || presence.GameTitle.Length <= ExoPresenceClient.MaxGameTitleLength &&
            !presence.GameTitle.Any(char.IsControl) && !ContainsSecret(presence.GameTitle, secret));

    private static void FilterExportMap(
        Dictionary<string, JsonElement> values,
        HashSet<string> allowed,
        string? secret)
    {
        foreach (var key in values.Keys.ToArray())
        {
            if (!allowed.Contains(key) || ContainsSecret(values[key].GetRawText(), secret))
                values.Remove(key);
        }
    }

    private static bool ContainsSecret(string? value, string? secret) =>
        !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(secret) &&
        value.Contains(secret, StringComparison.Ordinal);

    private static bool ContainsSecretInJson<T>(T value, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return false;
        try { return JsonSerializer.Serialize(value, JsonOpts).Contains(secret, StringComparison.Ordinal); }
        catch { return true; }
    }

    private static string? NormalizeMediaKind(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (normalized is "avatar" or "banner") return normalized;
        return normalized is { Length: 8 } &&
               normalized.StartsWith("gallery", StringComparison.Ordinal) &&
               normalized[7] is >= '0' and <= '5'
            ? normalized
            : null;
    }

    private static string? NormalizeMediaContentType(string? value)
    {
        var normalized = (value ?? string.Empty).Split(';', 2)[0].Trim().ToLowerInvariant();
        return normalized is "image/png" or "image/jpeg" or "image/webp" or "image/gif" ? normalized : null;
    }

    private static long MediaLimit(string kind) =>
        kind == "avatar" ? ExoProfileMediaCache.MaxAvatarBytes : ExoProfileMediaCache.MaxBannerBytes;

    private static bool ValidateMediaMetadata(
        ExoProfileMediaMetadata metadata,
        string? immutableUserId,
        string expectedKind)
    {
        var contentType = NormalizeMediaContentType(metadata.ContentType);
        var dimensionsValid = expectedKind == "avatar"
            ? metadata.Width is >= 64 and <= 4096 && metadata.Height is >= 64 and <= 4096
            : expectedKind.StartsWith("gallery", StringComparison.Ordinal)
                ? metadata.Width is >= 128 and <= 4096 && metadata.Height is >= 128 and <= 4096 &&
                  (double)metadata.Width / metadata.Height is >= 0.25 and <= 4.0
                : metadata.Width is >= 320 and <= 8192 &&
              metadata.Height is >= 120 and <= 4096 &&
              (double)metadata.Width / metadata.Height is >= 1.5 and <= 8.0;
        return IsSafeId(immutableUserId) &&
               string.Equals(metadata.Kind, expectedKind, StringComparison.Ordinal) &&
               metadata.Version.Length == 64 && metadata.Version.All(IsLowerHex) &&
               string.Equals(
                   metadata.Url,
                   ExoIdContract.MediaPath(immutableUserId!, expectedKind, metadata.Version),
                   StringComparison.Ordinal) &&
               contentType is not null &&
               metadata.Size is > 0 && metadata.Size <= MediaLimit(expectedKind) &&
               dimensionsValid &&
               metadata.Sha256.Length == 64 && metadata.Sha256.All(IsLowerHex);
    }

    private static bool ValidateProfileField(string key, JsonElement value)
    {
        static bool Text(JsonElement element, int max) =>
            element.ValueKind == JsonValueKind.String && (element.GetString()?.Trim().Length ?? 0) <= max;
        static bool Id(JsonElement element) =>
            element.ValueKind == JsonValueKind.Null ||
            element.ValueKind == JsonValueKind.String &&
            (element.GetString()?.Trim() ?? "") is var text &&
            text.Length <= 80 &&
            !text.Contains('/') && !text.Contains('\\') && !text.Contains("..", StringComparison.Ordinal);
        static bool List(JsonElement element, int max, HashSet<string>? allowed)
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > max)
                return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return false;
                var text = item.GetString()?.Trim() ?? "";
                if (text.Length == 0 || !seen.Add(text) || allowed is not null && !allowed.Contains(text))
                    return false;
                if (allowed is null &&
                    (text.Length > 80 || text.Contains('/') || text.Contains('\\') ||
                     text.Contains("..", StringComparison.Ordinal)))
                    return false;
            }
            return true;
        }

        return key switch
        {
            "displayName" => Text(value, 40),
            "pronouns" => Text(value, 24),
            "statusText" => Text(value, 80),
            "bio" => Text(value, 400),
            "accent" => value.ValueKind == JsonValueKind.String &&
                        value.GetString() is "ash" or "steel" or "sand" or "clay" or "sage" or "rose",
            "layout" => value.ValueKind == JsonValueKind.String && value.GetString() is "left" or "center",
            "bannerHeight" => value.ValueKind == JsonValueKind.String &&
                              value.GetString() is "short" or "standard" or "tall",
            "showcaseStyle" => value.ValueKind == JsonValueKind.String &&
                               value.GetString() is "grid" or "rows",
            "sections" or "hiddenSections" => List(
                value,
                8,
                new HashSet<string>(["facts", "about", "showcase", "stores"], StringComparer.Ordinal)),
            "showcase" => List(value, 10, null),
            "avatarGameId" or "bannerGameId" => Id(value),
            _ => false,
        };
    }

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long limit,
        CancellationToken cancellationToken)
    {
        if (stream.CanSeek && stream.Length - stream.Position > limit)
            throw new MediaTooLargeException();
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length > limit - read)
                throw new MediaTooLargeException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool ValidatePresenceRoster(ExoPresenceWireRoster roster, int limit) =>
        roster.Friends.Count <= limit && roster.Friends.All(entry =>
            IsSafeId(entry.UserId) &&
            entry.Availability is "available" or "unavailable" &&
            entry.Status is "online" or "away" or "in_game" or "offline" or "unknown" &&
            (entry.GameId is null || entry.GameId.Length <= ExoPresenceClient.MaxGameIdLength &&
                !entry.GameId.Any(char.IsControl)) &&
            (entry.GameTitle is null || entry.GameTitle.Length <= ExoPresenceClient.MaxGameTitleLength &&
                !entry.GameTitle.Any(char.IsControl)));

    private static ExoPresenceRoster ToPresenceRoster(ExoPresenceWireRoster roster)
    {
        var friends = roster.Friends.Select(entry =>
        {
            var available = entry.Availability == "available";
            var status = available
                ? entry.Status switch
                {
                    "online" => ExoPresenceStatus.Online,
                    "away" => ExoPresenceStatus.Away,
                    "in_game" => ExoPresenceStatus.InGame,
                    "offline" => ExoPresenceStatus.Offline,
                    _ => ExoPresenceStatus.Unknown,
                }
                : ExoPresenceStatus.Unknown;
            return new ExoPresenceEntry(
                entry.UserId,
                status,
                status == ExoPresenceStatus.InGame ? entry.GameId : null,
                status == ExoPresenceStatus.InGame ? entry.GameTitle : null,
                entry.LastSeen,
                available);
        }).ToArray();
        return new ExoPresenceRoster(
            friends,
            roster.Unavailable || friends.Any(friend => !friend.Available));
    }

    private static bool IsStore(string? store) => store is "steam" or "epic" or "gog";

    private static bool IsKnownStore(ExoLinkedStore store) =>
        store is ExoLinkedStore.Steam or ExoLinkedStore.Epic or ExoLinkedStore.Gog;

    private static string StoreName(ExoLinkedStore store) => store switch
    {
        ExoLinkedStore.Steam => "steam",
        ExoLinkedStore.Epic => "epic",
        ExoLinkedStore.Gog => "gog",
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    private static string StorePath(ExoLinkedStore store) => store switch
    {
        ExoLinkedStore.Steam => ExoIdContract.LinksPath + "/steam",
        ExoLinkedStore.Epic => ExoIdContract.LinksEpicPath,
        ExoLinkedStore.Gog => ExoIdContract.LinksGogPath,
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    private static int? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta)
            return Math.Max(0, (int)Math.Ceiling(delta.TotalSeconds));
        if (header?.Date is DateTimeOffset date)
            return Math.Max(0, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        return null;
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
    };

    private readonly record struct HttpResult(int Status, JsonDocument? Document, int? RetryAfter);

    private sealed class MediaTooLargeException : Exception
    {
    }
}
