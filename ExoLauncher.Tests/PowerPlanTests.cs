using ExoLauncher.Services;
using Class = ExoLauncher.Services.PowerProfiles.CpuClass;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo's power plan could go wrong silently:
/// - a processor lands in the wrong class (a dual-CCD X3D or a Zen 5 9900X/9950X gets parking off and AMD's driver
///   can't park its second CCD in games; an Intel hybrid gets its E-cores held awake; a 12th gen without E-cores is
///   called hybrid);
/// - a profile writes a value the spec doesn't (idle states off, sleep off on a laptop, battery values on a laptop) or
///   leaves one out; X3D or Zen 5 dual-CCD keeps min cores 100 or min state 100; an Intel hybrid's P-core values go to
///   the E-cores' class (efficiency class 1 is the P-cores, class 0 the E-cores);
/// - Apply edits Balanced itself instead of a copy, loses the plan it replaced, makes a second Exo plan each time, or
///   leaves Undo after Windows refused the switch;
/// - Auto switches a custom plan, switches again after Undo or after the user picked another plan, or doesn't make the
///   plan again when it was deleted;
/// - Undo puts back the plan but not Balanced's power mode.
/// </summary>
public sealed class PowerPlanTests : IDisposable
{
    private static readonly Guid Nexus = new("77777777-7777-7777-7777-777777777777");
    private readonly string _dir = Directory.CreateTempSubdirectory("exo-power-").FullName;
    private string StatePath => Path.Combine(_dir, "exo-power-plan.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- Classes ----

    [Theory]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen 5 5600X 6-Core Processor", null, "amd-single-ccd")] // this PC
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen 5 7600X 6-Core Processor", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen 7 7700X 8-Core Processor", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen 5 9600X 6-Core Processor", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen 7 9700X 8-Core Processor", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen 9 3900X 12-Core Processor", null, "amd-dual-ccd")]
    [InlineData("AMD Ryzen 9 3950X 16-Core Processor", null, "amd-dual-ccd")]
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor", null, "amd-dual-ccd")]
    [InlineData("AMD Ryzen 9 5950X 16-Core Processor", null, "amd-dual-ccd")]
    [InlineData("AMD Ryzen 9 5900XT 16-Core Processor", null, "amd-dual-ccd")]
    [InlineData("AMD Ryzen 9 7900 12-Core Processor", null, "amd-dual-ccd")]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", null, "amd-dual-ccd")]
    [InlineData("AMD Ryzen 9 9900X 12-Core Processor", null, "amd-dual-ccd-zen5")]
    [InlineData("AMD Ryzen 9 9950X 16-Core Processor", null, "amd-dual-ccd-zen5")]
    [InlineData("AMD Ryzen 9 7945HX with Radeon Graphics", null, "amd-dual-ccd")]
    [InlineData("AMD Ryzen 9 9850HX with Radeon Graphics", null, "amd-dual-ccd")]
    [InlineData("AMD Ryzen 9 9955HX with Radeon Graphics", null, "amd-dual-ccd")]
    [InlineData("AMD RYZEN AI MAX+ 395 w/ Radeon 8060S", null, "amd-dual-ccd")]
    [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen 7 8700G w/ Radeon 780M Graphics", null, "amd-single-ccd")]
    [InlineData("AMD Ryzen Threadripper 3970X 32-Core Processor", null, "amd-dual-ccd")]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", null, "amd-dual-ccd-x3d")]
    [InlineData("AMD Ryzen 9 7900X3D 12-Core Processor", null, "amd-dual-ccd-x3d")]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", null, "amd-dual-ccd-x3d")]
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor", null, "amd-dual-ccd-x3d")]
    [InlineData("AMD Ryzen 9 9955HX3D with Radeon Graphics", null, "amd-dual-ccd-x3d")]
    [InlineData("AMD Ryzen 9 7945HX3D with Radeon Graphics", null, "amd-dual-ccd-x3d")]
    [InlineData("Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", false, "intel-non-hybrid")]
    [InlineData("11th Gen Intel(R) Core(TM) i9-11900K @ 3.50GHz", null, "intel-non-hybrid")]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12400F", false, "intel-non-hybrid")] // no E-cores, as CPUID says
    [InlineData("12th Gen Intel(R) Core(TM) i5-12400F", null, "intel-non-hybrid")]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", true, "intel-hybrid")]
    [InlineData("13th Gen Intel(R) Core(TM) i5-13600K", null, "intel-hybrid")]
    [InlineData("Intel(R) Core(TM) i7-14700K", null, "intel-hybrid")]
    [InlineData("Intel(R) Core(TM) Ultra 7 265K", null, "intel-hybrid")]
    [InlineData("Intel(R) Core(TM) Ultra 9 285H", true, "intel-hybrid")]
    [InlineData("AMD Athlon Gold 3150U with Radeon Graphics", null, "unknown")]
    [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", null, "unknown")]
    [InlineData(null, null, "unknown")]
    public void ClassesFollowTheProcessor(string? name, bool? hybrid, string expected) =>
        Assert.Equal(expected, PowerProfiles.Keys[PcHealth.ClassOf(name, hybrid)]);

    [Theory]
    [InlineData("AMD Ryzen 5 5600X 6-Core Processor", "Ryzen 5 5600X")]
    [InlineData("AMD Ryzen 9 7945HX3D with Radeon Graphics", "Ryzen 9 7945HX3D")]
    [InlineData("Intel(R) Core(TM) i7-14700K", "Core i7-14700K")]
    [InlineData("11th Gen Intel(R) Core(TM) i9-11900K @ 3.50GHz", "Core i9-11900K")]
    [InlineData("Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", "Core i7-9700K")]
    [InlineData("Intel(R) Core(TM) Ultra 7 265K", "Core Ultra 7 265K")]
    [InlineData("  ", null)]
    public void TheButtonNamesTheProcessorAsPeopleSayIt(string name, string? expected) =>
        Assert.Equal(expected, PcHealth.ShortCpu(name));

    [Theory]
    [InlineData(false, (byte)3, false)] // desktop
    [InlineData(false, (byte)35, false)] // mini PC
    [InlineData(false, null, false)]
    [InlineData(true, (byte)3, true)] // a battery decides
    [InlineData(false, (byte)10, true)] // notebook
    [InlineData(false, (byte)31, true)] // convertible
    public void LaptopsByBatteryOrChassis(bool battery, byte? chassis, bool laptop) =>
        Assert.Equal(laptop, PcHealth.Laptop(battery, chassis));

    [Fact]
    public void ReadsTheChassisFromSmbios()
    {
        byte[] enclosure = [3, 0x15, 0, 0, 0, 0x83, .. new byte[0x0F], 0, 0]; // type 3, chassis 3 with the lock bit
        Assert.Equal((byte)3, PcHealth.ChassisType([0, 3, 3, 0, .. BitConverter.GetBytes((uint)enclosure.Length), .. enclosure]));
        Assert.Null(PcHealth.ChassisType([1, 2, 3]));
    }

    // ---- Profiles ----

    private static Dictionary<string, uint> Values(PowerProfiles.Profile profile) => profile.Values.ToDictionary(v => v.Name, v => v.Ac);

    [Theory]
    [InlineData("amd-single-ccd")]
    [InlineData("amd-dual-ccd")]
    [InlineData("intel-non-hybrid")]
    [InlineData("unknown")]
    public void TheBaselineIsTheMeasuredLatencySet(string key)
    {
        var values = Values(PowerProfiles.For(PowerProfiles.Keys.Single(k => k.Value == key).Key, laptop: false));
        Assert.Equal(new Dictionary<string, uint>
        {
            ["Minimum processor state"] = 100,
            ["Energy performance preference"] = 0,
            ["Boost mode"] = 2,
            ["Boost policy"] = 100,
            ["Core parking min cores"] = 100,
            ["Processor idle disable"] = 0,
            ["PCIe Link State Power Management"] = 0,
            ["USB selective suspend"] = 0,
            ["USB 3 Link Power Management"] = 0,
            ["NVMe NOPPME"] = 0,
            ["Primary NVMe Idle Timeout"] = 0,
            ["Primary NVMe Power State Transition Latency Tolerance"] = 0,
            ["Secondary NVMe Idle Timeout"] = 0,
            ["Secondary NVMe Power State Transition Latency Tolerance"] = 0,
            ["AHCI Link Power Management - HIPM/DIPM"] = 0,
            ["AHCI Link Power Management - Adaptive"] = 0,
            ["Turn off hard disk after"] = 0,
            ["Turn off display after"] = 0,
            ["Sleep after"] = 0,
            ["Hibernate after"] = 0,
        }, values);
    }

    [Fact]
    public void ClassesWithTheSameSettingsShareOneProfile()
    {
        var single = PowerProfiles.For(Class.AmdSingleCcd, false).Values;
        Assert.Equal(single, PowerProfiles.For(Class.AmdDualCcd, false).Values);
        Assert.Equal(single, PowerProfiles.For(Class.IntelNonHybrid, false).Values);
        Assert.Equal(single, PowerProfiles.For(Class.Unknown, false).Values); // the safe general profile on a desktop
        Assert.Equal("amd-single-ccd", PowerProfiles.For(Class.AmdSingleCcd, false).Key);
    }

    [Theory]
    [InlineData("amd-dual-ccd-x3d")]
    [InlineData("amd-dual-ccd-zen5")]
    public void ADualCcdThatAmdParksKeepsBalancedsParkingAndMinimumState(string key)
    {
        var cpu = PowerProfiles.Keys.Single(k => k.Value == key).Key;
        var values = Values(PowerProfiles.For(cpu, laptop: false));
        Assert.DoesNotContain("Core parking min cores", values.Keys);
        Assert.DoesNotContain("Minimum processor state", values.Keys);
        Assert.Equal(10u, values["Energy performance preference"]);
        Assert.Equal(0u, values["PCIe Link State Power Management"]);
        Assert.Equal(PowerProfiles.For(Class.AmdDualCcdX3d, false).Values, PowerProfiles.For(cpu, false).Values);
    }

    [Fact]
    public void IntelHybridTunesThePcoresClassAndLeavesEcoresToThreadDirector()
    {
        var profile = PowerProfiles.For(Class.IntelHybrid, laptop: false);
        var values = profile.Values.ToDictionary(v => v.Setting, v => v.Ac);
        // Efficiency class 1 (the ...64/...84 settings) is the P-cores: EPP 0, never parked.
        Assert.Equal(0u, values[new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864")]);
        Assert.Equal(100u, values[new Guid("0cc5b647-c1df-4637-891a-dec35c318584")]);
        // Class 0 (the E-cores) keeps Balanced's EPP and parking, and Thread Director's policies stay.
        Assert.DoesNotContain(new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), values.Keys);
        Assert.DoesNotContain(new Guid("0cc5b647-c1df-4637-891a-dec35c318583"), values.Keys);
        Assert.DoesNotContain(profile.Values, v => v.Name.Contains("heterogeneous", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0u, values[new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226")]); // USB selective suspend
    }

    [Fact]
    public void NoProfileTurnsIdleStatesOffOrHoldsThemShallow()
    {
        var idleDisable = new Guid("5d76a2ca-e8c0-402f-a133-2158492d58ad");
        foreach (var cpu in Enum.GetValues<Class>())
            foreach (var laptop in new[] { false, true })
            {
                var values = PowerProfiles.For(cpu, laptop).Values;
                Assert.All(values.Where(v => v.Setting == idleDisable), v => Assert.Equal(0u, v.Ac));
                // Idle promote/demote, autonomous mode, the plan's personality and the check interval stay Balanced's.
                Assert.DoesNotContain(values, v => v.Setting == new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c") || v.Setting == new Guid("4b92d758-5a24-4851-a470-815d78aee119")
                    || v.Setting == new Guid("8baa4a8a-14c6-4451-8e8b-14bdbd197537") || v.Setting == new Guid("245d8541-3943-4422-b025-13a784f679b7")
                    || v.Setting == new Guid("4d2b0152-7d5c-498b-88e2-34345392a2c5"));
            }
    }

    [Theory]
    [InlineData(true, true, "V-Cache")]
    [InlineData(true, false, "one half")]
    [InlineData(false, false, null)]
    public void AParkingDualCcdIsHeldBackByAPlanThatKeepsEveryCoreAwake(bool parked, bool x3d, string? says)
    {
        var nexus = new PowerPlan.State(Nexus, "Nexus LiteOS Power Plan", Guid.Empty, false, false, false, new PowerPlan.Limits(100, 2, 100));
        var why = PcHealth.HeldBack(nexus, parked, x3d);
        if (says is null) Assert.Null(why);
        else Assert.Contains(says, why);
    }

    [Fact]
    public void ALaptopGetsItsProfileOnAcOnlyAndKeepsSleep()
    {
        var laptop = PowerProfiles.For(Class.AmdSingleCcd, laptop: true);
        Assert.DoesNotContain(laptop.Values, v => v.Dc);
        Assert.DoesNotContain(laptop.Values, v => v.Subgroup == PowerProfiles.Sleep);
        Assert.Empty(PowerProfiles.For(Class.Unknown, laptop: true).Values); // Balanced as it is
        // A desktop never sleeps by itself, on a UPS too.
        Assert.All(PowerProfiles.For(Class.AmdSingleCcd, laptop: false).Values.Where(v => v.Subgroup == PowerProfiles.Sleep), v => Assert.True(v.Dc));
        // Every plan keeps the screen on: a desktop on battery too, a laptop on AC only.
        Assert.Contains(laptop.Values, v => v.Subgroup == PowerProfiles.Display && v.Ac == 0 && !v.Dc);
        foreach (var cpu in PowerProfiles.Keys.Keys.Where(c => c != Class.Unknown))
            Assert.Contains(PowerProfiles.For(cpu, laptop: false).Values, v => v.Subgroup == PowerProfiles.Display && v.Ac == 0 && v.Dc);
    }

    // ---- Exo's plan, on a fake Windows ----

    private sealed class FakePower : PcHealth.IPowerApi
    {
        public Guid ActiveScheme;
        public Guid Mode;
        public bool OnBattery;
        public bool RefuseSwitch;
        public readonly Dictionary<Guid, string> Plans = new() { [PowerPlan.Balanced] = "Balanced", [PowerPlan.HighPerformance] = "High performance", [Nexus] = "Nexus LiteOS Power Plan" };
        public readonly Dictionary<(Guid Scheme, Guid Setting, bool Dc), uint> Written = [];
        public readonly HashSet<Guid> Shown = [];
        public readonly List<string> Calls = [];

        public FakePower(Guid active, Guid mode = default) { ActiveScheme = active; Mode = mode; }

        public Guid? Active() => ActiveScheme;
        public Guid Overlay() => Mode;
        public bool Battery() => OnBattery;
        public string? Name(Guid scheme) => Plans.GetValueOrDefault(scheme);
        public uint? AcValue(Guid scheme, Guid subgroup, Guid setting) => Written.TryGetValue((scheme, setting, false), out var value) ? value : null;

        public bool SetActive(Guid scheme)
        {
            Calls.Add($"plan {Plans.GetValueOrDefault(scheme, scheme.ToString())}");
            if (RefuseSwitch || !Plans.ContainsKey(scheme)) return false;
            ActiveScheme = scheme;
            return true;
        }

        public bool SetOverlay(Guid overlay)
        {
            Calls.Add($"mode {overlay} on {Plans.GetValueOrDefault(ActiveScheme)}");
            Mode = overlay;
            return true;
        }

        public Guid? Duplicate(Guid source)
        {
            Calls.Add($"copy {Plans[source]}");
            var made = Guid.NewGuid();
            Plans[made] = Plans[source] + " (copy)";
            return made;
        }

        public bool Rename(Guid scheme, string name, string description)
        {
            Plans[scheme] = name;
            return true;
        }

        public bool Write(Guid scheme, Guid subgroup, Guid setting, uint value, bool dc)
        {
            Written[(scheme, setting, dc)] = value;
            return true;
        }

        public bool Show(Guid subgroup, Guid setting) => Shown.Add(setting);

        public Guid? Exo => Plans.FirstOrDefault(p => p.Value == PowerPlan.PlanName).Key is var key && key != Guid.Empty ? key : null;
    }

    private static readonly PowerProfiles.Profile ThisPc = PowerProfiles.For(Class.AmdSingleCcd, laptop: false);

    [Fact]
    public void UseExosPlanCopiesBalancedWritesTheProfileAndUndoPutsNexusBack()
    {
        var windows = new FakePower(Nexus);
        var plan = new PowerPlan(windows, StatePath);
        Assert.Null(plan.Apply(ThisPc));

        var exo = windows.Exo!.Value;
        Assert.Equal(["copy Balanced", "plan Exo"], windows.Calls);
        Assert.Equal(exo, windows.ActiveScheme);
        // Every value went into Exo's copy, none into Balanced; each is shown in Control Panel.
        Assert.All(ThisPc.Values, v => Assert.Equal(v.Ac, windows.Written[(exo, v.Setting, false)]));
        Assert.DoesNotContain(windows.Written.Keys, key => key.Scheme == PowerPlan.Balanced);
        Assert.Equal(ThisPc.Values.Select(v => v.Setting).ToHashSet(), windows.Shown);
        Assert.Equal(0u, windows.Written[(exo, new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), true)]); // desktop: no sleep on battery either
        Assert.True(plan.Read()!.CanUndo);

        windows.Calls.Clear();
        Assert.Null(plan.Undo());
        Assert.Equal(["plan Nexus LiteOS Power Plan"], windows.Calls);
        Assert.False(plan.Read()!.CanUndo);
        // Never switched again by itself.
        Assert.Null(plan.Auto(ThisPc));
        Assert.Equal(Nexus, windows.ActiveScheme);
    }

    [Fact]
    public void AutoSwitchesWindowsOwnPlansOnlyAndOnce()
    {
        var custom = new FakePower(Nexus);
        Assert.Null(new PowerPlan(custom, StatePath).Auto(ThisPc));
        Assert.Empty(custom.Calls);

        var stock = new FakePower(PowerPlan.HighPerformance);
        var plan = new PowerPlan(stock, Path.Combine(_dir, "stock.json"));
        Assert.Null(plan.Auto(ThisPc));
        Assert.Equal(stock.Exo, stock.ActiveScheme);

        // The user picks High performance again in Windows: their choice, kept.
        stock.ActiveScheme = PowerPlan.HighPerformance;
        Assert.False(plan.Read()!.CanUndo);
        stock.Calls.Clear();
        Assert.Null(plan.Auto(ThisPc));
        Assert.Empty(stock.Calls);
    }

    [Fact]
    public void AutoWithExosPlanOnWritesItAgainWithoutASecondCopy()
    {
        var windows = new FakePower(PowerPlan.Balanced);
        var plan = new PowerPlan(windows, StatePath);
        plan.Auto(ThisPc);
        windows.Calls.Clear();
        windows.Written.Clear();
        Assert.Null(plan.Auto(ThisPc));
        Assert.DoesNotContain("copy Balanced", windows.Calls);
        Assert.Equal(ThisPc.Values.Count + ThisPc.Values.Count(v => v.Dc), windows.Written.Count);
        Assert.Single(windows.Plans, p => p.Value == PowerPlan.PlanName);
    }

    [Fact]
    public void ADeletedExoPlanIsMadeAgain()
    {
        var windows = new FakePower(PowerPlan.Balanced);
        var plan = new PowerPlan(windows, StatePath);
        plan.Auto(ThisPc);
        var first = windows.Exo!.Value;
        // Deleted while in use: Windows falls back to Balanced.
        windows.Plans.Remove(first);
        windows.ActiveScheme = PowerPlan.Balanced;
        Assert.Null(plan.Auto(ThisPc));
        var again = windows.Exo!.Value;
        Assert.NotEqual(first, again);
        Assert.Equal(again, windows.ActiveScheme);
        // And Use Exo's plan makes it again too.
        windows.Plans.Remove(again);
        windows.ActiveScheme = Nexus;
        Assert.Null(plan.Apply(ThisPc));
        Assert.Equal(windows.Exo, windows.ActiveScheme);
    }

    [Fact]
    public void ApplyingTwiceKeepsThePlanItFirstReplaced()
    {
        var windows = new FakePower(Nexus);
        var plan = new PowerPlan(windows, StatePath);
        plan.Apply(ThisPc);
        Assert.Null(plan.Apply(ThisPc));
        plan.Undo();
        Assert.Equal(Nexus, windows.ActiveScheme);
    }

    [Fact]
    public void ARefusedSwitchLeavesNoUndo()
    {
        var windows = new FakePower(Nexus) { RefuseSwitch = true };
        var plan = new PowerPlan(windows, StatePath);
        Assert.Equal("Windows didn't switch to Exo's plan.", plan.Apply(ThisPc));
        Assert.Equal(Nexus, windows.ActiveScheme);
        Assert.Equal("There's nothing to undo.", plan.Undo());
    }

    [Fact]
    public void UndoPutsBalancedsPowerModeBack()
    {
        var windows = new FakePower(PowerPlan.Balanced, PowerPlan.BestEfficiency);
        var plan = new PowerPlan(windows, StatePath);
        plan.Apply(ThisPc);
        windows.Mode = Guid.Empty; // a plan other than Balanced has no power mode
        windows.Calls.Clear();
        Assert.Null(plan.Undo());
        // The plan first: the power mode applies only while Balanced is on.
        Assert.Equal(["plan Balanced", $"mode {PowerPlan.BestEfficiency} on Balanced"], windows.Calls);
    }

    [Fact]
    public void ReadsTheActivePlansOwnSettings()
    {
        var windows = new FakePower(Nexus);
        windows.Written[(Nexus, new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), false)] = 100;
        windows.Written[(Nexus, new Guid("0cc5b647-c1df-4637-891a-dec35c318583"), false)] = 100;
        windows.Written[(PowerPlan.Balanced, new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), false)] = 80;
        var state = new PowerPlan(windows, StatePath).Read()!;
        Assert.Equal(new PowerPlan.Limits(100, null, 100), state.Cpu);
        Assert.False(state.Stock);
        Assert.False(state.Exo);
    }
}
