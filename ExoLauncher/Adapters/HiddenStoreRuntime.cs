using System.Collections.Concurrent;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Keeps vendor handoffs quiet while tracking providers needed by running games.
/// This is an in-process guard, not a tray agent or a second user-facing process.
/// </summary>
public sealed class HiddenStoreRuntime : IDisposable
{
    private readonly object _gate = new();
    private readonly StoreAudioSilencer _audioSilencer = new(
        store => IsStoreHandoffActive(store) && !IsSuspended(store));
    private Timer? _timer;
    private int _audioWakeState;
    private bool _disposed;
    private static event Action? HandoffChanged;

    /// <summary>Store → UTC time until which suppression is paused.</summary>
    private static readonly ConcurrentDictionary<StoreKind, DateTimeOffset> s_suspended = new();
    private static readonly ConcurrentDictionary<StoreKind, DateTimeOffset> s_userOpened = new();
    private long _nextUserWindowCheck;
    private long _nextTrayCheck;

    /// <summary>
    /// Clients Exo started or woke, by when: they stay quiet for as long as
    /// they run, not only for the handoff's seconds, until the user opens one.
    /// </summary>
    private static readonly ConcurrentDictionary<StoreKind, DateTimeOffset> s_owned = new();
    private StoreWindowHider? _sessionGuard;

    /// <summary>A deliberate Open remains visible while its main window is open.</summary>
    internal static void AllowUserOpen(StoreKind store)
    {
        s_userOpened[store] = DateTimeOffset.UtcNow.AddSeconds(30);
        if (s_owned.TryRemove(store, out _))
            _ = Task.Run(() => StoreTrayIcons.Restore(store));
        HandoffChanged?.Invoke();
    }

    internal static bool IsExoOwned(StoreKind store) => s_owned.ContainsKey(store);

    /// <summary>Exo keeps this client's windows hidden: a handoff now, or a client Exo runs.</summary>
    internal static bool IsStoreQuiet(StoreKind store) => IsStoreHandoffActive(store) || s_owned.ContainsKey(store);

    internal static bool IsAnyStoreQuiet => IsHandoffActive || !s_owned.IsEmpty;

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
    internal static readonly TimeSpan HandoffDuration = TimeSpan.FromSeconds(12);
    private static DateTimeOffset s_handoffUntil = DateTimeOffset.MinValue;
    private static readonly ConcurrentDictionary<StoreKind, DateTimeOffset> s_storeHandoffs = new();

    internal static bool IsHandoffActive
    {
        get { lock (s_operationGate) return DateTimeOffset.UtcNow < s_handoffUntil; }
    }

    internal static bool IsStoreHandoffActive(StoreKind store) =>
        s_storeHandoffs.TryGetValue(store, out var until) && DateTimeOffset.UtcNow < until;

    internal static void BeginHandoff(IEnumerable<string> processNames, bool own = true)
    {
        // A client already open belongs to the user. A client Exo starts or
        // wakes for this handoff is Exo's until it exits or the user opens it.
        var visible = StoreWindowHider.VisibleMainStores(Enum.GetValues<StoreKind>());
        foreach (var store in visible)
            AllowUserOpen(store);
        var now = DateTimeOffset.UtcNow;
        var until = now + HandoffDuration;
        foreach (var name in processNames)
            if (StoreForProcess(name) is { } store)
            {
                s_storeHandoffs[store] = until;
                if (own && !visible.Contains(store)) s_owned.TryAdd(store, now);
            }
        lock (s_operationGate) s_handoffUntil = until;
        HandoffChanged?.Invoke();
    }

    /// <summary>
    /// Providers currently launching or running a game through Exo. Cleanup
    /// takes this same gate before acting, so a sibling launch cannot become
    /// active between the "unused" check and an exact process termination.
    /// </summary>
    private static readonly object s_providerGate = new();
    private static readonly Dictionary<StoreKind, int> s_activeGameProviders = new();

    public static IDisposable Operation(TimeSpan? grace = null) =>
        new OperationScope(grace ?? TimeSpan.Zero);

    /// <summary>
    /// Protect a game's provider from unused-client cleanup until the game exits.
    /// A running game does not own launcher windows: users may reopen any client.
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
        private bool _done;

