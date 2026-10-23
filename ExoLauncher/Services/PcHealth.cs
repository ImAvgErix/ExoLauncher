using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// System > This PC: setup that quietly costs games speed. Exo reads it and shows a row only for what is wrong:
/// RAM below its rated speed (EXPO/XMP off), Resizable BAR off, a power plan that isn't Exo's (the one row Exo can fix,
/// with Undo: <see cref="PowerPlan"/>; Windows' own plans switch to it by themselves, a custom one is offered it), an AMD
/// or Intel chipset driver behind its maker's newest (<see cref="ChipsetUpdate"/>), and Game Mode off. Each check is a pure decision
/// over a reading (<see cref="Decide"/>); a reading that fails drops its row instead of guessing.
/// </summary>
internal static partial class PcHealth
{
    /// <summary>
    /// One problem and its hint. <paramref name="Applied"/>: Exo's plan on, with Undo; the chipset update waiting for a
    /// restart. <paramref name="Busy"/>: Exo is working on it (the chipset install). <paramref name="Cpu"/>: the
    /// processor Exo's plan is tuned for, for its button. <paramref name="Current"/>: nothing to do, said so (the chipset
    /// driver on its maker's newest, so an update that just finished doesn't make its row vanish).
    /// </summary>
    internal sealed record Item(string Key, string Label, string Detail, string? Url = null, bool Applied = false, bool CanUndo = false, bool Busy = false, string? Cpu = null, bool Current = false);

    /// <summary>A fitted memory module as SMBIOS (type 17) describes it. Speeds in MT/s; 0 when the firmware leaves them out.</summary>
    internal sealed record Dimm(int Speed, int Configured, string? Part, byte FormFactor);

    /// <summary>The main graphics card: whether it shares the system's memory (an integrated GPU) and whether Direct3D 12 reports GPU upload heaps (null: this Windows or driver can't say).</summary>
    internal sealed record GpuBar(string Name, bool Uma, bool? UploadHeap);

    internal sealed record Reading(
        IReadOnlyList<Dimm> Dimms,
        GpuBar? Gpu,
        string? Cpu,
        PowerPlan.State? Power,
        string? ChipsetInstalled,
        string? ChipsetLatest,
        bool GameModeOff,
        ChipsetUpdate.Status? ChipsetUpdate = null,
        bool ChipsetAuto = false);

    private const byte DimmFormFactor = 0x09; // SMBIOS: a desktop DIMM (SODIMMs are laptops and mini PCs, which seldom offer EXPO/XMP)

    // The speeds memory runs at with EXPO/XMP off (JEDEC's DDR4 and DDR5 bins). A kit at any other speed was set by hand.
    private static readonly int[] Jedec = [1333, 1600, 1866, 2133, 2400, 2666, 2933, 3200, 4000, 4400, 4800, 5200, 5600, 6000, 6400];

    internal static IReadOnlyList<Item> Decide(Reading r)
    {
        var items = new List<Item>();
        var x3d = r.Cpu is not null && DualCcdX3d().IsMatch(r.Cpu);
        var parked = x3d || r.Cpu is not null && DualCcdZen5().IsMatch(r.Cpu);
        if (SlowRam(r.Dimms) is { } ram)
            items.Add(new("ram", "Memory speed", $"Your RAM runs at {ram.Runs} MT/s; it's rated for {ram.Rated}. Turn on EXPO/XMP in the BIOS."));
        if (ReBarOff(r.Gpu))
            items.Add(new("rebar", "Resizable BAR", "Off, so games reach only a small part of your graphics card's memory at a time. Turn on Resizable BAR in the BIOS."));
        if (r.Power is { } power)
        {
            var cpu = ShortCpu(r.Cpu) ?? "processor";
            if (power.Exo)
            {
                if (power.CanUndo) items.Add(new("power", "Power plan", $"Exo's plan, tuned for your {cpu}", Applied: true, CanUndo: true));
            }
            else if (HeldBack(power, parked, x3d) is { } why)
                items.Add(new("power", "Power plan", $"{why} Exo's plan keeps your {cpu} at full speed{(x3d ? " and games on its V-Cache cores" : "")}.", Cpu: cpu));
            // Windows' own plans switch to Exo's by themselves; after Undo, only a plan that holds the processor back speaks.
            else if (!power.Stock)
                items.Add(new("power", "Power plan", $"Your PC uses {Quoted(power.Name)}. Exo's plan is tuned for your {cpu}.", Cpu: cpu));
        }
        if (ChipsetVendor(r.Cpu) is { } vendor && Chipset(r, vendor) is { } chipset) items.Add(chipset);
        if (r.GameModeOff)
            items.Add(new("gamemode", "Game Mode", x3d
                ? "Off. With Game Mode on, Windows keeps games on your processor's V-Cache cores."
                : "Off. With Game Mode on, Windows puts the game you're playing first."));
        return items;
    }

