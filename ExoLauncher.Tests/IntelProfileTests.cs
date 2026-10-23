using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using static ExoLauncher.Helpers.IntelIgcl;
using Keys = ExoLauncher.Helpers.IntelIgcl.Keys;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Intel's profile could go wrong silently. None of it can run here (this PC has no Intel GPU), so the IGCL layouts
/// are pinned to igcl_api.h as MSVC x64 lays it out, and the profile runs against a fake driver:
/// - a structure's size or a field's offset differs from the header, so the driver reads Exo's request as garbage or
///   refuses it for its size (ctlInit, ctlGetDeviceProperties, ctlGetSupported3DCapabilities, ctlGetSet3DFeature);
/// - a value is read from or written to the wrong member of the value union, or a write drops what Exo didn't mean to
///   change (the frame limit's fps when it is turned off, Endurance Gaming's mode, Adaptive Sync's balance);
/// - a value type the driver reports that Exo can't handle is written anyway;
/// - the main Intel GPU's driver version or name reads wrong, so "Driver changed some settings" never shows;
/// - the profile writes a setting the driver lacks, a frame cap without a screen that can run Adaptive Sync, a cap
///   outside the driver's range, or Low latency with boost the user didn't pick;
/// - a setting the driver refuses fails the whole profile, or leaves it "changed" forever;
/// - Undo can't get back to the start: the backup is taken after writing, a re-apply overwrites it, or it holds a
///   launch's temporary switch;
/// - a game's XeSS frame generation is not set before it starts, not put back once no game runs, put back while another
///   game still runs, or never put back after Exo stops mid-game;
/// - a PC without an Intel GPU, or with IGCL not starting, gets a write.
/// </summary>
public sealed class IntelProfileTests : IDisposable
{
    private readonly string _folder = Path.Combine(TestDataDirectory.Root, "intel-" + Guid.NewGuid().ToString("N"));
    private static readonly GpuProfile.Screen Vrr = new("ASUS VG27AQ", 165, true);
    private static readonly GpuProfile.Screen Fixed = new("DELL P2419H", 60, false);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    // ---- IGCL layouts (igcl_api.h, intel/drivers.gpu.control-library master, compiled with MSVC x64) ----

    [Theory]
    [InlineData(typeof(InitArgs), 36)]
    [InlineData(typeof(Property), 8)]
    [InlineData(typeof(FeatureGetSet), 56)]
    [InlineData(typeof(PropertyInfo), 24)]
    [InlineData(typeof(FeatureDetails), 72)]
    [InlineData(typeof(FeatureCaps), 24)]
    [InlineData(typeof(EnduranceGaming), 8)]
    [InlineData(typeof(AdaptiveSyncValue), 8)]
    [InlineData(typeof(DeviceProperties), 320)]
    public void StructuresAreTheHeadersSize(Type type, int size) => Assert.Equal(size, Marshal.SizeOf(type));

