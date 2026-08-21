using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Coalesces library.updated publishes. A CTS is cancelled immediately when a
/// newer scan lands, but it is disposed only after the in-flight task releases
/// it. Cancel, dispose, and ObjectDisposedException stay inside this boundary
/// and a snapshot is still delivered so Play/Apply chrome stays mounted.
/// </summary>
internal sealed class LibraryUpdatedPublisher : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(280);

    private readonly Func<CancellationToken, Task> _publish;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private int _inFlight;
    private bool _disposed;

    public LibraryUpdatedPublisher(Func<CancellationToken, Task> publish)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    public void Request()
    {
        CancellationTokenSource cts;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed) return;
            previous = _cts;
            cts = new CancellationTokenSource();
            _cts = cts;
        }

        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { /* already gone */ }
        catch { /* Cancel must never escape the event thread */ }

        Interlocked.Increment(ref _inFlight);
        _ = RunAsync(cts, previous);
    }

    private async Task RunAsync(CancellationTokenSource cts, CancellationTokenSource? previous)
    {
        try
        {
            try
            {
                await Task.Delay(Debounce, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                /* coalesced or disposed */
            }
            catch (ObjectDisposedException)
            {
                /* CTS disposed under Delay */
            }

            var shouldPublish = false;
            lock (_gate)
                shouldPublish = _disposed || ReferenceEquals(_cts, cts);

            if (shouldPublish)
            {
                try
                {
                    await _publish(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    /* publish itself cancelled */
                }
                catch (ObjectDisposedException ex)
                {
                    AppLog.Debug("library.updated failed: " + ex.Message);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("library.updated failed: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("library.updated failed: " + ex.Message);
        }
        finally
        {
            try { previous?.Dispose(); } catch { /* already disposed */ }
            Interlocked.Decrement(ref _inFlight);
            var disposeSelf = false;
            lock (_gate)
                disposeSelf = !ReferenceEquals(_cts, cts);
            if (disposeSelf)
            {
                try { cts.Dispose(); } catch { /* already disposed */ }
            }
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? current;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            current = _cts;
            _cts = null;
        }

        try { current?.Cancel(); } catch { /* already disposed */ }
        if (Volatile.Read(ref _inFlight) == 0)
        {
            try { current?.Dispose(); } catch { /* already disposed */ }
        }
    }
}
