using System.Diagnostics;
using System.Globalization;

namespace ExoLauncher.Helpers;

/// <summary>
/// Local managed-startup timing. The single log entry contains durations only
/// and is deferred until the app window is ready so file I/O is outside the
/// measured interval.
/// </summary>
internal static class StartupTiming
{
    private static long _managedEntryTimestamp;
    private static long _winUiStartTimestamp;
    private static int _logged;

    internal static void Begin()
    {
        Volatile.Write(ref _managedEntryTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _winUiStartTimestamp, 0);
        Volatile.Write(ref _logged, 0);
    }

    internal static void MarkWinUiStart() =>
        Volatile.Write(ref _winUiStartTimestamp, Stopwatch.GetTimestamp());

    internal static void LogWindowReady()
    {
        if (Interlocked.Exchange(ref _logged, 1) != 0)
            return;

        var readyTimestamp = Stopwatch.GetTimestamp();
        var managedEntryTimestamp = Volatile.Read(ref _managedEntryTimestamp);
        var winUiStartTimestamp = Volatile.Read(ref _winUiStartTimestamp);
        if (managedEntryTimestamp <= 0 ||
            winUiStartTimestamp < managedEntryTimestamp ||
            readyTimestamp < winUiStartTimestamp)
            return;

        var managedEntryMs = ElapsedMilliseconds(managedEntryTimestamp, readyTimestamp);
        var winuiMs = ElapsedMilliseconds(winUiStartTimestamp, readyTimestamp);
        AppLog.Info(string.Create(CultureInfo.InvariantCulture,
            $"PERF startup phase=window-ready managedEntryMs={managedEntryMs} winuiMs={winuiMs}"));
    }

    private static long ElapsedMilliseconds(long startTimestamp, long endTimestamp)
    {
        var milliseconds = Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalMilliseconds;
        return Math.Max(0L, (long)Math.Round(milliseconds, MidpointRounding.AwayFromZero));
    }
}
