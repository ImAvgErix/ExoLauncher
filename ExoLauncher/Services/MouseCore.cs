using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ExoLauncher.Helpers;
using static ExoLauncher.Services.InterruptMode;

namespace ExoLauncher.Services;

/// <summary>
/// Exo Mouse's CPU core panel (Devices). A 10-second test reads the moving mouse's reports through Raw Input and,
/// beside them, each CPU's interrupts and driver work (DPCs): how fast the mouse polls, how many reports missed their
/// slot while it moved fast enough to fill every one, whether those gaps come with the screen's refresh, and which CPU
/// took its USB controller's interrupts (the one whose interrupts rise and fall with the reports). Only when reports
/// came late AND that CPU is CPU 0 or its sibling (where Windows and most drivers go first) or the one busiest with
/// driver work does Exo offer to send that one xHCI controller's interrupts to a quiet CPU: DevicePolicy 4
/// (IrqPolicySpecifiedProcessors) and AssignmentSetOverride (the CPU's bit) in its Affinity Policy key, in one
/// permission prompt. Anything else is "fine" and changes nothing. The values there before are backed up first (or
/// "absent") and Undo puts them back exactly; DevicePriority beside them is <see cref="InterruptMode"/>'s and never
/// touched here. Windows applies it at the next restart, which Exo never does itself; after it Exo checks the values
/// are still there and the interrupts moved, and a second test shows before and after. It refuses a Bluetooth mouse,
/// a dock's (Thunderbolt or USB4) controller, the controller on Windows' drive's path, another tool's setting and PCs
/// with more than 64 CPUs.
/// </summary>
internal sealed partial class MouseCore(IDevices devices, MouseCore.IWriter writer, string backupPath, Func<DateTimeOffset> bootTime, Func<DateTimeOffset> now)
{
    internal const string PolicyName = "DevicePolicy";
    internal const string OverrideName = "AssignmentSetOverride";
    private const int Specified = 4; // IrqPolicySpecifiedProcessors
    internal static readonly TimeSpan TestLength = TimeSpan.FromSeconds(10);
    private static readonly int[] Rates = [125, 250, 500, 1000, 2000, 4000, 8000];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>One mouse report as Exo's reader got it: when (Stopwatch ticks) and how far it moved.</summary>
    internal readonly record struct Report(long At, int Dx, int Dy);

    /// <summary>One stretch of the test: each CPU's Interrupts/sec and % DPC Time (Processor(n), by CPU number).</summary>
    internal sealed record Window(long From, long To, double[] Interrupts, double[] Dpc);

    /// <summary>A physical core: its CPUs (logical processors, two with SMT) and its efficiency class (higher is faster: an Intel P-core).</summary>
    internal sealed record Core(int[] Threads, byte Efficiency);

    /// <summary>An L3 cache: the CPUs that share it and its size. A dual-CCD Ryzen has two; an X3D's larger one is its V-Cache.</summary>
    internal sealed record Cache(ulong Mask, long Bytes);

    /// <summary>Processor group 0's cores and L3s; <paramref name="Logical"/>: every CPU in the PC (more than 64 when it has more than one group).</summary>
    internal sealed record Topology(IReadOnlyList<Core> Cores, IReadOnlyList<Cache> L3, int Logical);

    /// <summary>What a test read: the moving mouse's reports and device instance, the Stopwatch's frequency, the main screen's refresh, the counters and the CPUs.</summary>
    internal sealed record Sample(IReadOnlyList<Report> Reports, string? Mouse, long Frequency, double RefreshHz, IReadOnlyList<Window> Windows, Topology Topology);

    /// <summary>The reports measured against the polling rate's slot. <paramref name="Checked"/>: the gaps between reports that were judged (moving fast enough).</summary>
    internal sealed record Gaps(int Rate, int Checked, int Missed, double LongestMs, double P999Ms, bool OnRefresh);

    /// <summary>Which CPU took the mouse's interrupts (null: couldn't tell), and each CPU's other driver work and interrupts over the test.</summary>
    internal sealed record Work(int? Cpu, double[] Dpc, double[] Interrupts);

    /// <summary>A test as React and the backup keep it. Longest and P999 in ms.</summary>
    internal sealed record Test(int Rate, int Checked, int Missed, double Longest, double P999, bool Refresh, int? Cpu);

