using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// The Devices room's cards: the mice, keyboards and game controllers on USB or connected over Bluetooth, and
/// Windows' default speakers and microphone. Exo only reads here: the device tree, Core Audio's defaults and, for a
/// DualSense's color and a Wooting's layout, the one read command each understands (Peripherals.Native).
///
/// Windows lists one device node per HID top-level collection. They are grouped under their USB device (or their
/// Bluetooth device's container), so one product is one card, and each product is classified once: by its
/// VID/PID (or an Xbox driver), then by its name, then by its first input collection (lowest interface, then
/// collection), which is how a mouse receiver that also has a keyboard collection stays a mouse and a keyboard
/// with a mouse collection stays a keyboard. Products with no input collection (lights, hubs, card readers,
/// webcams), audio composites (their sound comes from the endpoints), anything under a virtual bus (ViGEm,
/// vJoy) and Bluetooth devices that are paired but not connected are left out. A PC's own keyboard and touchpad
/// show only when there is no other keyboard or mouse.
/// </summary>
internal static partial class Peripherals
{
    /// <summary>One card. Null fields go on the wire as null, which the page tells apart from absent.</summary>
    internal sealed record Peripheral(
        string Id,
        string Kind,
        string Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Maker,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Model,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Connection,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Role,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Battery,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Color = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Layout = null)
    {
        /// <summary>The product's device instance (native only): where its color or layout is read and cached.</summary>
        [JsonIgnore] public string? Source { get; init; }
        [JsonIgnore] public int? VendorId { get; init; }
        [JsonIgnore] public int? ProductId { get; init; }
        /// <summary>Its HID collections' device instances and usages (native only).</summary>
        [JsonIgnore] public IReadOnlyList<Collection> Collections { get; init; } = [];
        /// <summary>Where it meets the PC (<see cref="UsbPorts"/>); null off USB or where Exo can't tell.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public UsbPorts.Port? Port { get; init; }
        /// <summary>A controller the Controller Lab reads (<see cref="ControllerLab.FamilyOf"/>): its card opens a page.</summary>
        public bool Lab { get; init; }
    }

    internal sealed record Collection(string Id, int Page, int Usage);

    /// <summary>
    /// A present device node as cfgmgr32 tells it: its name is the bus-reported one (a USB product string), else the
    /// friendly name, else the description; Internal is DEVPKEY_Device_InLocalMachineContainer (the PC's own);
    /// HardwareIds only for HID nodes; Connected and Battery (0..1) only for Bluetooth nodes.
    /// </summary>
    internal sealed record DevNode(string Id, string? Parent, string? Class = null, string? Service = null, string? Name = null,
        string? Container = null, bool Internal = false, IReadOnlyList<string>? HardwareIds = null, bool? Connected = null, double? Battery = null);

    /// <summary>
    /// A default endpoint: its id, its own name ("Headphones", "VG248"), its adapter's ("High Definition Audio Device",
    /// "fifine Microphone") and PKEY_AudioEndpoint_FormFactor. Its device node is SWD\MMDEVAPI\ and the id.
    /// </summary>
    internal sealed record AudioEndpoint(string Id, bool Capture, string Name, string? Adapter, int? FormFactor);

    private sealed class Product(string key, DevNode? node, string bus)
    {
        public string Key { get; } = key;
        public DevNode? Node { get; } = node;
        /// <summary>usb, bluetooth or internal.</summary>
        public string Bus { get; } = bus;
        public string? Name { get; set; } = node?.Name;
        public int? Vid { get; set; }
        public int? Pid { get; set; }
        public bool Internal { get; set; } = node?.Internal == true;
        public bool? Connected { get; set; }
        public double? Battery { get; set; }
        public List<(int Order, Collection Hid)> Collections { get; } = [];
        /// <summary>The classes and services of every node under it (a Bluetooth device: in its container).</summary>
        public List<DevNode> Members { get; } = [];
    }

    private const string LocalMachineContainer = "{00000000-0000-0000-ffff-ffffffffffff}";

