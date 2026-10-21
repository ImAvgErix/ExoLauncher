using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// The NVIDIA driver's settings store (NvAPI DRS), for Exo's global profile
/// (<see cref="GpuProfile"/>) and Profile Inspector imports. Exo talks to
/// nvapi64.dll directly through <c>nvapi_QueryInterface</c>; no wrapper
/// library ships with Exo.
///
/// The interface ids and structure layouts are NVIDIA's public NvAPI ones
/// (nvapi.h, nvapi_interface.h and NvApiDriverSettings.h, R615 SDK,
/// github.com/NVIDIA/nvapi), checked again in October 2026: NVDRS_SETTING
/// is still version 1 (12320 bytes, 2048-character unicode strings), and
/// every interface id below is unchanged. Every call opens and closes its own
/// session; the driver saves to its store as the signed-in user, without
/// elevation.
/// </summary>
internal static unsafe partial class NvidiaProfiles
{
    private const int Ok = 0;
    private const int SettingNotFound = -160;
    private const int InvalidUserPrivilege = -137;

    private const int UnicodeBytes = 4096;
    // NVDRS_SETTING_V1: version, name, id, type, location, two flags, two 4100-byte values.
    private const int SettingSize = 4 + UnicodeBytes + 5 * 4 + 2 * 4100;
    private const uint SettingVersion = SettingSize | (1u << 16);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint QueryInterface(uint id);

    /// <summary>The one lock for every NvAPI call (profiles, displays, scaling, video): NvAPI is not safe to call from two threads at once.</summary>
    internal static readonly Lock Gate = new();
    private static bool? _available;

    /// <summary>True when the NVIDIA driver's nvapi64.dll is present and initialises.</summary>
    internal static bool Available
    {
        get
        {
            lock (Gate)
            {
                if (_available is { } known) return known;
                try
                {
                    var init = (delegate* unmanaged[Cdecl]<int>)QueryInterface(0x0150E828);
                    _available = init is not null && init() == Ok;
                }
                catch { _available = false; }
                return _available.Value;
            }
        }
    }

    /// <summary>
    /// NvAPI loads once per process and keeps talking to the driver it found. After a driver install it is unloaded and
    /// initialised again, so profiles, displays and color reach the new driver without restarting Exo.
    /// </summary>
    internal static bool Reinitialize()
    {
        lock (Gate)
        {
            try
            {
                var unload = (delegate* unmanaged[Cdecl]<int>)QueryInterface(0xD22BDD7E);
                if (unload is not null) unload();
                var init = (delegate* unmanaged[Cdecl]<int>)QueryInterface(0x0150E828);
                _available = init is not null && init() == Ok;
            }
            catch { _available = false; }
            AppLog.Info($"NVIDIA: NvAPI loaded again for the new driver; available={_available}.");
            return _available.Value;
        }
    }

    internal sealed record Result(bool Ok, uint? Value, string? Message);

    /// <summary>One loaded DRS session, destroyed after <paramref name="body"/>.</summary>
    private static Result RunSession(Func<nint, Result> body)
    {
        if (!Available) return new Result(false, null, "This PC has no NVIDIA driver Exo can use.");
        lock (Gate)
        {
            nint session = 0;
            try
            {
                var status = F1(0x0694D52E)((nint)(&session));
                if (status != Ok) return Failed("open the driver settings", status);
                status = F1(0x375DBD6B)(session);
                if (status != Ok) return Failed("load the driver settings", status);
                return body(session);
            }
            catch (Exception ex)
            {
                AppLog.Warn("NVIDIA profile call failed: " + ex.Message);
                return new Result(false, null, "The NVIDIA driver did not answer.");
            }
            finally
            {
                if (session != 0) F1(0xDAD9CFF8)(session);
            }
        }
    }

    private static Result Failed(string what, int status)
    {
        AppLog.Warn($"NVIDIA profile: could not {what} (NvAPI status {status}).");
        return new Result(false, null, status == InvalidUserPrivilege
            ? "Windows did not let Exo change the NVIDIA driver settings."
            : $"The NVIDIA driver could not {what}.");
    }

    // Every NvAPI argument here is a handle, a pointer or a 32-bit id; on x64 each
    // travels in its own register, so one pointer-sized shape fits all of them.
    private static delegate* unmanaged[Cdecl]<nint, int> F1(uint id) => (delegate* unmanaged[Cdecl]<nint, int>)Resolve(id);
    private static delegate* unmanaged[Cdecl]<nint, nint, int> F2(uint id) => (delegate* unmanaged[Cdecl]<nint, nint, int>)Resolve(id);
    private static delegate* unmanaged[Cdecl]<nint, nint, nint, int> F3(uint id) => (delegate* unmanaged[Cdecl]<nint, nint, nint, int>)Resolve(id);
    private static delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int> F4(uint id) => (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)Resolve(id);

    private static nint Resolve(uint id)
    {
        var fn = QueryInterface(id);
        return fn != 0 ? fn : throw new EntryPointNotFoundException($"NvAPI 0x{id:X8} is not in this driver.");
    }
}