    [Theory]
    [InlineData(typeof(InitArgs), nameof(InitArgs.AppVersion), 8)]
    [InlineData(typeof(InitArgs), nameof(InitArgs.Flags), 12)]
    [InlineData(typeof(InitArgs), nameof(InitArgs.SupportedVersion), 16)]
    [InlineData(typeof(InitArgs), nameof(InitArgs.ApplicationUid), 20)]
    [InlineData(typeof(Property), nameof(Property.EnableType), 0)]
    [InlineData(typeof(Property), nameof(Property.Value), 4)]
    [InlineData(typeof(Property), nameof(Property.FloatValue), 4)]
    [InlineData(typeof(FeatureGetSet), nameof(FeatureGetSet.Version), 4)]
    [InlineData(typeof(FeatureGetSet), nameof(FeatureGetSet.FeatureType), 8)]
    [InlineData(typeof(FeatureGetSet), nameof(FeatureGetSet.ApplicationName), 16)]
    [InlineData(typeof(FeatureGetSet), nameof(FeatureGetSet.ApplicationNameLength), 24)]
    [InlineData(typeof(FeatureGetSet), nameof(FeatureGetSet.Set), 25)]
    [InlineData(typeof(FeatureGetSet), nameof(FeatureGetSet.ValueType), 28)]
    [InlineData(typeof(FeatureGetSet), nameof(FeatureGetSet.Value), 32)]
    [InlineData(typeof(FeatureGetSet), nameof(FeatureGetSet.CustomValueSize), 40)]
    [InlineData(typeof(FeatureGetSet), nameof(FeatureGetSet.CustomValue), 48)]
    [InlineData(typeof(PropertyInfo), nameof(PropertyInfo.SupportedTypes), 0)]
    [InlineData(typeof(PropertyInfo), nameof(PropertyInfo.Min), 4)]
    [InlineData(typeof(PropertyInfo), nameof(PropertyInfo.Max), 8)]
    [InlineData(typeof(FeatureDetails), nameof(FeatureDetails.ValueType), 4)]
    [InlineData(typeof(FeatureDetails), nameof(FeatureDetails.Value), 8)]
    [InlineData(typeof(FeatureDetails), nameof(FeatureDetails.CustomValueSize), 32)]
    [InlineData(typeof(FeatureDetails), nameof(FeatureDetails.CustomValue), 40)]
    [InlineData(typeof(FeatureDetails), nameof(FeatureDetails.PerAppSupport), 48)]
    [InlineData(typeof(FeatureDetails), nameof(FeatureDetails.ConflictingFeatures), 56)]
    [InlineData(typeof(FeatureDetails), nameof(FeatureDetails.FeatureMiscSupport), 64)]
    [InlineData(typeof(FeatureDetails), nameof(FeatureDetails.Reserved2), 70)]
    [InlineData(typeof(FeatureCaps), nameof(FeatureCaps.NumSupportedFeatures), 8)]
    [InlineData(typeof(FeatureCaps), nameof(FeatureCaps.Details), 16)]
    [InlineData(typeof(EnduranceGaming), nameof(EnduranceGaming.Mode), 4)]
    [InlineData(typeof(AdaptiveSyncValue), nameof(AdaptiveSyncValue.AdaptiveBalance), 1)]
    [InlineData(typeof(AdaptiveSyncValue), nameof(AdaptiveSyncValue.AllowAsyncForHighFps), 2)]
    [InlineData(typeof(AdaptiveSyncValue), nameof(AdaptiveSyncValue.AdaptiveBalanceStrength), 4)]
    [InlineData(typeof(DeviceProperties), nameof(DeviceProperties.DeviceId), 8)]
    [InlineData(typeof(DeviceProperties), nameof(DeviceProperties.DeviceIdSize), 16)]
    [InlineData(typeof(DeviceProperties), nameof(DeviceProperties.DriverVersion), 32)]
    [InlineData(typeof(DeviceProperties), nameof(DeviceProperties.Name), 88)]
    [InlineData(typeof(DeviceProperties), nameof(DeviceProperties.AdapterFlags), 188)]
    public void FieldsSitWhereTheHeaderPutsThem(Type type, string field, int offset) => Assert.Equal(offset, (int)Marshal.OffsetOf(type, field));

    [Fact]
    public void EnumsAndVersionAreTheHeaders()
    {
        Assert.Equal(0x10001u, ApiVersion);   // CTL_IMPL_VERSION 1.1
        Assert.Equal((1, 2, 7, 10, 16, 17, 18), (FeatureEndurance, FeatureFrameLimit, FeatureSharpening, FeatureAdaptiveSync, FeatureLowLatency, FeatureFrameGen, FeatureShaderDownload));
        Assert.Equal((0, 1, 2, 3, 4, 5), (TypeBool, TypeFloat, TypeInt32, TypeUInt32, TypeEnum, TypeCustom));
        Assert.Equal((200, 48, 56), (PropertiesSize, PropertiesActiveFlags, PropertiesSupportedFeatures));
        Assert.Equal(8, CustomSize(Keys.Endurance));
        Assert.Equal(8, CustomSize(Keys.AdaptiveSync));
        Assert.Equal(0, CustomSize(Keys.LowLatency));
    }

