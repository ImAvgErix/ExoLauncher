using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ExoLauncher.Adapters;

internal static class ProcessHelper
{
    private const int SwHide = 0;
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
        return ContainsFragment(TryGetExecutablePath(process), pathMustContain);
    }

    internal static bool MatchesOptionalPath(int processId, string? pathMustContain)
    {
        if (string.IsNullOrWhiteSpace(pathMustContain)) return true;
        return ContainsFragment(TryGetExecutablePath(processId), pathMustContain);
    }

    private static bool ContainsFragment(string? path, string fragment) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

    internal static string? QueryFullProcessImageName(int processId)
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

    /// <summary>
    /// <see cref="Process.ProcessName"/> for a process id. On a process from
    /// <see cref="Process.GetProcessById(int)"/> that property walks the whole
    /// process table (about 0.3 ms each); the image path answers the same name
    /// in a tenth of that. Process.ProcessName's rule: the file name, minus a
    /// trailing ".exe". The table walk remains the fallback for processes whose
    /// image cannot be queried (System, Registry, some protected ones).
    /// </summary>
    internal static string? TryReadProcessName(int processId)
    {
        if (processId > 0 && QueryFullProcessImageName(processId) is { Length: > 0 } image)
        {
            var name = image.AsSpan(image.LastIndexOf('\\') + 1);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            return name.ToString();
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch { return null; }
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
        "galaxyclient", "galaxyclientservice", "goggalaxynotifications", "gogdl", "nile",
        "itch", "minecraftlauncher", "robloxplayerlauncher", "paradox launcher", "paradoxlauncher", "wgc",
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
        string.IsNullOrWhiteSpace(processName) || NonGameProcessNames.Contains(processName) ||
        HelperWords.Any(word => processName.Contains(word, StringComparison.OrdinalIgnoreCase));

    // A game's own helpers, which its store runs from inside its folder without the game: Call of Duty's install cleaner,
    // content downloader and broker service run when a DLC is added or removed, and read as the game playing.
    private static readonly string[] HelperWords =
    [
        "install", "cleaner", "downloader", "broker", "crashhandler", "crashreport", "crashlogs", "logsgenerator", "attestation",
        "redist", "uninst", "updater", "prereq", "dxsetup", "errorreport", "bugreport",
    ];

    /// <summary>A folder in a game's install that holds only what its store installs alongside it (runtimes, installers).</summary>
    internal static bool IsInHelperFolder(string executablePath, string root)
    {
        var relative = Path.GetRelativePath(root, executablePath);
        return relative.Split('\\', '/').SkipLast(1).Any(part => HelperFolders.Contains(part));
    }

    private static readonly HashSet<string> HelperFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "_CommonRedist", "CommonRedist", "__Installer", "_Installer", "Installer", "Installers", "Redist", "Redistributables",
        "DirectX", "vcredist", "Prerequisites", "Prereqs", "EasyAntiCheat", "BattlEye",
    };

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

    public static Process? StartGame(string fileName, LaunchOptions options) =>
        Process.Start(BuildGameStartInfo(fileName, options));

    internal static ProcessStartInfo BuildGameStartInfo(string fileName, LaunchOptions options)
    {
        var workingDirectory = options.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
                throw new DirectoryNotFoundException("The saved launch working directory is unavailable.");
        }
        else workingDirectory = Path.GetDirectoryName(fileName) ?? string.Empty;
        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = options.ExtraArgs ?? string.Empty,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
            Verb = options.RunAsAdmin ? "runas" : string.Empty,
        };
    }

    /// <summary>Fully hide process top-level windows (preferred over minimize for stealth launches).</summary>
    public static void HideProcessWindows(int processId) =>
        ApplyWindowState(processId, SwHide);

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

        // Do not hide windows here — leftover SW_HIDE + older TOOLWINDOW
        // styles made Steam unopenable from the taskbar.
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
    /// <summary>
    /// Asks a game's processes to close as its window's own close button does: the ones with a main window are asked
    /// there, and the windowless ones (a bootstrapper waiting on the game, a helper) are left to end with it. Only when
    /// none has a window, or with <paramref name="everyWindow"/> (the second ask, seconds on), do all their windows get
    /// WM_CLOSE. Asking a windowless parent at once can take a game down before it cleans up: Call of Duty's
    /// bootstrapper did, and the game then thought it had crashed and offered safe mode at the next start.
    /// </summary>
    public static void RequestCloseProcesses(IEnumerable<int> processIds, bool everyWindow = false)
    {
        var pids = processIds.Where(id => id > 0).ToHashSet();
        if (pids.Count == 0) return;
        var windowed = new List<int>();
        var windowless = new HashSet<int>();
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) continue;
                if (everyWindow || process.MainWindowHandle == IntPtr.Zero) windowless.Add(pid);
                else windowed.Add(pid);
            }
            catch { /* best effort */ }
        }
        foreach (var pid in windowed)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.CloseMainWindow();
            }
            catch { /* best effort */ }
        }
        if (windowless.Count == 0 || windowed.Count > 0) return;
        try
        {
            t_closePids = windowless;
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
        if (string.IsNullOrWhiteSpace(processPath) || RootPrefix(root) is not { } fullRoot)
            return false;
        try
        {
            return Path.GetFullPath(processPath).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The full root with one trailing separator, as <see cref="IsPathUnderRoot"/>
    /// compares it; null when it is blank or not a valid path. A caller testing
    /// many paths against one root computes it once.
    /// </summary>
    internal static string? RootPrefix(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            return Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }
        catch
        {
            return null;
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
        CancellationToken ct = default,
        GameSessionRuntime? runtime = null)
    {
        runtime ??= new GameSessionRuntime();
        var ignored = new HashSet<string>(
            ignoredNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var unobservedGameDeadline = runtime.UtcNow() + GetUnobservedGameTimeout(
            appearTimeout,
            seedProcessWasObserved: seedPid is > 0,
            observedSeedGoneGrace);
        var sessionDeadline = runtime.UtcNow() + TimeSpan.FromHours(18);
        var sawGame = false;
        DateTimeOffset? lastSeen = null;
        var sawHandoff = false;
        DateTimeOffset? handoffLastSeen = null;
        var handoffAppearDeadline = handoffProcessNames is { Count: > 0 } &&
                                    handoffAppearTimeout is { } handoffTimeout &&
                                    handoffTimeout > TimeSpan.Zero
            ? runtime.UtcNow() + handoffTimeout
            : (DateTimeOffset?)null;

        while (runtime.UtcNow() < sessionDeadline && !ct.IsCancellationRequested)
        {
            if (!sawGame && handoffProcessNames is { Count: > 0 })
            {
                var handoffAlive = handoffProcessNames.Any(runtime.IsRunning);
                if (handoffAlive)
                {
                    sawHandoff = true;
                    handoffLastSeen = runtime.UtcNow();
                }
                else if (sawHandoff && handoffLastSeen is not null &&
                         runtime.UtcNow() - handoffLastSeen.Value >= goneDebounce)
                {
                    return false;
                }
            }

            // League can legitimately keep its product client open for a long
            // time before a match begins. That extended wait is valid only
            // after the client-to-game handoff has actually appeared; otherwise
            // an accepted-but-never-started cold launch must become retryable.
            if (HasMissedHandoff(sawGame, sawHandoff, runtime.UtcNow(), handoffAppearDeadline))
                return false;

            // Prefer a real game match. Never treat bootstrap/helper PIDs as the session.
            var match = runtime.FindMatch(installRoot, processNames, ignored);
            var seed = runtime.FindSeed(seedPid);
            int? pid = match;
            if (pid is null && seed is int sid && !runtime.IsIgnored(sid, ignored))
                pid = sid;

            if (pid is int live)
            {
                sawGame = true;
                lastSeen = runtime.UtcNow();
                seedPid = live; // follow handoff targets next loop
                try
                {
                    await runtime.WaitForExit(live, ct).ConfigureAwait(false);
                    // Start the successor grace at exit, even after a game
                    // process ran for hours. The pre-wait timestamp is stale.
                    lastSeen = runtime.UtcNow();
                    // The pid is dead; Windows may hand it to an unrelated process.
                    seedPid = null;
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // Access denied / already gone — poll below.
                    try { await runtime.Delay(TimeSpan.FromMilliseconds(800), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }

                continue;
            }

            // Seed may still be a live bootstrap (Launcher / RiotClient) — keep
            // polling for the real game instead of WaitForExit on the helper.
            if (!sawGame)
            {
                if (runtime.UtcNow() >= unobservedGameDeadline)
                    return false;
            }
            else if (lastSeen is not null &&
                     runtime.UtcNow() - lastSeen.Value >= goneDebounce)
            {
                return true;
            }

            try { await runtime.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        return sawGame;
    }

    internal sealed class GameSessionRuntime
    {
        public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;
        public Func<string?, IReadOnlyList<string>?, HashSet<string>, int?> FindMatch { get; init; } =
            (root, names, ignored) => FindMatchingProcessId(root, names, ignored);
        public Func<int?, int?> FindSeed { get; init; } = FindLivePid;
        public Func<int, HashSet<string>, bool> IsIgnored { get; init; } = IsIgnoredPid;
        public Func<string, bool> IsRunning { get; init; } = IsProcessRunning;
        public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
        public Func<int, CancellationToken, Task> WaitForExit { get; init; } = async (pid, token) =>
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) await process.WaitForExitAsync(token).ConfigureAwait(false);
        };
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
    /// With <paramref name="installRoot"/>, a descendant whose image is outside
    /// it (a browser the game opened) is left running.
    /// </summary>
    internal static void KillVerifiedGameTree(Process root, Func<string?, bool> isReservedName, string? installRoot = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(isReservedName);

        foreach (var childPid in GetDescendantProcessIds(root.Id))
        {
            try
            {
                using var child = Process.GetProcessById(childPid);
                if (isReservedName(child.ProcessName)) continue;
                if (!string.IsNullOrWhiteSpace(installRoot) &&
                    (TryGetExecutablePath(child) is not { } image || !IsPathUnderRoot(image, installRoot))) continue;
                try { ProcessTermination.Kill(child, entireProcessTree: false); } catch { /* already exiting */ }
            }
            catch { /* process gone or access denied */ }
        }

        try
        {
            if (!isReservedName(root.ProcessName))
                ProcessTermination.Kill(root, entireProcessTree: false);
        }
        catch { /* last resort failed */ }
    }

    /// <summary>Child-first descendant PIDs of <paramref name="rootPid"/> (not including the root).</summary>
    internal static IReadOnlyList<int> GetDescendantProcessIds(int rootPid)
    {
        if (rootPid <= 0) return Array.Empty<int>();
        // Breadth-first from the root, reversed: every child precedes its parent.
        var tree = Helpers.BackgroundTrim.TreePids(rootPid);
        var ordered = new List<int>(tree.Count);
        for (var i = tree.Count - 1; i >= 1; i--) ordered.Add(tree[i]);
        return ordered;
    }

}
