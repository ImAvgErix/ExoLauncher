using System.Runtime.InteropServices;
using static ExoLauncher.Helpers.CoreAudioInterop;

namespace ExoLauncher.Services;

/// <summary>
/// Per-application volume for hosted media. Reads and writes the WASAPI
/// per-session volume (<see cref="ISimpleAudioVolume"/>) of every audio session
/// owned by a set of process ids — a hosted client's process tree, or Exo's
/// WebView2 environment for the YouTube Music web player. All COM work runs on
/// one dedicated STA thread: the session objects are apartment-bound local
/// interfaces and cannot be enumerated on a random MTA thread-pool thread.
/// </summary>
internal static class AppVolume
{
    internal sealed record Result(double Level, bool Muted, bool Active = false);

    private sealed record Work(IReadOnlySet<int> Pids, IReadOnlySet<string> Names, double? Level, bool? Muted,
        TaskCompletionSource<Result?> Done, bool Probe = false);

    private const int AudioSessionStateActive = 1;

    /// True while any render or capture session of these processes is
    /// active — a voice call, a playing song, a live microphone. Never
    /// changes a level. Null when the processes own no session at all.
    internal static Task<bool?> ActiveAsync(IReadOnlySet<int> pids)
    {
        if (pids.Count == 0) return Task.FromResult<bool?>(null);
        var done = new TaskCompletionSource<Result?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue().Add(new Work(pids, new HashSet<string>(), null, null, done, Probe: true));
        return done.Task.ContinueWith(task => task.Result is { } result ? result.Active : (bool?)null,
            TaskContinuationOptions.ExecuteSynchronously);
    }

    private static readonly object Gate = new();
    private static System.Collections.Concurrent.BlockingCollection<Work>? _queue;

