namespace ExoLauncher.Adapters;

/// <summary>One owner for IPC/fallback publication, scoped to a single watch operation.</summary>
internal sealed class SteamProgressSource(Func<long>? clock = null)
{
    private readonly object _gate = new();
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private long? _observedAt;
    private long _sequence;
    private bool _alive;
    private bool _ended;
    private string? _sampleKey;

    internal bool ShouldUseFallback
    {
        get { lock (_gate) return !_alive || _observedAt is null || _clock() - _observedAt.Value >= 3000; }
    }

    internal void End() { lock (_gate) { _ended = true; _alive = false; _observedAt = null; } }

    internal void ReportHelper(long sequence, bool valid, Action publish, string? sampleKey = null)
    {
        lock (_gate)
        {
            if (_ended || sequence <= _sequence) return;
            _sequence = sequence;
            _alive = true;
            // A heartbeat proves the pipe is alive; repeated counters do not
            // prove they still describe the current download/patching phase.
            if (!valid) _observedAt = null;
            else if (sampleKey is null || sampleKey != _sampleKey || _observedAt is null)
                _observedAt = _clock();
            _sampleKey = sampleKey;
            if (valid && !ShouldUseFallback) publish();
        }
    }

    internal void ReportFallback(Action publish)
    {
        lock (_gate) if (ShouldUseFallback) publish();
    }
}
