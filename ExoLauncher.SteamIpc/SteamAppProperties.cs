using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ExoLauncher.SteamIpc;

/// <summary>
/// <c>props</c> and <c>set</c>: a game's Properties through the same Steam client
/// calls Steam's own Properties page makes (SteamClient.Apps.SetDLCEnabled,
/// SetAppAutoUpdateBehavior, JoinAppContentBeta, ToggleAppSteamCloudEnabled ...).
/// Exo never edits Steam's files for these; Steam writes its own manifests.
///
/// Every method is found by the 32-bit name hash its IPC stub sends, read out of
/// steamclient64 (IClientAppManagerMap and IClientRemoteStorageMap): a moved
/// method is still found, and a method that is missing or shows up twice is
/// refused instead of guessed. Argument shapes come from the same stubs.
/// </summary>
internal static partial class Program
{
    // IClientEngine slot that returns IClientRemoteStorage (checked by RTTI before use).
    internal const int EngineGetIClientRemoteStorage = 24;

    // IClientAppManager methods by name hash.
    private const uint HashBIsDlcEnabled = 0x143d455d;      // bool (app, dlc, bool* )
    private const uint HashSetDlcEnabled = 0x0ec3c8a6;      // void (app, dlc, bool)
    private const uint HashIsAppDlcInstalled = 0xcdbd8c75;  // bool (app, dlc)
    private const uint HashGetCurrentLanguage = 0x600aa904; // ELanguage (app)
    private const uint HashSetCurrentLanguage = 0x8050d9d2; // int (app, ELanguage)
    private const uint HashGetActiveBeta = 0x5062a05a;      // bool (app, char*, int)
    private const uint HashSetActiveBeta = 0x4d0b20cf;      // bool (app, const char*)
    private const uint HashCheckBetaPassword = 0x75217c37;  // bool (app, const char*)
    private const uint HashGetAutoUpdate = 0x34e964d2;      // int (app)
    private const uint HashSetAutoUpdate = 0xc5d6e59d;      // bool (app, int)
    private const uint HashGetBackground = 0x7145c838;      // int (app)
    private const uint HashSetBackground = 0xfe722135;      // bool (app, int)
    private const uint HashNumLibraryFolders = 0xab50eb2f;  // int ()
    private const uint HashLibraryFolderPath = 0xf27c5d48;  // int (index, char*, int)
    private const uint HashAppLibraryFolder = 0x166ebaa1;   // int (app)
    private const uint HashMoveApp = 0x47804a94;            // int (app, folder index)
    private const uint HashMoveProgress = 0x55236ba9;       // bool (app, u64*, u64*, u32*)

    // IClientRemoteStorage methods by name hash.
    private const uint HashCloudForAccount = 0x8694e98e;    // bool ()
    private const uint HashCloudForApp = 0x88a72799;        // bool (app)
    private const uint HashSetCloudForApp = 0x2eaada04;     // void (app, bool)

