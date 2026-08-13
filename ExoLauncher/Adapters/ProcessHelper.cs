using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ExoLauncher.Adapters;

internal static class ProcessHelper
{
    private const int SwHide = 0;
    private const int SwShowMinimized = 2;
    private const int SwMinimize = 6;
    private const uint WmClose = 0x0010;
    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        int dwFlags,
        StringBuilder lpExeName,
        ref int lpdwSize);

    private const uint Th32CsSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    /// <summary>
    /// Resolves a process image path without requiring PROCESS_VM_READ.
    /// <see cref="Process.MainModule"/> fails for many games (WOW64, protected,
    /// elevated), which made Stop/canStop silently miss live sessions.
    /// </summary>
    internal static string? TryGetExecutablePath(Process process)
    {
        try
        {
            if (process.HasExited) return null;
        }
        catch { return null; }

        try
        {
            var viaQuery = QueryFullProcessImageName(process.Id);
            if (!string.IsNullOrWhiteSpace(viaQuery))
                return Path.GetFullPath(viaQuery);
        }
        catch { /* fall through */ }

        try
        {
            var module = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(module))
                return Path.GetFullPath(module);
        }
        catch { /* access denied is common */ }

        return null;
    }

    internal static string? TryGetExecutablePath(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(processId);
            return TryGetExecutablePath(process);
        }
        catch { return null; }
    }

    internal static bool MatchesOptionalPath(Process process, string? pathMustContain)
    {
        if (string.IsNullOrWhiteSpace(pathMustContain)) return true;
        var path = TryGetExecutablePath(process);
        return !string.IsNullOrWhiteSpace(path) &&
               path.IndexOf(pathMustContain, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string? QueryFullProcessImageName(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var capacity = 1024;
            var buffer = new StringBuilder(capacity);
            var size = capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size) || size <= 0)
                return null;
            return buffer.ToString(0, size);
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    // Process-lifetime pin — NEVER free. Transient lambdas here caused FailFast.
    private static readonly EnumWindowsProc EnumProc = EnumCallback;
    private static readonly EnumWindowsProc EnumCloseProc = EnumCloseCallback;
    // ReSharper disable once NotAccessedField.Local
    private static readonly GCHandle EnumProcPin = GCHandle.Alloc(EnumProc);
    // ReSharper disable once NotAccessedField.Local
    private static readonly GCHandle EnumClosePin = GCHandle.Alloc(EnumCloseProc);

    // Every generic install-root scan (Epic, GOG and direct handoffs) shares
    // this hard deny-list. A process being located inside a game folder is not
    // proof that it is the playable game: overlays, anti-cheat services,
    // launchers and crash helpers can all live there too. Keep this separate
    // from store-specific executable allow-lists; it is the common floor that
    // prevents one adapter from crediting a process that Stop will later refuse
    // to touch.
    private static readonly HashSet<string> NonGameProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "steamservice", "gameoverlayui", "gameoverlayui64", "steamerrorreporter",
        "epicgameslauncher", "epicwebhelper", "epiconlineservices", "eosoverlayrenderer-win64-shipping",
        "galaxyclient", "galaxyclientservice", "goggalaxynotifications", "gogdl",
        "riotclientservices", "riotclientux", "riotclientuxrender", "riotclientcrashhandler", "riot client",
        "leagueclient", "leagueclientux", "leagueclientuxrender",
        "vgc", "vgk", "easyanticheat", "easyanticheat_eos", "easyanticheat_eos_setup",
        "beservice", "beservice_x64", "battleye", "battleye_launcher", "eac_launcher",
        "start_protected_game", "start_protected_game64", "eossdk-win64-shipping",
        "crashreportclient", "crashhandler", "crashpad_handler", "unitycrashhandler32",
        "unitycrashhandler64", "unins000", "setup", "updater", "patcher", "launcher",
    };

    [ThreadStatic]
    private static int t_targetPid;
    [ThreadStatic]
    private static int t_showCmd;
    [ThreadStatic]
    private static HashSet<int>? t_closePids;

    internal static bool IsNonGameProcessName(string? processName) =>
        string.IsNullOrWhiteSpace(processName) || NonGameProcessNames.Contains(processName);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static bool EnumCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == (uint)t_targetPid)
                ShowWindow(hWnd, t_showCmd);
        }
        catch { /* ignore */ }
        return true;
    }

    private static bool EnumCloseCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            var targets = t_closePids;
            if (targets is null || targets.Count == 0) return true;
            GetWindowThreadProcessId(hWnd, out var pid);
            if (targets.Contains((int)pid))
                PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* ignore */ }
        return true;
    }

    /// <summary>
    /// Start a command-line backend without creating a console window.
    /// Browser-based auth can still open the user's browser from the CLI.
    /// </summary>
    public static Process? StartHiddenCli(string fileName, string arguments = "", string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? string.Empty,
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

    /// <summary>Visible process for interactive CLI auth (browser + prompts).</summary>
    /// <summary>Start a game executable visibly. Store clients must not use this.</summary>
    public static Process? StartGame(string fileName, string arguments = "", string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? string.Empty,
        };
        return Process.Start(psi);
    }

    /// <summary>
    /// Auth CLI runs without a console window; its browser handoff remains visible
    /// only in the user's browser, never in a store client or console.
    /// </summary>
    public static Process? StartAuthConsole(string exe, string args) =>
        StartHiddenCli(exe, args);

    public static void MinimizeProcessWindows(int processId) =>
        ApplyWindowState(processId, SwMinimize);

    /// <summary>Fully hide process top-level windows (preferred over minimize for stealth launches).</summary>
    public static void HideProcessWindows(int processId) =>
        ApplyWindowState(processId, SwHide);

    public static void HideProcessesByName(params string[] processNames)
    {
        foreach (var name in processNames)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (!p.HasExited)
                            HideProcessWindows(p.Id);
                    }
                    catch { /* ignore */ }
                    finally { p.Dispose(); }
                }
            }
            catch { /* ignore */ }
        }
    }

    private static void ApplyWindowState(int processId, int showCmd)
    {
        try
        {
            t_targetPid = processId;
            t_showCmd = showCmd;
            EnumWindows(EnumProc, IntPtr.Zero);
        }
        catch { /* best-effort */ }
    }

    public static Process? StartHidden(string fileName, string arguments = "", string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? string.Empty,
        };
        return Process.Start(psi);
    }

    /// <summary>Hidden process with argument boundaries preserved (including paths with spaces).</summary>
    public static Process? StartHidden(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? string.Empty,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        return Process.Start(psi);
    }

    public static void TryCloseProcesses(params string[] processNames) =>
        TryCloseProcesses(processNames, pathMustContain: null);

    /// <summary>
    /// Soft-close store chrome. CloseMainWindow + WM_CLOSE to all top-level windows.
    /// Never Kill() — anti-cheat / tray helpers may ignore and stay resident.
    /// When <paramref name="pathMustContain"/> is set, processes whose image
    /// path is unknown or does not contain that fragment are left alone.
    /// </summary>
    public static void TryCloseProcesses(string[] processNames, string? pathMustContain)
    {
        var pids = new HashSet<int>();
        foreach (var name in processNames)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.HasExited) continue;
                        if (!MatchesOptionalPath(p, pathMustContain)) continue;
                        pids.Add(p.Id);
                        // Soft close — never kill anti-cheat services.
                        p.CloseMainWindow();
                    }
                    catch { /* ignore */ }
                    finally { p.Dispose(); }
                }
            }
            catch { /* ignore */ }
        }

        if (pids.Count == 0) return;

        // Epic / Galaxy often ignore CloseMainWindow; WM_CLOSE on every HWND is more reliable.
        try
        {
            t_closePids = pids;
            EnumWindows(EnumCloseProc, IntPtr.Zero);
        }
        catch { /* best-effort */ }
        finally { t_closePids = null; }

        RequestThreadQuit(pids);

        // Do not HideProcessesByName here — leftover SW_HIDE + older TOOLWINDOW
        // styles made Steam unopenable from the taskbar.
    }

    private static readonly HashSet<string> NeverTerminateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "vgk", "vgc", "vgm", "EasyAntiCheat", "EasyAntiCheat_EOS",
        "EpicOnlineServices", "steamservice", "GameOverlayUI", "gameoverlayui64",
    };

    /// <summary>
    /// Last resort for an unused launcher shell after graceful close failed.
    /// Never anti-cheat, never unnamed PIDs, never a process tree.
    /// </summary>
    public static void TerminateExactNames(string[] processNames, string? pathMustContain = null)
    {
        foreach (var name in processNames)
        {
            if (string.IsNullOrWhiteSpace(name) || NeverTerminateNames.Contains(name))
                continue;
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.HasExited) continue;
                        if (!MatchesOptionalPath(p, pathMustContain)) continue;
                        p.Kill(entireProcessTree: false);
                    }
                    catch { /* ignore */ }
                    finally { p.Dispose(); }
                }
            }
            catch { /* ignore */ }
        }
    }

    private const uint WmQuit = 0x0012;

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, UIntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Silent store clients often have no main window, so WM_CLOSE never lands.
    /// WM_QUIT on their UI threads is still a graceful exit — not Kill().
    /// </summary>
    private static void RequestThreadQuit(HashSet<int> pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) continue;
                foreach (ProcessThread thread in process.Threads)
                {
                    try { PostThreadMessage((uint)thread.Id, WmQuit, UIntPtr.Zero, IntPtr.Zero); }
                    catch { /* */ }
                }
            }
            catch { /* */ }
        }
    }

    /// <summary>Requests a graceful close for exact PIDs. It never expands into a process tree.</summary>
    public static void RequestCloseProcesses(IEnumerable<int> processIds)
    {
        var pids = processIds.Where(id => id > 0).ToHashSet();
        if (pids.Count == 0) return;
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) process.CloseMainWindow();
            }
            catch { /* best effort */ }
        }
        try
        {
            t_closePids = pids;
            EnumWindows(EnumCloseProc, IntPtr.Zero);
        }
        catch { /* best effort */ }
        finally { t_closePids = null; }
    }

    public static bool IsProcessRunning(string processName)
    {
        Process[] processes = [];
        try
        {
            processes = Process.GetProcessesByName(processName);
            return processes.Any(process =>
            {
                try { return !process.HasExited; }
                catch { return false; }
            });
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    internal static bool IsPathUnderRoot(string processPath, string root)
    {
        if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(root))
            return false;
        try
        {
            var fullPath = Path.GetFullPath(processPath);
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Captures live, non-helper PIDs whose executable is inside an install root.
    /// A direct launcher uses this before it starts so an already-running title
    /// cannot be mistaken for evidence that the new request worked.
    /// </summary>
    internal static HashSet<int> SnapshotLiveProcessIdsUnderPath(
        string? installRoot,
        IEnumerable<string>? ignoredNames = null)
    {
        var ignored = new HashSet<string>(
            ignoredNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return result;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.HasExited || ignored.Contains(process.ProcessName) ||
                        IsNonGameProcessName(process.ProcessName)) continue;
                    var module = TryGetExecutablePath(process);
                    if (!string.IsNullOrWhiteSpace(module) && IsPathUnderRoot(module, installRoot))
                        result.Add(process.Id);
                }
                catch { /* process exited during inspection */ }
                finally { process.Dispose(); }
            }
        }
        catch { /* enumeration race */ }

        return result;
    }

    /// <summary>
    /// Confirms a direct game launch without treating a same-tick PID as proof.
    /// The started process must survive the bounded settle window, unless a new
    /// non-helper executable appears beneath the install root during a handoff.
    /// </summary>
    internal static async Task<int?> ConfirmDirectLaunchAsync(
        Process? starter,
        string? installRoot,
        ISet<int> processIdsBeforeLaunch,
        CancellationToken ct,
        IEnumerable<string>? ignoredNames = null,
        TimeSpan? settleWindow = null)
    {
        if (starter is null) return null;

        int starterPid;
        try { starterPid = starter.Id; }
        catch { return null; }

        var settle = settleWindow is { } configured && configured > TimeSpan.Zero
            ? configured
            : TimeSpan.FromMilliseconds(700);
        var deadline = DateTimeOffset.UtcNow + settle;
        int? observedHandoffPid = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var live = SnapshotLiveProcessIdsUnderPath(installRoot, ignoredNames);
            var handoffPid = SelectDirectLaunchProcessId(
                starterPid,
                starterAliveAtSettle: false,
                processIdsBeforeLaunch,
                live);
            if (handoffPid is not null)
                observedHandoffPid = handoffPid;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100),
                    ct)
                .ConfigureAwait(false);
        }

        var finalLive = SnapshotLiveProcessIdsUnderPath(installRoot, ignoredNames);
        return SelectSettledDirectLaunchProcessId(
            starterPid,
            starterAliveAtSettle: IsAlive(starter),
            observedHandoffPid,
            finalLive);
    }

    /// <summary>Pure decision seam for direct-launch settlement tests.</summary>
    internal static int? SelectDirectLaunchProcessId(
        int starterPid,
        bool starterAliveAtSettle,
        ISet<int> processIdsBeforeLaunch,
        IEnumerable<int> liveProcessIdsUnderInstallRoot)
    {
        if (starterPid > 0 && starterAliveAtSettle)
            return starterPid;

        foreach (var pid in liveProcessIdsUnderInstallRoot)
        {
            if (pid > 0 && pid != starterPid && !processIdsBeforeLaunch.Contains(pid))
                return pid;
        }

        return null;
    }

    /// <summary>
    /// A handoff PID is evidence only when it was observed before the deadline
    /// and is still live at the deadline. This keeps a brief helper child from
    /// turning into a false successful launch.
    /// </summary>
    internal static int? SelectSettledDirectLaunchProcessId(
        int starterPid,
        bool starterAliveAtSettle,
        int? observedHandoffPid,
        IEnumerable<int> liveProcessIdsAtSettle)
    {
        if (starterPid > 0 && starterAliveAtSettle)
            return starterPid;
        if (observedHandoffPid is not int handoffPid || handoffPid <= 0 || handoffPid == starterPid)
            return null;
        return liveProcessIdsAtSettle.Contains(handoffPid) ? handoffPid : null;
    }

    public static async Task<int?> WaitForProcessUnderPathAsync(
        string? root,
        TimeSpan timeout,
        CancellationToken ct,
        IEnumerable<string>? ignoredNames = null,
        IReadOnlySet<int>? excludedProcessIds = null,
        TimeSpan? confirmationDelay = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        var ignored = new HashSet<string>(
            ignoredNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var hit = FindMatchingProcessId(root, processNames: null, ignored, excludedProcessIds);
            var confirmed = await ConfirmNewProcessCandidateAsync(
                    hit,
                    excludedProcessIds,
                    confirmationDelay ?? TimeSpan.Zero,
                    id => FindLivePid(id) is not null,
                    ct)
                .ConfigureAwait(false);
            if (confirmed is not null) return confirmed;

            try { await Task.Delay(350, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        return null;
    }

    internal static async Task<int?> ConfirmNewProcessCandidateAsync(
        int? candidatePid,
        IReadOnlySet<int>? excludedProcessIds,
        TimeSpan confirmationDelay,
        Func<int, bool> isLive,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(isLive);
        if (candidatePid is not int pid || pid <= 0 ||
            (excludedProcessIds?.Contains(pid) ?? false))
            return null;

        if (confirmationDelay > TimeSpan.Zero)
            await Task.Delay(confirmationDelay, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        return isLive(pid) ? pid : null;
    }

    /// <summary>
    /// Watch a launched game until it exits. Handles bootstrap handoffs
    /// (Epic <c>Launcher.exe</c> → real game): prefer install-path / process-name
    /// matches over clinging to a helper seed PID. Bootstrap names in
    /// <paramref name="ignoredNames"/> never credit a session.
    /// Returns true when a real game process was observed for at least one tick.
    /// </summary>
    public static async Task<bool> TrackGameSessionAsync(
        int? seedPid,
        string? installRoot,
        IReadOnlyList<string>? processNames,
        IEnumerable<string>? ignoredNames,
        TimeSpan appearTimeout,
        TimeSpan goneDebounce,
        IReadOnlyList<string>? handoffProcessNames = null,
        TimeSpan? handoffAppearTimeout = null,
        TimeSpan? observedSeedGoneGrace = null,
        CancellationToken ct = default)
    {
        var ignored = new HashSet<string>(
            ignoredNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var unobservedGameDeadline = DateTimeOffset.UtcNow + GetUnobservedGameTimeout(
            appearTimeout,
            seedProcessWasObserved: seedPid is > 0,
            observedSeedGoneGrace);
        var sessionDeadline = DateTimeOffset.UtcNow + TimeSpan.FromHours(18);
        var sawGame = false;
        DateTimeOffset? lastSeen = null;
        var sawHandoff = false;
        DateTimeOffset? handoffLastSeen = null;
        var handoffAppearDeadline = handoffProcessNames is { Count: > 0 } &&
                                    handoffAppearTimeout is { } handoffTimeout &&
                                    handoffTimeout > TimeSpan.Zero
            ? DateTimeOffset.UtcNow + handoffTimeout
            : (DateTimeOffset?)null;

        while (DateTimeOffset.UtcNow < sessionDeadline && !ct.IsCancellationRequested)
        {
            if (!sawGame && handoffProcessNames is { Count: > 0 })
            {
                var handoffAlive = handoffProcessNames.Any(IsProcessRunning);
                if (handoffAlive)
                {
                    sawHandoff = true;
                    handoffLastSeen = DateTimeOffset.UtcNow;
                }
                else if (sawHandoff && handoffLastSeen is not null &&
                         DateTimeOffset.UtcNow - handoffLastSeen.Value >= goneDebounce)
                {
                    return false;
                }
            }

            // League can legitimately keep its product client open for a long
            // time before a match begins. That extended wait is valid only
            // after the client-to-game handoff has actually appeared; otherwise
            // an accepted-but-never-started cold launch must become retryable.
            if (HasMissedHandoff(sawGame, sawHandoff, DateTimeOffset.UtcNow, handoffAppearDeadline))
                return false;

            // Prefer a real game match. Never treat bootstrap/helper PIDs as the session.
            var match = FindMatchingProcessId(installRoot, processNames, ignored);
            var seed = FindLivePid(seedPid);
            int? pid = match;
            if (pid is null && seed is int sid && !IsIgnoredPid(sid, ignored))
                pid = sid;

            if (pid is int live)
            {
                sawGame = true;
                lastSeen = DateTimeOffset.UtcNow;
                seedPid = live; // follow handoff targets next loop
                try
                {
                    using var proc = Process.GetProcessById(live);
                    if (!proc.HasExited)
                        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // Access denied / already gone — poll below.
                    try { await Task.Delay(800, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }

                continue;
            }

            // Seed may still be a live bootstrap (Launcher / RiotClient) — keep
            // polling for the real game instead of WaitForExit on the helper.
            if (!sawGame)
            {
                if (DateTimeOffset.UtcNow >= unobservedGameDeadline)
                    return false;
            }
            else if (lastSeen is not null &&
                     DateTimeOffset.UtcNow - lastSeen.Value >= goneDebounce)
            {
                return true;
            }

            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        return sawGame;
    }

    internal static TimeSpan GetUnobservedGameTimeout(
        TimeSpan appearTimeout,
        bool seedProcessWasObserved,
        TimeSpan? observedSeedGoneGrace)
    {
        if (seedProcessWasObserved && observedSeedGoneGrace is { } grace &&
            grace > TimeSpan.Zero && grace < appearTimeout)
            return grace;
        return appearTimeout;
    }

    internal static bool HasMissedHandoff(
        bool sawGame,
        bool sawHandoff,
        DateTimeOffset now,
        DateTimeOffset? handoffAppearDeadline) =>
        !sawGame && !sawHandoff &&
        handoffAppearDeadline is { } deadline && now >= deadline;

    private static int? FindLivePid(int? pid)
    {
        if (pid is not int id || id <= 0) return null;
        try
        {
            using var proc = Process.GetProcessById(id);
            return proc.HasExited ? null : id;
        }
        catch { return null; }
    }

    private static bool IsAlive(Process process)
    {
        try { return !process.HasExited; }
        catch { return false; }
    }

    private static bool IsIgnoredPid(int pid, HashSet<string> ignored)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return IsNonGameProcessName(proc.ProcessName) || ignored.Contains(proc.ProcessName);
        }
        catch { return false; }
    }

    private static int? FindMatchingProcessId(
        string? installRoot,
        IReadOnlyList<string>? processNames,
        HashSet<string> ignored,
        IReadOnlySet<int>? excludedProcessIds = null)
    {
        if (processNames is { Count: > 0 })
        {
            var requireInstallRoot = !string.IsNullOrWhiteSpace(installRoot) &&
                                     Directory.Exists(installRoot);
            foreach (var name in processNames)
            {
                if (string.IsNullOrWhiteSpace(name) || ignored.Contains(name) ||
                    IsNonGameProcessName(name)) continue;
                try
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            if (proc.HasExited || (excludedProcessIds?.Contains(proc.Id) ?? false)) continue;
                            if (requireInstallRoot)
                            {
                                var module = TryGetExecutablePath(proc);
                                if (string.IsNullOrWhiteSpace(module) ||
                                    !IsPathUnderRoot(module, installRoot!))
                                    continue;
                            }
                            return proc.Id;
                        }
                        finally { proc.Dispose(); }
                    }
                }
                catch { /* */ }
            }

            // A caller that supplied exact executable names asked for those
            // processes specifically. Do not fall back to an arbitrary helper
            // merely because it also lives below the install root.
            return null;
        }

        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return null;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (ignored.Contains(process.ProcessName) ||
                        IsNonGameProcessName(process.ProcessName) ||
                        (excludedProcessIds?.Contains(process.Id) ?? false)) continue;
                    var module = TryGetExecutablePath(process);
                    if (!string.IsNullOrWhiteSpace(module) &&
                        IsPathUnderRoot(module, installRoot))
                        return process.Id;
                }
                finally { process.Dispose(); }
            }
        }
        catch { /* enumeration race */ }

        return null;
    }

    /// <summary>
    /// Stops a verified game process and its non-reserved descendants.
    /// Never uses <c>Kill(entireProcessTree: true)</c> — that would terminate
    /// Easy Anti-Cheat / BattlEye / Vanguard children the deny-list never sees.
    /// </summary>
    internal static void KillVerifiedGameTree(Process root, Func<string?, bool> isReservedName)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(isReservedName);

        foreach (var childPid in GetDescendantProcessIds(root.Id))
        {
            try
            {
                using var child = Process.GetProcessById(childPid);
                if (isReservedName(child.ProcessName)) continue;
                try { child.Kill(entireProcessTree: false); } catch { /* already exiting */ }
            }
            catch { /* process gone or access denied */ }
        }

        try
        {
            if (!isReservedName(root.ProcessName))
                root.Kill(entireProcessTree: false);
        }
        catch { /* last resort failed */ }
    }

    /// <summary>Child-first descendant PIDs of <paramref name="rootPid"/> (not including the root).</summary>
    internal static IReadOnlyList<int> GetDescendantProcessIds(int rootPid)
    {
        if (rootPid <= 0) return Array.Empty<int>();
        var childrenByParent = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
            return Array.Empty<int>();
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return Array.Empty<int>();
            do
            {
                var pid = (int)entry.th32ProcessID;
                var parent = (int)entry.th32ParentProcessID;
                if (pid <= 0 || parent <= 0 || pid == parent) continue;
                if (!childrenByParent.TryGetValue(parent, out var kids))
                {
                    kids = [];
                    childrenByParent[parent] = kids;
                }
                kids.Add(pid);
            } while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }

        var ordered = new List<int>();
        var seen = new HashSet<int>();
        void Walk(int pid)
        {
            if (!childrenByParent.TryGetValue(pid, out var kids)) return;
            foreach (var kid in kids)
            {
                if (!seen.Add(kid)) continue;
                Walk(kid);
                ordered.Add(kid);
            }
        }
        Walk(rootPid);
        return ordered;
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
