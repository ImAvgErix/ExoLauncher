namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Discord closes a gateway connection that sends more than 120 messages in 60 seconds (close 4008).
/// Exo keeps under it with a sliding window of 100 per connection, leaving room for heartbeats.
/// </summary>
internal sealed class GatewaySendLimit(int perWindow = 100, long windowMs = 60_000)
{
    private readonly Queue<long> _sent = new();
    private readonly Lock _gate = new();

    /// <summary>0 when a send may go now (it is counted); otherwise the milliseconds until one may.</summary>
    internal long Take(long now)
    {
        lock (_gate)
        {
            while (_sent.Count > 0 && now - _sent.Peek() >= windowMs) _sent.Dequeue();
            if (_sent.Count < perWindow) { _sent.Enqueue(now); return 0; }
            return windowMs - (now - _sent.Peek());
        }
    }

    /// <summary>A new connection starts with the whole allowance.</summary>
    internal void Reset()
    {
        lock (_gate) _sent.Clear();
    }
}
