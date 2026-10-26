using System.Runtime.InteropServices;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using WinRT.Interop;

namespace ExoLauncher;

/// <summary>
/// Exo's own Discord client (Services\DiscordClient): its unread count is the
/// Friends tab's and taskbar's badge, and a clicked notification brings Exo up
/// on that conversation.
/// </summary>
public sealed partial class MainWindow
{
    private int _friendsBadge;
    private TaskbarBadge? _taskbarBadge;

    internal object FriendsBadge() => new { count = _friendsBadge };

    private void PublishFriendsBadge(int count)
    {
        if (count == _friendsBadge) return;
        _friendsBadge = count;
        if (_closingWindow) return;
        ShowTaskbarBadge();
        try
        {
            var core = WebHost.CoreWebView2;
            if (core is null || !WebViewTrustPolicy.IsTrustedAppUri(core.Source)) return;
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                @event = "friends.badge", data = new { count },
            }));
        }
        catch { }
    }

    // Discord settings > Notifications > Enable Unread Message Badge: the taskbar's count (the Discord tab keeps its own).
    private void ShowTaskbarBadge() =>
        (_taskbarBadge ??= new TaskbarBadge(WindowNative.GetWindowHandle(this))).Show(App.Services.Settings.Current.DiscordUnreadBadge ? _friendsBadge : 0);

    /// <summary>The unread badge setting changed: the taskbar shows or drops the count now.</summary>
    internal void RefreshTaskbarBadge() => DispatcherQueue.TryEnqueue(() => { if (!_closingWindow) ShowTaskbarBadge(); });

    // Discord settings > Notifications > Enable Taskbar Flashing: the taskbar button flashes until Exo comes to the front.
    private void FlashTaskbar()
    {
        if (_closingWindow || _windowActive) return;
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = WindowNative.GetWindowHandle(this), Flags = 2 | 12 }; // FLASHW_TRAY | FLASHW_TIMERNOFG
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    /// <summary>True while Exo (or a client it hosts) is the foreground window.</summary>
    internal bool WindowIsActive => _windowActive;

    // Coming to the front: the Discord conversation on screen is seen, so its notifications go (Discord's WINDOW_FOCUS).
    private void SetWindowActive(bool active)
    {
        var focused = active && !_windowActive;
        _windowActive = active;
        if (focused) Services.DiscordClient.DiscordSession.ExoFocused();
    }

    private void WireDiscordClient()
    {
        // These are static events that outlive the window, and Window.DispatcherQueue is null once it has closed: the
        // queue itself is kept, and it refuses work after shutdown instead of throwing.
        var queue = DispatcherQueue;
        Services.DiscordClient.DiscordSession.BadgeChanged += count => queue.TryEnqueue(() => PublishFriendsBadge(count));
        Services.DiscordClient.DiscordSession.FlashRequested += () => queue.TryEnqueue(FlashTaskbar);
        Services.DiscordClient.DiscordNotifier.AppOpened += () => queue.TryEnqueue(RestoreAndActivate);
        Services.DiscordClient.DiscordNotifier.Opened += (guildId, channelId, parentId, name) => queue.TryEnqueue(() =>
        {
            RestoreAndActivate();
            _bridge?.PostDiscordOpen(guildId, channelId, parentId, name);
        });
    }
}
