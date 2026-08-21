using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ExoLauncher.Services;

/// <summary>
/// RFC 8252 loopback redirect: <c>HttpListener</c> on 127.0.0.1 with an
/// ephemeral port. Never binds 0.0.0.0. Closed as soon as the callback is
/// answered (or the wait times out).
/// </summary>
internal sealed class ExoLoopbackListener : IDisposable
{
    internal const string CloseTabHtml =
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Exo</title></head>" +
        "<body><p>You can close this tab.</p></body></html>";

    private readonly object _gate = new();
    private HttpListener? _listener;

    private ExoLoopbackListener(HttpListener listener, int port, string prefix)
    {
        _listener = listener;
        Port = port;
        Prefix = prefix;
    }

    public int Port { get; }
    public string Prefix { get; }
    public string RedirectUriString => ExoIdContract.LoopbackRedirectUri(Port);
    public Uri RedirectUri => new(RedirectUriString);

    public bool IsListening
    {
        get
        {
            lock (_gate)
                return _listener?.IsListening == true;
        }
    }

    public static ExoLoopbackListener Start()
    {
        HttpListenerException? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var port = TakeEphemeralLoopbackPort();
            var prefix = $"http://127.0.0.1:{port}/";
            if (prefix.Contains("0.0.0.0", StringComparison.Ordinal) ||
                prefix.Contains("+", StringComparison.Ordinal) ||
                prefix.Contains("*", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing a non-loopback listener prefix.");

            var listener = new HttpListener { IgnoreWriteExceptions = true };
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return new ExoLoopbackListener(listener, port, prefix);
            }
            catch (HttpListenerException ex)
            {
                last = ex;
                try { listener.Close(); } catch { /* retry */ }
            }
        }

        throw new InvalidOperationException(
            "Could not start the sign-in listener on loopback.", last);
    }

    public async Task<LoopbackCallback> WaitForCallbackAsync(
        string expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var callback = await WaitForQueryAsync(expectedState, timeout, cancellationToken)
            .ConfigureAwait(false);
        if (callback.StateMismatch)
        {
            return new LoopbackCallback
            {
                Message = "Sign-in could not be verified. Try again.",
                StateMismatch = true,
            };
        }
        if (callback.Query.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
            return new LoopbackCallback { Error = error, Message = MapOAuthError(error) };
        if (callback.Query.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code))
            return new LoopbackCallback { Ok = true, Code = code };
        return new LoopbackCallback
        {
            Message = callback.Cancelled
                ? "Sign-in was cancelled."
                : callback.TimedOut
                    ? "Sign-in timed out. You can close the browser tab and try again."
                    : "Sign-in did not complete.",
        };
    }

