using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// A screen's scaling on an NVIDIA GPU: NVIDIA Control Panel's Display > Adjust desktop size and
/// position > Scaling (mode and "Perform scaling on"). Public NvAPI (github.com/NVIDIA/nvapi, nvapi.h):
/// - NvAPI_DISP_GetDisplayConfig 0x11ABCCF8 (NvU32* pathCount, NV_DISPLAYCONFIG_PATH_INFO*), called three
///   times: count, then targetInfoCount, then everything (the SDK's own sample).
/// - NvAPI_DISP_SetDisplayConfig 0x5D8CF8DE (NvU32 pathCount, NV_DISPLAYCONFIG_PATH_INFO*, NvU32 flags).
/// - x64 layouts: PATH_INFO_V2 48 bytes (version@0, sourceId@4, targetInfoCount@8, targetInfo*@16,
///   sourceModeInfo*@24, flags@32, pOSAdapterID*@40), version 48 | 2 &lt;&lt; 16; PATH_TARGET_INFO_V2 24 bytes
///   (displayId@0, details*@8, targetId@16); PATH_ADVANCED_TARGET_INFO_V1 128 bytes, version 0x10080,
///   scaling@8; SOURCE_MODE_INFO_V1 under 64 bytes.
/// - NV_SCALING: "Force GPU" 2 full-screen, 3 no scaling, 5 aspect ratio, 8 integer; "Balanced" (the
///   display scales) 1 full-screen, 6 aspect ratio, 7 no scaling. 0 is the driver's default.
/// The driver call order and the advanced-target size and scaling offset were confirmed in NVIDIA Control
/// Panel's own code (docs/architecture/nvidia-control-panel.md); read on an RTX 3070 (617.42): both
/// screens 3 (no scaling, on the GPU). A change is a mode set: the screen blinks once.
/// "Override the scaling mode set by games and programs" is private: NvAPI_DISP_GetScalingCapsOverride
/// 0x0BB06808 / NvAPI_DISP_SetScalingCapsOverride 0xC89D5384 (displayId, struct): 32 bytes, version
/// 32 | 2 &lt;&lt; 16, the switch at @16 (1 on), the scaling it holds at @28 (read only). The write is shaped differently: see WriteOverride. Found by the driver accepting
/// only that size and version, then turning the switch off and on in NVIDIA Control Panel and reading @16
/// follow it, on one screen only (RTX 3070, 617.42).
/// </summary>
internal static unsafe class NvidiaScaling
{
    internal readonly record struct Scaling(string Mode, bool OnGpu);

    private const int PathSize = 48, TargetSize = 24, DetailsSize = 128, SourceSize = 64;
    private const uint PathVersion = PathSize | (2u << 16), DetailsVersion = 0x10080, SaveToPersistence = 0x2;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint QueryInterface(uint id);

    private static Lock Gate => NvidiaProfiles.Gate;

    internal static Scaling? FromCode(int code) => code switch
    {
        2 => new("full", true), 3 => new("none", true), 5 => new("aspect", true), 8 => new("integer", true),
        1 => new("full", false), 7 => new("none", false), 6 => new("aspect", false),
        _ => null,
    };

    internal static int? ToCode(Scaling scaling) => (scaling.Mode, scaling.OnGpu) switch
    {
        ("full", true) => 2, ("none", true) => 3, ("aspect", true) => 5, ("integer", true) => 8,
        ("full", false) => 1, ("none", false) => 7, ("aspect", false) => 6,
        _ => null,
    };

    /// <summary>The raw NV_SCALING of the screen with this NvAPI display id, or null when the driver doesn't say.</summary>
    internal static int? Read(uint displayId) => WithConfig(displayId, null, out var code) ? code : null;

    /// <summary>Sets the raw NV_SCALING and reads it back; null on success, else a plain line.</summary>
    internal static string? Write(uint displayId, int code)
    {
        if (!WithConfig(displayId, code, out _)) return "The NVIDIA driver did not take that scaling.";
        Thread.Sleep(300);
        return Read(displayId) == code ? null : "The NVIDIA driver kept its own scaling.";
    }

