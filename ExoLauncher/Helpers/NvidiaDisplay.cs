using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// A screen's output color on an NVIDIA GPU: format, depth and range, the same settings
/// as the NVIDIA app's Display > Output. Exo calls nvapi64.dll through nvapi_QueryInterface
/// (initialised by <see cref="NvidiaProfiles.Available"/>). Ids and layouts are from NVIDIA's
/// public NvAPI SDK (github.com/NVIDIA/nvapi, main as of 2026-09; nvapi_interface.h, nvapi.h,
/// nvapi_lite_common.h):
/// - NvAPI_DISP_GetDisplayIdByDisplayName 0xAE457190 (const char* "\\.\DISPLAY1", NvU32*)
/// - NvAPI_Disp_ColorControl 0x92F9D80D (NvU32 displayId, NV_COLOR_DATA*)
/// - NV_COLOR_DATA_V5 under pack(8): version@0, size@4 (NvU16), cmd@6, then data aligned to 4:
///   colorFormat@8, colorimetry@9, dynamicRange@10, bpc@12, colorSelectionPolicy@16, depth@20;
///   24 bytes, version = 24 | 5 &lt;&lt; 16.
/// - cmd GET 1, SET 2, IS_SUPPORTED_COLOR 3, GET_DEFAULT 4. Format RGB 0, YUV422 1, YUV444 2,
///   YUV420 3. Range VESA (full) 0, CEA (limited) 1, AUTO 0xFF. Bpc 6=1, 8=2, 10=3, 12=4, 16=5.
///   Policy USER 0, BEST_QUALITY 1 (the driver's "default color settings").
/// IS_SUPPORTED_COLOR answers for the current mode, so a refresh change is probed again after.
/// Checked read-only on an RTX 3070 (driver 617.42): two DisplayPort screens answer RGB and
/// YCbCr 4:4:4/4:2:2 at 6-12 bpc, YCbCr only as Limited, 16 bpc NVAPI_NOT_SUPPORTED (-104).
/// Digital vibrance and hue are not in the public SDK (NVIDIA/nvapi main @70d337d, 2026-09-18,
/// has neither). Their ids and layouts are the ones falahati/NvAPIWrapper
/// (NvAPIWrapper/Native/Helpers/FunctionId.cs; Native/Display/Structures/PrivateDisplayDVCInfoEx.cs
/// and PrivateDisplayHUEInfo.cs; Native/DisplayApi.cs) and juv/vibranceDLL, vibranceGUI's engine
/// (vibrance/vibrance.cpp), both use and agree on. Each takes (NvDisplayHandle, outputId 0, ...):
/// - NvAPI_GetAssociatedNvidiaDisplayHandle 0x35C29134 (public: const char* "\\.\DISPLAY1", NvDisplayHandle*)
/// - NvAPI_GetDVCInfoEx 0x0E45002D, NvAPI_SetDVCLevelEx 0x4A82C2B1: struct v1, 20 bytes,
///   version = 20 | 1 &lt;&lt; 16, then current, min, max, default (int each)
/// - NvAPI_GetHUEInfo 0x95B64341: struct v1, 12 bytes, version = 12 | 1 &lt;&lt; 16, current, default;
///   NvAPI_SetHUEAngle 0xF5A0F22C takes the angle as an int
/// Read on the RTX 3070 (617.42) for both screens: vibrance 0-100, default 50, now 50 (NVIDIA Control
/// Panel's percent); hue 0, default 0. Writes are clamped to what the driver reports and read back.
/// </summary>
internal static unsafe class NvidiaDisplay
{
    private const int DataSize = 24;
    private const uint DataVersion = DataSize | (5u << 16);
    private const byte Get = 1, Set = 2, IsSupported = 3, GetDefault = 4;
    private const byte ColorimetryRgb = 0, ColorimetryAuto = 0xFF;
    private const int PolicyUser = 0, PolicyBestQuality = 1;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint QueryInterface(uint id);

    private static Lock Gate => NvidiaProfiles.Gate;

    /// <summary>The NvAPI display id for a GDI name, or null when an NVIDIA GPU does not drive it.</summary>
    internal static uint? DisplayId(string gdiName)
    {
        if (!NvidiaProfiles.Available) return null;
        lock (Gate)
        {
            try
            {
                var byName = (delegate* unmanaged[Cdecl]<byte*, uint*, int>)Resolve(0xAE457190);
                var name = System.Text.Encoding.ASCII.GetBytes(gdiName + "\0");
                uint id = 0;
                fixed (byte* text = name)
                    return byName(text, &id) == 0 && id != 0 ? id : null;
            }
            catch (Exception ex) { AppLog.Debug("NvAPI display id: " + ex.Message); return null; }
        }
    }

