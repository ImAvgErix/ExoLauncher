using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// PC health's reads of the hardware: SMBIOS through GetSystemFirmwareTable (no WMI), Direct3D 12's
/// OPTIONS16 on the high-performance adapter, the processor's vendor and hybrid flag (CPUID), and Windows' power plans
/// and power mode (powrprof).
/// </summary>
internal static partial class PcHealth
{
    private static byte[] SmbiosTable()
    {
        const uint rsmb = 0x52534D42; // 'RSMB'
        try
        {
            var size = GetSystemFirmwareTable(rsmb, 0, null, 0);
            if (size == 0) return [];
            var buffer = new byte[size];
            return GetSystemFirmwareTable(rsmb, 0, buffer, size) == size ? buffer : [];
        }
        catch (EntryPointNotFoundException) { return []; }
    }

    /// <summary>
    /// The adapter Windows picks for high performance (a laptop's discrete GPU), skipping Microsoft's software
    /// renderer: its name, whether it is integrated (UMA), and GPU upload heaps, which the driver reports only when
    /// the CPU can see all of the card's memory (Resizable BAR on). Read once: it changes only with a reboot.
    /// </summary>
    private static unsafe GpuBar? ReadGpu()
    {
        try
        {
            var factoryIid = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17"); // IDXGIFactory6
            if (CreateDXGIFactory2(0, in factoryIid, out var factory) < 0) return null;
            try
            {
                var adapterIid = new Guid("29038f61-3839-4626-91fd-086879011a05"); // IDXGIAdapter1
                var byPreference = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, Guid*, IntPtr*, int>)(*(IntPtr**)factory)[29]; // EnumAdapterByGpuPreference
                for (uint i = 0; i < 8; i++)
                {
                    IntPtr adapter;
                    if (byPreference(factory, i, 2 /* DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE */, &adapterIid, &adapter) < 0) return null;
                    try
                    {
                        AdapterDesc1 desc;
                        var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(IntPtr**)adapter)[10];
                        if (getDesc1(adapter, &desc) < 0 || (desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                        var name = new string(desc.Description).TrimEnd('\0').Trim();
                        var deviceIid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7"); // ID3D12Device
                        if (D3D12CreateDevice(adapter, 0xb000, in deviceIid, out var device) < 0) return new GpuBar(name, false, null);
                        try
                        {
                            var check = (delegate* unmanaged[Stdcall]<IntPtr, int, void*, uint, int>)(*(IntPtr**)device)[13]; // CheckFeatureSupport
                            var architecture = stackalloc uint[4]; // NodeIndex, TileBasedRenderer, UMA, CacheCoherentUMA
                            architecture[0] = 0;
                            var uma = check(device, 1 /* D3D12_FEATURE_ARCHITECTURE */, architecture, 16) >= 0 && architecture[2] != 0;
                            var options16 = stackalloc int[2]; // DynamicDepthBiasSupported, GPUUploadHeapSupported
                            bool? upload = check(device, 45 /* D3D12_FEATURE_D3D12_OPTIONS16 */, options16, 8) >= 0 ? options16[1] != 0 : null;
                            return new GpuBar(name, uma, upload);
                        }
                        finally { Marshal.Release(device); }
                    }
                    finally { Marshal.Release(adapter); }
                }
                return null;
            }
            finally { Marshal.Release(factory); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            AppLog.Debug("PC health: Direct3D 12 couldn't be asked about Resizable BAR: " + ex.Message);
            return null;
        }
    }

    /// <summary>CPUID leaf 7's hybrid flag (EDX bit 15: P- and E-cores); null when CPUID can't be asked.</summary>
    private static bool? CpuHybrid()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return null;
        if (System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0).Eax < 7) return false;
        return (System.Runtime.Intrinsics.X86.X86Base.CpuId(7, 0).Edx & (1 << 15)) != 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    /// <summary>The power plan and power mode Windows has on, and the two switches Settings > Power mode itself flips.</summary>
    internal interface IPowerApi
    {
        Guid? Active();
        /// <summary>The power mode (overlay) in effect: <see cref="Guid.Empty"/> is Balanced.</summary>
        Guid Overlay();
        /// <summary>On battery or with battery saver on: the power mode is left alone then.</summary>
        bool Battery();
        string? Name(Guid scheme);
        bool SetActive(Guid scheme);
        bool SetOverlay(Guid overlay);
        /// <summary>A setting's AC value in <paramref name="scheme"/> (GUIDs as <c>powercfg /qh</c> lists them); null when the plan doesn't have it.</summary>
        uint? AcValue(Guid scheme, Guid subgroup, Guid setting);
        /// <summary>A new plan copied from <paramref name="source"/>; null when Windows refused.</summary>
        Guid? Duplicate(Guid source);
        bool Rename(Guid scheme, string name, string description);
        /// <summary>Writes a setting's AC (or, with <paramref name="dc"/>, battery) value.</summary>
        bool Write(Guid scheme, Guid subgroup, Guid setting, uint value, bool dc);
        /// <summary>Shows a setting in Control Panel's advanced power settings (Attributes 2); needs an administrator.</summary>
        bool Show(Guid subgroup, Guid setting);
    }

