using System.Diagnostics;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// The driver's "NVIDIA Settings" notification-area icon (NVDisplay.Container's session plugin) is removed while Exo
/// runs, since System is where Exo sets the GPU. Nothing polls: it is looked for as Exo starts and when Explorer
/// restarts (TaskbarCreated, which makes the container add it back), a few times each since the container may add it
/// late (a sign-in starts both at once). It can't be given back on close (the container runs as SYSTEM, so Exo's
/// TaskbarCreated never reaches it): it returns at the next sign-in or Explorer restart.
/// </summary>
internal static class NvidiaTrayIcon
{
    private const string Container = "NVDisplay.Container";
    private static readonly TimeSpan[] Looks = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)];
    private static Timer? _watch;
    private static int _look;
    private static bool _logged;

    /// <summary>Looks now and a few times over the next two minutes; called as Exo starts and after Explorer restarts.</summary>
    internal static void Soon()
    {
        Volatile.Write(ref _look, 0);
        var watch = _watch ??= new Timer(_ => Look());
        try { watch.Change(Looks[0], Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    internal static void Stop() => Interlocked.Exchange(ref _watch, null)?.Dispose();

    private static void Look()
    {
        var removed = Remove();
        var next = Interlocked.Increment(ref _look);
        if (removed || next >= Looks.Length) return;
        try { _watch?.Change(Looks[next] - Looks[next - 1], Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private static bool Remove()
    {
        try
        {
            if (IconExecutable() is not { } executable) return false;
            using var self = Process.GetCurrentProcess();
            var session = self.SessionId;
            var pids = new List<int>();
            foreach (var process in Process.GetProcessesByName(Container))
                using (process) if (process.SessionId == session) pids.Add(process.Id);
            if (pids.Count == 0 || TrayIconRemover.Remove(TrayIcons.Read(executable), pids) == 0) return false;
            if (!_logged) { _logged = true; AppLog.Info("NVIDIA tray icon removed while Exo runs."); }
            return true;
        }
        catch (Exception ex) { AppLog.Debug("NVIDIA tray icon: " + ex.GetType().Name); return false; }
    }

    /// <summary>The path Windows recorded the icon under (a known-folder GUID prefix, not a file path).</summary>
    private static string? IconExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (key is null) return null;
        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub?.GetValue("ExecutablePath") is string path && path.EndsWith(@"\" + Container + ".exe", StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }
}
