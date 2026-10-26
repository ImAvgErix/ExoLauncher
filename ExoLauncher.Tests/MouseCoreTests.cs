using System.Buffers.Binary;
using ExoLauncher.Services;
using static ExoLauncher.Services.MouseCore;
using Device = ExoLauncher.Services.InterruptMode.Device;
using Mode = ExoLauncher.Services.InterruptMode.Mode;
using RegValue = ExoLauncher.Services.InterruptMode.RegValue;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the mouse's CPU core could go wrong silently:
/// - the CPU picked is on physical core 0 (or its SMT sibling), on the busiest DPC core, on a core another device is
///   pinned to, an Intel E-core, or on an X3D's smaller-L3 CCD (or, with two equal CCDs, not CPU 0's); a PC with more
///   than 64 CPUs or no free core gets one anyway;
/// - the mask is the wrong bytes (big-endian, 4 bytes, the CPU's number instead of its bit), reg.exe gets the wrong
///   type for a name, or a value for a name Exo doesn't own;
/// - a report a little late, a slot the mouse skipped because it moved slowly, or the hand stopping counts as a gap,
///   or a real missed slot doesn't; the polling rate is misread; gaps at random are said to come with the refresh;
/// - the CPU that took the mouse's interrupts is misread, or the mouse's own DPCs make its CPU look the busiest;
/// - the fix is offered with no real gaps, with the input already elsewhere, for a Bluetooth mouse, a dock's
///   controller, the drive's chain or over another tool's values;
/// - the backup is written after the change, a declined prompt leaves one, Undo writes a value where there was none
///   (or touches DevicePriority), a second change loses the original values;
/// - "Restart to finish" stays after the restart, a value a BIOS update cleared still reads as set, Windows not moving
///   the interrupts goes unsaid, or before and after are mixed up.
/// </summary>
public sealed class MouseCoreTests : IDisposable
{
    private const long F = 10_000_000; // Stopwatch ticks a second, as on Windows
    private const string Mouse = @"HID\VID_046D&PID_C547&MI_01&COL01\8&2F5B1B8A&0&0000";
    private const string MouseUsb = @"USB\VID_046D&PID_C547&MI_01\7&1&0&0001";
    private const string Hub = @"USB\ROOT_HUB30\5&1&0&0";
    private const string Usb = @"PCI\VEN_1022&DEV_15B6&SUBSYS_88771043&REV_00\4&2&0&0341";
    private const string RootPort = @"PCI\VEN_1022&DEV_1483&SUBSYS_88081043&REV_00\3&1&0&09";
    private const string Gpu = @"PCI\VEN_10DE&DEV_2484&SUBSYS_00001043&REV_A1\4&1&0&0019";
    private const string Nvme = @"PCI\VEN_144D&DEV_A80C&SUBSYS_A801144D&REV_00\4&5&0&0009";
    private const string NvmePort = @"PCI\VEN_1022&DEV_1483&SUBSYS_88081043&REV_00\3&1&0&0A";
    private const string Disk = @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG\5&1&0&000000";
    private const string Acpi = @"ACPI\PNP0A08\0";

    private readonly string _folder = Directory.CreateTempSubdirectory("exo-mousecore-").FullName;
    private string BackupPath => Path.Combine(_folder, "mouse-core-backup.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Device Pci(string id, byte cls, byte sub, byte progIf, string name = "PCI device", bool local = true, int? priority = null,
        int? policy = null, byte[]? mask = null, bool odd = false, ulong? affinity = null) =>
        new(id, name, "drv", 0, cls, sub, progIf, 7, 8, 0x00FF2001, Mode.Msi, 1, priority, policy, mask, odd, affinity, local);

    private static Device Xhci(int? priority = 3, bool local = true, string name = "USB xHCI Compliant Host Controller") =>
        Pci(Usb, 0x0C, 0x03, 0x30, name, local, priority, affinity: ulong.MaxValue >> 48);

