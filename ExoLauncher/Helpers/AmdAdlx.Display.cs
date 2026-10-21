using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// A screen's color depth and pixel format on an AMD GPU, through ADLX's display services
/// (GPUOpen-LibrariesAndSDKs/ADLX main, SDK/Include C vtables): IADLXSystem.GetDisplaysServices 3;
/// IADLXDisplayServices GetDisplays 4, GetColorDepth 14, GetPixelFormat 15; IADLXDisplayList
/// Size 3, At_DisplayList 11; IADLXDisplay Name 6; IADLXDisplayColorDepth and
/// IADLXDisplayPixelFormat IsSupported 3 (Supported), GetValue 4, SetValue 5, IsSupported… 6.
/// ADLX_COLOR_DEPTH: BPC_6 1, BPC_8 2, BPC_10 3, BPC_12 4. ADLX_PIXEL_FORMAT: RGB_444_FULL 1,
/// YCBCR_444 2, YCBCR_422 3, RGB_444_LIMITED 4, YCBCR_420 5 (ADLXDefines.h).
/// ADLX answers format and depth apart, so every pair is offered and the driver refuses what
/// the link can't carry. Not run on an AMD GPU: this PC has none.
/// </summary>
internal static unsafe partial class AmdAdlx
{
    private const int SystemGetDisplaysServices = 3;
    private const int DisplaysGetDisplays = 4, DisplaysGetColorDepth = 14, DisplaysGetPixelFormat = 15;
    private const int DisplayName = 6;
    private const int ValueGet = 4, ValueSet = 5, ValueIsSupported = 6;

    private static readonly (string Format, bool Full, int Code)[] PixelFormats =
        [("rgb", true, 1), ("444", false, 2), ("422", false, 3), ("rgb", false, 4), ("420", false, 5)];
    private static readonly (int Bpc, int Code)[] Depths = [(6, 1), (8, 2), (10, 3), (12, 4)];

    /// <summary>The current color and every format and depth pair for the screen ADLX names <paramref name="name"/>; null when ADLX has no such screen.</summary>
    internal static (OutputColor? Current, List<OutputColor> Supported)? ReadDisplay(string name) =>
        WithDisplay(name, [DisplaysGetColorDepth, DisplaysGetPixelFormat], items =>
        {
            var (depth, format) = (items[0], items[1]);
            if (!Supported(depth) || !Supported(format)) return null;
            var bpcs =Depths.Where(d => IsSupportedValue(depth, d.Code)).Select(d => d.Bpc).ToList();
            var supported = PixelFormats.Where(f => IsSupportedValue(format, f.Code))
                .SelectMany(f => bpcs.Select(bpc => new OutputColor(f.Format, bpc, f.Full))).ToList();
            var bpcNow = Depths.FirstOrDefault(d => d.Code == GetValue(depth)).Bpc;
            var formatNow = PixelFormats.FirstOrDefault(f => f.Code == GetValue(format));
            OutputColor? current = bpcNow > 0 && formatNow.Format is not null ? new OutputColor(formatNow.Format, bpcNow, formatNow.Full) : null;
            return ((OutputColor?, List<OutputColor>)?)(current, supported);
        });

