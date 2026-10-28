using System.Text.RegularExpressions;

namespace ExoLauncher.Helpers;

/// <summary>
/// Exo's NVIDIA global profile: every driver setting it writes, per GeForce
/// series and per sync choice (G-SYNC or No G-SYNC). Pure: no driver calls,
/// so every value is tested. docs/architecture/gpu-profiles.md has the full table with
/// the reason and source for each value, and the settings Exo leaves alone.
///
/// Built from a real RTX 3070 global profile (driver 617.42), each setting
/// checked against NVIDIA's NvApiDriverSettings.h (github.com/NVIDIA/nvapi,
/// R615 SDK) and NVIDIA Profile Inspector 3.0.2.3's CustomSettingNames.xml /
/// Reference.xml. A null value is the driver's own default: Exo removes the
/// user's value so NVIDIA's (predefined or built-in) one applies.
///
/// Key sources (October 2026):
/// - G-SYNC: Blur Busters G-SYNC 101, "Optimal G-SYNC Settings"
///   (blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/14/):
///   G-SYNC on, V-Sync On in the driver and off in games, Low latency On,
///   a cap under the refresh rate. The cap is NVIDIA Reflex's own,
///   refresh x 3600 / (refresh + 3600) (Blur Busters forum, G-SYNC 101
///   discussion, forums.blurbusters.com/viewtopic.php?p=125563).
/// - DLSS on every RTX card, each feature with the newest DLL and the "Custom" model profile
///   (the only one under which a forced letter applies). Super Resolution: NVIDIA's
///   Recommended on RTX 40/50 (M/L/K per mode, fast with FP8), K on RTX 20/30, where M and L
///   cost 15-30% (techspot.com/article/3080-nvidia-dlss-45-vs-40/, hothardware.com/news/nvidia-dlss-45-benchmarked).
///   Ray Reconstruction: F (DLSS 4.5, second-generation transformer) on RTX 20/30, where NVIDIA's
///   own DLSS-RR guide times it equal to or faster than D/E; Recommended (F today) on RTX 40/50.
///   Frame Generation: C, NVIDIA's performance preset (DLSS 310.9.16, up to 35% cheaper).
/// - Shader cache: Unlimited with room (the driver's 16 GB default, and even 100 GB,
///   evict games' shaders, which then recompile with stutter: Marvel Rivals' whole set
///   alone is ~80 GB), never smaller than set.
/// </summary>
internal static partial class GpuProfile
{
    /// <summary>The main screen: its name, current refresh rate, and whether the driver says it can run variable refresh.</summary>
    internal sealed record Screen(string? Name, int RefreshHz, bool Vrr);

    /// <summary>
    /// One AMD or Intel setting, in plain words, next to what their driver holds
    /// (<see cref="Current"/>): what to set by hand on a Radeon whose ADLX, or an Intel GPU whose IGCL, doesn't answer.
    /// </summary>
    internal sealed record Item(string Key, string Label, string Value, string Why, string? Current = null);

    internal const uint GsyncFeature = 0x1094F157, GsyncGlobalMode = 0x1094F1F7, GsyncAppMode = 0x1194F158;
    internal const uint GsyncAppState = 0x10A879CF, GsyncAppRequest = 0x10A879AC, VrrControl = 0x10A879CE;
    internal const uint VSync = 0x00A879CF, VSyncOn = 0x47814940;
    internal const uint FrameRateLimit = 0x10835002, FrameRateLimitSlider = 0x1083500A;
    internal const uint ModelPresetProfile = 0x00634291;
    internal const uint ShaderCacheSize = 0x00AC8497, CacheUnlimited = 0xFFFFFFFF;
    // Preset letters are A = 1 ... O = 15; Recommended lets NVIDIA pick per mode (SR/RR).
    private const uint PresetC = 3, PresetF = 6, PresetK = 11, Recommended = 0x00FFFFFF;

