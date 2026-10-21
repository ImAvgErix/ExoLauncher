using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Intel's global 3D settings through the Intel Graphics Control Library
/// (ControlLib.dll in System32, installed with the Arc and Iris Xe driver),
/// read only. Exo ships no IGCL code: it loads the driver's DLL only when it
/// is there and calls its C API by the layouts in igcl_api.h
/// (github.com/intel/drivers.gpu.control-library, API 1.1): ctlInit,
/// ctlEnumerateDevices, ctlGetSet3DFeature with an empty application name
/// (the adapter's global value), ctlClose. Changing them stays in Intel's own
/// app: this PC has no Intel GPU to verify a write on.
/// </summary>
internal static unsafe partial class IntelIgcl
{
    internal const string DllName = "ControlLib.dll";


    private const int FeatureFrameLimit = 2, FeatureLowLatency = 16;
    private const int ValueInt32 = 2, ValueEnum = 4;

    // ctl_init_args_t: Size, Version, AppVersion, flags, SupportedVersion, ApplicationUID (16).
    private const int InitArgsSize = 36;
    // ctl_3d_feature_getset_t on x64: Size@0, Version@4, FeatureType@8, ApplicationName@16,
    // ApplicationNameLength@24, bSet@25, ValueType@28, Value@32 (8), CustomValueSize@40, pCustomValue@48.
    private const int FeatureSize = 56;

    internal static bool Present => File.Exists(Path.Combine(Environment.SystemDirectory, DllName));

    private static readonly Lock Gate = new();

    /// <summary>The first Intel adapter's low latency mode and frame limit, as words.</summary>
    internal static IReadOnlyList<(string Label, string Value)> Read()
    {
        var facts = new List<(string Label, string Value)>();
        if (!Present) return facts;
        lock (Gate)
        {
            nint module = 0, api = 0;
            delegate* unmanaged[Cdecl]<nint, int> close = null;
            try
            {
                if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, DllName), out module)) return facts;
                var init = (delegate* unmanaged[Cdecl]<byte*, nint*, int>)NativeLibrary.GetExport(module, "ctlInit");
                var enumerate = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)NativeLibrary.GetExport(module, "ctlEnumerateDevices");
                var feature = (delegate* unmanaged[Cdecl]<nint, byte*, int>)NativeLibrary.GetExport(module, "ctlGetSet3DFeature");
                close = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(module, "ctlClose");

                var args = stackalloc byte[InitArgsSize];
                new Span<byte>(args, InitArgsSize).Clear();
                *(uint*)args = InitArgsSize;
                *(uint*)(args + 8) = (1u << 16) | 1u; // CTL_IMPL_VERSION 1.1
                if (init(args, &api) != 0 || api == 0) return facts;

                uint count = 0;
                if (enumerate(api, &count, null) != 0 || count == 0) return facts;
                var devices = new nint[Math.Min(count, 8u)];
                count = (uint)devices.Length;
                fixed (nint* list = devices)
                    if (enumerate(api, &count, list) != 0 || count == 0) return facts;

                var query = stackalloc byte[FeatureSize];
                var empty = stackalloc byte[1];
                if (Get(feature, devices[0], query, empty, FeatureLowLatency, ValueEnum))
                    facts.Add(("Low latency mode", *(uint*)(query + 32) switch { 0 => "Off", 1 => "On", 2 => "On with boost", _ => "Unknown" }));
                if (Get(feature, devices[0], query, empty, FeatureFrameLimit, ValueInt32))
                    facts.Add(("Frame limit", query[32] != 0 ? $"{*(int*)(query + 36)} fps" : "Off"));
            }
            catch (Exception ex)
            {
                AppLog.Warn("IGCL read failed: " + ex.Message);
            }
            finally
            {
                if (api != 0 && close is not null) close(api);
                if (module != 0) NativeLibrary.Free(module);
            }
        }
        return facts;
    }

    /// <summary>One global read (empty application name) into <paramref name="query"/>.</summary>
    private static bool Get(delegate* unmanaged[Cdecl]<nint, byte*, int> feature, nint device, byte* query, byte* empty, int type, int valueType)
    {
        new Span<byte>(query, FeatureSize).Clear();
        *(uint*)query = FeatureSize;
        *(int*)(query + 8) = type;
        *(nint*)(query + 16) = (nint)empty;
        *(int*)(query + 28) = valueType;
        return feature(device, query) == 0;
    }
}
