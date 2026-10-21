using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// Deletes a hosted client's notification-area icons. GUID-identified icons
/// are removed cross-process by GUID alone; hwnd+uID icons need the owning
/// window, which an empty NIM_MODIFY probe identifies because it only succeeds
/// for the window that registered the icon.
internal static class TrayIconRemover
{
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifGuid = 0x00000020;
    private static readonly nint HwndMessage = new(-3);

    internal static int Remove(IReadOnlyList<TrayIconRegistration> icons, IReadOnlyList<int> pids)
    {
        var removed = 0;
        foreach (var icon in icons)
        {
            if (icon.Guid is { } guid)
            {
                var data = NotifyIconData.ForGuid(guid);
                if (Shell_NotifyIcon(NimDelete, ref data)) removed++;
            }
            else if (icon.Uid is { } uid)
            {
                var hwnd = FindIconOwner(uid, pids);
                if (hwnd == nint.Zero) continue;
                var data = NotifyIconData.ForWindow(hwnd, uid);
                if (Shell_NotifyIcon(NimDelete, ref data)) removed++;
            }
        }
        return removed;
    }

    private static nint FindIconOwner(uint uid, IReadOnlyList<int> pids)
    {
        var set = new HashSet<int>(pids);
        nint owner = nint.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!OwnsIcon(hwnd, uid, set)) return true;
            owner = hwnd;
            return false;
        }, nint.Zero);
        for (var hwnd = FindWindowEx(HwndMessage, nint.Zero, null, null);
             hwnd != nint.Zero && owner == nint.Zero;
             hwnd = FindWindowEx(HwndMessage, hwnd, null, null))
        {
            if (OwnsIcon(hwnd, uid, set)) owner = hwnd;
        }
        return owner;
    }

    private static bool OwnsIcon(nint hwnd, uint uid, HashSet<int> pids)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (!pids.Contains((int)pid)) return false;
        var data = NotifyIconData.ForWindow(hwnd, uid);
        return Shell_NotifyIcon(NimModify, ref data);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;

        public static NotifyIconData ForGuid(Guid guid) => new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            uFlags = NifGuid,
            guidItem = guid,
        };

        public static NotifyIconData ForWindow(nint hwnd, uint uid) => new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = hwnd,
            uID = uid,
        };
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
