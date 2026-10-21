using System.Collections.Concurrent;

namespace ExoLauncher.Services;

/// <summary>Owns finite requests. Launcher/installer jobs deliberately retain their own service lifetime.</summary>
internal sealed class BridgeRequestRegistry
{
    private readonly ConcurrentDictionary<string, Lease> _requests = new(StringComparer.Ordinal);

    internal static TimeSpan? Deadline(string method) => method switch
    {
        "library.get" or "library.refresh" or "dlss.status" => TimeSpan.FromMinutes(2),
        "app.checkUpdate" => TimeSpan.FromSeconds(30),
        "dlss.updateAll" or "shaders.call" => TimeSpan.FromMinutes(10),
        _ => null,
    };

    internal Lease? Begin(string id, string method, CancellationToken lifetime)
    {
        if (Deadline(method) is not { } deadline) return null;
        var lease = new Lease(this, id, lifetime, deadline);
        if (_requests.TryAdd(id, lease)) return lease;
        lease.Dispose();
        throw new InvalidOperationException("A request with this identifier is already active.");
    }

    internal bool Cancel(string id) => _requests.TryGetValue(id, out var lease) && lease.Cancel();
    internal void CancelAll() { foreach (var lease in _requests.Values) lease.Cancel(); }

    internal sealed class Lease : IDisposable
    {
        private readonly BridgeRequestRegistry _owner;
        private readonly string _id;
        private readonly CancellationTokenSource _source;
        private readonly object _gate = new();
        private bool _disposed;
        internal CancellationToken Token { get; }
        internal Lease(BridgeRequestRegistry owner, string id, CancellationToken lifetime, TimeSpan deadline)
        {
            _owner = owner; _id = id;
            _source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            Token = _source.Token; _source.CancelAfter(deadline);
        }
        internal bool Cancel()
        {
            lock (_gate)
            {
                if (_disposed) return false;
                _source.Cancel(); return true;
            }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                ((ICollection<KeyValuePair<string, Lease>>)_owner._requests).Remove(new(_id, this));
                _source.Dispose();
            }
        }
    }
}