    private sealed class FakeDevices : InterruptMode.IDevices
    {
        public List<Device> Devices = [];
        public List<string> Boot = [Disk];
        public Dictionary<string, string> Parents = new(StringComparer.OrdinalIgnoreCase)
        {
            [Mouse] = MouseUsb, [MouseUsb] = Hub, [Hub] = Usb, [Usb] = RootPort, [RootPort] = Acpi,
            [Disk] = Nvme, [Nvme] = NvmePort, [NvmePort] = Acpi,
        };
        public IReadOnlyList<Device> List() => Devices.ToList();
        public IReadOnlyList<string> BootDisks() => Boot;
        public string? Parent(string id) => Parents.GetValueOrDefault(id);
    }

    /// <summary>The registry: a write sets the controller's two values (null deletes), unless declined or Windows ignores it.</summary>
    private sealed class FakeWriter(FakeDevices devices, string backupPath) : IWriter
    {
        public List<(string Id, int? Policy, byte[]? Mask)> Calls = [];
        public bool Decline;
        public bool Ignore;
        public string? BackupAtWrite;

        public string? Write(string id, int? policy, byte[]? mask)
        {
            BackupAtWrite = File.Exists(backupPath) ? File.ReadAllText(backupPath) : null;
            Calls.Add((id, policy, mask));
            if (Decline) return "Windows' permission was declined, so nothing changed.";
            if (Ignore) return null;
            var at = devices.Devices.FindIndex(d => d.Id == id);
            devices.Devices[at] = devices.Devices[at] with { Policy = policy, Override = mask };
            return null;
        }
    }

    private DateTimeOffset _boot = DateTimeOffset.UtcNow.AddHours(-1);
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    private (MouseCore Core, FakeDevices Devices, FakeWriter Writer) Make(params Device[] list)
    {
        var devices = new FakeDevices { Devices = [.. list] };
        var writer = new FakeWriter(devices, BackupPath);
        return (new MouseCore(devices, writer, BackupPath, () => _boot, () => _now), devices, writer);
    }

    /// <summary>Windows restarts: a controller with DevicePolicy 4 gets its interrupts aimed at its mask, unless <paramref name="ignored"/>.</summary>
    private void Restart(FakeDevices devices, bool ignored = false)
    {
        _boot = _now.AddMinutes(1);
        _now = _boot.AddMinutes(1);
        devices.Devices = devices.Devices.Select(d => d with { Affinity = d.Policy == 4 && d.Override is { } m && !ignored ? Bits(m) : ulong.MaxValue >> 48 }).ToList();
    }

    // ---- Builders ----

    /// <summary>Reports at the rate's slot for <paramref name="seconds"/> from <paramref name="from"/> s, each moving <paramref name="counts"/>; the slots in <paramref name="miss"/> never came.</summary>
    private static List<Report> Moving(int rate, double seconds, int counts = 4, IEnumerable<int>? miss = null, double from = 1, Func<int, long>? shift = null)
    {
        var skip = miss?.ToHashSet() ?? [];
        var slot = F / rate;
        var list = new List<Report>();
        for (var i = 0; i < (int)(seconds * rate); i++)
            if (!skip.Contains(i)) list.Add(new Report((long)(from * F) + i * slot + (shift?.Invoke(i) ?? 0), counts, -counts / 2));
        return list;
    }

    private static IEnumerable<int> Spread(int count) => Enumerable.Range(0, count).Select(i => 300 + i * 211);

    /// <summary>A quarter-second stretch at a time over 12 s: every CPU 500 interrupts a second, CPU <paramref name="on"/> one more per report, DPC time from <paramref name="dpc"/>.</summary>
    private static List<Window> Windows(IReadOnlyList<Report> reports, int cpus, int on, Func<int, double, double>? dpc = null, double seconds = 12)
    {
        var list = new List<Window>();
        var step = F / 4;
        for (long from = 0; from + step <= (long)(seconds * F); from += step)
        {
            var rate = reports.Count(r => r.At >= from && r.At < from + step) * 4.0;
            list.Add(new Window(from, from + step,
                Enumerable.Range(0, cpus).Select(c => 500.0 + (c == on ? rate : 0)).ToArray(),
                Enumerable.Range(0, cpus).Select(c => dpc?.Invoke(c, rate) ?? 0.02).ToArray()));
        }
        return list;
    }

    private static ulong All(int cpus) => cpus >= 64 ? ulong.MaxValue : (1UL << cpus) - 1;