    [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
    private delegate IntPtr GetClientInterfaceFn(IntPtr self, int user, int pipe, string version);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppDlcOutFn(IntPtr self, uint app, uint dlc, out byte extra);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void AppDlcSetFn(IntPtr self, uint app, uint dlc, [MarshalAs(UnmanagedType.I1)] bool enabled);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppDlcFn(IntPtr self, uint app, uint dlc);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int AppIntFn(IntPtr self, uint app);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int AppSetIntFn(IntPtr self, uint app, int value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppSetIntBoolFn(IntPtr self, uint app, int value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppBufferFn(IntPtr self, uint app, IntPtr buffer, int length);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppStringFn(IntPtr self, uint app, IntPtr text);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NoArgIntFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int FolderPathFn(IntPtr self, int index, IntPtr buffer, int length);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool MoveProgressFn(IntPtr self, uint app, out ulong done, out ulong total, out uint seconds);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NoArgBoolFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AppBoolFn(IntPtr self, uint app);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void AppSetBoolFn(IntPtr self, uint app, [MarshalAs(UnmanagedType.I1)] bool value);

    /// <summary>
    /// <c>props &lt;app&gt; [dlc,dlc,...]</c>: one JSON line with what Steam holds for
    /// the game now. Reads only. Exit 3 when Steam is not running or signed in.
    /// </summary>
    private static int RunProps(string[] args, uint appId)
    {
        var dlcs = args.Length >= 3 ? ParseAppIds(args[2]) : [];
        if (!TryConnectWithStorage(out var engine, out var pipe, out var engineVt, out var manager, out var storage))
            return 3;
        try
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteBoolean("ok", true);
                if (Find<AppIntFn>(manager, HashGetAutoUpdate) is { } autoUpdate)
                    json.WriteNumber("autoUpdate", autoUpdate(manager, appId));
                if (Find<AppIntFn>(manager, HashGetBackground) is { } background)
                    json.WriteNumber("background", background(manager, appId));
                if (Find<AppIntFn>(manager, HashGetCurrentLanguage) is { } language)
                    json.WriteNumber("language", language(manager, appId));
                if (Find<AppBufferFn>(manager, HashGetActiveBeta) is { } activeBeta)
                    json.WriteString("beta", ReadString((buffer, length) => activeBeta(manager, appId, buffer, length)) ?? "");
                if (storage != IntPtr.Zero &&
                    Find<NoArgBoolFn>(storage, HashCloudForAccount) is { } cloudAccount &&
                    Find<AppBoolFn>(storage, HashCloudForApp) is { } cloudApp)
                {
                    json.WriteBoolean("cloudAccount", cloudAccount(storage));
                    json.WriteBoolean("cloud", cloudApp(storage, appId));
                }
                if (Find<AppIntFn>(manager, HashAppLibraryFolder) is { } appFolder)
                    json.WriteNumber("folder", appFolder(manager, appId));
                if (Find<NoArgIntFn>(manager, HashNumLibraryFolders) is { } folderCount &&
                    Find<FolderPathFn>(manager, HashLibraryFolderPath) is { } folderPath)
                {
                    json.WriteStartArray("folders");
                    var count = Math.Clamp(folderCount(manager), 0, 64);
                    for (var index = 0; index < count; index++)
                        json.WriteStringValue(ReadString((buffer, length) => folderPath(manager, index, buffer, length) > 0) ?? "");
                    json.WriteEndArray();
                }
                if (Find<MoveProgressFn>(manager, HashMoveProgress) is { } move &&
                    move(manager, appId, out var moved, out var toMove, out _) && toMove > 0)
                {
                    json.WriteNumber("moveDone", moved);
                    json.WriteNumber("moveTotal", toMove);
                }
                var enabled = Find<AppDlcOutFn>(manager, HashBIsDlcEnabled);
                var installed = Find<AppDlcFn>(manager, HashIsAppDlcInstalled);
                json.WriteStartArray("dlc");
                foreach (var dlc in dlcs)
                {
                    json.WriteStartObject();
                    json.WriteNumber("id", dlc);
                    if (enabled is not null) json.WriteBoolean("enabled", enabled(manager, appId, dlc, out _));
                    if (installed is not null) json.WriteBoolean("installed", installed(manager, appId, dlc));
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            Console.Out.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
            Console.Out.Flush();
            return 0;
        }
        finally
        {
            ReleasePipe(engine, engineVt, pipe);
        }
    }

    /// <summary>
    /// <c>set &lt;app&gt; &lt;what&gt; [value...]</c>, one change the way Steam's own
    /// Properties page makes it: <c>dlc &lt;id&gt; 0|1</c>, <c>beta &lt;name|-&gt;</c>,
    /// <c>betacode</c> (the code on stdin, never on the command line), <c>autoupdate &lt;n&gt;</c>,
    /// <c>background &lt;n&gt;</c>, <c>cloud 0|1</c>, <c>language &lt;n&gt;</c>, <c>move &lt;folder&gt;</c>.
    /// Exit 0 when Steam accepted it, 1 when it refused, 2 for a bad request, 3 when unreachable.
    /// </summary>
    private static int RunSet(string[] args, uint appId)
    {
        if (args.Length < 3) return 2;
        var what = args[2].Trim().ToLowerInvariant();
        string? Arg(int index) => args.Length > index ? args[index] : null;
        if (!TryConnectWithStorage(out var engine, out var pipe, out var engineVt, out var manager, out var storage))
            return 3;
        try
        {
            var code = what switch
            {
                "dlc" when uint.TryParse(Arg(3), out var dlc) && Arg(4) is "0" or "1" =>
                    Call<AppDlcSetFn>(manager, HashSetDlcEnabled, set => set(manager, appId, dlc, Arg(4) == "1")),
                "beta" when Arg(3) is { } beta && IsBranchName(beta) =>
                    CallString(manager, HashSetActiveBeta, appId, beta == "-" ? "" : beta),
                "betacode" => Console.In.ReadLine()?.Trim() is { Length: > 0 and <= 256 } secret
                    ? CallString(manager, HashCheckBetaPassword, appId, secret)
                    : 2,
                "autoupdate" when int.TryParse(Arg(3), out var behavior) && behavior is >= 0 and <= 3 =>
                    CallBool<AppSetIntBoolFn>(manager, HashSetAutoUpdate, set => set(manager, appId, behavior)),
                "background" when int.TryParse(Arg(3), out var behavior) && behavior is >= 0 and <= 2 =>
                    CallBool<AppSetIntBoolFn>(manager, HashSetBackground, set => set(manager, appId, behavior)),
                "language" when int.TryParse(Arg(3), out var language) && language is >= 0 and < 64 =>
                    Call<AppSetIntFn>(manager, HashSetCurrentLanguage, set => set(manager, appId, language)),
                "move" when int.TryParse(Arg(3), out var folder) && folder is >= 0 and < 64 =>
                    CallBool<AppSetIntFn>(manager, HashMoveApp, move => move(manager, appId, folder) == 0),
                "cloud" when Arg(3) is "0" or "1" && storage != IntPtr.Zero =>
                    Call<AppSetBoolFn>(storage, HashSetCloudForApp, set => set(storage, appId, Arg(3) == "1")),
                _ => 2,
            };
            Note($"Steam IPC set appId={appId}; {what}; exit={code}.");
            return code;
        }
        finally
        {
            ReleasePipe(engine, engineVt, pipe);
        }
    }

    /// <summary>Steam branch names are short identifiers; "-" is the default branch.</summary>
    internal static bool IsBranchName(string value) =>
        value == "-" || value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static int Call<T>(IntPtr target, uint hash, Action<T> call) where T : Delegate
    {
        if (Find<T>(target, hash) is not { } method) return 1;
        call(method);
        return 0;
    }

    private static int CallBool<T>(IntPtr target, uint hash, Func<T, bool> call) where T : Delegate =>
        Find<T>(target, hash) is { } method ? call(method) ? 0 : 1 : 1;

    private static int CallString(IntPtr manager, uint hash, uint appId, string text)
    {
        if (Find<AppStringFn>(manager, hash) is not { } method) return 1;
        var native = Marshal.StringToCoTaskMemUTF8(text);
        try { return method(manager, appId, native) ? 0 : 1; }
        finally { Marshal.ZeroFreeCoTaskMemUTF8(native); }
    }

    private static string? ReadString(Func<IntPtr, int, bool> fill)
    {
        const int Size = 1024;
        var buffer = Marshal.AllocHGlobal(Size);
        try
        {
            Marshal.Copy(new byte[Size], 0, buffer, Size);
            if (!fill(buffer, Size - 1)) return null;
            return Marshal.PtrToStringUTF8(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static T? Find<T>(IntPtr target, uint hash) where T : Delegate
    {
        var slot = FindSlot(target, hash);
        if (slot == IntPtr.Zero)
        {
            Note($"Steam IPC: method 0x{hash:x8} not found once on this Steam build; skipped.");
            return null;
        }
        return Marshal.GetDelegateForFunctionPointer<T>(slot);
    }

    /// <summary>
    /// The one vtable entry whose IPC stub sends <paramref name="hash"/> near its
    /// start (every stub writes its hash within its first 0x84 bytes on today's
    /// build). None, or more than one, is <see cref="IntPtr.Zero"/>.
    /// </summary>
    private static IntPtr FindSlot(IntPtr target, uint hash)
    {
        if (target == IntPtr.Zero || SteamclientModule == IntPtr.Zero) return IntPtr.Zero;
        var moduleEnd = SteamclientModule + ModuleSize(SteamclientModule);
        var table = Marshal.ReadIntPtr(target);
        var found = IntPtr.Zero;
        var stub = new byte[0xC0];
        for (var index = 0; index < 192; index++)
        {
            var method = Marshal.ReadIntPtr(table, index * IntPtr.Size);
            if (method < SteamclientModule || method + stub.Length > moduleEnd) break;
            Marshal.Copy(method, stub, 0, stub.Length);
            if (!StubSendsHash(stub, hash)) continue;
            if (found != IntPtr.Zero) return IntPtr.Zero;
            found = method;
        }
        return found;
    }

    /// <summary>A <c>mov dword ptr [rbp/rsp+disp], hash</c> in the stub's first bytes.</summary>
    internal static bool StubSendsHash(ReadOnlySpan<byte> stub, uint hash)
    {
        Span<byte> needle = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(needle, hash);
        for (var at = stub.IndexOf(needle); at >= 0;)
        {
            foreach (var back in (ReadOnlySpan<int>)[3, 4, 6, 7])
                if (at >= back && stub[at - back] == 0xC7) return true;
            var next = stub[(at + 1)..].IndexOf(needle);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return false;
    }

    private static long ModuleSize(IntPtr module)
    {
        var header = Marshal.ReadInt32(module, 0x3C);
        return (uint)Marshal.ReadInt32(module, header + 0x50);
    }

    private static bool TryConnectWithStorage(out IntPtr engine, out int pipe, out IntPtr[] engineVt,
        out IntPtr manager, out IntPtr storage)
    {
        storage = IntPtr.Zero;
        if (!TryConnect(out engine, out pipe, out engineVt, out manager)) return false;
        try
        {
            if (ConnectedUser != 0)
            {
                var candidate = Marshal.GetDelegateForFunctionPointer<GetClientInterfaceFn>(
                    engineVt[EngineGetIClientRemoteStorage])(engine, ConnectedUser, pipe, "CLIENTREMOTESTORAGE_INTERFACE_VERSION001");
                if (ReadRttiName(candidate).Contains("IClientRemoteStorage", StringComparison.Ordinal)) storage = candidate;
                else Note("Steam IPC: IClientRemoteStorage not where expected; cloud skipped.");
            }
        }
        catch (Exception ex) { Note($"Steam IPC: remote storage unavailable: {ex.GetType().Name}."); }
        return true;
    }
}
