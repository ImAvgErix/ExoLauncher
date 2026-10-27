using System.Globalization;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// Where a USB device meets the PC, for the Devices room: on the processor's own USB, through the chipset, through an
/// extra USB chip, a dock or a hub, or over Bluetooth, and how many chips sit between it and the processor (the idea of
/// Marius Heier's CPU Direct USB, MIT; see THIRD_PARTY_NOTICES.md). Read only: the device tree Exo already reads, the
/// USB controllers' PCI ids and their place in the PCI tree. The fix is the user's own hand moving a plug.
///
/// The ways it could go wrong, each covered by UsbPortsTests:
/// - an X570 board's chipset ports read as the processor's (its chipset's controllers share the processor's id, 149C;
///   they sit behind the chipset's switch, which the walk checks first);
/// - a B550 board's chipset controller (43EE, ASMedia's subsystem id) read as an add-in card;
/// - a hub on the path not counted (a front-panel header, a monitor, a keyboard's pass-through);
/// - a Bluetooth device placed on its radio's USB port, which no plug move can change;
/// - a guess where Exo doesn't know the controller (Unknown says nothing rather than guess);
/// - a dock's controller read as the PC's own.
/// </summary>
internal static partial class UsbPorts
{
    internal enum Where { Cpu, Chipset, AddIn, Dock, Bluetooth, Unknown }

    /// <param name="Controller">The USB controller's PCI instance id (native only).</param>
    /// <param name="Chips">Chips between the device and the processor: 0 on its own USB, 1 through the chipset or an extra
    /// chip, one more for each hub and for an extra chip behind the chipset; null where it means nothing (Bluetooth, a
    /// dock, an unknown controller).</param>
    internal sealed record Placement(Where Where, int? Chips, int Hubs, string? Controller);

    /// <summary>A PCI node as the walk needs it: its instance id, its parent's, and whether it's part of the PC itself (false on a dock).</summary>
    internal sealed record PciNode(string Id, string? Parent, string? Name, bool? Local);

    // AMD's USB controllers, by device id. The processor's own (SoC) controllers: Zeppelin, Raven/Picasso, Renoir/Cezanne,
    // Matisse/Vermeer (149C, also used by X570's chipset), Raphael/Granite Ridge (15B6/15B7, and 15B8 for USB 2).
    private static readonly HashSet<int> AmdSoc = [0x145F, 0x15E0, 0x15E1, 0x1639, 0x149C, 0x15B6, 0x15B7, 0x15B8];
    // The chipsets' (Promontory and successors) USB controllers: 300/400-series 43B9-43BC and 43D5, 500-series 43EC and
    // 43EE, 600/800-series 43F7, 43F9-43FD; older FCH 7812/7814/7914.
    private static readonly HashSet<int> AmdChipset = [0x43B9, 0x43BA, 0x43BB, 0x43BC, 0x43D5, 0x43EC, 0x43EE, 0x43F7, 0x43F9, 0x43FA, 0x43FC, 0x43FD, 0x7812, 0x7814, 0x7914];
    // A chipset's switch upstream port: anything below one is the chipset's (X570 57AD, 500-series 43E9, 600/800-series
    // 43F4, 400-series 43C6).
    private static readonly HashSet<int> AmdChipsetSwitch = [0x57AD, 0x43E9, 0x43F4, 0x43C6];
    // The processor's Internal GPP bridges, to its own functions: a USB controller right below one is the processor's.
    private static readonly HashSet<int> AmdInternalBridge = [0x1454, 0x15DB, 0x15DC, 0x1484, 0x14DD, 0x14EB, 0x150C, 0x1110, 0x1111, 0x1112, 0x1130, 0x1131, 0x1132, 0x13E5, 0x13E6, 0x15FD, 0x15FE, 0x14A7, 0x14B9, 0x1555];
    // Intel's on-processor (TCSS and Thunderbolt 4) USB controllers; every other Intel controller is the PCH's (chipset).
    private static readonly HashSet<int> IntelCpu = [0x8A13, 0x9A13, 0x9A17, 0x461E, 0x464E, 0xA71E, 0x7EC0, 0xA831, 0xE331, 0xE431];