    /// <summary><paramref name="cores"/> cores with SMT (CPUs 2n and 2n+1) under one L3.</summary>
    private static Topology Smt(int cores, long l3 = 32 << 20) =>
        new(Enumerable.Range(0, cores).Select(c => new Core([2 * c, 2 * c + 1], 0)).ToList(), [new Cache(All(cores * 2), l3)], cores * 2);

    /// <summary>A test with 40 slots missed at 1000 Hz, the mouse's interrupts on <paramref name="on"/>, driver work on <paramref name="busy"/>.</summary>
    private static Sample Run(int on, int missed = 40, int busy = 0, Topology? topology = null, string? mouse = Mouse)
    {
        var reports = Moving(1000, 10, miss: Spread(missed));
        topology ??= Smt(8, 96 << 20);
        return new Sample(reports, mouse, F, 144, Windows(reports, topology.Logical, on, (c, _) => c == busy ? 2.0 : 0.05), topology);
    }

    // ---- Core choice ----

    /// <summary>Interrupts falling with the CPU's number: higher CPUs are quieter, so a pick that ignores a rule lands high.</summary>
    private static double[] Falling(int cpus) => Enumerable.Range(0, cpus).Select(c => 2000.0 - c * 10).ToArray();

    [Fact]
    public void ARyzen9800X3DGetsItsQuietestCoreButNeverCoreZeroTheBusiestOrAPinnedOne()
    {
        var t = Smt(8, 96 << 20);
        var none = new double[16];
        Assert.Equal(14, Choose(t, none, Falling(16), busiest: null, pinned: 0));
        Assert.Equal(12, Choose(t, none, Falling(16), busiest: 15, pinned: 0)); // CPU 15's core is the busiest: its sibling 14 goes too
        Assert.Equal(10, Choose(t, none, Falling(16), busiest: 15, pinned: 1UL << 13)); // the GPU is pinned to CPU 13
        // Core 0 is never taken, however quiet.
        var quietZero = Enumerable.Range(0, 16).Select(c => c < 2 ? 0.0 : 1000).ToArray();
        Assert.Equal(2, Choose(t, none, quietZero, busiest: null, pinned: 0));
        // DPC time leads; a few hundredths of a percent don't.
        var dpc = Enumerable.Range(0, 16).Select(c => c == 14 ? 0.8 : c == 12 ? 0.04 : 0).ToArray();
        Assert.Equal(12, Choose(t, dpc, Falling(16), busiest: null, pinned: 0));
    }

    [Fact]
    public void ARyzen7950X3DStaysOnTheVCacheCcd()
    {
        var cores = Enumerable.Range(0, 16).Select(c => new Core([2 * c, 2 * c + 1], 0)).ToList();
        var t = new Topology(cores, [new Cache(All(16), 96 << 20), new Cache(All(32) & ~All(16), 32 << 20)], 32);
        // The other CCD's cores are quieter, but the larger L3 is the one games run on.
        Assert.Equal(14, Choose(t, new double[32], Falling(32), busiest: null, pinned: 0));
        // The V-Cache CCD listed second, and CPU 0 on the other one: still the largest L3.
        var swapped = new Topology(cores, [new Cache(All(16), 32 << 20), new Cache(All(32) & ~All(16), 96 << 20)], 32);
        Assert.Equal(30, Choose(swapped, new double[32], Falling(32), busiest: null, pinned: 0));
    }

    [Fact]
    public void TwoEqualCcdsKeepTheOneWithCpuZero()
    {
        var cores = Enumerable.Range(0, 16).Select(c => new Core([2 * c, 2 * c + 1], 0)).ToList();
        // A 7950X: two 32 MB L3s, CPU 0's listed second.
        var t = new Topology(cores, [new Cache(All(32) & ~All(16), 32 << 20), new Cache(All(16), 32 << 20)], 32);
        Assert.Equal(14, Choose(t, new double[32], Falling(32), busiest: null, pinned: 0));
    }

    [Fact]
    public void AnIntelHybridGetsAPCoreNeverAnECore()
    {
        // 8 P-cores with SMT (CPUs 0-15, class 1) and 12 E-cores (CPUs 16-27, class 0), one L3.
        var cores = Enumerable.Range(0, 8).Select(c => new Core([2 * c, 2 * c + 1], 1))
            .Concat(Enumerable.Range(16, 12).Select(c => new Core([c], 0))).ToList();
        var t = new Topology(cores, [new Cache(All(28), 33 << 20)], 28);
        Assert.Equal(14, Choose(t, new double[28], Falling(28), busiest: null, pinned: 0));
        // Every P-core but core 0 taken: no E-core instead.
        Assert.Null(Choose(t, new double[28], Falling(28), busiest: null, pinned: All(16) & ~All(2)));
    }