    // ---- Values in and out of ctlGetSet3DFeature ----

    private static FeatureGetSet Got(int valueType, uint enableType = 0, byte enable = 0, int value = 0)
    {
        var got = new FeatureGetSet { ValueType = valueType, FeatureType = 99 };
        if (valueType == TypeEnum) got.Value.EnableType = enableType;
        else got.Value = got.Value with { Enable = enable, Value = value };
        return got;
    }

    [Fact]
    public void LowLatencyIsTheEnumAndBoostIsTwo()
    {
        var got = Got(TypeEnum, enableType: 1);
        Assert.Equal(1, Decode(Keys.LowLatency, TypeEnum, got, []));
        Assert.True(Encode(Keys.LowLatency, TypeEnum, GpuProfile.LowLatencyValue(GpuProfile.LowLatencyBoost), ref got, []));
        Assert.Equal(2u, got.Value.EnableType);
        Assert.Equal(1, got.Set);
        Assert.Equal(99, got.FeatureType);
        Assert.Equal((0, 1, 2), (GpuProfile.LowLatencyValue("off"), GpuProfile.LowLatencyValue("on"), GpuProfile.LowLatencyValue("boost")));
    }

    [Fact]
    public void FrameLimitReadsItsFpsAndTurningItOffKeepsThem()
    {
        Assert.Equal(60, Decode(Keys.FrameLimit, TypeInt32, Got(TypeInt32, enable: 1, value: 60), []));
        Assert.Equal(0, Decode(Keys.FrameLimit, TypeInt32, Got(TypeInt32, enable: 0, value: 60), []));
        var got = Got(TypeInt32, enable: 0, value: 60);
        Assert.True(Encode(Keys.FrameLimit, TypeInt32, 161, ref got, []));
        Assert.Equal(((byte)1, 161), (got.Value.Enable, got.Value.Value));
        Assert.True(Encode(Keys.FrameLimit, TypeInt32, 0, ref got, []));
        Assert.Equal(((byte)0, 161), (got.Value.Enable, got.Value.Value));
    }

    [Theory]
    [InlineData(TypeBool)]
    [InlineData(TypeInt32)]
    [InlineData(TypeUInt32)]
    [InlineData(TypeFloat)]
    public void ASwitchIsItsEnableWhateverTheDriverCallsIt(int valueType)
    {
        // The header calls sharpening "integer" and also gives it an off/on enum: the driver's answer decides.
        var got = Got(valueType, enable: 1, value: 7);
        Assert.Equal(1, Decode(Keys.Sharpening, valueType, got, []));
        Assert.True(Encode(Keys.Sharpening, valueType, 0, ref got, []));
        Assert.Equal(0, got.Value.Enable);
        Assert.Equal(7, got.Value.Value);   // a strength stays as it was
        Assert.Equal(0, Decode(Keys.Sharpening, valueType, got, []));
    }

    [Fact]
    public void EnduranceGamingKeepsItsModeAndAdaptiveSyncItsBalance()
    {
        var custom = new byte[16];
        MemoryMarshal.Write(custom, new EnduranceGaming { Control = 2, Mode = 1 });
        var got = Got(TypeCustom);
        Assert.Equal(2, Decode(Keys.Endurance, TypeCustom, got, custom));
        Assert.True(Encode(Keys.Endurance, TypeCustom, 0, ref got, custom));
        var written = MemoryMarshal.Read<EnduranceGaming>(custom);
        Assert.Equal((0, 1), (written.Control, written.Mode));

        custom = new byte[16];
        MemoryMarshal.Write(custom, new AdaptiveSyncValue { On = 0, AdaptiveBalance = 1, AllowAsyncForHighFps = 1, AdaptiveBalanceStrength = 0.5f });
        Assert.Equal(0, Decode(Keys.AdaptiveSync, TypeCustom, got, custom));
        Assert.True(Encode(Keys.AdaptiveSync, TypeCustom, 1, ref got, custom));
        var sync = MemoryMarshal.Read<AdaptiveSyncValue>(custom);
        Assert.Equal(((byte)1, (byte)1, (byte)1, 0.5f), (sync.On, sync.AdaptiveBalance, sync.AllowAsyncForHighFps, sync.AdaptiveBalanceStrength));
    }

