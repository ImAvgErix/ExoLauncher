using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Intel's 3D settings through IGCL's ctlGetSupported3DCapabilities and ctlGetSet3DFeature. Which features the driver has,
/// and each one's value type, come from the capabilities call: the header's own notes disagree on some (Endurance Gaming
/// is "integer" in ctl_3d_feature_t but a custom ctl_endurance_gaming_t in Intel's sample), so the driver's answer
/// decides. Every value is read and written globally (an empty application name). A write reads the whole value first,
/// so a custom value keeps its other fields and a frame limit its number, then sets it and reads it back; a write the
/// driver refuses or doesn't keep counts as a setting this driver doesn't have: it is logged and left out of later reads
/// until Exo restarts, and is not an error. IGCL can also keep values per application, but Exo knows a game by its
/// library entry, not by its executable's name, so it doesn't use them. CTL_3D_FEATURE_FRAME_GENERATION_CONTROL (20)
/// has no documented value in the header, so Exo leaves it alone.
/// </summary>
internal static unsafe partial class IntelIgcl
{
    /// <summary>The setting keys the Intel profile reads and writes.</summary>
    internal static class Keys
    {
        internal const string LowLatency = "lowLatency", FrameLimit = "frameLimit", Endurance = "endurance", Sharpening = "sharpening";
        internal const string AdaptiveSync = "adaptiveSync", ShaderDownload = "shaderDownload", FrameGen = "frameGen";
    }

    // ctl_3d_feature_t.
    internal const int FeatureEndurance = 1, FeatureFrameLimit = 2, FeatureSharpening = 7, FeatureAdaptiveSync = 10;
    internal const int FeatureLowLatency = 16, FeatureFrameGen = 17, FeatureShaderDownload = 18;
    // ctl_property_value_type_t.
    internal const int TypeBool = 0, TypeFloat = 1, TypeInt32 = 2, TypeUInt32 = 3, TypeEnum = 4, TypeCustom = 5;

    private static readonly (string Key, int Feature)[] Features =
    [
        (Keys.Endurance, FeatureEndurance), (Keys.FrameLimit, FeatureFrameLimit), (Keys.Sharpening, FeatureSharpening),
        (Keys.AdaptiveSync, FeatureAdaptiveSync), (Keys.LowLatency, FeatureLowLatency), (Keys.FrameGen, FeatureFrameGen),
        (Keys.ShaderDownload, FeatureShaderDownload),
    ];

    /// <summary>ctl_property_t (8 bytes): a get/set value, the union of the bool, float, int32, enum and uint32 forms.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    internal struct Property
    {
        [FieldOffset(0)] public byte Enable;      // BoolType.Enable, and the Enable of FloatType, IntType and UIntType
        [FieldOffset(0)] public uint EnableType;  // EnumType.EnableType
        [FieldOffset(4)] public int Value;        // IntType.Value, UIntType.Value
        [FieldOffset(4)] public float FloatValue; // FloatType.Value
    }

    /// <summary>ctl_3d_feature_getset_t (56 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FeatureGetSet
    {
        public uint Size;
        public byte Version;
        public int FeatureType;
        public nint ApplicationName;
        public sbyte ApplicationNameLength;
        public byte Set;
        public int ValueType;
        public Property Value;
        public int CustomValueSize;
        public nint CustomValue;
    }

    /// <summary>ctl_property_info_t (24 bytes): a feature's range or choices. Exo reads the int range.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropertyInfo
    {
        [FieldOffset(0)] public ulong SupportedTypes; // EnumType.SupportedTypes, which gives the union its 8-byte alignment
        [FieldOffset(4)] public int Min;              // IntType.RangeInfo.min_possible_value
        [FieldOffset(8)] public int Max;              // IntType.RangeInfo.max_possible_value
    }

    /// <summary>ctl_3d_feature_details_t (72 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FeatureDetails
    {
        public int FeatureType;
        public int ValueType;
        public PropertyInfo Value;
        public int CustomValueSize;
        public nint CustomValue;
        public byte PerAppSupport;
        public long ConflictingFeatures;
        public short FeatureMiscSupport, Reserved, Reserved1, Reserved2;
    }

    /// <summary>ctl_3d_feature_caps_t (24 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FeatureCaps
    {
        public uint Size;
        public byte Version;
        public uint NumSupportedFeatures;
        public nint Details;
    }

    /// <summary>ctl_endurance_gaming_t (8 bytes): Endurance Gaming's control (off 0, on 1, auto 2) and its mode.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EnduranceGaming
    {
        public int Control;
        public int Mode;
    }

    /// <summary>ctl_adaptivesync_getset_t (8 bytes), CTL_3D_FEATURE_ADAPTIVE_SYNC_PLUS's value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdaptiveSyncValue
    {
        public byte On;
        public byte AdaptiveBalance;
        public byte AllowAsyncForHighFps;
        public float AdaptiveBalanceStrength;
    }

