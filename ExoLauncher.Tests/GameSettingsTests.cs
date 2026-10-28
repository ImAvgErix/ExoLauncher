using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways best game settings can go wrong:
/// - writing changes anything in the game's file beyond the values Exo sets (its comments, ids, spacing, line breaks or BOM);
/// - a value the line doesn't list is written (a choice an update dropped, a number out of range, a misspelled choice);
/// - one of a setting's duplicate lines is missed, so the game keeps the other;
/// - turning a group off loses what the player had, or undoes a change they made in the game since;
/// - a group already set that way in the game looks like Exo's, or one Exo set looks like the player's;
/// - the render threads don't follow the processor (a hybrid's E-cores counted, a big chip given every core).
/// </summary>
public sealed class GameSettingsTests
{
    // As Modern Warfare 4 writes it (s.1.0.cod26.txt), cut down.
    private const string Mw4 = """
        1


        //
        // Display

        // Synchronizes framerate with refresh rate to prevent screen tearing issues
        VSync@0;9203;8071 = 100% // one of disabled, 100%, 50%, 33%, 25%

        NvidiaReflex@0;7717;12207 = Disabled // one of Disabled, Enabled, Enabled + boost

        // Ray tracing
        DxrMode@0;5126;8722 = On // one of Off, On

        DxrMode@0;29061;5591 = High // one of Off, Low, Medium, High, Ultra

        MotionBlur@0;11212;9876 = On // one of Off, On, Cinematic Only

        WeaponMotionBlur@0;23023;1234 = true

        RendererWorkerCount@0;3329;17262 = 16 // -1 to 16

        VideoMemoryScaleMP@0;4519;31987 = 0.750000 // 0.000000 to 2.000000

        ESSDI@0;1;1 =

        """;

    private static GameSettings.SettingsFile File(string text, string newLine = "\r\n", bool bom = false)
    {
        var body = Encoding.UTF8.GetBytes(text.ReplaceLineEndings(newLine));
        return GameSettings.Parse(bom ? [0xEF, 0xBB, 0xBF, .. body] : body);
    }

    private static GameSettings.Line Line(GameSettings.SettingsFile file, string name, int nth = 0) => file.Settings.Where(l => l.Name == name).ElementAt(nth);

    [Theory]
    [InlineData("\r\n", false)]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    public void WritingChangesOnlyTheValues(string newLine, bool bom)
    {
        var body = Encoding.UTF8.GetBytes(Mw4.ReplaceLineEndings(newLine));
        byte[] bytes = bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
        var file = GameSettings.Parse(bytes);
        Assert.Equal(bytes, GameSettings.Write(file, new Dictionary<int, string>()));

        var vsync = Line(file, "VSync");
        var written = GameSettings.Write(file, new Dictionary<int, string> { [vsync.Index] = "disabled" });
        var expected = Encoding.UTF8.GetBytes(Mw4.ReplaceLineEndings(newLine).Replace("VSync@0;9203;8071 = 100% //", "VSync@0;9203;8071 = disabled //", StringComparison.Ordinal));
        Assert.Equal(bom ? [0xEF, 0xBB, 0xBF, .. expected] : expected, written);
    }

    [Fact]
    public void LinesAreReadWithTheirIdsAndAllowedValues()
    {
        var file = File(Mw4);
        Assert.Equal(9, file.Settings.Count);
        var vsync = Line(file, "VSync");
        Assert.Equal("VSync@0;9203;8071", vsync.Id);
        Assert.Equal("100%", vsync.Value);
        Assert.Equal("one of disabled, 100%, 50%, 33%, 25%", vsync.Allowed);
        Assert.Null(Line(file, "WeaponMotionBlur").Allowed);
        Assert.Equal("", Line(file, "ESSDI").Value);
        Assert.Equal(2, file.Settings.Count(l => l.Name == "DxrMode"));
    }

    [Fact]
    public void OnlyValuesTheLineListsAreWritten()
    {
        var file = File(Mw4);
        Assert.Equal("Enabled + boost", GameSettings.Fit(Line(file, "NvidiaReflex"), "enabled + BOOST"));
        Assert.Null(GameSettings.Fit(Line(file, "NvidiaReflex"), "On"));
        Assert.Equal("7", GameSettings.Fit(Line(file, "RendererWorkerCount"), "7"));
        Assert.Null(GameSettings.Fit(Line(file, "RendererWorkerCount"), "17"));
        Assert.Null(GameSettings.Fit(Line(file, "RendererWorkerCount"), "lots"));
        Assert.Equal("0.850000", GameSettings.Fit(Line(file, "VideoMemoryScaleMP"), "0.85"));
        Assert.Equal("false", GameSettings.Fit(Line(file, "WeaponMotionBlur"), "false"));
        Assert.Null(GameSettings.Fit(Line(file, "WeaponMotionBlur"), "Off"));
        Assert.Null(GameSettings.Fit(Line(file, "ESSDI"), "x"));
    }

