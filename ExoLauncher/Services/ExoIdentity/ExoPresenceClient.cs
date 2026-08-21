using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// Optional live presence transport. Callers own its lifetime explicitly;
/// constructing it never connects, and it is not a startup or tray service.
/// Bearer credentials stay inside the native transport boundary.
/// </summary>
internal sealed class ExoPresenceClient : IAsyncDisposable
{
    internal const int MaxSocketMessageBytes = 4 * 1024;
    internal const int MaxRestResponseBytes = 64 * 1024;
    internal const int MaxGameIdLength = 128;
    internal const int MaxGameTitleLength = 160;
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private static readonly Func<TimeSpan, CancellationToken, Task> DefaultDelay = Task.Delay;
    private readonly Uri _socketUri;
    private readonly Func<IExoPresenceSocket> _createSocket;
    private readonly Func<TimeSpan, CancellationToken, Task> _reconnectDelayAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _heartbeatDelayAsync;
    private readonly Func<TimeSpan, TimeSpan> _jitter;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<CancellationToken, Task<string>>? _restFallbackAsync;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private IExoPresenceSocket? _activeSocket;
    private IExoPresenceSocket? _readySocket;
    private ExoPresenceActivity _activity = ExoPresenceActivity.Online;
    private string? _gameId;
    private string? _gameTitle;
    private string? _redactionSecret;
    private bool _hasStarted;
    private bool _disposed;

    public ExoPresenceClient(
        Uri socketUri,
        Func<CancellationToken, Task<string>>? restFallbackAsync = null)
        : this(
            socketUri,
            static () => new ClientWebSocketPresenceSocket(),
            DefaultDelay,
            DefaultDelay,
            AddReconnectJitter,
            static () => DateTimeOffset.UtcNow,
            restFallbackAsync)
    {
    }

    internal ExoPresenceClient(
        Uri socketUri,
        Func<IExoPresenceSocket> createSocket,
        Func<TimeSpan, CancellationToken, Task> reconnectDelayAsync,
        Func<TimeSpan, CancellationToken, Task> heartbeatDelayAsync,
        Func<TimeSpan, TimeSpan> jitter,
        Func<DateTimeOffset> utcNow,
        Func<CancellationToken, Task<string>>? restFallbackAsync = null)
    {
        ArgumentNullException.ThrowIfNull(socketUri);
        ArgumentNullException.ThrowIfNull(createSocket);
        ArgumentNullException.ThrowIfNull(reconnectDelayAsync);
        ArgumentNullException.ThrowIfNull(heartbeatDelayAsync);
        ArgumentNullException.ThrowIfNull(jitter);
        ArgumentNullException.ThrowIfNull(utcNow);
        if (!socketUri.IsAbsoluteUri || socketUri.Scheme is not ("ws" or "wss"))
            throw new ArgumentException("Presence requires an absolute WebSocket URI.", nameof(socketUri));
        if (socketUri.Scheme == "ws" && !ExoIdContract.IsLoopbackHost(socketUri.DnsSafeHost))
            throw new ArgumentException("Cleartext presence WebSockets are only allowed on loopback.", nameof(socketUri));
        if (!string.IsNullOrEmpty(socketUri.UserInfo) ||
            !string.IsNullOrEmpty(socketUri.Query) ||
            !string.IsNullOrEmpty(socketUri.Fragment) ||
            !string.Equals(socketUri.AbsolutePath, ExoIdContract.PresenceSocketPath, StringComparison.Ordinal))
            throw new ArgumentException("Presence WebSocket URIs cannot contain user information.", nameof(socketUri));

        _socketUri = socketUri;
        _createSocket = createSocket;
        _reconnectDelayAsync = reconnectDelayAsync;
        _heartbeatDelayAsync = heartbeatDelayAsync;
        _jitter = jitter;
        _utcNow = utcNow;
        _restFallbackAsync = restFallbackAsync;
    }

