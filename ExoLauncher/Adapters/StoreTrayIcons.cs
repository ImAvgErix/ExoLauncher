using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// Takes a store client's notification-area icon away while Exo runs it, the
/// way Spotify's is, and gives it back when the user opens the client or Exo
/// closes. Giving back is Explorer's own restart signal: every client that
/// keeps a tray icon re-adds it when its windows receive "TaskbarCreated".
/// </summary>
internal static class StoreTrayIcons
{
    private static readonly ConcurrentDictionary<StoreKind, byte> s_removed = new();

    internal static void Remove(StoreKind store)
    {
        var processes = ClientProcesses(store);
        if (processes.Count == 0) return;
        var pids = processes.Select(process => process.Pid).ToArray();
        var removed = 0;
        foreach (var executable in processes.Select(process => process.Executable).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var icons = TrayIcons.Read(executable);
            if (icons.Count > 0) removed += TrayIconRemover.Remove(icons, pids);
        }
        if (removed > 0 && s_removed.TryAdd(store, 0))
            AppLog.Info(store + " tray icon removed while Exo runs it.");
    }

    internal static void Restore(StoreKind store)
    {
        if (!s_removed.TryRemove(store, out _)) return;
        var pids = ClientProcesses(store).Select(process => (uint)process.Pid).ToHashSet();
        if (pids.Count == 0) return;
        var message = RegisterWindowMessage("TaskbarCreated");
        if (message == 0) return;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pids.Contains(pid)) PostMessage(window, message, nint.Zero, nint.Zero);
            return true;
        }, nint.Zero);
    }

    internal static void RestoreAll()
    {
        foreach (var store in s_removed.Keys.ToArray()) Restore(store);
    }

    private static List<(int Pid, string Executable)> ClientProcesses(StoreKind store)
    {
        var found = new List<(int, string)>();
        foreach (var name in StoreClientCleanup.ExitProcessNames(store))
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var process in processes)
            {
                using (process)
                {
                    // Rockstar's launcher is a plain "Launcher.exe": only its own install counts.
                    if (store == StoreKind.Rockstar && !ProcessHelper.MatchesOptionalPath(process.Id, "Rockstar Games")) continue;
                    if (ProcessHelper.TryGetExecutablePath(process) is { } path) found.Add((process.Id, path));
                }
            }
        }
        return found;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