    [GeneratedRegex(@"^PCI\\VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PciIds();

    internal static (int Vendor, int Device)? Ids(string? pciId) =>
        pciId is not null && PciIds().Match(pciId) is { Success: true } m
            ? (int.Parse(m.Groups[1].Value, NumberStyles.HexNumber), int.Parse(m.Groups[2].Value, NumberStyles.HexNumber))
            : null;

    /// <summary>
    /// Where <paramref name="source"/> (a card's USB device instance) meets the PC. <paramref name="parent"/> and
    /// <paramref name="service"/> answer for any device instance; <paramref name="pci"/> holds the PCI nodes by instance id.
    /// </summary>
    internal static Placement? Locate(string source, Func<string, string?> parent, Func<string, string?> service, IReadOnlyDictionary<string, PciNode> pci)
    {
        var hubs = 0;
        var usb = false;
        string? id = source;
        for (var hops = 0; !string.IsNullOrEmpty(id) && hops < 32; hops++)
        {
            // BTHENUM, BTHLE, BTHLEDEVICE, BTH: it reaches the PC over a Bluetooth radio, whichever port that sits on.
            if (id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return new Placement(Where.Bluetooth, null, 0, null);
            if (id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) break;
            usb |= id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase);
            if (id != source && IsHub(id, service(id))) hubs++;
            id = parent(id);
        }
        // Only a path through a USB root hub is a USB port: the onboard sound chip a headphone jack hangs off is PCI too.
        if (!usb || id is null || !pci.TryGetValue(id, out var xhci)) return null;
        var ancestors = new List<PciNode>();
        for (string? up = xhci.Parent; up is not null && pci.TryGetValue(up, out var node) && ancestors.Count < 32; up = node.Parent) ancestors.Add(node);
        if (xhci.Local == false || ancestors.Any(a => a.Local == false || Dock(a.Name))) return new Placement(Where.Dock, null, hubs, xhci.Id);
        var (where, behindChipset) = Classify(xhci, ancestors);
        int? chips = where switch
        {
            Where.Cpu => hubs,
            Where.Chipset => 1 + hubs,
            Where.AddIn => 1 + (behindChipset ? 1 : 0) + hubs,
            _ => null,
        };
        return new Placement(where, chips, hubs, xhci.Id);
    }

    /// <summary>A USB hub on the path, not the controller's own root hub: by its driver (USBHUB3, usbhub), which no language changes.</summary>
    internal static bool IsHub(string id, string? service) =>
        !id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(service, "USBHUB3", StringComparison.OrdinalIgnoreCase) || string.Equals(service, "usbhub", StringComparison.OrdinalIgnoreCase));

    /// <summary>The USB controller's place, and whether an extra chip sits on the chipset's lanes. <paramref name="ancestors"/>: its PCI parents, nearest first.</summary>
    internal static (Where Where, bool BehindChipset) Classify(PciNode xhci, IReadOnlyList<PciNode> ancestors)
    {
        if (Ids(xhci.Id) is not var (vendor, device)) return (Where.Unknown, false);
        var underSwitch = ancestors.Any(a => Ids(a.Id) is (0x1022, var d) && AmdChipsetSwitch.Contains(d));
        switch (vendor)
        {
            case 0x1022:
                if (AmdChipset.Contains(device) || underSwitch) return (Where.Chipset, false);
                if (AmdSoc.Contains(device)) return (Where.Cpu, false);
                if (ancestors.Count > 0 && Ids(ancestors[0].Id) is (0x1022, var bridge) && AmdInternalBridge.Contains(bridge)) return (Where.Cpu, false);
                return (Where.Unknown, false);
            case 0x8086:
                return (IntelCpu.Contains(device) ? Where.Cpu : Where.Chipset, false);
            default:
                // ASMedia, VIA, Renesas, Fresco Logic, Etron, discrete Thunderbolt: an extra chip on PCIe lanes.
                return (Where.AddIn, underSwitch);
        }
    }

    private static bool Dock(string? name) => name is not null &&
        (name.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase) || name.Contains("USB4", StringComparison.OrdinalIgnoreCase) || name.Contains("Dock", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Which device gets the processor's ports first: the mouse (or its receiver), then the keyboard, then the controller.
    /// Audio never asks for one: its delay is its buffer, not its port.
    /// </summary>
    internal static int Priority(string kind) => kind switch { "mouse" => 0, "keyboard" => 1, "controller" => 2, _ => 9 };

    /// <summary>
    /// What to say about one device's port: "free" when it isn't on the processor's own USB and a free port there would
    /// take it; "hub" when a hub sits on its path; null when it's where it should be or nothing would help.
    /// </summary>
    internal static string? Better(string kind, Placement? at, int? freeCpuPorts)
    {
        if (at is null || Priority(kind) > 2) return null;
        if (at.Hubs > 0 && at.Where is Where.Cpu or Where.Chipset or Where.AddIn) return "hub";
        return at.Where is Where.Chipset or Where.AddIn && freeCpuPorts > 0 ? "free" : null;
    }
}
