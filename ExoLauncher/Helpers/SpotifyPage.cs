using System.Text.Json;

namespace ExoLauncher.Helpers;

/// <summary>
/// One DevTools session on the hosted Spotify client's main page (xpui),
/// attached over the private pipe Exo launched it with
/// (<see cref="SpotifyDevTools"/>). It only evaluates expressions: the mini
/// player reads and writes Spotify's own volume through it. The session
/// re-attaches lazily after Spotify replaces its page or restarts; nothing is
/// written to Spotify's install or profile.
/// </summary>
internal sealed class SpotifyPage : IDisposable
{
    // Short: an idle-frozen Spotify can drop the page, and the next change
    // after it thaws should reach the mini player within about a second.
    private static readonly TimeSpan AttachCooldown = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _attachGate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private string? _session;
    private long _retryAt;

    /// Runtime.evaluate with awaitPromise and returnByValue; the result's
    /// value, or null when the page threw, the call timed out or Exo holds no
    /// pipe to Spotify (an adopted session before its restart).
    internal async Task<JsonElement?> EvaluateAsync(string expression, TimeSpan timeout, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _life.Token);
        linked.CancelAfter(timeout);
        try
        {
            if (await AttachAsync(linked.Token).ConfigureAwait(false) is not { } session) return null;
            var message = await SpotifyDevTools.SendAsync("Runtime.evaluate",
                new { expression, awaitPromise = true, returnByValue = true }, session, linked.Token).ConfigureAwait(false);
            if (message is not { } reply)
            {
                Interlocked.CompareExchange(ref _session, null, session);
                return null;
            }
            if (!reply.TryGetProperty("result", out var result) || result.TryGetProperty("exceptionDetails", out _) ||
                !result.TryGetProperty("result", out var remote))
                return null;
            return remote.TryGetProperty("value", out var value) ? value.Clone() : null;
        }
        catch (OperationCanceledException) { return null; }
    }

    private async Task<string?> AttachAsync(CancellationToken cancellation)
    {
        if (Volatile.Read(ref _session) is { } live && SpotifyDevTools.IsLive(live)) return live;
        if (!SpotifyDevTools.IsConnected) return null;
        await _attachGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_session is { } again && SpotifyDevTools.IsLive(again)) return again;
            _session = null;
            if (Environment.TickCount64 < Volatile.Read(ref _retryAt)) return null;
            Volatile.Write(ref _retryAt, Environment.TickCount64 + (long)AttachCooldown.TotalMilliseconds);
            if (await FindPageAsync(cancellation).ConfigureAwait(false) is not { } target) return null;
            var session = await SpotifyDevTools.AttachAsync(target, cancellation).ConfigureAwait(false);
            if (session is null) return null;
            _session = session;
            Volatile.Write(ref _retryAt, 0);
            return session;
        }
        finally { _attachGate.Release(); }
    }

    /// The xpui page target; Spotify also lists helper pages.
    private static async Task<string?> FindPageAsync(CancellationToken cancellation)
    {
        string? fallback = null;
        foreach (var (id, url) in await SpotifyDevTools.PagesAsync(cancellation).ConfigureAwait(false))
        {
            if (url.Contains("xpui", StringComparison.OrdinalIgnoreCase)) return id;
            if (url.Contains("spotify", StringComparison.OrdinalIgnoreCase)) fallback ??= id;
        }
        return fallback;
    }

    public void Dispose()
    {
        _life.Cancel();
        if (Interlocked.Exchange(ref _session, null) is { } session && SpotifyDevTools.IsLive(session))
            _ = SpotifyDevTools.SendAsync("Target.detachFromTarget", new { sessionId = session }, null, CancellationToken.None);
    }
}