    /// <summary>
    /// The current output color (Auto when the driver picks it), and every format, depth and
    /// range the driver says the screen takes at its current mode.
    /// </summary>
    internal static (OutputColor? Current, bool Auto, List<OutputColor> Supported) Read(uint displayId)
    {
        var supported = new List<OutputColor>();
        lock (Gate)
        {
            try
            {
                var data = stackalloc byte[DataSize];
                OutputColor? current = null;
                var auto = false;
                if (Call(displayId, data, Get, 0, 0, 0, 0, 0) == 0)
                {
                    current = Color(data);
                    auto = *(int*)(data + 16) == PolicyBestQuality;
                }
                foreach (var format in DisplayOutput.Formats)
                    foreach (var bpc in new[] { 6, 8, 10, 12 })
                        foreach (var full in format == "rgb" ? new[] { true, false } : [false])
                            if (Call(displayId, data, IsSupported, FormatCode(format), ColorimetryAuto, full ? (byte)0 : (byte)1, BpcCode(bpc), PolicyUser) == 0)
                                supported.Add(new OutputColor(format, bpc, full));
                return (current, auto, supported);
            }
            catch (Exception ex)
            {
                AppLog.Warn("NvAPI color read failed: " + ex.Message);
                return (null, false, supported);
            }
        }
    }

    /// <summary>
    /// Sets the output color (auto: the driver's own default color settings) and reads it back;
    /// null on success, else a plain line.
    /// </summary>
    internal static string? Write(uint displayId, OutputColor color, bool auto = false)
    {
        lock (Gate)
        {
            try
            {
                var data = stackalloc byte[DataSize];
                int status;
                if (auto)
                {
                    if ((status = Call(displayId, data, GetDefault, 0, 0, 0, 0, 0)) != 0) return Failed("read its default color", status);
                    color = Color(data) ?? color;
                    status = Call(displayId, data, Set, data[8], data[9], data[10], *(int*)(data + 12), PolicyBestQuality);
                }
                else
                {
                    status = Call(displayId, data, Set, FormatCode(color.Format), color.Format == "rgb" ? ColorimetryRgb : ColorimetryAuto,
                        color.Full ? (byte)0 : (byte)1, BpcCode(color.Bpc), PolicyUser);
                }
                AppLog.Info($"Display: NVIDIA color {(auto ? "driver default" : DisplayOutput.Label(color))} on 0x{displayId:X}: status {status}.");
                if (status != 0) return Failed("set that color", status);
                if (Call(displayId, data, Get, 0, 0, 0, 0, 0) != 0) return null;
                var now = Color(data);
                return now == color ? null : $"The screen runs {(now is null ? "something else" : DisplayOutput.Label(now))} instead of {DisplayOutput.Label(color)}.";
            }
            catch (Exception ex)
            {
                AppLog.Warn("NvAPI color write failed: " + ex.Message);
                return "The NVIDIA driver did not answer.";
            }
        }
    }

    /// <summary>Digital vibrance now, its range and the driver's default; null when the driver doesn't answer for this screen.</summary>
    internal static (int Level, int Min, int Max, int Default)? ReadVibrance(string gdiName)
    {
        if (!NvidiaProfiles.Available) return null;
        lock (Gate)
        {
            try
            {
                var info = stackalloc int[5];
                return ReadDvc(DisplayHandle(gdiName), info) ? (info[1], info[2], info[3], info[4]) : null;
            }
            catch (Exception ex) { AppLog.Debug("NvAPI vibrance: " + ex.Message); return null; }
        }
    }

    /// <summary>Hue in degrees now and the driver's default; null when the driver doesn't answer for this screen.</summary>
    internal static (int Angle, int Default)? ReadHue(string gdiName)
    {
        if (!NvidiaProfiles.Available) return null;
        lock (Gate)
        {
            try
            {
                var info = stackalloc int[3];
                return ReadHueInfo(DisplayHandle(gdiName), info) ? (info[1], info[2]) : null;
            }
            catch (Exception ex) { AppLog.Debug("NvAPI hue: " + ex.Message); return null; }
        }
    }

    /// <summary>Sets digital vibrance (within the driver's range) and reads it back; null on success, else a plain line.</summary>
    internal static string? SetVibrance(string gdiName, int level)
    {
        if (!NvidiaProfiles.Available) return "This PC has no NVIDIA driver Exo can use.";
        lock (Gate)
        {
            try
            {
                var handle = DisplayHandle(gdiName);
                var info = stackalloc int[5];
                if (!ReadDvc(handle, info)) return "The NVIDIA driver has no digital vibrance for this screen.";
                level = Math.Clamp(level, info[2], info[3]);
                if (level == info[1]) return null;
                var set = stackalloc int[5];
                set[0] = DvcSize | (1 << 16);
                set[1] = level;
                var status = ((delegate* unmanaged[Cdecl]<nint, uint, int*, int>)Resolve(0x4A82C2B1))(handle, 0, set);
                AppLog.Info($"Display: NVIDIA digital vibrance {level} on {gdiName}: status {status}.");
                return status == 0 && ReadDvc(handle, info) && info[1] == level ? null : "The NVIDIA driver did not take that vibrance.";
            }
            catch (Exception ex)
            {
                AppLog.Warn("NvAPI vibrance write failed: " + ex.Message);
                return "The NVIDIA driver did not answer.";
            }
        }
    }

