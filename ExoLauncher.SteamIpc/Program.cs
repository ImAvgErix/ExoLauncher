using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Adapters;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ExoLauncher.SteamIpc;

/// <summary>
/// Commands the already-running Steam client through steamclient64.dll.
/// Steam stays the official backend; Exo does not copy Steam into its tree.
/// </summary>
internal static partial class Program
{
    internal const string ClientAppManagerVersion = "CLIENTAPPMANAGER_INTERFACE_VERSION001";
    internal const int EngineCreateSteamPipe = 0;
    internal const int EngineReleasePipe = 1;
    internal const int EngineConnectToGlobalUser = 3;
    internal const int EngineGetIClientAppManager = 43;
    internal const int AppInstallApp = 0;
    internal const int AppUninstallApp = 1;
    internal const int AppGetAppInstallState = 4;
    // IClientAppManagerMap's slot for GetUpdateInfo in today's steamclient64 (InstallApp 0 … GetDependentApps 19,
    // GetUpdateInfo 20); 13 is SetDlcContext there, which must never be called with an update-info pointer.
    internal const int AppGetUpdateInfo = 20;
    internal const int AppErrorNotInstalled = 18;

    private static readonly string[] EngineVersions =
    [
        "CLIENTENGINE_INTERFACE_VERSION004",
        "CLIENTENGINE_INTERFACE_VERSION005",
    ];

    private static IntPtr SteamclientModule;
    /// <summary>The global user <see cref="TryConnect"/> connected to, for the other client interfaces.</summary>
    private static int ConnectedUser;
    private const uint LoadWithAlteredSearchPath = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr CreateInterfaceFn(string name, IntPtr returnCode);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int CreateSteamPipeFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int ConnectToGlobalUserFn(IntPtr self, int pipe);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool ReleaseSteamPipeFn(IntPtr self, int pipe);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
    private delegate IntPtr GetClientAppManagerFn(IntPtr self, int user, int pipe, string version);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int InstallAppFn(
        IntPtr self,
        uint appId,
        IntPtr appDir,
        int baseFolder,
        [MarshalAs(UnmanagedType.I1)] bool legacy);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int UninstallAppFn(
        IntPtr self,
        uint appId,
        [MarshalAs(UnmanagedType.I1)] bool complete);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int GetAppInstallStateFn(IntPtr self, uint appId);

    // The fields Exo reads from Steam's update info (see GetUpdateInfo for where each sits).
    private struct AppUpdateInfo
    {
        public uint TimeUpdateStart;
        public ulong BytesToDownload;
        public ulong BytesDownloaded;
        public ulong BytesToProcess;
        public ulong BytesProcessed;
        public uint Unk;
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    // The struct Steam fills is read from a generous zeroed buffer: a newer, larger struct can never write past it.
    private delegate uint GetUpdateInfoFn(IntPtr self, uint appId, IntPtr info);
    private const int UpdateInfoBuffer = 512;

    private static int Main(string[] args)
    {
        try
        {
            Note("Steam IPC starting.");
            if (args.Length >= 1 && args[0].Trim().Equals("owned", StringComparison.OrdinalIgnoreCase))
            {
                var ownedCode = RunOwned(args);
                // steamclient64 keeps worker threads; returning from Main will hang.
                Environment.Exit(ownedCode);
                return ownedCode;
            }
            if (args.Length < 2 ||
                !uint.TryParse(args[1], out var appId) ||
                appId == 0)
            {
                Note("Steam IPC usage: install|update|uninstall|state|watch|props|set <appId> | owned <ids|->");
                Environment.Exit(2);
                return 2;
            }

            var action = args[0].Trim().ToLowerInvariant();
            if (action == "watch") return RunWatch(args, appId);
            if (action is "props" or "set")
            {
                var propsCode = action == "props" ? RunProps(args, appId) : RunSet(args, appId);
                // steamclient64 keeps worker threads; returning from Main will hang.
                Environment.Exit(propsCode);
                return propsCode;
            }
            if (!TryConnect(out var engine, out var pipe, out var engineVt, out var manager))
            {
                Environment.Exit(3);
                return 3;
            }

            var code = action switch
            {
                "install" or "update" => RunUpdate(manager, appId),
                "uninstall" => RunUninstall(manager, appId),
                "state" => RunState(manager, appId),
                _ => 2,
            };
            ReleasePipe(engine, engineVt, pipe);
            // steamclient64 keeps worker threads; returning from Main will hang.
            Environment.Exit(code);
            return code;
        }
        catch (Exception ex)
        {
            Note($"Steam IPC failed: {ex.GetType().Name}: {ex.Message}");
            Environment.Exit(4);
            return 4;
        }
    }

