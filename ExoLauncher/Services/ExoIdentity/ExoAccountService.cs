using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Optional Exo account. Sign-in, sign-out, handle reserve, and portable
/// profile/preference get/set. Library, launch, and install never call this.
/// There is no refresh token; a dead session is a badge, not a wall.
/// </summary>
internal sealed class ExoAccountService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex EmailShape = new(
        @"^[^\s@]+@[^\s@]+\.[^\s@]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] ProviderOrder = ["google", "email", "password"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ExoSessionStore _store;
    private readonly HttpClient _http;
    private readonly Func<string, bool> _openBrowser;
    private readonly Func<ExoLoopbackListener> _startListener;
    private readonly Action _clearOnlineState;
    private readonly ExoIdentityLifecycle _lifecycle;
    private readonly string? _origin;
    private string? _deviceId;
    private string[] _availableProviders = [];
    private string[] _roles = [];
    private List<ExoProfileBadge> _badges = [];
    private bool _canManageBadges;
    private string? _authorityAccountId;
    private bool _disposed;

    public ExoAccountService()
        : this(
            new ExoSessionStore(),
            CreateHandler(),
            OpenSystemBrowser,
            ExoLoopbackListener.Start,
            origin: null,
            clearOnlineState: ClearDefaultOnlineState)
    {
    }

    internal ExoAccountService(
        ExoSessionStore store,
        HttpMessageHandler handler,
        Func<string, bool> openBrowser,
        Func<ExoLoopbackListener> startListener,
        string? origin,
        string? deviceId = null,
        Action? clearOnlineState = null,
        ExoIdentityLifecycle? lifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(openBrowser);
        ArgumentNullException.ThrowIfNull(startListener);
        _store = store;
        _openBrowser = openBrowser;
        _startListener = startListener;
        _clearOnlineState = clearOnlineState ?? (() => { });
        _lifecycle = lifecycle ?? new ExoIdentityLifecycle(
            store,
            new ExoOnlineCache(),
            new ExoProfileMediaCache());
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = ExoIdContract.HttpTimeout,
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ExoLauncher");
        try { _origin = ExoIdContract.ResolveOrigin(origin); }
        catch (Exception ex)
        {
            AppLog.Debug("Exo identity origin rejected: " + ex.GetType().Name);
            _origin = null;
        }
    }

    public async Task<ExoAccountState> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshProviderCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            var session = _store.TryLoad();
            if (session is null)
            {
                await _lifecycle.EndSessionAsync(ExoSessionEndReason.SignedOut).ConfigureAwait(false);
                return SignedOut();
            }

            session = await DropIfExpiredAsync(session).ConfigureAwait(false);
            if (session is null)
                return SignedOut();

            session = await PullMeAsync(session, cancellationToken).ConfigureAwait(false);
            return session is null ? SignedOut() : SignedIn(session);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<object> SignInAsync(
        string? provider,
        SettingsService? settings,
        CancellationToken cancellationToken = default,
        string? email = null)
    {
        if (!TryNormalizeProvider(provider, out var normalized, out var providerMessage))
            return Failed(providerMessage);

        if (normalized == "email")
        {
            var trimmed = (email ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > 254 || !EmailShape.IsMatch(trimmed))
                return Failed("An email address is required.");
            email = trimmed;
        }

        if (string.IsNullOrEmpty(_origin))
            return Failed("Exo accounts are not configured on this build.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ExoLoopbackListener? listener = null;
        try
        {
            if (_disposed)
                return Failed("Sign-in is not available.");

            var capabilitiesKnown = await RefreshProviderCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (capabilitiesKnown && !_availableProviders.Contains(normalized, StringComparer.Ordinal))
            {
                return Failed(normalized == "email"
                    ? ExoIdErrors.UserMessage("EMAIL_NOT_CONFIGURED")!
                    : ExoIdErrors.UserMessage("GOOGLE_NOT_CONFIGURED")!);
            }

            var verifier = ExoPkce.CreateVerifier();
            var challenge = ExoPkce.ChallengeS256(verifier);
            var state = ExoPkce.CreateState();
            listener = _startListener();
            var redirect = listener.RedirectUriString;

            var start = await StartLoginAsync(
                    normalized, redirect, challenge, state, email, cancellationToken)
                .ConfigureAwait(false);
            if (!start.Ok)
            {
                listener.Stop();
                listener = null;
                return Failed(start.Message);
            }

            if (normalized == "google")
            {
                if (string.IsNullOrEmpty(start.AuthorizationUrl) ||
                    !ExoIdContract.IsTrustedContinueUrl(_origin, start.AuthorizationUrl))
                {
                    listener.Stop();
                    listener = null;
                    return Failed("The identity service returned an invalid sign-in URL.");
                }

                if (!_openBrowser(start.AuthorizationUrl))
                {
                    listener.Stop();
                    listener = null;
                    return Failed("Could not open the system browser.");
                }
            }

            var wait = normalized == "email"
                ? ExoIdContract.MagicLinkLifetime
                : ExoIdContract.PendingLoginLifetime;
            var callback = await listener.WaitForCallbackAsync(state, wait, cancellationToken)
                .ConfigureAwait(false);
            listener = null;
            if (!callback.Ok)
                return Failed(string.IsNullOrWhiteSpace(callback.Message)
                    ? "Sign-in did not complete."
                    : callback.Message);

            var exchanged = await ExchangeCodeAsync(
                    callback.Code!, redirect, verifier, normalized, cancellationToken)
                .ConfigureAwait(false);
            if (exchanged.Session is null)
                return Failed(exchanged.Error ?? "The identity service could not complete sign-in.");

            _clearOnlineState();
            _store.Save(exchanged.Session);
            _lifecycle.MarkSignedIn();
            var session = await PullMeAsync(exchanged.Session, cancellationToken).ConfigureAwait(false);
            if (session is null)
                return Failed("Sign-in did not complete.");
            if (settings is not null)
            {
                await PullProfileIntoAsync(session, settings, cancellationToken)
                    .ConfigureAwait(false);
                if (_store.TryLoad() is null)
                    return Failed("Sign-in did not complete.");
                if (!string.IsNullOrEmpty(session.Handle))
                    await PullSyncIntoAsync(session, settings, cancellationToken)
                        .ConfigureAwait(false);
            }

            return SignedIn(session, ok: true, "Signed in.");
        }
        catch (OperationCanceledException)
        {
            return Failed(cancellationToken.IsCancellationRequested
                ? "Sign-in was cancelled."
                : "Sign-in timed out. You can close the browser tab and try again.");
        }
        catch (Exception ex)
        {
            AppLog.Debug("Exo sign-in failed: " + ex.GetType().Name);
            return Failed("Sign-in did not complete.");
        }
        finally
        {
            try { listener?.Stop(); } catch { /* */ }
            _gate.Release();
        }
    }

    public async Task<object> SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _store.TryLoad();
            var revoked = false;
            if (session is not null && !string.IsNullOrEmpty(_origin))
                revoked = await SignOutRemoteAsync(session, cancellationToken).ConfigureAwait(false);
            else if (session is null)
                revoked = true;

            var ended = await _lifecycle.EndSessionAsync(ExoSessionEndReason.SignedOut)
                .ConfigureAwait(false);
            var removed = ended.SessionDeleted;
            if (!removed)
            {
                return new
                {
                    ok = false,
                    signedIn = session is not null && !revoked,
                    message = "The server session was handled, but Exo could not remove the protected session data from this PC.",
                };
            }
            if (session is null)
                return new { ok = true, signedIn = false, message = "Signed out." };

            return new
            {
                ok = true,
                signedIn = false,
                message = revoked
                    ? "Signed out."
                    : "Signed out on this PC. The identity service could not revoke the server session.",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<object> ReserveHandleAsync(
        string? handle,
        SettingsService? settings,
        CancellationToken cancellationToken = default)
    {
        if (!ExoHandle.TryValidate(handle, out var clean, out var message))
            return Failed(message);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _store.TryLoad();
            session = await DropIfExpiredAsync(session).ConfigureAwait(false);
            if (session is null)
                return Failed("Sign in first to reserve a handle.");
            if (string.IsNullOrEmpty(_origin))
                return Failed("Exo accounts are not configured on this build.");

            using var request = JsonRequest(HttpMethod.Put, ExoIdContract.HandlePath, new { handle = clean });
            AttachBearer(request, session);
            var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            using (response.Document)
            {
                if (response.Status == 401)
                {
                    await _lifecycle.EndSessionAsync(ExoSessionEndReason.RemoteUnauthorized)
                        .ConfigureAwait(false);
                    return Failed(MapError(response));
                }

                if (response.Status < 200 || response.Status >= 300 || response.Document is null)
                    return Failed(MapError(response) ?? "That handle could not be reserved.");

                var reserved = ReadHandle(response.Document.RootElement) ?? clean;
                session.Handle = reserved;
                _store.Save(session);
                settings?.UpdateProfile(current => current.ProfileHandle = reserved);
                return new
                {
                    ok = true,
                    signedIn = true,
                    handle = reserved,
                    message = "Handle reserved.",
                    id = session.AccountId,
                    email = session.Email,
                    provider = session.Provider,
                };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<object> GetProfileAsync(
        SettingsService? settings,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _store.TryLoad();
            session = await DropIfExpiredAsync(session).ConfigureAwait(false);
            if (session is null)
                return new { ok = true, signedIn = false, profile = (object?)null, preferences = (object?)null };

            if (string.IsNullOrEmpty(_origin))
                return LocalPortable(settings);

            using var request = new HttpRequestMessage(HttpMethod.Get, Url(ExoIdContract.ProfilePath));
            AttachBearer(request, session);
            var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            using (response.Document)
            {
                if (response.Status < 0)
                    return LocalPortable(settings);
                if (response.Status == 401)
                {
                    await _lifecycle.EndSessionAsync(ExoSessionEndReason.RemoteUnauthorized)
                        .ConfigureAwait(false);
                    return new { ok = true, signedIn = false, profile = (object?)null, preferences = (object?)null };
                }

                if (response.Status < 200 || response.Status >= 300 || response.Document is null)
                    return LocalPortable(settings);

                var profile = ExoSyncedSettings.FilterProfile(response.Document.RootElement);
                JsonObject? preferences = null;
                if (!string.IsNullOrEmpty(session.Handle))
                    preferences = await GetSyncValuesAsync(session, cancellationToken).ConfigureAwait(false);
                if (_store.TryLoad() is null)
                    return new { ok = true, signedIn = false, profile = (object?)null, preferences = (object?)null };

                return new { ok = true, signedIn = true, profile, preferences };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<object> SetProfileAsync(
        JsonElement parameters,
        bool hasParams,
        SettingsService settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _store.TryLoad();
            session = await DropIfExpiredAsync(session).ConfigureAwait(false);
            if (session is null)
                return Failed("Sign in first to sync a profile.");
            if (string.IsNullOrEmpty(_origin))
                return Failed("Exo accounts are not configured on this build.");

            var snapshot = settings.Current;
            var profile = hasParams && parameters.ValueKind == JsonValueKind.Object
                ? ExoSyncedSettings.FilterProfile(parameters)
                : ExoSyncedSettings.ExtractProfile(snapshot);
            var sync = hasParams && parameters.ValueKind == JsonValueKind.Object
                ? ExoSyncedSettings.FilterSync(parameters)
                : ExoSyncedSettings.ExtractSync(snapshot);

            if (profile.Count == 0 && sync.Count == 0)
                return Failed("Nothing to save.");

            var stamp = DateTimeOffset.UtcNow;
            var deviceId = ResolveDeviceId();
            var older = false;
            JsonObject appliedProfile = profile;
            JsonObject? appliedSync = null;

            if (profile.Count > 0)
            {
                using var request = JsonRequest(
                    HttpMethod.Put,
                    ExoIdContract.ProfilePath,
                    ExoSyncedSettings.FieldVector(profile, deviceId, stamp));
                AttachBearer(request, session);
                var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                using (response.Document)
                {
                    if (response.Status == 401)
                    {
                        await _lifecycle.EndSessionAsync(ExoSessionEndReason.RemoteUnauthorized)
                            .ConfigureAwait(false);
                        return Failed(MapError(response));
                    }

                    if (response.Status < 200 || response.Status >= 300)
                        return Failed(MapError(response) ?? "The profile could not be saved.");

                    if (response.Document is not null)
                    {
                        older |= ExoSyncedSettings.HasOlderDiscard(response.Document.RootElement);
                        var incoming = ExoSyncedSettings.FilterProfile(response.Document.RootElement);
                        if (incoming.Count > 0)
                            appliedProfile = incoming;
                    }
                }
            }

            if (sync.Count > 0 && !string.IsNullOrEmpty(session.Handle))
            {
                using var request = JsonRequest(
                    HttpMethod.Put,
                    ExoIdContract.SyncPath,
                    ExoSyncedSettings.FieldVector(sync, deviceId, stamp));
                AttachBearer(request, session);
                var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                using (response.Document)
                {
                    if (response.Status == 401)
                    {
                        await _lifecycle.EndSessionAsync(ExoSessionEndReason.RemoteUnauthorized)
                            .ConfigureAwait(false);
                        return Failed(MapError(response));
                    }

                    if (response.Status == 403)
                    {
                        /* HANDLE_REQUIRED — profile still saved. */
                    }
                    else if (response.Status >= 200 && response.Status < 300 && response.Document is not null)
                    {
                        older |= ExoSyncedSettings.HasOlderDiscard(response.Document.RootElement);
                        var incoming = ExoSyncedSettings.FilterSync(response.Document.RootElement);
                        appliedSync = incoming.Count > 0 ? incoming : sync;
                    }
                    else if (response.Status >= 0)
                    {
                        return Failed(MapError(response) ?? "Preferences could not be saved.");
                    }
                }
            }

            settings.UpdateProfile(current =>
            {
                using var profileDoc = JsonDocument.Parse(appliedProfile.ToJsonString());
                ExoSyncedSettings.ApplyProfile(current, profileDoc.RootElement);
                if (appliedSync is not null)
                {
                    using var syncDoc = JsonDocument.Parse(appliedSync.ToJsonString());
                    ExoSyncedSettings.ApplySync(current, syncDoc.RootElement);
                }
            });

            return new
            {
                ok = true,
                signedIn = true,
                profile = appliedProfile,
                preferences = appliedSync,
                message = older
                    ? "The other PC's value was kept."
                    : "Profile saved.",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _http.Dispose(); } catch { /* */ }
    }

    public Task<object> CreatePasswordAccountAsync(
        string? name,
        string? email,
        string? password,
        SettingsService? settings,
        CancellationToken cancellationToken = default) =>
        PasswordAuthenticateAsync(createAccount: true, name, email, password, settings, cancellationToken);

    public Task<object> SignInWithPasswordAsync(
        string? email,
        string? password,
        SettingsService? settings,
        CancellationToken cancellationToken = default) =>
        PasswordAuthenticateAsync(createAccount: false, name: null, email, password, settings, cancellationToken);

    private static void ClearDefaultOnlineState()
    {
        new ExoOnlineCache().Clear();
        new ExoProfileMediaCache().Clear();
    }

    private async Task<ExoSession?> DropIfExpiredAsync(ExoSession? session)
    {
        if (session is null)
            return null;
        if (session.ExpiresUtc > DateTimeOffset.UtcNow)
            return session;
        await _lifecycle.EndSessionAsync(ExoSessionEndReason.Expired).ConfigureAwait(false);
        return null;
    }

    private async Task<StartLoginResult> StartLoginAsync(
        string provider,
        string redirectUri,
        string challenge,
        string state,
        string? email,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["provider"] = provider,
            ["redirectUri"] = redirectUri,
            ["codeChallenge"] = challenge,
            ["codeChallengeMethod"] = ExoIdContract.CodeChallengeMethod,
            ["state"] = state,
        };
        if (provider == "email")
            body["email"] = email;

        using var request = JsonRequest(HttpMethod.Post, ExoIdContract.AuthStartPath, body);
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using (response.Document)
        {
            var expected = provider == "email" ? 202 : 200;
            if (response.Status != expected || response.Document is null)
            {
                return new StartLoginResult
                {
                    Message = MapError(response)
                              ?? (response.Status < 0
                                  ? "The identity service could not be reached."
                                  : "Sign-in could not start."),
                };
            }

            var root = response.Document.RootElement;
            return new StartLoginResult
            {
                Ok = true,
                AuthorizationUrl = ReadString(root, "authorizationUrl"),
            };
        }
    }

    private async Task<TokenExchangeResult> ExchangeCodeAsync(
        string code,
        string redirectUri,
        string verifier,
        string provider,
        CancellationToken cancellationToken)
    {
        using var request = JsonRequest(
            HttpMethod.Post,
            ExoIdContract.AuthTokenPath,
            new
            {
                code,
                codeVerifier = verifier,
                redirectUri,
            });
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using (response.Document)
        {
            if (response.Status < 200 || response.Status >= 300 || response.Document is null)
            {
                return new TokenExchangeResult
                {
                    Error = MapError(response) ?? "The identity service could not complete sign-in.",
                };
            }

            return TryReadTokenResponse(response.Document.RootElement, existing: null, provider, out var session)
                ? new TokenExchangeResult { Session = session }
                : new TokenExchangeResult { Error = "The identity service could not complete sign-in." };
        }
    }

    private async Task<ExoSession?> PullMeAsync(ExoSession session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_origin))
            return session;

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(ExoIdContract.MePath));
        AttachBearer(request, session);
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using (response.Document)
        {
            if (response.Status == 401)
            {
                await _lifecycle.EndSessionAsync(ExoSessionEndReason.RemoteUnauthorized)
                    .ConfigureAwait(false);
                return null;
            }

            if (response.Status < 200 || response.Status >= 300 || response.Document is null)
                return session;

            ApplyAccount(session, response.Document.RootElement);
            ApplyAuthorityProjection(response.Document.RootElement);
            _store.Save(session);
            return session;
        }
    }

    private void ApplyAuthorityProjection(JsonElement root)
    {
        _roles = [];
        _badges = [];
        _canManageBadges = false;
        _authorityAccountId = null;

        if (!root.TryGetProperty("roles", out var rolesElement) ||
            rolesElement.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("badges", out var badgesElement) ||
            badgesElement.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("canManageBadges", out var canManageElement) ||
            canManageElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return;

        string[]? roles;
        List<ExoProfileBadge>? badges;
        try
        {
            roles = rolesElement.Deserialize<string[]>(JsonOpts);
            badges = badgesElement.Deserialize<List<ExoProfileBadge>>(JsonOpts);
        }
        catch (JsonException)
        {
            return;
        }

        if (!ExoBadgeCatalog.ValidateRoleSet(roles) || !ExoBadgeCatalog.SanitizeBadgeSet(badges))
            return;

        _roles = roles!;
        _badges = badges!;
        _authorityAccountId = ReadString(root, "id", "accountId", "sub");
        var roleAllowsManagement = _roles.Contains("owner", StringComparer.Ordinal) ||
                                   _roles.Contains("admin", StringComparer.Ordinal);
        _canManageBadges = canManageElement.GetBoolean() && roleAllowsManagement;
    }

    private async Task PullProfileIntoAsync(
        ExoSession session,
        SettingsService settings,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(ExoIdContract.ProfilePath));
        AttachBearer(request, session);
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using (response.Document)
        {
            if (response.Status == 401)
            {
                await _lifecycle.EndSessionAsync(ExoSessionEndReason.RemoteUnauthorized)
                    .ConfigureAwait(false);
                return;
            }

            if (response.Status < 200 || response.Status >= 300 || response.Document is null)
                return;
            settings.UpdateProfile(current =>
                ExoSyncedSettings.ApplyProfile(current, response.Document.RootElement));
        }
    }

    private async Task PullSyncIntoAsync(
        ExoSession session,
        SettingsService settings,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(ExoIdContract.SyncPath));
        AttachBearer(request, session);
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using (response.Document)
        {
            if (response.Status == 401)
            {
                await _lifecycle.EndSessionAsync(ExoSessionEndReason.RemoteUnauthorized)
                    .ConfigureAwait(false);
                return;
            }

            if (response.Status == 403 || response.Status < 200 || response.Status >= 300 ||
                response.Document is null)
                return;
            settings.UpdateProfile(current =>
                ExoSyncedSettings.ApplySync(current, response.Document.RootElement));
        }
    }

    private async Task<JsonObject?> GetSyncValuesAsync(ExoSession session, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(ExoIdContract.SyncPath));
        AttachBearer(request, session);
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using (response.Document)
        {
            if (response.Status == 401)
            {
                await _lifecycle.EndSessionAsync(ExoSessionEndReason.RemoteUnauthorized)
                    .ConfigureAwait(false);
                return null;
            }

            if (response.Status < 200 || response.Status >= 300 || response.Document is null)
                return null;
            return ExoSyncedSettings.FilterSync(response.Document.RootElement);
        }
    }

    private async Task<bool> SignOutRemoteAsync(ExoSession session, CancellationToken cancellationToken)
    {
        using var request = JsonRequest(HttpMethod.Post, ExoIdContract.AuthSignOutPath, new { });
        AttachBearer(request, session);
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.Document?.Dispose();
        return response.Status is >= 200 and < 300 or 401;
    }

    private async Task<object> PasswordAuthenticateAsync(
        bool createAccount,
        string? name,
        string? email,
        string? password,
        SettingsService? settings,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizePasswordCredentials(
                createAccount, name, email, password,
                out var normalizedName, out var normalizedEmail,
                out var validationCode, out var validationMessage))
            return PasswordFailed(validationCode, validationMessage);
        if (string.IsNullOrEmpty(_origin))
            return Failed("Exo accounts are not configured on this build.");

        var gateEntered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            if (_disposed)
                return Failed(createAccount ? "Account creation is not available." : "Sign-in is not available.");

            var capabilitiesKnown = await RefreshProviderCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (capabilitiesKnown && !_availableProviders.Contains("password", StringComparer.Ordinal))
                return PasswordFailed("INVALID_REQUEST", "Email and password accounts are not available.");

            if (createAccount)
            {
                object? signUpBody = new { name = normalizedName, email = normalizedEmail, password };
                IdentityResponse signUp;
                try
                {
                    signUp = await SendSensitiveJsonAsync(
                            ExoIdContract.PasswordSignUpPath,
                            signUpBody,
                            captureSessionToken: false,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    signUpBody = null;
                }
                using (signUp.Document)
                {
                    if (signUp.Status < 200 || signUp.Status >= 300)
                    {
                        var error = MapPasswordError(signUp, createAccount: true);
                        return PasswordFailed(error.Code, error.Message);
                    }
                }
            }

            object? signInBody = new { email = normalizedEmail, password };
            IdentityResponse response;
            try
            {
                response = await SendSensitiveJsonAsync(
                        ExoIdContract.PasswordSignInPath,
                        signInBody,
                        captureSessionToken: true,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                signInBody = null;
            }
            using (response.Document)
            {
                if (response.Status < 200 || response.Status >= 300)
                {
                    var error = MapPasswordError(response, createAccount);
                    return PasswordFailed(error.Code, error.Message);
                }
                if (string.IsNullOrEmpty(response.SessionToken))
                    return PasswordFailed("INTERNAL", "The identity service did not return a valid session.");

                var session = new ExoSession
                {
                    V = 1,
                    AccessToken = response.SessionToken,
                    RefreshToken = null,
                    Email = normalizedEmail,
                    Provider = "password",
                    ExpiresUtc = DateTimeOffset.UtcNow.Add(ExoIdContract.SessionLifetime),
                };
                if (response.Document is not null)
                    ApplyAccount(session, response.Document.RootElement);
                session.Provider = "password";

                _clearOnlineState();
                _store.Save(session);
                _lifecycle.MarkSignedIn();
                session = await PullMeAsync(session, cancellationToken).ConfigureAwait(false);
                if (session is null)
                    return Failed(createAccount ? "Account creation did not complete." : "Sign-in did not complete.");
                if (settings is not null)
                {
                    await PullProfileIntoAsync(session, settings, cancellationToken).ConfigureAwait(false);
                    if (_store.TryLoad() is null)
                        return Failed(createAccount ? "Account creation did not complete." : "Sign-in did not complete.");
                    if (!string.IsNullOrEmpty(session.Handle))
                        await PullSyncIntoAsync(session, settings, cancellationToken).ConfigureAwait(false);
                }

                return SignedIn(session, ok: true, createAccount ? "Account created." : "Signed in.");
            }
        }
        catch (PasswordRequestTooLargeException)
        {
            return PasswordFailed("INVALID_REQUEST", "That account request is too large.");
        }
        catch (OperationCanceledException)
        {
            return Failed(createAccount ? "Account creation was cancelled." : "Sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            AppLog.Debug("Exo password authentication failed: " + ex.GetType().Name);
            return Failed(createAccount ? "Account creation did not complete." : "Sign-in did not complete.");
        }
        finally
        {
            password = null;
            if (gateEntered)
                _gate.Release();
        }
    }

    private async Task<bool> RefreshProviderCapabilitiesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_origin))
        {
            _availableProviders = [];
            return true;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(ExoIdContract.HealthPath));
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using (response.Document)
        {
            if (response.Status < 200 || response.Status >= 300 || response.Document is null)
                return false;
            var available = new List<string>(ProviderOrder.Length);
            var root = response.Document.RootElement;
            if (root.TryGetProperty("capabilities", out var capabilities) &&
                capabilities.ValueKind == JsonValueKind.Object &&
                capabilities.TryGetProperty("providers", out var providers) &&
                providers.ValueKind == JsonValueKind.Object)
            {
                foreach (var provider in ProviderOrder)
                {
                    if (providers.TryGetProperty(provider, out var enabled) &&
                        enabled.ValueKind == JsonValueKind.True)
                        available.Add(provider);
                }
            }
            _availableProviders = available.ToArray();
            return true;
        }
    }

    private object LocalPortable(SettingsService? settings)
    {
        if (settings is null)
            return new { ok = true, signedIn = true, profile = new JsonObject(), preferences = new JsonObject() };
        return new
        {
            ok = true,
            signedIn = true,
            profile = ExoSyncedSettings.ExtractProfile(settings.Current),
            preferences = ExoSyncedSettings.ExtractSync(settings.Current),
        };
    }

    private string ResolveDeviceId()
    {
        if (!string.IsNullOrEmpty(_deviceId) && ExoDeviceId.IsValid(_deviceId))
            return _deviceId;
        _deviceId = ExoDeviceId.Get();
        return _deviceId;
    }

    private Uri Url(string path) => new(ExoIdContract.Combine(_origin!, path), UriKind.Absolute);

    private HttpRequestMessage JsonRequest(HttpMethod method, string path, object body)
    {
        var request = new HttpRequestMessage(method, Url(path));
        var json = body is JsonNode node
            ? node.ToJsonString()
            : JsonSerializer.Serialize(body, JsonOpts);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private HttpRequestMessage SensitiveJsonRequest(HttpMethod method, string path, object body) =>
        new(method, Url(path))
        {
            Content = new ZeroingJsonContent(body, JsonOpts),
        };

    private async Task<IdentityResponse> SendSensitiveJsonAsync(
        string path,
        object body,
        bool captureSessionToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = SensitiveJsonRequest(HttpMethod.Post, path, body);
            return await SendAsync(request, cancellationToken, captureSessionToken).ConfigureAwait(false);
        }
        finally
        {
            body = null!;
        }
    }

    private static void AttachBearer(HttpRequestMessage request, ExoSession session) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

    private async Task<IdentityResponse> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        bool captureSessionToken = false)
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
            var sessionToken = captureSessionToken ? ReadSessionToken(response) : null;
            var declared = response.Content.Headers.ContentLength;
            if (declared is > ExoIdContract.MaxJsonResponseBytes)
                return new IdentityResponse(status, null, retryAfter, sessionToken);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var sink = new MemoryStream(
                declared is > 0 and <= ExoIdContract.MaxJsonResponseBytes ? (int)declared.Value : 0);
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (sink.Length + read > ExoIdContract.MaxJsonResponseBytes)
                    return new IdentityResponse(status, null, retryAfter, sessionToken);
                sink.Write(buffer, 0, read);
            }
            if (sink.Length == 0)
                return new IdentityResponse(status, null, retryAfter, sessionToken);
            var bytes = sink.ToArray();
            try
            {
                using var jsonStream = new MemoryStream(bytes, writable: false);
                return new IdentityResponse(status, JsonDocument.Parse(jsonStream), retryAfter, sessionToken);
            }
            catch (JsonException)
            {
                return new IdentityResponse(status, null, retryAfter, sessionToken);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Exo identity request failed: " + ex.GetType().Name);
            return new IdentityResponse(-1, null, null, null);
        }
    }

    private static string? ReadSessionToken(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(ExoIdContract.BearerSessionHeader, out var values))
            return null;
        var tokens = values.Take(2).ToArray();
        if (tokens.Length != 1)
            return null;
        var token = tokens[0].Trim();
        if (token.Length is < 1 or > 4096 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            return null;
        return token;
    }

    private static int? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta)
            return Math.Max(0, (int)Math.Ceiling(delta.TotalSeconds));
        if (header?.Date is DateTimeOffset date)
            return Math.Max(0, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        if (response.Headers.TryGetValues("X-Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (int.TryParse(raw, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return Math.Clamp(seconds, 0, 86_400);
        }
        return null;
    }

    private static bool TryReadTokenResponse(
        JsonElement root,
        ExoSession? existing,
        string? provider,
        out ExoSession session)
    {
        var access = ReadString(root, "accessToken", "access_token");
        session = new ExoSession
        {
            V = 1,
            AccessToken = access ?? "",
            RefreshToken = null,
            AccountId = existing?.AccountId,
            Handle = existing?.Handle,
            Email = existing?.Email,
            Provider = existing?.Provider ?? provider,
            ExpiresUtc = ReadExpiry(root),
        };
        if (root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            ApplyAccount(session, user);
        else
            ApplyAccount(session, root);

        return !string.IsNullOrEmpty(session.AccessToken);
    }

    private static void ApplyAccount(ExoSession session, JsonElement element)
    {
        session.AccountId = ReadString(element, "id", "accountId", "sub") ?? session.AccountId;
        session.Handle = ReadHandle(element) ?? session.Handle;
        session.Email = ReadString(element, "email") ?? session.Email;
        session.Provider = ReadString(element, "provider") ?? session.Provider;
        if (element.TryGetProperty("session", out var nested) &&
            nested.ValueKind == JsonValueKind.Object &&
            nested.TryGetProperty("expiresAt", out var expiresAt))
        {
            var parsed = ReadDate(expiresAt);
            if (parsed is not null)
                session.ExpiresUtc = parsed.Value;
        }
    }

    private static string? ReadHandle(JsonElement element)
    {
        if (!element.TryGetProperty("handle", out var handle))
            return null;
        if (handle.ValueKind == JsonValueKind.Null)
            return null;
        if (handle.ValueKind == JsonValueKind.String)
            return handle.GetString();
        if (handle.ValueKind == JsonValueKind.Object)
            return ReadString(handle, "display") ?? ReadString(handle, "normalized");
        return null;
    }

    private static DateTimeOffset ReadExpiry(JsonElement root)
    {
        if (root.TryGetProperty("expiresAt", out var expiresAt))
        {
            var parsed = ReadDate(expiresAt);
            if (parsed is not null)
                return parsed.Value;
        }

        foreach (var name in new[] { "expiresIn", "expires_in" })
        {
            if (!root.TryGetProperty(name, out var element))
                continue;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
            {
                var seconds = Math.Clamp(value, 30, (int)TimeSpan.FromDays(30).TotalSeconds);
                return DateTimeOffset.UtcNow.AddSeconds(seconds);
            }
        }

        return DateTimeOffset.UtcNow.Add(ExoIdContract.SessionLifetime);
    }

    private static DateTimeOffset? ReadDate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(element.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static string? ReadErrorCode(JsonDocument? document)
    {
        if (document is null)
            return null;
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object)
                return ReadString(error, "code");
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }

        return ReadString(root, "code");
    }

    private static string? ReadErrorMessage(JsonDocument? document)
    {
        if (document is null)
            return null;
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var nested = ReadString(error, "message");
            if (!string.IsNullOrWhiteSpace(nested))
                return nested;
        }

        var message = ReadString(root, "message", "error_description", "error");
        if (string.IsNullOrWhiteSpace(message))
            return null;
        if (message.Length > 180)
            return "The identity service could not complete that request.";
        return message;
    }

    private static string MapError(IdentityResponse response)
    {
        var code = ReadErrorCode(response.Document);
        if (string.Equals(code, "RATE_LIMITED", StringComparison.Ordinal))
            return ExoIdErrors.RateLimited(response.RetryAfter);
        var mapped = ExoIdErrors.UserMessage(code);
        if (!string.IsNullOrEmpty(mapped))
            return mapped;
        if (response.Status == 401)
            return ExoIdErrors.UserMessage("UNAUTHENTICATED")!;
        var fallback = ReadErrorMessage(response.Document);
        if (!string.IsNullOrWhiteSpace(fallback) && fallback.Length <= 180 && fallback.IndexOf('@') < 0)
            return fallback;
        if (response.Status < 0)
            return "The identity service could not be reached.";
        return "The identity service could not complete that request.";
    }

    private static PasswordError MapPasswordError(IdentityResponse response, bool createAccount)
    {
        var code = ReadErrorCode(response.Document);
        if (string.Equals(code, "RATE_LIMITED", StringComparison.Ordinal) || response.Status == 429)
            return new PasswordError("RATE_LIMITED", ExoIdErrors.RateLimited(response.RetryAfter));
        if (string.Equals(code, "ACCOUNT_CONFLICT", StringComparison.Ordinal) ||
            string.Equals(code, "USER_ALREADY_EXISTS_USE_ANOTHER_EMAIL", StringComparison.Ordinal))
            return new PasswordError("ACCOUNT_CONFLICT", ExoIdErrors.UserMessage("ACCOUNT_CONFLICT")!);
        if (string.Equals(code, "INVALID_CREDENTIALS", StringComparison.Ordinal) ||
            string.Equals(code, "INVALID_EMAIL_OR_PASSWORD", StringComparison.Ordinal) ||
            response.Status == 401)
            return new PasswordError("INVALID_CREDENTIALS", ExoIdErrors.UserMessage("INVALID_CREDENTIALS")!);
        if (string.Equals(code, "INVALID_PASSWORD", StringComparison.Ordinal) ||
            string.Equals(code, "PASSWORD_TOO_SHORT", StringComparison.Ordinal) ||
            string.Equals(code, "PASSWORD_TOO_LONG", StringComparison.Ordinal))
            return new PasswordError("INVALID_PASSWORD", ExoIdErrors.UserMessage("INVALID_PASSWORD")!);
        if (response.Status < 0)
            return new PasswordError("INTERNAL", "The identity service could not be reached.");
        return new PasswordError(
            "INTERNAL",
            createAccount ? "Account creation did not complete." : "Sign-in did not complete.");
    }

    private static bool TryNormalizePasswordCredentials(
        bool createAccount,
        string? name,
        string? email,
        string? password,
        out string normalizedName,
        out string normalizedEmail,
        out string code,
        out string message)
    {
        normalizedName = (name ?? string.Empty).Trim();
        normalizedEmail = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedEmail.Length is < 3 or > 254 || !EmailShape.IsMatch(normalizedEmail))
        {
            code = "INVALID_REQUEST";
            message = "Enter a valid email address.";
            return false;
        }
        if (password is null || password.Length is < 12 or > 128)
        {
            code = "INVALID_PASSWORD";
            message = "Password must be 12 to 128 characters.";
            return false;
        }
        if (createAccount &&
            (normalizedName.Length == 0 ||
             normalizedName.EnumerateRunes().Take(81).Count() > 80 ||
             normalizedName.Any(char.IsControl)))
        {
            code = "INVALID_REQUEST";
            message = "Name must be 1 to 80 characters.";
            return false;
        }

        code = string.Empty;
        message = string.Empty;
        return true;
    }

    private static bool TryNormalizeProvider(string? raw, out string provider, out string message)
    {
        provider = string.IsNullOrWhiteSpace(raw) ? "google" : raw.Trim().ToLowerInvariant();
        switch (provider)
        {
            case "google":
            case "email":
                message = string.Empty;
                return true;
            case "apple":
                message = "Apple sign-in is not available.";
                return false;
            default:
                message = "Unknown sign-in provider.";
                return false;
        }
    }

    internal ExoAccountState SignedOutState => SignedOut();

    private ExoAccountState SignedOut()
    {
        _roles = [];
        _badges = [];
        _canManageBadges = false;
        _authorityAccountId = null;
        return new ExoAccountState
        {
            Ok = true,
            SignedIn = false,
            Configured = !string.IsNullOrEmpty(_origin),
            Providers = [.. _availableProviders],
        };
    }

    private ExoAccountState SignedIn(ExoSession session, bool ok = true, string? message = null)
    {
        var authorityMatches = !string.IsNullOrEmpty(session.AccountId) &&
                               string.Equals(session.AccountId, _authorityAccountId, StringComparison.Ordinal);
        return new ExoAccountState
        {
            Ok = ok,
            SignedIn = true,
            Configured = !string.IsNullOrEmpty(_origin),
            Providers = [.. _availableProviders],
            Message = message,
            Id = session.AccountId,
            Handle = session.Handle,
            Email = session.Email,
            Provider = session.Provider,
            Roles = authorityMatches ? [.. _roles] : [],
            CanManageBadges = authorityMatches && _canManageBadges,
            Badges = authorityMatches ? [.. _badges] : [],
        };
    }

    private static object Failed(string message) => new
    {
        ok = false,
        signedIn = false,
        message,
    };

    private static object PasswordFailed(string code, string message) => new
    {
        ok = false,
        signedIn = false,
        code,
        message,
    };

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
    };

    internal static bool OpenSystemBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("https" or "http"))
            return false;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return true;
    }

    private readonly record struct IdentityResponse(
        int Status,
        JsonDocument? Document,
        int? RetryAfter,
        string? SessionToken);

    private readonly record struct PasswordError(string Code, string Message);

    private sealed class ZeroingJsonContent : HttpContent
    {
        private byte[]? _buffer;

        public ZeroingJsonContent(object value, JsonSerializerOptions options)
        {
            var buffer = JsonSerializer.SerializeToUtf8Bytes(value, options);
            if (buffer.Length > ExoIdContract.MaxPasswordRequestBytes)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
                throw new PasswordRequestTooLargeException();
            }
            _buffer = buffer;
            Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
            Headers.ContentLength = _buffer.LongLength;
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context)
        {
            var buffer = _buffer ?? throw new ObjectDisposedException(nameof(ZeroingJsonContent));
            return stream.WriteAsync(buffer).AsTask();
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context,
            CancellationToken cancellationToken)
        {
            var buffer = _buffer ?? throw new ObjectDisposedException(nameof(ZeroingJsonContent));
            return stream.WriteAsync(buffer, cancellationToken).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _buffer?.LongLength ?? 0;
            return _buffer is not null;
        }

        protected override void Dispose(bool disposing)
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
            base.Dispose(disposing);
        }
    }

    private sealed class PasswordRequestTooLargeException : Exception
    {
    }

    private sealed class StartLoginResult
    {
        public bool Ok { get; init; }
        public string? AuthorizationUrl { get; init; }
        public string Message { get; init; } = "";
    }

    private sealed class TokenExchangeResult
    {
        public ExoSession? Session { get; init; }
        public string? Error { get; init; }
    }
}