    /// <summary>
    /// powrprof: PowerSetActiveScheme is documented; the power mode calls (PowerGetEffectiveOverlayScheme,
    /// PowerSetActiveOverlayScheme) are not, but they are what Settings > Power mode uses, and Lenovo Legion
    /// Toolkit and G-Helper call them the same way (the GUID by value, only while Balanced is on, never on battery saver).
    /// </summary>
    internal sealed class NativePower : IPowerApi
    {
        public Guid? Active()
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero) return null;
            try { return Marshal.PtrToStructure<Guid>(pointer); }
            finally { LocalFree(pointer); }
        }

        public Guid Overlay()
        {
            try { return PowerGetEffectiveOverlayScheme(out var overlay) == 0 ? overlay : Guid.Empty; }
            catch (EntryPointNotFoundException) { return Guid.Empty; }
        }

        public bool Battery() => GetSystemPowerStatus(out var status) && (status.AcLineStatus == 0 || status.SystemStatusFlag == 1);

        /// <summary>The PC has a system battery (BatteryFlag 128: none; 255: unknown).</summary>
        internal static bool HasBattery() => GetSystemPowerStatus(out var status) && status.BatteryFlag is not (128 or 255);

        public string? Name(Guid scheme)
        {
            uint size = 0;
            if (PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size is 0 or > 4096) return null;
            var buffer = new byte[size];
            return PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
                : null;
        }

        public bool SetActive(Guid scheme) => PowerSetActiveScheme(IntPtr.Zero, in scheme) == 0;

        public uint? AcValue(Guid scheme, Guid subgroup, Guid setting) =>
            PowerReadACValueIndex(IntPtr.Zero, in scheme, in subgroup, in setting, out var value) == 0 ? value : null;

        public Guid? Duplicate(Guid source)
        {
            var made = IntPtr.Zero;
            if (PowerDuplicateScheme(IntPtr.Zero, in source, ref made) != 0 || made == IntPtr.Zero) return null;
            try { return Marshal.PtrToStructure<Guid>(made); }
            finally { LocalFree(made); }
        }

        public bool Rename(Guid scheme, string name, string description)
        {
            var nameBytes = System.Text.Encoding.Unicode.GetBytes(name + "\0");
            var descriptionBytes = System.Text.Encoding.Unicode.GetBytes(description + "\0");
            return PowerWriteFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, nameBytes, (uint)nameBytes.Length) == 0
                & PowerWriteDescription(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, descriptionBytes, (uint)descriptionBytes.Length) == 0;
        }

        public bool Write(Guid scheme, Guid subgroup, Guid setting, uint value, bool dc) =>
            (dc ? PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in subgroup, in setting, value) : PowerWriteACValueIndex(IntPtr.Zero, in scheme, in subgroup, in setting, value)) == 0;

        public bool Show(Guid subgroup, Guid setting) => PowerWriteSettingAttributes(in subgroup, in setting, 2) == 0;

        public bool SetOverlay(Guid overlay)
        {
            try { return PowerSetActiveOverlayScheme(overlay) == 0; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory2(uint flags, in Guid riid, out IntPtr factory);

    [DllImport("d3d12.dll")]
    private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, in Guid riid, out IntPtr device);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr root, in Guid scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr root, in Guid source, ref IntPtr destination);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteFriendlyName(IntPtr root, in Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDescription(IntPtr root, in Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr root, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr root, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteSettingAttributes(in Guid subgroup, in Guid setting, uint attributes);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr root, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr root, in Guid scheme, IntPtr subgroup, IntPtr setting, byte[]? buffer, ref uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
}
