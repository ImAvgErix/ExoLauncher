using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Mutes only the vendor-client audio sessions Exo owns for an active operation.
/// It deliberately does not touch games, overlays, services, anti-cheat, or the
/// system-wide notification policy. Every session Exo changes is remembered and
/// restored when the operation ends, the store is surfaced, or Exo shuts down.
/// </summary>
internal sealed class StoreAudioSilencer : IDisposable
{
    private static readonly IReadOnlyDictionary<StoreKind, string[]> s_processNames =
        new Dictionary<StoreKind, string[]>
        {
            [StoreKind.Steam] = ["steam", "steamwebhelper", "steamerrorreporter"],
            [StoreKind.Epic] = ["EpicGamesLauncher", "EpicWebHelper"],
            [StoreKind.Gog] = ["GalaxyClient", "GOG Galaxy Notifications"],
            [StoreKind.Riot] = ["Riot Client", "RiotClientUx", "RiotClientUxRender"],
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

    /// <summary>
    /// Exact client/UI process names eligible for session mute. This catalog
    /// intentionally excludes GameOverlayUI, game executables, service hosts,
    /// Vanguard, Easy Anti-Cheat, BattlEye, and EOS helpers.
    /// </summary>
    internal static IReadOnlyList<string> ProcessNamesFor(StoreKind store) =>
        s_processNames.TryGetValue(store, out var names) ? names : [];

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
            initialized = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded) >= 0;
            // Enumerate first, then subscribe. Core Audio documents that the
            // callback only reports sessions created after registration.
            Sweep();
            while (!(_cts?.IsCancellationRequested ?? true))
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
                try { Sweep(); } catch { /* an audio endpoint can restart at any time */ }
                _wake.Wait(TimeSpan.FromMilliseconds(250));
                _wake.Reset();
            }
        }
        finally
        {
            // This is the only path that restores a mute. It runs both when the
            // scope ends and when Exo exits, and only addresses keys Exo muted.
            try { RestoreAll(); } catch { /* best effort during shutdown */ }
            UnregisterSessionNotifications(notificationManagers);
            if (initialized) CoUninitialize();
        }
    }

    private void Sweep()
    {
        var allowedNames = ActiveProcessNames();
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
                            session.Volume.SetMute(true, ref s_eventContext);
                        continue;
                    }
                    session.Volume.GetMute(out var wasMuted);
                    if (wasMuted) continue; // Exo never claims ownership of someone else's mute.
                    session.Volume.SetMute(true, ref s_eventContext);
                    _exoMuted.Add(key);
                }
                else if (_exoMuted.Remove(key))
                {
                    // The user opened/surfaced this store or Exo's operation ended.
                    session.Volume.SetMute(false, ref s_eventContext);
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
                session.Volume.SetMute(false, ref s_eventContext);
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
            using var process = Process.GetProcessById(unchecked((int)pid));
            return process.ProcessName;
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
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.ERender, DeviceStateActive, out devices));
            Marshal.ThrowExceptionForHR(devices.GetCount(out var count));
            for (var index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                object? managerObject = null;
                try
                {
                    Marshal.ThrowExceptionForHR(devices.Item(index, out device));
                    var managerId = typeof(IAudioSessionManager2).GUID;
                    Marshal.ThrowExceptionForHR(device.Activate(ref managerId, ClsctxAll, IntPtr.Zero, out managerObject));
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

    private static Guid s_eventContext = new("1A7163C8-5D32-4CBE-A00B-44F9559A1A21");
    private const uint CoinitMultithreaded = 0x0;
    private const int ClsctxAll = 23;
    private const uint DeviceStateActive = 0x00000001;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private enum EDataFlow { ERender, ECapture, EAll }
    private enum ERole { EConsole, EMultimedia, ECommunications }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        int OpenPropertyStore(int accessMode, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out uint state);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-C0A0BB8728BE")]
    private interface IMMDeviceCollection
    {
        int GetCount(out int deviceCount);
        int Item(int deviceNumber, out IMMDevice device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr groupingParam, uint streamFlags, out IAudioSessionControl sessionControl);
        int GetSimpleAudioVolume(IntPtr groupingParam, uint streamFlags, out ISimpleAudioVolume audioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
        int RegisterSessionNotification(IAudioSessionNotification sessionNotification);
        int UnregisterSessionNotification(IAudioSessionNotification sessionNotification);
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);
        int GetSession(int sessionCount, out IAudioSessionControl session);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
    private interface IAudioSessionNotification
    {
        int OnSessionCreated(IAudioSessionControl newSession);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    private interface IAudioSessionControl
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    private interface IAudioSessionControl2 : IAudioSessionControl
    {
        new int GetState(out int state);
        new int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        new int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        new int GetGroupingParam(out Guid groupingParam);
        new int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        new int RegisterAudioSessionNotification(IntPtr client);
        new int UnregisterAudioSessionNotification(IntPtr client);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        int GetProcessId(out uint processId);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("87CE5498-68D6-44E5-9215-6DA47EF883D3")]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float levelNorm, ref Guid eventContext);
        int GetMasterVolume(out float levelNorm);
        int SetMute(bool isMuted, ref Guid eventContext);
        int GetMute(out bool isMuted);
    }
}