    /// <summary>
    /// The chipset row (AMD's or Intel's, <paramref name="vendor"/>): Restart to finish after an update that needs one, the
    /// version going in while it installs, Up to date on the maker's newest, else when the maker has a newer one what is
    /// installed and what's next (Exo installs it while you're away when it can do so without asking; Update installs it
    /// now). After a failed install, why, with the maker's page.
    /// </summary>
    private static Item? Chipset(Reading r, string vendor)
    {
        var maker = GpuDriverUpdate.Maker(vendor);
        var label = $"{maker} chipset driver";
        switch (r.ChipsetUpdate)
        {
            case { Phase: Services.ChipsetUpdate.Phase.Restart }:
                return new("chipset", label, $"Restart to finish the {maker} chipset update", Applied: true);
            case { Phase: Services.ChipsetUpdate.Phase.Installing } installing:
                return new("chipset", label, $"Updating to {installing.Version}. {(vendor == "amd" ? "USB and sound may drop" : "Devices may reset")} for a moment", Busy: true);
        }
        if (!ChipsetBehind(r.ChipsetInstalled, r.ChipsetLatest))
            return r is { ChipsetInstalled: { } installed, ChipsetLatest: not null } ? new("chipset", label, $"{installed} installed, {maker}'s newest", Current: true) : null;
        var now = $"{r.ChipsetInstalled} installed · {r.ChipsetLatest}";
        if (r.ChipsetUpdate is { Phase: Services.ChipsetUpdate.Phase.Failed } failed)
            return new("chipset", label, $"{now} available. {failed.Reason}", Url: vendor == "amd" ? Services.ChipsetUpdate.AmdPage : Services.ChipsetUpdate.IntelPage);
        return new("chipset", label, r.ChipsetAuto ? $"{now} installs while you're away" : $"{now} available");
    }

    /// <summary>Whose chipset driver Exo keeps up to date on this processor: amd on a Ryzen, intel on an Intel processor, else null.</summary>
    internal static string? ChipsetVendor(string? cpu) =>
        cpu is null ? null
        : cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ? "amd"
        : cpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "intel"
        : null;

    /// <summary>
    /// Why the active plan costs the processor speed, or null when it doesn't. Held back: Power saver, a maximum
    /// processor state under 100, boost disabled, or Balanced with Best power efficiency on AC. On a dual-CCD X3D or
    /// Zen 5's 9900X/9950X (<paramref name="parked"/>), also core parking off (minimum cores 100): AMD's driver parks the
    /// second CCD so a game stays on one CCD (the cache CCD on X3D), and a plan that keeps every core awake stops it.
    /// Anything else (Balanced, High performance, a third-party plan tuned for speed) isn't holding the processor back.
    /// </summary>
    internal static string? HeldBack(PowerPlan.State power, bool parked, bool x3d)
    {
        var plan = string.IsNullOrWhiteSpace(power.Name) ? "Your power plan" : Quoted(power.Name);
        if (power.Active == PowerPlan.PowerSaver) return "Your PC uses Power saver, which slows your processor down.";
        if (power.Cpu?.MaxState is < 100 and var max) return $"{plan} caps your processor at {max}%.";
        if (power.Cpu?.BoostMode == 0) return $"{plan} turns processor boost off.";
        if (power.Active == PowerPlan.Balanced && power.Overlay == PowerPlan.BestEfficiency && !power.Battery)
            return "Your PC uses Balanced with Best power efficiency, which slows your processor down.";
        if (parked && power.Cpu?.MinCores >= 100)
            return x3d
                ? $"{plan} keeps every core awake, so Windows can't keep games on your processor's V-Cache cores."
                : $"{plan} keeps every core awake, so Windows can't keep a game on one half of your processor.";
        return null;
    }