    /// <summary>Fix: a CPU is offered. Fine: nothing to change. Cannot: late reports, but Exo won't or can't set a CPU. Retry: the test didn't read enough.</summary>
    internal enum Verdict { Fix, Fine, Cannot, Retry }

    /// <summary>A test and what Exo makes of it: <paramref name="Offer"/>, the CPU a fix gives the <paramref name="Controller"/> (its instance id, never sent to React).</summary>
    internal sealed record Result(Test Test, Verdict Verdict, string Detail, string? Controller, int? Offer, DateTimeOffset At);

    internal interface IWriter
    {
        /// <summary>Sets one controller's DevicePolicy (REG_DWORD) and AssignmentSetOverride (REG_BINARY), null deleting either, in one step Windows asks permission for. Null when it ran.</summary>
        string? Write(string id, int? policy, byte[]? mask);
    }

    /// <summary>Exo's change: the controller, the CPU it got, the values before (null: absent; the mask as hex), the test before and the one after the restart.</summary>
    internal sealed record Saved(string Id, int Cpu, int? WasPolicy, string? WasMask, DateTimeOffset WrittenAt, Test Before, Test? After = null, bool Reverted = false);

    internal enum Stage { None, Restart, Set }

    /// <summary>Where Exo's change stands. <paramref name="Better"/>: the test after the restart beat the one before (null until there is one).</summary>
    internal sealed record State(Stage Stage, string? Detail, int? Cpu, Test? Before, Test? After, bool? Better, bool CanUndo);

    private volatile Result? _last;

    /// <summary>This session's last test, which Apply acts on.</summary>
    internal Result? Last => _last;

    // ---- The test ----

    /// <summary>Judges a test and keeps it; after a restart with Exo's CPU set on the same controller, it is the test after.</summary>
    internal Result Measure(Sample sample)
    {
        var result = Judge(sample);
        _last = result;
        if (Load() is { Reverted: false } saved && saved.WrittenAt <= bootTime() && result.Controller is { } id && Same(id, saved.Id) && result.Test.Checked >= MinChecked)
            Save(saved with { After = result.Test });
        return result;
    }

    private const int MinChecked = 200;