    /// <summary>What a profile is built for: the GeForce series (0 = not one), G-SYNC or not, and the main screen's refresh rate.</summary>
    internal sealed record Variant(int Series, bool Gsync, int RefreshHz)
    {
        internal bool Rtx => Series >= 20;
        internal bool Fp8 => Series >= 40;
        internal uint? Cap => Gsync && FrameCap(RefreshHz) is > 0 and var cap ? (uint)cap : null;
    }

    /// <summary>
    /// Settings in a stock global profile that Exo leaves exactly as they are:
    /// the control panel's own greyed-out hints (it rewrites them), NVIDIA's private
    /// post-processing flags, and Resizable BAR's size limit (a binary value
    /// Exo's backup cannot hold).
    /// </summary>
    internal static readonly uint[] LeftAlone = [0x000F00FF, 0x00666665, 0x00676635, 0x00676869, 0x10C158AD, 0x11112233, 0x11112256];

    /// <param name="Touches">False: Exo neither writes nor resets the setting for that variant.</param>
    private sealed record Rule(uint Id, Func<Variant, uint?> Value, Func<Variant, bool>? Touches = null);

    private static Rule Set(uint id, uint? value) => new(id, _ => value);
    private static Rule Default(uint id) => new(id, _ => null);
    // Never written nor reset: the value stays the player's (or what NVIDIA's app set).
    private static Rule Leave(uint id) => new(id, _ => null, _ => false);

