namespace ExoLauncher.Helpers;

/// <summary>A sensor line for System > Graphics: what the GPU reports right now (null when it doesn't).</summary>
internal sealed record GpuSensors(double? TemperatureC, double? HotspotC, double? Watts, int? ClockMhz, int? VramUsedMb, int? VramTotalMb, int? FanRpm, double? UsagePercent);

internal static unsafe partial class AmdAdlx
{
    /// <summary>The setting keys the Radeon profile reads and writes.</summary>
    internal static class Keys
    {
        internal const string AntiLag = "antiLag", Chill = "chill", Boost = "boost", Rsr = "rsr", EnhancedSync = "enhancedSync";
        internal const string Frtc = "frtc", FrtcFps = "frtcFps", Fsr = "fsr", Afmf = "afmf", FreeSync = "freeSync:";
        internal const string Sharpening = "ris", VSync = "vsync";
    }

    /// <summary>ADLX on this PC's driver, as the Radeon profile's <see cref="IGpuDriver"/>.</summary>
    internal static IGpuDriver Driver { get; } = new AdlxDriver();

    // Which 3D-settings interface holds a setting (0 = IADLX3DSettingsServices, 1 = …1 for AMD Fluid Motion
    // Frames, 3 = …3 for the FSR override, ADLX 1.5 drivers), its Get slot there and whether that takes the GPU,
    // then on the setting's own interface its getter and setter slot and whether the value is an int (else adlx_bool).
    // IsSupported is slot 3 on each. Slots from I3DSettings.h, I3DSettings1.h and I3DSettings3.h (ADLX 2.0), each checked
    // by compiling the headers' C vtables with MSVC x64 (offsetof / sizeof(void*)).
    private sealed record Setting(string Key, int Version, int ServiceSlot, bool PerGpu, int GetSlot, int SetSlot, bool IsInt = false);

    private static readonly Setting[] Settings =
    [
        new(Keys.AntiLag, 0, GetAntiLag, true, 4, 5),                 // IADLX3DAntiLag: IsEnabled 4, SetEnabled 5
        new(Keys.Chill, 0, GetChill, true, 4, 8),                     // IADLX3DChill: SetEnabled 8
        new(Keys.Boost, 0, GetBoost, true, 4, 7),                     // IADLX3DBoost: SetEnabled 7
        new(Keys.Sharpening, 0, GetImageSharpening, true, 4, 7),      // IADLX3DImageSharpening: IsEnabled 4, SetEnabled 7
        new(Keys.EnhancedSync, 0, 7, true, 4, 5),                     // IADLX3DEnhancedSync
        new(Keys.VSync, 0, GetWaitForVerticalRefresh, true, 5, 6, true), // IADLX3DWaitForVerticalRefresh: GetMode 5, SetMode 6 (ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE)
        new(Keys.Frtc, 0, GetFrameRateTarget, true, 4, 7),            // IADLX3DFrameRateTargetControl: SetEnabled 7
        new(Keys.FrtcFps, 0, GetFrameRateTarget, true, 6, 8, true),   // … GetFPS 6, SetFPS 8 (GetFPSRange 5)
        new(Keys.Rsr, 0, GetRadeonSuperResolution, false, 4, 5),      // IADLX3DRadeonSuperResolution (no GPU argument)
        new(Keys.Afmf, 1, GetFluidMotionFrames, false, 4, 5),         // IADLX3DAMDFluidMotionFrames (global for all GPUs)
        new(Keys.Fsr, 3, 19, true, 4, 5),                             // IADLX3DFidelityFXSuperResolution
    ];

    private const int FpsRangeSlot = 5;
    // IADLXSystem.GetDisplaysServices 3 (AmdAdlx.Display.cs); IADLXDisplayServices.GetFreeSync 9;
    // IADLXDisplayFreeSync: IsSupported 3, IsEnabled 4, SetEnabled 5.
    private const int DisplaysGetFreeSync = 9;
    // IADLXGPU2.AMDSoftwareVersion 31 (ISystem2.h).
    private const int Gpu2SoftwareVersion = 31;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Range { public int Min, Max, Step; }