    /// <summary>One feature as this driver lists it: its value type and, for an int, its range.</summary>
    internal readonly record struct Feature(int ValueType, int Min, int Max);

    // A custom value's buffer: both custom values Exo reads are 8 bytes.
    private const int CustomBytes = 16;

    /// <summary>IGCL on this PC's driver, as the Intel profile's <see cref="IGpuDriver"/>.</summary>
    internal static IGpuDriver Driver { get; } = new IgclDriver();

    // Settings the driver refused or didn't keep this run (read and written under Gate).
    private static readonly HashSet<string> Refused = [];

    /// <summary>A setting's name for a plain line.</summary>
    internal static string Label(string key) => key switch
    {
        Keys.LowLatency => "Low latency mode",
        Keys.FrameLimit => "the frame limit",
        Keys.Endurance => "Endurance Gaming",
        Keys.Sharpening => "sharpening",
        Keys.AdaptiveSync => "Adaptive Sync",
        Keys.ShaderDownload => "prebuilt shader downloads",
        Keys.FrameGen => "XeSS frame generation",
        _ => key,
    };

    /// <summary>
    /// A feature's value in Exo's terms from a get: on 1 or off 0, a level or mode as Intel numbers it (Low latency mode,
    /// Endurance Gaming's control, the frame generation override), the frame limit in fps with 0 for off; null when
    /// Exo can't read that value type.
    /// </summary>
    internal static int? Decode(string key, int valueType, in FeatureGetSet got, ReadOnlySpan<byte> custom) => valueType switch
    {
        TypeCustom when key == Keys.Endurance && custom.Length >= sizeof(EnduranceGaming) => MemoryMarshal.Read<EnduranceGaming>(custom).Control,
        TypeCustom when key == Keys.AdaptiveSync && custom.Length >= sizeof(AdaptiveSyncValue) => MemoryMarshal.Read<AdaptiveSyncValue>(custom).On != 0 ? 1 : 0,
        TypeEnum => (int)got.Value.EnableType,
        TypeInt32 or TypeUInt32 when key == Keys.FrameLimit => got.Value.Enable != 0 ? got.Value.Value : 0,
        TypeBool or TypeFloat or TypeInt32 or TypeUInt32 => got.Value.Enable != 0 ? 1 : 0,
        _ => null,
    };

    /// <summary>
    /// Turns a get (<paramref name="request"/> and its <paramref name="custom"/> value) into the set for
    /// <paramref name="value"/>, keeping every field Exo doesn't change; false when Exo can't write that value type.
    /// </summary>
    internal static bool Encode(string key, int valueType, int value, ref FeatureGetSet request, Span<byte> custom)
    {
        switch (valueType)
        {
            case TypeCustom when key == Keys.Endurance && custom.Length >= sizeof(EnduranceGaming):
                var endurance = MemoryMarshal.Read<EnduranceGaming>(custom);
                endurance.Control = value;
                MemoryMarshal.Write(custom, in endurance);
                break;
            case TypeCustom when key == Keys.AdaptiveSync && custom.Length >= sizeof(AdaptiveSyncValue):
                var sync = MemoryMarshal.Read<AdaptiveSyncValue>(custom);
                sync.On = value != 0 ? (byte)1 : (byte)0;
                MemoryMarshal.Write(custom, in sync);
                break;
            case TypeEnum when value >= 0:
                request.Value.EnableType = (uint)value;
                break;
            case TypeInt32 or TypeUInt32 when key == Keys.FrameLimit:
                request.Value.Enable = value > 0 ? (byte)1 : (byte)0;
                if (value > 0) request.Value.Value = value;
                break;
            case TypeBool or TypeFloat or TypeInt32 or TypeUInt32:
                request.Value.Enable = value != 0 ? (byte)1 : (byte)0;
                break;
            default:
                return false;
        }
        request.Set = 1;
        return true;
    }

    /// <summary>The features a capabilities answer lists, by ctl_3d_feature_t (the first entry for a feature wins).</summary>
    internal static Dictionary<int, Feature> Listed(ReadOnlySpan<FeatureDetails> details)
    {
        var features = new Dictionary<int, Feature>();
        foreach (var detail in details)
        {
            var isInt = detail.ValueType is TypeInt32 or TypeUInt32;
            features.TryAdd(detail.FeatureType, new Feature(detail.ValueType, isInt ? detail.Value.Min : 0, isInt ? detail.Value.Max : 0));
        }
        return features;
    }

    private sealed class IgclDriver : IGpuDriver
    {
        public GpuDriverState? Read() => WithSession<GpuDriverState>("settings read", session =>
        {
            var (device, name, driver) = session.Main();
            if (Capabilities(session, device) is not { } features) return null;
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            int min = 0, max = 0;
            foreach (var (key, id) in Features)
            {
                if (Refused.Contains(key) || !features.TryGetValue(id, out var feature)) continue;
                if (Get(session, device, id, feature.ValueType, key) is { } value) values[key] = value;
                if (key == Keys.FrameLimit && feature.Max > feature.Min) (min, max) = (feature.Min, feature.Max);
            }
            return new GpuDriverState(name, driver, values, min, max);
        });