    public event Action<ExoPresenceMessage>? MessageReceived;

    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
                return _runTask is { IsCompleted: false };
        }
    }

    public async Task StartAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("A presence access token is required.", nameof(accessToken));

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_runTask is { IsCompleted: false })
                    throw new InvalidOperationException("Presence is already running.");

                _runCancellation?.Dispose();
                _runCancellation = new CancellationTokenSource();
                _hasStarted = true;
                _redactionSecret = accessToken;
                _runTask = RunAsync(accessToken, _runCancellation.Token);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task StartAsync(ExoSessionStore sessionStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        var session = sessionStore.TryLoad();
        if (session is null || session.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            if (session is not null)
                sessionStore.Delete();
            throw new InvalidOperationException("Sign in to use live presence.");
        }
        return StartAsync(session.AccessToken, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? runTask;
        CancellationTokenSource? runCancellation;
        IExoPresenceSocket? activeSocket;

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                runTask = _runTask;
                runCancellation = _runCancellation;
                activeSocket = _activeSocket;
            }
            try { runCancellation?.Cancel(); }
            catch { }

            if (activeSocket is not null)
            {
                try { await CloseSocketAsync(activeSocket, cancellationToken).ConfigureAwait(false); }
                catch when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Presence stop was canceled.", cancellationToken);
                }
                catch { }
            }

            if (runTask is not null)
            {
                try { await runTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Presence stop was canceled.", cancellationToken);
                }
                catch { }
            }

            lock (_stateGate)
            {
                if (ReferenceEquals(_runTask, runTask))
                {
                    _runTask = null;
                    _runCancellation = null;
                    _activeSocket = null;
                    _readySocket = null;
                    _redactionSecret = null;
                }
            }
            runCancellation?.Dispose();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        await StopAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
        _sendGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public override string ToString() => nameof(ExoPresenceClient);

    public async Task<ExoPresenceRoster> GetRestFallbackAsync(CancellationToken cancellationToken = default)
    {
        if (_restFallbackAsync is null)
            return ExoPresenceRoster.ServiceUnavailable;

        string? redactionSecret;
        lock (_stateGate)
        {
            if (_disposed || (_hasStarted && _redactionSecret is null))
                return ExoPresenceRoster.ServiceUnavailable;
            redactionSecret = _redactionSecret;
        }

        string serialized;
        try
        {
            serialized = await _restFallbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Presence fallback was canceled.", cancellationToken);
        }
        catch
        {
            return ExoPresenceRoster.ServiceUnavailable;
        }

        return ParseRestFallback(serialized, redactionSecret);
    }

    public async Task SetStatusAsync(
        ExoPresenceActivity activity,
        string? gameId = null,
        string? gameTitle = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(activity))
            throw new ArgumentOutOfRangeException(nameof(activity));
        var normalizedGameId = NormalizeActivityField(gameId, MaxGameIdLength, nameof(gameId));
        var normalizedGameTitle = NormalizeActivityField(gameTitle, MaxGameTitleLength, nameof(gameTitle));
        if (activity != ExoPresenceActivity.InGame &&
            (normalizedGameId is not null || normalizedGameTitle is not null))
        {
            throw new ArgumentException("Game fields require in-game presence.");
        }

        IExoPresenceSocket? socket;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activity = activity;
            _gameId = activity == ExoPresenceActivity.InGame ? normalizedGameId : null;
            _gameTitle = activity == ExoPresenceActivity.InGame ? normalizedGameTitle : null;
            socket = _readySocket;
        }

        if (socket is null)
            return;

        try
        {
            await SendCurrentStatusAsync(socket, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Presence status update was canceled.", cancellationToken);
        }
        catch
        {
            throw new InvalidOperationException("Presence status could not be sent.");
        }
    }

    public static ExoPresenceRoster ParseRestFallback(string serialized) =>
        ParseRestFallback(serialized, redactionSecret: null);

    private static ExoPresenceRoster ParseRestFallback(string serialized, string? redactionSecret)
    {
        if (string.IsNullOrWhiteSpace(serialized) || Encoding.UTF8.GetByteCount(serialized) > MaxRestResponseBytes)
            return ExoPresenceRoster.ServiceUnavailable;

        try
        {
            using var document = JsonDocument.Parse(serialized, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("friends", out var friendsElement) ||
                friendsElement.ValueKind != JsonValueKind.Array)
            {
                return ExoPresenceRoster.ServiceUnavailable;
            }

            var friends = new List<ExoPresenceEntry>();
            foreach (var item in friendsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    friends.Add(ParseEntry(item, requireAvailability: true, redactionSecret));
            }

            var unavailable = !root.TryGetProperty("unavailable", out var unavailableElement) ||
                              unavailableElement.ValueKind != JsonValueKind.False;
            return new ExoPresenceRoster(friends, unavailable);
        }
        catch (JsonException)
        {
            return ExoPresenceRoster.ServiceUnavailable;
        }
    }

    private async Task RunAsync(string accessToken, CancellationToken cancellationToken)
    {
        var failedAttempts = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var becameReady = false;
            try
            {
                await RunConnectionAsync(
                        accessToken,
                        () => becameReady = true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                Publish(new ExoPresenceMessage(
                    ExoPresenceMessageKind.TransportError,
                    null,
                    "TRANSPORT_UNAVAILABLE",
                    "Presence connection is unavailable.",
                    _utcNow()));
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            if (becameReady)
                failedAttempts = 0;
            var baseDelay = ReconnectDelay(failedAttempts++);
            TimeSpan delayed;
            try { delayed = _jitter(baseDelay); }
            catch { delayed = baseDelay; }
            if (delayed < TimeSpan.Zero)
                delayed = TimeSpan.Zero;
            if (delayed > TimeSpan.FromSeconds(30))
                delayed = TimeSpan.FromSeconds(30);
            await _reconnectDelayAsync(delayed, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunConnectionAsync(
        string accessToken,
        Action markReady,
        CancellationToken cancellationToken)
    {
        await using var socket = _createSocket();
        lock (_stateGate)
            _activeSocket = socket;

        try
        {
            await socket.ConnectAsync(_socketUri, "Bearer " + accessToken, cancellationToken).ConfigureAwait(false);
            using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeatTask = RunHeartbeatAsync(socket, connectionCancellation.Token);
            var receiveTask = ReceiveMessagesAsync(
                socket,
                accessToken,
                markReady,
                connectionCancellation.Token);
            var completed = await Task.WhenAny(receiveTask, heartbeatTask).ConfigureAwait(false);
            try
            {
                await completed.ConfigureAwait(false);
            }
            finally
            {
                connectionCancellation.Cancel();
                if (!ReferenceEquals(completed, receiveTask))
                {
                    try { await receiveTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested) { }
                }
                if (!ReferenceEquals(completed, heartbeatTask))
                {
                    try { await heartbeatTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested) { }
                }
            }
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_activeSocket, socket))
                    _activeSocket = null;
                if (ReferenceEquals(_readySocket, socket))
                    _readySocket = null;
            }
        }
    }

    private async Task ReceiveMessagesAsync(
        IExoPresenceSocket socket,
        string accessToken,
        Action markReady,
        CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[1024];
        using var messageBuffer = new MemoryStream(MaxSocketMessageBytes);
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(receiveBuffer, cancellationToken).ConfigureAwait(false);
            if (result.FrameType == ExoPresenceFrameType.Close)
                return;
            if (result.FrameType != ExoPresenceFrameType.Text ||
                result.Count < 0 ||
                result.Count > receiveBuffer.Length ||
                messageBuffer.Length + result.Count > MaxSocketMessageBytes)
            {
                await RejectInvalidMessageAsync(socket, cancellationToken).ConfigureAwait(false);
                return;
            }

            messageBuffer.Write(receiveBuffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            ExoPresenceMessage message;
            try
            {
                message = ParseServerMessage(messageBuffer.GetBuffer().AsSpan(0, checked((int)messageBuffer.Length)), accessToken);
            }
            catch (ExoPresenceProtocolException)
            {
                await RejectInvalidMessageAsync(socket, cancellationToken).ConfigureAwait(false);
                return;
            }
            finally
            {
                messageBuffer.SetLength(0);
            }

            if (message.Kind == ExoPresenceMessageKind.Ready)
            {
                markReady();
                lock (_stateGate)
                    _readySocket = socket;
                Publish(message);
                await SendCurrentStatusAsync(socket, cancellationToken).ConfigureAwait(false);
                continue;
            }
            Publish(message);
        }
    }

    private async Task RunHeartbeatAsync(IExoPresenceSocket socket, CancellationToken cancellationToken)
    {
        var heartbeat = Encoding.UTF8.GetBytes("{\"type\":\"heartbeat\"}");
        while (true)
        {
            await _heartbeatDelayAsync(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            await SendAsync(socket, heartbeat, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SendCurrentStatusAsync(IExoPresenceSocket socket, CancellationToken cancellationToken)
    {
        ExoPresenceActivity activity;
        string? gameId;
        string? gameTitle;
        lock (_stateGate)
        {
            activity = _activity;
            gameId = _gameId;
            gameTitle = _gameTitle;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "status",
            status = activity switch
            {
                ExoPresenceActivity.Away => "away",
                ExoPresenceActivity.InGame => "in_game",
                _ => "online",
            },
            gameId = activity == ExoPresenceActivity.InGame ? gameId : null,
            gameTitle = activity == ExoPresenceActivity.InGame ? gameTitle : null,
        });
        return SendAsync(socket, payload, cancellationToken);
    }

    private async Task SendAsync(
        IExoPresenceSocket socket,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendTextAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task RejectInvalidMessageAsync(IExoPresenceSocket socket, CancellationToken cancellationToken)
    {
        Publish(new ExoPresenceMessage(
            ExoPresenceMessageKind.Error,
            null,
            "INVALID_MESSAGE",
            "Presence message is invalid.",
            _utcNow()));
        try { await CloseSocketAsync(socket, cancellationToken).ConfigureAwait(false); }
        catch when (!cancellationToken.IsCancellationRequested) { }
    }

    private async Task CloseSocketAsync(IExoPresenceSocket socket, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private ExoPresenceMessage ParseServerMessage(ReadOnlySpan<byte> serialized, string accessToken)
    {
        try
        {
            using var document = JsonDocument.Parse(serialized.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                throw new ExoPresenceProtocolException();
            }

            var type = typeElement.GetString();
            var receivedAt = _utcNow();
            if (type is "ready" or "ack")
            {
                if (!root.TryGetProperty("self", out var self) || self.ValueKind != JsonValueKind.Object)
                    throw new ExoPresenceProtocolException();
                return new ExoPresenceMessage(
                    type == "ready" ? ExoPresenceMessageKind.Ready : ExoPresenceMessageKind.Ack,
                    ParseEntry(self, requireAvailability: false, accessToken),
                    null,
                    null,
                    receivedAt);
            }

            if (type == "presence")
            {
                if (!root.TryGetProperty("presence", out var presence) || presence.ValueKind != JsonValueKind.Object)
                    throw new ExoPresenceProtocolException();
                return new ExoPresenceMessage(
                    ExoPresenceMessageKind.Presence,
                    ParseEntry(presence, requireAvailability: true, accessToken),
                    null,
                    null,
                    receivedAt);
            }

            if (type == "error")
            {
                return new ExoPresenceMessage(
                    ExoPresenceMessageKind.Error,
                    null,
                    Redact(ReadString(root, "code"), accessToken) ?? "PRESENCE_ERROR",
                    Redact(ReadString(root, "message"), accessToken) ?? "Presence request was rejected.",
                    receivedAt);
            }

            throw new ExoPresenceProtocolException();
        }
        catch (JsonException)
        {
            throw new ExoPresenceProtocolException();
        }
    }

    private void Publish(ExoPresenceMessage message)
    {
        var handlers = MessageReceived;
        if (handlers is null)
            return;
        foreach (Action<ExoPresenceMessage> handler in handlers.GetInvocationList())
        {
            try { handler(message); }
            catch { }
        }
    }

    private static TimeSpan ReconnectDelay(int failedAttempts)
    {
        var shift = Math.Min(failedAttempts, 5);
        return TimeSpan.FromSeconds(Math.Min(1 << shift, 30));
    }

    private static TimeSpan AddReconnectJitter(TimeSpan delay)
    {
        var multiplier = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromMilliseconds(delay.TotalMilliseconds * multiplier);
    }

    private static ExoPresenceEntry ParseEntry(
        JsonElement value,
        bool requireAvailability,
        string? redactionSecret)
    {
        var userId = Redact(ReadString(value, "userId"), redactionSecret) ?? string.Empty;
        var availability = ReadString(value, "availability");
        var isAvailable = !requireAvailability || string.Equals(availability, "available", StringComparison.Ordinal);
        var status = isAvailable ? MapStatus(ReadString(value, "status")) : ExoPresenceStatus.Unknown;
        var gameId = status == ExoPresenceStatus.InGame
            ? Redact(ReadBoundedString(value, "gameId", MaxGameIdLength), redactionSecret)
            : null;
        var gameTitle = status == ExoPresenceStatus.InGame
            ? Redact(ReadBoundedString(value, "gameTitle", MaxGameTitleLength), redactionSecret)
            : null;
        DateTimeOffset? lastSeen = null;
        var lastSeenText = ReadString(value, "lastSeen");
        if (DateTimeOffset.TryParse(
                lastSeenText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsedLastSeen))
        {
            lastSeen = parsedLastSeen;
        }

        return new ExoPresenceEntry(userId, status, gameId, gameTitle, lastSeen, isAvailable);
    }

    private static string? Redact(string? value, string? secret)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(secret))
            return value;
        return value.Replace(secret, "[redacted]", StringComparison.Ordinal);
    }

    private static string? NormalizeActivityField(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (normalized.Length > maxLength || normalized.Any(char.IsControl))
            throw new ArgumentException("Presence activity text is invalid.", parameterName);
        return normalized;
    }

    private static string MapStatus(string? status) => status switch
    {
        "online" => ExoPresenceStatus.Online,
        "away" => ExoPresenceStatus.Away,
        "in_game" => ExoPresenceStatus.InGame,
        "offline" => ExoPresenceStatus.Offline,
        _ => ExoPresenceStatus.Unknown,
    };

    private static string? ReadBoundedString(JsonElement value, string propertyName, int maxLength)
    {
        var text = ReadString(value, propertyName)?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > maxLength || text.Any(char.IsControl))
            return null;
        return text;
    }

    private static string? ReadString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;
        return property.GetString();
    }
}

internal enum ExoPresenceActivity
{
    Online,
    Away,
    InGame,
}

internal enum ExoPresenceMessageKind
{
    Ready,
    Ack,
    Presence,
    Error,
    TransportError,
}

internal sealed record ExoPresenceMessage(
    ExoPresenceMessageKind Kind,
    ExoPresenceEntry? Presence,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset ReceivedAt)
{
    public override string ToString() => nameof(ExoPresenceMessage);
}

internal static class ExoPresenceStatus
{
    public const string Unknown = "unknown";
    public const string Offline = "offline";
    public const string Online = "online";
    public const string Away = "away";
    public const string InGame = "ingame";
}

internal sealed record ExoPresenceEntry(
    string UserId,
    string Status,
    string? GameId,
    string? GameTitle,
    DateTimeOffset? LastSeen,
    bool Available)
{
    public override string ToString() => nameof(ExoPresenceEntry);
}

internal sealed record ExoPresenceRoster(IReadOnlyList<ExoPresenceEntry> Friends, bool Unavailable)
{
    public static ExoPresenceRoster ServiceUnavailable { get; } = new([], true);

    public override string ToString() => nameof(ExoPresenceRoster);
}

internal enum ExoPresenceFrameType
{
    Text,
    Binary,
    Close,
}

internal readonly record struct ExoPresenceReceiveResult(
    int Count,
    bool EndOfMessage,
    ExoPresenceFrameType FrameType);

internal interface IExoPresenceSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, string authorization, CancellationToken cancellationToken);

    ValueTask<ExoPresenceReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);

    ValueTask SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    ValueTask CloseAsync(CancellationToken cancellationToken);
}

internal sealed class ClientWebSocketPresenceSocket : IExoPresenceSocket
{
    private readonly ClientWebSocket _socket = new();

    public async Task ConnectAsync(Uri uri, string authorization, CancellationToken cancellationToken)
    {
        _socket.Options.SetRequestHeader("Authorization", authorization);
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ExoPresenceReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new ExoPresenceReceiveResult(
            result.Count,
            result.EndOfMessage,
            result.MessageType switch
            {
                WebSocketMessageType.Text => ExoPresenceFrameType.Text,
                WebSocketMessageType.Binary => ExoPresenceFrameType.Binary,
                _ => ExoPresenceFrameType.Close,
            });
    }

    public ValueTask SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "presence stopped",
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    public override string ToString() => nameof(ClientWebSocketPresenceSocket);
}

internal sealed class ExoPresenceProtocolException : Exception
{
    public ExoPresenceProtocolException()
        : base("Presence message is invalid.")
    {
    }
}
