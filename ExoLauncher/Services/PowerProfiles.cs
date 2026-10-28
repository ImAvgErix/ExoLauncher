namespace ExoLauncher.Services;

/// <summary>
/// The settings Exo's power plan writes, per processor class: one table, so a new class or setting is one line.
/// Exo's plan is a copy of Windows' Balanced (<see cref="PowerPlan"/>), so everything not listed here stays as
/// Balanced has it, including what AMD's and Intel's own drivers tune there (Game Mode's CCD parking, Thread Director's
/// heterogeneous policies) and its power profiles (Game Mode and LowLatency hold the minimum state at 100 while a game or
/// an app launch runs), which Windows keys to the Balanced personality. Values are written for AC; on battery a laptop
/// keeps Balanced's. Classes that want the same settings share a profile. GUIDs are powercfg /qh's.
/// Left out on purpose, though gaming plans set them:
/// - idle states off: in AlchemyTweaks' 39-plan Fortnite test (i5-10400F, PresentMon, 5 runs a plan) three of the four
///   idle-off plans placed last, 13-14% below the leader;
/// - idle held shallow (promote/demote near 100), a long performance check interval, throttle states off: that test has
///   them at its top and its bottom alike, and two plans that differ only in settings inert on its processor placed 6%
///   apart, so it can't credit any of them;
/// - autonomous mode off, which stops EPP; the High performance personality, which drops Game Mode's profile and AMD's
///   parking; performance increase/decrease thresholds, times and policies, which Windows stops using once the processor
///   picks its own clocks (autonomous mode); latency hints and hub suspend timeouts, redundant with the values below.
/// </summary>
internal static class PowerProfiles
{
    internal enum CpuClass { AmdSingleCcd, AmdDualCcd, AmdDualCcdX3d, AmdDualCcdZen5, IntelNonHybrid, IntelHybrid, Unknown }

    /// <param name="Dc">Also written for battery (only where noted: desktops, which run on battery only behind a UPS).</param>
    internal sealed record Value(string Name, Guid Subgroup, Guid Setting, uint Ac, bool Dc = false);

    /// <summary>What Exo's plan holds on this PC: the class's key (amd-single-ccd, ...) and the settings it writes.</summary>
    internal sealed record Profile(CpuClass Class, bool Laptop, IReadOnlyList<Value> Values)
    {
        internal string Key => Keys[Class];
    }

    internal static readonly IReadOnlyDictionary<CpuClass, string> Keys = new Dictionary<CpuClass, string>
    {
        [CpuClass.AmdSingleCcd] = "amd-single-ccd",
        [CpuClass.AmdDualCcd] = "amd-dual-ccd",
        [CpuClass.AmdDualCcdX3d] = "amd-dual-ccd-x3d",
        [CpuClass.AmdDualCcdZen5] = "amd-dual-ccd-zen5",
        [CpuClass.IntelNonHybrid] = "intel-non-hybrid",
        [CpuClass.IntelHybrid] = "intel-hybrid",
        [CpuClass.Unknown] = "unknown",
    };

    internal static readonly Guid Processor = new("54533251-82be-4824-96c1-47b60b740d00");
    internal static readonly Guid Pcie = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    internal static readonly Guid Usb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    internal static readonly Guid Disk = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    internal static readonly Guid Sleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    internal static readonly Guid Display = new("7516b95f-f776-4464-8c53-06167f40cc99");

    // ---- Processor ----