    // Every setting Exo writes. docs/architecture/gpu-profiles.md gives each one's reason and source.
    private static readonly Rule[] Rules =
    [
        // Latency: Low latency mode On (one queued frame), not Ultra: with a driver frame cap Blur Busters
        // (G-SYNC 101 p.14) says On; Ultra only adds its own cap. Reflex overrides it; Vulkan ignores it.
        Set(0x0005F543, 1), Set(0x10835000, 0), Set(0x007BA09E, 1),
        Default(0x10111133),                                       // VR pre-rendered frames

        // G-SYNC / variable refresh: full screen only, or off. Borderless DX12 games and DX10/11 games under
        // Windows 11's windowed-game optimizations already count as full screen; "and windowed" globally lets
        // desktop apps start variable refresh (stutter, and flicker on OLED and VA). display-best-settings.md.
        new(GsyncFeature, v => v.Gsync ? 1u : 0u),
        new(GsyncGlobalMode, v => v.Gsync ? 1u : 0u),
        new(GsyncAppMode, v => v.Gsync ? 1u : 0u),
        new(VrrControl, v => v.Gsync ? 1u : 0u),
        Default(GsyncAppState), Default(GsyncAppRequest),          // Allow: games may use it
        Set(0x1095F16F, 0),                                        // VRR overlay indicator off

        // V-Sync and the frame cap.
        new(VSync, v => v.Gsync ? VSyncOn : null),
        Default(0x005A375C), Default(0x101AE763),                  // tear control Standard, SLI smooth AFR
        new(FrameRateLimit, v => v.Cap), new(FrameRateLimitSlider, v => v.Cap),
        Default(0x10835005), Default(0x10835006), Default(0x1083502F),
        Set(0x0064B541, 1),                                        // preferred refresh rate: highest available
        Default(0x00666634), Default(0x201F619F), Default(0x20D690F8), Default(0x20FDD1F9),
        Default(0x10D91A4C), Default(0x10115C89), Default(0x10834FFE),

        // Performance first (the user's call: heat and power don't matter): texture filtering
        // High performance with its trilinear and anisotropic optimizations, the most frames
        // the driver gives here. Anisotropic filtering and antialiasing stay each game's own:
        // forcing them off overrides what a player picked in the game's menu.
        Set(0x00CE2691, 0x14), Set(0x002ECAF2, 0), Set(0x0084CD70, 1), Set(0x00E73211, 1),
        Default(0x101E61A9), Default(0x10D2BB16),
        Default(0x0098C1AC), Default(0x107AFC5B), Default(0x107D639D), Default(0x107EFC5B), Default(0x10D773D2),
        Default(0x10D48A85), Default(0x10ECDB82), Default(0x10FC2D9C), Default(0x2089BF6C), Default(0x1034CB89), Default(0x1074C972),

        // Rendering extras: off unless a game or the NVIDIA app turns them on.
        Default(0x00667329), Default(0x1035DB89), Default(0x1075D972), Default(0x00980881), Default(0x00980896),
        Default(0x10444444), Default(0x00ABAC21), Default(0x00ABAC31), Default(0x01ABAC23),
        Default(0x00D5E9C6), Default(0x00D5E9C7), Default(0x105E2A1D),
        Leave(0xB0D384C0), Leave(0xB0CC0875),                      // Smooth Motion: the player's (adds lag when on)

        // System: NVIDIA's per-game Resizable BAR list (forcing it on everywhere costs frames in
        // games NVIDIA left it off for), maximum performance power, compute defaults.
        Default(0x000F00BA), Default(0x000F00BB), Default(0x000BFA21),     // rBAR switch, options, v2 switch (616.56+)
        Default(0x00198FFF), Set(0x00EAD189, 3),                   // shader cache on; compile shaders while idle (High: done sooner)
        Default(0x00211020), Default(0x00C63A79), Default(0x00DB3858), Set(0x1057EB71, 1), // power: prefer maximum performance
        Default(0x10ECECC9), Default(0x50166C5E), Default(0x20C1221E),
        Default(0x202888C1), Default(0x209DF23E), Default(0x20378242), Default(0x206194D6), Default(0x206C28C4), Default(0x2072C5A3),

        // DLSS on every RTX card: the newest DLL the driver has for each feature and the best preset
        // per feature for the card. "Custom" on every RTX card: under "Recommended" the driver ignores
        // forced letters (Frame Generation C would never apply). GTX cards (no DLSS) are left as they
        // are. Modes and ratios stay the game's.
        // Super Resolution: RTX 40/50 run M and L fast (FP8), so NVIDIA's Recommended picks per mode;
        // RTX 20/30 lack FP8 (M and L cost 15-30%), so K, the best model they run fast.
        // Ray Reconstruction: F (second-generation transformer) runs as fast as D/E on RTX 20/30 (faster
        // on RTX 20, per NVIDIA's DLSS-RR guide); RTX 40/50 get Recommended (F today, newer models later).
        // Frame Generation: C, the performance preset, everywhere; RTX 20/30 have no native frame
        // generation, mods that bring it there read the same preset.
        new(ModelPresetProfile, v => v.Rtx ? 2u : null),
        new(0x10E41DF3, v => v.Fp8 ? Recommended : v.Rtx ? PresetK : null), // Super Resolution preset
        new(0x10E41E01, v => v.Rtx ? 1u : null),
        new(0x10E41DF7, v => v.Fp8 ? Recommended : v.Rtx ? PresetF : null), // Ray Reconstruction preset
        new(0x10E41E02, v => v.Rtx ? 1u : null),
        new(0x10E41DF1, v => PresetC, v => v.Rtx),                         // Frame Generation preset
        new(0x10E41E03, v => 1u, v => v.Rtx),                              // Frame Generation: newest DLL
        Default(0x10E41E04), Default(0x10AFB768), Default(0x10BD9423), Default(0x10E41DF5), Default(0x10C7D4A2),
        Leave(0x104D6667), Default(0x10308298), Default(0x10E41DF6),       // multi frame count: the player's
        Leave(0x10E41E06), Leave(0x10E41E05), // Streamline overrides (616.56+): the overrides above work without them; NVIDIA's app sets them only per tested game
    ];

