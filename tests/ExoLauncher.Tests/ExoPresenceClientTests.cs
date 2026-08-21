using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ExoPresenceClientTests
{
    [Fact]
    public void RestFallback_MapsBackendStatesToLauncherStates()
    {
        var roster = ExoPresenceClient.ParseRestFallback(
            """
            {
              "friends": [
                {
                  "userId": "playing",
                  "status": "in_game",
                  "gameId": "steam:10",
                  "gameTitle": "Counter-Strike",
                  "lastSeen": "2026-08-19T20:00:00Z",
                  "availability": "available"
                },
                {
                  "userId": "hidden",
                  "status": "online",
                  "gameId": "private",
                  "gameTitle": "Private",
                  "lastSeen": null,
                  "availability": "unavailable"
                },
                { "userId": "missing", "availability": "available" },
                { "userId": "invalid", "status": "busy", "availability": "available" }
              ],
              "unavailable": false
            }
            """);

        Assert.False(roster.Unavailable);
        Assert.Collection(
            roster.Friends,
            playing =>
            {
                Assert.Equal("playing", playing.UserId);
                Assert.Equal("ingame", playing.Status);
                Assert.Equal("steam:10", playing.GameId);
                Assert.Equal("Counter-Strike", playing.GameTitle);
            },
            hidden =>
            {
                Assert.Equal("unknown", hidden.Status);
                Assert.Null(hidden.GameId);
                Assert.Null(hidden.GameTitle);
            },
            missing => Assert.Equal("unknown", missing.Status),
            invalid => Assert.Equal("unknown", invalid.Status));

        var unavailableRoster = ExoPresenceClient.ParseRestFallback(
            """{"friends":[{"userId":"live","status":"online","availability":"available"},{"userId":"hidden","status":"online","availability":"unavailable"}],"unavailable":true}""");
        Assert.True(unavailableRoster.Unavailable);
        Assert.Collection(
            unavailableRoster.Friends,
            live =>
            {
                Assert.Equal("online", live.Status);
                Assert.True(live.Available);
            },
            hidden =>
            {
                Assert.Equal("unknown", hidden.Status);
                Assert.False(hidden.Available);
            });
    }

    [Fact]
    public async Task Constructor_AllowsCleartextOnlyForLoopback()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExoPresenceClient(new Uri("ws://identity.example/v1/presence/socket")));

        await using var loopback = new ExoPresenceClient(
            new Uri("ws://127.0.0.1:8787/v1/presence/socket"));
        Assert.False(loopback.IsRunning);
    }

    [Fact]
    public async Task StartAsync_ConnectsOnlyWhenAsked_AndPublishesTypedMessages()
    {
        const string token = "presence-secret-fixture";
        var now = new DateTimeOffset(2026, 8, 19, 20, 30, 0, TimeSpan.Zero);
        var socket = new FakeSocket();
        socket.QueueText(
            """{"type":"ready","self":{"userId":"self","status":"online","gameId":null,"gameTitle":null,"lastSeen":null}}""");
        socket.QueueText(
            """{"type":"ack","self":{"userId":"self","status":"away","gameId":null,"gameTitle":null,"lastSeen":"2026-08-19T20:15:00Z"}}""");
        socket.QueueText(
            """{"type":"presence","presence":{"userId":"friend","status":"in_game","gameId":"steam:10","gameTitle":"Counter-Strike","lastSeen":"2026-08-19T20:00:00Z","availability":"available"}}""");
        var received = new ConcurrentQueue<ExoPresenceMessage>();

        await using var client = new ExoPresenceClient(
            new Uri("wss://identity.example/v1/presence/socket"),
            () => socket,
            NeverDelay,
            NeverDelay,
            _ => TimeSpan.Zero,
            () => now);
        client.MessageReceived += received.Enqueue;

        Assert.False(client.IsRunning);
        Assert.False(socket.Connected.Task.IsCompleted);

        await client.StartAsync(token);
        await socket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => received.Count == 3);

        Assert.True(client.IsRunning);
        Assert.Equal("wss://identity.example/v1/presence/socket", socket.ConnectedUri?.AbsoluteUri);
        Assert.Equal("Bearer " + token, socket.Authorization);
        Assert.Collection(
            received,
            ready =>
            {
                Assert.Equal(ExoPresenceMessageKind.Ready, ready.Kind);
                Assert.Equal("online", ready.Presence?.Status);
                Assert.Equal(now, ready.ReceivedAt);
            },
            ack =>
            {
                Assert.Equal(ExoPresenceMessageKind.Ack, ack.Kind);
                Assert.Equal("away", ack.Presence?.Status);
                Assert.Equal(now, ack.ReceivedAt);
            },
            presence =>
            {
                Assert.Equal(ExoPresenceMessageKind.Presence, presence.Kind);
                Assert.Equal("friend", presence.Presence?.UserId);
                Assert.Equal("ingame", presence.Presence?.Status);
                Assert.Equal("Counter-Strike", presence.Presence?.GameTitle);
                Assert.Equal(now, presence.ReceivedAt);
            });

        await client.StopAsync();
        Assert.False(client.IsRunning);
        Assert.Equal(1, socket.CloseCount);
    }

    [Fact]
    public async Task StatusAndHeartbeat_UseTheBoundedServerWireContract()
    {
        var socket = new FakeSocket();
        socket.QueueText(
            """{"type":"ready","self":{"userId":"self","status":"online","gameId":null,"gameTitle":null,"lastSeen":null}}""");
        var heartbeat = new ControlledDelay();
        await using var client = new ExoPresenceClient(
            new Uri("wss://identity.example/v1/presence/socket"),
            () => socket,
            NeverDelay,
            heartbeat.DelayAsync,
            _ => TimeSpan.Zero,
            () => DateTimeOffset.UnixEpoch);

        await client.StartAsync("token");
        await WaitUntilAsync(() => socket.Sent.Count == 1);
        Assert.Equal(
            """{"type":"status","status":"online","gameId":null,"gameTitle":null}""",
            socket.Sent.ElementAt(0));

        await client.SetStatusAsync(ExoPresenceActivity.InGame, " steam:10 ", " Counter-Strike ");
        await WaitUntilAsync(() => socket.Sent.Count == 2);
        Assert.Equal(
            """{"type":"status","status":"in_game","gameId":"steam:10","gameTitle":"Counter-Strike"}""",
            socket.Sent.ElementAt(1));

        await client.SetStatusAsync(ExoPresenceActivity.Away);
        await WaitUntilAsync(() => socket.Sent.Count == 3);
        Assert.Equal(
            """{"type":"status","status":"away","gameId":null,"gameTitle":null}""",
            socket.Sent.ElementAt(2));

        Assert.Equal(ExoPresenceClient.HeartbeatInterval, await heartbeat.NextDurationAsync());
        heartbeat.ReleaseNext();
        await WaitUntilAsync(() => socket.Sent.Count == 4);
        Assert.Equal("""{"type":"heartbeat"}""", socket.Sent.ElementAt(3));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SetStatusAsync(ExoPresenceActivity.Online, gameId: "steam:10"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SetStatusAsync(ExoPresenceActivity.InGame, gameId: new string('x', 129)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.SetStatusAsync((ExoPresenceActivity)99));

        await client.StopAsync();
    }

    [Fact]
    public async Task Reconnect_UsesExponentialDelays_AndReadyResetsTheSequence()
    {
        var failures = Enumerable.Range(1, 7)
            .Select(attempt => new FakeSocket { ConnectException = new IOException("failure " + attempt) })
            .ToArray();
        var ready = new FakeSocket();
        ready.QueueText(
            """{"type":"ready","self":{"userId":"self","status":"online","gameId":null,"gameTitle":null,"lastSeen":null}}""");
        ready.QueueClose();
        var hold = new FakeSocket { BlockConnect = true };
        var sockets = new ConcurrentQueue<IExoPresenceSocket>(
            [.. failures.Cast<IExoPresenceSocket>(), ready, hold]);
        var delays = new ConcurrentQueue<TimeSpan>();
        var jitterInputs = new ConcurrentQueue<TimeSpan>();

        await using var client = new ExoPresenceClient(
            new Uri("wss://identity.example/v1/presence/socket"),
            () => sockets.TryDequeue(out var socket) ? socket : throw new InvalidOperationException("socket fixture empty"),
            (delay, _) =>
            {
                delays.Enqueue(delay);
                return Task.CompletedTask;
            },
            NeverDelay,
            delay =>
            {
                jitterInputs.Enqueue(delay);
                return delay + TimeSpan.FromMilliseconds(250);
            },
            () => DateTimeOffset.UnixEpoch);

        await client.StartAsync("token");
        await WaitUntilAsync(() => delays.Count == 8 && hold.ConnectStarted.Task.IsCompleted);

        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(1250),
                TimeSpan.FromMilliseconds(2250),
                TimeSpan.FromMilliseconds(4250),
                TimeSpan.FromMilliseconds(8250),
                TimeSpan.FromMilliseconds(16250),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(1250),
            ],
            delays.ToArray());
        Assert.Equal(
            [
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(16),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(1),
            ],
            jitterInputs.ToArray());

        await client.StopAsync();
    }

    [Fact]
    public async Task IncomingMessage_OverFourKiB_IsRejectedBeforeJsonParsing()
    {
        var socket = new FakeSocket();
        socket.QueueText(
            "{\"type\":\"error\",\"code\":\"INVALID_MESSAGE\",\"message\":\"" +
            new string('x', ExoPresenceClient.MaxSocketMessageBytes) +
            "\"}");
        var received = new ConcurrentQueue<ExoPresenceMessage>();
        await using var client = new ExoPresenceClient(
            new Uri("wss://identity.example/v1/presence/socket"),
            () => socket,
            NeverDelay,
            NeverDelay,
            delay => delay,
            () => DateTimeOffset.UnixEpoch);
        client.MessageReceived += received.Enqueue;

        await client.StartAsync("token");
        await WaitUntilAsync(() => socket.CloseCount == 1);

        var error = Assert.Single(received);
        Assert.Equal(ExoPresenceMessageKind.Error, error.Kind);
        Assert.Equal("INVALID_MESSAGE", error.ErrorCode);
        Assert.Equal("Presence message is invalid.", error.ErrorMessage);
        Assert.Equal(4 * 1024, ExoPresenceClient.MaxSocketMessageBytes);

        await client.StopAsync();
    }

    [Fact]
    public async Task Lifecycle_StartCancellationDoesNotOwnTheRun_AndStartIsNotImplicitOrDuplicate()
    {
        var socket = new FakeSocket();
        using var lifetime = new CancellationTokenSource();
        var postCancellationMessage = new TaskCompletionSource<ExoPresenceMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new ExoPresenceClient(
            new Uri("wss://identity.example/v1/presence/socket"),
            () => socket,
            NeverDelay,
            NeverDelay,
            delay => delay,
            () => DateTimeOffset.UnixEpoch);
        client.MessageReceived += message => postCancellationMessage.TrySetResult(message);

        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.StartAsync("token", alreadyCanceled.Token));
        Assert.False(socket.ConnectStarted.Task.IsCompleted);

        await client.StartAsync("token", lifetime.Token);
        await socket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync("token"));

        lifetime.Cancel();
        socket.QueueText(
            """{"type":"error","code":"INVALID_MESSAGE","message":"still connected"}""");
        var afterCancellation = await postCancellationMessage.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ExoPresenceMessageKind.Error, afterCancellation.Kind);
        Assert.True(client.IsRunning);
        Assert.False(socket.ReceiveCanceled.Task.IsCompleted);
        await client.StopAsync();
        await socket.ReceiveCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.StopAsync();
        Assert.False(client.IsRunning);
    }

    [Fact]
    public async Task AccessToken_IsAbsentFromEventsResultsToStringAndPublicExceptions()
    {
        const string token = "raw-presence-token-fixture";
        var socket = new FakeSocket();
        socket.QueueText(
            "{\"type\":\"error\",\"code\":\"BAD_" + token +
            "\",\"message\":\"server reflected " + token + "\"}");
        var received = new ConcurrentQueue<ExoPresenceMessage>();
        await using var client = new ExoPresenceClient(
            new Uri("wss://identity.example/v1/presence/socket"),
            () => socket,
            NeverDelay,
            NeverDelay,
            delay => delay,
            () => DateTimeOffset.UnixEpoch,
            _ => Task.FromResult(
                "{\"friends\":[{\"userId\":\"" + token +
                "\",\"status\":\"in_game\",\"gameTitle\":\"" + token +
                "\",\"availability\":\"available\"}],\"unavailable\":false}"));
        client.MessageReceived += received.Enqueue;

        await client.StartAsync(token);
        await WaitUntilAsync(() => received.Count == 1);
        var serverError = Assert.Single(received);

        Assert.DoesNotContain(token, serverError.ErrorCode, StringComparison.Ordinal);
        Assert.DoesNotContain(token, serverError.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(token, serverError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(serverError), StringComparison.Ordinal);
        Assert.DoesNotContain(token, client.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            token,
            JsonSerializer.Serialize(await client.GetRestFallbackAsync()),
            StringComparison.Ordinal);
        await client.StopAsync();

        var failingSend = new FakeSocket
        {
            SendException = new IOException("transport accidentally included " + token),
            ThrowOnSendNumber = 2,
        };
        failingSend.QueueText(
            """{"type":"ready","self":{"userId":"self","status":"online","gameId":null,"gameTitle":null,"lastSeen":null}}""");
        await using var secondClient = new ExoPresenceClient(
            new Uri("wss://identity.example/v1/presence/socket"),
            () => failingSend,
            NeverDelay,
            NeverDelay,
            delay => delay,
            () => DateTimeOffset.UnixEpoch);
        await secondClient.StartAsync(token);
        await WaitUntilAsync(() => failingSend.Sent.Count == 1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            secondClient.SetStatusAsync(ExoPresenceActivity.Away));
        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
        await secondClient.StopAsync();

        using var stopCancellation = new CancellationTokenSource();
        var failingClose = new FakeSocket
        {
            CloseException = new IOException("close accidentally included " + token),
            OnClose = stopCancellation.Cancel,
        };
        await using var thirdClient = new ExoPresenceClient(
            new Uri("wss://identity.example/v1/presence/socket"),
            () => failingClose,
            NeverDelay,
            NeverDelay,
            delay => delay,
            () => DateTimeOffset.UnixEpoch);
        await thirdClient.StartAsync(token);
        await failingClose.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var canceledStop = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            thirdClient.StopAsync(stopCancellation.Token));
        Assert.DoesNotContain(token, canceledStop.ToString(), StringComparison.Ordinal);
        await thirdClient.StopAsync();
    }

    [Fact]
    public async Task RestFallback_UsesInjectedGetDataWithoutOwningHttpTransport()
    {
        var socket = new FakeSocket();
        var calls = 0;
        await using var client = new ExoPresenceClient(
            new Uri("wss://identity.example/v1/presence/socket"),
            () => socket,
            NeverDelay,
            NeverDelay,
            delay => delay,
            () => DateTimeOffset.UnixEpoch,
            _ =>
            {
                calls++;
                return Task.FromResult(
                    """{"friends":[{"userId":"friend","status":"away","gameId":null,"gameTitle":null,"lastSeen":null,"availability":"available"}],"unavailable":false}""");
            });

        var roster = await client.GetRestFallbackAsync();

        Assert.Equal(1, calls);
        Assert.Equal("away", Assert.Single(roster.Friends).Status);
        Assert.False(socket.ConnectStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task RestFallback_RedactsWithTheStartingToken_WhenStopCompletesDuringTheGet()
    {
        const string token = "fallback-stop-race-token";
        var socket = new FakeSocket();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        await using var client = new ExoPresenceClient(
            new Uri("wss://identity.example/v1/presence/socket"),
            () => socket,
            NeverDelay,
            NeverDelay,
            delay => delay,
            () => DateTimeOffset.UnixEpoch,
            _ =>
            {
                requestCount++;
                requested.TrySetResult();
                return response.Task;
            });
        await client.StartAsync(token);
        await socket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var fallbackTask = client.GetRestFallbackAsync();
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.StopAsync();
        response.TrySetResult(
            "{\"friends\":[{\"userId\":\"" + token +
            "\",\"status\":\"in_game\",\"gameTitle\":\"" + token +
            "\",\"availability\":\"available\"}],\"unavailable\":false}");

        var roster = await fallbackTask;
        Assert.DoesNotContain(token, JsonSerializer.Serialize(roster), StringComparison.Ordinal);

        var afterStop = await client.GetRestFallbackAsync();
        Assert.True(afterStop.Unavailable);
        Assert.Empty(afterStop.Friends);
        Assert.Equal(1, requestCount);
    }

    private static Task NeverDelay(TimeSpan _, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeSocket : IExoPresenceSocket
    {
        private readonly Channel<FakeFrame> _frames = Channel.CreateUnbounded<FakeFrame>();
        private FakeFrame? _current;
        private int _offset;

        public TaskCompletionSource Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReceiveCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Sent { get; } = new();
        public Uri? ConnectedUri { get; private set; }
        public string? Authorization { get; private set; }
        public int CloseCount { get; private set; }
        public Exception? ConnectException { get; init; }
        public bool BlockConnect { get; init; }
        public Exception? SendException { get; init; }
        public int ThrowOnSendNumber { get; init; }
        public Exception? CloseException { get; init; }
        public Action? OnClose { get; init; }
        private int _sendCount;

        public void QueueText(string text) =>
            _frames.Writer.TryWrite(new FakeFrame(Encoding.UTF8.GetBytes(text), ExoPresenceFrameType.Text));

        public void QueueClose() =>
            _frames.Writer.TryWrite(new FakeFrame([], ExoPresenceFrameType.Close));

        public async Task ConnectAsync(Uri uri, string authorization, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectedUri = uri;
            Authorization = authorization;
            ConnectStarted.TrySetResult();
            if (ConnectException is not null)
                throw ConnectException;
            if (BlockConnect)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Connected.TrySetResult();
        }

        public async ValueTask<ExoPresenceReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            try
            {
                _current ??= await _frames.Reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ReceiveCanceled.TrySetResult();
                throw;
            }
            var remaining = _current.Payload.Length - _offset;
            var count = Math.Min(buffer.Length, remaining);
            _current.Payload.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            var end = _offset == _current.Payload.Length;
            var kind = _current.Kind;
            if (end)
            {
                _current = null;
                _offset = 0;
            }
            return new ExoPresenceReceiveResult(count, end, kind);
        }

        public ValueTask SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sendNumber = Interlocked.Increment(ref _sendCount);
            if (SendException is not null && sendNumber == ThrowOnSendNumber)
                throw SendException;
            Sent.Enqueue(Encoding.UTF8.GetString(payload.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            OnClose?.Invoke();
            if (CloseException is not null)
                throw CloseException;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed record FakeFrame(byte[] Payload, ExoPresenceFrameType Kind);
    }

    private sealed class ControlledDelay
    {
        private readonly Channel<DelayCall> _calls = Channel.CreateUnbounded<DelayCall>();
        private DelayCall? _current;

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            _ = completion.Task.ContinueWith(
                _ => registration.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _calls.Writer.TryWrite(new DelayCall(duration, completion));
            return completion.Task;
        }

        public async Task<TimeSpan> NextDurationAsync()
        {
            _current = await _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            return _current.Duration;
        }

        public void ReleaseNext()
        {
            Assert.NotNull(_current);
            _current!.Completion.TrySetResult();
            _current = null;
        }

        private sealed record DelayCall(TimeSpan Duration, TaskCompletionSource Completion);
    }
}