    private sealed class AdlxDriver : IGpuDriver
    {
        public GpuDriverState? Read() => WithSession("settings read", session =>
        {
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            int min = 0, max = 0;
            foreach (var setting in Settings)
            {
                var item = session.Open(setting);
                if (item == 0) continue;
                try
                {
                    if (Get(item, setting) is { } value) values[setting.Key] = value;
                    if (setting.Key == Keys.FrtcFps)
                    {
                        Range range = default;
                        if (((delegate* unmanaged[Stdcall]<nint, Range*, int>)Slot(item, FpsRangeSlot))(item, &range) == AdlxOk && range.Max > range.Min)
                            (min, max) = (range.Min, range.Max);
                    }
                }
                finally { ReleaseOf(item); }
            }
            foreach (var name in session.Screens())
            {
                var item = session.OpenFreeSync(name);
                if (item == 0) continue;
                try { if (Get(item, new(Keys.FreeSync, 0, 0, false, 4, 5)) is { } on) values[Keys.FreeSync + name] = on; }
                finally { ReleaseOf(item); }
            }
            return new GpuDriverState(session.Name, session.Software, values, min, max);
        });

        public string? Write(IReadOnlyList<KeyValuePair<string, int>> writes) => WithSession("settings write", session =>
        {
            foreach (var (key, value) in writes)
            {
                var screen = key.StartsWith(Keys.FreeSync, StringComparison.Ordinal) ? key[Keys.FreeSync.Length..] : null;
                var setting = screen is not null ? new Setting(key, 0, 0, false, 4, 5) : Settings.FirstOrDefault(s => s.Key == key);
                if (setting is null) return $"Exo has no AMD setting called {key}.";
                var item = screen is not null ? session.OpenFreeSync(screen) : session.Open(setting);
                if (item == 0) return $"AMD's driver has no {Label(key)} here.";
                try
                {
                    var status = setting.IsInt
                        ? ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(item, setting.SetSlot))(item, value)
                        : ((delegate* unmanaged[Stdcall]<nint, byte, int>)Slot(item, setting.SetSlot))(item, value != 0 ? (byte)1 : (byte)0);
                    var held = Get(item, setting);
                    AppLog.Info($"ADLX: {key} = {value}: status {status}, reads {held?.ToString() ?? "nothing"}.");
                    if (status is not (AdlxOk or AdlxAlreadyEnabled)) return $"AMD's driver did not take {Label(key)}.";
                    if (held != value) return $"AMD's driver did not keep {Label(key)}.";
                }
                finally { ReleaseOf(item); }
            }
            return "";
        }) is { } result ? result.Length == 0 ? null : result : "AMD's driver did not answer.";

