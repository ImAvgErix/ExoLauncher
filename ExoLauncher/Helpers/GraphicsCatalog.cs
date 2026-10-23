namespace ExoLauncher.Helpers;

/// <summary>
/// The names a Profile Inspector import is read out in, for the NVIDIA driver
/// settings people change most. Pure: no driver calls. Exo's own profile is
/// <see cref="GpuProfile"/>.
///
/// Ids and values are the driver's own (NvAPI DRS), as NVIDIA Profile
/// Inspector names them in its CustomSettingNames.xml (3.0.2.3, October 2026)
/// and NVIDIA's NvApiDriverSettings.h (R615 SDK),
/// and cross-checked as constants in the NVIDIA app 11.0.9.565's control
/// panel plug-in and backend. The driver 617.42 on an RTX 3070 lists the
/// DLSS ids, the frame rate limiter and pre-rendered frames in its own setting
/// catalogue (EnumAvailableSettingIds). It refuses to set an id it does not
/// list (NVAPI_SETTING_NOT_FOUND), which on that PC includes Smooth Motion and
/// the Ultra Low Latency pair, so the profile writes only ids in that catalogue.
/// - DLSS overrides: Super Resolution, Ray Reconstruction and Frame Generation
///   presets 0x10E41DF3 / 0x10E41DF7 / 0x10E41DF1 with "use the newest DLSS"
///   0x10E41E01 / 0x10E41E02 / 0x10E41E03 (the NVIDIA app sets both); SR mode
///   0x10AFB768 (0 Performance, 1 Balanced, 2 Quality, 4 DLAA, 5 Ultra
///   Performance); multi frame count 0x104D6667 (1 = 2x … 5 = 6x) with FG
///   mode 0x10308298 = 2 (fixed).
/// - Smooth Motion 0xB0D384C0 (0/1): RTX 50 from driver 571.86, RTX 40 from the 590 branch.
/// - Low latency mode: CPL state 0x0005F543 (0 Off, 1 On, 2 Ultra), Ultra Low
///   Latency 0x10835000 (0/1), maximum pre-rendered frames 0x007BA09E (0 = the
///   game decides, 1). Off = 0/0/0, On = 1/0/1, Ultra = 2/1/1.
/// - Max frame rate: Frame Rate Limiter V3 0x10835002, the fps (0 = off).
/// </summary>
internal static class GraphicsCatalog
{
    internal const uint SrPreset = 0x10E41DF3, RrPreset = 0x10E41DF7, FgPreset = 0x10E41DF1;
    internal const uint SrOverride = 0x10E41E01, RrOverride = 0x10E41E02, FgOverride = 0x10E41E03;
    internal const uint SrMode = 0x10AFB768, MfgCount = 0x104D6667, FgMode = 0x10308298;
    internal const uint SmoothMotion = 0xB0D384C0;
    internal const uint LowLatencyState = 0x0005F543, UltraLowLatency = 0x10835000, PreRenderedFrames = 0x007BA09E;
    internal const uint FrameRateLimit = 0x10835002;


    /// <summary>A setting's plain name, for an import's list; null when Exo has no name for it.</summary>
    internal static string? NameOf(uint id) => Names.TryGetValue(id, out var name) ? name.Name : null;

    /// <summary>A value in plain words, for an import's list.</summary>
    internal static string ValueOf(uint id, uint value)
    {
        var known = Names.GetValueOrDefault(id);
        if (known?.Values?.TryGetValue(value, out var word) == true) return word;
        if (id is FrameRateLimit) return value == 0 ? "Off" : $"{value} fps";
        if (id is SrPreset or RrPreset or FgPreset)
            return value switch
            {
                0 => "Driver default",
                0x00FFFFFF => id == FgPreset ? "Latest" : "Recommended",
                0x00FFFFFE when id == FgPreset => "Recommended",
                >= 1 and <= 26 => $"Preset {(char)('A' + value - 1)}",
                _ => value.ToString(),
            };
        if (id is MfgCount) return value is >= 1 and <= 5 ? $"{value + 1}x" : "Driver default";
        if (id is PreRenderedFrames) return value == 0 ? "Game decides" : value.ToString();
        return value <= 0xFFFF ? value.ToString() : $"0x{value:X8}";
    }

    private sealed record Known(string Name, IReadOnlyDictionary<uint, string>? Values = null);

    private static readonly Dictionary<uint, string> OnOff = new() { [0] = "Off", [1] = "On" };

