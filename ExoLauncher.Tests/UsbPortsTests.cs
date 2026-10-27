using ExoLauncher.Services;
using static ExoLauncher.Services.UsbPorts;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways telling a device's USB port can go wrong (UsbPorts):
/// - an X570 board's chipset ports read as the processor's (its chipset's controllers share the processor's 149C);
/// - a B550 board's chipset controller (43EE, with ASMedia's subsystem id) read as an add-in card or as the processor's;
/// - a hub on the path not counted, or the controller's own root hub counted as one;
/// - a Bluetooth device placed on its radio's port; a headphone jack's sound chip read as a USB port;
/// - a dock's controller read as the PC's own;
/// - the USB 2 and USB 3 halves of one socket counted as two free ports;
/// - a suggestion for audio, or for a device already on the processor's own USB.
/// </summary>
public sealed class UsbPortsTests
{
    private static PciNode Node(string id, string? parent, string? name = null, bool? local = true) => new(id, parent, name, local);

    // This PC: a Ryzen 5 5600X on a B550 board.
    private const string Root = @"PCI\VEN_1022&DEV_1480&SUBSYS_87C01043&REV_00\3&11583659&0&00";
    private const string InternalBridge = @"PCI\VEN_1022&DEV_1484&SUBSYS_00000000&REV_00\3&11583659&0&41";
    private const string GppBridge = @"PCI\VEN_1022&DEV_1483&SUBSYS_00000000&REV_00\3&11583659&0&0A";
    private const string CpuXhci = @"PCI\VEN_1022&DEV_149C&SUBSYS_87C01043&REV_00\4&2a3c5d9&0&0341";
    private const string ChipsetXhci = @"PCI\VEN_1022&DEV_43EE&SUBSYS_11421B21&REV_00\4&1c3f2e8&0&000A";

    private static Dictionary<string, PciNode> B550() => new(StringComparer.OrdinalIgnoreCase)
    {
        [Root] = Node(Root, null),
        [InternalBridge] = Node(InternalBridge, Root),
        [GppBridge] = Node(GppBridge, Root),
        [CpuXhci] = Node(CpuXhci, InternalBridge),
        [ChipsetXhci] = Node(ChipsetXhci, GppBridge),
    };

    private static List<PciNode> Up(Dictionary<string, PciNode> pci, string id)
    {
        var list = new List<PciNode>();
        for (var up = pci[id].Parent; up is not null; up = pci[up].Parent) list.Add(pci[up]);
        return list;
    }

    [Fact]
    public void OnB550TheProcessorsAndTheChipsetsControllersAreToldApart()
    {
        var pci = B550();
        Assert.Equal(Where.Cpu, Classify(pci[CpuXhci], Up(pci, CpuXhci)).Where);
        Assert.Equal(Where.Chipset, Classify(pci[ChipsetXhci], Up(pci, ChipsetXhci)).Where);
    }

    [Fact]
    public void OnX570TheChipsetsControllersBehindItsSwitchAreTheChipsets()
    {
        const string sw = @"PCI\VEN_1022&DEV_57AD&SUBSYS_00000000&REV_00\4&x&0&0009";
        const string down = @"PCI\VEN_1022&DEV_57A4&SUBSYS_00000000&REV_00\5&x&0&000809";
        const string chipset149c = @"PCI\VEN_1022&DEV_149C&SUBSYS_7C371462&REV_00\6&x&0&00080809";
        var pci = B550();
        pci[sw] = Node(sw, GppBridge);
        pci[down] = Node(down, sw);
        pci[chipset149c] = Node(chipset149c, down);
        Assert.Equal(Where.Chipset, Classify(pci[chipset149c], Up(pci, chipset149c)).Where);
        Assert.Equal(Where.Cpu, Classify(pci[CpuXhci], Up(pci, CpuXhci)).Where);
    }

    [Theory]
    [InlineData(@"PCI\VEN_1022&DEV_15B6&SUBSYS_00000000&REV_00\x", "Cpu")]       // AM5 processor
    [InlineData(@"PCI\VEN_1022&DEV_15B8&SUBSYS_00000000&REV_00\x", "Cpu")]       // AM5 processor, USB 2
    [InlineData(@"PCI\VEN_1022&DEV_43F7&SUBSYS_00000000&REV_00\x", "Chipset")]   // 600/800-series chipset
    [InlineData(@"PCI\VEN_8086&DEV_7AE0&SUBSYS_00000000&REV_00\x", "Chipset")]   // Intel Z790 PCH
    [InlineData(@"PCI\VEN_8086&DEV_A71E&SUBSYS_00000000&REV_00\x", "Cpu")]       // Intel Raptor Lake-P TCSS
    [InlineData(@"PCI\VEN_1B21&DEV_2142&SUBSYS_00000000&REV_00\x", "AddIn")]     // ASMedia card
    public void EachKnownControllerIsPlaced(string id, string where) =>
        Assert.Equal(Enum.Parse<Where>(where), Classify(Node(id, null), []).Where);

    [Fact]
    public void AnAmdControllerExoDoesntKnowSaysNothingUnlessItSitsOnTheProcessorsOwnBridge()
    {
        const string unknown = @"PCI\VEN_1022&DEV_151E&SUBSYS_00000000&REV_00\x";
        Assert.Equal(Where.Unknown, Classify(Node(unknown, GppBridge), [Node(GppBridge, Root)]).Where);
        Assert.Equal(Where.Cpu, Classify(Node(unknown, InternalBridge), [Node(InternalBridge, Root)]).Where);
    }

