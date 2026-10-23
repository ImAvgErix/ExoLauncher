using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// AMD's settings through ADLX, the driver's own library (amdadlx64.dll in System32,
/// installed with AMD Software). Exo ships no ADLX code: it loads the driver's DLL only
/// when it is there and walks ADLX's C interface (SDK/Include in
/// GPUOpen-LibrariesAndSDKs/ADLX, version 2.0: ADLX.h, ISystem.h, ISystem2.h,
/// I3DSettings.h to I3DSettings3.h, IDisplays.h, IDisplaySettings.h,
/// IPerformanceMonitoring.h) by its vtable order. Every call is checked: a null
/// object or vtable entry, or a result other than ADLX_OK, ends that read or write in
/// a plain line, and Exo falls back to showing what to set in AMD Software. ADLX keeps
/// 3D settings per GPU, not per game. Not run on an AMD GPU: this PC has none.
/// </summary>
internal static unsafe partial class AmdAdlx
{
    internal const string DllName = "amdadlx64.dll";

    // ADLX_RESULT: ADLX_OK 0, ADLX_ALREADY_ENABLED 1 (a Set that changes nothing), the rest are failures.
    private const int AdlxOk = 0, AdlxAlreadyEnabled = 1;

    // IADLXInterface: Acquire 0, Release 1, QueryInterface 2.
    private const int Release = 1, InterfaceQuery = 2;
    // IADLXSystem (not an IADLXInterface: no Acquire or Release).
    private const int SystemGetGpus = 1, SystemGet3DSettings = 7;
    // IADLXGPUList: Size 3 … At_GPUList 11.
    private const int ListSize = 3, ListAt = 11;
    // IADLXGPU: Type 5 (ADLX_GPU_TYPE: GPUTYPE_DISCRETE 2), Name 7, TotalVRAM 11.
    private const int GpuType = 5, GpuName = 7, GpuTotalVram = 11, GpuTypeDiscrete = 2;
    // IADLX3DSettingsServices (and …1, which adds GetAMDFluidMotionFrames at 17).
    private const int GetAntiLag = 3, GetChill = 4, GetBoost = 5, GetImageSharpening = 6, GetWaitForVerticalRefresh = 8, GetFrameRateTarget = 9;
    private const int GetRadeonSuperResolution = 14, GetFluidMotionFrames = 17;

    /// <summary>True when the AMD driver's ADLX library is installed.</summary>
    internal static bool Present => File.Exists(Path.Combine(Environment.SystemDirectory, DllName));

    private static readonly Lock Gate = new();
    private static int _openFailureLogged;

    /// <summary>The main AMD GPU's Anti-Lag, Radeon Super Resolution, Fluid Motion Frames, Chill, Boost, frame rate target, Image Sharpening and Wait for Vertical Refresh, as words.</summary>
    internal static IReadOnlyList<(string Label, string Value)> Read() =>
        WithGpu("read", (system, gpu) =>
        {
            var facts = new List<(string Label, string Value)>();
            nint services = 0, services1 = 0;
            try
            {
                if (Call(system, SystemGet3DSettings, &services) != AdlxOk || services == 0) return facts;
                facts.AddIfRead("Anti-Lag", Feature(services, GetAntiLag, gpu, item => Enabled(item)));
                facts.AddIfRead("Radeon Super Resolution", FeatureNoGpu(services, GetRadeonSuperResolution, item => Enabled(item)));
                // Fluid Motion Frames lives on IADLX3DSettingsServices1 (ADLX 1.2 and later drivers).
                services1 = Query(services, "IADLX3DSettingsServices1");
                if (services1 != 0) facts.AddIfRead("Fluid Motion Frames", FeatureNoGpu(services1, GetFluidMotionFrames, item => Enabled(item)));
                facts.AddIfRead("Radeon Chill", Feature(services, GetChill, gpu, item => Enabled(item) is { } on && on == "On"
                    ? $"{Int(item, 6)}–{Int(item, 7)} fps" : Enabled(item)));
                facts.AddIfRead("Radeon Boost", Feature(services, GetBoost, gpu, item => Enabled(item) is { } on && on == "On"
                    ? $"Down to {Int(item, 6)}%" : Enabled(item)));
                facts.AddIfRead("Frame rate target", Feature(services, GetFrameRateTarget, gpu, item => Enabled(item) is { } on && on == "On"
                    ? $"{Int(item, 6)} fps" : Enabled(item)));
                facts.AddIfRead("Radeon Image Sharpening", Feature(services, GetImageSharpening, gpu, item => Enabled(item)));
                facts.AddIfRead("Wait for Vertical Refresh", Feature(services, GetWaitForVerticalRefresh, gpu, item => GpuProfile.VSyncMode(Int(item, 5))));
                return facts;
            }
            finally { ReleaseAll(services1, services); }
        }) ?? [];