    /// <summary>
    /// The GeForce series whose rules fit an adapter (10, 16, 20, 30, 40, 50); null for cards older
    /// than Pascal and anything that isn't one. A newer GeForce than Exo knows gets the newest rules
    /// (it has everything RTX 50 has); workstation RTX cards get their generation's GeForce rules.
    /// </summary>
    internal static int? NvidiaSeries(string adapter)
    {
        if (WorkstationPattern().Match(adapter) is { Success: true } pro)
            return pro.Groups["turing"].Success ? 20 : pro.Groups["ampere"].Success ? 30 : pro.Groups["ada"].Success ? 40 : 50;
        var match = SeriesPattern().Match(adapter);
        if (match.Success)
        {
            var series = int.Parse(match.Groups[1].Value);
            return series is 10 or 16 or 20 or 30 or 40 or 50 ? series : series > 50 && series < 100 ? 50 : null;
        }
        if (TitanPattern().Match(adapter) is { Success: true } titan)
            return titan.Groups[1].Value.Equals("RTX", StringComparison.OrdinalIgnoreCase) ? 20 : 10;
        return null;
    }

    /// <summary>
    /// NVIDIA Reflex's own cap for a refresh rate (refresh x 3600 / (refresh + 3600), rounded down),
    /// never closer than 3 below refresh: under 100 Hz Reflex's cap sits 1-2 fps under it, where a
    /// frame-time spike over the limit meets V-Sync lag (Blur Busters: cap at least 3 under).
    /// </summary>
    internal static int FrameCap(int refreshHz) => refreshHz <= 0 ? 0 : Math.Min(refreshHz * 3600 / (refreshHz + 3600), refreshHz - 3);

    /// <summary>Whether Exo has a rule for the setting, including ones it deliberately never touches for some variants.</summary>
    internal static bool Decides(uint id) => id == ShaderCacheSize || Rules.Any(rule => rule.Id == id);

    /// <summary>
    /// Every write of the profile for <paramref name="variant"/> (null = the
    /// driver's default), only for ids the driver lists (it refuses the rest).
    /// <paramref name="current"/> is what the global profile holds now: the
    /// shader cache is never made smaller.
    /// </summary>
    internal static IReadOnlyDictionary<uint, uint?> Nvidia(Variant variant, long cacheDriveFreeGb, IReadOnlySet<uint> known, IReadOnlyDictionary<uint, uint?> current)
    {
        var writes = Rules.Where(rule => known.Contains(rule.Id) && (rule.Touches?.Invoke(variant) ?? true))
            .ToDictionary(rule => rule.Id, rule => rule.Value(variant));
        if (known.Contains(ShaderCacheSize))
        {
            // Unlimited with room for it: at any fixed size a big game evicts the others' shaders (Marvel Rivals fills ~80 GB
            // alone). Short of space, what is set (or the driver's 16 GB) stays.
            var now = current.GetValueOrDefault(ShaderCacheSize);
            writes[ShaderCacheSize] = cacheDriveFreeGb >= 200 ? CacheUnlimited : now;
        }
        return writes;
    }

