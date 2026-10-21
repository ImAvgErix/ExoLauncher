using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Windows HDR on the main screen, through the documented display configuration API
/// (the same calls Windows Settings uses; no admin rights). Windows 11 24H2 split HDR
/// from Auto Color Management, so the newer HDR-only calls are used when Windows has them;
/// older builds use the advanced-color calls. Switching HDR can reset the screen's
/// gamma ramp (a calibration), so the ramp is read before and put back after.
/// </summary>
internal static class WindowsHdr
{
    /// <summary>One active screen; PixelRate is its link's pixel clock now (Hz, 0 when Windows doesn't say).</summary>
    internal sealed record Screen(Luid Adapter, uint Target, uint Source, string GdiName, string Name, string DevicePath,
        bool Supported, bool Enabled, bool NewApi, long PixelRate = 0);

    /// <summary>The main screen's HDR state; null when Windows does not answer.</summary>
    internal static Screen? Primary()
    {
        var primary = PrimaryGdiName();
        return primary is null ? null : All().FirstOrDefault(screen => string.Equals(screen.GdiName, primary, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every active screen with its HDR state, in Windows' path order; empty when Windows does not answer.</summary>
    internal static List<Screen> All()
    {
        var screens = new List<Screen>();
        try
        {
            if (!ActivePaths(out var paths, out var modes)) return screens;
            foreach (var path in paths)
            {
                var gdi = SourceGdiName(path);
                // A cloned source shows once, as its first target.
                if (gdi.Length == 0 || screens.Any(screen => string.Equals(screen.GdiName, gdi, StringComparison.OrdinalIgnoreCase))) continue;
                var (supported, enabled, newApi) = ReadHdr(path.targetInfo.adapterId, path.targetInfo.id);
                var (name, devicePath) = TargetName(path.targetInfo.adapterId, path.targetInfo.id);
                // DISPLAYCONFIG_TARGET_MODE: the video signal's pixel rate is the union's first field.
                var mode = path.targetInfo.modeInfoIdx < modes.Length ? modes[path.targetInfo.modeInfoIdx] : default;
                screens.Add(new Screen(path.targetInfo.adapterId, path.targetInfo.id, path.sourceInfo.id, gdi, name, devicePath,
                    supported, enabled, newApi, mode.infoType == 2 ? (long)mode.u0 : 0));
            }
        }
        catch (Exception ex) { AppLog.Debug("HDR state: " + ex.GetType().Name); }
        return screens;
    }

    /// <summary>
    /// The screen's own peak brightness in nits, from the HDR block of its EDID (what the
    /// maker states, before any calibration); null when the EDID has none.
    /// </summary>
    internal static int? PeakNits(Screen screen) => ReadEdid(screen) is { } edid ? EdidPeakNits(edid) : null;

    /// <summary>What the screen's EDID says it takes; null when Windows has no EDID for it.</summary>
    internal static MonitorCaps? Caps(Screen screen) => Edid.Parse(ReadEdid(screen));

    private static byte[]? ReadEdid(Screen screen)
    {
        try
        {
            // \\?\DISPLAY#ACI27A1#5&1a2b&0&UID4352#{guid} is Enum\DISPLAY\ACI27A1\5&1a2b&0&UID4352.
            var parts = screen.DevicePath.Split('#');
            if (parts.Length < 3) return null;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            return key?.GetValue("EDID") as byte[];
        }
        catch (Exception ex) { AppLog.Debug("EDID: " + ex.GetType().Name); return null; }
    }

    /// <summary>Max luminance from the CTA-861 HDR static metadata block: 50 x 2^(code/32) nits.</summary>
    internal static int? EdidPeakNits(byte[] edid)
    {
        if (edid.Length < 256) return null;
        for (var block = 128; block + 128 <= edid.Length; block += 128)
        {
            if (edid[block] != 0x02) continue; // CTA-861 extension
            var end = Math.Min(block + Math.Max((int)edid[block + 2], 4), block + 127);
            for (var at = block + 4; at < end;)
            {
                var tag = edid[at] >> 5;
                var length = edid[at] & 0x1F;
                // Extended tag 6: HDR static metadata (EOTFs, descriptor, max, max frame-average, min).
                if (tag == 7 && length >= 4 && at + 4 < edid.Length && edid[at + 1] == 6 && edid[at + 4] != 0)
                    return (int)Math.Round(50 * Math.Pow(2, edid[at + 4] / 32.0));
                at += 1 + length;
            }
        }
        return null;
    }

    /// <summary>
    /// True when Windows holds an HDR color profile for the screen (the HDR Calibration app makes one).
    /// Exo's own SDR gamma profile is not a calibration: while it is the default, the one it replaced counts.
    /// </summary>
    internal static bool HasHdrProfile(Screen screen, string? replaced)
    {
        try
        {
            const int currentUser = 1, iccProfile = 0, extendedDisplayColorMode = 8;
            if (ColorProfileGetDisplayDefault(currentUser, screen.Adapter, screen.Source, iccProfile, extendedDisplayColorMode, out var name) != 0)
                return false;
            var text = name != IntPtr.Zero ? Marshal.PtrToStringUni(name) : null;
            var has = !string.IsNullOrWhiteSpace(text) &&
                (!text.StartsWith(SdrInHdr.Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(replaced));
            if (name != IntPtr.Zero) LocalFree(name);
            return has;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private static (string Name, string DevicePath) TargetName(Luid adapter, uint target)
    {
        var request = new TargetDeviceName { header = Header(2, Marshal.SizeOf<TargetDeviceName>(), adapter, target), friendlyName = "", devicePath = "" };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? (request.friendlyName ?? "", request.devicePath ?? "") : ("", "");
    }

    /// <summary>Switches HDR on that screen; true when Windows took it.</summary>
    internal static bool Set(Screen screen, bool on)
    {
        if (screen.NewApi)
        {
            var set = new SetHdrState { header = Header(16, Marshal.SizeOf<SetHdrState>(), screen.Adapter, screen.Target), value = on ? 1u : 0u };
            return DisplayConfigSetDeviceInfo(ref set) == 0;
        }
        var legacy = new SetAdvancedColor { header = Header(10, Marshal.SizeOf<SetAdvancedColor>(), screen.Adapter, screen.Target), enable = on ? 1u : 0u };
        return DisplayConfigSetDeviceInfo(ref legacy) == 0;
    }

    /// <summary>The screen's gamma ramp (3 x 256), or null.</summary>
    internal static ushort[]? ReadRamp(string gdiName)
    {
        var dc = CreateDC("DISPLAY", gdiName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try { var ramp = new ushort[3 * 256]; return GetDeviceGammaRamp(dc, ramp) ? ramp : null; }
        finally { DeleteDC(dc); }
    }

    /// <summary>Sets the screen's gamma ramp; false when Windows refused it (it refuses ramps far from a straight line).</summary>
    internal static bool WriteRamp(string gdiName, ushort[] ramp)
    {
        var dc = CreateDC("DISPLAY", gdiName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try { return SetDeviceGammaRamp(dc, ramp); }
        finally { DeleteDC(dc); }
    }

    private static (bool Supported, bool Enabled, bool NewApi) ReadHdr(Luid adapter, uint target)
    {
        if (Environment.OSVersion.Version.Build >= 26100)
        {
            var info2 = new AdvancedColorInfo2 { header = Header(15, Marshal.SizeOf<AdvancedColorInfo2>(), adapter, target) };
            if (DisplayConfigGetDeviceInfo(ref info2) == 0)
            {
                var supported = (info2.value & 0x10) != 0;
                return (supported, supported && ((info2.value & 0x20) != 0 || info2.activeColorMode == 2), true);
            }
        }
        var info = new AdvancedColorInfo { header = Header(9, Marshal.SizeOf<AdvancedColorInfo>(), adapter, target) };
        if (DisplayConfigGetDeviceInfo(ref info) != 0) return (false, false, false);
        // Bit 2, wide color enforced: an SDR screen under Auto color management, which reads as advanced color too.
        var hdr = (info.value & 1) != 0 && (info.value & 4) == 0;
        return (hdr, hdr && (info.value & 2) != 0, false);
    }

    private static bool ActivePaths(out PathInfo[] paths, out ModeInfo[] modes)
    {
        paths = [];
        modes = [];
        const uint onlyActive = 2;
        if (GetDisplayConfigBufferSizes(onlyActive, out var pathCount, out var modeCount) != 0) return false;
        paths = new PathInfo[pathCount];
        modes = new ModeInfo[modeCount];
        if (QueryDisplayConfig(onlyActive, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return false;
        Array.Resize(ref paths, (int)pathCount);
        Array.Resize(ref modes, (int)modeCount);
        return true;
    }

    private static string SourceGdiName(PathInfo path)
    {
        var request = new SourceDeviceName { header = Header(1, Marshal.SizeOf<SourceDeviceName>(), path.sourceInfo.adapterId, path.sourceInfo.id), viewGdiDeviceName = "" };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request.viewGdiDeviceName ?? "" : "";
    }

    internal static string? PrimaryGdiName()
    {
        var monitor = MonitorFromPoint(0, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>(), szDevice = "" };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info.szDevice : null;
    }

    private static DeviceInfoHeader Header(int type, int size, Luid adapter, uint id) =>
        new() { type = type, size = (uint)size, adapterId = adapter, id = id };

    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoHeader { public int type; public uint size; public Luid adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedColorInfo { public DeviceInfoHeader header; public uint value; public int colorEncoding; public uint bitsPerColorChannel; }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedColorInfo2 { public DeviceInfoHeader header; public uint value; public int colorEncoding; public uint bitsPerColorChannel; public int activeColorMode; }
    [StructLayout(LayoutKind.Sequential)] private struct SetAdvancedColor { public DeviceInfoHeader header; public uint enable; }
    [StructLayout(LayoutKind.Sequential)] private struct SetHdrState { public DeviceInfoHeader header; public uint value; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName { public DeviceInfoHeader header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader header; public uint flags; public uint outputTechnology; public ushort manufacturerId; public ushort productCode; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathSourceInfo { public Luid adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling;
        public uint refreshNumerator; public uint refreshDenominator; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public PathSourceInfo sourceInfo; public PathTargetInfo targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ModeInfo { public uint infoType; public uint id; public Luid adapterId; public ulong u0, u1, u2, u3, u4, u5; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize; public int left, top, right, bottom, workLeft, workTop, workRight, workBottom; public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo2 request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);
    [DllImport("mscms.dll")] private static extern int ColorProfileGetDisplayDefault(int scope, Luid adapter, uint source, int type, int subtype, out IntPtr name);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref SetAdvancedColor request);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref SetHdrState request);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(long point, uint flags); // POINT {0,0} packed in 64 bits
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
    [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
}
