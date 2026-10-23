using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using static ExoLauncher.Helpers.CoreAudioInterop;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Go Live's sound, captured with Windows' process loopback (ActivateAudioInterfaceAsync on
/// VAD\Process_Loopback; Windows 10 2004 and later) as 20 ms frames of 48 kHz stereo float for Opus.
/// A window sends only its app's sound (INCLUDE_TARGET_PROCESS_TREE on the window's process), as Discord does.
/// A screen sends every app that plays sound, each captured on its own and mixed, except Exo itself (the call's
/// voices) and apps that play the call or a microphone again (Discord, mixers such as Voicemeeter, Sonar or Wave Link,
/// voice changers; Windows' own "Listen to this device" isn't an app's): your voice and the call's never echo into the
/// stream, while Exo's Music room and games Exo started are in it (leaving out Exo's whole process tree left them out).
/// The apps are looked at again every two seconds. Silence flows as silence.
/// </summary>
/// <param name="app">The shared window's process, or 0 for a screen.</param>
internal sealed unsafe class StreamAudio(uint app = 0) : IDisposable
{
    private const string ProcessLoopback = @"VAD\Process_Loopback";
    private const uint Loopback = 0x00020000, EventCallback = 0x00040000, AutoConvert = 0x80000000 | 0x08000000;
    private const int Frame = VoiceAudio.FrameFloats;
    private const int MostHeld = Frame * 4; // 80 ms an app may run ahead of the stream's clock before its oldest sound goes

    // Executables (lowercase, no ".exe"; a trailing * matches the start) whose sound is the call's or a microphone's.
    private static readonly string[] EchoNames = ["exocapture", "discord*", "vesktop", "voicemeeter*", "steelseriessonar*", "wavelink*",
        "nvidia broadcast", "obs64", "obs32", "voicemod*", "krisp", "fxsound*", "boom3d*", "ms-teams", "teams", "zoom"];

    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<uint> _failed = [];
    private Thread? _thread;

    /// <summary>A 20 ms stereo frame (1920 floats).</summary>
    internal event Action<float[]>? Captured;

    internal void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Exo stream sound", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        using var mmcss = MmcssTask.Join(MmcssTask.Audio);
        using var ready = new AutoResetEvent(false);
        var sources = new List<Source>();
        try
        {
            if (app != 0) Open(app, "the shared app", sources, ready);
            AppLog.Info(app == 0 ? "Discord video: stream sound started (this PC's apps, not the call)." : "Discord video: stream sound started (the shared app's sound).");
            var frame = new float[Frame];
            var clock = Stopwatch.StartNew();
            long sent = 0, looked = -2000;
            while (!_stop.IsCancellationRequested)
            {
                ready.WaitOne(10);
                if (app == 0 && clock.ElapsedMilliseconds - looked >= 2000)
                {
                    looked = clock.ElapsedMilliseconds;
                    Follow(sources, ready);
                }
                foreach (var source in sources) source.Drain();
                // The stream's clock: a frame every 20 ms, each app adding its next 20 ms when it has them.
                var due = clock.ElapsedTicks * 50 / Stopwatch.Frequency;
                if (due - sent > 10) sent = due - 1; // the thread stalled: no burst of old frames
                for (; sent < due; sent++)
                {
                    Array.Clear(frame);
                    foreach (var source in sources) source.MixInto(frame);
                    for (var i = 0; i < frame.Length; i++) frame[i] = MathF.Max(-1f, MathF.Min(1f, frame[i]));
                    Captured?.Invoke(frame); // encoded at once: the frame is filled again after
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or EntryPointNotFoundException or TimeoutException)
        {
            AppLog.Info($"Discord video: stream sound failed ({ex.GetType().Name} 0x{ex.HResult:X8}).");
        }
        finally
        {
            foreach (var source in sources) source.Dispose();
        }
    }

    // A screen: an app that started playing joins the mix, one that closed leaves it.
    private void Follow(List<Source> sources, AutoResetEvent ready)
    {
        Dictionary<uint, string> playing;
        try { playing = AppsPlaying(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return; }
        var changed = sources.RemoveAll(s =>
        {
            if (playing.ContainsKey(s.Pid)) return false;
            s.Dispose();
            return true;
        }) > 0;
        foreach (var (pid, name) in playing)
        {
            if (sources.Exists(s => s.Pid == pid) || _failed.Contains(pid)) continue;
            try
            {
                Open(pid, name, sources, ready);
                changed = true;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or TimeoutException)
            {
                _failed.Add(pid);
                AppLog.Info($"Discord video: stream sound can't take {name} ({ex.GetType().Name} 0x{ex.HResult:X8}).");
            }
        }
        if (changed) AppLog.Info("Discord video: stream sound from " + (sources.Count == 0 ? "no app" : string.Join(", ", sources.Select(s => s.Name))) + ".");
    }

    /// <summary>
    /// Every app with a sound session on a speaker that's plugged in, by its process id and executable name: the topmost
    /// of its process tree that has one (process loopback takes the tree), never Exo itself, an app that echoes the call
    /// or a microphone, or anything such an app started.
    /// </summary>
    private static Dictionary<uint, string> AppsPlaying()
    {
        var pids = new HashSet<uint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            if (enumerator.EnumAudioEndpoints(EDataFlow.ERender, DeviceStateActive, out var devices) != 0) return [];
            devices.GetCount(out var count);
            var iid = typeof(IAudioSessionManager2).GUID;
            for (var d = 0; d < count; d++)
            {
                if (devices.Item(d, out var device) != 0 || device.Activate(ref iid, ClsctxAll, 0, out var manager) != 0) continue;
                if (((IAudioSessionManager2)manager).GetSessionEnumerator(out var sessions) != 0) continue;
                sessions.GetCount(out var n);
                for (var i = 0; i < n; i++)
                    if (sessions.GetSession(i, out var control) == 0 && control is IAudioSessionControl2 session
                        && session.GetState(out var state) == 0 && state != 2 /* expired */ && session.GetProcessId(out var pid) == 0 && pid != 0)
                        pids.Add(pid);
            }
        }
        finally { Marshal.ReleaseComObject(enumerator); }

        var table = new Dictionary<uint, (uint Parent, string Name)>();
        foreach (var (pid, parent, exe) in BackgroundTrim.ProcessTable())
            table[(uint)pid] = ((uint)parent, Path.GetFileNameWithoutExtension(exe).ToLowerInvariant());
        var self = (uint)Environment.ProcessId;
        var started = new Dictionary<int, long>();
        var apps = new Dictionary<uint, string>();
        foreach (var pid in pids)
        {
            if (pid == self || !table.TryGetValue(pid, out var entry) || Echoes(entry.Name)) continue;
            var keep = true;
            // Up its tree (a parent younger than its child is a reused number, not its parent): under an app that
            // echoes it is that app's sound; under another playing app it is in that app's capture already.
            for (var (child, up, depth) = (pid, entry.Parent, 0); keep && up != 0 && depth < 32 && table.TryGetValue(up, out var parent)
                     && !BackgroundTrim.StartedBefore((int)child, (int)up, started); (child, up, depth) = (up, parent.Parent, depth + 1))
                if (Echoes(parent.Name) || (up != self && pids.Contains(up))) keep = false;
            if (keep) apps[pid] = entry.Name;
        }
        return apps;
    }

    private static bool Echoes(string name) =>
        Array.Exists(EchoNames, e => e.EndsWith('*') ? name.StartsWith(e[..^1], StringComparison.Ordinal) : name == e);

    private static void Open(uint pid, string name, List<Source> sources, AutoResetEvent ready)
    {
        var client = Activate(pid);
        try
        {
            var format = VoiceAudio.Format();
            Marshal.ThrowExceptionForHR(client.Initialize(0, Loopback | EventCallback | AutoConvert, 200_000, 0, ref format, 0));
            // One event for every app: each wakes the thread, which looks at them all.
            Marshal.ThrowExceptionForHR(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            var iid = typeof(VoiceAudio.IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var capture));
            Marshal.ThrowExceptionForHR(client.Start());
            sources.Add(new Source(pid, name, client, (VoiceAudio.IAudioCaptureClient)capture));
        }
        catch
        {
            Marshal.ReleaseComObject(client);
            throw;
        }
    }

    // One app's sound, held until the stream's clock takes it 20 ms at a time.
    private sealed class Source(uint pid, string name, VoiceAudio.IAudioClient client, VoiceAudio.IAudioCaptureClient capture) : IDisposable
    {
        private readonly float[] _held = new float[MostHeld];
        private int _count;

        internal uint Pid => pid;
        internal string Name => name;

        internal void Drain()
        {
            while (capture.GetNextPacketSize(out var packet) == 0 && packet > 0)
            {
                if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0) return;
                var played = new ReadOnlySpan<float>((void*)data, (int)frames * Opus.Channels);
                if (played.Length > MostHeld) played = played[^MostHeld..];
                if (_count + played.Length > MostHeld)
                {
                    var drop = _count + played.Length - MostHeld;
                    _held.AsSpan(drop, _count - drop).CopyTo(_held);
                    _count -= drop;
                }
                if ((flags & 2) != 0) _held.AsSpan(_count, played.Length).Clear();
                else played.CopyTo(_held.AsSpan(_count));
                _count += played.Length;
                capture.ReleaseBuffer(frames);
            }
        }

        internal void MixInto(float[] frame)
        {
            if (_count < Frame) return;
            for (var i = 0; i < Frame; i++) frame[i] += _held[i];
            _held.AsSpan(Frame, _count - Frame).CopyTo(_held);
            _count -= Frame;
        }

        public void Dispose()
        {
            client.Stop();
            Marshal.ReleaseComObject(capture);
            Marshal.ReleaseComObject(client);
        }
    }

    // Process loopback on an app's process tree.
    private static VoiceAudio.IAudioClient Activate(uint pid)
    {
        var parameters = new ActivationParams { ActivationType = 1, TargetProcessId = pid, ProcessLoopbackMode = 0 };
        var size = Marshal.SizeOf<ActivationParams>();
        var blob = Marshal.AllocHGlobal(size);
        var variant = Marshal.AllocHGlobal(sizeof(PropVariant));
        try
        {
            Marshal.StructureToPtr(parameters, blob, false);
            *(PropVariant*)variant = new PropVariant { Type = 65 /* VT_BLOB */, BlobSize = (uint)size, BlobData = blob };
            var done = new Completion();
            ActivateAudioInterfaceAsync(ProcessLoopback, typeof(VoiceAudio.IAudioClient).GUID, variant, done, out _);
            if (!done.Finished.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Windows didn't open process loopback.");
            done.Operation!.GetActivateResult(out var hr, out var client);
            Marshal.ThrowExceptionForHR(hr);
            return (VoiceAudio.IAudioClient)client;
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;       // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
        public uint TargetProcessId;
        public int ProcessLoopbackMode;  // 0 INCLUDE_TARGET_PROCESS_TREE, 1 EXCLUDE_TARGET_PROCESS_TREE
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint BlobSize;
        [FieldOffset(16)] public nint BlobData;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, nint activationParams, IActivateCompletion completionHandler, out IActivateOperation operation);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    private interface IActivateOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    private interface IActivateCompletion
    {
        void ActivateCompleted(IActivateOperation operation);
    }

    // IAgileObject: Windows calls the handler on any thread.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    private interface IAgileObject;

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Completion : IActivateCompletion, IAgileObject
    {
        internal readonly ManualResetEventSlim Finished = new();
        internal IActivateOperation? Operation;

        public void ActivateCompleted(IActivateOperation operation)
        {
            Operation = operation;
            Finished.Set();
        }
    }
}