    /// <summary>Sets pixel format then color depth and reads both back; null on success, else a plain line.</summary>
    internal static string? WriteDisplay(string name, OutputColor color)
    {
        var result = WithDisplay(name, [DisplaysGetColorDepth, DisplaysGetPixelFormat], items =>
        {
            var (depth, format) = (items[0], items[1]);
            if (!Supported(depth) || !Supported(format)) return null;
            var formatCode =PixelFormats.FirstOrDefault(f => f.Format == color.Format && f.Full == color.Full).Code;
            var depthCode = Depths.FirstOrDefault(d => d.Bpc == color.Bpc).Code;
            if (formatCode == 0 || depthCode == 0) return "AMD's driver has no such setting.";
            var status = ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(format, ValueSet))(format, formatCode);
            if (status == AdlxOk) status = ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(depth, ValueSet))(depth, depthCode);
            AppLog.Info($"Display: AMD color {DisplayOutput.Label(color)}: status {status}.");
            if (status != AdlxOk) return "AMD's driver did not take that color.";
            return GetValue(format) == formatCode && GetValue(depth) == depthCode ? "" : "The screen runs something else than what was set.";
        });
        return result is null ? "AMD's driver did not find this screen." : result.Length == 0 ? null : result;
    }

    // IADLXDisplayServices.GetCustomColor is slot 16; IADLXDisplayCustomColor (IDisplaySettings.h, ADLX main 2026-09)
    // has per control IsSupported, GetRange (ADLX_IntRange min, max, step), Get, Set at base+0..3:
    // hue 3, saturation 7, brightness 11, contrast 15, temperature 19.
    // shortcut: ADLX reports no defaults, so "normal" is AMD Software's own (brightness 0, contrast 100,
    // saturation 100, hue 0, 6500 K), clamped into the range; read the driver's when ADLX offers it.
    private const int DisplaysGetCustomColor = 16;
    private static readonly (string Key, string Label, int Slot, int Normal, string Unit)[] CustomColors =
    [
        ("brightness", "Brightness", 11, 0, ""), ("contrast", "Contrast", 15, 100, ""), ("saturation", "Saturation", 7, 100, ""),
        ("hue", "Hue", 3, 0, "°"), ("temperature", "Color temperature", 19, 6500, " K"),
    ];

    [StructLayout(LayoutKind.Sequential)] private struct IntRange { public int Min, Max, Step; }

    /// <summary>AMD Software's Custom Color controls this screen has, with their ranges; null when ADLX has none.</summary>
    internal static List<ColorControl>? ReadColor(string name) =>
        WithDisplay(name, [DisplaysGetCustomColor], items =>
        {
            var color = items[0];
            var controls = new List<ColorControl>();
            foreach (var c in CustomColors)
            {
                byte on = 0;
                IntRange range = default;
                int value = 0;
                if (((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(color, c.Slot))(color, &on) != AdlxOk || on == 0) continue;
                if (((delegate* unmanaged[Stdcall]<nint, IntRange*, int>)Slot(color, c.Slot + 1))(color, &range) != AdlxOk || range.Max <= range.Min) continue;
                if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(color, c.Slot + 2))(color, &value) != AdlxOk) continue;
                controls.Add(new ColorControl(c.Key, c.Label, value, range.Min, range.Max, Math.Max(1, range.Step), Math.Clamp(c.Normal, range.Min, range.Max), c.Unit));
            }
            return controls.Count > 0 ? controls : null;
        });

    /// <summary>Sets Custom Color values (each within its range) and reads them back; null on success, else a plain line.</summary>
    internal static string? WriteColor(string name, IReadOnlyList<(string Key, int Value)> changes)
    {
        var result = WithDisplay(name, [DisplaysGetCustomColor], items =>
        {
            var color = items[0];
            foreach (var (key, wanted) in changes)
            {
                var c = CustomColors.FirstOrDefault(item => item.Key == key);
                IntRange range = default;
                int now = 0;
                if (c.Key is null || ((delegate* unmanaged[Stdcall]<nint, IntRange*, int>)Slot(color, c.Slot + 1))(color, &range) != AdlxOk)
                    return "AMD's driver has no such setting.";
                var value = Math.Clamp(wanted, range.Min, range.Max);
                var status = ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(color, c.Slot + 3))(color, value);
                AppLog.Info($"Display: AMD {key} {value}: status {status}.");
                if (status != AdlxOk || ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(color, c.Slot + 2))(color, &now) != AdlxOk || now != value)
                    return $"AMD's driver did not take that {c.Label.ToLowerInvariant()}.";
            }
            return "";
        });
        return result is null ? "AMD's driver did not find this screen." : result.Length == 0 ? null : result;
    }

    // Finds the screen, fetches the per-display interfaces at those IADLXDisplayServices slots and runs body on them (null when ADLX has no such screen or interface).
    private static T? WithDisplay<T>(string name, int[] slots, Func<nint[], T?> body)
    {
        if (!Present) return default;
        lock (Gate)
        {
            nint module = 0, system = 0, services = 0, list = 0, display = 0;
            var items = new nint[slots.Length];
            try
            {
                if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, DllName), out module)) return default;
                var queryVersion = (delegate* unmanaged[Cdecl]<ulong*, int>)NativeLibrary.GetExport(module, "ADLXQueryFullVersion");
                var initialize = (delegate* unmanaged[Cdecl]<ulong, nint*, int>)NativeLibrary.GetExport(module, "ADLXInitialize");
                ulong version = 0;
                if (queryVersion(&version) != AdlxOk || initialize(version, &system) != AdlxOk || system == 0) return default;
                if (Call(system, SystemGetDisplaysServices, &services) != AdlxOk || services == 0) return default;
                if (Call(services, DisplaysGetDisplays, &list) != AdlxOk || list == 0) return default;
                var size = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(list, ListSize))(list);
                // shortcut: matched by the monitor's name, so two identical monitors on one AMD GPU both map to the first; match by EDID when someone has that setup.
                for (uint index = 0; index < size && display == 0; index++)
                {
                    nint item = 0;
                    if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(list, ListAt))(list, index, &item) != AdlxOk || item == 0) continue;
                    byte* text = null;
                    if (((delegate* unmanaged[Stdcall]<nint, byte**, int>)Slot(item, DisplayName))(item, &text) == AdlxOk && text != null
                        && string.Equals(Marshal.PtrToStringUTF8((nint)text)?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                        display = item;
                    else ReleaseOf(item);
                }
                if (display == 0) return default;
                for (var i = 0; i < slots.Length; i++)
                {
                    nint item = 0;
                    if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(services, slots[i]))(services, display, &item) != AdlxOk || item == 0) return default;
                    items[i] = item;
                }
                return body(items);
            }
            catch (Exception ex)
            {
                AppLog.Warn("ADLX display failed: " + ex.Message);
                return default;
            }
            finally
            {
                foreach (var item in items.Concat([display, list, services])) if (item != 0) ReleaseOf(item);
                if (module != 0)
                {
                    if (system != 0 && NativeLibrary.TryGetExport(module, "ADLXTerminate", out var terminate))
                        ((delegate* unmanaged[Cdecl]<int>)terminate)();
                    NativeLibrary.Free(module);
                }
            }
        }
    }

    private static bool IsSupportedValue(nint item, int code)
    {
        byte value = 0;
        return ((delegate* unmanaged[Stdcall]<nint, int, byte*, int>)Slot(item, ValueIsSupported))(item, code, &value) == AdlxOk && value != 0;
    }

    private static int GetValue(nint item)
    {
        int value = 0;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(item, ValueGet))(item, &value) == AdlxOk ? value : 0;
    }
}
