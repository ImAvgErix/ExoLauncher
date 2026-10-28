using System.Globalization;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// Call of Duty: Modern Warfare 4 (Call of Duty HQ's cod26). Its graphics settings are one text file for the PC,
/// %LOCALAPPDATA%\Activision\Call of Duty\players\s.1.0.cod26.txt, written by the game's own menu. Exo's values are where
/// the guides, benchmarks, shared configs and creators agree (October 2026: PC Gamer's per-setting tests, HDOpti, Hone,
/// metka.gg, cod-mw4.wiki, Dot Esports, esports.gg, Tier1Settings, MurkTweaks', ItalianTweaks' and RivalsXO's configs),
/// set for this PC: the card's own latency switch with V-Sync and frame generation off; render threads for the
/// processor; the full render resolution with the card's own upscaler (DLSS's CNN model on an RTX 20 or 30, as PC Gamer
/// measured, its preset by the resolution the game outputs); no blur of any kind; textures and the video memory budget
/// for the card's memory; and the costly effects at their lowest (a strong card keeps detail and terrain at Medium,
/// where lower turns trees blocky; an older card without DLSS, FSR 4 or XeSS keeps its own upscaler and render
/// resolution). Borderless on the screen's highest refresh (true full screen gains nothing on DX12 under Windows 11 and
/// can hide the game from Exo Capture; borderless runs at the desktop's rate, which PC › Display sets to the top one),
/// the mouse kept in the game with a second screen, and on a variable-refresh screen a cap 3 frames under its top rate
/// so it never leaves that range. Music and cutscenes muted and effects and hit markers full, so footsteps carry; the
/// upscaler's sharpening at the middle the guides agree on; no intros. Left as the player has them: resolution, field
/// of view, voice and master volume, keys, and the hidden values only one config family sets with no measurement behind
/// them (subdivision, sun-shadow clipmap, upload heaps, corpses).
/// </summary>
internal static partial class GameSettings
{
    internal static string CallOfDutyPlayers =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Activision", "Call of Duty", "players");