    [Fact]
    public void AValueTypeExoCantHandleIsNeitherReadNorWritten()
    {
        var got = Got(TypeCustom);
        Assert.Null(Decode(Keys.LowLatency, TypeCustom, got, new byte[16]));
        Assert.False(Encode(Keys.LowLatency, TypeCustom, 1, ref got, new byte[16]));
        Assert.Equal(0, got.Set);
        Assert.Null(Decode(Keys.ShaderDownload, 9, got, []));
        Assert.False(Encode(Keys.ShaderDownload, 9, 1, ref got, []));
        Assert.False(Encode(Keys.LowLatency, TypeEnum, -1, ref got, []));
        // A custom value too short to hold the header's struct is not touched.
        Assert.Null(Decode(Keys.Endurance, TypeCustom, got, new byte[4]));
    }

    [Fact]
    public void CapabilitiesGiveEachFeaturesTypeAndAnIntsRange()
    {
        var details = new FeatureDetails[]
        {
            new() { FeatureType = FeatureFrameLimit, ValueType = TypeInt32, Value = new PropertyInfo { Min = 30, Max = 300 } },
            new() { FeatureType = FeatureLowLatency, ValueType = TypeEnum, Value = new PropertyInfo { SupportedTypes = 0b111 } },
            new() { FeatureType = FeatureLowLatency, ValueType = TypeBool },
        };
        var listed = Listed(details);
        Assert.Equal(new Feature(TypeInt32, 30, 300), listed[FeatureFrameLimit]);
        Assert.Equal(new Feature(TypeEnum, 0, 0), listed[FeatureLowLatency]);   // the first entry wins; no range for an enum
    }

    [Theory]
    [InlineData(0x0020_0000_0065_22A0ul, "32.0.101.8864")]
    [InlineData(0x001F_0000_0065_1194ul, "31.0.101.4500")]
    [InlineData(0ul, null)]
    public void DriverVersionReadsAsWindowsWritesIt(ulong version, string? text) => Assert.Equal(text, DriverVersion(version));

    [Theory]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", "Arc B580")]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "Iris Xe")]
    [InlineData("Intel(R) UHD Graphics 770", "UHD Graphics 770")]
    [InlineData("Intel(R) Arc(TM) Graphics", "Arc")]
    [InlineData("Intel(R) Graphics", "Graphics")]
    [InlineData("  ", null)]
    public void GpuNamesAreShort(string name, string? expected) => Assert.Equal(expected, ShortName(name));

    // ---- Exo's Intel profile ----

    /// <summary>Intel's driver as IGCL shows it: the settings it has, what each set did, and sets it refuses (then, as IgclDriver does, the setting drops out).</summary>
    private sealed class FakeDriver : IGpuDriver
    {
        internal Dictionary<string, int> Values { get; } = new(StringComparer.Ordinal);
        internal string? Version { get; set; } = "32.0.101.8864";
        internal bool Down { get; set; }
        internal HashSet<string> Refuses { get; } = [];
        internal List<KeyValuePair<string, int>> Written { get; } = [];

        public GpuDriverState? Read() => Down ? null : new GpuDriverState("Arc B580", Version, new Dictionary<string, int>(Values), 30, 300);

