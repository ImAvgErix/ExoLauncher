using System.Runtime.InteropServices;

namespace ExoLauncher.Services;

/// <summary>
/// Small Win32 notification-area icon used while the main Exo window is hidden.
/// It does not create a resident agent or start Exo with Windows.
/// </summary>
internal sealed class NotificationAreaIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 77; // WM_APP + 77
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint NotifyIconVersion4 = 4;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmSysCommand = 0x0112;
    private const nuint ScMask = 0xFFF0;
    private const nuint ScMinimize = 0xF020;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;

    private readonly nint _hwnd;
    private readonly Action _restore;
    private readonly Action? _minimize;
    private readonly SubclassProc _subclassProc;
    private readonly uint _taskbarCreatedMessage;
    private nint _icon;
    private bool _shown;
    private bool _disposed;

    public NotificationAreaIcon(nint hwnd, string iconPath, Action restore, Action? minimize = null)
    {
        _hwnd = hwnd;
        _restore = restore;
        _minimize = minimize;
        _subclassProc = WindowSubclass;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (File.Exists(iconPath))
            _icon = LoadImage(nint.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile);
        SetWindowSubclass(_hwnd, _subclassProc, IconId, 0);
    }

    public bool Show()
    {
        if (_disposed || _shown || _icon == nint.Zero) return _shown;
        var data = CreateData();
        if (!Shell_NotifyIcon(NimAdd, ref data)) return false;
        data.uTimeoutOrVersion = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
        _shown = true;
        return true;
    }

    public void Hide()
    {
        if (!_shown) return;
        var data = CreateData();
        Shell_NotifyIcon(NimDelete, ref data);
        _shown = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        RemoveWindowSubclass(_hwnd, _subclassProc, IconId);
        if (_icon != nint.Zero)
        {
            DestroyIcon(_icon);
            _icon = nint.Zero;
        }
    }

    private NotifyIconData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = "Exo Launcher — click to restore",
    };

    private nint WindowSubclass(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint refData)
    {
        if (message == WmSysCommand && (wParam & ScMask) == ScMinimize && _minimize is not null)
        {
            _minimize();
            return nint.Zero;
        }
        if (ShouldRecreateAfterShellRestart(message, _taskbarCreatedMessage, _shown))
        {
            // Explorer discards every notification-area icon when its taskbar
            // process restarts. Reset our local state before re-adding the icon;
            // otherwise Show() sees _shown and incorrectly assumes it survived.
            _shown = false;
            Show();
            return nint.Zero;
        }
        if (message == CallbackMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
            if (notification is WmLButtonUp or WmLButtonDoubleClick or WmRButtonUp or NinSelect or NinKeySelect)
                _restore();
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    internal static bool ShouldRecreateAfterShellRestart(
        uint message,
        uint taskbarCreatedMessage,
        bool shown) =>
        shown && taskbarCreatedMessage != 0 && message == taskbarCreatedMessage;

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
    }

    private delegate nint SubclassProc(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint refData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        uint type,
        int width,
        int height,
        uint loadFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint hwnd,
        SubclassProc callback,
        nuint subclassId,
        nuint refData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