        private static int? Get(nint item, Setting setting)
        {
            if (setting.IsInt)
            {
                int number = 0;
                return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(item, setting.GetSlot))(item, &number) == AdlxOk ? number : null;
            }
            byte on = 0;
            return ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(item, setting.GetSlot))(item, &on) == AdlxOk ? on != 0 ? 1 : 0 : null;
        }
    }

    /// <summary>A setting's name for a plain line.</summary>
    internal static string Label(string key) => key switch
    {
        Keys.AntiLag => "Anti-Lag",
        Keys.Chill => "Radeon Chill",
        Keys.Boost => "Radeon Boost",
        Keys.Sharpening => "Radeon Image Sharpening",
        Keys.EnhancedSync => "Enhanced Sync",
        Keys.VSync => "Wait for Vertical Refresh",
        Keys.Frtc or Keys.FrtcFps => "the frame rate target",
        Keys.Rsr => "Radeon Super Resolution",
        Keys.Afmf => "Fluid Motion Frames",
        Keys.Fsr => "FSR 4 for FSR 3.1 games",
        _ when key.StartsWith(Keys.FreeSync, StringComparison.Ordinal) => $"FreeSync on {key[Keys.FreeSync.Length..]}",
        _ => key,
    };

    private static T? WithSession<T>(string what, Func<Session, T?> body) =>
        WithGpu(what, (system, gpu) =>
        {
            using var session = new Session(system, gpu);
            return body(session);
        });

    /// <summary>
    /// One ADLX session's 3D-settings and display interfaces for the main GPU. A newer interface
    /// (IADLX3DSettingsServices1, …3) is asked for once by QueryInterface; a driver without it
    /// answers ADLX_UNKNOWN_INTERFACE, and every setting it holds reads as missing.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private readonly nint _system, _gpu;
        private nint _services, _displayServices, _displays;
        private readonly Dictionary<int, nint> _versions = [];
        private bool _servicesAsked, _displaysAsked;

        internal Session(nint system, nint gpu)
        {
            (_system, _gpu) = (system, gpu);
            Name = Text(gpu, GpuName);
            var gpu2 = Query(gpu, "IADLXGPU2");
            if (gpu2 != 0)
                try { Software = Text(gpu2, Gpu2SoftwareVersion); }
                finally { ReleaseOf(gpu2); }
        }

        internal string? Name { get; }
        internal string? Software { get; }

        /// <summary>The setting's own interface when the driver has it and the GPU supports it (the caller releases it), else 0.</summary>
        internal nint Open(Setting setting)
        {
            var services = Services(setting.Version);
            if (services == 0) return 0;
            nint item = 0;
            var status = setting.PerGpu
                ? ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(services, setting.ServiceSlot))(services, _gpu, &item)
                : Call(services, setting.ServiceSlot, &item);
            if (status != AdlxOk || item == 0) return 0;
            if (Supported(item)) return item;
            ReleaseOf(item);
            return 0;
        }

        private nint Services(int version)
        {
            if (!_servicesAsked)
            {
                _servicesAsked = true;
                nint services = 0;
                if (Call(_system, SystemGet3DSettings, &services) == AdlxOk) _services = services;
            }
            if (_services == 0 || version == 0) return _services;
            if (!_versions.TryGetValue(version, out var versioned))
                _versions[version] = versioned = Query(_services, $"IADLX3DSettingsServices{version}");
            return versioned;
        }

        /// <summary>Every screen's name on AMD's display list.</summary>
        internal List<string> Screens()
        {
            var names = new List<string>();
            if (Displays() is not { } list) return names;
            var size = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(list, ListSize))(list);
            for (uint index = 0; index < size; index++)
            {
                nint item = 0;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(list, ListAt))(list, index, &item) != AdlxOk || item == 0) continue;
                try { if (Text(item, DisplayName) is { Length: > 0 } name && !names.Contains(name)) names.Add(name); }
                finally { ReleaseOf(item); }
            }
            return names;
        }

        /// <summary>The FreeSync interface of the screen named <paramref name="name"/> when it supports FreeSync, else 0.</summary>
        internal nint OpenFreeSync(string name)
        {
            if (Displays() is not { } list) return 0;
            var size = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(list, ListSize))(list);
            for (uint index = 0; index < size; index++)
            {
                nint display = 0;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(list, ListAt))(list, index, &display) != AdlxOk || display == 0) continue;
                try
                {
                    if (!string.Equals(Text(display, DisplayName), name, StringComparison.OrdinalIgnoreCase)) continue;
                    nint item = 0;
                    if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(_displayServices, DisplaysGetFreeSync))(_displayServices, display, &item) != AdlxOk || item == 0) return 0;
                    if (Supported(item)) return item;
                    ReleaseOf(item);
                    return 0;
                }
                finally { ReleaseOf(display); }
            }
            return 0;
        }

        private nint? Displays()
        {
            if (!_displaysAsked)
            {
                _displaysAsked = true;
                nint services = 0, list = 0;
                if (Call(_system, SystemGetDisplaysServices, &services) == AdlxOk && services != 0)
                {
                    _displayServices = services;
                    if (Call(services, DisplaysGetDisplays, &list) == AdlxOk) _displays = list;
                }
            }
            return _displays == 0 ? null : _displays;
        }

        public void Dispose()
        {
            foreach (var item in _versions.Values) if (item != 0) ReleaseOf(item);
            ReleaseAll(_displays, _displayServices, _services);
        }
    }

    // IADLXSystem.GetPerformanceMonitoringServices 9. IADLXPerformanceMonitoringServices: GetCurrentGPUMetrics 18,
    // GetSupportedGPUMetrics 21 (both take the GPU). IADLXGPUMetricsSupport: IsSupportedGPUUsage 3, ClockSpeed 4,
    // Temperature 6, HotspotTemperature 7, Power 8, TotalBoardPower 9, FanSpeed 10, VRAM 11. IADLXGPUMetrics:
    // GPUUsage 4 (double %), GPUClockSpeed 5 (int MHz), GPUTemperature 7, GPUHotspotTemperature 8 (double °C),
    // GPUPower 9, GPUTotalBoardPower 10 (double W), GPUFanSpeed 11 (int RPM), GPUVRAM 12 (int MB). IPerformanceMonitoring.h.
    private const int SystemGetPerformance = 9, PerfCurrentGpu = 18, PerfSupportedGpu = 21;

    /// <summary>The main AMD GPU's temperature, hotspot, board power, clock, VRAM in use and fan, as it reports them now; null when ADLX can't.</summary>
    internal static GpuSensors? ReadSensors() => WithGpu("sensors", (system, gpu) =>
    {
        nint services = 0, support = 0, metrics = 0;
        try
        {
            if (Call(system, SystemGetPerformance, &services) != AdlxOk || services == 0) return null;
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(services, PerfSupportedGpu))(services, gpu, &support) != AdlxOk || support == 0) return null;
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(services, PerfCurrentGpu))(services, gpu, &metrics) != AdlxOk || metrics == 0) return null;
            var (has, now) = (support, metrics);
            bool Has(int slot)
            {
                byte on = 0;
                return ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(has, slot))(has, &on) == AdlxOk && on != 0;
            }
            double? Real(int supportSlot, int slot)
            {
                double value = 0;
                return Has(supportSlot) && ((delegate* unmanaged[Stdcall]<nint, double*, int>)Slot(now, slot))(now, &value) == AdlxOk && double.IsFinite(value) ? value : null;
            }
            int? Whole(int supportSlot, int slot)
            {
                int value = 0;
                return Has(supportSlot) && ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(now, slot))(now, &value) == AdlxOk ? value : null;
            }
            uint total = 0;
            var vramTotal = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(gpu, GpuTotalVram))(gpu, &total) == AdlxOk && total > 0 ? (int?)total : null;
            return new GpuSensors(Real(6, 7), Real(7, 8), Real(9, 10) ?? Real(8, 9), Whole(4, 5), Whole(11, 12), vramTotal, Whole(10, 11), Real(3, 4));
        }
        finally { ReleaseAll(metrics, support, services); }
    });

    // IADLXSystem.GetGPUTuningServices 8; IADLXGPUTuningServices1 (IGPUTuning1.h, by QueryInterface).GetSmartAccessMemory 18;
    // IADLXSmartAccessMemory (ISmartAccessMemory.h): IsSupported 3, IsEnabled 4. Its SetEnabled (5) is deprecated and answers
    // ADLX_NOT_SUPPORTED: Resizable BAR is turned on in the BIOS, so Exo only reads it.
    private const int SystemGetGpuTuning = 8, TuningGetSmartAccessMemory = 18;

    /// <summary>Whether Smart Access Memory (AMD's Resizable BAR) is on for the main AMD GPU; null when ADLX can't say or this PC can't run it.</summary>
    internal static bool? SmartAccessMemory() => WithGpu<bool?>("Smart Access Memory read", (system, gpu) =>
    {
        nint tuning = 0, tuning1 = 0, sam = 0;
        try
        {
            if (Call(system, SystemGetGpuTuning, &tuning) != AdlxOk || tuning == 0) return null;
            tuning1 = Query(tuning, "IADLXGPUTuningServices1");
            if (tuning1 == 0 || ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(tuning1, TuningGetSmartAccessMemory))(tuning1, gpu, &sam) != AdlxOk || sam == 0)
                return null;
            if (!Supported(sam)) return null;
            byte on = 0;
            return ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(sam, 4))(sam, &on) == AdlxOk ? on != 0 : null;
        }
        finally { ReleaseAll(sam, tuning1, tuning); }
    });
}