    /// Reads the whole display config; with <paramref name="set"/>, changes this screen's scaling and applies it.
    private static bool WithConfig(uint displayId, int? set, out int code)
    {
        code = 0;
        if (!NvidiaProfiles.Available) return false;
        lock (Gate)
        {
            var blocks = new List<nint>();
            nint Alloc(int size) { var p = (nint)NativeMemory.AllocZeroed((nuint)size); blocks.Add(p); return p; }
            try
            {
                var get = (delegate* unmanaged[Cdecl]<uint*, byte*, int>)Resolve(0x11ABCCF8);
                uint count = 0;
                if (get(&count, null) != 0 || count == 0 || count > 16) return false;
                var paths = (byte*)Alloc(PathSize * (int)count);
                for (var i = 0; i < count; i++) *(uint*)(paths + i * PathSize) = PathVersion;
                if (get(&count, paths) != 0) return false;
                for (var i = 0; i < count; i++)
                {
                    var path = paths + i * PathSize;
                    var targets = *(uint*)(path + 8);
                    if (targets is 0 or > 8) return false;
                    *(nint*)(path + 24) = Alloc(SourceSize);
                    var targetInfo = (byte*)Alloc(TargetSize * (int)targets);
                    *(nint*)(path + 16) = (nint)targetInfo;
                    for (var j = 0; j < targets; j++)
                    {
                        var details = (byte*)Alloc(DetailsSize);
                        *(uint*)details = DetailsVersion;
                        *(nint*)(targetInfo + j * TargetSize + 8) = (nint)details;
                    }
                }
                if (get(&count, paths) != 0) return false;

                byte* mine = null;
                for (var i = 0; i < count && mine is null; i++)
                {
                    var path = paths + i * PathSize;
                    var targetInfo = *(byte**)(path + 16);
                    for (var j = 0; j < *(uint*)(path + 8); j++)
                        if (*(uint*)(targetInfo + j * TargetSize) == displayId) { mine = *(byte**)(targetInfo + j * TargetSize + 8); break; }
                }
                if (mine is null) return false;
                code = *(int*)(mine + 8);
                if (set is not { } wanted || wanted == code) return true;

                *(int*)(mine + 8) = wanted;
                var apply = (delegate* unmanaged[Cdecl]<uint, byte*, uint, int>)Resolve(0x5D8CF8DE);
                // NV_DISPLAYCONFIG_SAVE_TO_PERSISTENCE: without it the driver applies the topology but keeps its stored scaling.
                var status = apply(count, paths, SaveToPersistence);
                AppLog.Info($"Display: NVIDIA scaling {code} -> {wanted} on 0x{displayId:X}: status {status}.");
                return status == 0;
            }
            catch (Exception ex)
            {
                AppLog.Warn("NvAPI display config failed: " + ex.Message);
                return false;
            }
            finally
            {
                foreach (var block in blocks) NativeMemory.Free((void*)block);
            }
        }
    }

    private const int OverrideSize = 32;
    private const uint OverrideVersion = OverrideSize | (2u << 16);

    /// <summary>Whether the screen's scaling overrides what games and programs ask for; null when the driver doesn't say.</summary>
    internal static bool? ReadOverride(uint displayId)
    {
        if (!NvidiaProfiles.Available) return null;
        lock (Gate)
        {
            try
            {
                var data = stackalloc byte[OverrideSize];
                return GetOverride(displayId, data) ? *(int*)(data + 16) != 0 : null;
            }
            catch (Exception ex) { AppLog.Debug("NvAPI scaling override read: " + ex.Message); return null; }
        }
    }

    /// <summary>Turns the override on or off and reads it back; null on success, else a plain line.</summary>
    internal static string? WriteOverride(uint displayId, bool on)
    {
        lock (Gate)
        {
            try
            {
                var data = stackalloc byte[OverrideSize];
                if (!GetOverride(displayId, data)) return "The NVIDIA driver has no scaling override for this screen.";
                // As NVIDIA Control Panel builds it, fresh, with a third argument of 0: on sets only @16; off sets every
                // flag the panel knows (@4, @8, @12, @16 at 1, @20 at 7), which the read then reports as 0. All zero is
                // rejected (NVAPI_INVALID_ARGUMENT). Found by writing each shape on one screen and reading it back.
                new Span<byte>(data, OverrideSize).Clear();
                *(uint*)data = OverrideVersion;
                *(int*)(data + 16) = 1;
                if (!on) { *(int*)(data + 4) = 1; *(int*)(data + 8) = 1; *(int*)(data + 12) = 1; *(int*)(data + 20) = 7; }
                var status = ((delegate* unmanaged[Cdecl]<uint, byte*, uint, int>)Resolve(0xC89D5384))(displayId, data, 0);
                AppLog.Info($"Display: NVIDIA scaling override {(on ? "on" : "off")} on 0x{displayId:X}: status {status}.");
                if (status != 0) return "The NVIDIA driver did not take that.";
            }
            catch (Exception ex)
            {
                AppLog.Warn("NvAPI scaling override write failed: " + ex.Message);
                return "The NVIDIA driver did not answer.";
            }
        }
        return ReadOverride(displayId) == on ? null : "The NVIDIA driver kept its own setting.";
    }

    private static bool GetOverride(uint displayId, byte* data)
    {
        new Span<byte>(data, OverrideSize).Clear();
        *(uint*)data = OverrideVersion;
        return ((delegate* unmanaged[Cdecl]<uint, byte*, int>)Resolve(0x0BB06808))(displayId, data) == 0;
    }

    private static nint Resolve(uint id)
    {
        var fn = QueryInterface(id);
        return fn != 0 ? fn : throw new EntryPointNotFoundException($"NvAPI 0x{id:X8} is not in this driver.");
    }
}
