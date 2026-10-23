using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways System > This PC could go wrong silently:
/// - SMBIOS is misread (an empty slot counted, the strings skipped wrongly so the part number is another
///   module's, an extended speed ignored, a cut-off table throwing), so RAM speed is wrong;
/// - a kit running EXPO/XMP, or one tuned by hand, is told to turn EXPO/XMP on; a laptop's SODIMM is told to;
/// - a part number's rating is misread (Kingston's and Crucial's hundreds, Patriot's tens);
/// - Resizable BAR is called off on an integrated GPU, a card that can't have it, or a Windows that can't say;
/// - the chipset version compares as text (8.10 before 8.9), or AMD's page with several links gives an older one;
/// - Game Mode is called off when the value is missing (Windows has it on);
/// - the power plan row offers nothing on a custom plan, or nags on Windows' own plan (Exo switches those itself),
///   or loses Undo after Use Exo's plan;
/// - a plan that holds the processor back (Power saver, a capped maximum state, boost off, Best power efficiency) isn't
///   said to, or a plan tuned for speed (this PC's Nexus) is called a problem;
/// - a dual-CCD X3D with parking off isn't told that it stops AMD's cache placement, or a single-CCD X3D is;
/// - the button names another processor than this PC's.
/// Exo's plan itself (classes, profiles, Apply, Undo, Auto): PowerPlanTests.
/// </summary>
public sealed class PcHealthTests
{
    private const string ThisPcCpu = "AMD Ryzen 5 5600X 6-Core Processor";
    private static readonly Guid Nexus = new("77777777-7777-7777-7777-777777777777");

    // ---- SMBIOS ----

    private static byte[] Type17(ushort sizeMb, ushort speed, ushort configured, byte partIndex, byte formFactor = 0x09, uint extSpeed = 0, uint extConfigured = 0)
    {
        var s = new byte[0x5C];
        s[0] = 17; s[1] = 0x5C;
        BitConverter.GetBytes(sizeMb).CopyTo(s, 0x0C);
        s[0x0E] = formFactor;
        s[0x12] = 0x1A;
        BitConverter.GetBytes(speed).CopyTo(s, 0x15);
        s[0x1A] = partIndex;
        BitConverter.GetBytes(configured).CopyTo(s, 0x20);
        BitConverter.GetBytes(extSpeed).CopyTo(s, 0x54);
        BitConverter.GetBytes(extConfigured).CopyTo(s, 0x58);
        return s;
    }

    private static byte[] Strings(params string[] strings) =>
        strings.Length == 0 ? [0, 0] : [.. strings.SelectMany(text => Encoding.ASCII.GetBytes(text).Append((byte)0)), 0];

    private static byte[] Table(params byte[][] parts)
    {
        var body = parts.SelectMany(part => part).ToArray();
        return [0, 3, 3, 0, .. BitConverter.GetBytes((uint)body.Length), .. body];
    }

    [Fact]
    public void ReadsFittedModulesAndTheirOwnPartNumbers()
    {
        var raw = Table(
            [0, 4, 0, 0], Strings("BIOS vendor"),                                   // type 0 with a string
            Type17(0, 0, 0, 1), Strings("DIMM_A1", "Unknown"),                       // empty slot
            Type17(8192, 3600, 3600, 2), Strings("DIMM_A2", "CMK16GX4M2D3600C18"),
            Type17(8192, 0xFFFF, 0xFFFF, 0, extSpeed: 8000, extConfigured: 7200), Strings(),
            [127, 4, 0, 0], Strings());
        var dimms = PcHealth.ParseDimms(raw);
        Assert.Equal(2, dimms.Count);
        Assert.Equal(new PcHealth.Dimm(3600, 3600, "CMK16GX4M2D3600C18", 0x09), dimms[0]);
        Assert.Equal(new PcHealth.Dimm(8000, 7200, null, 0x09), dimms[1]);
    }