    private static string Quoted(string? name) => string.IsNullOrWhiteSpace(name) ? "another plan" : $"“{name.Trim()}”";

    /// <summary>
    /// Which of Exo's plan profiles fits this processor. AMD by name: dual-CCD X3D, Zen 5's 9900X/9950X (parked in Game
    /// Mode like X3D), dual-CCD (Ryzen 9 x900/x950 and Threadripper, the 12- and 16-core HX laptops, Ryzen AI Max
    /// 390/395), any other Ryzen single-CCD. Intel: hybrid when CPUID says so
    /// (<paramref name="hybrid"/>; 12th gen and newer with E-cores, Core Ultra), else up to 11th gen and E-core-less
    /// models. When CPUID can't say, the name decides. Anything else is unknown.
    /// </summary>
    internal static PowerProfiles.CpuClass ClassOf(string? name, bool? hybrid)
    {
        if (string.IsNullOrWhiteSpace(name)) return PowerProfiles.CpuClass.Unknown;
        if (DualCcdX3d().IsMatch(name)) return PowerProfiles.CpuClass.AmdDualCcdX3d;
        if (DualCcdZen5().IsMatch(name)) return PowerProfiles.CpuClass.AmdDualCcdZen5;
        if (DualCcd().IsMatch(name)) return PowerProfiles.CpuClass.AmdDualCcd;
        if (name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)) return PowerProfiles.CpuClass.AmdSingleCcd;
        if (!name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return PowerProfiles.CpuClass.Unknown;
        return hybrid ?? HybridIntel().IsMatch(name) ? PowerProfiles.CpuClass.IntelHybrid : PowerProfiles.CpuClass.IntelNonHybrid;
    }

