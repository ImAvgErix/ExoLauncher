using System.Runtime.InteropServices;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

/// <summary>
/// Discord's unread overlay on Exo's taskbar button. Discord sets the overlay
/// on its own window's button (APP_BADGE_SET → setOverlayIcon), which Exo
/// hides while it hosts the window; the same icons go on Exo's button
/// instead. They are Discord's own badge-1..badge-11.ico, which it writes
/// into %APPDATA%\discord: badge-N for N unread mentions, badge-10 for ten
/// or more. UI thread only.
/// </summary>
internal sealed class TaskbarBadge(nint window)
{
    private ITaskbarList3? _taskbar;
    private readonly Dictionary<int, nint> _icons = [];
    private int _shown;

    internal void Show(int count)
    {
        var index = Math.Clamp(count, 0, 10);
        if (index == _shown) return;
        try
        {
            if (_taskbar is null)
            {
                _taskbar = (ITaskbarList3)new TaskbarList();
                _taskbar.HrInit();
            }
            var icon = index == 0 ? 0 : Icon(index);
            if (index != 0 && icon == 0) return;
            _taskbar.SetOverlayIcon(window, icon, index == 0 ? null : index == 10 ? "10+ Discord notifications" :
                index + " Discord notification" + (index == 1 ? "" : "s"));
            _shown = index;
        }
        catch (Exception error) { AppLog.Warn("Discord badge could not be shown on the taskbar: " + error.Message); }
    }

    private nint Icon(int index)
    {
        if (_icons.TryGetValue(index, out var cached)) return cached;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "discord", "badge-" + index + ".ico");
        var icon = File.Exists(path) ? LoadImage(0, path, ImageIcon, 16, 16, LrLoadFromFile) : 0;
        if (icon != 0) _icons[index] = icon;
        return icon;
    }

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint load);

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, int flags);
        void RegisterTab(nint tab, nint mdi);
        void UnregisterTab(nint tab);
        void SetTabOrder(nint tab, nint insertBefore);
        void SetTabActive(nint tab, nint mdi, uint reserved);
        void ThumbBarAddButtons(nint hwnd, uint count, nint buttons);
        void ThumbBarUpdateButtons(nint hwnd, uint count, nint buttons);
        void ThumbBarSetImageList(nint hwnd, nint imageList);
        void SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }
}
