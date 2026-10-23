namespace ExoLauncher.Helpers;

/// <summary>
/// A screen on an Intel GPU: its color format and depth and whether it can run variable refresh, by IGCL's wire format
/// (github.com/intel/drivers.gpu.control-library master, include/igcl_api.h): ctlEnumerateDisplayOutputs,
/// ctlGetDisplayProperties (Os_display_encoder_handle is Windows' display target id) and ctlGetSetWireFormat. Layouts on x64:
/// ctl_display_properties_t 200 bytes, encoder id @8, DisplayConfigFlags @48 (bit 0 active), FeatureSupportedFlags @56
/// (bit 3 VESA Adaptive Sync or HDMI VRR); ctl_wire_format_t 16 bytes (Size, Version, ColorModel@8, ColorDepth flags@12);
/// ctl_get_set_wire_format_config_t 92 bytes (Operation@8, SupportedWireFormat[4]@12, WireFormat@76).
/// Color model RGB 0, YCbCr 4:2:0 1, 4:2:2 2, 4:4:4 3; depth flags 6/8/10/12 bpc = bits 0-3.
/// The wire format has no range, so Intel screens show RGB as Full. Not run on an Intel GPU:
/// this PC has none.
/// </summary>
internal static unsafe partial class IntelIgcl
{
    internal const int PropertiesSize = 200, PropertiesActiveFlags = 48, PropertiesSupportedFeatures = 56;
    private const int WireConfigSize = 92, WireSize = 16;
    // ctl_std_display_feature_flag_t: CTL_STD_DISPLAY_FEATURE_FLAG_ADAPTIVESYNC_VRR.
    private const uint AdaptiveSyncVrr = 1u << 3;
    private static readonly string[] Models = ["rgb", "420", "422", "444"];
    private static readonly int[] DepthBits = [6, 8, 10, 12];

    private static int Send(Session session, nint output, byte* config) =>
        ((delegate* unmanaged[Cdecl]<nint, byte*, int>)session.Export("ctlGetSetWireFormat"))(output, config);

    /// <summary>The current color and every format and depth for the screen on Windows target <paramref name="target"/>; null when IGCL has no such screen.</summary>
    internal static (OutputColor? Current, List<OutputColor> Supported)? ReadDisplay(uint target) =>
        WithOutput<Tuple<OutputColor?, List<OutputColor>>>("display read", target, (session, output, _) =>
        {
            var config = stackalloc byte[WireConfigSize];
            Clear(config);
            if (Send(session, output, config) != Success) return null;
            var supported = new List<OutputColor>();
            for (var i = 0; i < 4; i++)
            {
                var entry = config + 12 + i * WireSize;
                var model = *(int*)(entry + 8);
                if (model is < 0 or > 3) continue;
                for (var bit = 0; bit < 4; bit++)
                    if ((*(uint*)(entry + 12) & (1u << bit)) != 0)
                        supported.Add(new OutputColor(Models[model], DepthBits[bit], model == 0));
            }
            return new Tuple<OutputColor?, List<OutputColor>>(Wire(config + 76), supported);
        }) is { } read ? (read.Item1, read.Item2) : null;

    /// <summary>Sets the wire format and reads it back; null on success, else a plain line.</summary>
    internal static string? WriteDisplay(uint target, OutputColor color)
    {
        var model = Array.IndexOf(Models, color.Format);
        var bit = Array.IndexOf(DepthBits, color.Bpc);
        if (model < 0 || bit < 0) return "Intel's driver has no such setting.";
        var result = WithOutput("display write", target, (session, output, _) =>
        {
            var config = stackalloc byte[WireConfigSize];
            Clear(config);
            *(int*)(config + 8) = 1; // CTL_WIRE_FORMAT_OPERATION_TYPE_SET
            *(uint*)(config + 76) = WireSize;
            *(int*)(config + 84) = model;
            *(uint*)(config + 88) = 1u << bit;
            var status = Send(session, output, config);
            AppLog.Info($"Display: Intel color {DisplayOutput.Label(color)}: status {status}.");
            if (status != Success) return "Intel's driver did not take that color.";
            Clear(config);
            return Send(session, output, config) == Success && Wire(config + 76) == color with { Full = model == 0 } ? "" : "The screen runs something else than what was set.";
        });
        return result is null ? "Intel's driver did not find this screen." : result.Length == 0 ? null : result;
    }

    /// <summary>Whether the screen on Windows target <paramref name="target"/> can run variable refresh (Adaptive Sync or HDMI VRR), as Intel's driver reports it; false when it has no such screen.</summary>
    internal static bool ScreenVrr(uint target) =>
        WithOutput<bool?>("display read", target, (_, _, props) => (*(uint*)(props + PropertiesSupportedFeatures) & AdaptiveSyncVrr) != 0) == true;

    private static OutputColor? Wire(byte* wire)
    {
        var model = *(int*)(wire + 8);
        var flags = *(uint*)(wire + 12);
        var bit = Enumerable.Range(0, 4).LastOrDefault(b => (flags & (1u << b)) != 0, -1);
        return model is >= 0 and <= 3 && bit >= 0 ? new OutputColor(Models[model], DepthBits[bit], model == 0) : null;
    }

    private static void Clear(byte* config)
    {
        new Span<byte>(config, WireConfigSize).Clear();
        *(uint*)config = WireConfigSize; // Version 0, Operation GET 0
    }

    // body gets the session, the output and its ctl_display_properties_t.
    private delegate T? OutputBody<T>(Session session, nint output, byte* props);

    /// <summary>Finds the active output on Windows target <paramref name="target"/> on any Intel GPU and runs <paramref name="body"/> on it; null when there is none.</summary>
    private static T? WithOutput<T>(string what, uint target, OutputBody<T> body) =>
        WithSession<T>(what, session =>
        {
            var outputs = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)session.Export("ctlEnumerateDisplayOutputs");
            var properties = (delegate* unmanaged[Cdecl]<nint, byte*, int>)session.Export("ctlGetDisplayProperties");
            var props = stackalloc byte[PropertiesSize];
            foreach (var device in session.Devices)
            {
                uint outputCount = 0;
                if (outputs(device, &outputCount, null) != Success || outputCount == 0) continue;
                var handles = new nint[Math.Min(outputCount, 16u)];
                outputCount = (uint)handles.Length;
                fixed (nint* list = handles)
                    if (outputs(device, &outputCount, list) != Success) continue;
                foreach (var output in handles.Take((int)Math.Min(outputCount, (uint)handles.Length)))
                {
                    new Span<byte>(props, PropertiesSize).Clear();
                    *(uint*)props = PropertiesSize;
                    if (properties(output, props) != Success || (*(uint*)(props + PropertiesActiveFlags) & 1) == 0 || *(uint*)(props + 8) != target) continue;
                    return body(session, output, props);
                }
            }
            return default;
        });
}