    [Fact]
    public void EveryDuplicateLineIsChangedAndOneThatCantTakeItIsLeftOut()
    {
        var file = File(Mw4);
        var group = new GameSettings.Group("effects", "Effects", [new("DxrMode", "Off"), new("MotionBlur", "Cinematic"), new("Nowhere", "1")]);
        var targets = GameSettings.Targets(file, group);
        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Equal("DxrMode", t.Line.Name));
    }

    [Fact]
    public void OffPutsBackWhatItHeldButNotWhatChangedInTheGameSince()
    {
        var group = new GameSettings.Group("latency", "Latency", [new("VSync", "disabled"), new("NvidiaReflex", "Enabled"), new("MotionBlur", "Off")]);
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var file = File(Mw4);

        var on = GameSettings.Plan(file, group, true, kept);
        Assert.Equal(3, on.Count);
        file = GameSettings.Parse(GameSettings.Write(file, on));
        Assert.True(GameSettings.States(new("t", "T", "", _ => [group], []), file, kept).Single() is { On: true, Exo: true });

        // In the game, Reflex is changed to boost since.
        file = GameSettings.Parse(GameSettings.Write(file, new Dictionary<int, string> { [Line(file, "NvidiaReflex").Index] = "Enabled + boost" }));
        var off = GameSettings.Plan(file, group, false, kept);
        file = GameSettings.Parse(GameSettings.Write(file, off));
        Assert.Equal("100%", Line(file, "VSync").Value);
        Assert.Equal("On", Line(file, "MotionBlur").Value);
        Assert.Equal("Enabled + boost", Line(file, "NvidiaReflex").Value);
        Assert.Empty(kept);
    }

    [Fact]
    public void AGroupAlreadySetInTheGameIsOnButNotExos()
    {
        var group = new GameSettings.Group("vsync", "V-Sync", [new("VSync", "100%")]);
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var file = File(Mw4);
        Assert.Empty(GameSettings.Plan(file, group, true, kept));
        Assert.True(GameSettings.States(new("t", "T", "", _ => [group], []), file, kept).Single() is { On: true, Exo: false });
        // Off leaves the player's own value.
        Assert.Empty(GameSettings.Plan(file, group, false, kept));
    }

    [Fact]
    public void AGroupWithNothingInThisVersionIsLeftOut()
    {
        var group = new GameSettings.Group("gone", "Gone", [new("Nowhere", "1")]);
        Assert.Empty(GameSettings.States(new("t", "T", "", _ => [group], []), File(Mw4), new Dictionary<string, GameSettings.Remembered>()));
    }

    /// <summary>Fast cores (two threads each) then slow ones; ccds L3s of the given sizes, splitting the fast cores evenly.</summary>
    private static MouseCore.Topology Cpu(int fast, int slow = 0, params long[] ccds)
    {
        MouseCore.Core[] cores =
        [.. Enumerable.Range(0, fast).Select(i => new MouseCore.Core([i * 2, i * 2 + 1], 1)),
         .. Enumerable.Range(0, slow).Select(i => new MouseCore.Core([fast * 2 + i], 0))];
        var per = ccds.Length == 0 ? fast : fast / ccds.Length;
        var l3 = ccds.Select((bytes, c) => new MouseCore.Cache(Enumerable.Range(c * per * 2, per * 2).Aggregate(0UL, (mask, cpu) => mask | 1UL << cpu), bytes)).ToArray();
        return new MouseCore.Topology(cores, l3, fast * 2 + slow);
    }

    [Theory]
    [InlineData(8, 16, 7)]  // i9 hybrid: its P-cores less one
    [InlineData(6, 8, 5)]   // i5 hybrid
    [InlineData(8, 0, 7)]   // Ryzen 7: its cores less one
    [InlineData(6, 0, 5)]
    [InlineData(4, 0, 3)]
    [InlineData(2, 0, 2)]   // never fewer than two
    public void RenderThreadsFollowTheFastCores(int fast, int slow, int expected) =>
        Assert.Equal(expected, GameSettings.RenderThreads(Cpu(fast, slow)));

    [Fact]
    public void ARyzenWithTwoCcdsCountsOne()
    {
        Assert.Equal(7, GameSettings.RenderThreads(Cpu(16, 0, 32L << 20, 32L << 20)));  // 9950X
        Assert.Equal(7, GameSettings.RenderThreads(Cpu(16, 0, 32L << 20, 96L << 20)));  // 9950X3D: the V-Cache CCD
        Assert.Equal(5, GameSettings.RenderThreads(Cpu(12, 0, 32L << 20, 32L << 20)));  // 9900X
    }

    private static GameSettings.SettingsFile Mw4File => File(Mw4);

    [Fact]
    public void NoCoresMeansNoRenderThreadsGroup()
    {
        Assert.Null(GameSettings.RenderThreads(new MouseCore.Topology([], [], 0)));
        Assert.DoesNotContain(GameSettings.CallOfDutyGroups(new("nvidia", null, 8), Mw4File), g => g.Id == "threads");
    }

    [Theory]
    [InlineData("nvidia", "NvidiaReflex")]
    [InlineData("amd", "AmdAntilag2")]
    public void LatencyUsesTheCardMakersOwnSwitch(string vendor, string key)
    {
        var latency = GameSettings.CallOfDutyGroups(new(vendor, 7, 8), Mw4File).Single(g => g.Id == "latency");
        Assert.Contains(latency.Changes, c => c.Key == key);
        Assert.Contains(latency.Changes, c => c.Key == "VSync" && c.Value == "disabled");
    }

    [Fact]
    public void IntelLeavesXeLLAsTheGameWritesIt()
    {
        // Every MW4 file holds XeLL twice, the second already on whatever the card.
        var latency = GameSettings.CallOfDutyGroups(new("intel", 7, 8), Mw4File).Single(g => g.Id == "latency");
        Assert.DoesNotContain(latency.Changes, c => c.Key == "IntelXeLL");
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070", "Auto:1920x1080", "DLSS", "CNN", "Maximum Quality")]
    [InlineData("NVIDIA GeForce RTX 2060 SUPER", "1920x1080", "DLSS", "CNN", "Maximum Quality")]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER", "Auto:2560x1440", "DLSS", "TRANSFORMER", "Balanced")]
    [InlineData("NVIDIA GeForce RTX 5090", "Auto:3840x2160", "DLSS", "TRANSFORMER", "Maximum Performance")]
    [InlineData("AMD Radeon RX 7800 XT", "Auto:2560x1440", "FSR AA", null, "Balanced")]
    [InlineData("AMD Radeon RX 9070 XT", "Auto:1920x1080", "FSR AA", null, "Maximum Quality")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", "Auto:1920x1080", "XeSS", null, "Maximum Quality")]
    public void TheUpscalerIsTheCardsOwn(string gpu, string resolution, string technique, string? model, string preset)
    {
        var group = GameSettings.Upscaler(gpu, resolution);
        Assert.NotNull(group);
        Assert.Contains(group.Changes, c => c.Key == "AATechniquePreferredMP" && c.Value == technique);
        if (model is not null) Assert.Contains(group.Changes, c => c.Key == "DLSSModelUI" && c.Value == model);
        Assert.Contains(group.Changes, c => c.Value == preset && c.Key is "DLSSPerfModeMP" or "AMDSuperResolution2Quality" or "XeSSQuality");
    }

    [Theory]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER")]
    [InlineData("AMD Radeon RX 6700 XT")]
    [InlineData("")]
    public void AnOlderCardKeepsItsOwnUpscaler(string gpu) => Assert.Null(GameSettings.Upscaler(gpu, "Auto:1920x1080"));

    [Theory]
    [InlineData(4, "3", "0.7")]
    [InlineData(8, "2", "0.7")]
    [InlineData(12, "1", "0.8")]
    [InlineData(24, "1", "0.8")]
    public void TexturesAndMemoryFollowTheCardsMemory(double gb, string textures, string scale)
    {
        var group = GameSettings.Memory(gb);
        Assert.Contains(group.Changes, c => c.Key == "TextureQuality" && c.Value == textures);
        Assert.Contains(group.Changes, c => c.Key == "VideoMemoryScaleMP" && c.Value == scale);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", true)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", true)]
    [InlineData("NVIDIA GeForce RTX 3080", true)]
    [InlineData("AMD Radeon RX 7900 XTX", true)]
    [InlineData("AMD Radeon RX 9070 XT", true)]
    [InlineData("NVIDIA GeForce RTX 3070", false)]
    [InlineData("NVIDIA GeForce RTX 4060 Ti", false)]
    [InlineData("AMD Radeon RX 7600", false)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", false)]
    public void AStrongCardKeepsDetailAtMedium(string gpu, bool strong)
    {
        var file = File(Mw4 + $"GPUName@0;1;1 = {gpu}\nModelQuality@0;2;2 = High Quality // one of Low Quality, Medium Quality, High Quality\n");
        var effects = GameSettings.CallOfDutyGroups(new("nvidia", 7, 12), file).Single(g => g.Id == "effects");
        Assert.Contains(effects.Changes, c => c.Key == "ModelQuality" && c.Value == (strong ? "Medium Quality" : "Low Quality"));
    }

    [Fact]
    public void AnOlderCardKeepsItsRenderResolution()
    {
        // No upscaler Exo sets on a GTX, so its render resolution stays the player's.
        var file = File(Mw4 + "GPUName@0;1;1 = NVIDIA GeForce GTX 1660 SUPER\nResolutionMultiplier@0;3;3 = 85 // 0 to 200\n");
        Assert.DoesNotContain(GameSettings.CallOfDutyGroups(new("nvidia", 5, 6), file).SelectMany(g => g.Changes), c => c.Key == "ResolutionMultiplier");
        Assert.Contains(GameSettings.Upscaler("NVIDIA GeForce RTX 3060", "1920x1080")!.Changes, c => c.Key == "ResolutionMultiplier" && c.Value == "100");
    }

    [Fact]
    public void BorderlessAtTheHighestRefresh()
    {
        var display = GameSettings.Display(new("nvidia", 7, 8), "144.000");
        Assert.Contains(display.Changes, c => c.Key == "DisplayMode" && c.Value == "Fullscreen borderless window");
        Assert.Contains(display.Changes, c => c.Key == "RefreshRate" && c.Value == "Auto:144.000" && c.Free);
        Assert.DoesNotContain(display.Changes, c => c.Key == "ConstrainMouse");
        // Already Auto (the highest): left as the game wrote it.
        Assert.DoesNotContain(GameSettings.Display(new("nvidia", 7, 8), "Auto:164.917").Changes, c => c.Key == "RefreshRate");
        // A second screen keeps the mouse in the game.
        Assert.Contains(GameSettings.Display(new("nvidia", 7, 8, Screens: 2), "Auto:164.917").Changes, c => c.Key == "ConstrainMouse" && c.Value == "true");
    }

    [Fact]
    public void AFreeValueGoesOnlyToALineThatListsNone()
    {
        var file = File(Mw4 + "RefreshRate@0;4;4 = 144.000\nVSync@0;5;5 = 100% // one of disabled, 100%\n");
        var group = new GameSettings.Group("g", "G", [new("RefreshRate", "Auto:144.000", Free: true), new("VSync", "anything", Free: true)]);
        var targets = GameSettings.Targets(file, group);
        Assert.Single(targets);
        Assert.Equal("Auto:144.000", targets[0].Value);
    }

    [Theory]
    [InlineData(true, 165, "162")]
    [InlineData(true, 360, "300")]  // the game caps at 300 at most
    [InlineData(false, 165, null)]  // no variable refresh: uncapped or the player's own cap
    [InlineData(true, 60, null)]
    public void VariableRefreshCapsJustUnderTheTopRate(bool vrr, int top, string? cap)
    {
        var pacing = GameSettings.Pacing(new("nvidia", 7, 8, TopRefresh: top, Vrr: vrr));
        if (cap is null) Assert.Null(pacing);
        else Assert.Contains(pacing!.Changes, c => c.Key == "MaxFpsInGame" && c.Value == cap);
    }

    [Fact]
    public void FootstepsOverMusicAndTheUpscalersSharpening()
    {
        var file = File(Mw4 + "MusicVolume@0;6;6 = 1.000000 // 0.000000 to 1.000000\n");
        var audio = GameSettings.CallOfDutyGroups(new("nvidia", 7, 8), file).Single(g => g.Id == "audio");
        Assert.Equal("0.000000", GameSettings.Targets(file, audio).Single().Value);
        Assert.Contains(GameSettings.Upscaler("NVIDIA GeForce RTX 3070", "1920x1080")!.Changes, c => c.Key == "DLSSSharpnessMP" && c.Value == "0.75");
    }

    [Theory]
    [InlineData("Auto:1920x1080", "120")]
    [InlineData("Auto:2560x1440", "120")]
    [InlineData("Auto:3440x1440", "110")]  // ultrawide: the game already widens it
    [InlineData(null, "120")]
    public void TheWidestViewForTheScreen(string? resolution, string fov) =>
        Assert.Contains(GameSettings.View(resolution).Changes, c => c.Key == "Fov" && c.Value == fov);

    // A player's file as MW4 writes it: names with a space are its own, and are never taken for another.
    private const string Player = """
        1

        // 1st Person Field of View
        Fov@1;65095;45350 = 90.000000 // 60.000000 to 120.000000

        // 1st Person Field of View (Handheld)
        Handheld Fov@0;24892;5108 = 80.000000 // 60.000000 to 120.000000

        MusicVolume@0;54363;21371 = 1.000000 // 0.000000 to 1.000000

        """;

    [Fact]
    public void APlayerFilesLinesAreKeptApartFromTheMainFiles()
    {
        var main = File(Mw4 + "MusicVolume@0;6;6 = 1.000000 // 0.000000 to 1.000000\n");
        var player = File(Player);
        Assert.DoesNotContain(player.Settings, l => l.Name == "Handheld");
        var group = new GameSettings.Group("audio", "Audio", [new("MusicVolume", "0"), new("Fov", "120")]);
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var mainEdits = GameSettings.Plan(main, group, true, kept);
        var playerEdits = GameSettings.Plan(player, group, true, kept, "123/g.cod26.1.0.l.txt|");
        Assert.Single(mainEdits);
        Assert.Equal(2, playerEdits.Count);
        Assert.Contains("123/g.cod26.1.0.l.txt|Fov@1;65095;45350", kept.Keys);
        Assert.Equal(3, kept.Count);

        // Applied only once both files hold it.
        GameSettings.OpenFile Open(string key, GameSettings.SettingsFile f) => new("", key, f);
        var profile = new GameSettings.Profile("t", "T", "", _ => [group], []);
        Assert.False(GameSettings.States(profile, [Open("", GameSettings.Parse(GameSettings.Write(main, mainEdits))), Open("123/g.cod26.1.0.l.txt|", player)], kept).Single().On);
        Assert.True(GameSettings.States(profile, [Open("", GameSettings.Parse(GameSettings.Write(main, mainEdits))),
            Open("123/g.cod26.1.0.l.txt|", GameSettings.Parse(GameSettings.Write(player, playerEdits)))], kept).Single() is { On: true, Exo: true });
    }


    [Fact]
    public void RawMouseInputAndYourSensitivityStays()
    {
        var mouse = GameSettings.CallOfDutyGroups(new("nvidia", 7, 8), File(Mw4)).Single(g => g.Id == "mouse");
        Assert.Contains(mouse.Changes, c => c.Key == "MouseAcceleration" && c.Value == "0");
        Assert.Contains(mouse.Changes, c => c.Key == "ADSTimingSensitivity" && c.Value == "interpolated");
        // Sensitivity, ADS multipliers and invert are muscle memory: never part of it.
        Assert.DoesNotContain(GameSettings.CallOfDutyGroups(new("nvidia", 7, 8, Headphones: true), File(Mw4)).SelectMany(g => g.Changes),
            c => c.Key.Contains("Sensibility", StringComparison.Ordinal) || c.Key is "ADSSensitivity" || c.Key.Contains("ZoomSensitivity", StringComparison.Ordinal)
                 || c.Key.Contains("Multiplier", StringComparison.Ordinal) || c.Key.Contains("Invert", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHeadphonesMixOnlyForHeadphones()
    {
        var file = File(Mw4 + "AudioMixPreset@0;7;7 = home_theater\nWindowsSonicEnable@0;8;8 = true\n");
        Assert.DoesNotContain(GameSettings.CallOfDutyGroups(new("nvidia", 7, 8), file), g => g.Id == "headphones");
        var headphones = GameSettings.CallOfDutyGroups(new("nvidia", 7, 8, Headphones: true), file).Single(g => g.Id == "headphones");
        var targets = GameSettings.Targets(file, headphones);
        Assert.Contains(targets, t => t.Line.Name == "AudioMixPreset" && t.Value == "headphones_rex");
        Assert.Contains(targets, t => t.Line.Name == "WindowsSonicEnable" && t.Value == "false");
    }

}