    internal Result Judge(Sample sample)
    {
        var gaps = Analyze(sample.Reports, sample.Frequency, sample.RefreshHz);
        var load = Attribute(sample.Reports, sample.Windows, sample.Frequency);
        var list = devices.List();
        var (controller, refusal) = sample.Mouse is null ? ((Device?)null, (string?)null) : Controller(sample.Mouse, devices, list);
        // No counters to go by: an interrupt Windows aims at one CPU only lands there.
        var cpu = load.Cpu ?? (controller?.Affinity is { } aim && BitOperations.PopCount(aim) == 1 ? BitOperations.TrailingZeroCount(aim) : null);
        var test = new Test(gaps.Rate, gaps.Checked, gaps.Missed, Math.Round(gaps.LongestMs, 2), Math.Round(gaps.P999Ms, 2), gaps.OnRefresh, cpu);
        Result Say(Verdict verdict, string detail, int? offer = null) => new(test, verdict, detail, controller?.Id, offer, now());

        if (sample.Mouse is null || sample.Reports.Count < 64) return Say(Verdict.Retry, "Exo didn't see a mouse move. Test again and move it in circles.");
        if (gaps.Checked < MinChecked) return Say(Verdict.Retry, "Exo needs quicker circles to check every report. Test again and move it faster.");
        var summary = Summary(test);
        if (!Real(test)) return Say(Verdict.Fine, summary + (test.Missed == 0 ? " Nothing to change." : " Too few to matter, so nothing to change."));
        if (refusal is not null || controller is null) return Say(Verdict.Cannot, $"{summary} {refusal ?? "It isn't on a USB 3 controller Exo can set."}");
        var topology = sample.Topology;
        if (topology.Logical > 64) return Say(Verdict.Cannot, summary + " This PC has more than 64 CPUs, which Exo doesn't set.");
        if (topology.Cores.Count == 0) return Say(Verdict.Cannot, summary + " Exo couldn't read this PC's CPUs, so it changes nothing.");
        if (cpu is not { } on) return Say(Verdict.Cannot, summary + " Exo couldn't tell which CPU takes its input, so it changes nothing.");

        var busiest = Busiest(load.Dpc);
        var first = Siblings(topology, 0).Contains(on);
        if (!first && !(busiest is { } busy && Siblings(topology, busy).Contains(on)))
            return Say(Verdict.Fine, $"{summary} Its input lands on CPU {on}, away from the busy ones, so another CPU wouldn't help.");

        var saved = Load();
        var ours = saved is { Reverted: false } && Same(saved.Id, controller.Id);
        if (saved is { Reverted: false } && !ours) return Say(Verdict.Cannot, summary + " Undo the CPU Exo set for your other USB controller first.");
        if (!ours && Taken(controller)) return Say(Verdict.Cannot, summary + " Another tool already set a CPU for its USB controller.");
        var pinned = list.Where(d => !Same(d.Id, controller.Id) && d.Policy == Specified && d.Override is { Length: > 0 })
            .Aggregate(0UL, (all, d) => all | Bits(d.Override!));
        if (Choose(topology, load.Dpc, load.Interrupts, busiest, pinned) is not { } offer)
            return Say(Verdict.Cannot, summary + " No quiet CPU is free, so Exo changes nothing.");
        var where = first ? $"Its input lands on CPU {on}, where Windows and most drivers go first." : $"Its input lands on CPU {on}, the one busiest with driver work.";
        return Say(Verdict.Fix, $"{summary} {where} CPU {offer} is quiet.", offer);
    }

    /// <summary>Late reports that matter: at least 3, and at least one in a thousand.</summary>
    private static bool Real(Test t) => t.Missed >= Math.Max(3, t.Checked / 1000);

    /// <summary>A value Exo didn't write: another tool's CPU, or a form Exo leaves alone. DevicePolicy 0 alone is Windows' default.</summary>
    private static bool Taken(Device d) => d.OddAffinity || d.Policy is not (null or 0) || d.Override is not null;

    private static string Summary(Test t) => t.Missed == 0
        ? $"{t.Rate} Hz, every report on time."
        : $"{t.Rate} Hz, {t.Missed.ToString("N0", Inv)} of {t.Checked.ToString("N0", Inv)} reports late, the longest {t.Longest.ToString("0.0", Inv)} ms."
            + (t.Refresh ? " They come with your screen's refresh." : "");

    /// <summary>
    /// Gaps against the polling rate's slot. A gap between two reports is judged only while the mouse moves at two counts a
    /// slot or more over the 8 reports before it (slower, it has nothing to send in some slots), and under 50 ms (longer is
    /// the hand stopping or lifting). A missed slot is a gap: 1.5 slots, and at least a quarter millisecond past the slot,
    /// so a report a little late (or Exo's own reading jitter at 4 and 8 kHz) isn't one.
    /// </summary>
    internal static Gaps Analyze(IReadOnlyList<Report> reports, long frequency, double refreshHz)
    {
        if (reports.Count < 64 || frequency <= 0) return new Gaps(0, 0, 0, 0, 0, false);
        var rate = Rate(reports, frequency);
        var slot = (double)frequency / rate;
        var still = frequency / 20.0;
        var late = Math.Max(slot * 1.5, slot + frequency * 0.00025);
        var intervals = new List<long>();
        var at = new List<long>();
        for (var k = 8; k + 1 < reports.Count; k++)
        {
            var interval = reports[k + 1].At - reports[k].At;
            var span = reports[k].At - reports[k - 8].At;
            if (interval >= still || span <= 0) continue;
            long counts = 0;
            for (var j = k - 7; j <= k; j++) counts += Math.Max(Math.Abs(reports[j].Dx), Math.Abs(reports[j].Dy));
            if (counts * slot < 2.0 * span) continue;
            intervals.Add(interval);
            if (interval >= late) at.Add(reports[k].At);
        }
        if (intervals.Count == 0) return new Gaps(rate, 0, 0, 0, 0, false);
        intervals.Sort();
        double Ms(long ticks) => ticks * 1000.0 / frequency;
        var p999 = intervals[Math.Clamp((int)Math.Ceiling(intervals.Count * 0.999) - 1, 0, intervals.Count - 1)];
        return new Gaps(rate, intervals.Count, at.Count, Ms(intervals[^1]), Ms(p999), OnBeat(at, frequency, refreshHz, slot));
    }

    /// <summary>The polling rate: reports in the busiest 50 ms stretches (the 95th percentile, so a catch-up burst doesn't count), snapped to a rate mice use.</summary>
    internal static int Rate(IReadOnlyList<Report> reports, long frequency)
    {
        var bin = Math.Max(1, frequency / 20);
        var counts = reports.GroupBy(r => (r.At - reports[0].At) / bin).Select(g => g.Count()).Order().ToList();
        var top = counts[Math.Min(counts.Count - 1, (int)(counts.Count * 0.95))] * 20.0;
        return Rates.MinBy(r => Math.Abs(Math.Log2(r / top)));
    }

    /// <summary>
    /// The gaps repeat with the screen: from one to the next is a whole number of refreshes (within 15%) for 70% of them,
    /// at least 6 gaps. Between neighbors only, so a refresh of 59.94 read as 60 doesn't drift. Not judged when the
    /// mouse's own slot is over a quarter of a refresh: its timing can't tell.
    /// </summary>
    internal static bool OnBeat(IReadOnlyList<long> at, long frequency, double refreshHz, double slot)
    {
        if (refreshHz < 20 || at.Count < 6) return false;
        var period = frequency / refreshHz;
        if (slot > period / 4) return false;
        var on = 0;
        for (var i = 1; i < at.Count; i++)
        {
            var beats = (at[i] - at[i - 1]) / period;
            var near = Math.Round(beats);
            if (near is >= 1 and <= 50 && Math.Abs(beats - near) <= 0.15) on++;
        }
        return on >= (at.Count - 1) * 0.7;
    }

    /// <summary>
    /// Which CPU took the mouse's interrupts: the one whose Interrupts/sec follows the reports per second across the test's
    /// stretches (correlation 0.6 or more, 0.05 to 2.5 interrupts a report: xHCI moderation batches them at high rates).
    /// It needs the reports to vary (the hand starting, slowing, stopping). What the mouse adds to that CPU's DPC time
    /// and interrupts is taken off, so its own work doesn't make its CPU look busy.
    /// </summary>
    internal static Work Attribute(IReadOnlyList<Report> reports, IReadOnlyList<Window> windows, long frequency)
    {
        var cpus = windows.Count == 0 ? 0 : windows.Min(w => Math.Min(w.Interrupts.Length, w.Dpc.Length));
        var dpc = new double[cpus];
        var ints = new double[cpus];
        for (var c = 0; c < cpus; c++)
        {
            dpc[c] = windows.Average(w => w.Dpc[c]);
            ints[c] = windows.Average(w => w.Interrupts[c]);
        }
        if (cpus == 0 || windows.Count < 8 || frequency <= 0) return new Work(null, dpc, ints);
        var rates = new double[windows.Count];
        var r = 0;
        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            while (r < reports.Count && reports[r].At < w.From) r++;
            var n = 0;
            for (var j = r; j < reports.Count && reports[j].At < w.To; j++) n++;
            rates[i] = n * (double)frequency / Math.Max(1, w.To - w.From);
        }
        var mean = rates.Average();
        if (Math.Sqrt(rates.Average(x => (x - mean) * (x - mean))) < 20) return new Work(null, dpc, ints);
        int? cpu = null;
        var best = 0.6;
        var slope = 0.0;
        for (var c = 0; c < cpus; c++)
        {
            var (b, corr) = Fit(rates, windows.Select(w => w.Interrupts[c]).ToArray());
            if (b is >= 0.05 and <= 2.5 && corr > best) (cpu, best, slope) = (c, corr, b);
        }
        if (cpu is { } m)
        {
            var (own, _) = Fit(rates, windows.Select(w => w.Dpc[m]).ToArray());
            dpc[m] = Math.Max(0, dpc[m] - Math.Max(0, own) * mean);
            ints[m] = Math.Max(0, ints[m] - slope * mean);
        }
        return new Work(cpu, dpc, ints);
    }