    [Fact]
    public void AnExtraChipOnTheChipsetsLanesCountsBoth()
    {
        const string sw = @"PCI\VEN_1022&DEV_43E9&SUBSYS_00000000&REV_00\x";
        var (where, behind) = Classify(Node(@"PCI\VEN_1B21&DEV_2142&SUBSYS_00000000&REV_00\y", sw), [Node(sw, GppBridge), Node(GppBridge, Root)]);
        Assert.Equal(Where.AddIn, where);
        Assert.True(behind);
    }

    // The device side of the walk, as cfgmgr32 tells it.
    private const string RootHub = @"USB\ROOT_HUB30\5&1&0&0";
    private const string Receiver = @"USB\VID_361D&PID_0300\6&2&0&5";
    private const string Hub = @"USB\VID_05E3&PID_0608\7&3&0&11";
    private const string BehindHub = @"USB\VID_31E3&PID_1342\8&4&0&2";

    private static (Func<string, string?> Parent, Func<string, string?> Service) Tree(params (string Id, string? Parent, string? Service)[] nodes)
    {
        var map = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        return (id => map.TryGetValue(id, out var n) ? n.Parent : null, id => map.TryGetValue(id, out var n) ? n.Service : null);
    }

    [Fact]
    public void YourMouseReceiverOnTheChipsetIsOneChipAway()
    {
        var (parent, service) = Tree((Receiver, RootHub, "HidUsb"), (RootHub, ChipsetXhci, "USBHUB3"));
        var at = Locate(Receiver, parent, service, B550());
        Assert.Equal(new Placement(Where.Chipset, 1, 0, ChipsetXhci), at);
    }

    [Fact]
    public void AHubOnThePathCountsAndTheRootHubDoesnt()
    {
        var (parent, service) = Tree((BehindHub, Hub, "HidUsb"), (Hub, RootHub, "USBHUB3"), (RootHub, CpuXhci, "USBHUB3"));
        var at = Locate(BehindHub, parent, service, B550());
        Assert.Equal(Where.Cpu, at!.Where);
        Assert.Equal(1, at.Hubs);
        Assert.Equal(1, at.Chips);
    }

    [Fact]
    public void BluetoothIsBluetoothWhicheverPortItsRadioIsOn()
    {
        const string pad = @"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0df2\9&5&0&000000000000_00000000";
        var (parent, service) = Tree((pad, @"BTH\MS_BTHBRB\8&6&0&0", null));
        Assert.Equal(Where.Bluetooth, Locate(pad, parent, service, B550())!.Where);
    }

    [Fact]
    public void AHeadphoneJacksSoundChipIsNoUsbPort()
    {
        const string audio = @"PCI\VEN_1022&DEV_1487&SUBSYS_87C01043&REV_00\4&2a3c5d9&0&0441";
        const string endpoint = @"SWD\MMDEVAPI\{0.0.0.00000000}.{f2b1c4d6}";
        const string codec = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_1220\5&7&0&0001";
        var pci = B550();
        pci[audio] = Node(audio, InternalBridge);
        var (parent, service) = Tree((endpoint, codec, null), (codec, audio, "HDAudAddService"));
        Assert.Null(Locate(endpoint, parent, service, pci));
    }

    [Fact]
    public void ADocksControllerIsTheDocks()
    {
        const string tb = @"PCI\VEN_8086&DEV_15EF&SUBSYS_00000000&REV_06\x";
        const string dockXhci = @"PCI\VEN_8086&DEV_15F0&SUBSYS_00000000&REV_06\y";
        var pci = B550();
        pci[tb] = Node(tb, GppBridge, "Thunderbolt(TM) Controller - 15EF", true);
        pci[dockXhci] = Node(dockXhci, tb, "USB eXtensible Host Controller", false);
        var (parent, service) = Tree((Receiver, RootHub, "HidUsb"), (RootHub, dockXhci, "USBHUB3"));
        Assert.Equal(Where.Dock, Locate(Receiver, parent, service, pci)!.Where);
    }

    [Fact]
    public void OneSocketsUsb2AndUsb3HalvesAreOneConnector()
    {
        // This PC's processor root hub: USB 2 ports 1-4, their USB 3 companions 5-8; the mic on 1 and the Edge on 2.
        var ports = new List<HubPort>
        {
            new(1, true, false, 5, true), new(2, true, false, 6, true), new(3, true, false, 7, false), new(4, true, false, 8, false),
            new(5, true, false, 1, false), new(6, true, false, 2, false), new(7, true, false, 3, false), new(8, true, false, 4, false),
        };
        var connectors = Connectors(ports);
        Assert.Equal(4, connectors.Count);
        Assert.Equal(2, connectors.Count(c => c.All(p => !p.Connected)));
    }

    [Fact]
    public void PortsNobodyCanPlugIntoDontCount()
    {
        var ports = new List<HubPort> { new(1, true, true, 0, false), new(2, false, false, 0, false) };
        Assert.Single(Connectors(ports));
    }

    [Fact]
    public void TheSuggestionGoesToTheMouseKeyboardAndControllerOnly()
    {
        var chipset = new Placement(Where.Chipset, 1, 0, ChipsetXhci);
        var cpu = new Placement(Where.Cpu, 0, 0, CpuXhci);
        Assert.Equal("free", Better("mouse", chipset, 2));
        Assert.Equal("free", Better("keyboard", chipset, 1));
        Assert.Null(Better("mouse", chipset, 0));
        Assert.Null(Better("mouse", cpu, 2));
        Assert.Null(Better("audio", chipset, 2));
        Assert.Equal("hub", Better("controller", cpu with { Hubs = 1, Chips = 1 }, 2));
        Assert.Null(Better("mouse", new Placement(Where.Bluetooth, null, 0, null), 2));
    }
}
