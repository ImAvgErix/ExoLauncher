using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using IAudioSessionControl = ExoLauncher.Helpers.CoreAudioInterop.IAudioSessionControl;
using IAudioSessionControl2 = ExoLauncher.Helpers.CoreAudioInterop.IAudioSessionControl2;
using IAudioSessionEnumerator = ExoLauncher.Helpers.CoreAudioInterop.IAudioSessionEnumerator;
using IAudioSessionManager2 = ExoLauncher.Helpers.CoreAudioInterop.IAudioSessionManager2;
using IAudioSessionNotification = ExoLauncher.Helpers.CoreAudioInterop.IAudioSessionNotification;
using IMMDevice = ExoLauncher.Helpers.CoreAudioInterop.IMMDevice;
using IMMDeviceCollection = ExoLauncher.Helpers.CoreAudioInterop.IMMDeviceCollection;
using IMMDeviceEnumerator = ExoLauncher.Helpers.CoreAudioInterop.IMMDeviceEnumerator;
using ISimpleAudioVolume = ExoLauncher.Helpers.CoreAudioInterop.ISimpleAudioVolume;
using MMDeviceEnumeratorComObject = ExoLauncher.Helpers.CoreAudioInterop.MMDeviceEnumeratorComObject;

namespace ExoLauncher.Adapters;

/// <summary>
/// Mutes only the vendor-client audio sessions Exo owns for an active operation.
/// It deliberately does not touch games, overlays, services, anti-cheat, or the
/// system-wide notification policy. Every session Exo changes is remembered and
/// restored when the operation ends, the store is surfaced, or Exo shuts down.
/// </summary>
internal sealed class StoreAudioSilencer : IDisposable
{
    internal static readonly TimeSpan ActiveSweepInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan IdleSweepInterval = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlyDictionary<StoreKind, string[]> s_processNames =
        new Dictionary<StoreKind, string[]>
        {
            [StoreKind.Steam] = ["steam", "steamwebhelper", "steamerrorreporter"],
            [StoreKind.Epic] = ["EpicGamesLauncher", "EpicWebHelper"],
            [StoreKind.Gog] = ["GalaxyClient", "GOG Galaxy Notifications"],
            [StoreKind.Riot] = ["Riot Client", "RiotClientUx", "RiotClientUxRender"],
            [StoreKind.Xbox] = ["XboxPcApp", "GamingApp"],
            [StoreKind.Ea] = ["EADesktop"],
            [StoreKind.Ubisoft] = ["UbisoftConnect", "upc", "UplayWebCore"],
            [StoreKind.BattleNet] = ["Battle.net"],
            [StoreKind.Amazon] = ["Amazon Games", "AmazonGames", "AmazonGamesUI"],
            [StoreKind.Rockstar] = ["Launcher", "LauncherPatcher"],
            [StoreKind.Itch] = ["itch"],
            [StoreKind.Minecraft] = ["MinecraftLauncher"],
            [StoreKind.Roblox] = ["RobloxPlayerLauncher"],
            [StoreKind.Paradox] = ["Paradox Launcher", "ParadoxLauncher"],
            [StoreKind.Wargaming] = ["wgc"],
        };