    [Fact]
    public void WithSmtOffEachCpuIsItsOwnCore()
    {
        var t = new Topology(Enumerable.Range(0, 8).Select(c => new Core([c], 0)).ToList(), [new Cache(All(8), 32 << 20)], 8);
        Assert.Equal(6, Choose(t, new double[8], Falling(8), busiest: 7, pinned: 0));
        Assert.Equal(1, Choose(t, new double[8], Enumerable.Repeat(100.0, 8).ToArray(), busiest: null, pinned: 0));
    }

    [Fact]
    public void NoCpuForMoreThan64OrWhenNoneIsFree()
    {
        var big = Smt(32) with { Logical = 128 };
        Assert.Null(Choose(big, new double[64], Falling(64), busiest: null, pinned: 0));
        var two = Smt(2);
        Assert.Null(Choose(two, new double[4], Falling(4), busiest: 2, pinned: 0));
    }

    [Fact]
    public void TheCpusAreReadFromWindowsRecords()
    {
        static byte[] CoreRecord(ulong mask, byte efficiency, ushort group = 0)
        {
            var r = new byte[48];
            BinaryPrimitives.WriteInt32LittleEndian(r, 0);
            BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(4), 48);
            r[9] = efficiency;
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(30), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(32), mask);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(40), group);
            return r;
        }
        static byte[] CacheRecord(ulong mask, uint size, byte level)
        {
            var r = new byte[56];
            BinaryPrimitives.WriteInt32LittleEndian(r, 2);
            BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(4), 56);
            r[8] = level;
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), size);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(38), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(40), mask);
            return r;
        }
        var data = CoreRecord(0b11, 1).Concat(CoreRecord(0b1100, 1)).Concat(CoreRecord(0b10000, 0))
            .Concat(CacheRecord(0b11111, 2 << 20, 2)).Concat(CacheRecord(0b11111, 36 << 20, 3)).ToArray();
        var t = Parse(data, 5);
        Assert.Equal([[0, 1], [2, 3], [4]], t.Cores.Select(c => c.Threads));
        Assert.Equal([1, 1, 0], t.Cores.Select(c => (int)c.Efficiency));
        Assert.Equal(36 << 20, Assert.Single(t.L3).Bytes);
        Assert.Equal(5, t.Logical);
        // A second processor group: more than 64 CPUs, whatever the count said.
        Assert.True(Parse(data.Concat(CoreRecord(1, 0, group: 1)).ToArray(), 5).Logical > 64);
    }

    // ---- Mask bytes and reg.exe ----

    [Fact]
    public void TheMaskIsAKaffinityLittleEndian()
    {
        Assert.Equal(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0 }, Mask(0));
        Assert.Equal(new byte[] { 0x40, 0, 0, 0, 0, 0, 0, 0 }, Mask(6));
        Assert.Equal(new byte[] { 0, 0x20, 0, 0, 0, 0, 0, 0 }, Mask(13));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0x80 }, Mask(63));
        for (var cpu = 0; cpu < 64; cpu++) Assert.Equal(1UL << cpu, Bits(Mask(cpu)));
        Assert.Equal(0x3UL, Bits([0x03]));
    }

    [Fact]
    public void RegExeGetsEachNameWithItsOwnType()
    {
        const string reg = @"C:\Windows\System32\reg.exe";
        const string key = "\"HKLM\\SYSTEM\\CurrentControlSet\\Enum\\" + Usb + "\\Device Parameters\\Interrupt Management\\Affinity Policy\"";
        Assert.Equal($"\"{reg}\" add {key} /v AssignmentSetOverride /t REG_BINARY /d 4000000000000000 /f /reg:64",
            InterruptMode.RegCommand(reg, new RegValue(Usb, OverrideName, Mask(6))));
        Assert.Equal($"\"{reg}\" add {key} /v DevicePolicy /t REG_DWORD /d 4 /f /reg:64", InterruptMode.RegCommand(reg, new RegValue(Usb, PolicyName, 4)));
        Assert.Equal($"\"{reg}\" delete {key} /v DevicePolicy /f /reg:64", InterruptMode.RegCommand(reg, new RegValue(Usb, PolicyName, null)));
        Assert.Equal($"\"{reg}\" delete {key} /v AssignmentSetOverride /f /reg:64", InterruptMode.RegCommand(reg, new RegValue(Usb, OverrideName, null)));
        // DevicePriority stays under the same key, MSISupported under its own.
        Assert.Equal($"\"{reg}\" add {key} /v DevicePriority /t REG_DWORD /d 3 /f /reg:64", InterruptMode.RegCommand(reg, new RegValue(Usb, InterruptMode.PriorityName, 3)));
        Assert.Contains("MessageSignaledInterruptProperties\" /v MSISupported /t REG_DWORD /d 1 ", InterruptMode.RegCommand(reg, new RegValue(Usb, InterruptMode.MsiName, 1)));
        // The wrong type for a name, an empty or oversized mask, a name Exo doesn't own, an id cmd would read: nothing.
        Assert.Null(InterruptMode.RegCommand(reg, new RegValue(Usb, OverrideName, 64)));
        Assert.Null(InterruptMode.RegCommand(reg, new RegValue(Usb, PolicyName, Mask(6))));
        Assert.Null(InterruptMode.RegCommand(reg, new RegValue(Usb, OverrideName, Array.Empty<byte>())));
        Assert.Null(InterruptMode.RegCommand(reg, new RegValue(Usb, OverrideName, new byte[65])));
        Assert.Null(InterruptMode.RegCommand(reg, new RegValue(Usb, "MessageNumberLimit", 1)));
        Assert.Null(InterruptMode.RegCommand(reg, new RegValue(Usb + "\" & calc", PolicyName, 4)));
    }

    // ---- Gaps against the slot ----

    [Fact]
    public void AMissedSlotIsAGapAndALateReportIsNot()
    {
        var clean = Analyze(Moving(1000, 8), F, 144);
        Assert.Equal(1000, clean.Rate);
        Assert.Equal(0, clean.Missed);
        Assert.InRange(clean.LongestMs, 0.99, 1.01);
        Assert.True(clean.Checked > 7900);

        var missed = Analyze(Moving(1000, 8, miss: Spread(20)), F, 144);
        Assert.Equal(20, missed.Missed);
        Assert.InRange(missed.LongestMs, 1.99, 2.01);
        Assert.InRange(missed.P999Ms, 1.99, 2.01);

        // Every 50th report 0.4 ms late (1.4 ms, then 0.6): not a missed slot.
        var late = Analyze(Moving(1000, 8, shift: i => i % 50 == 0 ? F / 2500 : 0), F, 144);
        Assert.Equal(0, late.Missed);
        Assert.InRange(late.LongestMs, 1.39, 1.41);
    }

    [Fact]
    public void SlowMovesAndStillnessAreNotGaps()
    {
        // One count a report with every third slot empty: the mouse had nothing to send.
        var slow = Analyze(Moving(1000, 8, counts: 1, miss: Enumerable.Range(0, 8000).Where(i => i % 3 == 0)), F, 144);
        Assert.Equal(0, slow.Missed);
        Assert.True(slow.Checked < 200);
        // Two bursts of circles with the hand still for 300 ms between.
        var still = Analyze([.. Moving(1000, 3), .. Moving(1000, 3, from: 4.3)], F, 144);
        Assert.Equal(0, still.Missed);
        Assert.InRange(still.LongestMs, 0.99, 1.01);
    }

    [Fact]
    public void ThePollingRateIsRead()
    {
        Assert.Equal(125, Analyze(Moving(125, 8, counts: 30), F, 144).Rate);
        Assert.Equal(500, Analyze(Moving(500, 8, counts: 8), F, 144).Rate);
        Assert.Equal(4000, Analyze(Moving(4000, 4), F, 144).Rate);
        var fast = Analyze(Moving(8000, 4, miss: Enumerable.Range(0, 30).Select(i => 1000 + i * 977)), F, 144);
        Assert.Equal(8000, fast.Rate);
        // At 8 kHz a single missed slot is under Exo's reading jitter: three slots are a gap.
        Assert.Equal(0, fast.Missed);
        var stall = Analyze(Moving(8000, 4, miss: Enumerable.Range(0, 30).SelectMany(i => new[] { 1000 + i * 977, 1001 + i * 977, 1002 + i * 977 })), F, 144);
        Assert.Equal(30, stall.Missed);
    }

    [Fact]
    public void GapsThatComeWithTheRefreshAreSaidSo()
    {
        // A missed slot every 10 frames at 144 Hz.
        var beat = Analyze(Moving(1000, 8, miss: Enumerable.Range(1, 100).Select(i => (int)Math.Round(i * 1000 * 10 / 144.0))), F, 144);
        Assert.True(beat.Missed > 50);
        Assert.True(beat.OnRefresh);
        // 59.94 Hz read as 60: still on the beat from one gap to the next.
        var ntsc = Analyze(Moving(1000, 8, miss: Enumerable.Range(1, 90).Select(i => (int)Math.Round(i * 1000 * 5 / 59.94))), F, 60);
        Assert.True(ntsc.OnRefresh);
        // At random: no.
        var random = new Random(7);
        var scattered = Analyze(Moving(1000, 8, miss: Enumerable.Range(0, 60).Select(_ => random.Next(100, 7900)).Distinct()), F, 144);
        Assert.True(scattered.Missed > 40);
        Assert.False(scattered.OnRefresh);
        // A 125 Hz mouse's slot is longer than a quarter frame: not judged.
        Assert.False(OnBeat([0, 694_444, 1_388_888, 2_083_333, 2_777_777, 3_472_222], F, 144, F / 125.0));
    }

    // ---- Which CPU ----

    [Fact]
    public void TheCpuWhoseInterruptsFollowTheReportsIsTheMouses()
    {
        var reports = Moving(1000, 10);
        Assert.Equal(3, Attribute(reports, Windows(reports, 8, on: 3), F).Cpu);
        // The mouse's own DPCs on CPU 3 don't make it the busiest: CPU 0's steady driver work is.
        var work = Attribute(reports, Windows(reports, 8, on: 3, dpc: (c, rate) => c == 3 ? 0.5 + rate * 0.003 : c == 0 ? 1.5 : 0.02), F);
        Assert.Equal(3, work.Cpu);
        Assert.InRange(work.Dpc[3], 0.49, 0.51);
        Assert.Equal(0, Busiest(work.Dpc));
        // Moving the whole time with no still moment: nothing to tell by.
        var steady = Moving(1000, 12, from: 0);
        Assert.Null(Attribute(steady, Windows(steady, 8, on: 3), F).Cpu);
        Assert.Null(Busiest([0.01, 0.02, 0.05]));
    }

    // ---- The verdict ----

    [Fact]
    public void TheFixIsOfferedOnlyForRealGapsOnCpuZeroOrTheBusiestCore()
    {
        var (core, _, _) = Make(Xhci(), Pci(Gpu, 0x03, 0, 0, "GPU"));
        var fix = core.Judge(Run(on: 0));
        Assert.Equal(Verdict.Fix, fix.Verdict);
        Assert.Equal(2, fix.Offer);
        Assert.Equal(Usb, fix.Controller);
        Assert.Contains("CPU 0, where Windows and most drivers go first. CPU 2 is quiet.", fix.Detail);

        // On CPU 1, core 0's sibling: the same.
        Assert.Equal(Verdict.Fix, core.Judge(Run(on: 1)).Verdict);
        // On the busiest DPC core's CPU.
        var busy = core.Judge(Run(on: 9, busy: 8));
        Assert.Equal(Verdict.Fix, busy.Verdict);
        Assert.Contains("the one busiest with driver work", busy.Detail);
        Assert.NotEqual(8, busy.Offer);
        Assert.NotEqual(9, busy.Offer);
        // Elsewhere: another CPU wouldn't help.
        var away = core.Judge(Run(on: 6, busy: 0));
        Assert.Equal(Verdict.Fine, away.Verdict);
        Assert.Null(away.Offer);
        // No real gaps: nothing to change, wherever the input lands.
        Assert.Equal(Verdict.Fine, core.Judge(Run(on: 0, missed: 0)).Verdict);
        Assert.Equal(Verdict.Fine, core.Judge(Run(on: 0, missed: 4)).Verdict);
        // No mouse moved, or too slowly to judge.
        Assert.Equal(Verdict.Retry, core.Judge(Run(on: 0, mouse: null)).Verdict);
    }

    [Fact]
    public void BluetoothDocksTheDrivesChainAndOtherToolsAreRefused()
    {
        var (core, devices, _) = Make(Xhci());
        // A Bluetooth LE mouse on a USB radio on that controller.
        devices.Parents[Mouse] = @"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&B023\9&1&0";
        devices.Parents[@"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&B023\9&1&0"] = @"BTHLE\DEV_C1A2B3C4D5E6\8&1&0";
        devices.Parents[@"BTHLE\DEV_C1A2B3C4D5E6\8&1&0"] = MouseUsb;
        Assert.Contains("Bluetooth", Refused(core.Judge(Run(on: 0))));

        (core, devices, _) = Make(Xhci(local: false));
        Assert.Contains("dock", Refused(core.Judge(Run(on: 0))));
        (core, _, _) = Make(Xhci(), Pci(RootPort, 0x06, 0x04, 0, "Thunderbolt(TM) PCI Express Root Port"));
        Assert.Contains("dock", Refused(core.Judge(Run(on: 0))));

        (core, devices, _) = Make(Xhci());
        devices.Parents[Disk] = Hub; // Windows on a USB drive on the same controller
        Assert.Contains("Windows starts from a drive", Refused(core.Judge(Run(on: 0))));
        devices.Boot = [];
        Assert.Contains("couldn't find the drive", Refused(core.Judge(Run(on: 0))));

        (core, _, _) = Make(Xhci() with { Policy = 4, Override = Mask(5) });
        Assert.Contains("Another tool", Refused(core.Judge(Run(on: 0))));
        (core, _, _) = Make(Xhci() with { OddAffinity = true });
        Assert.Contains("Another tool", Refused(core.Judge(Run(on: 0))));
        // Windows' default policy alone is no one's setting.
        (core, _, _) = Make(Xhci() with { Policy = 0 });
        Assert.Equal(Verdict.Fix, core.Judge(Run(on: 0)).Verdict);

        (core, _, _) = Make(Pci(Usb, 0x0C, 0x03, 0x20, "EHCI")); // not xHCI
        Assert.Contains("isn't on a USB 3 controller", Refused(core.Judge(Run(on: 0))));
        (core, _, _) = Make(Xhci());
        Assert.Contains("more than 64 CPUs", Refused(core.Judge(Run(on: 0, topology: Smt(8) with { Logical = 96 }))));

        // A GPU pinned to every free core leaves none.
        (core, _, _) = Make(Xhci(), Pci(Gpu, 0x03, 0, 0, "GPU", policy: 4, mask: BitConverter.GetBytes(All(16) & ~All(2))));
        Assert.Contains("No quiet CPU", Refused(core.Judge(Run(on: 0))));
    }

    private static string Refused(Result r)
    {
        Assert.Equal(Verdict.Cannot, r.Verdict);
        Assert.Null(r.Offer);
        return r.Detail;
    }

    // ---- Backup, undo and after the restart ----

    [Fact]
    public void ApplyBacksUpFirstAndUndoPutsBackExactly()
    {
        var (core, devices, writer) = Make(Xhci(priority: 3), Pci(Gpu, 0x03, 0, 0, "GPU"));
        Assert.Equal("Test your mouse first.", core.Apply());
        core.Measure(Run(on: 0));
        Assert.Null(core.Apply());
        var write = Assert.Single(writer.Calls);
        Assert.Equal((Usb, (int?)4), (write.Id, write.Policy));
        Assert.Equal(Mask(2), write.Mask);
        Assert.Contains("\"WasPolicy\":null", writer.BackupAtWrite);
        Assert.Contains("\"WasMask\":null", writer.BackupAtWrite);

        var state = core.Read();
        Assert.Equal(Stage.Restart, state.Stage);
        Assert.Equal(2, state.Cpu);
        Assert.True(state.CanUndo);
        Assert.Equal(40, state.Before?.Missed);

        Assert.Null(core.Undo());
        Assert.Equal((Usb, (int?)null, (byte[]?)null), writer.Calls[1]);
        var usb = devices.Devices.Single(d => d.Id == Usb);
        Assert.Null(usb.Policy);
        Assert.Null(usb.Override);
        Assert.Equal(3, usb.Priority); // MSI mode's priority is never Exo's to touch here
        Assert.Equal(Stage.Restart, core.Read().Stage);
        Assert.False(core.Read().CanUndo);
        Assert.Equal("There's nothing to undo.", core.Undo());

        Restart(devices);
        Assert.Equal(Stage.None, core.Read().Stage);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void UndoRestoresAnEarlierValueAndASecondChangeKeepsTheOriginal()
    {
        var (core, _, writer) = Make(Xhci() with { Policy = 0 });
        core.Measure(Run(on: 0));
        Assert.Null(core.Apply());
        // Measured again on the same controller before the restart, Exo moves it again: the original stays 0.
        core.Measure(Run(on: 0, busy: 2));
        Assert.Null(core.Apply());
        Assert.Equal(Mask(4), writer.Calls[1].Mask);
        Assert.Null(core.Undo());
        Assert.Equal((Usb, (int?)0, (byte[]?)null), writer.Calls[2]);
    }

    [Fact]
    public void ADeclinedPromptLeavesNoBackup()
    {
        var (core, _, writer) = Make(Xhci());
        core.Measure(Run(on: 0));
        writer.Decline = true;
        Assert.Equal("Windows' permission was declined, so nothing changed.", core.Apply());
        Assert.False(File.Exists(BackupPath));
        Assert.Equal(Stage.None, core.Read().Stage);
        // Windows ignoring the write leaves none either.
        writer.Decline = false;
        writer.Ignore = true;
        Assert.Equal("Windows didn't take the change.", core.Apply());
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void AfterTheRestartBeforeAndAfterAreCompared()
    {
        var (core, devices, _) = Make(Xhci());
        core.Measure(Run(on: 0));
        Assert.Null(core.Apply());
        Restart(devices);
        var set = core.Read();
        Assert.Equal(Stage.Set, set.Stage);
        Assert.Equal("CPU 2 takes your mouse's input now. Measure again to compare.", set.Detail);
        Assert.Null(set.After);

        // Measured again: input on CPU 2, five late reports instead of forty.
        Assert.Equal(Verdict.Fine, core.Measure(Run(on: 2, missed: 5)).Verdict);
        var better = core.Read();
        Assert.Equal(40, better.Before?.Missed);
        Assert.Equal(5, better.After?.Missed);
        Assert.Equal(2, better.After?.Cpu);
        Assert.True(better.Better);
        Assert.Equal("Fewer late reports since CPU 2 took your mouse's input.", better.Detail);

        // Measured once more, no better: Undo is suggested.
        core.Measure(Run(on: 2, missed: 40));
        var worse = core.Read();
        Assert.False(worse.Better);
        Assert.EndsWith("Undo puts it back.", worse.Detail);
    }

    [Fact]
    public void WindowsNotMovingItOrAnUpdateClearingItIsSaid()
    {
        var (core, devices, _) = Make(Xhci());
        core.Measure(Run(on: 0));
        Assert.Null(core.Apply());
        Restart(devices, ignored: true);
        Assert.Equal("Windows didn't move your mouse's input to CPU 2. Undo puts it back.", core.Read().Detail);

        // A driver update rewrote the controller's key.
        var at = devices.Devices.FindIndex(d => d.Id == Usb);
        devices.Devices[at] = devices.Devices[at] with { Policy = null, Override = null };
        var cleared = core.Read();
        Assert.Equal(Stage.None, cleared.Stage);
        Assert.Equal("A BIOS or driver update cleared the CPU Exo set for your mouse.", cleared.Detail);
        Assert.False(File.Exists(BackupPath));
        Assert.Equal("There's nothing to undo.", core.Undo());
    }

    [Fact]
    public void ReactNeverSeesTheInstanceIds()
    {
        var (core, _, _) = Make(Xhci());
        var result = core.Judge(Run(on: 0));
        Assert.DoesNotContain("PCI\\", result.Detail);
        Assert.DoesNotContain("VID_", result.Detail);
    }
}
