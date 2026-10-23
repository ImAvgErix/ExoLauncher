using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// While any of Exo's live sound runs (a call, a stream being watched, the microphone meter, Voice Check, Voice Tune),
/// Exo's process is kept fit for real time, with a game in front too. Windows' timer runs at 1 ms: DeepFilterNet's
/// worker and every run waiting on it sleep in 2 ms steps, which the default 15.6 ms tick stretches to as much as
/// 15.6 ms each (a frame that waited past real time is late at the socket and grows the plugin's buffer by 10 ms).
/// Windows honors that while Exo's window is hidden, which Windows 11 otherwise doesn't. And the host is neither put in
/// EcoQoS nor trimmed nor given low memory priority in the background (<see cref="BackgroundTrim"/>): EcoQoS runs the
/// audio threads on slow cores, and a trim pages the noise model out mid-call, so the next frames wait on it coming back.
/// </summary>
internal static partial class LiveAudio
{
    private static readonly Lock Gate = new();
    private static int _count;

    /// <summary>Whether live sound runs now.</summary>
    internal static bool Active => Volatile.Read(ref _count) > 0;

    /// <summary>Live sound started or stopped (the background trim looks again at once).</summary>
    internal static event Action? Changed;

    internal static void Begin()
    {
        lock (Gate)
        {
            if (++_count != 1) return;
            TimeBeginPeriod(1);
            BackgroundTrim.SetHostLiveAudio(true);
        }
        Changed?.Invoke();
    }

    internal static void End()
    {
        lock (Gate)
        {
            if (_count == 0 || --_count != 0) return;
            BackgroundTrim.SetHostLiveAudio(false);
            TimeEndPeriod(1);
        }
        Changed?.Invoke();
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint milliseconds);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint milliseconds);
}

/// <summary>
/// One of the call's audio threads (the microphone's, the voice thread, the speakers', each person's own and the stream's
/// sound) in Windows' Multimedia Class Scheduler for as long as it runs: registered as a "Pro Audio" (or "Audio") task, it
/// is scheduled ahead of every ordinary thread, so a game at 100% CPU can't starve it the way it can starve a thread at
/// ThreadPriority.Highest alone. Joined at the thread's start and left (Dispose) as it ends. Where MMCSS isn't running
/// (some Windows Server installs), the thread runs as before and app.log says so once.
/// </summary>
internal readonly partial struct MmcssTask : IDisposable
{
    internal const string ProAudio = "Pro Audio", Audio = "Audio";
    private static int _refusedSaid;
    private readonly nint _handle;

    private MmcssTask(nint handle) => _handle = handle;

    /// <summary>Whether this thread is in the scheduler now.</summary>
    internal bool Joined => _handle != 0;

    /// <summary>The calling thread joins the scheduler as <paramref name="task"/>.</summary>
    internal static MmcssTask Join(string task = ProAudio)
    {
        uint index = 0;
        var handle = AvSetMmThreadCharacteristicsW(task, ref index);
        if (handle == 0 && Interlocked.Exchange(ref _refusedSaid, 1) == 0)
            AppLog.Info($"Discord voice: Windows' multimedia scheduler didn't take the audio threads (error {Marshal.GetLastPInvokeError()}); they run at high priority only.");
        return new MmcssTask(handle);
    }

    /// <summary>The thread leaves the scheduler; whether Windows took it back.</summary>
    internal bool Leave() => _handle != 0 && AvRevertMmThreadCharacteristics(_handle);

    public void Dispose() => Leave();

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(nint handle);
}