        public string? Write(IReadOnlyList<KeyValuePair<string, int>> writes)
        {
            if (Down) return "Intel's driver did not answer.";
            foreach (var write in writes)
            {
                if (!Values.ContainsKey(write.Key)) continue;
                if (Refuses.Contains(write.Key)) { Values.Remove(write.Key); continue; }
                Values[write.Key] = write.Value;
                Written.Add(write);
            }
            return null;
        }

        /// <summary>An Arc on a current driver with every setting Exo touches, set the way a player might have them.</summary>
        internal static FakeDriver Current() => new()
        {
            Values =
            {
                [Keys.Endurance] = 2, [Keys.Sharpening] = 1, [Keys.LowLatency] = 0, [Keys.AdaptiveSync] = 0,
                [Keys.FrameLimit] = 0, [Keys.ShaderDownload] = 0, [Keys.FrameGen] = 0,
            },
        };
    }

    private sealed class Pc(bool intel = true)
    {
        internal bool Intel { get; set; } = intel;
        internal bool Playing { get; set; }
    }

    private IntelProfileService Service(FakeDriver driver, Pc? pc = null)
    {
        pc ??= new Pc();
        return new IntelProfileService(driver, _folder, () => pc.Intel, () => pc.Playing);
    }

    [Fact]
    public void ProfileWritesInOrderAndOnlyWhatTheDriverHas()
    {
        var writes = GpuProfile.IntelWrites(FakeDriver.Current().Read()!, Vrr.Vrr, Vrr.RefreshHz, GpuProfile.LowLatencyOn);
        Assert.Equal(new[] { Keys.Endurance, Keys.Sharpening, Keys.LowLatency, Keys.AdaptiveSync, Keys.FrameLimit, Keys.ShaderDownload }, writes.Select(write => write.Key));
        var values = writes.ToDictionary();
        Assert.Equal(0, values[Keys.Endurance]);
        Assert.Equal(0, values[Keys.Sharpening]);
        Assert.Equal(1, values[Keys.LowLatency]);      // On, not boost
        Assert.Equal(1, values[Keys.AdaptiveSync]);
        Assert.Equal(GpuProfile.FrameCap(165), values[Keys.FrameLimit]);
        Assert.Equal(1, values[Keys.ShaderDownload]);
        Assert.DoesNotContain(Keys.FrameGen, values.Keys);   // the player's, per game

        // An older driver with low latency and the frame limit only.
        var old = new GpuDriverState("Iris Xe", null, new Dictionary<string, int> { [Keys.LowLatency] = 0, [Keys.FrameLimit] = 60 });
        Assert.Equal(new[] { Keys.LowLatency, Keys.FrameLimit }, GpuProfile.IntelWrites(old, true, 144, GpuProfile.LowLatencyBoost).Select(write => write.Key));
        Assert.Equal(2, GpuProfile.IntelWrites(old, true, 144, GpuProfile.LowLatencyBoost).ToDictionary()[Keys.LowLatency]);
    }

    [Fact]
    public void FrameLimitOnlyWithAdaptiveSyncAndInsideTheDriversRange()
    {
        var state = FakeDriver.Current().Read()!;
        Assert.Equal(0, GpuProfile.IntelWrites(state, Fixed.Vrr, Fixed.RefreshHz, GpuProfile.LowLatencyOn).ToDictionary()[Keys.FrameLimit]);
        // 30-300 here, so a 500 Hz screen's cap is clamped.
        Assert.Equal(300, GpuProfile.IntelWrites(state, true, 500, GpuProfile.LowLatencyOn).ToDictionary()[Keys.FrameLimit]);
        // No range from the driver: the cap as it is.
        var noRange = state with { FpsMin = 0, FpsMax = 0 };
        Assert.Equal(GpuProfile.FrameCap(500), GpuProfile.IntelWrites(noRange, true, 500, GpuProfile.LowLatencyOn).ToDictionary()[Keys.FrameLimit]);
    }

