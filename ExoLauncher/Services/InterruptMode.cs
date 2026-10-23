using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// System > This PC > Device interrupts. A PCI device signals the processor either on a line it may share with others
/// or with its own messages (MSI/MSI-X); messages never wait on a neighbor's interrupt, so a mouse's USB controller
/// or the GPU isn't held up by a busy device on the same line. Windows reports what the hardware can do
/// (DEVPKEY_PciDevice_InterruptSupport, InterruptMessageMaximum) and what it was given (the allocated IRQs: a message
/// interrupt has a negative number); the driver's INF decides by writing MSISupported. Optimize sets MSISupported=1
/// only for a graphics card, network adapter, HD Audio controller or xHCI USB controller that has MSI, runs a signed
/// driver and is on a line interrupt now, and gives the graphics card and the USB controllers (a frame's and an input's
/// path) high interrupt priority (DevicePriority 3), the one priority worth having: everything high is nothing first.
/// It never writes MessageNumberLimit (the device and driver pick the count, up to the device's maximum) or affinity,
/// never lowers a priority already set, and never touches storage, the controllers between Windows' drive and the
/// processor, bridges or anything else. Every value is backed up first; Undo all or one device's Revert puts it back.
/// Windows applies it at the next restart, which Exo never does itself.
/// </summary>
internal sealed partial class InterruptMode(InterruptMode.IDevices devices, InterruptMode.IWriter writer, string backupPath, Func<DateTimeOffset> bootTime, Func<DateTimeOffset> now)
{
    internal enum Mode { None, Line, Msi }

    internal enum Plan { Change, Done, Keep, Skip, Restart }

    /// <summary>A present PCI device as Windows reports it. <paramref name="MsiValue"/>: MSISupported in its registry key, <paramref name="Priority"/>: DevicePriority; null when absent.</summary>
    internal sealed record Device(string Id, string Name, string? Service, uint Problem, byte? BaseClass, byte? SubClass, byte? ProgIf,
        uint? Support, uint? MessageMax, uint? DriverRank, Mode Mode, int? MsiValue, int? Priority = null);

    internal interface IDevices
    {
        IReadOnlyList<Device> List();
        /// <summary>The disk devices that hold the Windows volume; empty when Windows couldn't say.</summary>
        IReadOnlyList<string> BootDisks();
        string? Parent(string id);
    }

    internal interface IWriter
    {
        /// <summary>Sets each device's value (<see cref="MsiName"/> or <see cref="PriorityName"/>; null deletes it) in one step Windows asks permission for. Null when it ran, else the line to show.</summary>
        string? Write(IReadOnlyList<(string Id, string Name, int? Value)> values);
    }

    /// <summary>
    /// <paramref name="Was"/>: MSISupported before Exo wrote it (null: absent), <paramref name="WasPriority"/> DevicePriority;
    /// <paramref name="WroteMsi"/> and <paramref name="WrotePriority"/>: which of them Exo wrote. <paramref name="Reverted"/>: put back, waiting on a restart.
    /// </summary>
    internal sealed record Entry(string Id, string Name, string Kind, int? Was, bool Reverted = false, bool WroteMsi = true, bool WrotePriority = false, int? WasPriority = null);

    internal sealed record Backup(DateTimeOffset WrittenAt, List<Entry> Devices);

    internal sealed record Row(string Token, string Name, string Kind, Mode Mode, Plan Plan, string Reason, bool Changed);

    /// <summary><paramref name="Restart"/>: Exo wrote since Windows started. <paramref name="Result"/>: after the restart, what took.</summary>
    internal sealed record State(IReadOnlyList<Row> Devices, bool Restart, string? Result, bool CanUndo);

    private const uint MsiBits = 0x2 | 0x4; // PCI_INTERRUPT_SUPPORT: 1 line, 2 MSI, 4 MSI-X
    internal const string MsiName = "MSISupported";
    internal const string PriorityName = "DevicePriority";
    private const int High = 3; // IrqPriorityHigh
    private const string Creative = "1102";