    private sealed record WatchSnapshot(
        bool ManifestExists,
        long? StateFlags,
        long? BytesDownloaded,
        long? BytesToDownload,
        long? BytesStaged,
        long? BytesToStage,
        long? IpcDownloaded,
        long? IpcToDownload,
        long? IpcProcessed,
        long? IpcToProcess,
        bool PendingTargetBuild);

    private sealed record WatchMessage(
        string GameId,
        string Phase,
        double? Percent,
        long? BytesDownloaded,
        long? BytesToDownload,
        string Status,
        bool IsActive,
        bool HasTelemetry = false,
        long Sequence = 0,
        string Source = "ipc");

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(WatchMessage))]
    private partial class WatchJsonContext : JsonSerializerContext { }
    private static long _watchSequence;
    private static long _lastWriteAt;

    private static int RunWatch(string[] args, uint appId)
    {
        var parentPid = TryGetIntArg(args, "--parent-pid");
        var gameId = TryGetArg(args, "--game-id") ?? $"steam:{appId}";
        var mode = (TryGetArg(args, "--mode") ?? "update").Trim().ToLowerInvariant();
        var steamRoot = ResolveSteamRoot();
        if (steamRoot is null)
        {
            WriteWatch(new WatchMessage(gameId, "preparing", null, null, null, "Steam path unavailable.", true));
            return 3;
        }

        IntPtr engine = IntPtr.Zero;
        IntPtr[] engineVt = [];
        IntPtr manager = IntPtr.Zero;
        var pipe = 0;
        GetUpdateInfoFn? getUpdate = null;
        void Disconnect()
        {
            var ownedEngine = engine;
            var ownedVt = engineVt;
            var ownedPipe = pipe;
            engine = manager = IntPtr.Zero;
            engineVt = [];
            pipe = 0;
            getUpdate = null;
            ReleasePipe(ownedEngine, ownedVt, ownedPipe);
        }

        var sawWork = false;
        WatchSnapshot? initialManifest = null;
        var sawManifestPending = false;
        var readyStreak = 0;
        long nextConnectAt = 0;
        string lastKey = "";
        WriteChanged(ref lastKey, new WatchMessage(gameId, "preparing", null, null, null, "Starting Steam…", true));
        try
        {
            while (true)
            {
                if (parentPid is > 0 && !IsProcessAlive(parentPid.Value)) return 0;
                // A cold Steam client may not expose its global user yet. Keep
                // manifest monitoring live and retry IPC at most once every 2s.
                if (getUpdate is null && Environment.TickCount64 >= nextConnectAt)
                {
                    try
                    {
                        if (TryConnect(out engine, out pipe, out engineVt, out manager))
                        {
                            var vt = ReadVTable(manager, AppGetUpdateInfo + 1);
                            if (vt[AppGetUpdateInfo] != IntPtr.Zero)
                                getUpdate = Marshal.GetDelegateForFunctionPointer<GetUpdateInfoFn>(vt[AppGetUpdateInfo]);
                        }
                    }
                    catch (Exception ex) { NoteOnce($"Steam IPC watch connection failed: {ex.Message}"); }
                    if (getUpdate is null) Disconnect();
                    nextConnectAt = Environment.TickCount64 + 2000;
                }
                var current = ReadWatchSnapshot(steamRoot, appId, getUpdate, manager, out var ipcFailed);
                if (ipcFailed)
                {
                    Disconnect();
                    nextConnectAt = Environment.TickCount64 + 2000;
                }
                var liveFolder = HasLiveTransferFolder(steamRoot, appId);
                var busy = IsBusy(current, liveFolder);
                if (current.ManifestExists && current.StateFlags is not null)
                {
                    initialManifest ??= current;
                    sawManifestPending |= !IsManifestReady(current);
                }
                var freshManifest = sawManifestPending || initialManifest is not null &&
                    (current.BytesDownloaded != initialManifest.BytesDownloaded || current.BytesToDownload != initialManifest.BytesToDownload ||
                     current.BytesStaged != initialManifest.BytesStaged || current.BytesToStage != initialManifest.BytesToStage);
                // An unchanged ready ACF may still describe the previous build
                // while IPC already reports a newly queued transfer.
                if (!freshManifest && (Incomplete(current.IpcDownloaded, current.IpcToDownload) ||
                                       Incomplete(current.IpcProcessed, current.IpcToProcess)))
                    busy = true;
                var reading = LiveTransfer(current);
                if (busy || reading.Percent is > 0 and < 100) sawWork = true;
                var hasCounters = reading.Percent is not null && (sawWork || reading.Percent < 100);
                var hasTelemetry = hasCounters || busy && reading.Phase == "installing";
                var ready = sawWork && !busy && IsManifestReady(current);
                readyStreak = ready ? readyStreak + 1 : 0;
                if (readyStreak >= 20)
                {
                    WriteChanged(ref lastKey, new WatchMessage(gameId, "completed", null, null, null, "Steam is up to date.", false, true));
                    return 0;
                }
                var phase = ready ? "finalizing" : reading.Phase;
                var verb = phase == "installing" ? (mode == "install" ? "Installing" : "Applying update") : "Downloading";
                var status = ready ? "Finalizing in Steam…" : hasCounters
                    ? $"{verb}… {FormatBytes(reading.Done!.Value)} / {FormatBytes(reading.Total!.Value)}"
                    : busy ? $"{verb}…" : "Waiting for Steam…";
                WriteChanged(ref lastKey, new WatchMessage(gameId, phase,
                    ready ? null : reading.Percent,
                    phase == "downloading" ? reading.Done : null,
                    phase == "downloading" ? reading.Total : null,
                    status, true, hasTelemetry || ready));
                Thread.Sleep(250);
            }
        }
        finally { Disconnect(); }
    }

    private static SteamProgressCounters.Reading LiveTransfer(WatchSnapshot snap) =>
        SteamProgressCounters.Resolve(snap.IpcDownloaded, snap.IpcToDownload,
            snap.IpcProcessed, snap.IpcToProcess,
            installing: snap.StateFlags is long state && (state & SteamStateFlags.Downloading) == 0 &&
                (state & (SteamStateFlags.Staging | SteamStateFlags.Committing)) != 0,
            downloading: snap.StateFlags is long downloadState && (downloadState & SteamStateFlags.Downloading) != 0);

    private static WatchSnapshot ReadWatchSnapshot(
        string steamRoot,
        uint appId,
        GetUpdateInfoFn? getUpdate,
        IntPtr manager,
        out bool ipcFailed)
    {
        ipcFailed = false;
        string? manifest = null;
        foreach (var library in LibraryFolders(steamRoot))
        {
            var candidate = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (File.Exists(candidate))
            {
                manifest = candidate;
                break;
            }
        }

        var text = string.Empty;
        if (manifest is not null)
        {
            try { text = File.ReadAllText(manifest); } catch { }
        }

        long? ipcDownloaded = null;
        long? ipcToDownload = null;
        long? ipcProcessed = null;
        long? ipcToProcess = null;
        if (getUpdate is not null && manager != IntPtr.Zero)
        {
            try
            {
                var buffer = Marshal.AllocHGlobal(UpdateInfoBuffer);
                AppUpdateInfo info;
                uint updateResult;
                try
                {
                    Marshal.Copy(new byte[UpdateInfoBuffer], 0, buffer, UpdateInfoBuffer);
                    updateResult = getUpdate(manager, appId, buffer);
                    // Today's AppUpdateInfo (read from steamclient64 against a live download's content_log totals):
                    // update start (0), seconds left (8), bytes to process (64) and processed (72), bytes downloaded (128)
                    // and to download (136). Anything that doesn't add up counts as no reading.
                    info = new AppUpdateInfo
                    {
                        TimeUpdateStart = (uint)Marshal.ReadInt32(buffer, 0),
                        Unk = (uint)Marshal.ReadInt32(buffer, 8),
                        BytesToProcess = (ulong)Marshal.ReadInt64(buffer, 64),
                        BytesProcessed = (ulong)Marshal.ReadInt64(buffer, 72),
                        BytesDownloaded = (ulong)Marshal.ReadInt64(buffer, 128),
                        BytesToDownload = (ulong)Marshal.ReadInt64(buffer, 136),
                    };
                    if (info.BytesDownloaded > info.BytesToDownload || info.BytesProcessed > info.BytesToProcess) info = default;
                    if (DebugOutput)
                        { var raw = new byte[160]; Marshal.Copy(buffer, raw, 0, raw.Length); Console.Error.WriteLine("GetUpdateInfo raw " + Convert.ToHexString(raw)); }
                }
                finally { Marshal.FreeHGlobal(buffer); }
                if (DebugOutput)
                    Console.Error.WriteLine($"GetUpdateInfo result={updateResult} start={info.TimeUpdateStart} dl={info.BytesDownloaded}/{info.BytesToDownload} proc={info.BytesProcessed}/{info.BytesToProcess} unk={info.Unk}");
                if (info.BytesToDownload <= long.MaxValue && info.BytesDownloaded <= long.MaxValue)
                {
                    ipcToDownload = (long)info.BytesToDownload;
                    ipcDownloaded = (long)info.BytesDownloaded;
                }
                if (info.BytesToProcess <= long.MaxValue && info.BytesProcessed <= long.MaxValue)
                {
                    ipcToProcess = (long)info.BytesToProcess;
                    ipcProcessed = (long)info.BytesProcessed;
                }
                if (ipcToDownload is > 0)
                    NoteOnce($"Steam IPC GetUpdateInfo appId={appId}; {ipcDownloaded}/{ipcToDownload} process={info.BytesProcessed}/{info.BytesToProcess}.");
            }
            catch (Exception ex)
            {
                ipcFailed = true;
                NoteOnce($"Steam IPC GetUpdateInfo appId={appId} failed: {ex.Message}");
            }
        }

        return new WatchSnapshot(
            manifest is not null,
            ReadNumber(text, "StateFlags"),
            ReadNumber(text, "BytesDownloaded"),
            ReadNumber(text, "BytesToDownload"),
            ReadNumber(text, "BytesStaged"),
            ReadNumber(text, "BytesToStage"),
            ipcDownloaded,
            ipcToDownload,
            ipcProcessed,
            ipcToProcess,
            SteamStateFlags.HasPendingTargetBuild(ReadNumber(text, "buildid")?.ToString(), ReadNumber(text, "TargetBuildID")?.ToString()));
    }

    private static bool HasLiveTransferFolder(string steamRoot, uint appId)
    {
        foreach (var library in LibraryFolders(steamRoot))
        {
            foreach (var directory in new[]
                     {
                         Path.Combine(library, "steamapps", "downloading", appId.ToString()),
                         Path.Combine(library, "steamapps", "workshop", "downloads", appId.ToString()),
                     })
            {
                try
                {
                    if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                        return true;
                }
                catch
                {
                    /* one library failed */
                }
            }
        }

        return false;
    }

    private static bool IsBusy(WatchSnapshot snapshot, bool directoryMoved)
    {
        if (directoryMoved) return true;
        // Steam can retain GetUpdateInfo counters after committing. A ready
        // manifest with no pending build or byte delta is the completion source.
        if (IsManifestReady(snapshot)) return false;
        return SteamStateFlags.IsBusy(snapshot.StateFlags?.ToString(), snapshot.BytesToDownload, snapshot.BytesDownloaded) ||
               Incomplete(snapshot.IpcDownloaded, snapshot.IpcToDownload) ||
               Incomplete(snapshot.IpcProcessed, snapshot.IpcToProcess) ||
               Incomplete(snapshot.BytesStaged, snapshot.BytesToStage);
    }

    private static bool Incomplete(long? done, long? total) => total is > 0 && (done is null || done < total);

    private static bool IsManifestReady(WatchSnapshot snapshot) =>
        snapshot.ManifestExists && SteamStateFlags.IsReady(snapshot.StateFlags?.ToString()) &&
        !snapshot.PendingTargetBuild &&
        !Incomplete(snapshot.BytesDownloaded, snapshot.BytesToDownload) &&
        !Incomplete(snapshot.BytesStaged, snapshot.BytesToStage);

    private static IEnumerable<string> LibraryFolders(string steamRoot)
    {
        yield return steamRoot;
        var path = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        string text;
        try { text = File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch { yield break; }
        foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\""))
        {
            var value = match.Groups[1].Value.Replace("\\\\", "\\");
            if (Directory.Exists(value) && !value.Equals(steamRoot, StringComparison.OrdinalIgnoreCase))
                yield return value;
        }
    }

    private static long? ReadNumber(string text, string key)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, "\\\"" + Regex.Escape(key) + "\\\"\\s+\\\"(?<value>\\d+)\\\"", RegexOptions.CultureInvariant);
        return match.Success && long.TryParse(match.Groups["value"].Value, out var value) ? value : null;
    }

    private static string? ResolveSteamRoot()
    {
        var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var root = value.Replace('/', Path.DirectorySeparatorChar);
        return Directory.Exists(root) ? Path.GetFullPath(root) : null;
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }

    private static string? TryGetArg(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    private static int? TryGetIntArg(string[] args, string name) =>
        int.TryParse(TryGetArg(args, name), out var value) && value > 0 ? value : null;

    private static void WriteWatch(WatchMessage message)
    {
        try
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(message with { Sequence = ++_watchSequence }, WatchJsonContext.Default.WatchMessage));
            Console.Out.Flush();
        }
        catch { }
    }

    private static void WriteChanged(ref string lastKey, WatchMessage message)
    {
        var percentKey = message.Percent is double percent ? Math.Round(percent, 1).ToString("0.0") : "";
        var key = $"{message.Phase}|{percentKey}|{message.BytesDownloaded}|{message.BytesToDownload}|{message.Status}";
        var now = Environment.TickCount64;
        if (key == lastKey && now - _lastWriteAt < 1000) return;
        _lastWriteAt = now;
        lastKey = key;
        WriteWatch(message);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    private static int RunUpdate(IntPtr manager, uint appId)
    {
        var vt = ReadVTable(manager, AppInstallApp + 1);
        var install = Marshal.GetDelegateForFunctionPointer<InstallAppFn>(
            vt[AppInstallApp])(manager, appId, IntPtr.Zero, 0, false);
        Note($"Steam IPC update appId={appId}; install={install}.");
        return install == 0 ? 0 : 1;
    }

    private static int RunUninstall(IntPtr manager, uint appId)
    {
        var vt = ReadVTable(manager, AppGetAppInstallState + 1);
        var readState = Marshal.GetDelegateForFunctionPointer<GetAppInstallStateFn>(vt[AppGetAppInstallState]);
        var root = ResolveSteamRoot();
        var manifests = root is null ? [] : LibraryFolders(root)
            .Select(folder => Path.Combine(folder, "steamapps", $"appmanifest_{appId}.acf")).ToArray();
        if (!SteamUninstallReadiness.Wait(() => readState(manager, appId), () => manifests.Any(File.Exists)))
        {
            Note($"Steam IPC uninstall appId={appId}; installed library has not loaded yet.");
            return 3; // Transient readiness failure; the host can retry without repeating an accepted command.
        }
        NoteInstallState(manager, vt, appId, "before");
        var result = Marshal.GetDelegateForFunctionPointer<UninstallAppFn>(
            vt[AppUninstallApp])(manager, appId, true);
        Note($"Steam IPC uninstall appId={appId}; result={result}.");
        if (result == AppErrorNotInstalled)
            Note($"Steam IPC uninstall appId={appId}; Steam returned NotInstalled.");
        NoteInstallState(manager, vt, appId, "after");
        return result == 0 ? 0 : 1;
    }

    private static int RunState(IntPtr manager, uint appId)
    {
        var vt = ReadVTable(manager, AppGetAppInstallState + 1);
        NoteInstallState(manager, vt, appId, "query");
        return 0;
    }

    private static void NoteInstallState(IntPtr manager, IntPtr[] vt, uint appId, string when)
    {
        if (vt.Length <= AppGetAppInstallState)
            return;
        var state = Marshal.GetDelegateForFunctionPointer<GetAppInstallStateFn>(
            vt[AppGetAppInstallState])(manager, appId);
        Note($"Steam IPC install-state appId={appId}; when={when}; state={state}.");
    }

    private static void ReleasePipe(IntPtr engine, IntPtr[] engineVt, int pipe)
    {
        if (engine == IntPtr.Zero || pipe == 0 || engineVt.Length <= EngineReleasePipe)
            return;
        try
        {
            Marshal.GetDelegateForFunctionPointer<ReleaseSteamPipeFn>(
                engineVt[EngineReleasePipe])(engine, pipe);
        }
        catch
        {
            /* best-effort */
        }
    }

    private static bool TryCreateEngine(
        int engineSlots,
        out IntPtr engine,
        out int pipe,
        out int user,
        out IntPtr[] engineVt)
    {
        engine = IntPtr.Zero;
        pipe = 0;
        user = 0;
        engineVt = [];
        if (!TryLoadSteamclient(out var create))
            return false;
        foreach (var version in EngineVersions)
        {
            engine = create(version, IntPtr.Zero);
            if (engine != IntPtr.Zero)
            {
                Note($"Steam IPC engine {version}.");
                break;
            }
        }

        if (engine == IntPtr.Zero)
        {
            Note("Steam IPC could not create IClientEngine. This Steam client is newer than Exo supports.");
            return false;
        }

        engineVt = ReadVTable(engine, engineSlots);
        pipe = Marshal.GetDelegateForFunctionPointer<CreateSteamPipeFn>(
            engineVt[EngineCreateSteamPipe])(engine);
        if (pipe == 0)
        {
            Note("Steam IPC CreateSteamPipe failed.");
            return false;
        }

        user = Marshal.GetDelegateForFunctionPointer<ConnectToGlobalUserFn>(
            engineVt[EngineConnectToGlobalUser])(engine, pipe);
        if (user == 0)
        {
            Note("Steam IPC ConnectToGlobalUser failed. Is Steam running and signed in?");
            return false;
        }

        return true;
    }

    private static bool TryLoadSteamclient(out CreateInterfaceFn create)
    {
        create = null!;
        var steamRoot = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot))
        {
            Note("Steam IPC could not find the Steam install path.");
            return false;
        }

        steamRoot = Path.GetFullPath(steamRoot);
        var dll = Path.Combine(steamRoot, "steamclient64.dll");
        if (!File.Exists(dll))
        {
            Note("Steam IPC missing steamclient64.dll.");
            return false;
        }
        var module = SteamclientModule;
        if (module == IntPtr.Zero)
        {
            var bin = Path.Combine(steamRoot, "bin");
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            var paths = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var missing = new[] { steamRoot, bin }.Where(folder => !paths.Contains(folder, StringComparer.OrdinalIgnoreCase));
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, missing.Concat(paths)));
            SetDllDirectory(steamRoot);
            Note($"Steam IPC loading {dll}");
            module = LoadLibraryEx(dll, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (module == IntPtr.Zero)
            {
                Note($"Steam IPC LoadLibraryEx failed: {Marshal.GetLastWin32Error()}.");
                return false;
            }
            SteamclientModule = module;
            Note("Steam IPC steamclient64 loaded.");
        }

        var createPtr = GetProcAddress(module, "CreateInterface");
        if (createPtr == IntPtr.Zero)
        {
            Note("Steam IPC CreateInterface export missing.");
            return false;
        }

        create = Marshal.GetDelegateForFunctionPointer<CreateInterfaceFn>(createPtr);
        return true;
    }

    private static bool ManagerLooksValid(IntPtr manager)
    {
        var name = ReadRttiName(manager);
        if (name.Contains("IClientAppManager", StringComparison.Ordinal))
        {
            Note($"Steam IPC IClientAppManager ok; rtti={name}.");
            return true;
        }

        Note($"Steam IPC IClientAppManager layout mismatch (rtti={name}).");
        return false;
    }

    private static string ReadRttiName(IntPtr obj)
    {
        if (obj == IntPtr.Zero || SteamclientModule == IntPtr.Zero)
            return "null";
        try
        {
            var vtable = Marshal.ReadIntPtr(obj);
            var col = Marshal.ReadIntPtr(vtable, -IntPtr.Size);
            if (col == IntPtr.Zero)
                return "no-col";
            var signature = Marshal.ReadInt32(col);
            var typeRva = Marshal.ReadInt32(col, 12);
            var imageBaseRva = Marshal.ReadInt32(col, 20);
            IntPtr typeDesc;
            if (signature == 1 && imageBaseRva != 0)
                typeDesc = col - imageBaseRva + typeRva;
            else
                typeDesc = SteamclientModule + typeRva;
            var name = Marshal.PtrToStringAnsi(typeDesc + 16);
            return string.IsNullOrWhiteSpace(name) ? $"empty sig={signature} rva={typeRva:X}" : name;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static bool TryConnect(out IntPtr engine, out int pipe, out IntPtr[] engineVt, out IntPtr manager)
    {
        engine = IntPtr.Zero;
        pipe = 0;
        engineVt = [];
        manager = IntPtr.Zero;
        var connected = false;
        try
        {
            if (!TryCreateEngine(EngineGetIClientAppManager + 1, out engine, out pipe, out var user, out engineVt))
                return false;

            ConnectedUser = user;
            var getManager = Marshal.GetDelegateForFunctionPointer<GetClientAppManagerFn>(
                engineVt[EngineGetIClientAppManager]);
            foreach (var version in new[] { ClientAppManagerVersion, "CLIENTAPPMANAGER_INTERFACE_VERSION005" })
            {
                manager = getManager(engine, user, pipe, version);
                if (manager == IntPtr.Zero)
                    continue;
                Note($"Steam IPC IClientAppManager {version} ptr.");
                if (ManagerLooksValid(manager))
                {
                    connected = true;
                    return true;
                }
                Note("Steam IPC GetIClientAppManager pointed at a different interface.");
            }

            Note("Steam IPC GetIClientAppManager returned null.");
            return false;
        }
        finally
        {
            if (!connected)
            {
                ReleasePipe(engine, engineVt, pipe);
                engine = manager = IntPtr.Zero;
                pipe = 0;
                engineVt = [];
            }
        }
    }

    private static IntPtr[] ReadVTable(IntPtr obj, int count)
    {
        var table = Marshal.ReadIntPtr(obj);
        var slots = new IntPtr[count];
        Marshal.Copy(table, slots, 0, count);
        return slots;
    }

    private static int _noteOnce;

    private static void NoteOnce(string message)
    {
        if (Interlocked.Exchange(ref _noteOnce, 1) != 0) return;
        Note(message);
    }

    /// <summary>EXO_STEAMIPC_DEBUG=1 also prints notes and raw counters to stderr.</summary>
    private static readonly bool DebugOutput = Environment.GetEnvironmentVariable("EXO_STEAMIPC_DEBUG") == "1";

    private static void Note(string message)
    {
        if (DebugOutput) Console.Error.WriteLine(message);
        try
        {
            var log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExoLauncher", "logs", "app.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"[{DateTime.UtcNow:O}] INFO {message}{Environment.NewLine}");
        }
        catch
        {
            /* best-effort */
        }
    }
}