    [Fact]
    public void ACutOffTableEndsTheListInsteadOfThrowing()
    {
        var raw = Table(Type17(8192, 3600, 3600, 1), Strings("CMK16GX4M2D3600C18"), Type17(8192, 3200, 3200, 1));
        Assert.Single(PcHealth.ParseDimms(raw[..^40]));
        Assert.Empty(PcHealth.ParseDimms([1, 2, 3]));
        Assert.Empty(PcHealth.ParseDimms([0, 3, 3, 0, 255, 255, 0, 0, 17, 2]));
    }

    // ---- RAM ----

    [Theory]
    [InlineData("CMK16GX4M2D3600C18", 3600)]
    [InlineData("CMK32GX5M2B6000C36", 6000)]
    [InlineData("F4-3600C16D-16GVKC", 3600)]
    [InlineData("F5-6000J3038F16GX2-TZ5RK", 6000)]
    [InlineData("KF3600C17D4/8GX", 3600)]
    [InlineData("KF436C16BB/8", 3600)]
    [InlineData("KF560C36BBE-16", 6000)]
    [InlineData("HX436C17FB3/8", 3600)]
    [InlineData("BL8G36C16U4B", 3600)]
    [InlineData("CP16G60C36U5B", 6000)]
    [InlineData("PVS416G360C8K", 3600)]
    [InlineData("AX4U360038G18A-DT41", 3600)]
    [InlineData("TF3D416G3600HC18JDC01", 3600)]
    [InlineData("FF3D532G6000HC38ADC01", 6000)]
    public void ReadsTheRatingInEnthusiastPartNumbers(string part, int rated) => Assert.Equal(rated, PcHealth.RatedFromPart(part));

    [Theory]
    [InlineData("M378A1K43CB2-CTD")] // Samsung JEDEC
    [InlineData("HMA81GU6CJR8N-VK")] // SK hynix JEDEC
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void PlainPartNumbersHaveNoRating(string? part) => Assert.Null(PcHealth.RatedFromPart(part));

    [Fact]
    public void AKitAtItsJedecDefaultIsSlow()
    {
        PcHealth.Dimm[] off = [new(2133, 2133, "CMK16GX4M2D3600C18", 0x09), new(2133, 2133, "CMK16GX4M2D3600C18", 0x09)];
        Assert.Equal((2133, 3600), PcHealth.SlowRam(off));
        // The firmware's own rating is enough when the part number says nothing.
        Assert.Equal((2400, 3200), PcHealth.SlowRam([new(3200, 2400, "Unknown", 0x09)]));
        Assert.Equal((4800, 6000), PcHealth.SlowRam([new(4800, 4800, "F5-6000J3038F16GX2-TZ5RK", 0x09)]));
        // 2667 is how some boards write 2666.
        Assert.Equal((2667, 3600), PcHealth.SlowRam([new(3600, 2667, null, 0x09)]));
    }

    [Fact]
    public void RamAtItsRatingTunedByHandOrInALaptopIsLeftAlone()
    {
        Assert.Null(PcHealth.SlowRam([new(3600, 3600, "CMK16GX4M2D3600C18", 0x09), new(3600, 3600, "CMK16GX4M2D3600C18", 0x09)])); // this PC
        Assert.Null(PcHealth.SlowRam([new(3600, 3466, "CMK16GX4M2D3600C18", 0x09)])); // set by hand
        Assert.Null(PcHealth.SlowRam([new(3200, 2400, "KF432S20IB/8", 0x0D)])); // SODIMM
        Assert.Null(PcHealth.SlowRam([new(3200, 2400, null, 0x09), new(3200, 2400, null, 0x0D)]));
        Assert.Null(PcHealth.SlowRam([new(3600, 0, null, 0x09)])); // speed not reported
        Assert.Null(PcHealth.SlowRam([]));
    }

    [Fact]
    public void TheSlowestModuleBoundsAMixedKit() =>
        Assert.Equal((2133, 3200), PcHealth.SlowRam([new(2133, 2133, "CMK16GX4M2D3600C18", 0x09), new(2133, 2133, "F4-3200C16-8GVKB", 0x09)]));