    private static readonly IReadOnlyDictionary<uint, Known> Names = new Dictionary<uint, Known>
    {
        [SrPreset] = new("DLSS Super Resolution preset"),
        [RrPreset] = new("DLSS Ray Reconstruction preset"),
        [FgPreset] = new("DLSS Frame Generation preset"),
        [SrOverride] = new("Use the newest DLSS Super Resolution", OnOff),
        [RrOverride] = new("Use the newest DLSS Ray Reconstruction", OnOff),
        [FgOverride] = new("Use the newest DLSS Frame Generation", OnOff),
        [0x10E41E06] = new("DLSS Streamline override", OnOff),
        [0x00634291] = new("DLSS model preset profile", new Dictionary<uint, string> { [0] = "Driver default", [1] = "Recommended", [2] = "Custom" }),
        [SrMode] = new("DLSS mode", new Dictionary<uint, string> { [0] = "Performance", [1] = "Balanced", [2] = "Quality", [3] = "Driver default", [4] = "DLAA", [5] = "Ultra Performance", [6] = "Custom" }),
        [0x10BD9423] = new("DLSS Ray Reconstruction mode", new Dictionary<uint, string> { [0] = "Performance", [1] = "Balanced", [2] = "Quality", [3] = "Driver default", [4] = "DLAA", [5] = "Ultra Performance", [6] = "Custom" }),
        [0x10E41DF5] = new("DLSS render scale"),
        [0x10C7D4A2] = new("DLSS Ray Reconstruction render scale"),
        [0x10AFB76C] = new("DLSS Performance as Ultra Performance", OnOff),
        [0x10E41DF4] = new("DLSS modes as DLAA", OnOff),
        [FgMode] = new("Frame Generation mode", new Dictionary<uint, string> { [0] = "Driver default", [2] = "Fixed", [4] = "Dynamic" }),
        [MfgCount] = new("Frame Generation multiplier"),
        [0x10562D0F] = new("Dynamic Frame Generation limit"),
        [0x10CF4125] = new("Dynamic Frame Generation target"),
        [SmoothMotion] = new("Smooth Motion", OnOff),
        [0xB0CC0875] = new("Smooth Motion graphics APIs"),
        [LowLatencyState] = new("Low latency mode", new Dictionary<uint, string> { [0] = "Off", [1] = "On", [2] = "Ultra" }),
        [UltraLowLatency] = new("Ultra low latency", OnOff),
        [PreRenderedFrames] = new("Maximum pre-rendered frames"),
        [FrameRateLimit] = new("Max frame rate"),
        [0x1083500A] = new("Max frame rate slider"),
        [0x10835005] = new("Max frame rate in the background"),
        [0x10834FEE] = new("Frame rate limiter (old)"),
        [0x00A879CF] = new("Vertical sync", new Dictionary<uint, string>
        {
            [0x08416747] = "Off", [0x47814940] = "On", [0x18888888] = "Fast", [0x60925292] = "Game decides",
            [0x32610244] = "Half refresh rate", [0x71271021] = "Third of refresh rate", [0x13245256] = "Quarter of refresh rate",
        }),
        [0x005A375C] = new("Vertical sync tear control", new Dictionary<uint, string> { [0x96861077] = "Standard", [0x99941284] = "Adaptive" }),
        [0x1094F157] = new("G-SYNC", OnOff),
        [0x1094F1F7] = new("G-SYNC mode", new Dictionary<uint, string> { [0] = "Off", [1] = "Full screen", [2] = "Full screen and windowed" }),
        [0x1194F158] = new("G-SYNC application mode", new Dictionary<uint, string> { [0] = "Off", [1] = "Full screen", [2] = "Full screen and windowed" }),
        [0x10A879CF] = new("G-SYNC application state", new Dictionary<uint, string> { [0] = "Allow", [1] = "Force off", [2] = "Disallow", [3] = "ULMB", [4] = "Fixed refresh" }),
        [0x10A879CE] = new("Variable refresh rate", OnOff),
        [0x0064B541] = new("Preferred refresh rate", new Dictionary<uint, string> { [0] = "Game decides", [1] = "Highest available" }),
        [0x10D2BB16] = new("Anisotropic filtering mode", new Dictionary<uint, string> { [0] = "Game decides", [1] = "Forced" }),
        [0x107EFC5B] = new("Antialiasing mode", new Dictionary<uint, string> { [0] = "Game decides", [1] = "Override", [2] = "Enhance" }),
        [0x00EAD189] = new("Compile shaders while idle", new Dictionary<uint, string> { [0] = "Off", [1] = "Low", [2] = "Medium", [3] = "High" }),
        [0x1057EB71] = new("Power management mode", new Dictionary<uint, string> { [0] = "Normal", [1] = "Prefer maximum performance", [5] = "Optimal power" }),
        [0x00CE2691] = new("Texture filtering quality", new Dictionary<uint, string> { [0] = "Quality", [0x0A] = "Performance", [0x14] = "High performance", [0xFFFFFFF6] = "High quality" }),
        [0x00AC8497] = new("Shader cache size"),
        [0x00198FFF] = new("Shader cache", OnOff),
        [0x000F00BA] = new("Resizable BAR", OnOff),
        [0x000BFA21] = new("Resizable BAR", new Dictionary<uint, string> { [0] = "Off", [1] = "Auto", [2] = "On" }),
        [0x000F00BB] = new("Resizable BAR options"),
    };
}