    /// <summary>The processor's name as people say it: "Ryzen 5 5600X", "Core i7-14700K", "Core Ultra 7 265K".</summary>
    internal static string? ShortCpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var plain = CpuNoise().Replace(CpuTail().Replace(name, ""), " ");
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        return plain.Length == 0 ? null : plain;
    }

    /// <summary>SMBIOS chassis types of a portable PC: portable, laptop, notebook, handheld, sub notebook, tablet, convertible, detachable.</summary>
    private static readonly byte[] PortableChassis = [8, 9, 10, 11, 14, 30, 31, 32];

    /// <summary>A laptop (or tablet): a system battery, or a portable chassis in SMBIOS.</summary>
    internal static bool Laptop(bool battery, byte? chassis) => battery || chassis is { } type && PortableChassis.Contains(type);

    /// <summary>
    /// The slowest module's speed and the kit's rating when the kit runs at a JEDEC default below what it's rated for:
    /// EXPO/XMP is off. The rating is the larger of what the firmware reports and what the part number says
    /// (many boards report the JEDEC speed as the module's own). Desktop DIMMs only.
    /// </summary>
    internal static (int Runs, int Rated)? SlowRam(IReadOnlyList<Dimm> dimms)
    {
        var fitted = dimms.Where(d => d.Configured > 0 && d.FormFactor == DimmFormFactor).ToList();
        if (fitted.Count == 0 || fitted.Count != dimms.Count(d => d.Configured > 0)) return null;
        var runs = fitted.Min(d => d.Configured);
        var rated = fitted.Min(d => Math.Max(d.Speed, RatedFromPart(d.Part) ?? 0));
        return rated - runs >= 100 && Jedec.Any(speed => Math.Abs(speed - runs) <= 2) ? (runs, rated) : null;
    }

    /// <summary>The EXPO/XMP speed an enthusiast kit's part number spells out (Corsair, G.Skill, Kingston, Crucial, Patriot, ADATA, TeamGroup), else null.</summary>
    internal static int? RatedFromPart(string? part)
    {
        if (string.IsNullOrWhiteSpace(part) || PartRating().Match(part.Trim()) is not { Success: true } m) return null;
        var mt = m.Groups["mt"].Success ? int.Parse(m.Groups["mt"].Value)
            : m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value) * 100
            : int.Parse(m.Groups["t"].Value) * 10;
        return mt is >= 1600 and <= 12000 ? mt : null;
    }

    /// <summary>
    /// Resizable BAR is off when a card that supports it (GeForce RTX 30 and newer, Radeon RX 6000 and newer, Intel Arc)
    /// has its own memory and Direct3D 12 says the CPU can't see all of it (no GPU upload heaps). An integrated GPU, an
    /// older card or a Windows that can't say shows nothing.
    /// </summary>
    internal static bool ReBarOff(GpuBar? gpu) => gpu is { Uma: false, UploadHeap: false } && ReBarCard().IsMatch(gpu.Name);

    internal static bool ChipsetBehind(string? installed, string? latest) =>
        Version.TryParse(installed, out var have) && Version.TryParse(latest, out var newest) && newest > have;

    /// <summary>The newest chipset package AMD's page links to (its installer is named after its version), else null.</summary>
    internal static string? LatestChipset(string html) =>
        ChipsetLink().Matches(html).Select(m => m.Groups[1].Value).Where(v => Version.TryParse(v, out _)).MaxBy(Version.Parse);

    /// <summary>Game Mode is off only when its Settings switch wrote 0; Windows 11 has it on until then.</summary>
    internal static bool GameModeOff(object? value) => value is int i && i == 0;

    private delegate void Visit(int type, ReadOnlySpan<byte> structure, List<string> strings);

    /// <summary>
    /// Each structure in SMBIOS's raw table (GetSystemFirmwareTable's 'RSMB' block: an 8-byte header, then the
    /// structures) with its strings. A cut-off or malformed table ends the walk where it breaks.
    /// </summary>
    private static void Walk(ReadOnlySpan<byte> raw, Visit visit)
    {
        if (raw.Length < 8) return;
        var table = raw[8..];
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        if (declared < table.Length) table = table[..(int)declared];
        var at = 0;
        while (at + 4 <= table.Length)
        {
            int type = table[at], length = table[at + 1];
            if (length < 4 || at + length > table.Length) break;
            // The strings follow the formatted part, each ending in 0 and the set in a second 0 (two 0s when there are none).
            var strings = new List<string>();
            var end = at + length;
            while (end < table.Length)
            {
                var zero = table[end..].IndexOf((byte)0);
                if (zero < 0) { end = table.Length; break; }
                if (zero == 0) { end += strings.Count == 0 ? 2 : 1; break; }
                strings.Add(Encoding.ASCII.GetString(table.Slice(end, zero)));
                end += zero + 1;
            }
            visit(type, table.Slice(at, length), strings);
            if (type == 127) break; // end of table
            at = end;
        }
    }

    /// <summary>The memory modules in SMBIOS (type 17). Empty slots are left out.</summary>
    internal static List<Dimm> ParseDimms(ReadOnlySpan<byte> raw)
    {
        var dimms = new List<Dimm>();
        Walk(raw, (type, s, strings) =>
        {
            if (type != 17 || s.Length < 0x1B || U16(s, 0x0C) == 0) return;
            var speed = U16(s, 0x15);
            if (speed == 0xFFFF) speed = (int)U32(s, 0x54);
            var configured = U16(s, 0x20);
            if (configured == 0xFFFF) configured = (int)U32(s, 0x58);
            var part = s[0x1A] is > 0 and var index && index <= strings.Count ? strings[index - 1].Trim() : null;
            dimms.Add(new Dimm(speed, configured, part, s[0x0E]));
        });
        return dimms;
    }

    /// <summary>The system enclosure's chassis type (SMBIOS type 3, lock bit dropped), else null.</summary>
    internal static byte? ChassisType(ReadOnlySpan<byte> raw)
    {
        byte? chassis = null;
        Walk(raw, (type, s, _) => { if (type == 3 && s.Length > 5 && chassis is null) chassis = (byte)(s[5] & 0x7F); });
        return chassis;
    }

    private static int U16(ReadOnlySpan<byte> s, int offset) => s.Length >= offset + 2 ? BinaryPrimitives.ReadUInt16LittleEndian(s[offset..]) : 0;
    private static uint U32(ReadOnlySpan<byte> s, int offset) => s.Length >= offset + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(s[offset..]) : 0;

    // ---- Reading this PC ----

    private static readonly Lazy<byte[]> Smbios = new(SmbiosTable, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<IReadOnlyList<Dimm>> Memory = new(() => ParseDimms(Smbios.Value), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<GpuBar?> Gpu = new(() => WithSmartAccessMemory(ReadGpu()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// On a Radeon, AMD's own answer (ADLX's Smart Access Memory, AMD's Resizable BAR) wins over Direct3D 12's upload
    /// heaps, which a driver may leave out with Resizable BAR on; when ADLX can't say, Direct3D 12's answer stays.
    /// </summary>
    private static GpuBar? WithSmartAccessMemory(GpuBar? gpu) =>
        gpu is { Uma: false } && AmdAdlx.Present && RadeonName().IsMatch(gpu.Name) && AmdAdlx.SmartAccessMemory() is { } sam
            ? gpu with { UploadHeap = sam } : gpu;
    private static int _logged;

    /// <summary>Exo's plan for this PC: its processor's class (CPUID and name) and whether it's a laptop.</summary>
    internal static PowerProfiles.Profile ThisPcProfile() =>
        PowerProfiles.For(ClassOf(CpuName(), CpuHybrid()), Laptop(NativePower.HasBattery(), ChassisType(Smbios.Value)));

    internal static async Task<Reading> ReadAsync(PowerPlan power, bool chipsetAuto, CancellationToken ct)
    {
        var cpu = CpuName();
        var chipset = Services.ChipsetUpdate.For(cpu);
        var installed = chipset?.Installed();
        string? latest = null;
        if (chipset is not null && installed is not null)
        {
            try { latest = (await chipset.LatestAsync().WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false))?.Version; }
            catch (TimeoutException) { /* no row this time; the answer is kept when it lands */ }
        }
        object? gameMode = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            gameMode = key?.GetValue("AutoGameModeEnabled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* left as on */ }
        var update = installed is null ? null : chipset!.Current(latest, Services.ChipsetUpdate.BootedAt());
        var reading = new Reading(Memory.Value, Gpu.Value, cpu, power.Read(), installed, latest, GameModeOff(gameMode),
            update, chipsetAuto && installed is not null && !chipset!.WindowsAsks());
        if (Interlocked.Exchange(ref _logged, 1) == 0) AppLog.Info("PC health: " + Describe(reading));
        return reading;
    }

    /// <summary>One app.log line of what was read, so a row that did or didn't show can be explained.</summary>
    internal static string Describe(Reading r)
    {
        var ram = r.Dimms.Count == 0 ? "RAM not reported"
            : $"RAM {string.Join(", ", r.Dimms.Select(d => $"{d.Configured}/{d.Speed} MT/s{(RatedFromPart(d.Part) is { } p ? $" (part {p})" : "")}"))}";
        var bar = r.Gpu is null ? "GPU not read" : $"{r.Gpu.Name}: {(r.Gpu.Uma ? "shared memory" : r.Gpu.UploadHeap switch { true => "Resizable BAR on", false => "Resizable BAR off", null => "Resizable BAR unknown" })}";
        var plan = r.Power is null ? "plan not read"
            : $"plan {r.Power.Active} overlay {r.Power.Overlay}{(r.Power.Exo ? " (Exo's)" : "")} class {PowerProfiles.Keys[ClassOf(r.Cpu, null)]} max {r.Power.Cpu?.MaxState?.ToString() ?? "-"}% boost {r.Power.Cpu?.BoostMode?.ToString() ?? "-"} min cores {r.Power.Cpu?.MinCores?.ToString() ?? "-"}%";
        return $"{ram}; {bar}; {plan}; chipset {r.ChipsetInstalled ?? "-"} (newest {r.ChipsetLatest ?? "-"}{(r.ChipsetUpdate is { } u ? $", update {u.Phase} {u.Version}" : "")}); Game Mode {(r.GameModeOff ? "off" : "on")}.";
    }

    /// <summary>The processor's name as Windows has it ("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics"), or null.</summary>
    internal static string? CpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    [GeneratedRegex(@"amd_chipset_software_(\d+\.\d+\.\d+\.\d+)\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex ChipsetLink();

    [GeneratedRegex(@"\b(?:7950|7900|9950|9900)X3D|\b(?:7945|9955)HX3D", RegexOptions.IgnoreCase)]
    private static partial Regex DualCcdX3d();

    // Zen 5's desktop 9900X and 9950X: AMD's chipset driver parks their second CCD in Game Mode, as on X3D (AnandTech, Gamers Nexus).
    [GeneratedRegex(@"\b99[05]0X\b", RegexOptions.IgnoreCase)]
    private static partial Regex DualCcdZen5();

    // Ryzen 9 3900/3950, 5900/5950, 7900/7950, 9900/9950 (X, XT or neither), every Threadripper, the 12- and 16-core HX laptops, and Ryzen AI Max 390/395.
    [GeneratedRegex(@"\b(?:39|59|79|99)[05]0(?:XT?)?\b|Threadripper|\b(?:7845|7945|9850|9955)HX\b|\bAI\s+MAX\+?\s+39[05]\b", RegexOptions.IgnoreCase)]
    private static partial Regex DualCcd();

    // Intel with E-cores, when CPUID can't say: Core Ultra, and 12th to 14th gen i5/i7/i9 except the E-core-less i5-12400/12500 family.
    [GeneratedRegex(@"Core\(?T?M?\)?\s*Ultra|\bi[79]-1[2-4]\d{3}|\bi5-1(?:2[6-9]|[34]\d)\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex HybridIntel();

    // "(R)", "(TM)", "11th Gen", "AMD", "Intel".
    [GeneratedRegex(@"\((?:R|TM)\)|\b\d+(?:st|nd|rd|th)\s+Gen\b|\bAMD\b|\bIntel\b", RegexOptions.IgnoreCase)]
    private static partial Regex CpuNoise();

    // " 6-Core Processor", " with Radeon Graphics", " CPU @ 3.60GHz", " Processor".
    [GeneratedRegex(@"\s+(?:\d+-Core\s+Processor|with\s+Radeon.*|CPU\b.*|@.*|Processor\b.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CpuTail();

    [GeneratedRegex(@"\bRTX\s*[3-9]0[5-9]0|\bRX\s*[6-9]\d{3}|\bArc\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReBarCard();

    [GeneratedRegex(@"\b(?:AMD|Radeon)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonName();

    // Corsair CMK16GX4M2D3600C18, G.Skill F5-6000J3038F16G, Kingston KF3600C17 / KF436C16 / HX436C17, Crucial BL8G36C16 / CP16G60C36,
    // Patriot PVS416G360C8, ADATA AX4U360038G18, TeamGroup TF3D416G3600HC18 (mt: MT/s, h: hundreds, t: tens).
    [GeneratedRegex(@"^(?:CM\w*?X[45]M\d[A-Z]?(?<mt>\d{4})C\d|F[45]-(?<mt>\d{4})[A-Z]\d|KF(?<mt>\d{4})C|(?:KF|HX)[45](?<h>\d{2})C|(?:BL|CP)\w*?G(?<h>\d{2})C|PV\w*?G(?<t>\d{3})C|AX[45]U(?<mt>\d{4})|\w*?(?<mt>\d{4})HC\d)", RegexOptions.IgnoreCase)]
    private static partial Regex PartRating();
}
