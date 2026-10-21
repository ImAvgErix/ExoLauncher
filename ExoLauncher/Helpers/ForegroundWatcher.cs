using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Truthful "is any Exo window in front" state. WinUI's Window.Activated is
/// unreliable with WebView2 child HWNDs (focus moving into a webview raises
/// Deactivated, and an alt-tab away from a focused webview raises nothing),
/// so the foreground window is read on every foreground change instead. The
/// hook is delivered on the thread that started the watcher (the UI thread).
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint GaRootOwner = 3;

    private readonly nint _window;
    private readonly Action<bool> _changed;
    private readonly WinEventProc _proc;
    private nint _hook;
    private bool _inFront = true;
    private bool _failureLogged;

    private ForegroundWatcher(nint window, Action<bool> changed)
    {
        _window = window;
        _changed = changed;
        _proc = OnForeground;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, 0, 0, WineventOutOfContext);
        if (_hook == IntPtr.Zero) AppLog.Warn("Foreground hook could not be installed; Exo stays in foreground mode.");
        OnForeground(IntPtr.Zero, EventSystemForeground, GetForegroundWindow(), 0, 0, 0, 0);
    }

    internal static ForegroundWatcher Start(nint window, Action<bool> changed) => new(window, changed);

    internal static bool ExoInFront(nint window) => ExoInFront(window, GetForegroundWindow());

    // Any window of Exo's process counts (main window, pop-outs, dialogs), and
    // so does every window Exo owns: the hosted clients are owned popups of
    // Exo's window, and their contained dialogs are owned by it too.
    private static bool ExoInFront(nint window, nint foreground)
    {
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        return pid == (uint)Environment.ProcessId || GetAncestor(foreground, GaRootOwner) == window;
    }

    private void OnForeground(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var inFront = ExoInFront(_window, hwnd != IntPtr.Zero ? hwnd : GetForegroundWindow());
        if (inFront == _inFront) return;
        _inFront = inFront;
        // A native callback: an exception escaping here ends the process.
        try { _changed(inFront); }
        catch (Exception error)
        {
            if (_failureLogged) return;
            _failureLogged = true;
            AppLog.Error("Foreground change handler failed.", error);
        }
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