    /// Change and/or read every matching session. Returns the aggregate state
    /// (loudest level, all-muted flag) or null when no matching session exists.
    /// <paramref name="names"/> are process names (no extension) that also
    /// qualify a session: a client whose audio lives in a helper outside the
    /// tree Exo tracks, or an adopted session, still gets its volume.
    internal static Task<Result?> ApplyAsync(IReadOnlySet<int> pids, double? level = null, bool? muted = null,
        IReadOnlySet<string>? names = null)
    {
        names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pids.Count == 0 && names.Count == 0) return Task.FromResult<Result?>(null);
        if (level is { } value && (!double.IsFinite(value) || value < 0 || value > 1))
            return Task.FromResult<Result?>(null);
        var done = new TaskCompletionSource<Result?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue().Add(new Work(pids, names, level, muted, done));
        return done.Task;
    }

    private static bool Matches(Work work, uint pid, Dictionary<uint, bool> byName)
    {
        var id = unchecked((int)pid);
        if (work.Pids.Contains(id)) return true;
        if (work.Names.Count == 0 || id <= 0) return false;
        if (byName.TryGetValue(pid, out var known)) return known;
        // The mini player re-reads Apple Music's volume every
        // second; a table walk per foreign session made that the poll's cost.
        var match = Adapters.ProcessHelper.TryReadProcessName(id) is { } name && work.Names.Contains(name);
        byName[pid] = match;
        return match;
    }

    private static System.Collections.Concurrent.BlockingCollection<Work> Queue()
    {
        lock (Gate)
        {
            if (_queue is not null) return _queue;
            _queue = new System.Collections.Concurrent.BlockingCollection<Work>();
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "exo-app-volume",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return _queue;
        }
    }

    private static void Run()
    {
        var initialized = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded) >= 0;
        WatchDevices();
        try
        {
            foreach (var work in _queue!.GetConsumingEnumerable())
            {
                try { work.Done.TrySetResult(Apply(work)); }
                catch { work.Done.TrySetResult(null); }
            }
        }
        finally
        {
            if (initialized) CoUninitialize();
        }
    }

    // Every trim tick (5 s while Exo is behind a game) each idle hosted client
    // probes, one right after the other, and each probe used to enumerate every
    // session on every endpoint (about 2 ms of cross-process calls). Probes
    // within a second answer from the same reading of the sessions.
    private static readonly TimeSpan ProbeReuse = TimeSpan.FromSeconds(1);
    private static List<(uint Pid, bool Active)>? _probeReading;
    private static long _probeReadingAt;

    private static Result? Probe(Work work)
    {
        var now = Environment.TickCount64;
        if (_probeReading is null || _devicesChanged || now - _probeReadingAt >= ProbeReuse.TotalMilliseconds)
        {
            var reading = new List<(uint Pid, bool Active)>();
            var sessions = Sessions(EDataFlow.EAll);
            try
            {
                foreach (var session in sessions)
                {
                    try
                    {
                        if (session.Control.GetProcessId(out var pid) < 0) continue;
                        reading.Add((pid, session.Base.GetState(out var state) >= 0 && state == AudioSessionStateActive));
                    }
                    catch { /* a session can vanish mid-enumeration */ }
                }
            }
            finally
            {
                foreach (var session in sessions) Release(session.Base);
            }
            _probeReading = reading;
            _probeReadingAt = Environment.TickCount64;
        }
        var byName = new Dictionary<uint, bool>();
        bool? active = null;
        foreach (var (pid, isActive) in _probeReading)
        {
            if (!Matches(work, pid, byName)) continue;
            active = active == true || isActive;
        }
        return active is { } any ? new Result(0, false, any) : null;
    }

    private static Result? Apply(Work work)
    {
        if (work.Probe) return Probe(work);
        var sessions = Sessions(EDataFlow.ERender);
        try
        {
            var byName = new Dictionary<uint, bool>();
            var matched = new List<(Session Session, bool Active)>();
            foreach (var session in sessions)
            {
                try
                {
                    if (session.Control.GetProcessId(out var pid) < 0 || !Matches(work, pid, byName)) continue;
                    var isActive = session.Base.GetState(out var state) >= 0 && state == AudioSessionStateActive;
                    matched.Add((session, isActive));
                }
                catch { /* a session can vanish mid-enumeration */ }
            }
            if (matched.Count == 0) return null;
            var anyActive = matched.Exists(item => item.Active);

            var context = EventContext;
            double? level = null;
            var muted = true;
            foreach (var (session, isActive) in matched)
            {
                if (anyActive && !isActive) continue;
                try
                {
                    if (work.Level is { } target)
                        session.Volume.SetMasterVolume((float)target, ref context);
                    if (work.Muted is { } flag)
                        session.Volume.SetMute(flag, ref context);
                    if (session.Volume.GetMasterVolume(out var current) >= 0)
                        level = level is null ? current : Math.Min(level.Value, current);
                    if (session.Volume.GetMute(out var isMuted) >= 0)
                        muted = muted && isMuted;
                }
                catch { }
            }
            return new Result(level ?? 0, muted, anyActive);
        }
        finally
        {
            foreach (var session in sessions) Release(session.Base);
        }
    }

    private sealed record Session(IAudioSessionControl Base, IAudioSessionControl2 Control, ISimpleAudioVolume Volume);

    // Session managers are activated once per endpoint and reused; the trim
    // loop's audio probes would otherwise enumerate and activate every audio
    // device on every tick. An endpoint change (IMMNotificationClient) or a
    // minute passing drops them, and the next call enumerates again. Only the
    // worker thread touches them.
    private static readonly TimeSpan ManagerLifetime = TimeSpan.FromSeconds(60);
    private static readonly Dictionary<EDataFlow, List<IAudioSessionManager2>> Managers = new();
    private static long _managersAt;
    private static volatile bool _devicesChanged;
    private static IMMDeviceEnumerator? _deviceWatch;
    private static DeviceNotification? _deviceNotification;

    private static void WatchDevices()
    {
        try
        {
            _deviceWatch = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _deviceNotification = new DeviceNotification();
            var client = Marshal.GetComInterfaceForObject(_deviceNotification, typeof(IMMNotificationClient));
            if (_deviceWatch.RegisterEndpointNotificationCallback(client) < 0) Marshal.Release(client);
        }
        catch { /* the one-minute refresh still picks up device changes */ }
    }

    private static List<IAudioSessionManager2> ManagersFor(EDataFlow flow)
    {
        var now = Environment.TickCount64;
        if (_devicesChanged || now - _managersAt >= ManagerLifetime.TotalMilliseconds)
        {
            _devicesChanged = false;
            _managersAt = now;
            foreach (var list in Managers.Values)
                foreach (var stale in list) Release(stale);
            Managers.Clear();
        }
        if (Managers.TryGetValue(flow, out var cached)) return cached;
        var result = new List<IAudioSessionManager2>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out devices));
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
                        managerObject = null; // the cache owns the COM reference.
                    }
                }
                catch { /* one endpoint may disappear during enumeration */ }
                finally
                {
                    Release(managerObject);
                    Release(device);
                }
            }
        }
        catch { }
        finally
        {
            Release(devices);
            Release(enumerator);
        }
        Managers[flow] = result;
        return result;
    }

    private static List<Session> Sessions(EDataFlow flow)
    {
        var result = new List<Session>();
        foreach (var manager in ManagersFor(flow))
        {
            IAudioSessionEnumerator? collection = null;
            try
            {
                Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out collection));
                Marshal.ThrowExceptionForHR(collection.GetCount(out var sessions));
                for (var i = 0; i < sessions; i++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(collection.GetSession(i, out control));
                        if (control is IAudioSessionControl2 extended && control is ISimpleAudioVolume volume)
                        {
                            result.Add(new Session(control, extended, volume));
                            control = null; // result owns the COM reference.
                        }
                    }
                    finally
                    {
                        Release(control);
                    }
                }
            }
            catch { _devicesChanged = true; /* the endpoint went away under a cached manager */ }
            finally
            {
                Release(collection);
            }
        }
        return result;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    internal interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig]
        int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey { public Guid FormatId; public int PropertyId; }

    [ComVisible(true)]
    private sealed class DeviceNotification : IMMNotificationClient
    {
        // Called on an audio service thread: only flag the change; the worker
        // re-enumerates on its next call.
        public int OnDeviceStateChanged(string deviceId, uint newState) { _devicesChanged = true; return 0; }
        public int OnDeviceAdded(string deviceId) { _devicesChanged = true; return 0; }
        public int OnDeviceRemoved(string deviceId) { _devicesChanged = true; return 0; }
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); } catch { /* best effort */ }
        }
    }
}