    /// <summary>Sets the hue in degrees (0-359) and reads it back; null on success, else a plain line.</summary>
    internal static string? SetHue(string gdiName, int angle)
    {
        if (!NvidiaProfiles.Available) return "This PC has no NVIDIA driver Exo can use.";
        lock (Gate)
        {
            try
            {
                var handle = DisplayHandle(gdiName);
                var info = stackalloc int[3];
                if (!ReadHueInfo(handle, info)) return "The NVIDIA driver has no hue for this screen.";
                angle = Math.Clamp(angle, 0, 359);
                if (angle == info[1]) return null;
                var status = ((delegate* unmanaged[Cdecl]<nint, uint, int, int>)Resolve(0xF5A0F22C))(handle, 0, angle);
                AppLog.Info($"Display: NVIDIA hue {angle} on {gdiName}: status {status}.");
                return status == 0 && ReadHueInfo(handle, info) && info[1] == angle ? null : "The NVIDIA driver did not take that hue.";
            }
            catch (Exception ex)
            {
                AppLog.Warn("NvAPI hue write failed: " + ex.Message);
                return "The NVIDIA driver did not answer.";
            }
        }
    }

    private const int DvcSize = 20, HueSize = 12;

    private static nint DisplayHandle(string gdiName)
    {
        var byName = (delegate* unmanaged[Cdecl]<byte*, nint*, int>)Resolve(0x35C29134);
        var name = System.Text.Encoding.ASCII.GetBytes(gdiName + "\0");
        nint handle = 0;
        fixed (byte* text = name)
            return byName(text, &handle) == 0 ? handle : 0;
    }

    // info: version, current, min, max, default. A driver without vibrance answers all zero.
    private static bool ReadDvc(nint handle, int* info)
    {
        if (handle == 0) return false;
        new Span<int>(info, 5).Clear();
        info[0] = DvcSize | (1 << 16);
        return ((delegate* unmanaged[Cdecl]<nint, uint, int*, int>)Resolve(0x0E45002D))(handle, 0, info) == 0 && info[3] > info[2];
    }

    // info: version, current, default.
    private static bool ReadHueInfo(nint handle, int* info)
    {
        if (handle == 0) return false;
        new Span<int>(info, 3).Clear();
        info[0] = HueSize | (1 << 16);
        return ((delegate* unmanaged[Cdecl]<nint, uint, int*, int>)Resolve(0x95B64341))(handle, 0, info) == 0;
    }

    private static int Call(uint displayId, byte* data, byte cmd, byte format, byte colorimetry, byte range, int bpc, int policy)
    {
        new Span<byte>(data, DataSize).Clear();
        *(uint*)data = DataVersion;
        *(ushort*)(data + 4) = DataSize;
        data[6] = cmd;
        data[8] = format;
        data[9] = colorimetry;
        data[10] = range;
        *(int*)(data + 12) = bpc;
        *(int*)(data + 16) = policy;
        // depth@20 stays 0: keep the desktop color depth as it is.
        return ((delegate* unmanaged[Cdecl]<uint, byte*, int>)Resolve(0x92F9D80D))(displayId, data);
    }

    private static OutputColor? Color(byte* data)
    {
        var format = data[8] switch { 0 => "rgb", 2 => "444", 1 => "422", 3 => "420", _ => null };
        var bpc = *(int*)(data + 12) switch { 1 => 6, 2 => 8, 3 => 10, 4 => 12, 5 => 16, _ => 0 };
        // Range AUTO on RGB is full on a PC screen; YCbCr is always limited.
        return format is null || bpc == 0 ? null : new OutputColor(format, bpc, format == "rgb" && data[10] != 1);
    }

    private static byte FormatCode(string format) => format switch { "422" => 1, "444" => 2, "420" => 3, _ => 0 };
    private static int BpcCode(int bpc) => bpc switch { 6 => 1, 8 => 2, 10 => 3, 12 => 4, _ => 0 };

    private static string Failed(string what, int status)
    {
        AppLog.Warn($"NvAPI color: could not {what} (status {status}).");
        return status == -104 ? "The screen doesn't take that at this refresh rate." : $"The NVIDIA driver could not {what}.";
    }

    private static nint Resolve(uint id)
    {
        var fn = QueryInterface(id);
        return fn != 0 ? fn : throw new EntryPointNotFoundException($"NvAPI 0x{id:X8} is not in this driver.");
    }
}