    private static (double Slope, double R) Fit(double[] x, double[] y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < x.Length; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        return sxx <= 0 || syy <= 0 ? (0, 0) : (sxy / sxx, sxy / Math.Sqrt(sxx * syy));
    }

    /// <summary>The CPU with the most DPC time over the test, when it has any to speak of (0.1%).</summary>
    internal static int? Busiest(IReadOnlyList<double> dpc)
    {
        var at = -1;
        for (var c = 0; c < dpc.Count; c++) if (at < 0 || dpc[c] > dpc[at]) at = c;
        return at >= 0 && dpc[at] >= 0.1 ? at : null;
    }

    /// <summary>The CPUs of the physical core <paramref name="cpu"/> is on (itself when Windows didn't say).</summary>
    private static int[] Siblings(Topology t, int cpu) => t.Cores.FirstOrDefault(c => c.Threads.Contains(cpu))?.Threads ?? [cpu];

    /// <summary>
    /// The CPU for the controller, or null when none may take it. Never physical core 0 or its sibling, the core of the
    /// busiest DPC CPU, or a core with a CPU another device is pinned to. Only the highest efficiency class (an Intel
    /// P-core). With more than one L3 (a dual-CCD Ryzen) only the largest's (an X3D's V-Cache), else CPU 0's. Then the
    /// quietest core over the test (DPC time to a tenth of a percent, then interrupts), and its first thread.
    /// </summary>
    internal static int? Choose(Topology t, IReadOnlyList<double> dpc, IReadOnlyList<double> interrupts, int? busiest, ulong pinned)
    {
        if (t.Logical > 64 || t.Cores.Count == 0) return null;
        var top = t.Cores.Max(c => c.Efficiency);
        var cores = t.Cores.Where(c => c.Efficiency == top).ToList();
        var caches = t.L3.Where(l => cores.Any(c => c.Threads.Any(x => Has(l.Mask, x)))).ToList();
        if (caches.Count > 1)
        {
            var size = caches.Max(l => l.Bytes);
            var largest = caches.Where(l => l.Bytes == size).ToList();
            var pick = largest.FirstOrDefault(l => Has(l.Mask, 0)) ?? largest[0];
            cores = cores.Where(c => c.Threads.All(x => Has(pick.Mask, x))).ToList();
        }
        double Sum(IReadOnlyList<double> values, Core c) => c.Threads.Sum(x => x < values.Count ? values[x] : 0);
        return cores
            .Where(c => !c.Threads.Contains(0) && !(busiest is { } b && c.Threads.Contains(b)) && !c.Threads.Any(x => Has(pinned, x)))
            .OrderBy(c => Math.Round(Sum(dpc, c), 1)).ThenBy(c => Sum(interrupts, c)).ThenBy(c => c.Threads.Min())
            .Select(c => (int?)c.Threads.Min()).FirstOrDefault();
    }

