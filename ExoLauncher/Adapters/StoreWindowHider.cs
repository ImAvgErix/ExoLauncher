using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Keeps store chrome hidden while Exo owns the user experience.
/// Vendor clients remain dependencies; Settings → Open store reveals chrome on request.
/// </summary>
internal sealed class StoreWindowHider : IDisposable
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const int GwlExstyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;

    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private const int ObjidWindow = 0;
    private const uint WmQuit = 0x0012;

    public static readonly string[] SteamProcessNames =
    [
        "steam", "steamwebhelper", "steamerrorreporter", "gameoverlayui", "gameoverlayui64",
    ];

    /// <summary>
    /// Processes that may own the main Steam shell (modern Steam UI is often
    /// steamwebhelper). Reveal still filters to the primary titled frame so
    /// helper CEF surfaces do not become extra taskbar “Steam” buttons.
    /// </summary>
    public static readonly string[] SteamMainProcessNames = ["steam", "steamwebhelper"];

    public static readonly string[] EpicProcessNames =
    [
        "EpicGamesLauncher", "EpicWebHelper",
    ];

    public static readonly string[] GalaxyProcessNames =
    [
        "GalaxyClient", "GOG Galaxy Notifications",
    ];

    /// <summary>Riot Client chrome only — never League / VALORANT game processes.</summary>
    public static readonly string[] RiotUiProcessNames =
    [
        "Riot Client", "RiotClientUx", "RiotClientUxRender",
    ];

    // These are exact, user-facing official-client processes only. Do not add
    // installers, background services, game executables, overlays, or
    // anti-cheat helpers here: quiet mode may hide client chrome, never a
    // component required to keep a game running safely.
    public static readonly string[] XboxClientProcessNames = ["XboxPcApp", "GamingApp"];
    public static readonly string[] EaClientProcessNames = ["EADesktop"];
    public static readonly string[] UbisoftClientProcessNames = ["UbisoftConnect", "upc", "UplayWebCore"];
    public static readonly string[] BattleNetClientProcessNames = ["Battle.net"];
    public static readonly string[] AmazonClientProcessNames = ["Amazon Games", "AmazonGames", "AmazonGamesUI"];
    // RockstarService and SocialClubHelper are intentionally excluded: they
    // are support components, not launcher chrome, and may be required by a
    // game session.
    public static readonly string[] RockstarClientProcessNames = ["Launcher", "LauncherPatcher"];

    /// <summary>
    /// Legacy alias used by hide-all paths. Prefer <see cref="RiotUiProcessNames"/> for launch
    /// so we do not hide LeagueClient / the game the user just started.
    /// </summary>
    public static readonly string[] RiotProcessNames = RiotUiProcessNames;

    // Process-lifetime pin — never free. Eliminates GC FailFast on native callbacks.
    private static readonly EnumWindowsProc EnumProc = EnumCallback;
    private static readonly EnumWindowsProc EnumCollapseProc = EnumCollapseCallback;
    private static readonly EnumWindowsProc EnumRevealProc = EnumRevealCallback;
    // ReSharper disable once NotAccessedField.Local — keeps the delegate rooted.
    private static readonly GCHandle EnumProcPin = GCHandle.Alloc(EnumProc);
    // ReSharper disable once NotAccessedField.Local
    private static readonly GCHandle EnumCollapseProcPin = GCHandle.Alloc(EnumCollapseProc);
    // ReSharper disable once NotAccessedField.Local
    private static readonly GCHandle EnumRevealProcPin = GCHandle.Alloc(EnumRevealProc);

    /// <summary>HWNDs we hid → original extended style (so we can undo TOOLWINDOW leftovers).</summary>
    private static readonly ConcurrentDictionary<nint, nint> s_originalExStyle = new();

    // The original-style map is intentionally shared: short install/auth hiders
    // overlap the session-long game hider. Do not let the first one that stops
    // restore and clear styles while another guard is still suppressing chrome.
    private static readonly WindowSuppressionOwnership s_suppressionOwnership = new();

    [ThreadStatic]
    private static HashSet<string>? t_activeNames;

    [ThreadStatic]
    private static Func<string, bool>? t_shouldSuppressName;

    /// <summary>PIDs observed by the polling safety net. WinEvent callbacks
    /// also validate their own hook-local process-name set because concurrent
    /// hiders can refresh different store sets at the same time.</summary>
    private static readonly ConcurrentDictionary<uint, byte> s_trackedPids = new();

    private readonly string[] _names;
    private readonly Func<string, bool>? _shouldSuppressName;
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private bool _disposed;
    private bool _restoreOnStop = true;
    private bool _hasSuppressionOwnership;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public StoreWindowHider(params string[] processNames)
        : this(processNames, null)
    {
    }

    private StoreWindowHider(string[] processNames, Func<string, bool>? shouldSuppressName)
    {
        _names = processNames.Length > 0 ? processNames : SteamProcessNames;
        _shouldSuppressName = shouldSuppressName;
    }

    public static StoreWindowHider ForSteam() =>
        new(SteamProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Steam));
    public static StoreWindowHider ForEpic() =>
        new(EpicProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Epic));
    public static StoreWindowHider ForGalaxy() =>
        new(GalaxyProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Gog));
    public static StoreWindowHider ForRiot() =>
        new(RiotUiProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Riot));
    internal static StoreWindowHider ForAllStoreChrome() => new(
        // The session guard is for client/message chrome only. In-game overlays
        // belong to the game and are intentionally outside this surface set.
        SteamMainProcessNames.Concat(["steamerrorreporter"])
            .Concat(EpicProcessNames)
            .Concat(GalaxyProcessNames)
            .Concat(RiotUiProcessNames)
            .Concat(XboxClientProcessNames)
            .Concat(EaClientProcessNames)
            .Concat(UbisoftClientProcessNames)
            .Concat(BattleNetClientProcessNames)
            .Concat(AmazonClientProcessNames)
            .Concat(RockstarClientProcessNames)
            .ToArray(),
        HiddenStoreRuntime.IsStoreSurfaceSuppressed);

    /// <param name="duration">How long to keep polling hide.</param>
    /// <param name="restoreOnStop">
    /// When false, leave windows SW_HIDE after stop (Play handoff).
    /// Settings → Open Steam still restores via <see cref="RestoreStoreWindows"/>.
    /// Default true so install/auth paths do not leave chrome stuck forever.
    /// </param>
    public void Start(TimeSpan duration, bool restoreOnStop = true)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            StopCore();
            _restoreOnStop = restoreOnStop;
            _cts = new CancellationTokenSource(duration);
            StartCore(_cts.Token);
        }
    }

    private void StartCore(CancellationToken token)
    {
        var names = _names;
        s_suppressionOwnership.Acquire();
        _hasSuppressionOwnership = true;
        try
        {
            RefreshTrackedPids(names);
            SuppressAllNow(names, _shouldSuppressName);
            StartShowHook();
            _pollTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    // Safety net only. The WinEvent hook does the fast hiding; this
                    // catches windows that already existed and keeps the pid set warm.
                    try
                    {
                        RefreshTrackedPids(names);
                        SuppressAllNow(names, _shouldSuppressName);
                    }
                    catch { /* best-effort */ }
                    try { await Task.Delay(250, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, CancellationToken.None);
        }
        catch
        {
            // Thread start failures are rare, but must not leave a phantom
            // global owner that prevents the next normal scope from restoring.
            ReleaseSuppressionOwnership();
            throw;
        }
    }

    /// <summary>
    /// Hide store windows the moment they are created or shown.
    /// Polling alone left a window on screen for up to a full poll interval,
    /// which is the flash users saw when a game was launched.
    /// </summary>
    private void StartShowHook()
    {
        if (_hookThread is not null) return;
        var names = _names;
        var ready = new ManualResetEventSlim(false);
        _hookThread = new Thread(() =>
        {
            t_activeNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            t_shouldSuppressName = _shouldSuppressName;
            _hookThreadId = GetCurrentThreadId();
            WinEventProc callback = OnWindowEvent;
            var hook = SetWinEventHook(
                EventObjectCreate, EventObjectShow, IntPtr.Zero, callback,
                0, 0, WineventOutofcontext | WineventSkipownprocess);
            ready.Set();
            try
            {
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch { /* pump torn down */ }
            finally
            {
                if (hook != IntPtr.Zero) UnhookWinEvent(hook);
                GC.KeepAlive(callback);
            }
        })
        {
            IsBackground = true,
            Name = "exo-store-window-guard",
        };
        _hookThread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    private static void OnWindowEvent(IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hWnd == IntPtr.Zero || idObject != ObjidWindow || idChild != 0) return;
        try
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0 || !IsTrackedProcess(pid)) return;
            SuppressWindow(hWnd);
        }
        catch { /* never throw across the native boundary */ }
    }

    private static void RefreshTrackedPids(IEnumerable<string> processNames)
    {
        var live = new HashSet<uint>();
        foreach (var name in processNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in procs)
            {
                try { if (!p.HasExited) live.Add((uint)p.Id); }
                catch { /* */ }
                finally { p.Dispose(); }
            }
        }
        foreach (var pid in live) s_trackedPids[pid] = 0;
        foreach (var known in s_trackedPids.Keys.ToArray())
        {
            if (!live.Contains(known)) s_trackedPids.TryRemove(known, out _);
        }
    }

    public void Stop()
    {
        lock (_lifecycleGate)
            StopCore();
    }

    private void StopCore()
    {
        var cts = _cts;
        var pollTask = _pollTask;
        var hookThread = _hookThread;
        var hookThreadId = _hookThreadId;
        _cts = null;
        _pollTask = null;
        _hookThread = null;
        _hookThreadId = 0;

        try { cts?.Cancel(); } catch { /* */ }
        try { pollTask?.Wait(1500); } catch { /* */ }
        try { cts?.Dispose(); } catch { /* */ }
        if (hookThread is not null)
        {
            try { PostThreadMessage(hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero); } catch { /* */ }
            try { hookThread.Join(1500); } catch { /* */ }
        }
        ReleaseSuppressionOwnership();
        // Play path: leave SW_HIDE + TOOLWINDOW; Open Steam clears and reveals.
    }

    private void ReleaseSuppressionOwnership()
    {
        if (!_hasSuppressionOwnership) return;
        _hasSuppressionOwnership = false;
        // Restoration and a new guard acquisition share one gate. Without that
        // atomic handoff, a new overlapping guard can hide a window between the
        // final release and RestoreTrackedStyles(), briefly revealing chrome.
        _ = s_suppressionOwnership.Release(_restoreOnStop, RestoreTrackedStyles);
    }

    /// <summary>One-shot hide for named processes (no poll loop). Styles are still tracked.</summary>
    public static void HideOnce(params string[] processNames) =>
        SuppressAllNow(processNames);

    /// <summary>
    /// Keep the WinEvent guard alive until its owner explicitly stops it. This
    /// is used for a verified Exo game session, not for the app lifetime, so a
    /// launcher the user opens outside Exo remains entirely untouched.
    /// </summary>
    internal void StartUntilStopped(bool restoreOnStop = true)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            StopCore();
            _restoreOnStop = restoreOnStop;
            _cts = new CancellationTokenSource();
            StartCore(_cts.Token);
        }
    }

    /// <summary>
    /// Put titled store chrome back on screen (Settings → Open Steam / Epic / GOG).
    /// </summary>
    public static void RestoreStoreWindows(params string[] processNames)
    {
        var names = processNames.Length > 0
            ? processNames
            : SteamProcessNames
                .Concat(EpicProcessNames)
                .Concat(GalaxyProcessNames)
                .Concat(RiotUiProcessNames)
                .Concat(XboxClientProcessNames)
                .Concat(EaClientProcessNames)
                .Concat(UbisoftClientProcessNames)
                .Concat(BattleNetClientProcessNames)
                .Concat(AmazonClientProcessNames)
                .Concat(RockstarClientProcessNames)
                .ToArray();
        RevealProcessWindows(names);
    }

    private static bool EnumCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return true;
            if (lParam != IntPtr.Zero && pid != unchecked((uint)lParam.ToInt32()))
                return true;
            if (lParam == IntPtr.Zero)
            {
                if (IsTrackedProcess(pid))
                    SuppressWindow(hWnd);
            }
            else
            {
                SuppressWindow(hWnd);
            }
        }
        catch { /* ignore */ }
        return true;
    }

    /// <summary>
    /// Real store chrome has a title ("Steam", "Epic Games Launcher", …).
    /// Untitled HWNDs are CEF/SDL helper surfaces — hiding them is fine; restoring them
    /// creates dozens of blank taskbar entries.
    /// </summary>
    private static bool IsChromeWindow(IntPtr hWnd)
    {
        try
        {
            if (!IsWindow(hWnd)) return false;
            var sb = new System.Text.StringBuilder(512);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SuppressAllNow(IEnumerable<string> processNames, Func<string, bool>? shouldSuppressName = null)
    {
        var activeNames = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        t_activeNames = activeNames;
        t_shouldSuppressName = shouldSuppressName;
        foreach (var name in activeNames)
        {
            if (shouldSuppressName is not null && !shouldSuppressName(name)) continue;
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (p.HasExited) continue;
                    EnumWindows(EnumProc, new IntPtr(p.Id));
                }
                catch { /* ignore */ }
                finally { p.Dispose(); }
            }
        }
    }

    private static bool IsTrackedProcess(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return t_activeNames?.Contains(p.ProcessName) == true &&
                   (t_shouldSuppressName?.Invoke(p.ProcessName) ?? true);
        }
        catch
        {
            return false;
        }
    }

    private static void SuppressWindow(IntPtr hWnd)
    {
        try
        {
            var original = GetWindowLongPtr(hWnd, GwlExstyle);
            // Only remember titled chrome for later Open Steam restore.
            if (IsChromeWindow(hWnd))
                s_originalExStyle.TryAdd(hWnd, original);

            // Off the main taskbar (tray overflow only). The original style is
            // retained for cleanup, but Exo never reveals the store window.
            var ex = original.ToInt64();
            ex = (ex | WsExToolwindow) & ~WsExAppwindow;
            SetWindowLongPtr(hWnd, GwlExstyle, new IntPtr(ex));
            ShowWindow(hWnd, SwHide);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Restore a tracked window's original extended style while keeping it hidden.
    /// </summary>
    private static void RestoreTrackedStyles()
    {
        foreach (var kv in s_originalExStyle.ToArray())
        {
            var hWnd = (IntPtr)kv.Key;
            try
            {
                if (!IsWindow(hWnd))
                {
                    s_originalExStyle.TryRemove(kv.Key, out _);
                    continue;
                }
            // Restore the exact original style, then remain hidden. Replacing it
            // with APPWINDOW was the source of duplicate main-taskbar buttons.
            SetWindowLongPtr(hWnd, GwlExstyle, kv.Value);
                ShowWindow(hWnd, SwHide);
            }
            catch { /* */ }
            finally
            {
                s_originalExStyle.TryRemove(kv.Key, out _);
            }
        }
    }

    private static bool EnumCollapseCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return true;
            if (lParam != IntPtr.Zero && pid != unchecked((uint)lParam.ToInt32()))
                return true;
            // Hide titled + untitled — nothing from this store on the big taskbar.
            SuppressWindow(hWnd);
        }
        catch { /* */ }
        return true;
    }

    /// <summary>
    /// Put a vendor client back on screen.
    /// Used only when a step cannot complete without it — a Riot sign-in, a Steam
    /// login prompt. Hiding those forever means the launch silently never happens,
    /// which is worse than briefly admitting the dependency exists.
    /// </summary>
    public static void RevealProcessWindows(params string[] processNames)
    {
        if (processNames.Length == 0) return;
        foreach (var name in processNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    if (p.HasExited) continue;
                    s_trackedPids.TryRemove((uint)p.Id, out _);
                    EnumWindows(EnumRevealProc, new IntPtr(p.Id));
                }
                catch { /* */ }
                finally { p.Dispose(); }
            }
        }
    }

    private static bool EnumRevealCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return true;
            if (lParam != IntPtr.Zero && pid != unchecked((uint)lParam.ToInt32())) return true;
            // Only real chrome comes back; helper surfaces would be blank taskbar spam.
            if (!IsChromeWindow(hWnd)) return true;
            // Prefer the main Steam frame. Secondary titled CEF views become
            // extra taskbar "Steam" buttons if restored.
            if (!IsPrimaryStoreChrome(hWnd)) return true;

            var ex = GetWindowLongPtr(hWnd, GwlExstyle).ToInt64();
            ex = (ex & ~WsExToolwindow) | WsExAppwindow;
            SetWindowLongPtr(hWnd, GwlExstyle, new IntPtr(ex));
            s_originalExStyle.TryRemove(hWnd, out _);
            ShowWindow(hWnd, SwShow);
            ShowWindow(hWnd, SwRestore);
            SetForegroundWindow(hWnd);
        }
        catch { /* */ }
        return true;
    }

    /// <summary>
    /// Main store shell — not every CEF popup titled with the product name.
    /// </summary>
    private static bool IsPrimaryStoreChrome(IntPtr hWnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(512);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0) return false;
            // Steam main frame is usually just "Steam"; store deep-links keep that
            // title. Skip "Steam - News", overlay helpers, etc. when possible —
            // but still allow exact "Steam".
            if (title.Equals("Steam", StringComparison.OrdinalIgnoreCase)) return true;
            if (title.Equals("Epic Games Launcher", StringComparison.OrdinalIgnoreCase)) return true;
            if (title.Equals("GOG Galaxy", StringComparison.OrdinalIgnoreCase)) return true;
            if (title.Contains("Riot Client", StringComparison.OrdinalIgnoreCase)) return true;
            // Other vendors / unknown titles: only if sizable (real shell).
            GetWindowRect(hWnd, out var rc);
            var w = rc.Right - rc.Left;
            var h = rc.Bottom - rc.Top;
            return w >= 640 && h >= 400;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    public static void CollapseOrphanSurfaces(params string[] processNames) =>
        CollapseOrphanSurfaces(processNames, pathMustContain: null);

    /// <summary>
    /// Force store chrome off the main taskbar (SW_HIDE + temporary TOOLWINDOW).
    /// Steam’s tray icon stays in the overflow; Settings → Open Steam restores one window.
    /// When <paramref name="pathMustContain"/> is set, processes whose image
    /// path is unknown or does not contain that fragment are left alone.
    /// </summary>
    public static void CollapseOrphanSurfaces(string[] processNames, string? pathMustContain)
    {
        var names = processNames.Length > 0 ? processNames : SteamProcessNames;
        foreach (var name in names)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.HasExited) continue;
                        if (!ProcessHelper.MatchesOptionalPath(p, pathMustContain)) continue;
                        EnumWindows(EnumCollapseProc, new IntPtr(p.Id));
                    }
                    catch { /* */ }
                    finally { p.Dispose(); }
                }
            }
            catch { /* */ }
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            StopCore();
        }
    }
}

/// <summary>
/// Coordinates the shared original-style map across independent hiders. A
/// hider's lifetime can overlap a short store operation with a game session;
/// styles are restored only when the final owner requests restoration.
/// </summary>
internal sealed class WindowSuppressionOwnership
{
    private readonly object _gate = new();
    private int _owners;

    internal int ActiveOwners
    {
        get
        {
            lock (_gate) return _owners;
        }
    }

    internal void Acquire()
    {
        lock (_gate)
            _owners = checked(_owners + 1);
    }

    /// <summary>
    /// Executes <paramref name="restoreTrackedStyles"/> under the ownership
    /// lock when this is the final owner. That makes the final restore and the
    /// next guard's acquisition an atomic handoff.
    /// </summary>
    internal bool Release(bool restoreOnStop, Action restoreTrackedStyles)
    {
        ArgumentNullException.ThrowIfNull(restoreTrackedStyles);
        lock (_gate)
        {
            if (_owners == 0) return false;
            _owners--;
            if (_owners != 0 || !restoreOnStop) return false;
            restoreTrackedStyles();
            return true;
        }
    }
}
