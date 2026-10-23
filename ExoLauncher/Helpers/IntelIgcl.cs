using System.Runtime.InteropServices;
using System.Text;

namespace ExoLauncher.Helpers;

/// <summary>
/// Intel's driver through the Intel Graphics Control Library (ControlLib.dll in System32, installed with the Arc and
/// Iris Xe driver). Exo ships no IGCL code: it loads the driver's DLL only when it is there and calls its C API by the
/// layouts in igcl_api.h (github.com/intel/drivers.gpu.control-library master, API 1.1). Every size and offset below
/// was measured by compiling that header with MSVC x64, and IntelIgclTests pins them. One session is ctlInit,
/// ctlEnumerateDevices, the caller's work, ctlClose. The main Intel GPU is the first discrete one (an Arc card beside
/// the processor's graphics), else the first. Not run on an Intel GPU: this PC has none.
/// </summary>
internal static unsafe partial class IntelIgcl
{
    internal const string DllName = "ControlLib.dll";

    // CTL_RESULT_SUCCESS; CTL_IMPL_VERSION 1.1 (CTL_MAKE_VERSION(1, 1)).
    private const int Success = 0;
    internal const uint ApiVersion = (1u << 16) | 1u;
    // ctl_adapter_properties_flag_t: CTL_ADAPTER_PROPERTIES_FLAG_INTEGRATED.
    private const uint Integrated = 1;

    internal static bool Present => File.Exists(Path.Combine(Environment.SystemDirectory, DllName));

    private static readonly Lock Gate = new();
    private static int _openFailureLogged;

    /// <summary>ctl_init_args_t (36 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct InitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public Guid ApplicationUid;
    }

    /// <summary>ctl_device_adapter_properties_t (320 bytes), read at Version 0; only the fields Exo reads.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 320)]
    internal struct DeviceProperties
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public nint DeviceId;
        [FieldOffset(16)] public uint DeviceIdSize;
        [FieldOffset(32)] public ulong DriverVersion;
        [FieldOffset(88)] public fixed byte Name[100];
        [FieldOffset(188)] public uint AdapterFlags;
    }

    /// <summary>
    /// The driver's version as Windows writes it ("32.0.101.8864"): ctl_device_adapter_properties_t's driver_version
    /// is a LARGE_INTEGER of four 16-bit parts, highest first. Null for 0.
    /// </summary>
    internal static string? DriverVersion(ulong version) =>
        version == 0 ? null : $"{version >> 48}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}.{version & 0xFFFF}";

    /// <summary>"Intel(R) Arc(TM) B580 Graphics" as "Arc B580".</summary>
    internal static string? ShortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase);
        if (text.StartsWith("Intel ", StringComparison.OrdinalIgnoreCase)) text = text[6..];
        if (text.TrimEnd().EndsWith(" Graphics", StringComparison.OrdinalIgnoreCase) && text.Trim().Contains(' ')) text = text.TrimEnd()[..^9];
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>An open IGCL session: the driver's exports and its Intel GPUs.</summary>
    private sealed class Session(nint module, nint[] devices)
    {
        internal nint[] Devices => devices;

        /// <summary>The driver's export; throws (caught by <see cref="WithSession"/>) when this driver lacks it.</summary>
        internal nint Export(string name) =>
            NativeLibrary.TryGetExport(module, name, out var address) ? address : throw new InvalidOperationException($"IGCL has no {name}.");

        /// <summary>The main Intel GPU: the first discrete one, else the first, with its name and driver version when the driver gives them.</summary>
        internal (nint Device, string? Name, string? Driver) Main()
        {
            if (!NativeLibrary.TryGetExport(module, "ctlGetDeviceProperties", out var export)) return (devices[0], null, null);
            var properties = (delegate* unmanaged[Cdecl]<nint, DeviceProperties*, int>)export;
            (nint Device, string? Name, string? Driver)? first = null;
            foreach (var device in devices)
            {
                long luid = 0;
                var props = new DeviceProperties { Size = (uint)sizeof(DeviceProperties), DeviceId = (nint)(&luid), DeviceIdSize = sizeof(long) };
                if (properties(device, &props) != Success) continue;
                var found = (device, ShortName(Encoding.UTF8.GetString(props.Name, 100).TrimEnd('\0').Trim()), DriverVersion(props.DriverVersion));
                if ((props.AdapterFlags & Integrated) == 0) return found;
                first ??= found;
            }
            return first ?? (devices[0], null, null);
        }
    }

    /// <summary>
    /// Loads IGCL, starts it and runs <paramref name="body"/> on its Intel GPUs, then closes and unloads; default when
    /// the DLL, its exports or ctlInit fail (logged once in plain words), when it lists no GPU, or when body throws
    /// (logged with <paramref name="what"/>).
    /// </summary>
    private static T? WithSession<T>(string what, Func<Session, T?> body)
    {
        if (!Present) return default;
        lock (Gate)
        {
            nint module = 0, api = 0;
            delegate* unmanaged[Cdecl]<nint, int> close = null;
            try
            {
                if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, DllName), out module)) return OpenFailed<T>("its library would not load");
                if (!NativeLibrary.TryGetExport(module, "ctlInit", out var init) || !NativeLibrary.TryGetExport(module, "ctlEnumerateDevices", out var enumerate)
                    || !NativeLibrary.TryGetExport(module, "ctlClose", out var closeExport))
                    return OpenFailed<T>("its library has no ctlInit");
                close = (delegate* unmanaged[Cdecl]<nint, int>)closeExport;
                var args = new InitArgs { Size = (uint)sizeof(InitArgs), AppVersion = ApiVersion };
                var status = ((delegate* unmanaged[Cdecl]<InitArgs*, nint*, int>)init)(&args, &api);
                if (status != Success || api == 0) { api = 0; return OpenFailed<T>($"it did not start (status 0x{status:X})"); }

                var list = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)enumerate;
                uint count = 0;
                if (list(api, &count, null) != Success || count == 0) return default;
                var devices = new nint[Math.Min(count, 8u)];
                count = (uint)devices.Length;
                fixed (nint* handles = devices)
                    if (list(api, &count, handles) != Success || count == 0) return default;
                return body(new Session(module, devices[..(int)Math.Min(count, (uint)devices.Length)]));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"IGCL {what} failed: {ex.Message}");
                return default;
            }
            finally
            {
                if (api != 0 && close is not null) close(api);
                if (module != 0) NativeLibrary.Free(module);
            }
        }
    }

    private static T? OpenFailed<T>(string why)
    {
        if (Interlocked.Exchange(ref _openFailureLogged, 1) == 0)
            AppLog.Warn($"IGCL: Intel's driver library is installed but {why}, so Exo only shows what to set in Intel Graphics Software.");
        return default;
    }
}