    internal static List<Peripheral> Classify(IReadOnlyList<DevNode> tree, IReadOnlyList<AudioEndpoint> audio, bool laptop)
    {
        var byId = new Dictionary<string, DevNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in tree) byId.TryAdd(node.Id, node);
        var children = tree.Where(n => n.Parent is not null).ToLookup(n => n.Parent!, StringComparer.OrdinalIgnoreCase);
        var products = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);

        foreach (var hid in tree.Where(n => n.Id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)))
        {
            if (Owner(hid, byId, tree, products) is not { } product || UsageOf(hid) is not { } usage) continue;
            product.Collections.Add((Order(hid.Id), new Collection(hid.Id, usage.Page, usage.Usage)));
        }
        // USB devices with no HID under them (an Xbox controller on xusb22 or GIP), and PS/2 keyboards and mice.
        foreach (var usb in tree.Where(IsUsbDevice))
            if (!products.ContainsKey(usb.Id) && OnUsbHost(usb, byId)) products[usb.Id] = UsbProduct(usb);
        foreach (var ps2 in tree.Where(n => n.Id.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase) && n.Class is "Keyboard" or "Mouse"))
            if (!products.ContainsKey(ps2.Id))
            {
                var product = products[ps2.Id] = new Product(ps2.Id, ps2, "internal") { Internal = true };
                product.Collections.Add((0, new Collection(ps2.Id, 0x01, ps2.Class == "Keyboard" ? 0x06 : 0x02)));
            }

        var found = new List<Peripheral>();
        var builtIn = new List<Peripheral>();
        foreach (var product in products.Values)
        {
            if (product.Bus == "usb") product.Members.AddRange(Descendants(product.Key, children));
            else if (product.Bus == "bluetooth") BluetoothDetails(product, tree);
            if (product.Connected == false || Card(product) is not { } card) continue;
            (product.Internal && card.Kind is "keyboard" or "mouse" ? builtIn : found).Add(card);
        }
        foreach (var kind in new[] { "keyboard", "mouse" })
        {
            if (found.Any(p => p.Kind == kind)) continue;
            var own = builtIn.Where(p => p.Kind == kind).ToList();
            if (laptop && own.Count > 0)
                found.Add(new Peripheral("builtin-" + kind, kind, kind == "keyboard" ? "Built-in keyboard" : "Touchpad", null, null, null, null, null));
            else found.AddRange(own);
        }
        foreach (var endpoint in audio)
            if (AudioCard(endpoint, byId, tree) is { } card) found.Add(card);

        return [.. found.OrderBy(p => Array.IndexOf(KindOrder, p.Kind)).ThenBy(p => p.Role == "input").ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static readonly string[] KindOrder = ["mouse", "keyboard", "controller", "audio"];

    // ------------------------------------------------------------------ the product a HID collection belongs to

    /// <summary>
    /// The product above a HID node: its USB device (the first USB\VID_ node above it that isn't one interface of
    /// a composite device, on a real USB host), its Bluetooth device (by container), or the PC's own ACPI device
    /// (an I2C touchpad). Null under a virtual bus (ROOT, SWD) or anywhere else.
    /// </summary>
    private static Product? Owner(DevNode hid, Dictionary<string, DevNode> byId, IReadOnlyList<DevNode> tree, Dictionary<string, Product> products)
    {
        var at = hid;
        for (var depth = 0; depth < 16 && at.Parent is { } parentId && byId.TryGetValue(parentId, out var parent); depth++, at = parent)
        {
            var id = parent.Id;
            if (IsUsbDevice(parent))
            {
                if (products.TryGetValue(id, out var known)) return known;
                return OnUsbHost(parent, byId) ? products[id] = UsbProduct(parent) : null;
            }
            if (Enumerator(id) is "BTHENUM" or "BTHLE" or "BTHLEDEVICE" or "BTHHFENUM")
            {
                var key = parent.Container is { } c && !c.Equals(LocalMachineContainer, StringComparison.OrdinalIgnoreCase) ? c : id;
                if (products.TryGetValue(key, out var known)) return known;
                var named = tree.FirstOrDefault(n => n.Container is not null && n.Container.Equals(key, StringComparison.OrdinalIgnoreCase)
                    && (n.Id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase) || n.Id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)));
                return products[key] = new Product(key, named ?? parent, "bluetooth");
            }
            if (Enumerator(id) is "ROOT" or "SWD") return null;
            if (Enumerator(id) == "ACPI")
                return products.TryGetValue(id, out var known) ? known : products[id] = new Product(id, parent, "internal") { Internal = true };
        }
        return null;
    }

    private static Product UsbProduct(DevNode node)
    {
        var (vid, pid) = UsbIds(node.Id);
        return new Product(node.Id, node, "usb") { Vid = vid, Pid = pid };
    }

    /// <summary>A USB device itself: USB\VID_xxxx&amp;PID_yyyy\ with no &amp;MI_ (one interface of a composite device).</summary>
    private static bool IsUsbDevice(DevNode node) =>
        node.Id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) && node.Id.Split('\\') is [_, var device, ..]
        && !device.Contains("&MI_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Under a real USB host controller's root hub: ViGEm's and vJoy's virtual pads hang off a ROOT bus instead.</summary>
    private static bool OnUsbHost(DevNode node, Dictionary<string, DevNode> byId)
    {
        var at = node;
        for (var depth = 0; depth < 16 && at.Parent is { } parentId && byId.TryGetValue(parentId, out var parent); depth++, at = parent)
            if (parent.Id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static List<DevNode> Descendants(string id, ILookup<string, DevNode> children)
    {
        var list = new List<DevNode>();
        var queue = new Queue<string>([id]);
        while (queue.Count > 0 && list.Count < 256)
            foreach (var child in children[queue.Dequeue()])
            {
                list.Add(child);
                queue.Enqueue(child.Id);
            }
        return list;
    }

    /// <summary>A Bluetooth device's name, connection, battery and USB ids, from the nodes in its container.</summary>
    private static void BluetoothDetails(Product product, IReadOnlyList<DevNode> tree)
    {
        var members = tree.Where(n => n.Container is not null && n.Container.Equals(product.Key, StringComparison.OrdinalIgnoreCase)).ToList();
        product.Members.AddRange(members);
        var named = product.Node;
        product.Name = named?.Name;
        product.Connected = named?.Connected;
        product.Battery = members.Select(n => n.Battery).FirstOrDefault(b => b is not null);
        foreach (var member in members.Prepend(named))
            if (member is not null && BluetoothIds(member.Id) is ({ } vid, { } pid))
            {
                (product.Vid, product.Pid) = (vid, pid);
                break;
            }
    }

    // ------------------------------------------------------------------ classification

    private static Peripheral? Card(Product product)
    {
        var inputs = product.Collections.Where(c => KindOfUsage(c.Hid.Page, c.Hid.Usage) is not null).OrderBy(c => c.Order).ToList();
        var name = Generic(product.Name) ? null : product.Name;
        var table = Table(product.Vid, product.Pid);
        var xboxDriver = product.Members.Prepend(product.Node).Any(n => n is not null
            && (n.Service is { } s && XboxServices.Contains(s) || n.Class is "XnaComposite" or "XboxComposite"));
        var hasAudioOrCamera = product.Members.Any(n => n.Class is "MEDIA" or "AudioEndpoint" or "Camera" or "Image");

        string? kind = table?.Kind ?? (xboxDriver ? "controller" : null);
        if (kind is null && inputs.Count > 0 && name is not null) kind = KindOfName(name, inputs.Any(c => c.Hid.Page == 0x01 && c.Hid.Usage == 0x02));
        if (kind is null && inputs.Count > 0)
        {
            var first = KindOfUsage(inputs[0].Hid.Page, inputs[0].Hid.Usage);
            // A microphone, headset or webcam with a mute key exposed as a keyboard is not a keyboard.
            if (!hasAudioOrCamera || first == "controller") kind = first;
        }
        if (kind is null) return null;
        // The Xbox Wireless Adapter is a card only while a controller is on it.
        if (table?.Adapter == true && product.Members.Count == 0) return null;

        var maker = MakerOfName(name) ?? MakerOfVendor(product.Vid);
        var model = kind switch
        {
            "controller" => table?.Model ?? ControllerOfName(name) ?? (xboxDriver ? "xbox" : "gamepad"),
            "keyboard" => table?.Model ?? KeyboardSize(name),
            _ => null,
        };
        // Xbox pads call themselves "Controller", Logitech's receivers "USB Receiver": the card says whose.
        if (name is not null && BareName().IsMatch(name)) name = model == "xbox" ? "Xbox Controller" : maker is null ? name : $"{maker} {name}";
        var connection = product.Bus switch
        {
            "bluetooth" => "bluetooth",
            "usb" => table?.Wireless == true || name is not null && WirelessName().IsMatch(name) ? "wireless" : "usb",
            _ => null,
        };
        var id = product.Bus switch
        {
            "usb" => $"usb-{product.Vid:x4}-{product.Pid:x4}-{Hash(product.Key)}",
            "bluetooth" => product.Vid is { } v ? $"bt-{v:x4}-{product.Pid:x4}-{Hash(product.Key)}" : "bt-" + Hash(product.Key),
            _ => "pnp-" + Hash(product.Key),
        };
        return new Peripheral(id, kind, name ?? Fallback(kind, maker), maker, model, connection, null, product.Bus == "bluetooth" ? product.Battery : null)
        {
            Source = product.Node?.Id ?? product.Key,
            VendorId = product.Vid,
            ProductId = product.Pid,
            Collections = [.. product.Collections.OrderBy(c => c.Order).Select(c => c.Hid)],
        };
    }

    private static readonly HashSet<string> XboxServices = new(StringComparer.OrdinalIgnoreCase) { "xusb22", "xboxgip", "dc1-controller", "xinputhid" };

    /// <summary>Generic Desktop mouse, keyboard, keypad, joystick and gamepad; a digitizer's touch pad.</summary>
    private static string? KindOfUsage(int page, int usage) => (page, usage) switch
    {
        (0x01, 0x02) or (0x0D, 0x05) => "mouse",
        (0x01, 0x06) or (0x01, 0x07) => "keyboard",
        (0x01, 0x04) or (0x01, 0x05) => "controller",
        _ => null,
    };

    private static string? KindOfName(string name, bool hasMouse)
    {
        if (ControllerWords().IsMatch(name)) return "controller";
        if (KeyboardWords().IsMatch(name)) return "keyboard";
        if (MouseWords().IsMatch(name)) return "mouse";
        if (hasMouse && ReceiverWords().IsMatch(name)) return "mouse";
        return null;
    }

    [GeneratedRegex(@"\b(controller|gamepad|joystick|joy-con)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ControllerWords();
    [GeneratedRegex(@"\b(keyboard|keypad)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeyboardWords();
    [GeneratedRegex(@"\bmouse\b", RegexOptions.IgnoreCase)]
    private static partial Regex MouseWords();
    [GeneratedRegex(@"\b(dongle|receiver)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReceiverWords();
    /// <summary>A 2.4 GHz receiver's name. A controller called "Wireless Controller" on a cable is still USB.</summary>
    [GeneratedRegex(@"\b(dongle|receiver|adapter|adaptor|2\.4\s?g(hz)?|lightspeed|hyperspeed|slipstream)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WirelessName();

    private static string? ControllerOfName(string? name) => name switch
    {
        null => null,
        _ when name.Contains("DualSense Edge", StringComparison.OrdinalIgnoreCase) => "dualsense-edge",
        _ when name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) => "dualsense",
        _ when name.Contains("DualShock", StringComparison.OrdinalIgnoreCase) => "dualshock4",
        _ when name.Contains("Xbox", StringComparison.OrdinalIgnoreCase) => "xbox",
        _ when name.Contains("Pro Controller", StringComparison.OrdinalIgnoreCase) => "switch-pro",
        _ => null,
    };

    /// <summary>A keyboard's size from its name: "Mini" and 60 are 60%, TKL and 80 tenkeyless, 96 and up full size.</summary>
    internal static string? KeyboardSize(string? name)
    {
        if (name is null) return null;
        if (Regex.IsMatch(name, @"\bmini\b", RegexOptions.IgnoreCase)) return "60";
        if (Regex.IsMatch(name, @"\b(tkl|tenkeyless)\b", RegexOptions.IgnoreCase)) return "tkl";
        var size = Regex.Match(name, @"(?<![\d.])(60|65|75|80|87|96|98|100|104|108)(?![\d.])");
        return size.Success ? size.Value switch { "60" => "60", "65" => "65", "75" => "75", "80" or "87" => "tkl", _ => "full" } : null;
    }

    private sealed record Known(string Kind, string? Model = null, bool Wireless = false, bool Adapter = false);

    /// <summary>Products whose kind their name and collections don't settle (or settle wrongly).</summary>
    private static Known? Table(int? vid, int? pid)
    {
        if (vid is not { } v || pid is not { } p) return null;
        return v switch
        {
            0x31E3 => new Known("keyboard", WootingSize(p)), // Wooting: every product a keyboard (its mouse collection is analog emulation)
            0x03EB when p is 0xFF01 => new Known("keyboard", "tkl"), // Wooting One, on Atmel's ids
            0x03EB when p is 0xFF02 => new Known("keyboard", "full"), // Wooting Two
            0x361D => new Known("mouse"), // Finalmouse: its receiver also has a keyboard collection
            0x046D when p is 0xC07D or 0xC084 or 0xC085 or 0xC088 or 0xC08B or 0xC08D or 0xC090 or 0xC091 or 0xC092 or 0xC094 or 0xC09D or 0xC332
                => new Known("mouse"), // Logitech G mice on a cable
            0x054C when p is 0x05C4 or 0x09CC => new Known("controller", "dualshock4"),
            0x054C when p is 0x0BA0 => new Known("controller", "dualshock4", Wireless: true), // DualShock 4 USB wireless adaptor
            0x054C when p is 0x0CE6 => new Known("controller", "dualsense"),
            0x054C when p is 0x0DF2 => new Known("controller", "dualsense-edge"),
            0x045E when p is 0x028E or 0x028F or 0x0291 or 0x02A1 or 0x02D1 or 0x02DD or 0x02E0 or 0x02E3 or 0x02EA or 0x02FD or 0x02FF
                or 0x0B00 or 0x0B02 or 0x0B05 or 0x0B12 or 0x0B13 or 0x0B20 or 0x0B21 or 0x0B22 => new Known("controller", "xbox"),
            0x045E when p is 0x0719 or 0x02E6 or 0x02FE => new Known("controller", "xbox", Wireless: true, Adapter: true), // wireless receivers
            0x057E when p is 0x2009 or 0x2069 => new Known("controller", "switch-pro"),
            0x057E when p is 0x2006 or 0x2007 or 0x200E => new Known("controller", "gamepad"), // Joy-Con
            0x28DE when p is 0x1102 => new Known("controller", "gamepad"), // Steam Controller
            0x28DE when p is 0x1142 => new Known("controller", "gamepad", Wireless: true), // its dongle
            _ => null,
        };
    }

    /// <summary>
    /// A Wooting's size by its base product id (each has up to two alternative ids, base | 1 and base | 2, for
    /// gamepad driver compatibility): 60HE, its ARM, 60HE+ and 60HE v2 are 60%; One tenkeyless; Two full; 80HE 80%.
    /// </summary>
    internal static string? WootingSize(int pid) => (pid & 0xFFF0) switch
    {
        0x1300 or 0x1310 or 0x1320 or 0x1340 => "60",
        0x1100 => "tkl",
        0x1400 => "tkl",
        0x1200 or 0x1210 or 0x1220 or 0x1230 => "full",
        _ => null,
    };

    private static readonly Dictionary<int, string> Vendors = new()
    {
        [0x046D] = "Logitech", [0x1532] = "Razer", [0x045E] = "Microsoft", [0x054C] = "Sony", [0x057E] = "Nintendo",
        [0x2DC8] = "8BitDo", [0x1038] = "SteelSeries", [0x1B1C] = "Corsair", [0x0951] = "HyperX", [0x3434] = "Keychron",
        [0x31E3] = "Wooting", [0x361D] = "Finalmouse", [0x0B05] = "ASUS", [0x1E7D] = "ROCCAT", [0x10F5] = "Turtle Beach",
        [0x28DE] = "Valve", [0x0F0D] = "HORI", [0x0E6F] = "PDP", [0x20D6] = "PowerA", [0x24C6] = "PowerA", [0x044F] = "Thrustmaster",
        [0x0738] = "Mad Catz", [0x05AC] = "Apple", [0x17EF] = "Lenovo", [0x413C] = "Dell", [0x046A] = "Cherry", [0x0FD9] = "Elgato",
        [0x1235] = "Focusrite", [0x0B0E] = "Jabra", [0x047F] = "Poly", [0x1395] = "EPOS", [0x2516] = "Cooler Master", [0x1E71] = "NZXT",
        [0x3297] = "ZSA", [0x3142] = "Fifine",
    };

    private static string? MakerOfVendor(int? vid) => vid is { } v && Vendors.TryGetValue(v, out var maker) ? maker : null;

    /// <summary>A maker a product names itself after ("Razer Viper V3 Pro", "Xbox Wireless Controller", "fifine Microphone").</summary>
    private static string? MakerOfName(string? name)
    {
        if (name is null) return null;
        foreach (var (word, maker) in NameMakers)
            if (Regex.IsMatch(name, $@"(^|\s){Regex.Escape(word)}\b", RegexOptions.IgnoreCase)) return maker;
        return null;
    }

    private static readonly (string Word, string Maker)[] NameMakers =
    [
        ("Logitech", "Logitech"), ("Razer", "Razer"), ("SteelSeries", "SteelSeries"), ("Corsair", "Corsair"), ("HyperX", "HyperX"),
        ("Keychron", "Keychron"), ("Wooting", "Wooting"), ("Finalmouse", "Finalmouse"), ("8BitDo", "8BitDo"), ("Xbox", "Microsoft"),
        ("DualSense", "Sony"), ("DualShock", "Sony"), ("Sony", "Sony"), ("Nintendo", "Nintendo"), ("Joy-Con", "Nintendo"),
        ("ROG", "ASUS"), ("ASUS", "ASUS"), ("Glorious", "Glorious"), ("Pulsar", "Pulsar"), ("Lamzu", "Lamzu"), ("Zowie", "Zowie"),
        ("VAXEE", "VAXEE"), ("Endgame Gear", "Endgame Gear"), ("Ducky", "Ducky"), ("NuPhy", "NuPhy"), ("Cherry", "Cherry"),
        ("Turtle Beach", "Turtle Beach"), ("ROCCAT", "ROCCAT"), ("Sennheiser", "Sennheiser"), ("EPOS", "EPOS"), ("Jabra", "Jabra"),
        ("Bose", "Bose"), ("AirPods", "Apple"), ("Beats", "Apple"), ("Galaxy Buds", "Samsung"), ("Elgato", "Elgato"),
        ("Shure", "Shure"), ("RODE", "RØDE"), ("RØDE", "RØDE"), ("Audio-Technica", "Audio-Technica"), ("fifine", "Fifine"),
        ("Moondrop", "Moondrop"), ("Ziigaat", "Ziigaat"),
    ];

    /// <summary>Windows' words for a node with no product name of its own.</summary>
    private static bool Generic(string? name) => string.IsNullOrWhiteSpace(name) || GenericName().IsMatch(name);

    [GeneratedRegex(@"^(USB (Composite|Input) Device|USB Device|HID[- ]compliant .*|HID Keyboard Device|Bluetooth .*Device|Unknown USB Device.*|Wireless_Device)$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericName();

    [GeneratedRegex(@"^((game ?)?controller|game ?pad|(usb |wireless |gaming )?(keyboard|mouse|receiver))$", RegexOptions.IgnoreCase)]
    private static partial Regex BareName();

    private static string Fallback(string kind, string? maker)
    {
        var word = kind switch { "keyboard" => "keyboard", "mouse" => "mouse", _ => "controller" };
        return maker is null ? char.ToUpperInvariant(word[0]) + word[1..] : maker + " " + word;
    }

    // ------------------------------------------------------------------ audio

    /// <summary>
    /// A default endpoint as a card, or null for a virtual one (Krisp, NVIDIA Broadcast, Steam Streaming, VB-Audio:
    /// a device under ROOT or SWD that no Bluetooth device owns). Its connection is its audio device's bus: USB,
    /// Bluetooth, else HD Audio's display codecs (NVIDIA, AMD, Intel's) and a monitor's form factor are HDMI and the
    /// rest a jack. Analog headphones and IEMs look alike on a jack: both are headphones.
    /// </summary>
    private static Peripheral? AudioCard(AudioEndpoint endpoint, Dictionary<string, DevNode> byId, IReadOnlyList<DevNode> tree)
    {
        byId.TryGetValue(@"SWD\MMDEVAPI\" + endpoint.Id, out var node);
        DevNode? device = node?.Parent is { } parent && byId.TryGetValue(parent, out var d) ? d : null;
        var container = node?.Container is { } c && !c.Equals(LocalMachineContainer, StringComparison.OrdinalIgnoreCase) ? c : null;
        var family = container is null ? [] : tree.Where(n => n.Container is not null && n.Container.Equals(container, StringComparison.OrdinalIgnoreCase)).ToList();
        var bluetooth = family.Any(n => Enumerator(n.Id).StartsWith("BTH", StringComparison.Ordinal))
            || device is not null && Enumerator(device.Id).StartsWith("BTH", StringComparison.Ordinal);
        var bus = device is null ? null : Enumerator(device.Id);
        if (!bluetooth && (bus is "ROOT" or "SWD" || VirtualAudio().IsMatch(endpoint.Adapter ?? "") || VirtualAudio().IsMatch(endpoint.Name))) return null;

        var name = AudioName(endpoint);
        var monitor = endpoint.FormFactor == 9 || bus is "HDAUDIO" or "INTELAUDIO" && DisplayCodec().IsMatch(device!.Id);
        string? connection = bluetooth ? "bluetooth"
            : bus == "USB" ? (WirelessName().IsMatch(name) || Regex.IsMatch(endpoint.Adapter ?? "", @"\bwireless\b", RegexOptions.IgnoreCase) ? "wireless" : "usb")
            : bus is null ? null
            : monitor ? "hdmi" : "jack";
        var model = monitor && !endpoint.Capture ? "monitor" : endpoint.FormFactor switch
        {
            3 => "headphones",
            5 or 6 => "headset",
            4 => "microphone",
            1 or 2 or 7 or 8 when !endpoint.Capture => "speakers",
            _ => endpoint.Capture ? "microphone" : "speakers",
        };
        int? vid = null;
        for (var at = device; at is not null && vid is null; at = at.Parent is { } p && byId.TryGetValue(p, out var up) ? up : null)
            if (Enumerator(at.Id) == "USB") vid = UsbIds(at.Id).Vid;
            else if (Enumerator(at.Id) is not ("HDAUDIO" or "BTHENUM" or "BTHHFENUM" or "BTHLEDEVICE")) break;
        if (bluetooth) vid ??= family.Select(n => BluetoothIds(n.Id).Vid).FirstOrDefault(v => v is not null);
        return new Peripheral(endpoint.Id, "audio", name, MakerOfName(name) ?? MakerOfName(endpoint.Adapter) ?? MakerOfVendor(vid), model, connection,
            endpoint.Capture ? "input" : "output", bluetooth ? family.Select(n => n.Battery).FirstOrDefault(b => b is not null) : null);
    }

    /// <summary>
    /// The endpoint's own name when it says something ("VG248"); for a generic one ("Headphones", "Microphone") the
    /// product's name its adapter carries ("fifine Microphone", "WH-1000XM5"), unless that is only the driver's
    /// ("High Definition Audio Device", "Realtek(R) Audio"), which Windows shows in brackets and the card leaves out.
    /// </summary>
    internal static string AudioName(AudioEndpoint endpoint)
    {
        var own = endpoint.Name.Trim();
        if (!GenericEndpoint().IsMatch(own) || endpoint.Adapter is not { } adapter || DriverAdapter().IsMatch(adapter)) return own;
        var product = BluetoothSuffix().Replace(adapter.Trim(), "").Trim();
        return product.Length > 0 ? product : own;
    }

    [GeneratedRegex(@"^(speakers?|headphones?|headset( (earphone|microphone))?|earphones?|microphones?( array)?|((front|rear|external) )?mic( in)?|line( (in|out))?|digital (audio|output)( \(s/pdif\))?|s/?pdif|stereo mix|output|input|hands-free( ag audio)?|realtek digital output)( \d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericEndpoint();

    [GeneratedRegex(@"high definition audio|^realtek|display audio|smart sound|^usb audio|^usb pnp|conexant|synaptics|^idt\b|cirrus logic|^amd\b|nvidia|^ati\b|senary|^c-media|^generic", RegexOptions.IgnoreCase)]
    private static partial Regex DriverAdapter();

    [GeneratedRegex(@"\s+(hands-free( ag audio)?|stereo|a2dp( snk)?|avrcp)$", RegexOptions.IgnoreCase)]
    private static partial Regex BluetoothSuffix();

    [GeneratedRegex(@"krisp|nvidia broadcast|steam streaming|vb-audio|voicemeeter|\bcable (in|out)put\b", RegexOptions.IgnoreCase)]
    private static partial Regex VirtualAudio();

    /// <summary>HD Audio codecs that only drive a display: NVIDIA's, AMD's (and ATI's) and Intel's display audio.</summary>
    [GeneratedRegex(@"VEN_(10DE|1002|8086)&", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayCodec();

    // ------------------------------------------------------------------ ids and orders

    private static string Enumerator(string id) => id.Split('\\')[0].ToUpperInvariant();

    internal static (int? Vid, int? Pid) UsbIds(string id)
    {
        var m = UsbIdPattern().Match(id);
        return m.Success ? (int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : (null, null);
    }

    /// <summary>
    /// A Bluetooth service's ids ("_VID&amp;0002054c_PID&amp;0ce6", LE "_VID&amp;02045e_PID&amp;0b13"): the vendor only when
    /// its source is the USB-IF (2), whose ids the tables know; the Bluetooth SIG's company ids (1) are another list.
    /// </summary>
    internal static (int? Vid, int? Pid) BluetoothIds(string id)
    {
        var m = BluetoothIdPattern().Match(id);
        if (!m.Success) return (null, null);
        var vendor = m.Groups[1].Value;
        if (int.Parse(vendor[..^4], NumberStyles.HexNumber, CultureInfo.InvariantCulture) != 2) return (null, null);
        return (int.Parse(vendor[^4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbIdPattern();

    [GeneratedRegex(@"VID&([0-9A-F]{6}|[0-9A-F]{8})_PID&([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex BluetoothIdPattern();

    /// <summary>A HID node's top-level usage, from its hardware ids ("HID_DEVICE_UP:0001_U:0002").</summary>
    private static (int Page, int Usage)? UsageOf(DevNode hid)
    {
        foreach (var id in hid.HardwareIds ?? [])
            if (UsagePattern().Match(id) is { Success: true } m)
                return (int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return null;
    }

    [GeneratedRegex(@"^HID_DEVICE_UP:([0-9A-F]{4})_U:([0-9A-F]{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex UsagePattern();

    /// <summary>Where a collection sits in its product: its USB interface (MI_), then its collection (COL).</summary>
    private static int Order(string id)
    {
        var device = id.Split('\\') is [_, var part, ..] ? part : "";
        var mi = Regex.Match(device, @"&MI_([0-9A-F]{2})", RegexOptions.IgnoreCase);
        var col = Regex.Match(device, @"&COL([0-9A-F]{2})", RegexOptions.IgnoreCase);
        return (mi.Success ? int.Parse(mi.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0) * 256
            + (col.Success ? int.Parse(col.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>Stable across runs and names no serial or Bluetooth address.</summary>
    private static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())), 0, 6).ToLowerInvariant();

    // ------------------------------------------------------------------ what the devices answer

    /// <summary>
    /// A DualSense's firmware info (feature report 0x20, report id first): the serial read below needs hardware info
    /// (bytes 24..27, its low 16 bits) of at least 777 and the main firmware (bytes 28..31) at least 65655. A DualSense
    /// Edge numbers both its own way and isn't gated.
    /// </summary>
    internal static bool ReadsSerial(ReadOnlySpan<byte> info) =>
        info.Length >= 32 && info[0] == 0x20
        && BitConverter.ToUInt16(info[24..26]) >= 777 && BitConverter.ToUInt32(info[28..32]) >= 65655;

    /// <summary>
    /// The serial from a test command's answer (feature report 0x81: device 0x01 system, action 0x13 read serial,
    /// status 0x02 done, then the serial in ASCII up to a zero); null until it is done or when it answers another command.
    /// </summary>
    internal static string? SerialOf(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 5 || reply[0] != 0x81 || reply[1] != 0x01 || reply[2] != 0x13 || reply[3] != 0x02) return null;
        var data = reply[4..Math.Min(reply.Length, 4 + 32)];
        var end = data.IndexOf((byte)0);
        var serial = Encoding.ASCII.GetString(end < 0 ? data : data[..end]);
        return serial.Length >= 6 && serial.All(ch => ch is >= ' ' and <= '~') ? serial : null;
    }

    /// <summary>The body color a DualSense's serial carries in its characters 4 and 5.</summary>
    internal static string? ColorOf(string serial) => serial.Length < 6 ? null : serial[4..6].ToUpperInvariant() switch
    {
        "00" => "White",
        "01" => "Midnight Black",
        "02" => "Cosmic Red",
        "03" => "Nova Pink",
        "04" => "Galactic Purple",
        "05" => "Starlight Blue",
        "06" => "Grey Camouflage",
        "07" => "Volcanic Red",
        "08" => "Sterling Silver",
        "09" => "Cobalt Blue",
        "10" => "Chroma Teal",
        "11" => "Chroma Indigo",
        "12" => "Chroma Pearl",
        "30" => "30th Anniversary",
        "Z1" => "God of War Ragnarok",
        "Z2" => "Spider-Man 2",
        "Z3" => "Astro Bot",
        "Z4" => "Fortnite",
        "Z6" => "The Last of Us",
        "ZB" => "Icon Blue Limited Edition",
        "ZE" => "Genshin Impact",
        _ => null,
    };

    /// <summary>
    /// The layout byte in a Wooting's device config answer: byte 6 after its header, counting from 0. Unnumbered (usage page
    /// 0x1337, its zero report id gone): D0 DA, the command, a status, so byte 10, as Wooting's SDK reads it. Numbered
    /// as report 1 (usage page 0xFF55): 01, D1 DA, the command, a status and one more byte, so byte 12 (this PC's 60HE v2
    /// answers 01 D1 DA 13 88 07, then 00 00 00 00 00 DC 03: ANSI with a split spacebar, which it has; byte 10 is 0).
    /// Null when the answer isn't the device config's.
    /// </summary>
    internal static byte? LayoutByte(ReadOnlySpan<byte> answer) => answer switch
    {
        [0x01, 0xD1, 0xDA, 19, ..] when answer.Length > 12 => answer[12],
        [0xD0, 0xDA, 19, ..] when answer.Length > 10 => answer[10],
        _ => null,
    };

    /// <summary>A Wooting's layout byte: 0 ANSI, 1 ISO, 2 JIS, 3 ANSI and 4 ISO with a split spacebar.</summary>
    internal static string? LayoutOf(byte value) => value switch
    {
        0 => "ansi",
        1 => "iso",
        2 => "jis",
        3 => "ansi-split",
        4 => "iso-split",
        _ => null,
    };
}
