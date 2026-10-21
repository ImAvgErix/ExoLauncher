namespace ExoLauncher.Helpers;

/// <summary>Coalesces writes without putting directory reconciliation on the writer's thread.</summary>
internal sealed class CoalescedCacheMaintenance(Func<(int Files, long Bytes)> reconcile, TimeSpan delay)
{
    private readonly object _gate = new();
    private Task? _pending;
    private int _writes;

    internal Task Notify()
    {
        lock (_gate)
        {
            _writes = Math.Min(int.MaxValue - 1, _writes) + 1;
            return _pending ??= Task.Run(PumpAsync);
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            lock (_gate) _writes = 0;
            try { reconcile(); }
            catch { /* A later write retries maintenance; saved artwork remains usable. */ }
            lock (_gate)
            {
                if (_writes != 0) continue;
                _pending = null;
                return;
            }
        }
    }
}
