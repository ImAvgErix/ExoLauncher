using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// AMD's global 3D settings through ADLX, the driver's own library
/// (amdadlx64.dll in System32, installed with AMD Software), read only. Exo
/// ships no ADLX code: it loads the driver's DLL only when it is there and
/// walks ADLX's C interface (SDK/Include in GPUOpen-LibrariesAndSDKs/ADLX:
/// ADLX.h, ISystem.h, I3DSettings.h, I3DSettings1.h, version 2.0) by its
/// vtable order. ADLX keeps these settings per GPU, not per game, so they
/// show in System > Graphics > Driver and profile only. Changing them stays in AMD Software:
/// this PC has no AMD GPU to verify a write on.
/// </summary>
internal static unsafe partial class AmdAdlx
{
    internal const string DllName = "amdadlx64.dll";


    private const int AdlxOk = 0;

    // IADLXInterface: Acquire 0, Release 1, QueryInterface 2.
    private const int Release = 1, InterfaceQuery = 2;
    // IADLXSystem (not an IADLXInterface: no Acquire or Release).
    private const int SystemGetGpus = 1, SystemGet3DSettings = 7;
    // IADLXGPUList: Size 3 … At_GPUList 11.
    private const int ListSize = 3, ListAt = 11;
    // IADLX3DSettingsServices (and …1, which adds GetAMDFluidMotionFrames at 17).
    private const int GetAntiLag = 3, GetChill = 4, GetBoost = 5, GetFrameRateTarget = 9, GetRadeonSuperResolution = 14, GetFluidMotionFrames = 17;

    /// <summary>True when the AMD driver's ADLX library is installed.</summary>
    internal static bool Present => File.Exists(Path.Combine(Environment.SystemDirectory, DllName));

    private static readonly Lock Gate = new();

    /// <summary>The first AMD GPU's Anti-Lag, Radeon Super Resolution, Fluid Motion Frames, Chill, Boost and frame rate target, as words.</summary>
    internal static IReadOnlyList<(string Label, string Value)> Read()
    {
        var facts = new List<(string Label, string Value)>();
        if (!Present) return facts;
        lock (Gate)
        {
            nint module = 0;
            nint system = 0, gpus = 0, gpu = 0, services = 0, services1 = 0;
            try
            {
                if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, DllName), out module)) return facts;
                var queryVersion = (delegate* unmanaged[Cdecl]<ulong*, int>)NativeLibrary.GetExport(module, "ADLXQueryFullVersion");
                var initialize = (delegate* unmanaged[Cdecl]<ulong, nint*, int>)NativeLibrary.GetExport(module, "ADLXInitialize");
                ulong version = 0;
                if (queryVersion(&version) != AdlxOk || initialize(version, &system) != AdlxOk || system == 0) return facts;

                if (Call(system, SystemGetGpus, &gpus) != AdlxOk || gpus == 0) return facts;
                var size = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(gpus, ListSize))(gpus);
                if (size == 0 || ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(gpus, ListAt))(gpus, 0, &gpu) != AdlxOk || gpu == 0) return facts;
                if (Call(system, SystemGet3DSettings, &services) != AdlxOk || services == 0) return facts;

                facts.AddIfRead("Anti-Lag", Feature(services, GetAntiLag, gpu, item => Enabled(item)));
                facts.AddIfRead("Radeon Super Resolution", FeatureNoGpu(services, GetRadeonSuperResolution, item => Enabled(item)));
                // Fluid Motion Frames lives on IADLX3DSettingsServices1 (ADLX 1.2 and later drivers).
                fixed (char* iid = "IADLX3DSettingsServices1")
                    if (((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Slot(services, InterfaceQuery))(services, iid, &services1) == AdlxOk && services1 != 0)
                        facts.AddIfRead("Fluid Motion Frames", FeatureNoGpu(services1, GetFluidMotionFrames, item => Enabled(item)));
                facts.AddIfRead("Radeon Chill", Feature(services, GetChill, gpu, item => Enabled(item) is { } on && on == "On"
                    ? $"{Int(item, 6)}–{Int(item, 7)} fps" : Enabled(item)));
                facts.AddIfRead("Radeon Boost", Feature(services, GetBoost, gpu, item => Enabled(item) is { } on && on == "On"
                    ? $"Down to {Int(item, 6)}%" : Enabled(item)));
                facts.AddIfRead("Frame rate target", Feature(services, GetFrameRateTarget, gpu, item => Enabled(item) is { } on && on == "On"
                    ? $"{Int(item, 6)} fps" : Enabled(item)));
            }
            catch (Exception ex)
            {
                AppLog.Warn("ADLX read failed: " + ex.Message);
            }
            finally
            {
                foreach (var item in new[] { services1, services, gpu, gpus }) if (item != 0) ReleaseOf(item);
                if (module != 0)
                {
                    if (system != 0 && NativeLibrary.TryGetExport(module, "ADLXTerminate", out var terminate))
                        ((delegate* unmanaged[Cdecl]<int>)terminate)();
                    NativeLibrary.Free(module);
                }
            }
        }
        return facts;
    }

    private static void AddIfRead(this List<(string Label, string Value)> facts, string label, string? value)
    {
        if (value is not null) facts.Add((label, value));
    }

    /// <summary>One feature object for the GPU: null when ADLX has none or it is not supported.</summary>
    private static string? Feature(nint services, int slot, nint gpu, Func<nint, string?> read)
    {
        nint item = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(services, slot))(services, gpu, &item) != AdlxOk || item == 0) return null;
        try { return Supported(item) ? read(item) : null; }
        finally { ReleaseOf(item); }
    }

    private static string? FeatureNoGpu(nint services, int slot, Func<nint, string?> read)
    {
        nint item = 0;
        if (Call(services, slot, &item) != AdlxOk || item == 0) return null;
        try { return Supported(item) ? read(item) : null; }
        finally { ReleaseOf(item); }
    }

    // Every feature interface: IsSupported 3, IsEnabled 4.
    private static bool Supported(nint item)
    {
        byte value = 0;
        return ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(item, 3))(item, &value) == AdlxOk && value != 0;
    }

    private static string? Enabled(nint item)
    {
        byte value = 0;
        return ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(item, 4))(item, &value) == AdlxOk ? value != 0 ? "On" : "Off" : null;
    }

    private static int Int(nint item, int slot)
    {
        int value = 0;
        ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(item, slot))(item, &value);
        return value;
    }

    private static int Call(nint self, int slot, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(self, slot))(self, result);

    private static void ReleaseOf(nint item) => ((delegate* unmanaged[Stdcall]<nint, long>)Slot(item, Release))(item);

    private static nint Slot(nint self, int index) => (*(nint**)self)[index];
}
