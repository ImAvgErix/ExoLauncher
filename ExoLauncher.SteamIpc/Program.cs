using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExoLauncher.SteamIpc;

/// <summary>
/// Commands the already-running Steam client through steamclient64.dll.
/// Steam stays the official backend; Exo does not copy Steam into its tree.
/// </summary>
internal static class Program
{
    internal const string ClientAppManagerVersion = "CLIENTAPPMANAGER_INTERFACE_VERSION001";
    internal const int EngineCreateSteamPipe = 0;
    internal const int EngineReleasePipe = 1;
    internal const int EngineConnectToGlobalUser = 3;
    internal const int EngineGetIClientAppManager = 43;
    internal const int AppInstallApp = 0;
    internal const int AppUninstallApp = 1;
    internal const int AppGetAppInstallState = 4;
    internal const int AppErrorNotInstalled = 18;

    private static readonly string[] EngineVersions =
    [
        "CLIENTENGINE_INTERFACE_VERSION004",
        "CLIENTENGINE_INTERFACE_VERSION005",
    ];

    private static IntPtr SteamclientModule;
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
        int baseFolder,
        [MarshalAs(UnmanagedType.I1)] bool legacy);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int UninstallAppFn(
        IntPtr self,
        uint appId,
        [MarshalAs(UnmanagedType.I1)] bool complete);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int GetAppInstallStateFn(IntPtr self, uint appId);

    private static int Main(string[] args)
    {
        try
        {
            Note("Steam IPC starting.");
            if (args.Length < 2 ||
                !uint.TryParse(args[1], out var appId) ||
                appId == 0)
            {
                Note("Steam IPC usage: install|update|uninstall|state <appId>");
                Environment.Exit(2);
                return 2;
            }

            var action = args[0].Trim().ToLowerInvariant();
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

    private static int RunUpdate(IntPtr manager, uint appId)
    {
        var vt = ReadVTable(manager, AppInstallApp + 1);
        var install = Marshal.GetDelegateForFunctionPointer<InstallAppFn>(
            vt[AppInstallApp])(manager, appId, 0, false);
        Note($"Steam IPC update appId={appId}; install={install}.");
        return install == 0 ? 0 : 1;
    }

    private static int RunUninstall(IntPtr manager, uint appId)
    {
        var vt = ReadVTable(manager, AppGetAppInstallState + 1);
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
        var steamRoot = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot))
        {
            Note("Steam IPC could not find the Steam install path.");
            return false;
        }

        steamRoot = Path.GetFullPath(steamRoot);
        var bin = Path.Combine(steamRoot, "bin");
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable(
            "PATH",
            steamRoot + Path.PathSeparator + bin + Path.PathSeparator + path);
        SetDllDirectory(steamRoot);

        var dll = Path.Combine(steamRoot, "steamclient64.dll");
        if (!File.Exists(dll))
        {
            Note("Steam IPC missing steamclient64.dll.");
            return false;
        }
        Note($"Steam IPC loading {dll}");

        var module = LoadLibraryEx(dll, IntPtr.Zero, LoadWithAlteredSearchPath);
        if (module == IntPtr.Zero)
        {
            Note($"Steam IPC LoadLibraryEx failed: {Marshal.GetLastWin32Error()}.");
            return false;
        }
        SteamclientModule = module;
        Note("Steam IPC steamclient64 loaded.");

        var createPtr = GetProcAddress(module, "CreateInterface");
        if (createPtr == IntPtr.Zero)
        {
            Note("Steam IPC CreateInterface export missing.");
            return false;
        }

        var create = Marshal.GetDelegateForFunctionPointer<CreateInterfaceFn>(createPtr);
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
        if (!TryCreateEngine(EngineGetIClientAppManager + 1, out engine, out pipe, out var user, out engineVt))
            return false;

        var getManager = Marshal.GetDelegateForFunctionPointer<GetClientAppManagerFn>(
            engineVt[EngineGetIClientAppManager]);
        foreach (var version in new[] { ClientAppManagerVersion, "CLIENTAPPMANAGER_INTERFACE_VERSION005" })
        {
            manager = getManager(engine, user, pipe, version);
            if (manager == IntPtr.Zero)
                continue;
            Note($"Steam IPC IClientAppManager {version} ptr.");
            if (ManagerLooksValid(manager))
                return true;
            Note("Steam IPC GetIClientAppManager pointed at a different interface.");
        }

        Note("Steam IPC GetIClientAppManager returned null.");
        ReleasePipe(engine, engineVt, pipe);
        return false;
    }

    private static IntPtr[] ReadVTable(IntPtr obj, int count)
    {
        var table = Marshal.ReadIntPtr(obj);
        var slots = new IntPtr[count];
        Marshal.Copy(table, slots, 0, count);
        return slots;
    }

    private static void Note(string message)
    {
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
