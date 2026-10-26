using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// RTX Video: NVIDIA Control Panel's Video > Adjust video image settings > RTX video enhancement
/// (Super resolution with its quality, and High Dynamic Range). Private NvAPI, decoded from the panel
/// (docs/architecture/nvidia-control-panel.md, section 5):
/// - NvAPI_GetVideoStateEx 0x0B6EF8B9 (NvDisplayHandle, GET struct 0x80 bytes, version 0x10080):
///   componentId@4, flags@0xC (bit0 supported), lastValueSet@0x58.
/// - NvAPI_SetVideoStateEx 0x9321CA5B (NvDisplayHandle, SET struct 0x40 bytes, version 0x10040):
///   componentId@4, bIsTopPriority@0xC, enable@0x10, setToValue@0x14.
/// - Component 29 Super resolution: 0 off, 1-4 quality, 5 Auto. Component 30 RTX Video HDR: 0 off,
///   5 on. Both are global (the device index is ignored).
/// Values decoded on an RTX 3070 (617.42) by setting each choice in NVIDIA Control Panel and reading it
/// back; the panel writes "off" as value 0 with enable set, as Exo does.
/// </summary>
internal static unsafe class NvidiaVideo
{
    internal const int SuperResolution = 29, VideoHdr = 30;
    private static readonly HashSet<int> Unknown = [];

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint QueryInterface(uint id);

    private static Lock Gate => NvidiaProfiles.Gate;

    /// <summary>A component's setting, or null when the card or driver has no such component.</summary>
    internal static int? Read(string gdiName, int component)
    {
        if (!NvidiaProfiles.Available) return null;
        lock (Gate)
        {
            try
            {
                if (Handle(gdiName) is not { } handle) return null;
                var data = stackalloc byte[0x80];
                new Span<byte>(data, 0x80).Clear();
                *(uint*)data = 0x10080;
                *(uint*)(data + 4) = (uint)component;
                var get = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Resolve(0x0B6EF8B9);
                if (get(handle, data) != 0 || (*(uint*)(data + 0xC) & 1) == 0) return null;
                var value = *(int*)(data + 0x58);
                // A value Exo has no name for (seen on another PC: the page then says Set elsewhere and no change sticks)
                // goes to the log once, with the struct's words around it, so it can be decoded.
                if (component == SuperResolution && value is < 0 or > 5 && Unknown.Add(value))
                    AppLog.Info($"Display: RTX Video Super Resolution reads {value} (flags 0x{*(uint*)(data + 0xC):X}, words 0x50-0x5F: " +
                        $"{*(int*)(data + 0x50)} {*(int*)(data + 0x54)} {value} {*(int*)(data + 0x5C)}); Exo knows 0-5.");
                return value;
            }
            catch (Exception ex) { AppLog.Debug("NvAPI video state read: " + ex.Message); return null; }
        }
    }

    /// <summary>Sets a component and reads it back; null on success, else a plain line.</summary>
    internal static string? Write(string gdiName, int component, int value)
    {
        lock (Gate)
        {
            try
            {
                if (Handle(gdiName) is not { } handle) return "The NVIDIA driver has no RTX Video for this screen.";
                var data = stackalloc byte[0x40];
                new Span<byte>(data, 0x40).Clear();
                *(uint*)data = 0x10040;
                *(uint*)(data + 4) = (uint)component;
                *(uint*)(data + 0xC) = 1;
                *(uint*)(data + 0x10) = 1;
                *(int*)(data + 0x14) = value;
                var set = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Resolve(0x9321CA5B);
                var status = set(handle, data);
                AppLog.Info($"Display: RTX Video component {component} = {value}: status {status}, result {*(int*)(data + 0x20)}.");
                if (status != 0 || *(int*)(data + 0x20) != 0) return "The NVIDIA driver did not take that.";
            }
            catch (Exception ex)
            {
                AppLog.Warn("NvAPI video state write failed: " + ex.Message);
                return "The NVIDIA driver did not answer.";
            }
        }
        if (Read(gdiName, component) is var now && now == value) return null;
        AppLog.Info($"Display: RTX Video component {component} set to {value} reads back {now?.ToString() ?? "nothing"}.");
        // The NVIDIA app can set RTX Video itself; where it owns it, that is where it changes (not yet confirmed on a PC that showed this).
        return "The NVIDIA driver kept its own setting. If the NVIDIA app is installed, change RTX Video there.";
    }

    private static nint? Handle(string gdiName)
    {
        var byName = (delegate* unmanaged[Cdecl]<byte*, nint*, int>)Resolve(0x35C29134);
        var name = System.Text.Encoding.ASCII.GetBytes(gdiName + "\0");
        nint handle = 0;
        fixed (byte* text = name)
            return byName(text, &handle) == 0 && handle != 0 ? handle : null;
    }

    private static nint Resolve(uint id)
    {
        var fn = QueryInterface(id);
        return fn != 0 ? fn : throw new EntryPointNotFoundException($"NvAPI 0x{id:X8} is not in this driver.");
    }
}
