namespace ExoLauncher.Services;

internal enum ExoSessionEndReason
{
    RemoteUnauthorized,
    Expired,
    SignedOut,
    CurrentSessionRevoked,
    AllSessionsRevoked,
    AccountDeleted,
}

internal sealed record ExoSessionEndResult(bool SessionDeleted, bool StateChanged);

/// <summary>
/// The one session-ending module shared by account and social transports.
/// It owns protected-session deletion and both account-scoped disk caches;
/// its single observer is the bridge seam for presence and UI state.
/// </summary>
internal sealed class ExoIdentityLifecycle
{
    private readonly ExoSessionStore _sessionStore;
    private readonly ExoOnlineCache _onlineCache;
    private readonly ExoProfileMediaCache _mediaCache;
    private Func<ExoSessionEndReason, Task>? _signedOut;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _ended;

    internal ExoIdentityLifecycle(
        ExoSessionStore sessionStore,
        ExoOnlineCache onlineCache,
        ExoProfileMediaCache mediaCache,
        Func<ExoSessionEndReason, Task>? signedOut = null)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(onlineCache);
        ArgumentNullException.ThrowIfNull(mediaCache);
        _sessionStore = sessionStore;
        _onlineCache = onlineCache;
        _mediaCache = mediaCache;
        _signedOut = signedOut;
        _ended = sessionStore.TryLoad() is null ? 1 : 0;
    }

    internal void SetSignedOutObserver(Func<ExoSessionEndReason, Task> signedOut)
    {
        ArgumentNullException.ThrowIfNull(signedOut);
        if (Interlocked.CompareExchange(ref _signedOut, signedOut, null) is not null)
            throw new InvalidOperationException("The identity lifecycle observer is already configured.");
    }

    internal void MarkSignedIn() => Interlocked.Exchange(ref _ended, 0);

    internal async Task<ExoSessionEndResult> EndSessionAsync(ExoSessionEndReason reason)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var stateChanged = Interlocked.Exchange(ref _ended, 1) == 0;
            var sessionDeleted = _sessionStore.Delete();
            _onlineCache.Clear();
            _mediaCache.Clear();

            if (stateChanged && _signedOut is not null)
            {
                try
                {
                    await _signedOut(reason).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The protected state is already gone. Bridge repaint or
                    // optional presence failure cannot resurrect the session.
                    Helpers.AppLog.Debug("Exo signed-out bridge cleanup failed: " + ex.GetType().Name);
                }
            }

            return new ExoSessionEndResult(sessionDeleted, stateChanged);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Observable bridge half of session teardown. Callers get one awaited method;
/// ordering of mapped-media cleanup, presence stop, and state events stays here.
/// </summary>
internal sealed class ExoBridgeSessionCoordinator
{
    private readonly Action _clearMappedMedia;
    private readonly Func<Task> _stopPresenceAsync;
    private readonly Func<ExoAccountState> _signedOutAccount;
    private readonly Func<object> _profileSnapshot;
    private readonly Action<string, object?> _publishEvent;

    internal ExoBridgeSessionCoordinator(
        Action clearMappedMedia,
        Func<Task> stopPresenceAsync,
        Func<ExoAccountState> signedOutAccount,
        Func<object> profileSnapshot,
        Action<string, object?> publishEvent)
    {
        ArgumentNullException.ThrowIfNull(clearMappedMedia);
        ArgumentNullException.ThrowIfNull(stopPresenceAsync);
        ArgumentNullException.ThrowIfNull(signedOutAccount);
        ArgumentNullException.ThrowIfNull(profileSnapshot);
        ArgumentNullException.ThrowIfNull(publishEvent);
        _clearMappedMedia = clearMappedMedia;
        _stopPresenceAsync = stopPresenceAsync;
        _signedOutAccount = signedOutAccount;
        _profileSnapshot = profileSnapshot;
        _publishEvent = publishEvent;
    }

    internal async Task CompleteSignedOutAsync(ExoSessionEndReason reason)
    {
        _ = reason; // The UI state is intentionally the same for every terminal cause.
        _clearMappedMedia();
        await _stopPresenceAsync().ConfigureAwait(false);
        _publishEvent("account.updated", _signedOutAccount());
        _publishEvent("profile.updated", _profileSnapshot());
    }
}
