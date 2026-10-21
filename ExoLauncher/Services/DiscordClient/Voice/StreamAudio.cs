using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Go Live's sound, captured with Windows' process loopback (ActivateAudioInterfaceAsync on
/// VAD\Process_Loopback; Windows 10 2004 and later) as 20 ms frames of 48 kHz stereo float for Opus.
/// A screen sends everything this PC plays except Exo itself (EXCLUDE_TARGET_PROCESS_TREE on Exo, so
/// the call's voices and Exo's music never echo into the stream); a window sends only its app's sound
/// (INCLUDE_TARGET_PROCESS_TREE on the window's process), as Discord does. Silence flows as silence.
/// </summary>
/// <param name="app">The shared window's process, or 0 for a screen.</param>
internal sealed unsafe class StreamAudio(uint app = 0) : IDisposable
{
    private const string ProcessLoopback = @"VAD\Process_Loopback";
    private const uint Loopback = 0x00020000, EventCallback = 0x00040000, AutoConvert = 0x80000000 | 0x08000000;

    private readonly CancellationTokenSource _stop = new();
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
        try
        {
            var client = Activate();
            var format = VoiceAudio.Format();
            Marshal.ThrowExceptionForHR(client.Initialize(0, Loopback | EventCallback | AutoConvert, 200_000, 0, ref format, 0));
            using var ready = new AutoResetEvent(false);
            Marshal.ThrowExceptionForHR(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            var iid = typeof(VoiceAudio.IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var serviceObject));
            var capture = (VoiceAudio.IAudioCaptureClient)serviceObject;
            var frame = new float[VoiceAudio.FrameFloats];
            var filled = 0;
            Marshal.ThrowExceptionForHR(client.Start());
            AppLog.Info(app == 0 ? "Discord video: stream sound started (this PC's sound, not Exo's)." : "Discord video: stream sound started (the shared app's sound).");
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    if (!ready.WaitOne(100)) continue;
                    while (capture.GetNextPacketSize(out var packet) == 0 && packet > 0)
                    {
                        if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0) break;
                        var source = new ReadOnlySpan<float>((void*)data, (int)frames * Opus.Channels);
                        var silent = (flags & 2) != 0;
                        for (var i = 0; i < source.Length; i++)
                        {
                            frame[filled++] = silent ? 0 : MathF.Max(-1f, MathF.Min(1f, source[i]));
                            if (filled == frame.Length)
                            {
                                Captured?.Invoke(frame); // encoded at once: the frame is filled again after
                                filled = 0;
                            }
                        }
                        capture.ReleaseBuffer(frames);
                    }
                }
            }
            finally
            {
                client.Stop();
                Marshal.ReleaseComObject(capture);
                Marshal.ReleaseComObject(client);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or EntryPointNotFoundException or TimeoutException)
        {
            AppLog.Info($"Discord video: stream sound failed ({ex.GetType().Name} 0x{ex.HResult:X8}).");
        }
    }

    // Process loopback: the app's process tree, or everything but Exo's.
    private VoiceAudio.IAudioClient Activate()
    {
        var parameters = app == 0
            ? new ActivationParams { ActivationType = 1, TargetProcessId = (uint)Environment.ProcessId, ProcessLoopbackMode = 1 }
            : new ActivationParams { ActivationType = 1, TargetProcessId = app, ProcessLoopbackMode = 0 };
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