    private static bool Has(ulong mask, int cpu) => cpu is >= 0 and < 64 && (mask & (1UL << cpu)) != 0;

    /// <summary>AssignmentSetOverride for one CPU: a KAFFINITY, 8 bytes little-endian with that CPU's bit.</summary>
    internal static byte[] Mask(int cpu)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 1UL << cpu);
        return bytes;
    }

    /// <summary>A KAFFINITY's bits (the first 8 bytes, little-endian; a shorter value as far as it goes).</summary>
    internal static ulong Bits(byte[] mask)
    {
        var bytes = new byte[8];
        mask.AsSpan(0, Math.Min(8, mask.Length)).CopyTo(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    // ---- The controller ----

    /// <summary>
    /// The xHCI controller the mouse hangs from, or why Exo won't set one: the first PCI device up its tree, with no
    /// Bluetooth on the way, a USB 3 (xHCI) controller that is part of the PC (not a dock's or an enclosure's, nor under
    /// a Thunderbolt or USB4 bridge) and not on Windows' drive's path.
    /// </summary>
    internal static (Device? Controller, string? Refusal) Controller(string mouse, IDevices devices, IReadOnlyList<Device> list)
    {
        string? pci = null;
        string? id = mouse;
        for (var hops = 0; !string.IsNullOrEmpty(id) && hops < 32; hops++)
        {
            // BTHENUM, BTHLE, BTHLEDEVICE, BTH: the mouse talks to the PC through a Bluetooth radio.
            if (id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return (null, "It's a Bluetooth mouse, which a CPU for a USB controller can't help.");
            if (id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
            {
                pci = id;
                break;
            }
            id = devices.Parent(id);
        }
        var xhci = pci is null ? null : list.FirstOrDefault(d => Same(d.Id, pci));
        if (xhci is not { BaseClass: 0x0C, SubClass: 0x03, ProgIf: 0x30 } || !PciId().IsMatch(xhci.Id)) return (null, "It isn't on a USB 3 controller Exo can set.");
        string? up = xhci.Id;
        for (var hops = 0; !string.IsNullOrEmpty(up) && hops < 32; hops++)
        {
            var here = up;
            if (list.FirstOrDefault(d => Same(d.Id, here)) is { } d && (d.Local == false || Dock(d.Name)))
                return (null, "It's on a dock's USB controller, which Exo leaves alone.");
            up = devices.Parent(here);
        }
        var boot = BootChain(devices);
        if (boot.Count == 0) return (null, "Exo couldn't find the drive Windows starts from, so it changes nothing.");
        if (boot.Contains(xhci.Id)) return (null, "Windows starts from a drive on the same USB controller, so Exo leaves it alone.");
        return (xhci, null);
    }

    private static bool Dock(string name) => name.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase) || name.Contains("USB4", StringComparison.OrdinalIgnoreCase);

    // ---- The change ----

    /// <summary>Gives the last test's controller its CPU, backed up first; nothing when the last test didn't offer one. Null when done.</summary>
    internal string? Apply()
    {
        if (_last is not { Verdict: Verdict.Fix, Controller: { } id, Offer: { } cpu } last || now() - last.At > TimeSpan.FromMinutes(30)) return "Test your mouse first.";
        if (devices.List().FirstOrDefault(x => Same(x.Id, id)) is not { } d) return "Your mouse's USB controller is gone, so nothing changed.";
        var before = Load();
        var ours = before is { Reverted: false } && Same(before.Id, id);
        if (before is { Reverted: false } && !ours) return "Undo the CPU Exo set for your other USB controller first.";
        if (!ours && Taken(d)) return "Another tool already set a CPU for this USB controller, so nothing changed.";
        // A second change keeps what was there before Exo's first, and the test from before it.
        var saved = ours ? before! with { Cpu = cpu, WrittenAt = now(), After = null }
            : new Saved(id, cpu, d.Policy, d.Override is { } o ? Convert.ToHexString(o) : null, now(), last.Test);
        if (!Save(saved)) return "Your current settings couldn't be saved for Undo, so nothing changed.";
        var mask = Mask(cpu);
        if (writer.Write(id, Specified, mask) is { } refused)
        {
            Restore(before);
            return refused;
        }
        var after = devices.List().FirstOrDefault(x => Same(x.Id, id));
        if (after is { Policy: Specified } && SameBytes(after.Override, mask))
        {
            AppLog.Info($"Mouse core: USB controller set to CPU {cpu}.");
            return null;
        }
        if (after is null || (after.Policy == d.Policy && SameBytes(after.Override, d.Override)))
        {
            Restore(before);
            return "Windows didn't take the change.";
        }
        return "Windows took only part of the change. Undo puts it back.";
    }

    /// <summary>Puts back exactly what was there: the old values, or deletes them where there were none.</summary>
    internal string? Undo()
    {
        if (Load() is not { Reverted: false } saved) return "There's nothing to undo.";
        byte[]? was;
        try { was = saved.WasMask is { } hex ? Convert.FromHexString(hex) : null; }
        catch (FormatException) { return "Exo's backup is damaged, so nothing changed."; }
        if (writer.Write(saved.Id, saved.WasPolicy, was) is { } refused) return refused;
        var d = devices.List().FirstOrDefault(x => Same(x.Id, saved.Id));
        if (d is not null && (d.Policy != saved.WasPolicy || !SameBytes(d.Override, was))) return "Windows didn't put it back.";
        Save(saved with { Reverted = true, WrittenAt = now() });
        AppLog.Info("Mouse core: USB controller put back.");
        return null;
    }

    /// <summary>
    /// Where Exo's change stands: waiting on a restart, or since it whether the values are still there (a BIOS or driver
    /// update can clear them), whether Windows moved the interrupts (the allocated target set, and the test after) and
    /// whether the test after beat the one before.
    /// </summary>
    internal State Read()
    {
        if (Load() is not { } saved) return new State(Stage.None, null, null, null, null, null, false);
        var pending = saved.WrittenAt > bootTime();
        if (saved.Reverted)
        {
            if (pending) return new State(Stage.Restart, "Your mouse's USB controller goes back to how it was after you restart Windows.", null, null, null, null, false);
            Forget();
            return new State(Stage.None, null, null, null, null, null, false);
        }
        var cpu = saved.Cpu;
        var d = devices.List().FirstOrDefault(x => Same(x.Id, saved.Id));
        if (d is not { Policy: Specified } || !SameBytes(d.Override, Mask(cpu)))
        {
            Forget();
            AppLog.Info("Mouse core: Exo's CPU for the USB controller is gone.");
            var why = d is null ? "Your mouse's USB controller changed, so the CPU Exo set for it is gone."
                : pending ? "Another tool cleared the CPU Exo set for your mouse."
                : "A BIOS or driver update cleared the CPU Exo set for your mouse.";
            return new State(Stage.None, why, null, null, null, null, false);
        }
        if (pending) return new State(Stage.Restart, $"CPU {cpu} takes your mouse's input after you restart Windows.", cpu, saved.Before, null, null, true);
        var moved = !(d.Affinity is { } aim && aim != 1UL << cpu) && !(saved.After?.Cpu is { } landed && landed != cpu);
        var better = saved.After is { } after ? Better(saved.Before, after) : (bool?)null;
        var detail = !moved ? $"Windows didn't move your mouse's input to CPU {cpu}. Undo puts it back."
            : better is null ? $"CPU {cpu} takes your mouse's input now. Measure again to compare."
            : better.Value ? $"Fewer late reports since CPU {cpu} took your mouse's input."
            : $"No better since CPU {cpu} took your mouse's input. Undo puts it back.";
        return new State(Stage.Set, detail, cpu, saved.Before, saved.After, moved ? better : false, true);
    }

    /// <summary>A quarter fewer late reports (as a share of those checked), or no more of them and a quarter shorter at the longest.</summary>
    internal static bool Better(Test before, Test after)
    {
        static double Share(Test t) => t.Checked == 0 ? 0 : (double)t.Missed / t.Checked;
        return Share(after) <= Share(before) * 0.75 || (Share(after) <= Share(before) && after.Longest <= before.Longest * 0.75);
    }

    private static bool SameBytes(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private Saved? Load()
    {
        try { return File.Exists(backupPath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(backupPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private bool Save(Saved saved)
    {
        try
        {
            var temp = backupPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, backupPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Mouse core: the backup couldn't be saved: " + ex.Message);
            return false;
        }
    }

    private void Restore(Saved? before)
    {
        if (before is null) Forget();
        else Save(before);
    }

    private void Forget()
    {
        try { File.Delete(backupPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Mouse core: the backup couldn't be removed: " + ex.Message); }
    }

    // ---- Topology ----

    /// <summary>
    /// GetLogicalProcessorInformationEx(RelationAll): each RelationProcessorCore (flags, efficiency class, its group mask)
    /// and each unified L3 RelationCache (size, group mask), in group 0. Another group makes <see cref="Topology.Logical"/> over 64.
    /// </summary>
    internal static Topology Parse(ReadOnlySpan<byte> data, int logical)
    {
        var cores = new List<Core>();
        var l3 = new List<Cache>();
        var groups = false;
        for (var at = 0; at + 8 <= data.Length;)
        {
            var relation = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 4)..]);
            if (size < 8 || at + size > data.Length) break;
            var record = data.Slice(at, size);
            if (relation == 0 && size >= 48) // PROCESSOR_RELATIONSHIP: GroupCount at 30, GROUP_AFFINITY (mask, group) at 32
            {
                var mask = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
                if (BinaryPrimitives.ReadUInt16LittleEndian(record[30..]) > 1 || BinaryPrimitives.ReadUInt16LittleEndian(record[40..]) != 0) groups = true;
                else cores.Add(new Core(Enumerable.Range(0, 64).Where(x => Has(mask, x)).ToArray(), record[9]));
            }
            else if (relation == 2 && size >= 56 && record[8] == 3 && BinaryPrimitives.ReadInt32LittleEndian(record[16..]) == 0) // CACHE_RELATIONSHIP: L3, unified
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(record[48..]) != 0) groups = true;
                else l3.Add(new Cache(BinaryPrimitives.ReadUInt64LittleEndian(record[40..]), BinaryPrimitives.ReadUInt32LittleEndian(record[12..])));
            }
            at += size;
        }
        return new Topology(cores, l3, groups ? Math.Max(logical, 65) : logical);
    }
}
