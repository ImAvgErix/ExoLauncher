using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// A screen's color format and depth on an Intel GPU: IGCL's wire format
/// (github.com/intel/drivers.gpu.control-library master, include/igcl_api.h):
/// ctlEnumerateDisplayOutputs, ctlGetDisplayProperties (Os_display_encoder_handle is Windows'
/// display target id) and ctlGetSetWireFormat. Layouts on x64:
/// ctl_display_properties_t 200 bytes, encoder id @8, DisplayConfigFlags @48 (bit 0 active);
/// ctl_wire_format_t 16 bytes (Size, Version, ColorModel@8, ColorDepth flags@12);
/// ctl_get_set_wire_format_config_t 92 bytes (Operation@8, SupportedWireFormat[4]@12, WireFormat@76).
/// Color model RGB 0, YCbCr 4:2:0 1, 4:2:2 2, 4:4:4 3; depth flags 6/8/10/12 bpc = bits 0-3.
/// The wire format has no range, so Intel screens show RGB as Full. Not run on an Intel GPU:
/// this PC has none.
/// </summary>
internal static unsafe partial class IntelIgcl
{
    private const int PropertiesSize = 200, WireConfigSize = 92, WireSize = 16;
    private static readonly string[] Models = ["rgb", "420", "422", "444"];
    private static readonly int[] DepthBits = [6, 8, 10, 12];

    // call is ctlGetSetWireFormat for the output; Send(call, config) sends the request in config.
    private delegate T? WireBody<T>((nint Fn, nint Output) call, byte* config);

    private static int Send((nint Fn, nint Output) call, byte* config) =>
        ((delegate* unmanaged[Cdecl]<nint, byte*, int>)call.Fn)(call.Output, config);

    /// <summary>The current color and every format and depth for the screen on Windows target <paramref name="target"/>; null when IGCL has no such screen.</summary>
    internal static (OutputColor? Current, List<OutputColor> Supported)? ReadDisplay(uint target) =>
        WithWireFormat<Tuple<OutputColor?, List<OutputColor>>>(target, (call, config) =>
        {
            if (Send(call, config) != 0) return null;
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
        var result = WithWireFormat<string>(target, (call, config) =>
        {
            *(int*)(config + 8) = 1; // CTL_WIRE_FORMAT_OPERATION_TYPE_SET
            *(uint*)(config + 76) = WireSize;
            *(int*)(config + 84) = model;
            *(uint*)(config + 88) = 1u << bit;
            var status = Send(call, config);
            AppLog.Info($"Display: Intel color {DisplayOutput.Label(color)}: status {status}.");
            if (status != 0) return "Intel's driver did not take that color.";
            Clear(config);
            return Send(call, config) == 0 && Wire(config + 76) == color with { Full = model == 0 } ? "" : "The screen runs something else than what was set.";
        });
        return result is null ? "Intel's driver did not find this screen." : result.Length == 0 ? null : result;
    }

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

    /// <summary>
    /// Finds the active output on Windows target <paramref name="target"/> and hands <paramref name="body"/>
    /// a cleared GET request and the call that sends it with ctlGetSetWireFormat; null when there is none.
    /// </summary>
    private static T? WithWireFormat<T>(uint target, WireBody<T> body) where T : class
    {
        if (!Present) return null;
        lock (Gate)
        {
            nint module = 0, api = 0;
            delegate* unmanaged[Cdecl]<nint, int> close = null;
            try
            {
                if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, DllName), out module)) return null;
                var init = (delegate* unmanaged[Cdecl]<byte*, nint*, int>)NativeLibrary.GetExport(module, "ctlInit");
                var enumerate = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)NativeLibrary.GetExport(module, "ctlEnumerateDevices");
                var outputs = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)NativeLibrary.GetExport(module, "ctlEnumerateDisplayOutputs");
                var properties = (delegate* unmanaged[Cdecl]<nint, byte*, int>)NativeLibrary.GetExport(module, "ctlGetDisplayProperties");
                var wire = NativeLibrary.GetExport(module, "ctlGetSetWireFormat");
                close = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(module, "ctlClose");

                var args = stackalloc byte[InitArgsSize];
                new Span<byte>(args, InitArgsSize).Clear();
                *(uint*)args = InitArgsSize;
                *(uint*)(args + 8) = (1u << 16) | 1u; // CTL_IMPL_VERSION 1.1
                if (init(args, &api) != 0 || api == 0) return null;

                uint count = 0;
                if (enumerate(api, &count, null) != 0 || count == 0) return null;
                var devices = new nint[Math.Min(count, 8u)];
                count = (uint)devices.Length;
                fixed (nint* list = devices)
                    if (enumerate(api, &count, list) != 0) return null;

                var props = stackalloc byte[PropertiesSize];
                var config = stackalloc byte[WireConfigSize];
                foreach (var device in devices.Take((int)count))
                {
                    uint outputCount = 0;
                    if (outputs(device, &outputCount, null) != 0 || outputCount == 0) continue;
                    var handles = new nint[Math.Min(outputCount, 16u)];
                    outputCount = (uint)handles.Length;
                    fixed (nint* list = handles)
                        if (outputs(device, &outputCount, list) != 0) continue;
                    foreach (var output in handles.Take((int)outputCount))
                    {
                        new Span<byte>(props, PropertiesSize).Clear();
                        *(uint*)props = PropertiesSize;
                        if (properties(output, props) != 0 || (*(uint*)(props + 48) & 1) == 0 || *(uint*)(props + 8) != target) continue;
                        Clear(config);
                        return body((wire, output), config);
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Warn("IGCL display failed: " + ex.Message);
                return null;
            }
            finally
            {
                if (api != 0 && close is not null) close(api);
                if (module != 0) NativeLibrary.Free(module);
            }
        }
    }
}