    [Fact]
    public void SummarySaysWhatTheProfileDoes()
    {
        var writes = GpuProfile.IntelWrites(FakeDriver.Current().Read()!, true, 165, GpuProfile.LowLatencyBoost);
        Assert.Equal("Adaptive Sync on, frames capped at 157 fps, Low latency with boost, Intel's prebuilt shaders; Endurance Gaming and sharpening off",
            GpuProfile.IntelSummary(writes, GpuProfile.LowLatencyBoost));
        var noCap = GpuProfile.IntelWrites(FakeDriver.Current().Read()!, false, 60, GpuProfile.LowLatencyOn);
        Assert.DoesNotContain("capped", GpuProfile.IntelSummary(noCap, GpuProfile.LowLatencyOn));
    }

    [Theory]
    [InlineData("game", 0)]
    [InlineData("2x", 1)]
    [InlineData("3x", 2)]
    [InlineData("4x", 3)]
    [InlineData("8x", null)]
    [InlineData(null, null)]
    public void FrameGenChoicesAreTheDriversOverride(string? choice, int? value)
    {
        Assert.Equal(value, GpuProfile.FrameGenValue(choice));
        if (value is { } known) Assert.Equal(choice, GpuProfile.FrameGenChoice(known));
    }

    [Fact]
    public void ApplyBacksUpFirstAndUndoPutsBackEveryValue()
    {
        var driver = FakeDriver.Current();
        var start = new Dictionary<string, int>(driver.Values);
        var intel = Service(driver);
        Assert.Equal("ready", intel.Read(Vrr)!.Status);

        var applied = intel.Apply(Vrr, GpuProfile.LowLatencyOn)!;
        Assert.True(applied.Ok);
        Assert.Equal("applied", applied.Status);
        Assert.True(applied.CanUndo);
        Assert.True(File.Exists(Path.Combine(_folder, "intel-profile.json")));

        // Re-apply with boost keeps the first backup.
        var boost = intel.Apply(Vrr, GpuProfile.LowLatencyBoost)!;
        Assert.Equal("applied", boost.Status);
        Assert.Equal(GpuProfile.LowLatencyBoost, boost.LowLatency);
        Assert.Equal(2, driver.Values[Keys.LowLatency]);

        var undone = intel.Undo(Vrr)!;
        Assert.True(undone.Ok);
        Assert.False(undone.CanUndo);
        Assert.Equal(start, driver.Values);
        Assert.False(File.Exists(Path.Combine(_folder, "intel-profile.json")));
        Assert.Equal("Nothing to undo.", intel.Undo(Vrr)!.Message);
    }

    [Fact]
    public void ASettingTheDriverRefusesDropsOutInsteadOfFailingTheProfile()
    {
        var driver = FakeDriver.Current();
        driver.Refuses.Add(Keys.Endurance);
        var intel = Service(driver);
        var applied = intel.Apply(Vrr, GpuProfile.LowLatencyOn)!;
        Assert.True(applied.Ok);
        Assert.Equal("applied", applied.Status);
        Assert.DoesNotContain("Endurance", applied.Summary);
        Assert.Equal(1, driver.Values[Keys.AdaptiveSync]);   // written after the refusal
    }

    [Fact]
    public void StatusComparesWithTheDriverAndNamesADriverUpdate()
    {
        var driver = FakeDriver.Current();
        var intel = Service(driver);
        intel.Apply(Vrr, GpuProfile.LowLatencyOn);
        driver.Values[Keys.Sharpening] = 1;
        var moved = intel.Read(Vrr)!;
        Assert.Equal("changed", moved.Status);
        Assert.False(moved.DriverUpdated);
        driver.Version = "32.0.101.9000";
        Assert.True(intel.Read(Vrr)!.DriverUpdated);
    }

