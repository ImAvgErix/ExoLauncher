using System.Collections.Concurrent;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Keeps vendor clients as invisible Exo dependencies for the lifetime of the app.
/// This is an in-process guard, not a tray agent or a second user-facing process.
/// </summary>
public sealed class HiddenStoreRuntime : IDisposable
{
    private readonly object _gate = new();
    private readonly StoreAudioSilencer _audioSilencer = new(
        store => IsExoDriving && !IsSuspended(store));
    private Timer? _timer;
    private bool _disposed;

    /// <summary>Store → UTC time until which suppression is paused.</summary>
    private static readonly ConcurrentDictionary<StoreKind, DateTimeOffset> s_suspended = new();

    /// <summary>
    /// Suppression only applies while Exo is driving something.
    ///
    /// Sweeping unconditionally meant a client the user opened deliberately was
    /// hidden again within 250ms, so Steam could not be opened at all outside
    /// Exo. Exo should never *cause* a client to appear, but it must not fight
    /// the user either.
    /// </summary>
    private static int s_activeOperations;

    private static readonly object s_operationGate = new();
    private static DateTimeOffset s_quietUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// Providers currently launching or running a game through Exo. Cleanup
    /// takes this same gate before acting, so a sibling launch cannot become
    /// active between the "unused" check and an exact process termination.
    /// </summary>
    private static readonly object s_providerGate = new();
    private static readonly Dictionary<StoreKind, int> s_activeGameProviders = new();

    public static IDisposable Operation(TimeSpan? grace = null) =>
        new OperationScope(grace ?? TimeSpan.FromSeconds(20));

    /// <summary>
    /// Keep every vendor launcher surface suppressed for the complete detected
    /// game session. The owner must dispose this scope when the game exits (or
    /// when the handoff times out). Unlike a short launch operation there is no
    /// long trailing grace period because the session watcher has already
    /// debounced the game exit.
    /// </summary>
    public static IDisposable GameSession(StoreKind activeProvider) =>
        new GameSessionScope(activeProvider);

    internal static bool IsGameProviderActive(StoreKind provider)
    {
        lock (s_providerGate)
            return s_activeGameProviders.TryGetValue(provider, out var count) && count > 0;
    }

    /// <summary>
    /// Run one short cleanup action only while the provider is not launching
    /// or running a game. Provider registration uses the same lock, closing the
    /// race where a second launch could become active during cleanup.
    /// </summary>
    internal static bool TryWhileGameProviderInactive(StoreKind provider, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (s_providerGate)
        {
            if (s_activeGameProviders.TryGetValue(provider, out var count) && count > 0)
                return false;
            action();
            return true;
        }
    }

    public static bool IsExoDriving
    {
        get
        {
            if (Volatile.Read(ref s_activeOperations) > 0) return true;
            lock (s_operationGate) return DateTimeOffset.UtcNow < s_quietUntil;
        }
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly TimeSpan _grace;
        private bool _done;

        public OperationScope(TimeSpan grace)
        {
            _grace = grace;
            Interlocked.Increment(ref s_activeOperations);
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            // Clients can surface a window shortly after a handoff completes.
            var requestedQuietUntil = DateTimeOffset.UtcNow + _grace;
            lock (s_operationGate)
            {
                // One scope finishing must never shorten another operation's
                // grace period (or a concurrently ending game session).
                if (requestedQuietUntil > s_quietUntil)
                    s_quietUntil = requestedQuietUntil;
            }
            Interlocked.Decrement(ref s_activeOperations);
        }
    }

    private sealed class GameSessionScope : IDisposable
    {
        private readonly StoreKind _provider;
        private readonly OperationScope _operation;
        private readonly StoreWindowHider? _windowGuard;
        private bool _done;