    /// <summary>
    /// Loads ADLX, initializes it and runs <paramref name="body"/> on its IADLXSystem, then
    /// terminates and unloads; default when the DLL, its exports or initialization fail
    /// (logged once in plain words) or when body throws (logged with <paramref name="what"/>).
    /// </summary>
    private static T? WithSystem<T>(string what, Func<nint, T?> body)
    {
        if (!Present) return default;
        lock (Gate)
        {
            nint module = 0, system = 0;
            try
            {
                if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, DllName), out module)) return OpenFailed<T>("its library would not load");
                if (!NativeLibrary.TryGetExport(module, "ADLXQueryFullVersion", out var queryExport) || !NativeLibrary.TryGetExport(module, "ADLXInitialize", out var initExport))
                    return OpenFailed<T>("its library has no ADLXInitialize");
                ulong version = 0;
                var status = ((delegate* unmanaged[Cdecl]<ulong*, int>)queryExport)(&version);
                if (status != AdlxOk) return OpenFailed<T>($"it did not give its version (status {status})");
                status = ((delegate* unmanaged[Cdecl]<ulong, nint*, int>)initExport)(version, &system);
                if (status != AdlxOk || system == 0) { system = 0; return OpenFailed<T>($"it did not start (status {status})"); }
                return body(system);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"ADLX {what} failed: {ex.Message}");
                return default;
            }
            finally
            {
                if (module != 0)
                {
                    if (system != 0 && NativeLibrary.TryGetExport(module, "ADLXTerminate", out var terminate))
                        ((delegate* unmanaged[Cdecl]<int>)terminate)();
                    NativeLibrary.Free(module);
                }
            }
        }
    }

    private static T? OpenFailed<T>(string why)
    {
        if (Interlocked.Exchange(ref _openFailureLogged, 1) == 0)
            AppLog.Warn($"ADLX: AMD's driver library is installed but {why}, so Exo only shows what to set in AMD Software.");
        return default;
    }

    /// <summary>Runs body on the main AMD GPU: the first discrete one, else the first (an APU's).</summary>
    private static T? WithGpu<T>(string what, Func<nint, nint, T?> body) =>
        WithSystem(what, system =>
        {
            nint gpus = 0, gpu = 0;
            try
            {
                if (Call(system, SystemGetGpus, &gpus) != AdlxOk || gpus == 0) return default;
                var size = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(gpus, ListSize))(gpus);
                for (uint index = 0; index < size; index++)
                {
                    nint item = 0;
                    if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(gpus, ListAt))(gpus, index, &item) != AdlxOk || item == 0) continue;
                    int type = 0;
                    var discrete = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(item, GpuType))(item, &type) == AdlxOk && type == GpuTypeDiscrete;
                    if (gpu == 0 || discrete && !IsDiscrete(gpu))
                    {
                        if (gpu != 0) ReleaseOf(gpu);
                        gpu = item;
                    }
                    else ReleaseOf(item);
                }
                return gpu == 0 ? default : body(system, gpu);
            }
            finally { ReleaseAll(gpu, gpus); }
        });

    private static bool IsDiscrete(nint gpu)
    {
        int type = 0;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(gpu, GpuType))(gpu, &type) == AdlxOk && type == GpuTypeDiscrete;
    }

    private static string? Text(nint item, int slot)
    {
        byte* text = null;
        return ((delegate* unmanaged[Stdcall]<nint, byte**, int>)Slot(item, slot))(item, &text) == AdlxOk && text != null
            ? Marshal.PtrToStringUTF8((nint)text)?.Trim() : null;
    }

    /// <summary>The interface <paramref name="iid"/> of an object (an ADLX version check), or 0 when this driver has none.</summary>
    private static nint Query(nint self, string iid)
    {
        nint result = 0;
        fixed (char* name = iid)
            return ((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Slot(self, InterfaceQuery))(self, name, &result) == AdlxOk ? result : 0;
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

    private static void ReleaseAll(params ReadOnlySpan<nint> items)
    {
        foreach (var item in items) if (item != 0) ReleaseOf(item);
    }

    /// <summary>A vtable entry; throws (caught by <see cref="WithSystem"/>) instead of calling through a null object or entry.</summary>
    private static nint Slot(nint self, int index)
    {
        if (self == 0) throw new InvalidOperationException("ADLX gave a null object.");
        var table = *(nint**)self;
        if (table == null || table[index] == 0) throw new InvalidOperationException($"ADLX object has no entry {index}.");
        return table[index];
    }
}