    /// <summary>applied (the driver holds every write), changed (Exo applied it and something moved since), or ready.</summary>
    internal static string Status<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> writes, IReadOnlyDictionary<TKey, TValue> values, bool applied) where TKey : notnull =>
        writes.All(write => EqualityComparer<TValue>.Default.Equals(values.GetValueOrDefault(write.Key), write.Value)) ? "applied" : applied ? "changed" : "ready";

    /// <summary>
    /// What Undo must put back: the first backup's value for every id it already
    /// holds (a re-apply or a switch of variant never overwrites the original),
    /// what the driver holds now for the rest.
    /// </summary>
    internal static Dictionary<TKey, TValue> MergeBackup<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? earlier, IReadOnlyDictionary<TKey, TValue> now, IEnumerable<TKey> ids) where TKey : notnull
    {
        var merged = earlier is null ? new Dictionary<TKey, TValue>() : new Dictionary<TKey, TValue>(earlier);
        foreach (var id in ids) merged.TryAdd(id, now.TryGetValue(id, out var value) ? value : default!);
        return merged;
    }

    /// <summary>The Radeon profile as what to set in AMD Software, next to what the driver holds: shown only when Exo can't reach ADLX.</summary>
    internal static IReadOnlyList<Item> Amd(Screen screen, IReadOnlyList<(string Label, string Value)> facts)
    {
        string? Now(string label) => facts.FirstOrDefault(fact => fact.Label == label).Value;
        var cap = FrameCap(screen.RefreshHz);
        return
        [
            new("antiLag", "Anti-Lag", "On in each game", "The CPU waits for the GPU instead of queueing frames: less input lag. Set it per game: on everywhere it also runs in Windows' own apps and costs CPU. Games with Anti-Lag 2 use their own.", Now("Anti-Lag")),
            new("freeSync", "FreeSync", "On", "If the screen has it: no tearing and no V-Sync lag. Set it in AMD Software > Display."),
            new("frameCap", "Frame rate target", cap > 0 ? $"{cap} fps" : "Under the refresh rate",
                "With FreeSync, keeps every frame inside its range. Off when the screen has no FreeSync.", Now("Frame rate target")),
            new("chill", "Radeon Chill", "Off", "Chill lowers the frame rate to save power, which costs frames and responsiveness.", Now("Radeon Chill")),
            new("boost", "Radeon Boost", "Off", "Boost drops the resolution while you move, which blurs motion.", Now("Radeon Boost")),
            new("enhancedSync", "Enhanced Sync", "Off", "Known to stutter. FreeSync with a frame cap is smoother."),
            new("vsync", "Wait for Vertical Refresh", VSyncMode(VSyncGameDecides)!, "Each game's own V-Sync setting decides. Fluid Motion Frames needs it off.", Now("Wait for Vertical Refresh")),
            new("fluidMotion", "Fluid Motion Frames", "Off", "Driver frame generation adds lag to every game. Turn it on only in games where you want it.", Now("Fluid Motion Frames")),
            new("rsr", "Radeon Super Resolution", "Off", "Upscales every game from a lower resolution. A game's own FSR looks better.", Now("Radeon Super Resolution")),
            new("ris", "Radeon Image Sharpening", "Off", "Sharpens every game on top of its own sharpening, which adds halos.", Now("Radeon Image Sharpening")),
        ];
    }

    /// <summary>The Intel profile as what to set in Intel Graphics Software: shown only when Exo can't reach IGCL.</summary>
    internal static IReadOnlyList<Item> Intel(Screen screen)
    {
        var cap = FrameCap(screen.RefreshHz);
        return
        [
            new("lowLatency", "Low latency mode", "On", "Fewer frames wait for the GPU: less input lag. On with boost also keeps the GPU's clock up, which costs power."),
            new("vrr", "Adaptive Sync", "On", "If the screen has it: no tearing and no V-Sync lag."),
            new("frameCap", "Frame limit", cap > 0 ? $"{cap} fps" : "Under the refresh rate",
                "With Adaptive Sync, keeps every frame inside its range. Off when the screen has none."),
            new("endurance", "Endurance Gaming", "Off", "Caps frames on battery to save power, which costs frames and responsiveness."),
            new("sharpening", "Sharpening filter", "Off", "Sharpens every game on top of its own sharpening, which adds halos."),
            new("shaders", "Download prebuilt shaders", "On", "The driver fetches Intel's compiled shaders, so games don't compile them while you play."),
        ];
    }

    [GeneratedRegex(@"\b(?:RTX|GTX)\s*(\d{2})\d{2}(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesPattern();

    [GeneratedRegex(@"\bTITAN\s+(RTX|Xp\b|X\s*\(Pascal\))", RegexOptions.IgnoreCase)]
    private static partial Regex TitanPattern();

    // Quadro RTX (Turing), RTX A-series (Ampere), RTX nnnn Ada, RTX PRO (Blackwell).
    [GeneratedRegex(@"\b(?:(?<turing>Quadro\s+RTX)|(?<ampere>RTX\s+A\d{3,4})|(?<ada>RTX\s+\d{4}\s+Ada)|(?<blackwell>RTX\s+PRO\s+\d{4}))\b", RegexOptions.IgnoreCase)]
    private static partial Regex WorkstationPattern();
}