    // Never below base clock, so a frame's first work doesn't wait for a ramp: High performance's value, and what Windows'
    // own Game Mode and LowLatency profiles set while they run (Microsoft PPM docs); every top-ten plan in AlchemyTweaks' test.
    private static readonly Value MinState = new("Minimum processor state", Processor, new("893dee8e-2bef-41e0-89c6-b55d0929964c"), 100);
    // Energy performance preference 0: the processor's own clock logic favors performance (Microsoft: 0 programs the EPP
    // register to 0; its server tuning guide's setting for latency); High performance's value.
    private static readonly Value Epp = new("Energy performance preference", Processor, new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), 0);
    // Aggressive: maximum boost where Windows picks clocks; the same as Enabled once the processor picks (Microsoft PERFBOOSTMODE).
    private static readonly Value BoostMode = new("Boost mode", Processor, new("be337238-0d82-4146-a960-4f3749d470c7"), 2);
    // Boost policy 100 (Balanced has 60): full boost on processors where Windows still picks clocks; High performance's value.
    private static readonly Value BoostPolicy = new("Boost policy", Processor, new("45bcc044-d885-43e2-8605-ee0ec6e96b59"), 100);
    // Core parking off: every core stays awake, so a game's new thread never waits for one to unpark (Microsoft: 100 turns
    // parking off; High performance's value).
    private static readonly Value MinCores = new("Core parking min cores", Processor, new("0cc5b647-c1df-4637-891a-dec35c318583"), 100);
    // Idle states stay on (0): with them off three of four plans placed last in AlchemyTweaks' test, 13-14% fewer frames.
    private static readonly Value IdleOn = new("Processor idle disable", Processor, new("5d76a2ca-e8c0-402f-a133-2158492d58ad"), 0);

    // ---- Devices: no power saving on the paths a frame or an input takes ----

    // PCIe link power management off: the GPU and NVMe links never wait to leave L0s/L1 mid-frame; High performance's value.
    private static readonly Value PcieLink = new("PCIe Link State Power Management", Pcie, new("ee12f906-d277-404b-b6da-e5fa1a576df5"), 0);
    // USB selective suspend off: a mouse, keyboard or headset left idle is never suspended and woken by its next input
    // (Microsoft HID power docs); Ultimate Performance's value.
    private static readonly Value UsbSuspend = new("USB selective suspend", Usb, new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"), 0);
    // USB 3 link power management off: no U1/U2 link states, whose exits take microseconds to milliseconds (Microsoft USB 3
    // LPM docs); Ultimate Performance's value.
    private static readonly Value Usb3Link = new("USB 3 Link Power Management", Usb, new("d4e98f31-5ffe-4ce1-be31-1b38b384c009"), 0);
    // NVMe: Windows' driver picks the deepest idle state whose entry plus exit latency fits these tolerances, so 0 keeps the
    // drive in its working state (Microsoft StorNVMe docs; High performance's values). The idle timeouts and NOPPME stay
    // Balanced's: with no state to enter they do nothing, and NOPPME off only forbids background work inside one.
    private static readonly Value NvmeLatency = new("Primary NVMe Power State Transition Latency Tolerance", Disk, new("fc95af4d-40e7-4b6d-835a-56d131dbc80e"), 0);
    private static readonly Value NvmeLatency2 = new("Secondary NVMe Power State Transition Latency Tolerance", Disk, new("dbc9e238-6de9-49e3-92cd-8c2b4946b472"), 0);
    // SATA (AHCI) link power management off, host- and device-initiated, and its adaptive timer; High performance's values.
    private static readonly Value AhciLink = new("AHCI Link Power Management - HIPM/DIPM", Disk, new("0b2d69d7-a2a1-449c-9680-f91c70521c60"), 0);
    private static readonly Value AhciAdaptive = new("AHCI Link Power Management - Adaptive", Disk, new("dab60367-53fe-4fbc-825e-521d069d2456"), 0);
    // Disks never spin down (a game's next load doesn't wait for a hard drive to spin up); Ultimate Performance's value.
    private static readonly Value DiskOff = new("Turn off hard disk after", Disk, new("6738e2c4-e8a5-4a42-b16a-e040e769756e"), 0);

    // ---- Display ----

    // The screen turns itself off after 30 minutes without keyboard or mouse input, so a still desktop doesn't burn into an
    // OLED; long enough that a read or a download isn't cut short, and a running game keeps it on (MainWindow.GameAwake).
    // Desktops on battery too (a UPS); a laptop on AC only, its battery keeping Balanced's shorter time.
    private static readonly Value DisplayOn = new("Turn off display after", Display, new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"), 1800, Dc: true);

    // ---- Desktops only ----

    // A desktop never sleeps or hibernates on its own: a long download, a shader compile, or a game played on a controller
    // that doesn't hold the PC awake itself keeps going.
    private static readonly Value SleepOff = new("Sleep after", Sleep, new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), 0, Dc: true);
    private static readonly Value HibernateOff = new("Hibernate after", Sleep, new("9d7815a6-7ee4-497e-8888-515a05f02364"), 0, Dc: true);

    // ---- Intel hybrid: efficiency class 1 is the faster class (P-cores), class 0 the E-cores ----
    // (Windows: a higher EfficiencyClass is faster; HeteroIncreaseThreshold unparks class 1 cores as class 0 fills.)

    // P-cores favor performance (0); the E-cores' preference stays as Balanced and Intel's PPM package have it.
    private static readonly Value EppPerformant = new("Energy performance preference for class 1", Processor, new("36687f9e-e3a5-4dbf-b1dc-15eb381c6864"), 0);
    // P-cores never park, so a game's thread never waits for one; E-cores park as Balanced has them, for Thread Director.
    private static readonly Value MinCoresPerformant = new("Core parking min cores for class 1", Processor, new("0cc5b647-c1df-4637-891a-dec35c318584"), 100);

    // ---- AMD dual-CCD with Game Mode CCD parking (X3D; Zen 5's 9900X and 9950X too) ----

    // Best performance's own preference (10): low, but leaving AMD's driver room to park the second CCD in games.
    private static readonly Value EppParked = Epp with { Ac = 10 };

    private static readonly Value[] Devices = [PcieLink, UsbSuspend, Usb3Link, NvmeLatency, NvmeLatency2, AhciLink, AhciAdaptive, DiskOff];

    /// <summary>The measured latency baseline: single-CCD Ryzen, Zen 2 to 4 dual-CCD Ryzen (no Game Mode parking), Intel up to 11th gen, and any desktop processor Exo doesn't know.</summary>
    private static readonly Value[] Baseline = [MinState, Epp, BoostMode, BoostPolicy, MinCores, IdleOn, .. Devices];

    /// <summary>
    /// Dual-CCD Ryzen whose AMD driver parks the second CCD in Game Mode (X3D, and Zen 5's 9900X and 9950X): the
    /// baseline, but minimum state and core parking stay as Balanced has them, so the driver can park it.
    /// </summary>
    private static readonly Value[] DualCcdParked = [EppParked, BoostMode, BoostPolicy, IdleOn, .. Devices];

    /// <summary>Intel hybrid: P-cores at EPP 0 and never parked; E-cores and Balanced's heterogeneous policies as they are, for Thread Director.</summary>
    private static readonly Value[] Hybrid = [EppPerformant, MinCoresPerformant, IdleOn, .. Devices];

    /// <summary>
    /// This class's settings. A laptop gets them on AC only (battery keeps Balanced's) and no sleep changes; a laptop
    /// with a processor Exo doesn't know keeps Balanced as it is.
    /// </summary>
    internal static Profile For(CpuClass cpu, bool laptop)
    {
        Value[] values = cpu switch
        {
            CpuClass.AmdDualCcdX3d or CpuClass.AmdDualCcdZen5 => DualCcdParked,
            CpuClass.IntelHybrid => Hybrid,
            CpuClass.Unknown when laptop => [],
            _ => Baseline,
        };
        if (values.Length == 0) return new Profile(cpu, laptop, values);
        return new Profile(cpu, laptop, laptop ? [.. values, DisplayOn with { Dc = false }] : [.. values, DisplayOn, SleepOff, HibernateOff]);
    }
}
