using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using static ExoLauncher.Services.Hid.HidInterop;

namespace ExoLauncher.Services;

/// <summary>
/// UsbPorts' reads of Windows: the device tree and the PCI devices (both read elsewhere already), and, for the
/// processor's own USB controllers, the root hub's ports (the hub queries USBView makes: how many ports, which a person
/// can plug into, which are USB-C, which pairs are one connector, which have something in them). Read only.
/// </summary>
internal static partial class UsbPorts
{
    /// <summary>What a card's port means, on the wire.</summary>
    /// <param name="Where">cpu, chipset, addin, dock, bluetooth or unknown.</param>
    /// <param name="Interrupts">msi or line: how its USB controller interrupts the processor; null when unknown.</param>
    /// <param name="Better">free (a port on the processor's own USB would take it), hub (plug it straight into the PC), or null.</param>
    internal sealed record Port(
        string Where,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Chips,
        int Hubs,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Interrupts,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Better);

    /// <summary>The processor's own USB connectors: how many are free, and whether any free one is USB-C.</summary>
    internal sealed record CpuPorts(int Free, int Total, bool FreeTypeC);

    /// <summary>Every card's port, and the processor's free connectors (null when its root hub didn't answer).</summary>
    internal static (Dictionary<string, Port> Ports, CpuPorts? Cpu) Read(IReadOnlyList<Peripherals.Peripheral> cards)
    {
        var tree = Peripherals.Tree();
        var parents = tree.ToDictionary(n => n.Id, n => n.Parent, StringComparer.OrdinalIgnoreCase);
        var services = tree.ToDictionary(n => n.Id, n => n.Service, StringComparer.OrdinalIgnoreCase);
        var devices = new InterruptMode.NativeDevices();
        var list = devices.List();
        var pci = list.ToDictionary(d => d.Id, d => new PciNode(d.Id, parents.GetValueOrDefault(d.Id) ?? devices.Parent(d.Id), d.Name, d.Local),
            StringComparer.OrdinalIgnoreCase);
        var modes = list.ToDictionary(d => d.Id, d => d.Mode, StringComparer.OrdinalIgnoreCase);

        var cpu = CountCpuPorts(tree, pci);
        var ports = new Dictionary<string, Port>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (card.Source is not { } source) continue;
            if (Locate(source, id => parents.GetValueOrDefault(id), id => services.GetValueOrDefault(id), pci) is not { } at) continue;
            var mode = at.Controller is { } c && modes.TryGetValue(c, out var m) ? m : InterruptMode.Mode.None;
            ports[card.Id] = new Port(at.Where.ToString().ToLowerInvariant(), at.Chips, at.Hubs,
                mode switch { InterruptMode.Mode.Msi => "msi", InterruptMode.Mode.Line => "line", _ => null },
                Better(card.Kind, at, cpu?.Free));
        }
        return (ports, cpu);
    }

    // ------------------------------------------------------------------ the processor's own ports

    private static readonly Guid HubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8"); // GUID_DEVINTERFACE_USB_HUB
    // usbioctl.h: CTL_CODE(FILE_DEVICE_USB, function, METHOD_BUFFERED, FILE_ANY_ACCESS). 277 is the hub's own
    // information, not its connectors': it answers too, with the port count where the connector flags should be.
    private const uint GetNodeInformation = 0x22 << 16 | 258 << 2, GetConnectionEx = 0x22 << 16 | 274 << 2, GetConnectorProperties = 0x22 << 16 | 278 << 2;

    /// <summary>
    /// The connectors on the processor's own USB controllers' root hubs: a USB 2 port and its USB 3 companion are one
    /// connector, only ports a person can plug into count, and a connector is free when neither port has a device.
    /// </summary>
    private static CpuPorts? CountCpuPorts(IReadOnlyList<Peripherals.DevNode> tree, IReadOnlyDictionary<string, PciNode> pci)
    {
        int free = 0, total = 0;
        var freeTypeC = false;
        var answered = false;
        foreach (var hub in tree.Where(n => n.Id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase) && n.Parent is not null))
        {
            if (!pci.TryGetValue(hub.Parent!, out var xhci)) continue;
            var ancestors = new List<PciNode>();
            for (string? up = xhci.Parent; up is not null && pci.TryGetValue(up, out var node) && ancestors.Count < 32; up = node.Parent) ancestors.Add(node);
            if (Classify(xhci, ancestors).Where != Where.Cpu || HubPorts(hub.Id) is not { } ports) continue;
            answered = true;
            foreach (var connector in Connectors(ports))
            {
                total++;
                if (connector.All(p => !p.Connected))
                {
                    free++;
                    freeTypeC |= connector.Any(p => p.TypeC);
                }
            }
        }
        return answered ? new CpuPorts(free, total, freeTypeC) : null;
    }

    /// <summary>One root hub port as the hub says it.</summary>
    internal sealed record HubPort(int Number, bool UserConnectable, bool TypeC, int Companion, bool Connected);

    /// <summary>The hub's connectors: each user-connectable port with its companion (the USB 2 and USB 3 halves of one socket), once.</summary>
    internal static IReadOnlyList<IReadOnlyList<HubPort>> Connectors(IReadOnlyList<HubPort> ports)
    {
        var byNumber = ports.ToDictionary(p => p.Number);
        var seen = new HashSet<int>();
        var connectors = new List<IReadOnlyList<HubPort>>();
        foreach (var port in ports.Where(p => p.UserConnectable).OrderBy(p => p.Number))
        {
            if (!seen.Add(port.Number)) continue;
            var connector = new List<HubPort> { port };
            if (port.Companion != 0 && byNumber.TryGetValue(port.Companion, out var twin) && seen.Add(twin.Number)) connector.Add(twin);
            connectors.Add(connector);
        }
        return connectors;
    }

    private static List<HubPort>? HubPorts(string hubInstance)
    {
        if (InterfacePath(hubInstance) is not { } path) return null;
        using var hub = CreateFile(path, GenericWrite, FileShareWrite, 0, OpenExisting, 0, 0);
        if (hub.IsInvalid) return null;
        var node = new byte[80];
        if (!DeviceIoControl(hub, GetNodeInformation, node, node.Length, node, node.Length, out _, 0)) return null;
        int count = node[6]; // USB_NODE_INFORMATION: NodeType (4), then the hub descriptor: length, type, bNumberOfPorts
        var ports = new List<HubPort>();
        for (var number = 1; number <= count; number++)
        {
            var properties = new byte[256];
            BitConverter.TryWriteBytes(properties.AsSpan(0, 4), number);
            if (!DeviceIoControl(hub, GetConnectorProperties, properties, properties.Length, properties, properties.Length, out _, 0)) continue;
            var flags = BitConverter.ToUInt32(properties, 8);
            var companion = BitConverter.ToUInt16(properties, 14);
            var connection = new byte[512];
            BitConverter.TryWriteBytes(connection.AsSpan(0, 4), number);
            // USB_NODE_CONNECTION_INFORMATION_EX is packed: its ConnectionStatus (1 = a device) sits at byte 31.
            var connected = DeviceIoControl(hub, GetConnectionEx, connection, connection.Length, connection, connection.Length, out _, 0)
                && BitConverter.ToUInt32(connection, 31) == 1;
            ports.Add(new HubPort(number, (flags & 0x1) != 0, (flags & 0x8) != 0, companion, connected));
        }
        return ports;
    }

    private static string? InterfacePath(string instanceId)
    {
        var guid = HubInterface;
        if (CM_Get_Device_Interface_List_Size(out var length, ref guid, instanceId, 0) != 0 || length <= 1) return null;
        var buffer = new char[length];
        if (CM_Get_Device_Interface_List(ref guid, instanceId, buffer, length, 0) != 0) return null;
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, [Out] byte[] output, int outputSize, out int returned, nint overlapped);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static extern uint CM_Get_Device_Interface_List_Size(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static extern uint CM_Get_Device_Interface_List(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);
}