    /// <summary>
    /// Call of Duty's running marker: __cod in the install folder, holding the process id of the cod.exe that made it.
    /// The game deletes it as it quits; one left behind (a crash, a forced close) makes the next start ask to run in safe
    /// mode, which resets its settings. Before Exo starts the game, a marker whose process is gone is deleted, so it
    /// starts as it was set. Returns whether one was.
    /// </summary>
    internal static bool ClearLeftoverMarker(Models.GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path)) return false;
        var marker = Path.Combine(game.Path, "__cod");
        if (!File.Exists(marker)) return false;
        var bytes = File.ReadAllBytes(marker);
        if (bytes.Length >= 4 && BitConverter.ToInt32(bytes, 0) is var pid and > 0)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                if (process.ProcessName.Equals("cod", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (ArgumentException) { /* no such process: the session it marks is over */ }
        }
        File.Delete(marker);
        return true;
    }

    [GeneratedRegex(@"^(cod|sp|mp)\d{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex CallOfDutyTitle();

    /// <summary>
    /// Call of Duty's own shader caches: shadercache in each title's folder of the install (Steam's Call of Duty HQ\cod26,
    /// Battle.net's Call of Duty\_retail_\cod26). Deleting them is the fix for colors or textures gone wrong after a game
    /// or driver update; the game builds them again as it starts. Null for a game that isn't Call of Duty; a cache that is
    /// a link to elsewhere is never listed.
    /// </summary>
    internal static IReadOnlyList<string>? ShaderCaches(Models.GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path) || !game.Title.Contains("Call of Duty", StringComparison.OrdinalIgnoreCase)) return null;
        var titles = new[] { game.Path, Path.Combine(game.Path, "_retail_") }.Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories).Where(folder => CallOfDutyTitle().IsMatch(Path.GetFileName(folder))).ToList();
        if (titles.Count == 0) return null;
        return [.. titles.Select(folder => Path.Combine(folder, "shadercache"))
            .Where(cache => new DirectoryInfo(cache) is { Exists: true } info && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))];
    }

    internal static long ShaderCacheBytes(IEnumerable<string> caches) => caches.Sum(cache => new DirectoryInfo(cache)
        .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
        .Sum(file => file.Length));

    internal static void ClearShaderCaches(IEnumerable<string> caches)
    {
        foreach (var cache in caches) Directory.Delete(cache, recursive: true);
    }

    internal static Profile ModernWarfare4(Pc pc) =>
        new("mw4", "Modern Warfare 4", Path.Combine(CallOfDutyPlayers, "s.1.0.cod26.txt"), file => CallOfDutyGroups(pc, file), ["cod"],
            () => PlayerFiles("cod26"));

    /// <summary>
    /// Each account's own settings beside the PC's: players\{account}\g.{game}.1.0.l.txt and g.{game}.pm.1.0.l.txt, where
    /// the field of view and the audio live. The game reads these text files (tried on MW4: values written there came
    /// back from the game as it loaded and saved), though it keeps binary .b files beside them.
    /// </summary>
    internal static IReadOnlyList<string> PlayerFiles(string game)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(CallOfDutyPlayers)
                .SelectMany(account => new[] { Path.Combine(account, $"g.{game}.1.0.l.txt"), Path.Combine(account, $"g.{game}.pm.1.0.l.txt") })
                .Where(File.Exists)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The first line named <paramref name="name"/>'s value, if the file has one.</summary>
    internal static string? ValueOf(SettingsFile file, string name) => file.Settings.FirstOrDefault(line => line.Name == name)?.Value;

    [GeneratedRegex(@"RTX\s*(?<series>[2-9])0[5-9]0", RegexOptions.IgnoreCase)]
    private static partial Regex GeForceRtx();

    [GeneratedRegex(@"Radeon\s+RX\s+(?<series>[5-9])\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonRx();

    [GeneratedRegex(@"(?<width>\d{3,5})\s*x\s*(?<height>\d{3,5})")]
    private static partial Regex Size();

    internal static IReadOnlyList<Group> CallOfDutyGroups(Pc pc, SettingsFile file)
    {
        Change[] noWaiting = [new("VSync", "disabled"), new("DLSSFrameGeneration", "false"), new("FSRFrameInterpolation", "false"), new("IntelXeFG", "false")];
        var latency = pc.Vendor switch
        {
            "nvidia" => new Group("latency", "Reflex on, V-Sync and frame generation off", [new("NvidiaReflex", "Enabled"), .. noWaiting]),
            "amd" => new Group("latency", "Anti-Lag 2 on, V-Sync and frame generation off", [new("AmdAntilag2", "true"), .. noWaiting]),
            _ => new Group("latency", "V-Sync and frame generation off", noWaiting),
        };
        List<Group> groups = [latency, Display(pc, ValueOf(file, "RefreshRate"))];
        if (Pacing(pc) is { } pacing) groups.Add(pacing);
        if (pc.RenderThreads is { } threads)
            groups.Add(new("threads", $"{threads} render threads", [new("RendererWorkerCount", threads.ToString(CultureInfo.InvariantCulture))]));
        var gpu = ValueOf(file, "GPUName");
        groups.Add(new("picture", "No blur, steady resolution",
        [
            new("DynamicSceneResolution", "false"), new("NVIDIAImageScalingMP", "false"),
            new("MotionBlur", "Off"), new("WeaponMotionBlur", "false"), new("EnableVelocityBasedBlur", "false"),
            new("DofEnable", "Off"), new("DofWeaponDisable", "true"), new("DofWorldDisable", "true"), new("DepthOfFieldQuality", "Low"),
        ]));
        if (Upscaler(gpu, ValueOf(file, "Resolution")) is { } upscaler) groups.Add(upscaler);
        if (pc.VideoMemoryGb is { } memory) groups.Add(Memory(memory));
        groups.Add(new("clutter", "Blood, shell casings and lingering damage off",
            [new("ShowBlood", "false"), new("BloodLimit", "true"), new("ShowBrass", "false"), new("PersistentDamageLayer", "false")]));
        groups.Add(new("effects", "Ray tracing and costly effects lowest",
        [
            new("DxrMode", "Off"), new("SSRQuality", "Off"), new("ScreenSpaceShadowQuality", "Off"), new("AmbientLightingQuality", "Off"),
            new("ShadowQuality", "Very_Low"), new("ShadowFilteringQuality", "Low"), new("ShadingQuality", "Low"), new("ShaderQuality", "Low"),
            // A strong card keeps detail and terrain at Medium: below that trees turn blocky, and it has the headroom.
            new("ModelQuality", Strong(gpu) ? "Medium Quality" : "Low Quality"), new("TerrainQuality", Strong(gpu) ? "Medium" : "Very Low"),
            new("ParticleQuality", "very low"), new("VRS", "true"),
            new("VolumetricQuality", "QUALITY_LOW"), new("WeatherGridVolumesQuality", "Off"), new("WaterCausticsMode", "Off"),
            new("WaterWaveWetness", "false"), new("DeferredPhysics", "Low Quality"), new("Tessellation", "0_Off"),
            new("ReflectionProbeHalfResolution", "true"), new("WorldStreamingQuality", "Low"), new("CinematicQuality", "true"),
            // Shaders keep preloading in the background: off (as two configs set it) means stutter while they compile.
            new("EnableOfflineGameUpdater", "true"),
        ]));
        // In the PC's file and each player's: the game takes the player's and copies it over the PC's.
        groups.Add(new("audio", "Footsteps over music", [
            new("MusicVolume", "0"), new("CinematicVolume", "0"), new("EffectsVolume", "1"), new("HitMarkersVolume", "1"),
            new("AltShellShock", "true"), new("MonoSound", "false")]));
        groups.Add(View(ValueOf(file, "Resolution")));
        // The mouse as it moves, raw: no acceleration, filtering or smoothing; the ADS multipliers eased in, not snapped,
        // so hip-fire comes straight back after aiming; and no delay between scroll-wheel weapon switches.
        groups.Add(new("mouse", "Raw mouse input", [
            new("MouseAcceleration", "0"), new("MouseFilter", "0"), new("MouseSmoothing", "false"),
            new("ADSTimingSensitivity", "interpolated"), new("WeaponCycleDelay", "0")]));
        if (pc.Headphones)
            // MW4's headphones mix (its own spelling), stereo, and no Windows Sonic layered on it: the mix places sounds itself.
            groups.Add(new("headphones", "Mixed for headphones", [
                new("AudioMixPreset", "headphones_rex", Free: true), new("AudioWantedChannelsNumber", "2"), new("WindowsSonicEnable", "false")]));
        groups.Add(new("idle", "No quality drop when idle, no intros", [
            new("SustainabilityReduceQualityIdle", "off"), new("SustainabilityPauseRendering", "false"), new("SustainabilityPauseRendering", "0"),
            new("SkipIntro", "true"), new("ViewedSplashScreen", "true"), new("SkipSeasonVideo", "true"), new("SkipSeasonIntroVideo", "true")]));
        return groups;
    }

    /// <summary>
    /// The widest view, set for the screen: 120 on a 16:9 or 16:10 screen (what most competitive players and guides run),
    /// 110 on an ultrawide, which the game already widens; aiming scales with it ("Affected"), and the third-person view
    /// at its widest.
    /// </summary>
    internal static Group View(string? resolution)
    {
        var wide = resolution is not null && Size().Match(resolution) is { Success: true } size &&
            double.Parse(size.Groups["width"].Value, CultureInfo.InvariantCulture) / double.Parse(size.Groups["height"].Value, CultureInfo.InvariantCulture) > 2;
        return new("view", "Widest field of view for your screen",
            [new("Fov", wide ? "110" : "120"), new("ADSFovScaling", "true"), new("ThirdPersonFov", "90")]);
    }

    /// <summary>
    /// Borderless full screen, the menu's own choice for both lines, on the screen's highest refresh: a refresh rate the
    /// player fixed lower is set back to the game's Auto (its own spelling, "Auto:" and the rate it wrote, which it then
    /// reads as the highest). With a second screen the mouse stays in the game, so a click near the edge isn't lost to it.
    /// </summary>
    internal static Group Display(Pc pc, string? refresh)
    {
        List<Change> changes = [new("DisplayMode", "Fullscreen borderless window"), new("PreferredDisplayMode", "Fullscreen borderless window"),
            new("FocusedMode", "false")];
        if (refresh is { Length: > 0 } && !refresh.StartsWith("Auto", StringComparison.OrdinalIgnoreCase))
            changes.Add(new("RefreshRate", "Auto:" + refresh, Free: true));
        if (pc.Screens > 1) changes.Add(new("ConstrainMouse", "true"));
        return new("display", "Borderless at the highest refresh", changes);
    }

    /// <summary>
    /// On a variable-refresh screen (G-Sync, or adaptive sync as the driver reports it), frames capped 3 under its top
    /// rate so they stay in that range with V-Sync off; the game caps at 300 at most. Null without it: an uncapped game
    /// or the player's own cap is right there.
    /// </summary>
    internal static Group? Pacing(Pc pc) => pc.Vrr && pc.TopRefresh > 60
        ? new("pacing", "Frames held just under your refresh", [new("CapFps", "true"),
            new("MaxFpsInGame", Math.Min(300, pc.TopRefresh - 3).ToString(CultureInfo.InvariantCulture)), new("MaxFpsOutOfFocus", "30")])
        : null;

    /// <summary>
    /// The card's own upscaler at the preset its output suits, by the card the game found: DLSS on a GeForce RTX (the
    /// CNN model on an RTX 20 or 30, which PC Gamer measured 9% faster than the transformer with no visible loss;
    /// the transformer on a 40 or 50), FSR on a Radeon RX 7000 or 9000, XeSS on Intel Arc. Maximum Quality at 1080p,
    /// Balanced at 1440p, Maximum Performance at 4K, with the render resolution at 100% so the upscaler, not the
    /// multiplier, lowers it. Null for any other card: an older one keeps its own upscaler and render resolution.
    /// </summary>
    internal static Group? Upscaler(string? gpu, string? resolution)
    {
        if (string.IsNullOrEmpty(gpu)) return null;
        var height = resolution is not null && Size().Match(resolution) is { Success: true } size ? int.Parse(size.Groups["height"].Value, CultureInfo.InvariantCulture) : 1080;
        var preset = height >= 2000 ? "Maximum Performance" : height >= 1300 ? "Balanced" : "Maximum Quality";
        if (GeForceRtx().Match(gpu) is { Success: true } rtx)
            return new("upscaler", "DLSS for your card", [
                new("ResolutionMultiplier", "100"), new("DLSSSharpnessMP", "0.75"), new("AATechniquePreferredMP", "DLSS"), new("DLSSModeMP", "DLSS"), new("DLSSPerfModeMP", preset),
                new("DLSSModelUI", rtx.Groups["series"].Value is "2" or "3" ? "CNN" : "TRANSFORMER"),
            ]);
        if (RadeonRx().Match(gpu) is { Success: true } radeon && radeon.Groups["series"].Value is "7" or "9")
            return new("upscaler", "FSR for your card", [new("ResolutionMultiplier", "100"), new("AATechniquePreferredMP", "FSR AA"),
                new("AMDSuperResolution2Quality", preset), new("AMDContrastAdaptiveSharpeningStrength", "0.75")]);
        if (gpu.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            return new("upscaler", "XeSS for your card", [new("ResolutionMultiplier", "100"), new("AATechniquePreferredMP", "XeSS"), new("XeSSQuality", preset)]);
        return null;
    }

    /// <summary>
    /// Textures and the share of video memory the game may fill, by the card's memory: the game's own budget is set
    /// lower so it doesn't thrash (and crash on 8 GB), and textures go as high as that leaves room for (3 is the lowest,
    /// which flattens the map and hurts spotting; 0, the highest, drops the 1% lows even on big cards).
    /// </summary>
    internal static Group Memory(double gb) => gb switch
    {
        <= 4.5 => Textures("3", "0.7"),
        <= 8.5 => Textures("2", "0.7"),
        < 10 => Textures("2", "0.75"),
        _ => Textures("1", "0.8"),
    };

    // 16x filtering keeps angled ground sharp and measured free (BenchmarKing).
    private static Group Textures(string quality, string memory) => new("memory", "Textures and video memory for your card",
        [new("TextureQuality", quality), new("VideoMemoryScaleMP", memory), new("TextureFilter", "aniso 16x")]);

    [GeneratedRegex(@"RTX\s*(30[89]0|40[789]0|50[789]0)|RX\s*(79\d0|9070)", RegexOptions.IgnoreCase)]
    private static partial Regex StrongCard();

    /// <summary>
    /// A card with headroom at competitive settings: GeForce RTX 3080 and up, 4070 and up, 5070 and up; Radeon RX 7900
    /// and 9070. Exo lets it keep detail and terrain at Medium, where weaker cards go lowest for frames.
    /// </summary>
    internal static bool Strong(string? gpu) => gpu is not null && StrongCard().IsMatch(gpu);
}