    [Fact]
    public void TheFrameLimitFollowsANewRefreshRateAndNothingElse()
    {
        var driver = FakeDriver.Current();
        var intel = Service(driver);
        intel.Apply(Vrr, GpuProfile.LowLatencyOn);
        intel.FollowRefresh(Vrr with { RefreshHz = 240 });
        Assert.Equal(GpuProfile.FrameCap(240), driver.Values[Keys.FrameLimit]);

        // Something else moved too: that is a change for Re-apply, not for the follower.
        driver.Values[Keys.Sharpening] = 1;
        intel.FollowRefresh(Vrr with { RefreshHz = 144 });
        Assert.Equal(GpuProfile.FrameCap(240), driver.Values[Keys.FrameLimit]);
    }

    [Fact]
    public void FrameGenerationSwitchesAtLaunchAndBackOnceNoGameRuns()
    {
        var driver = FakeDriver.Current();
        var pc = new Pc();
        var intel = Service(driver, pc);
        Assert.Equal((true, "game"), intel.FrameGen("steam:1"));
        Assert.True(intel.SetFrameGen("steam:1", "3x"));
        Assert.False(intel.SetFrameGen("steam:1", "8x"));
        Assert.Equal((true, "3x"), intel.FrameGen("steam:1"));
        Assert.Equal((true, "game"), intel.FrameGen("steam:2"));

        pc.Playing = true;
        intel.GameLaunching("steam:2", "Left to itself");
        Assert.Equal(0, driver.Values[Keys.FrameGen]);
        intel.GameLaunching("steam:1", "3x");
        Assert.Equal(2, driver.Values[Keys.FrameGen]);
        // A running game's switch is not a change to the profile.
        intel.Apply(Vrr, GpuProfile.LowLatencyOn);
        Assert.Equal("applied", intel.Read(Vrr)!.Status);

        // Another game still runs: nothing goes back yet.
        intel.GameEnded();
        Assert.Equal(2, driver.Values[Keys.FrameGen]);
        pc.Playing = false;
        intel.GameEnded();
        Assert.Equal(0, driver.Values[Keys.FrameGen]);

        // Back to the game's own choice: no switch at launch.
        Assert.True(intel.SetFrameGen("steam:1", "game"));
        driver.Written.Clear();
        intel.GameLaunching("steam:1", "Game");
        Assert.Empty(driver.Written);
    }

    [Fact]
    public void ALaunchSwitchIsPutBackAfterExoStopsMidGame()
    {
        var driver = FakeDriver.Current();
        var pc = new Pc { Playing = true };
        var intel = Service(driver, pc);
        intel.SetFrameGen("steam:1", "4x");
        intel.GameLaunching("steam:1", "Game");
        Assert.Equal(3, driver.Values[Keys.FrameGen]);

        pc.Playing = false;
        Service(driver, pc).GameEnded();
        Assert.Equal(0, driver.Values[Keys.FrameGen]);
    }

    [Fact]
    public void NoIntelGpuOrNoIgclMeansNoWrite()
    {
        var driver = FakeDriver.Current();
        var pc = new Pc(intel: false);
        var intel = Service(driver, pc);
        intel.SetFrameGen("steam:1", "2x");
        intel.GameLaunching("steam:1", "Game");
        Assert.Empty(driver.Written);
        Assert.False(intel.FrameGen("steam:1").Supported);

        driver.Down = true;
        pc.Intel = true;
        Assert.Null(intel.Read(Vrr));
        Assert.Null(intel.Apply(Vrr, GpuProfile.LowLatencyOn));
        Assert.False(File.Exists(Path.Combine(_folder, "intel-profile.json")));

        // A driver without the frame generation override: the game's row doesn't show and nothing is switched.
        var older = FakeDriver.Current();
        older.Values.Remove(Keys.FrameGen);
        var noFrameGen = new IntelProfileService(older, _folder + "-old", () => true, () => true);
        Assert.False(noFrameGen.FrameGen("steam:1").Supported);
    }
}