        public string? Write(IReadOnlyList<KeyValuePair<string, int>> writes) => WithSession<string>("settings write", session =>
        {
            var (device, _, _) = session.Main();
            if (Capabilities(session, device) is not { } features) return "Intel's driver did not list its settings.";
            foreach (var (key, value) in writes)
            {
                var index = Array.FindIndex(Features, item => item.Key == key);
                if (index < 0) return $"Exo has no Intel setting called {key}.";
                var id = Features[index].Feature;
                if (Refused.Contains(key) || !features.TryGetValue(id, out var feature)) continue;
                var held = Set(session, device, id, feature.ValueType, key, value, out var status);
                AppLog.Info($"IGCL: {key} = {value}: status 0x{status:X}, reads {held?.ToString() ?? "nothing"}.");
                if (held == value || !Refused.Add(key)) continue;
                AppLog.Info($"IGCL: Intel's driver here {(status != Success ? "refused" : "did not keep")} {Label(key)}, so Exo treats it as a setting this driver doesn't have.");
            }
            return "";
        }) is { } result ? result.Length == 0 ? null : result : "Intel's driver did not answer.";
    }

    /// <summary>Every 3D feature the driver lists for the GPU; null when it lists none.</summary>
    private static Dictionary<int, Feature>? Capabilities(Session session, nint device)
    {
        var call = (delegate* unmanaged[Cdecl]<nint, FeatureCaps*, int>)session.Export("ctlGetSupported3DCapabilities");
        var caps = new FeatureCaps { Size = (uint)sizeof(FeatureCaps) };
        if (call(device, &caps) != Success || caps.NumSupportedFeatures == 0) return null;
        var details = new FeatureDetails[Math.Min(caps.NumSupportedFeatures, 64u)];
        fixed (FeatureDetails* list = details)
        {
            caps.NumSupportedFeatures = (uint)details.Length;
            caps.Details = (nint)list;
            if (call(device, &caps) != Success) return null;
        }
        return Listed(details.AsSpan(0, (int)Math.Min(caps.NumSupportedFeatures, (uint)details.Length)));
    }

    private static FeatureGetSet Request(int feature, int valueType, string key, byte* empty, byte* custom) => new()
    {
        Size = (uint)sizeof(FeatureGetSet),
        FeatureType = feature,
        ApplicationName = (nint)empty,
        ValueType = valueType,
        CustomValueSize = valueType == TypeCustom ? CustomSize(key) : 0,
        CustomValue = valueType == TypeCustom ? (nint)custom : 0,
    };

    /// <summary>The custom value's size for a setting whose value type is custom (0 when Exo has none for it).</summary>
    internal static int CustomSize(string key) => key switch
    {
        Keys.Endurance => sizeof(EnduranceGaming),
        Keys.AdaptiveSync => sizeof(AdaptiveSyncValue),
        _ => 0,
    };

    private static delegate* unmanaged[Cdecl]<nint, FeatureGetSet*, int> GetSet(Session session) =>
        (delegate* unmanaged[Cdecl]<nint, FeatureGetSet*, int>)session.Export("ctlGetSet3DFeature");

    /// <summary>One global get of a feature, in Exo's terms; null when the driver doesn't answer.</summary>
    private static int? Get(Session session, nint device, int feature, int valueType, string key)
    {
        var empty = stackalloc byte[1];
        empty[0] = 0;
        var custom = stackalloc byte[CustomBytes];
        new Span<byte>(custom, CustomBytes).Clear();
        var request = Request(feature, valueType, key, empty, custom);
        return GetSet(session)(device, &request) == Success ? Decode(key, valueType, request, new ReadOnlySpan<byte>(custom, CustomBytes)) : null;
    }

    /// <summary>Reads the feature, sets <paramref name="value"/> in it and reads it back; null when any step fails.</summary>
    private static int? Set(Session session, nint device, int feature, int valueType, string key, int value, out int status)
    {
        var empty = stackalloc byte[1];
        empty[0] = 0;
        var custom = stackalloc byte[CustomBytes];
        new Span<byte>(custom, CustomBytes).Clear();
        var call = GetSet(session);
        var request = Request(feature, valueType, key, empty, custom);
        status = call(device, &request);
        if (status != Success) return null;
        if (!Encode(key, valueType, value, ref request, new Span<byte>(custom, CustomBytes)))
        {
            status = Unwritable;
            return null;
        }
        status = call(device, &request);
        return status == Success ? Get(session, device, feature, valueType, key) : null;
    }

    // Not an IGCL result: Exo can't write this value type.
    private const int Unwritable = -1;
}
