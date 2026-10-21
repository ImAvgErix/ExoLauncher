namespace ExoLauncher.Helpers;

/// <summary>
/// Whether a screen can run variable refresh (G-SYNC or G-SYNC Compatible), as
/// the NVIDIA driver sees it: NvAPI_DISP_GetMonitorCapabilities (0x3B05C7E1)
/// with NV_MONITOR_CAPS_TYPE_GENERIC, whose first byte of data (offset 17 in
/// the 68-byte NV_MONITOR_CAPABILITIES_V1) has supportVRR as bit 0; and
/// NvAPI_Disp_GetVRRInfo (0xDF8FDA57, NV_GET_VRR_INFO_V1, 24 bytes) whose
/// bits are VRR enabled (0) and possible (1). Layouts from nvapi.h
/// (github.com/NVIDIA/nvapi). On this PC (driver 617.42, VG248 at 165 Hz)
/// both calls answer and both say no variable refresh.
/// </summary>
internal static unsafe partial class NvidiaProfiles
{
    /// <summary>True when the driver says the screen can run variable refresh; null when it does not answer.</summary>
    internal static bool? ScreenVrr(string gdiName)
    {
        if (NvidiaDisplay.DisplayId(gdiName) is not { } display) return null;
        lock (Gate)
        {
            try
            {
                const int capsSize = 68, vrrSize = 24;
                var caps = stackalloc byte[capsSize];
                new Span<byte>(caps, capsSize).Clear();
                *(uint*)caps = capsSize | (1u << 16);
                *(ushort*)(caps + 4) = capsSize;
                *(uint*)(caps + 8) = 0x1002; // NV_MONITOR_CAPS_TYPE_GENERIC
                var capsRead = F2(0x3B05C7E1)((nint)display, (nint)caps) == Ok && (caps[16] & 1) != 0;

                var vrr = stackalloc byte[vrrSize];
                new Span<byte>(vrr, vrrSize).Clear();
                *(uint*)vrr = vrrSize | (1u << 16);
                var vrrRead = F2(0xDF8FDA57)((nint)display, (nint)vrr) == Ok;

                if (!capsRead && !vrrRead) return null;
                return capsRead && (caps[17] & 1) != 0 || vrrRead && (*(uint*)(vrr + 4) & 0b11) != 0;
            }
            catch (Exception ex)
            {
                AppLog.Debug("NvAPI VRR: " + ex.Message);
                return null;
            }
        }
    }
}