        public GameSessionScope(StoreKind provider)
        {
            _provider = provider;
            lock (s_providerGate)
            {
                s_activeGameProviders.TryGetValue(provider, out var count);
                s_activeGameProviders[provider] = checked(count + 1);
            }

            try
            {
                _operation = new OperationScope(TimeSpan.Zero);
                // Polling is a fallback; this session-scoped hook hides a new
                // Steam message / Epic / GOG notification / Riot client HWND
                // at the create/show event while Exo owns the game session.
                _windowGuard = StoreWindowHider.ForAllStoreChrome();
                _windowGuard.StartUntilStopped(restoreOnStop: true);
            }
            catch
            {
                try { _windowGuard?.Dispose(); } catch { /* */ }
                RemoveActiveProvider(provider);
                throw;
            }
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            try { _windowGuard?.Dispose(); } catch { /* */ }
            RemoveActiveProvider(_provider);
            _operation.Dispose();
        }
    }

    private static void RemoveActiveProvider(StoreKind provider)
    {
        lock (s_providerGate)
        {
            if (!s_activeGameProviders.TryGetValue(provider, out var count)) return;
            if (count <= 1)
                s_activeGameProviders.Remove(provider);
            else
                s_activeGameProviders[provider] = count - 1;
        }
    }

    /// <summary>
    /// Stop hiding a vendor client for a while.
    /// Some steps genuinely require the vendor's own window — Riot sign-in, a
    /// Steam login prompt. Suppressing those leaves the user staring at a launch
    /// that can never finish, so Exo surfaces the client and says why instead.
    /// </summary>
    public static void SuspendFor(StoreKind store, TimeSpan duration)
    {
        var until = DateTimeOffset.UtcNow + duration;
        s_suspended.AddOrUpdate(store, until, (_, existing) => existing > until ? existing : until);
    }

    /// <summary>Re-hide a store immediately (the interaction finished).</summary>
    public static void Resume(StoreKind store) => s_suspended.TryRemove(store, out _);

    public static bool IsSuspended(StoreKind store) =>
        s_suspended.TryGetValue(store, out var until) && DateTimeOffset.UtcNow < until;

    /// <summary>
    /// Dynamic gate for the all-store WinEvent guard. An explicit Settings →
    /// Open Store / required sign-in surface must not be hidden again while a
    /// game session is still active.
    /// </summary>
    internal static bool IsStoreSurfaceSuppressed(string processName)
    {
        if (StoreWindowHider.SteamMainProcessNames.Concat(["steamerrorreporter"])
            .Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return !IsSuspended(StoreKind.Steam);
        if (StoreWindowHider.EpicProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return !IsSuspended(StoreKind.Epic);
        if (StoreWindowHider.GalaxyProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return !IsSuspended(StoreKind.Gog);
        if (StoreWindowHider.RiotUiProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return !IsSuspended(StoreKind.Riot);
        return false;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null) return;
            _audioSilencer.Start();
            Sweep();
            _timer = new Timer(
                static state => ((HiddenStoreRuntime)state!).Sweep(),
                this,
                dueTime: TimeSpan.FromMilliseconds(100),
                period: TimeSpan.FromMilliseconds(250));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _audioSilencer.Dispose();
        }
    }

    private void Sweep()
    {
        // The audio worker owns restoration. Wake it promptly when the last
        // Exo operation ends rather than leaving a client muted until its next
        // periodic scan.
        _audioSilencer.Refresh();
        if (!IsExoDriving) return;
        try
        {
            SweepStore(StoreKind.Steam, StoreWindowHider.SteamProcessNames);
            SweepStore(StoreKind.Epic, StoreWindowHider.EpicProcessNames);
            SweepStore(StoreKind.Gog, StoreWindowHider.GalaxyProcessNames);
            SweepStore(StoreKind.Riot, StoreWindowHider.RiotUiProcessNames);
        }
        catch
        {
            // A store can start or exit while its HWNDs are being enumerated.
        }
    }

    private static void SweepStore(StoreKind store, string[] processNames)
    {
        if (IsSuspended(store)) return;
        StoreWindowHider.CollapseOrphanSurfaces(processNames);
    }
}