    private readonly Func<StoreKind, bool> _shouldMute;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly SessionCreatedNotification _sessionCreatedNotification;
    private readonly object _gate = new();
    private readonly HashSet<string> _exoMuted = new(StringComparer.Ordinal);
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public StoreAudioSilencer(Func<StoreKind, bool> shouldMute)
    {
        _shouldMute = shouldMute ?? throw new ArgumentNullException(nameof(shouldMute));
        _sessionCreatedNotification = new SessionCreatedNotification(() => _wake.Set());
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _thread is not null) return;
            _cts = new CancellationTokenSource();
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "exo-store-audio-guard",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    /// <summary>Request a prompt rescan after an operation/suspension change.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        try { _wake.Set(); }
        catch (ObjectDisposedException) { /* shutdown raced the runtime timer */ }
    }

    public void Dispose()
    {
        Thread? thread;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
            cts = _cts;
            _thread = null;
            _cts = null;
        }

        try { cts?.Cancel(); } catch { /* best effort */ }
        _wake.Set();
        try { thread?.Join(2000); } catch { /* best effort */ }
        try { cts?.Dispose(); } catch { /* best effort */ }
        _wake.Dispose();
    }

    private void Run()
    {
        var initialized = false;
        var notificationManagers = new List<IAudioSessionManager2>();
        var nextEndpointRefresh = DateTimeOffset.MinValue;
        try
        {
            initialized = CoreAudioInterop.CoInitializeEx(IntPtr.Zero, CoreAudioInterop.CoinitMultithreaded) >= 0;
            // Enumerate first, then subscribe. Core Audio documents that the
            // callback only reports sessions created after registration.
            Sweep();
            while (!(_cts?.IsCancellationRequested ?? true))
            {
                var allowedNames = ActiveProcessNames();
                var shouldEnumerate = ShouldEnumerateSessions(allowedNames.Count, _exoMuted.Count);
                if (shouldEnumerate)
                {
                    // Audio endpoint changes are not delivered through the session
                    // manager. Rebind all active render endpoints as recovery; new
                    // sessions on an already-bound endpoint use the immediate COM
                    // callback below.
                    if (DateTimeOffset.UtcNow >= nextEndpointRefresh)
                    {
                        RebindSessionNotifications(notificationManagers);
                        nextEndpointRefresh = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                    }
                    try { Sweep(allowedNames); } catch { /* an audio endpoint can restart at any time */ }
                }
                _wake.Wait(shouldEnumerate ? ActiveSweepInterval : IdleSweepInterval);
                _wake.Reset();
            }
        }
        finally
        {
            // This is the only path that restores a mute. It runs both when the
            // scope ends and when Exo exits, and only addresses keys Exo muted.
            try { RestoreAll(); } catch { /* best effort during shutdown */ }
            UnregisterSessionNotifications(notificationManagers);
            if (initialized) CoreAudioInterop.CoUninitialize();
        }
    }

    internal static bool ShouldEnumerateSessions(int activeProcessNameCount, int ownedMuteCount) =>
        activeProcessNameCount > 0 || ownedMuteCount > 0;

    private void Sweep() => Sweep(ActiveProcessNames());

    private void Sweep(HashSet<string> allowedNames)
    {
        // At idle there is nothing to mute or restore. Avoid constructing Core
        // Audio endpoint/session COM graphs until an Exo operation is active.
        if (!ShouldEnumerateSessions(allowedNames.Count, _exoMuted.Count)) return;
        var sessions = GetSessions();
        try
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var session in sessions)
            {
                if (!TryGetSessionKey(session.Control, out var key)) continue;
                live.Add(key);

                var shouldMute = allowedNames.Contains(ProcessName(session.Control));
                if (shouldMute)
                {
                    if (_exoMuted.Contains(key))
                    {
                        // Clients can replace/reset an endpoint session while
                        // retaining its instance id. Keep Exo-owned sessions
                        // silent for the whole scope instead of assuming the
                        // first SetMute call remains in force forever.
                        session.Volume.GetMute(out var stillMuted);
                        if (!stillMuted)
                            session.Volume.SetMute(true, ref CoreAudioInterop.EventContext);
                        continue;
                    }
                    session.Volume.GetMute(out var wasMuted);
                    if (wasMuted) continue; // Exo never claims ownership of someone else's mute.
                    session.Volume.SetMute(true, ref CoreAudioInterop.EventContext);
                    _exoMuted.Add(key);
                }
                else if (_exoMuted.Remove(key))
                {
                    // The user opened/surfaced this store or Exo's operation ended.
                    session.Volume.SetMute(false, ref CoreAudioInterop.EventContext);
                }
            }

            // A vanished session cannot be restored and no longer consumes an
            // audio resource. Remove its stale key without touching any PID.
            _exoMuted.RemoveWhere(key => !live.Contains(key));
        }
        finally
        {
            ReleaseSessions(sessions);
        }
    }

    private HashSet<string> ActiveProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (store, storeNames) in s_processNames)
        {
            if (!_shouldMute(store)) continue;
            names.UnionWith(storeNames);
        }
        return names;
    }

    private void RestoreAll()
    {
        if (_exoMuted.Count == 0) return;
        var sessions = GetSessions();
        try
        {
            foreach (var session in sessions)
            {
                if (!TryGetSessionKey(session.Control, out var key) || !_exoMuted.Contains(key)) continue;
                session.Volume.SetMute(false, ref CoreAudioInterop.EventContext);
            }
            _exoMuted.Clear();
        }
        finally
        {
            ReleaseSessions(sessions);
        }
    }

    private static string ProcessName(IAudioSessionControl2 control)
    {
        try
        {
            control.GetProcessId(out var pid);
            if (pid == 0) return string.Empty;
            return ProcessHelper.TryReadProcessName(unchecked((int)pid)) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static bool TryGetSessionKey(IAudioSessionControl2 control, out string key)
    {
        try
        {
            control.GetSessionInstanceIdentifier(out var id);
            key = string.IsNullOrWhiteSpace(id) ? string.Empty : id;
            return key.Length > 0;
        }
        catch
        {
            key = string.Empty;
            return false;
        }
    }

    private static List<AudioSession> GetSessions()
    {
        var result = new List<AudioSession>();
        var managers = GetActiveSessionManagers();
        try
        {
            foreach (var manager in managers)
            {
                IAudioSessionEnumerator? collection = null;
                try
                {
                    Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out collection));
                    Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                    for (var i = 0; i < count; i++)
                    {
                        IAudioSessionControl? baseControl = null;
                        try
                        {
                            Marshal.ThrowExceptionForHR(collection.GetSession(i, out baseControl));
                            if (baseControl is not IAudioSessionControl2 control || baseControl is not ISimpleAudioVolume volume)
                            {
                                Release(baseControl);
                                continue;
                            }
                            result.Add(new AudioSession(baseControl, control, volume));
                            baseControl = null; // result owns the COM reference.
                        }
                        finally
                        {
                            Release(baseControl);
                        }
                    }
                }
                finally
                {
                    Release(collection);
                }
            }
            return result;
        }
        catch
        {
            ReleaseSessions(result);
            throw;
        }
        finally
        {
            foreach (var manager in managers) Release(manager);
        }
    }

    private void RebindSessionNotifications(List<IAudioSessionManager2> notificationManagers)
    {
        UnregisterSessionNotifications(notificationManagers);
        foreach (var manager in GetActiveSessionManagers())
        {
            try
            {
                Marshal.ThrowExceptionForHR(manager.RegisterSessionNotification(_sessionCreatedNotification));
                notificationManagers.Add(manager);
            }
            catch
            {
                Release(manager);
            }
        }
    }

    private void UnregisterSessionNotifications(List<IAudioSessionManager2> notificationManagers)
    {
        foreach (var manager in notificationManagers)
        {
            try { manager.UnregisterSessionNotification(_sessionCreatedNotification); }
            catch { /* the device may have been unplugged */ }
            Release(manager);
        }
        notificationManagers.Clear();
    }

    private static List<IAudioSessionManager2> GetActiveSessionManagers()
    {
        var result = new List<IAudioSessionManager2>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(CoreAudioInterop.EDataFlow.ERender, CoreAudioInterop.DeviceStateActive, out devices));
            Marshal.ThrowExceptionForHR(devices.GetCount(out var count));
            for (var index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                object? managerObject = null;
                try
                {
                    Marshal.ThrowExceptionForHR(devices.Item(index, out device));
                    var managerId = typeof(IAudioSessionManager2).GUID;
                    Marshal.ThrowExceptionForHR(device.Activate(ref managerId, CoreAudioInterop.ClsctxAll, IntPtr.Zero, out managerObject));
                    if (managerObject is IAudioSessionManager2 manager)
                    {
                        result.Add(manager);
                        managerObject = null; // result owns the COM reference.
                    }
                }
                catch { /* one endpoint may disappear during enumeration */ }
                finally
                {
                    Release(managerObject);
                    Release(device);
                }
            }
            return result;
        }
        catch
        {
            foreach (var manager in result) Release(manager);
            return [];
        }
        finally
        {
            Release(devices);
            Release(enumerator);
        }
    }

    private static void ReleaseSessions(IEnumerable<AudioSession> sessions)
    {
        foreach (var session in sessions) Release(session.BaseControl);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); } catch { /* best effort */ }
        }
    }

    private sealed record AudioSession(IAudioSessionControl BaseControl, IAudioSessionControl2 Control, ISimpleAudioVolume Volume);

    [ComVisible(true)]
    private sealed class SessionCreatedNotification(Action wake) : IAudioSessionNotification
    {
        // COM can invoke this from an AudioSrv-owned thread. Do no COM work or
        // audio mutation here; the dedicated MTA worker will enumerate safely.
        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            try { wake(); } catch { /* never throw through the COM callback */ }
            return 0;
        }
    }


}
