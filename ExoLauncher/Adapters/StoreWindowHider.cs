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
    public static readonly string[] ItchClientProcessNames = ["itch"];
    public static readonly string[] MinecraftClientProcessNames = ["MinecraftLauncher"];
    public static readonly string[] RobloxClientProcessNames = ["RobloxPlayerLauncher"];
    public static readonly string[] ParadoxClientProcessNames = ["Paradox Launcher", "ParadoxLauncher"];
    public static readonly string[] WargamingClientProcessNames = ["wgc"];

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
    private static readonly object s_visibilityGate = new();

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
    private int _hasSuppressionOwnership;

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
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

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

    /// <summary>False for the all-store guard: it quiets clients, it does not claim them.</summary>
    private readonly bool _owns;

    private StoreWindowHider(string[] processNames, Func<string, bool>? shouldSuppressName, bool owns = true)
    {
        _names = processNames.Length > 0 ? processNames : SteamProcessNames;
        _shouldSuppressName = shouldSuppressName;
        _owns = owns;
    }

    public static StoreWindowHider ForSteam() =>
        new(SteamProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Steam));
    public static StoreWindowHider ForEpic() =>
        new(EpicProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Epic));
    public static StoreWindowHider ForGalaxy() =>
        new(GalaxyProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Gog));
    public static StoreWindowHider ForRiot() =>
        new(RiotUiProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Riot));
    public static StoreWindowHider ForXbox() =>
        new(XboxClientProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Xbox));
    public static StoreWindowHider ForEa() =>
        new(EaClientProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Ea));
    public static StoreWindowHider ForUbisoft() =>
        new(UbisoftClientProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Ubisoft));
    public static StoreWindowHider ForBattleNet() =>
        new(BattleNetClientProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.BattleNet));
    public static StoreWindowHider ForAmazon() =>
        new(AmazonClientProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Amazon));
    public static StoreWindowHider ForRockstar() =>
        new(RockstarClientProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Rockstar));
    public static StoreWindowHider ForItch() =>
        new(ItchClientProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Itch));
    public static StoreWindowHider ForMinecraft() =>
        new(MinecraftClientProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Minecraft));
    public static StoreWindowHider ForRoblox() =>
        new(RobloxClientProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Roblox));
    public static StoreWindowHider ForParadox() =>
        new(ParadoxClientProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Paradox));
    public static StoreWindowHider ForWargaming() =>
        new(WargamingClientProcessNames, _ => !HiddenStoreRuntime.IsSuspended(StoreKind.Wargaming));
    internal static StoreWindowHider? For(StoreKind store) => store switch
    {
        StoreKind.Steam => ForSteam(),
        StoreKind.Epic => ForEpic(),
        StoreKind.Gog => ForGalaxy(),
        StoreKind.Riot => ForRiot(),
        StoreKind.Xbox => ForXbox(),
        StoreKind.Ea => ForEa(),
        StoreKind.Ubisoft => ForUbisoft(),
        StoreKind.BattleNet => ForBattleNet(),
        StoreKind.Amazon => ForAmazon(),
        StoreKind.Rockstar => ForRockstar(),
        StoreKind.Itch => ForItch(),
        StoreKind.Minecraft => ForMinecraft(),
        StoreKind.Roblox => ForRoblox(),
        StoreKind.Paradox => ForParadox(),
        StoreKind.Wargaming => ForWargaming(),
        _ => null,
    };

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
            .Concat(ItchClientProcessNames)
            .Concat(MinecraftClientProcessNames)
            .Concat(RobloxClientProcessNames)
            .Concat(ParadoxClientProcessNames)
            .Concat(WargamingClientProcessNames)
            .ToArray(),
        HiddenStoreRuntime.IsStoreSurfaceSuppressed,
        owns: false);

    /// <param name="duration">How long to keep polling hide.</param>
    /// <remarks>Stopping always restores original window styles and preserves
    /// the current visibility, including a user reopening it.</remarks>
    public void Start(TimeSpan duration)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            StopCore();
            HiddenStoreRuntime.BeginHandoff(_names, _owns);
            _cts = new CancellationTokenSource(HandoffLimit(duration));
            StartCore(_cts.Token);
        }
    }

    /// <summary>
    /// The hook alone, with no time limit, for as long as Exo runs a client:
    /// a window a client Exo owns shows later (after an update, a scan, a
    /// game's exit) is hidden on sight. HiddenStoreRuntime's sweep is its
    /// safety net, so this guard does not poll.
    /// </summary>
    internal void StartSession()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _cts is not null) return;
            _cts = new CancellationTokenSource();
            s_suppressionOwnership.Acquire();
            Volatile.Write(ref _hasSuppressionOwnership, 1);
            try { StartShowHook(_cts.Token); }
            catch { ReleaseSuppressionOwnership(); throw; }
        }
    }

    private void StartCore(CancellationToken token)
    {
        var names = _names;
        s_suppressionOwnership.Acquire();
        Volatile.Write(ref _hasSuppressionOwnership, 1);
        try
        {
            RefreshTrackedPids(names);
            SuppressAllNow(names, _shouldSuppressName);
            StartShowHook(token);
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
    private void StartShowHook(CancellationToken token)
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
            using var cancelHook = token.Register(() => PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero));
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
                ReleaseSuppressionOwnership();
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
            // Reopening a frame Exo already hid belongs to the user/client.
            if (eventType == EventObjectShow && IsWindowVisible(hWnd) && s_originalExStyle.ContainsKey(hWnd))
            {
                using var process = Process.GetProcessById((int)pid);
                if (HiddenStoreRuntime.StoreForProcess(process.ProcessName) is { } store)
                {
                    HiddenStoreRuntime.AllowUserOpen(store);
                    RevealProcessWindows(process.ProcessName);
                    return;
                }
            }
            SuppressWindow(hWnd);
        }
        catch { /* never throw across the native boundary */ }
    }

    private static void RefreshTrackedPids(IEnumerable<string> processNames)
    {
        var live = new HashSet<uint>();
        foreach (var (processId, _) in SnapshotNamedProcesses(processNames))
            live.Add((uint)processId);
        foreach (var pid in live) s_trackedPids[pid] = 0;
        foreach (var known in s_trackedPids.Keys.ToArray())
        {
            if (!live.Contains(known)) s_trackedPids.TryRemove(known, out _);
        }
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
        // Preserve visibility while restoring normal taskbar behavior.
    }

    private void ReleaseSuppressionOwnership()
    {
        if (Interlocked.Exchange(ref _hasSuppressionOwnership, 0) == 0) return;
        // Restoration and a new guard acquisition share one gate. Without that
        // atomic handoff, a new overlapping guard can hide a window between the
        // final release and RestoreTrackedStyles(), briefly revealing chrome.
        // Every final guard restores taskbar styles. A quiet handoff keeps the
        // current visibility, so reopening a client works normally afterwards.
        _ = s_suppressionOwnership.Release(true, RestoreTrackedStyles);
    }

    /// <summary>One-shot hide for named processes (no poll loop). Styles are still tracked.</summary>
    public static void HideOnce(params string[] processNames) =>
        SuppressAllNow(processNames);

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

    internal static bool IsAgreementTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        return title.Contains("EULA", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("License Agreement", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Subscriber Agreement", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("End User License", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAgreementWindow(IntPtr hWnd)
    {
        try
        {
            if (!IsWindow(hWnd)) return false;
            var sb = new System.Text.StringBuilder(512);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return IsAgreementTitle(sb.ToString());
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRedeemWindow(IntPtr hWnd)
    {
        try
        {
            if (!IsWindow(hWnd)) return false;
            var sb = new System.Text.StringBuilder(512);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            return title.Contains("Activate", StringComparison.OrdinalIgnoreCase)
                   || title.Contains("Product Activation", StringComparison.OrdinalIgnoreCase)
                   || title.Contains("Register Product", StringComparison.OrdinalIgnoreCase)
                   || title.Contains("Redeem", StringComparison.OrdinalIgnoreCase);
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
        foreach (var (processId, name) in SnapshotNamedProcesses(activeNames))
        {
            if (shouldSuppressName is not null && !shouldSuppressName(name)) continue;
            try { EnumWindows(EnumProc, new IntPtr(processId)); }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Live PIDs whose process name is in <paramref name="processNames"/>.
    /// Looking each name up individually snapshots the whole process table once
    /// per name, and the guard polls up to 25 names four times a second for a
    /// full game session. One snapshot per pass keeps that cost flat.
    /// </summary>
    internal static List<(int ProcessId, string Name)> SnapshotNamedProcesses(IEnumerable<string> processNames)
    {
        var wanted = processNames is HashSet<string> set &&
                     ReferenceEquals(set.Comparer, StringComparer.OrdinalIgnoreCase)
            ? set
            : new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        var hits = new List<(int, string)>();
        if (wanted.Count == 0) return hits;

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return hits; }

        foreach (var process in all)
        {
            try
            {
                var name = process.ProcessName;
                if (wanted.Contains(name) && !process.HasExited)
                    hits.Add((process.Id, name));
            }
            catch { /* process exited during inspection */ }
            finally { process.Dispose(); }
        }

        return hits;
    }

    /// <summary>
    /// Process names by pid for the window hook, which sees every window any
    /// process creates for as long as Exo runs a client; a lookup per event
    /// would open a process handle hundreds of times a second. Cleared every
    /// few seconds so a reused pid is named again.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, string> s_processNames = new();
    private static long s_processNamesUntil;

    private static bool IsTrackedProcess(uint pid)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now >= Interlocked.Read(ref s_processNamesUntil))
            {
                s_processNames.Clear();
                Interlocked.Exchange(ref s_processNamesUntil, now + 5000);
            }
            if (!s_processNames.TryGetValue(pid, out var name))
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
                s_processNames[pid] = name;
            }
            return t_activeNames?.Contains(name) == true &&
                   (t_shouldSuppressName?.Invoke(name) ?? true);
        }
        catch
        {
            return false;
        }
    }

    private static void SuppressWindow(IntPtr hWnd)
    {
        lock (s_visibilityGate)
        try
        {
            if (!HiddenStoreRuntime.IsAnyStoreQuiet || !IsWindowVisible(hWnd)) return;
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return;
            using var process = Process.GetProcessById((int)pid);
            var store = HiddenStoreRuntime.StoreForProcess(process.ProcessName);
            if (store is { } kind)
            {
                if (!HiddenStoreRuntime.IsStoreQuiet(kind) || HiddenStoreRuntime.IsSuspended(kind)) return;
            }
            var title = new System.Text.StringBuilder(512);
            _ = GetWindowText(hWnd, title, title.Capacity);
            if (!IsQuietStoreShell(store, title.ToString(), GetWindow(hWnd, 4) != IntPtr.Zero)) return;
            var original = GetWindowLongPtr(hWnd, GwlExstyle);
            // Only remember titled chrome for later Open Steam restore.
            if (IsAgreementWindow(hWnd))
                return;
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
    /// Restore a tracked window's original extended style without changing visibility.
    /// </summary>
    private static void RestoreTrackedStyles()
    {
        lock (s_visibilityGate)
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
                // A user may have reopened this window during the handoff.
                SetWindowLongPtr(hWnd, GwlExstyle, kv.Value);
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
            // Only a recognized main shell is eligible for quiet handoff.
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
        foreach (var (processId, _) in SnapshotNamedProcesses(processNames))
        {
            try
            {
                s_trackedPids.TryRemove((uint)processId, out _);
                EnumWindows(EnumRevealProc, new IntPtr(processId));
            }
            catch { /* */ }
        }
    }

    private static bool EnumRevealCallback(IntPtr hWnd, IntPtr lParam)
    {
        lock (s_visibilityGate)
        try
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return true;
            if (lParam != IntPtr.Zero && pid != unchecked((uint)lParam.ToInt32())) return true;
            if (IsAgreementWindow(hWnd) || IsRedeemWindow(hWnd))
            {
                ShowWindow(hWnd, SwShow);
                ShowWindow(hWnd, SwRestore);
                SetForegroundWindow(hWnd);
                return true;
            }
            // Only real chrome comes back; helper surfaces would be blank taskbar spam.
            if (!IsChromeWindow(hWnd)) return true;
            // Required launch dialogs can be smaller than the main frame.
            // Preserve their own taskbar styles when restoring them.
            var primary = IsPrimaryStoreChrome(hWnd);

            var ex = GetWindowLongPtr(hWnd, GwlExstyle).ToInt64();
            if (s_originalExStyle.TryRemove(hWnd, out var original)) ex = original.ToInt64();
            else if (primary) ex = (ex & ~WsExToolwindow) | WsExAppwindow;
            SetWindowLongPtr(hWnd, GwlExstyle, new IntPtr(ex));
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

    internal static TimeSpan HandoffLimit(TimeSpan requested) =>
        requested < HiddenStoreRuntime.HandoffDuration ? requested : HiddenStoreRuntime.HandoffDuration;

    internal static HashSet<StoreKind> VisibleMainStores(IEnumerable<StoreKind> stores)
    {
        var wanted = stores.ToHashSet();
        var visible = new HashSet<StoreKind>();
        EnumWindows((window, _) =>
        {
            try
            {
                if (!IsWindowVisible(window) || !IsPrimaryStoreChrome(window)) return true;
                GetWindowThreadProcessId(window, out var pid);
                using var process = Process.GetProcessById((int)pid);
                if (HiddenStoreRuntime.StoreForProcess(process.ProcessName) is { } store && wanted.Contains(store))
                    visible.Add(store);
            }
            catch { /* window closed during enumeration */ }
            return true;
        }, IntPtr.Zero);
        return visible;
    }

    internal static bool RevealClientWindows(StoreClientLaunchCommand command, IReadOnlyList<string> processNames)
    {
        var names = processNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!command.IsAppx) names.Add(Path.GetFileNameWithoutExtension(command.FileName));
        var visible = false;
        foreach (var (processId, _) in SnapshotNamedProcesses(names))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!MatchesClientInstall(command, ProcessHelper.TryGetExecutablePath(process))) continue;
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var owner);
                    if (owner != processId || !IsChromeWindow(window)) return true;
                    GetWindowRect(window, out var rect);
                    if (rect.Right - rect.Left < 200 || rect.Bottom - rect.Top < 100) return true;
                    EnumRevealCallback(window, new IntPtr(processId));
                    visible |= IsWindowVisible(window);
                    return true;
                }, IntPtr.Zero);
            }
            catch { /* client updated or exited while its window was being inspected */ }
        }
        return visible;
    }

    internal static bool MatchesClientInstall(StoreClientLaunchCommand command, string? processPath)
    {
        if (command.IsAppx) return true; // callers already restricted the registered app's process names
        if (string.IsNullOrWhiteSpace(processPath)) return false;
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(command.FileName));
            return folder is not null && Path.GetFullPath(processPath).StartsWith(
                folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool IsQuietStoreShell(StoreKind? store, string? title, bool hasOwner)
    {
        // Unrecognized windows may be required game-launch or sign-in prompts.
        if (hasOwner || string.IsNullOrWhiteSpace(title)) return false;
        string[] titles = store switch
        {
            StoreKind.Steam => ["Steam"], StoreKind.Epic => ["Epic Games Launcher"],
            StoreKind.Gog => ["GOG Galaxy"], StoreKind.Riot => ["Riot Client"],
            StoreKind.Xbox => ["Xbox"], StoreKind.Ea => ["EA", "EA app"],
            StoreKind.Ubisoft => ["Ubisoft Connect", "Ubisoft Game Launcher", "Uplay"],
            StoreKind.BattleNet => ["Battle.net"], StoreKind.Amazon => ["Amazon Games"],
            StoreKind.Rockstar => ["Rockstar Games Launcher"], StoreKind.Itch => ["itch"],
            StoreKind.Minecraft => ["Minecraft Launcher"], StoreKind.Paradox => ["Paradox Launcher"],
            StoreKind.Wargaming => ["Wargaming.net Game Center", "Wargaming Game Center"],
            _ => [],
        };
        return titles.Contains(title.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

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
        foreach (var (processId, _) in SnapshotNamedProcesses(names))
        {
            try
            {
                if (!ProcessHelper.MatchesOptionalPath(processId, pathMustContain)) continue;
                EnumWindows(EnumCollapseProc, new IntPtr(processId));
            }
            catch { /* */ }
        }
    }

    internal sealed record SurfaceSnapshot(IReadOnlyList<(int ProcessId, string Name)> Processes,
        IReadOnlyList<(IntPtr Window, uint ProcessId)> Windows);

    internal static SurfaceSnapshot CaptureSurfaces(IEnumerable<string> names)
    {
        var processes = SnapshotNamedProcesses(names);
        var ids = processes.Select(p => (uint)p.ProcessId).ToHashSet();
        var windows = new List<(IntPtr, uint)>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (ids.Contains(pid)) windows.Add((window, pid));
            return true;
        }, IntPtr.Zero);
        return new(processes, windows);
    }

    internal static void CollapseOrphanSurfaces(SurfaceSnapshot snapshot, string[] names, string? pathMustContain)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = snapshot.Processes.Where(p => wanted.Contains(p.Name) &&
            ProcessHelper.MatchesOptionalPath(p.ProcessId, pathMustContain)).Select(p => (uint)p.ProcessId).ToHashSet();
        foreach (var (window, pid) in snapshot.Windows)
            if (ids.Contains(pid)) EnumCollapseCallback(window, new IntPtr(pid));
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