    /// <summary>Only ids Windows itself gives a PCI device reach reg.exe: no quote, percent or caret for cmd to read.</summary>
    [GeneratedRegex(@"^PCI\\[A-Z0-9_&]+\\[A-Z0-9_&]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PciId();

    internal static DateTimeOffset BootTime() => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    internal State Read()
    {
        var list = devices.List();
        var boot = BootChain();
        var backup = Load();
        var pending = backup is not null && backup.WrittenAt > bootTime();
        if (backup is not null && !pending && backup.Devices.Any(e => e.Reverted))
        {
            // Restarted since a revert: the reverted ones are done.
            backup = backup with { Devices = backup.Devices.Where(e => !e.Reverted).ToList() };
            if (backup.Devices.Count == 0) Forget();
            else Save(backup);
        }
        if (backup is { Devices.Count: 0 }) backup = null;
        var rows = list.Where(Listed).Select(d => RowFor(d, boot, backup, pending)).ToList();
        var result = backup is not null && !pending ? Verify(list, backup) : null;
        return new State(rows, pending, result, backup?.Devices.Any(e => !e.Reverted) == true);
    }

    /// <summary>MSISupported=1 for every device whose plan is Change, backed up first; all-or-nothing on a declined prompt. Null when done or nothing to do.</summary>
    internal string? Apply()
    {
        var list = devices.List();
        var boot = BootChain();
        if (boot.Count == 0) return "Exo couldn't find the drive Windows starts from, so nothing changed.";
        var targets = list.Where(d => Listed(d) && Decide(d, boot).Plan == Plan.Change).ToList();
        if (targets.Count == 0) return null;

        var before = Load();
        var kept = before?.Devices ?? [];
        var entries = kept.Where(e => targets.All(t => !Same(t.Id, e.Id))).ToList();
        foreach (var t in targets)
        {
            var old = kept.Find(e => Same(e.Id, t.Id));
            entries.Add(old is not null
                ? old with { Reverted = false, WroteMsi = old.WroteMsi || NeedsMsi(t), WrotePriority = old.WrotePriority || NeedsPriority(t), WasPriority = old.WrotePriority ? old.WasPriority : t.Priority }
                : new Entry(t.Id, t.Name, Kind(t), t.MsiValue, WroteMsi: NeedsMsi(t), WrotePriority: NeedsPriority(t), WasPriority: t.Priority));
        }
        if (!Save(new Backup(now(), entries))) return "Your current settings couldn't be saved for Undo, so nothing changed.";

        var writes = targets.SelectMany(t => (NeedsMsi(t) ? new[] { (t.Id, MsiName, (int?)1) } : []).Concat(NeedsPriority(t) ? [(t.Id, PriorityName, (int?)High)] : [])).ToList();
        if (writer.Write(writes) is { } refused)
        {
            Restore(before);
            return refused;
        }
        var after = devices.List();
        var missed = targets.Where(t => after.FirstOrDefault(d => Same(d.Id, t.Id)) is not { } a || (NeedsMsi(t) && a.MsiValue != 1) || (NeedsPriority(t) && a.Priority != High)).ToList();
        if (missed.Count == targets.Count)
        {
            Restore(before);
            return "Windows didn't take the change.";
        }
        if (missed.Count > 0)
        {
            // Those values didn't change: the backup keeps only what Exo really wrote (and what it held before).
            var real = entries.Select(e => missed.Any(m => Same(m.Id, e.Id)) ? kept.Find(k => Same(k.Id, e.Id)) : e).OfType<Entry>().ToList();
            Save(new Backup(now(), real));
        }
        AppLog.Info($"Interrupts: {targets.Count - missed.Count} device(s) set (MSI mode {targets.Count(NeedsMsi)}, high priority {targets.Count(NeedsPriority)}){(missed.Count > 0 ? $", {missed.Count} refused" : "")}.");
        return missed.Count > 0 ? $"{Names(missed)} didn't take the change." : null;
    }

    /// <summary>Puts back what Exo changed: every device, or the one whose token is <paramref name="token"/>.</summary>
    internal string? Undo(string? token)
    {
        if (Load() is not { } backup) return "There's nothing to undo.";
        var targets = backup.Devices.Where(e => !e.Reverted && (token is null || Token(e.Id) == token)).ToList();
        if (targets.Count == 0) return "There's nothing to undo.";
        var writes = targets.SelectMany(e => (e.WroteMsi ? new[] { (e.Id, MsiName, e.Was) } : []).Concat(e.WrotePriority ? [(e.Id, PriorityName, e.WasPriority)] : [])).ToList();
        if (writer.Write(writes) is { } refused) return refused;
        var after = devices.List();
        var back = targets.Where(e => after.FirstOrDefault(d => Same(d.Id, e.Id)) is not { } d
            || ((!e.WroteMsi || d.MsiValue == e.Was) && (!e.WrotePriority || d.Priority == e.WasPriority))).ToList();
        var devicesNow = backup.Devices.Select(e => back.Contains(e) ? e with { Reverted = true } : e).ToList();
        Save(new Backup(now(), devicesNow));
        AppLog.Info($"Interrupts: {back.Count} device(s) put back{(back.Count < targets.Count ? $", {targets.Count - back.Count} refused" : "")}.");
        return back.Count == targets.Count ? null : "Windows didn't put every device back.";
    }

    /// <summary>The devices between Windows' drive and the processor (the disk itself, its controller, every bridge above it).</summary>
    internal HashSet<string> BootChain()
    {
        var chain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var disk in devices.BootDisks())
        {
            for (var id = disk; !string.IsNullOrEmpty(id) && chain.Add(id) && chain.Count < 64; id = devices.Parent(id)) { }
        }
        return chain;
    }

    /// <summary>A device that reports its interrupt support; bridges and most chipset parts don't and aren't listed.</summary>
    private static bool Listed(Device d) => d.Support is not null && PciId().IsMatch(d.Id);

    /// <summary>What Exo would do for a device, before any change of its own; the first rule that matches wins.</summary>
    internal static (Plan Plan, string Reason) Decide(Device d, HashSet<string> boot)
    {
        if (boot.Contains(d.Id)) return (Plan.Skip, "Left alone: Windows starts from the drive behind it");
        if (d.BaseClass == 0x01) return (Plan.Skip, "Left alone: storage stays as Windows set it");
        if (string.IsNullOrEmpty(d.Service) || d.Problem != 0) return (Plan.Skip, "Left alone: no driver is running it");
        if (d.DriverRank is not { } rank || rank >= 0x80000000) return (Plan.Skip, "Left alone: its driver isn't signed");
        if (Vendor(d.Id) == Creative) return (Plan.Skip, "Left alone: Sound Blaster drivers can fail in MSI mode");
        if (!Allowed(d)) return (Plan.Skip, "Left alone: Windows manages it");
        if ((d.Support & MsiBits) == 0 || d.MessageMax is null or 0) return (Plan.Keep, "Supports only a shared line");
        if (d.Mode == Mode.Msi) return !Prioritized(d) ? (Plan.Done, "Uses MSI mode")
            : d.Priority == High ? (Plan.Done, "Uses MSI mode, high priority") : (Plan.Change, "Uses MSI mode. Exo will give it high priority");
        if (d.Mode == Mode.None) return (Plan.Skip, "Not running");
        if (d.MsiValue == 1) return (Plan.Keep, "Its driver keeps it on a shared line");
        return (Plan.Change, Prioritized(d) ? "Uses a shared line. Exo will switch it to MSI mode, high priority" : "Uses a shared line. Exo will switch it to MSI mode");
    }

    private static Row RowFor(Device d, HashSet<string> boot, Backup? backup, bool pending)
    {
        var entry = backup?.Devices.Find(e => Same(e.Id, d.Id));
        var (plan, reason) = pending && entry is { Reverted: true } ? (Plan.Restart, "Goes back to how it was after a restart")
            : pending && entry is { Reverted: false, WroteMsi: true } && d.MsiValue == 1 && d.Mode != Mode.Msi ? (Plan.Restart, "Switches to MSI mode after a restart")
            : pending && entry is { Reverted: false, WrotePriority: true } && d.Priority == High ? (Plan.Restart, "Takes high priority after a restart")
            : Decide(d, boot);
        return new Row(Token(d.Id), d.Name, Kind(d), d.Mode, plan, reason, entry is { Reverted: false });
    }

    /// <summary>After the restart: one short paragraph on what switched, what didn't and what Windows or a driver put back.</summary>
    internal static string? Verify(IReadOnlyList<Device> list, Backup backup)
    {
        var done = new List<string>();
        var stuck = new List<string>();
        var undone = new List<string>();
        var high = new List<string>();
        foreach (var e in backup.Devices.Where(e => !e.Reverted))
        {
            var d = list.FirstOrDefault(x => Same(x.Id, e.Id));
            if (!e.WroteMsi)
            {
                if (d?.Priority == High) high.Add(e.Kind);
                else undone.Add(e.Kind);
            }
            else if (d?.Mode == Mode.Msi) done.Add(e.Kind);
            else if (d is null || d.MsiValue != 1) undone.Add(e.Kind);
            else stuck.Add(e.Kind);
        }
        var parts = new List<string>();
        if (done.Count > 0) parts.Add($"Your {Phrase(done)} now {(done.Count == 1 ? "uses" : "use")} MSI mode.");
        if (stuck.Count > 0) parts.Add($"Your {Phrase(stuck)} didn't switch: {(stuck.Count == 1 ? "its driver keeps" : "their drivers keep")} a shared line.");
        if (high.Count > 0) parts.Add($"Your {Phrase(high)} now {(high.Count == 1 ? "has" : "have")} high interrupt priority.");
        if (undone.Count > 0) parts.Add($"Windows or a driver update put your {Phrase(undone)} back.");
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>"graphics card and 3 USB controllers": each kind once, counted, in the order first seen.</summary>
    internal static string Phrase(IReadOnlyList<string> kinds)
    {
        var items = kinds.GroupBy(k => k).Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} {g.Key}s").ToList();
        return items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
    }

    private static string Names(IReadOnlyList<Device> list) => list.Count == 1 ? list[0].Name : $"{list.Count} devices";

    /// <summary>What a device the plan changes needs: MSI when it is on a line, high priority when it is a frame's or an input's path.</summary>
    private static bool NeedsMsi(Device d) => d.Mode != Mode.Msi;

    private static bool NeedsPriority(Device d) => Prioritized(d) && d.Priority != High;

    /// <summary>A frame's path (the graphics card) and an input's (an xHCI USB controller): the devices worth high interrupt priority.</summary>
    private static bool Prioritized(Device d) => (d.BaseClass, d.SubClass, d.ProgIf) is (0x03, _, _) or (0x0C, 0x03, 0x30);

    private static bool Allowed(Device d) => (d.BaseClass, d.SubClass, d.ProgIf) is (0x03, _, _) or (0x02, _, _) or (0x04, 0x03, _) or (0x0C, 0x03, 0x30);

    internal static string Kind(Device d) => (d.BaseClass, d.SubClass) switch
    {
        (0x03, _) => "graphics card",
        (0x02, _) => "network adapter",
        (0x04, 0x03) => "audio controller",
        (0x04, _) => "media device",
        (0x0C, 0x03) => "USB controller",
        (0x01, _) => "storage controller",
        _ => "system device",
    };

    private static string? Vendor(string id)
    {
        var at = id.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        return at >= 0 && id.Length >= at + 8 ? id.Substring(at + 4, 4).ToUpperInvariant() : null;
    }

    /// <summary>React gets this, never the instance id (it can carry a serial number).</summary>
    internal static string Token(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToUpperInvariant())))[..12].ToLowerInvariant();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private Backup? Load()
    {
        try { return File.Exists(backupPath) ? JsonSerializer.Deserialize<Backup>(File.ReadAllText(backupPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private bool Save(Backup backup)
    {
        try
        {
            var temp = backupPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(backup));
            File.Move(temp, backupPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Interrupts: the backup couldn't be saved: " + ex.Message);
            return false;
        }
    }

    private void Restore(Backup? before)
    {
        if (before is null) Forget();
        else Save(before);
    }

    private void Forget()
    {
        try { File.Delete(backupPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Interrupts: the backup couldn't be removed: " + ex.Message); }
    }
}