    public async Task<LoopbackLinkCallback> WaitForLinkCallbackAsync(
        string expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var callback = await WaitForQueryAsync(expectedState, timeout, cancellationToken)
            .ConfigureAwait(false);
        if (callback.StateMismatch)
        {
            return new LoopbackLinkCallback
            {
                Message = "Steam linking could not be verified. Try again.",
                StateMismatch = true,
            };
        }
        if (callback.Query.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
        {
            return new LoopbackLinkCallback
            {
                Error = error,
                Message = ExoIdErrors.UserMessage(error) ?? "Steam account linking did not complete.",
            };
        }
        if (callback.Query.TryGetValue("link", out var link) &&
            string.Equals(link, "ok", StringComparison.Ordinal))
            return new LoopbackLinkCallback { Ok = true };
        return new LoopbackLinkCallback
        {
            Message = callback.Cancelled
                ? "Steam account linking was cancelled."
                : callback.TimedOut
                    ? "Steam account linking timed out. You can close the browser tab and try again."
                    : "Steam account linking did not complete.",
        };
    }

    private async Task<RawLoopbackCallback> WaitForQueryAsync(
        string expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedState);
        var listener = _listener ?? throw new ObjectDisposedException(nameof(ExoLoopbackListener));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        await using var registration = linked.Token.Register(Stop);

        try
        {
            while (!linked.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                var request = context.Request;
                var path = request.Url?.AbsolutePath ?? "";
                if (!IsCallbackPath(path))
                {
                    try
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                    }
                    catch
                    {
                        /* ignore */
                    }

                    continue;
                }

                var query = ParseQuery(request.Url);
                WriteQuietPage(context.Response);
                Stop();
                query.TryGetValue("state", out var state);
                if (!ExoPkce.FixedEquals(expectedState, state))
                    return new RawLoopbackCallback(query, StateMismatch: true, Cancelled: false, TimedOut: false);
                return new RawLoopbackCallback(query, StateMismatch: false, Cancelled: false, TimedOut: false);
            }
        }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            /* timed out or cancelled */
        }
        catch (HttpListenerException)
        {
            /* listener stopped */
        }
        catch (ObjectDisposedException)
        {
            /* listener stopped */
        }
        catch (InvalidOperationException)
        {
            /* listener stopped */
        }
        finally
        {
            Stop();
        }

        return new RawLoopbackCallback(
            new Dictionary<string, string>(StringComparer.Ordinal),
            StateMismatch: false,
            Cancelled: cancellationToken.IsCancellationRequested,
            TimedOut: !cancellationToken.IsCancellationRequested);
    }

    public void Stop()
    {
        HttpListener? listener;
        lock (_gate)
        {
            listener = _listener;
            _listener = null;
        }

        if (listener is null) return;
        try { if (listener.IsListening) listener.Stop(); } catch { /* */ }
        try { listener.Close(); } catch { /* */ }
    }

    public void Dispose() => Stop();

    internal static bool IsLoopbackOnlyPrefix(string prefix) =>
        prefix.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) &&
        !prefix.Contains("0.0.0.0", StringComparison.Ordinal) &&
        !prefix.Contains("://+", StringComparison.Ordinal) &&
        !prefix.Contains("://*", StringComparison.Ordinal);

    private static bool IsCallbackPath(string path) =>
        path.Equals(ExoIdContract.CallbackPath, StringComparison.OrdinalIgnoreCase);

    private static int TakeEphemeralLoopbackPort()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        try
        {
            return ((IPEndPoint)tcp.LocalEndpoint).Port;
        }
        finally
        {
            tcp.Stop();
        }
    }

    private static void WriteQuietPage(HttpListenerResponse response)
    {
        var bytes = Encoding.UTF8.GetBytes(CloseTabHtml);
        try
        {
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.Headers["Cache-Control"] = "no-store";
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            try { response.OutputStream.Close(); } catch { /* */ }
            try { response.Close(); } catch { /* */ }
        }
    }

    private static Dictionary<string, string> ParseQuery(Uri? uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var query = uri?.Query;
        if (string.IsNullOrEmpty(query))
            return result;

        var text = query[0] == '?' ? query[1..] : query;
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var rawKey = eq < 0 ? part : part[..eq];
            var rawValue = eq < 0 ? string.Empty : part[(eq + 1)..];
            var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            var value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            if (key.Length > 0)
                result[key] = value;
        }

        return result;
    }

    private static string MapOAuthError(string error) => error.Trim().ToLowerInvariant() switch
    {
        "access_denied" => "Sign-in was cancelled.",
        "server_error" or "temporarily_unavailable" =>
            "The identity service could not complete sign-in.",
        _ => "Sign-in did not complete.",
    };

    private sealed record RawLoopbackCallback(
        Dictionary<string, string> Query,
        bool StateMismatch,
        bool Cancelled,
        bool TimedOut);
}

internal sealed class LoopbackCallback
{
    public bool Ok { get; init; }
    public string? Code { get; init; }
    public string? Error { get; init; }
    public string Message { get; init; } = "";
    public bool StateMismatch { get; init; }
}

internal sealed class LoopbackLinkCallback
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string Message { get; init; } = "";
    public bool StateMismatch { get; init; }
}
