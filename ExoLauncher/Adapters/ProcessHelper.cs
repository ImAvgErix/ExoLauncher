using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExoLauncher.Adapters;

internal static class ProcessHelper
{
    private const int SwHide = 0;
    private const int SwShowMinimized = 2;
    private const int SwMinimize = 6;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static Process? StartMinimized(string fileName, string arguments = "", string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        return Process.Start(psi);
    }

    public static Process? StartProtocol(string uri)
    {
        var psi = new ProcessStartInfo
        {
            FileName = uri,
            UseShellExecute = true,
        };
        return Process.Start(psi);
    }

    public static void MinimizeProcessWindows(int processId)
    {
        try
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == (uint)processId)
                    ShowWindow(hWnd, SwMinimize);
                return true;
            }, IntPtr.Zero);
        }
        catch { /* best-effort */ }
    }

    public static void TryCloseProcesses(params string[] processNames)
    {
        foreach (var name in processNames)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        // Soft close first — never kill anti-cheat services.
                        if (!p.HasExited)
                            p.CloseMainWindow();
                    }
                    catch { /* ignore */ }
                    finally { p.Dispose(); }
                }
            }
            catch { /* ignore */ }
        }
    }

    public static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* skip bad PATH entries */ }
        }
        return null;
    }
}