        /// <summary>
        /// The EA app, Ubisoft Connect, Battle.net and the other clients a
        /// launch hands its game to by link: hidden from the handoff on, like
        /// the clients whose adapters guard their own launches.
        /// </summary>
        private readonly StoreWindowHider? _handoff;

        public GameSessionScope(StoreKind provider)
        {
            _provider = provider;
            lock (s_providerGate)
            {
                s_activeGameProviders.TryGetValue(provider, out var count);
                s_activeGameProviders[provider] = checked(count + 1);
            }
            if (provider is not (StoreKind.Steam or StoreKind.Epic or StoreKind.Gog or StoreKind.Riot))
            {
                _handoff = StoreWindowHider.For(provider);
                _handoff?.Start(HandoffDuration);
            }
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _handoff?.Dispose();
            RemoveActiveProvider(_provider);
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

    public static bool IsSuspended(StoreKind store) =>
        s_suspended.TryGetValue(store, out var until) && DateTimeOffset.UtcNow < until ||
        s_userOpened.TryGetValue(store, out var userUntil) && DateTimeOffset.UtcNow < userUntil;

    /// <summary>
    /// Dynamic gate for the all-store WinEvent guard. An explicit Settings →
    /// Open Store / required sign-in surface must not be hidden again while a
    /// game session is still active.
    /// </summary>
    internal static bool IsStoreSurfaceSuppressed(string processName)
    {
        var store = StoreForProcess(processName);
        return store is { } kind && IsStoreQuiet(kind) && !IsSuspended(kind);
    }

    internal static StoreKind? StoreForProcess(string processName)
    {
        if (StoreWindowHider.SteamMainProcessNames.Concat(["steamerrorreporter"])
            .Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Steam;
        if (StoreWindowHider.EpicProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Epic;
        if (StoreWindowHider.GalaxyProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Gog;
        if (StoreWindowHider.RiotUiProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Riot;
        if (StoreWindowHider.XboxClientProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Xbox;
        if (StoreWindowHider.EaClientProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Ea;
        if (StoreWindowHider.UbisoftClientProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Ubisoft;
        if (StoreWindowHider.BattleNetClientProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.BattleNet;
        if (StoreWindowHider.AmazonClientProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Amazon;
        if (StoreWindowHider.RockstarClientProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Rockstar;
        if (StoreWindowHider.ItchClientProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Itch;
        if (StoreWindowHider.MinecraftClientProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Minecraft;
        if (StoreWindowHider.RobloxClientProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Roblox;
        if (StoreWindowHider.ParadoxClientProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Paradox;
        if (StoreWindowHider.WargamingClientProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return StoreKind.Wargaming;
        return null;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null) return;
            _audioSilencer.Start();
            _timer = new Timer(
                static state => ((HiddenStoreRuntime)state!).Sweep(),
                this,
                dueTime: Timeout.InfiniteTimeSpan,
                period: Timeout.InfiniteTimeSpan);
            HandoffChanged += WakeForHandoff;
            WakeForHandoff();
        }
    }

    private void WakeForHandoff()
    {
        lock (_gate)
            if (!_disposed) _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            HandoffChanged -= WakeForHandoff;
            _timer?.Dispose();
            _timer = null;
            _audioSilencer.Dispose();
            _sessionGuard?.Dispose();
            _sessionGuard = null;
        }
        // Clients left running after Exo closes get their tray icons back.
        try { StoreTrayIcons.RestoreAll(); } catch { /* best-effort */ }
    }

    private int _sweeping;

    private void Sweep()
    {
        if (_disposed || Interlocked.Exchange(ref _sweeping, 1) != 0) return;
        try { SweepCore(); }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
            lock (_gate)
                if (!_disposed) _timer?.Change(IsHandoffActive ? TimeSpan.FromMilliseconds(250)
                    : !s_userOpened.IsEmpty || !s_owned.IsEmpty ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void SweepCore()
    {
        if (!s_userOpened.IsEmpty && Environment.TickCount64 >= _nextUserWindowCheck)
        {
            _nextUserWindowCheck = Environment.TickCount64 + 1000;
            var visible = StoreWindowHider.VisibleMainStores(s_userOpened.Keys);
            foreach (var entry in s_userOpened.ToArray())
            {
                if (visible.Contains(entry.Key)) AllowUserOpen(entry.Key);
                else if (DateTimeOffset.UtcNow >= entry.Value) s_userOpened.TryRemove(entry);
            }
        }
        var driving = IsHandoffActive;
        var wasDriving = Interlocked.Exchange(ref _audioWakeState, driving ? 1 : 0) != 0;
        // The audio worker owns restoration. Wake throughout active operations,
        // and once more on the active → idle edge so Exo-owned mutes are promptly
        // restored. Pure idle ticks no longer wake the Core Audio worker.
        if (driving || wasDriving)
            _audioSilencer.Refresh();
        KeepOwnedClientsQuiet();
        if (!IsAnyStoreQuiet) return;
        try
        {
            (StoreKind Store, string[] Names)[] groups =
            [
                (StoreKind.Steam, StoreWindowHider.SteamProcessNames),
                (StoreKind.Epic, StoreWindowHider.EpicProcessNames),
                (StoreKind.Gog, StoreWindowHider.GalaxyProcessNames),
                (StoreKind.Riot, StoreWindowHider.RiotUiProcessNames),
                (StoreKind.Xbox, StoreWindowHider.XboxClientProcessNames),
                (StoreKind.Ea, StoreWindowHider.EaClientProcessNames),
                (StoreKind.Ubisoft, StoreWindowHider.UbisoftClientProcessNames),
                (StoreKind.BattleNet, StoreWindowHider.BattleNetClientProcessNames),
                (StoreKind.Amazon, StoreWindowHider.AmazonClientProcessNames),
                (StoreKind.Rockstar, StoreWindowHider.RockstarClientProcessNames),
                (StoreKind.Itch, StoreWindowHider.ItchClientProcessNames),
                (StoreKind.Minecraft, StoreWindowHider.MinecraftClientProcessNames),
                (StoreKind.Roblox, StoreWindowHider.RobloxClientProcessNames),
                (StoreKind.Paradox, StoreWindowHider.ParadoxClientProcessNames),
                (StoreKind.Wargaming, StoreWindowHider.WargamingClientProcessNames),
            ];
            var active = groups.Where(group => IsStoreQuiet(group.Store) && !IsSuspended(group.Store)).ToArray();
            var snapshot = StoreWindowHider.CaptureSurfaces(active.SelectMany(group => group.Names));
            foreach (var (store, names) in active)
                SweepStore(store, names, snapshot);
        }
        catch
        {
            // A store can start or exit while its HWNDs are being enumerated.
        }
    }

    /// <summary>
    /// Ends ownership of a client that has exited, keeps the window guard's
    /// hook running while Exo owns any client, and takes owned clients' tray
    /// icons away (again after a client or Explorer re-adds them).
    /// </summary>
    private void KeepOwnedClientsQuiet()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (store, since) in s_owned.ToArray())
        {
            if (now - since < HandoffDuration) continue; // a client Exo is starting may not be up yet
            if (!StoreClientCleanup.ExitProcessNames(store).Any(ProcessHelper.IsProcessRunning))
                s_owned.TryRemove(store, out _);
        }
        lock (_gate)
        {
            if (_disposed) return;
            if (s_owned.IsEmpty) { _sessionGuard?.Dispose(); _sessionGuard = null; }
            else if (_sessionGuard is null) { _sessionGuard = StoreWindowHider.ForAllStoreChrome(); _sessionGuard.StartSession(); }
        }
        if (s_owned.IsEmpty || Environment.TickCount64 < _nextTrayCheck) return;
        _nextTrayCheck = Environment.TickCount64 + 10_000;
        foreach (var store in s_owned.Keys)
        {
            if (IsSuspended(store)) continue;
            try { StoreTrayIcons.Remove(store); }
            catch { /* the client exited while its icons were read */ }
        }
    }

    private static void SweepStore(StoreKind store, string[] processNames, StoreWindowHider.SurfaceSnapshot snapshot)
    {
        if (IsSuspended(store)) return;
        if (store == StoreKind.Rockstar)
            StoreWindowHider.CollapseOrphanSurfaces(snapshot, processNames, "Rockstar Games");
        else
            StoreWindowHider.CollapseOrphanSurfaces(snapshot, processNames, null);
    }
}
