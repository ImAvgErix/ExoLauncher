using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// What Go Live can share, as Discord's picker lists it: every screen, and every application window
/// Alt+Tab would show (visible, not minimized, not a tool window or another window's dialog, not
/// cloaked on another desktop or suspended, titled, on a screen), never Exo's own or Exo Capture's.
/// A Store app's frame (ApplicationFrameHost) is named, iconed and heard as the app inside it.
/// A window of a running library game is the Game shortcut. Pictures come from Exo Capture
/// (<see cref="GoLiveCapture"/>): Exo itself never captures the screen. Ids are
/// <c>window:&lt;hwnd&gt;</c> and <c>screen:&lt;device&gt;</c>; an id is looked up again before use.
/// </summary>
internal static class GoLiveSources
{
    /// <summary>A screen (Monitor set) or a window: its title, app, physical size, process and executable (native only).</summary>
    internal sealed record Source(string Id, string Kind, string Title, string App, nint Window, nint Monitor,
        int Width, int Height, uint ProcessId, string? Exe, string? Game);

    /// <summary>What decides whether a visible, unminimized window is listed (<see cref="Shareable"/>).</summary>
    internal readonly record struct WindowFacts(bool Owned, bool AppWindow, bool Tool, bool NoActivate,
        bool Cloaked, bool Exo, bool OnScreen, string Title, string ClassName, int Width, int Height);

    // The shell's own surfaces: the desktop, the taskbars, Start and search.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
    };

    /// <summary>Alt+Tab's rule, less what can't be captured or isn't worth sharing.</summary>
    internal static bool Shareable(in WindowFacts f) =>
        !f.Cloaked && !f.Exo && f.OnScreen
        && (f.AppWindow || (!f.Owned && !f.Tool && !f.NoActivate))
        && f.Title.Trim().Length > 0 && f.Width >= 120 && f.Height >= 80
        && !ShellClasses.Contains(f.ClassName);

    /// <summary>Screens first (the main one, then left to right), then windows front to back.</summary>
    /// <param name="games">Running library games: title and install folder.</param>
    internal static List<Source> List(IReadOnlyList<(string Title, string Folder)> games)
    {
        var sources = Screens();
        var windows = new List<Source>();
        EnumWindows((hwnd, _) =>
        {
            if (Describe(hwnd, games) is { } source) windows.Add(source);
            return true;
        }, 0);
        sources.AddRange(windows);
        return sources;
    }

    /// <summary>The source an id names, if it is still there and still shareable.</summary>
    internal static Source? Find(string? id, IReadOnlyList<(string Title, string Folder)> games)
    {
        if (id is null) return null;
        if (id.StartsWith("screen:", StringComparison.Ordinal))
            return Screens().FirstOrDefault(s => s.Id == id);
        return id.StartsWith("window:", StringComparison.Ordinal)
            && long.TryParse(id.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var raw) && IsWindow((nint)raw)
            ? Describe((nint)raw, games) : null;
    }

    private static List<Source> Screens()
    {
        var found = new List<(MonitorInfoEx Info, nint Handle)>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info)) found.Add((info, monitor));
            return true;
        }, 0);
        return found
            .OrderByDescending(m => (m.Info.Flags & 1) != 0)
            .ThenBy(m => m.Info.Monitor.Left).ThenBy(m => m.Info.Monitor.Top)
            .Select((m, i) => new Source("screen:" + m.Info.Device, "screen", "Screen " + (i + 1), "Screen", 0, m.Handle,
                m.Info.Monitor.Right - m.Info.Monitor.Left, m.Info.Monitor.Bottom - m.Info.Monitor.Top, 0, null, null))
            .ToList();
    }

    private static Source? Describe(nint hwnd, IReadOnlyList<(string Title, string Folder)> games)
    {
        // Windows gives a minimized window no picture: it is listed once it is back.
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        var exe = ImagePath(pid);
        var style = (long)GetWindowLongPtr(hwnd, -20); // GWL_EXSTYLE
        var cloaked = DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloak, sizeof(int)) == 0 && cloak != 0;
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out Rect bounds, Marshal.SizeOf<Rect>()) != 0)
            GetWindowRect(hwnd, out bounds);
        var title = Text(hwnd);
        var facts = new WindowFacts(
            Owned: GetWindow(hwnd, 4 /* GW_OWNER */) != 0,
            AppWindow: (style & 0x00040000) != 0, Tool: (style & 0x00000080) != 0, NoActivate: (style & 0x08000000) != 0,
            Cloaked: cloaked,
            Exo: pid == Environment.ProcessId || string.Equals(Path.GetFileName(exe), "ExoCapture.exe", StringComparison.OrdinalIgnoreCase),
            OnScreen: MonitorFromWindow(hwnd, 0 /* MONITOR_DEFAULTTONULL */) != 0,
            Title: title, ClassName: ClassName(hwnd),
            Width: bounds.Right - bounds.Left, Height: bounds.Bottom - bounds.Top);
        if (!Shareable(facts)) return null;
        // A Store app draws inside ApplicationFrameHost's frame: its own process is the one inside.
        if (string.Equals(Path.GetFileName(exe), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            && FrameApp(hwnd) is { } inner && inner != pid && ImagePath(inner) is { } innerExe)
            (pid, exe) = (inner, innerExe);
        var game = exe is null ? null : games.FirstOrDefault(g => IsUnder(exe, g.Folder)).Title;
        return new Source("window:" + ((long)hwnd).ToString(CultureInfo.InvariantCulture), "window", title, AppName(exe),
            hwnd, 0, facts.Width, facts.Height, pid, exe, game);
    }

    private static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static uint? FrameApp(nint frame)
    {
        uint found = 0;
        EnumChildWindows(frame, (child, _) =>
        {
            if (ClassName(child) != "Windows.UI.Core.CoreWindow") return true;
            GetWindowThreadProcessId(child, out found);
            return false;
        }, 0);
        return found == 0 ? null : found;
    }

    private static readonly ConcurrentDictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The executable's own name for itself (Google Chrome), else its file name.</summary>
    private static string AppName(string? exe) => exe is null ? "" : Names.GetOrAdd(exe, path =>
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description)) return description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return Path.GetFileNameWithoutExtension(path);
    });

    /// <summary>The app's icon as a PNG data URL (32 px), cached per executable.</summary>
    internal static string? Icon(string? exe) => exe is null ? null : IconCache.GetOrAdd(exe, path =>
        GameIconArt.IconPng(path, 32) is { } png ? "data:image/png;base64," + Convert.ToBase64String(png) : null);

    private static string? ImagePath(uint pid)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == 0) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(process); }
    }

    private static string Text(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var buffer = new StringBuilder(Math.Min(length, 512) + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string ClassName(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    private delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