    // ---- Resizable BAR ----

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070", false, false, true)]
    [InlineData("NVIDIA GeForce RTX 3070", false, true, false)] // this PC
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", false, false, true)]
    [InlineData("NVIDIA GeForce RTX 5090", false, false, true)]
    [InlineData("AMD Radeon RX 6800 XT", false, false, true)]
    [InlineData("AMD Radeon RX 9070 XT", false, false, true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", false, false, true)]
    [InlineData("Intel(R) Arc(TM) Graphics", true, false, false)] // integrated: shares memory
    [InlineData("AMD Radeon(TM) Graphics", true, false, false)]
    [InlineData("NVIDIA GeForce RTX 2080 Ti", false, false, false)] // no Resizable BAR on Turing
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", false, false, false)]
    [InlineData("AMD Radeon RX 580", false, false, false)]
    public void ResizableBarIsOffOnlyOnACardThatSupportsIt(string name, bool uma, bool upload, bool off) =>
        Assert.Equal(off, PcHealth.ReBarOff(new PcHealth.GpuBar(name, uma, upload)));

    [Fact]
    public void AWindowsThatCannotSayShowsNothing()
    {
        Assert.False(PcHealth.ReBarOff(new PcHealth.GpuBar("NVIDIA GeForce RTX 3070", false, null)));
        Assert.False(PcHealth.ReBarOff(null));
    }

    // ---- Chipset ----

    [Fact]
    public void ReadsTheNewestPackageOnAmdsPage()
    {
        const string page = """
            <h4>AMD Chipset Drivers</h4><strong>Revision Number</strong><p>8.09.28.1124</p>
            <a href="https://drivers.amd.com/drivers/amd_chipset_software_8.09.28.1124.exe">Download</a>
            <a href="https://drivers.amd.com/drivers/amd_chipset_software_8.08.12.551.exe">Previous</a>
            """;
        Assert.Equal("8.09.28.1124", PcHealth.LatestChipset(page));
        Assert.Null(PcHealth.LatestChipset("<html>Access denied</html>"));
    }

    [Theory]
    [InlineData("8.08.12.551", "8.09.28.1124", true)] // this PC
    [InlineData("8.09.28.1124", "8.09.28.1124", false)]
    [InlineData("8.10.01.100", "8.9.28.1124", false)] // compared as numbers, not text
    [InlineData("8.9.28.1124", "8.10.01.100", true)]
    [InlineData(null, "8.09.28.1124", false)]
    [InlineData("8.08.12.551", null, false)] // AMD's page couldn't be read
    [InlineData("garbage", "8.09.28.1124", false)]
    public void ComparesChipsetVersionsAsNumbers(string? installed, string? latest, bool behind) =>
        Assert.Equal(behind, PcHealth.ChipsetBehind(installed, latest));

    // ---- Game Mode ----

    [Fact]
    public void GameModeIsOffOnlyWhenItsSwitchWroteZero()
    {
        Assert.True(PcHealth.GameModeOff(0));
        Assert.False(PcHealth.GameModeOff(1));
        Assert.False(PcHealth.GameModeOff(null)); // this PC: never switched, so on
        Assert.False(PcHealth.GameModeOff("0"));
    }

    // ---- What shows ----

    // Nexus LiteOS on this PC: maximum state 100, boost aggressive, parking off. Balanced on a dual-CCD X3D parks (AMD's provisioning).
    private static readonly PowerPlan.Limits NexusLimits = new(100, 2, 100);
    private static readonly PowerPlan.Limits X3dBalancedLimits = new(100, 2, 50);

    private static PowerPlan.State Plan(Guid active, Guid overlay, string? name = null, bool exo = false, bool canUndo = false, PowerPlan.Limits? cpu = null) =>
        new(active, name, overlay, false, exo, canUndo, cpu ?? new(100, 1, 100));

    private static PcHealth.Reading ThisPc() => new(
        [new(3600, 3600, "CMK16GX4M2D3600C18", 0x09), new(3600, 3600, "CMK16GX4M2D3600C18", 0x09)],
        new PcHealth.GpuBar("NVIDIA GeForce RTX 3070", false, true),
        ThisPcCpu,
        Plan(Nexus, Guid.Empty, "Nexus LiteOS Power Plan", cpu: NexusLimits),
        "8.08.12.551",
        "8.09.28.1124",
        false);

    [Fact]
    public void ThisPcIsOfferedExosPlanAndShowsItsChipset()
    {
        var items = PcHealth.Decide(ThisPc());
        Assert.Equal(["power", "chipset"], items.Select(item => item.Key));
        // Nexus isn't holding the 5600X back: an offer, never a warning, and never switched by itself.
        Assert.Equal("Your PC uses “Nexus LiteOS Power Plan”. Exo's plan is tuned for your Ryzen 5 5600X.", items[0].Detail);
        Assert.Equal("Ryzen 5 5600X", items[0].Cpu);
        Assert.False(items[0].Applied);
        Assert.Equal("8.08.12.551 installed · 8.09.28.1124 available", items[1].Detail);
        Assert.Null(items[1].Url); // Update installs it through Exo (ChipsetUpdateTests)
    }

    [Fact]
    public void PowerSaverSaysWhy()
    {
        var item = PcHealth.Decide(ThisPc() with { Power = Plan(PowerPlan.PowerSaver, Guid.Empty, "Power saver") })[0];
        Assert.Equal("power", item.Key);
        Assert.Equal("Your PC uses Power saver, which slows your processor down. Exo's plan keeps your Ryzen 5 5600X at full speed.", item.Detail);
        Assert.False(item.Applied);
    }

    [Theory]
    [InlineData(99u, 2u, "“Nexus LiteOS Power Plan” caps your processor at 99%.")]
    [InlineData(100u, 0u, "“Nexus LiteOS Power Plan” turns processor boost off.")]
    public void ACustomPlanThatHoldsTheProcessorBackSaysWhy(uint max, uint boost, string start)
    {
        var item = PcHealth.Decide(ThisPc() with { Power = Plan(Nexus, Guid.Empty, "Nexus LiteOS Power Plan", cpu: new(max, boost, 100)) })[0];
        Assert.Equal("power", item.Key);
        Assert.StartsWith(start, item.Detail);
    }

    [Fact]
    public void ACustomPlanWhoseSettingsCantBeReadIsOfferedNotWarned() =>
        Assert.StartsWith("Your PC uses another plan.", PcHealth.Decide(ThisPc() with { Power = Plan(Nexus, Guid.Empty, cpu: new(null, null, null)) })[0].Detail);

    [Fact]
    public void WindowsOwnPlanThatIsFineShowsNothing()
    {
        // Exo switches Windows' own plans by itself; after Undo, Balanced or High performance isn't a problem.
        Assert.DoesNotContain(PcHealth.Decide(ThisPc() with { Power = Plan(PowerPlan.Balanced, Guid.Empty, "Balanced") }), i => i.Key == "power");
        Assert.DoesNotContain(PcHealth.Decide(ThisPc() with { Power = Plan(PowerPlan.HighPerformance, Guid.Empty, "High performance") }), i => i.Key == "power");
    }

    [Fact]
    public void EverythingRightShowsOnlyTheChipsetUpToDate()
    {
        var item = Assert.Single(PcHealth.Decide(ThisPc() with { Power = Plan(Guid.NewGuid(), Guid.Empty, "Exo", exo: true), ChipsetLatest = "8.08.12.551" }));
        Assert.Equal("chipset", item.Key);
        Assert.True(item.Current);
        Assert.Equal("8.08.12.551 installed, AMD's newest", item.Detail);
        // AMD's page unread: no claim either way.
        Assert.Empty(PcHealth.Decide(ThisPc() with { Power = Plan(Guid.NewGuid(), Guid.Empty, "Exo", exo: true), ChipsetLatest = null }));
    }

    [Fact]
    public void EveryProblemShowsInOrder()
    {
        var items = PcHealth.Decide(ThisPc() with
        {
            Dimms = [new(2133, 2133, "CMK16GX4M2D3600C18", 0x09)],
            Power = Plan(PowerPlan.PowerSaver, Guid.Empty),
            Gpu = new PcHealth.GpuBar("NVIDIA GeForce RTX 3070", false, false),
            GameModeOff = true,
        });
        Assert.Equal(["ram", "rebar", "power", "chipset", "gamemode"], items.Select(item => item.Key));
        Assert.Equal("Your RAM runs at 2133 MT/s; it's rated for 3600. Turn on EXPO/XMP in the BIOS.", items[0].Detail);
        Assert.Contains("Turn on Resizable BAR in the BIOS", items[1].Detail);
    }

    [Fact]
    public void AfterUseExosPlanTheRowStaysWithUndo()
    {
        var item = Assert.Single(PcHealth.Decide(ThisPc() with { Power = Plan(Guid.NewGuid(), Guid.Empty, "Exo", exo: true, canUndo: true), ChipsetLatest = null }));
        Assert.Equal("Exo's plan, tuned for your Ryzen 5 5600X", item.Detail);
        Assert.True(item.Applied);
        Assert.True(item.CanUndo);
    }

    [Fact]
    public void BalancedWithBestPowerEfficiencySaysSo()
    {
        var item = PcHealth.Decide(ThisPc() with { Power = Plan(PowerPlan.Balanced, PowerPlan.BestEfficiency) })[0];
        Assert.StartsWith("Your PC uses Balanced with Best power efficiency, which slows your processor down.", item.Detail);
        // On battery the power mode is Windows' to pick.
        Assert.DoesNotContain(PcHealth.Decide(ThisPc() with { Power = Plan(PowerPlan.Balanced, PowerPlan.BestEfficiency) with { Battery = true } }), i => i.Key == "power");
    }

    [Fact]
    public void AnIntelPcGetsExosPlanForItsOwnProcessorAndNoAmdRows()
    {
        // Without Intel's chipset package installed, no chipset row either (ChipsetUpdateTests has Intel's row).
        var items = PcHealth.Decide(ThisPc() with { Cpu = "Intel(R) Core(TM) i7-14700K", ChipsetInstalled = null, GameModeOff = true });
        Assert.Equal(["power", "gamemode"], items.Select(item => item.Key));
        Assert.Equal("Core i7-14700K", items[0].Cpu);
        Assert.DoesNotContain("V-Cache", items[1].Detail);
    }

    [Fact]
    public void APlanWindowsDidntReportShowsNoPowerRow() =>
        Assert.DoesNotContain(PcHealth.Decide(ThisPc() with { Power = null }), item => item.Key == "power");

    [Fact]
    public void ADualCcdX3dWithParkingOffHearsAboutTheCacheCores()
    {
        var items = PcHealth.Decide(ThisPc() with { Cpu = "AMD Ryzen 9 9950X3D 16-Core Processor", GameModeOff = true });
        Assert.Equal("“Nexus LiteOS Power Plan” keeps every core awake, so Windows can't keep games on your processor's V-Cache cores. Exo's plan keeps your Ryzen 9 9950X3D at full speed and games on its V-Cache cores.",
            items.Single(item => item.Key == "power").Detail);
        Assert.Contains("V-Cache cores", items.Single(item => item.Key == "gamemode").Detail);
        // One CCD: nothing to park, so the same plan only gets the offer.
        Assert.StartsWith("Your PC uses", PcHealth.Decide(ThisPc() with { Cpu = "AMD Ryzen 7 5800X3D 8-Core Processor" })[0].Detail);
    }

    [Theory]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor")]
    [InlineData("AMD Ryzen 9 7900X3D 12-Core Processor")]
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor")]
    public void ADualCcdX3dOnBalancedShowsNothing(string cpu) =>
        Assert.DoesNotContain(PcHealth.Decide(ThisPc() with { Cpu = cpu, Power = Plan(PowerPlan.Balanced, Guid.Empty, cpu: X3dBalancedLimits) }), item => item.Key == "power");
}
