using ExoLauncher.Services;
using static ExoLauncher.Services.Peripherals;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Devices room's cards could come out wrong without anything failing:
/// - a product shows once per HID collection (a Wooting has five) instead of once;
/// - a mouse receiver with a keyboard collection (Finalmouse's) shows as a keyboard, or a keyboard with a mouse
///   collection (Wooting's) as a mouse, or a product with both and no table entry is decided by whichever collection
///   Windows listed last instead of its first interface;
/// - a DualSense Edge is drawn as a plain DualSense, or called wireless because its name says "Wireless" while it is on a cable;
/// - a USB microphone (a composite with a consumer or headset collection) or a light controller shows as a USB card;
/// - a virtual pad (ViGEm, under a ROOT bus) or a virtual microphone (Krisp, NVIDIA Broadcast) shows as hardware;
/// - a Bluetooth device that is paired but off shows, or a connected one loses its battery;
/// - jack headphones are named after the driver ("High Definition Audio Device") or called speakers;
/// - a laptop's own keyboard shows beside a USB one, or vanishes when it is the only one;
/// - a DualSense's serial or a Wooting's layout byte is misread into the wrong color or layout;
/// - a keyboard's button opens the wrong app (a Wooting on Atmel's ids without Wootility, a Vial board sent to VIA, an
///   MX Keys to G HUB, a mouse given a keyboard's app), an app's page isn't https, or "iCUE Uninstall" passes for iCUE.
/// </summary>
public sealed class PeripheralsTests
{
    private const string RootHub = @"USB\ROOT_HUB30\5&2BCE96AA&0&0";
    private static readonly DevNode Hub = new(RootHub, @"PCI\VEN_1022&DEV_43EE\4&1&0&000A", "USB", "USBHUB3");

    /// <summary>A USB device, an interface per (mi), and a HID node per collection (col 0: the interface has one).</summary>
    private static List<DevNode> Usb(string vidPid, string serial, string? name, params (int Mi, int Col, int Page, int Usage)[] collections)
    {
        var device = $@"USB\{vidPid}\{serial}";
        var nodes = new List<DevNode> { new(device, RootHub, "USB", "usbccgp", name) };
        foreach (var mi in collections.Select(c => c.Mi).Distinct())
            nodes.Add(new($@"USB\{vidPid}&MI_{mi:X2}\7&1CB5B46E&0&{mi:X4}", device, "HIDClass", "HidUsb"));
        foreach (var (mi, col, page, usage) in collections)
            nodes.Add(new($@"HID\{vidPid}&MI_{mi:X2}{(col > 0 ? $"&COL{col:X2}" : "")}\8&3ACBEE22&0&{col:X4}",
                $@"USB\{vidPid}&MI_{mi:X2}\7&1CB5B46E&0&{mi:X4}", "HIDClass", "HidUsb",
                HardwareIds: [$@"HID\{vidPid}&MI_{mi:X2}", $"HID_DEVICE_UP:{page:X4}_U:{usage:X4}", "HID_DEVICE"]));
        return nodes;
    }

    private static List<Peripheral> Run(IEnumerable<DevNode> nodes, IReadOnlyList<AudioEndpoint>? audio = null, bool laptop = false) =>
        Classify([Hub, .. nodes], audio ?? [], laptop);

    [Fact]
    public void FinalmouseDongleWithAKeyboardCollectionIsOneWirelessMouse()
    {
        var cards = Run(Usb("VID_361D&PID_0300", "937AEB5354AAB2ED", "StarlightX Dongle", (0, 0, 0x01, 0x02), (1, 0, 0xFF00, 0x01), (2, 0, 0x01, 0x06)));
        var mouse = Assert.Single(cards);
        Assert.Equal(("mouse", "StarlightX Dongle", "Finalmouse", "wireless"), (mouse.Kind, mouse.Name, mouse.Maker, mouse.Connection));
        Assert.StartsWith("usb-361d-0300-", mouse.Id);
        Assert.DoesNotContain("937AEB", mouse.Id, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WootingWithAMouseCollectionIsOneSixtyPercentKeyboard()
    {
        var cards = Run(Usb("VID_31E3&PID_1342", "A02B2610W05T06100S02H07165", "Wooting 60HE v2",
            (0, 0, 0xFF00, 0x01), (1, 0, 0x01, 0x06), (2, 0, 0xFF55, 0x01), (3, 1, 0x01, 0x80), (3, 2, 0x0C, 0x01), (3, 3, 0x01, 0x02), (4, 0, 0xFF53, 0x01)));
        var keyboard = Assert.Single(cards);
        Assert.Equal(("keyboard", "Wooting 60HE v2", "Wooting", "60", "usb"), (keyboard.Kind, keyboard.Name, keyboard.Maker, keyboard.Model, keyboard.Connection));
        Assert.Contains(keyboard.Collections, c => c.Page == 0xFF55); // where its layout is asked
    }

    [Fact]
    public void DualSenseEdgeOnACableIsAUsbDualSenseEdge()
    {
        var nodes = Usb("VID_054C&PID_0DF2", "6&C1A2E2F&0&2", "DualSense Edge Wireless Controller", (3, 0, 0x01, 0x05));
        nodes.Add(new(@"USB\VID_054C&PID_0DF2&MI_00\7&26D31004&0&0000", @"USB\VID_054C&PID_0DF2\6&C1A2E2F&0&2", "MEDIA", "usbaudio"));
        var pad = Assert.Single(Run(nodes));
        Assert.Equal(("controller", "dualsense-edge", "Sony", "usb"), (pad.Kind, pad.Model, pad.Maker, pad.Connection));
        Assert.Equal((0x054C, 0x0DF2), (pad.VendorId, pad.ProductId));
    }

    [Fact]
    public void UsbMicrophoneIsOnlyItsEndpoint()
    {
        var mic = Usb("VID_3142&PID_0A80", "REV1.0", "fifine Microphone", (3, 1, 0x0C, 0x01), (3, 2, 0x0B, 0x05));
        mic.Add(new(@"USB\VID_3142&PID_0A80&MI_00\7&22FE6265&0&0000", @"USB\VID_3142&PID_0A80\REV1.0", "MEDIA", "usbaudio"));
        const string endpoint = "{0.0.1.00000000}.{0deb347c-90b6-43d6-b549-63e63b1b23a9}";
        mic.Add(new(@"SWD\MMDEVAPI\" + endpoint, @"USB\VID_3142&PID_0A80&MI_00\7&22FE6265&0&0000", "AudioEndpoint", Container: "{6459b2f3-d6bc-5a6b-b6f4-307e0ec84e7f}"));
        // A light controller with only vendor collections is no card either.
        mic.AddRange(Usb("VID_0B05&PID_1939", "9876543210", "AURA LED Controller", (2, 0, 0xFF72, 0xA1)));

        var card = Assert.Single(Run(mic, [new AudioEndpoint(endpoint, true, "Microphone", "fifine Microphone", 4)]));
        Assert.Equal(("audio", "fifine Microphone", "microphone", "usb", "input", "Fifine"), (card.Kind, card.Name, card.Model, card.Connection, card.Role, card.Maker));
        Assert.Equal(endpoint, card.Id);
    }

    [Fact]
    public void BluetoothHeadsetCarriesItsBattery()
    {
        const string container = "{1b6f3a2c-0d4e-4f51-9a8b-7c6d5e4f3a2b}";
        const string hfp = @"BTHHFENUM\BthHFPAudio\8&2D5C3A1B&0&97";
        const string endpoint = "{0.0.0.00000000}.{5a7c9e1f-2b3d-4e5f-8a9b-0c1d2e3f4a5b}";
        var nodes = new List<DevNode>
        {
            new(@"BTHENUM\DEV_001122334455\7&1A2B3C4D&0&BLUETOOTHDEVICE_001122334455", @"BTH\MS_BTHBRB\6&1&0&0", "Bluetooth", Name: "WH-1000XM5", Container: container, Connected: true),
            new(@"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&0001012d_PID&0d58\7&1&0&001122334455_C00000000", @"BTH\MS_BTHBRB\6&1&0&0", "Bluetooth", Container: container, Battery: 0.8),
            new(hfp, @"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&0001012d_PID&0d58\7&1&0&001122334455_C00000000", "MEDIA", "BthHFAud", Container: container),
            new(@"SWD\MMDEVAPI\" + endpoint, hfp, "AudioEndpoint", Container: container),
            // Its play/pause buttons: a consumer collection, which makes no card.
            new(@"HID\{0000110e-0000-1000-8000-00805f9b34fb}_VID&0001012d_PID&0d58\8&1&0&0000",
                @"BTHENUM\{0000110e-0000-1000-8000-00805f9b34fb}_VID&0001012d_PID&0d58\7&1&0&001122334455_C00000000", "HIDClass",
                Container: container, HardwareIds: ["HID_DEVICE_UP:000C_U:0001"]),
            new(@"BTHENUM\{0000110e-0000-1000-8000-00805f9b34fb}_VID&0001012d_PID&0d58\7&1&0&001122334455_C00000000", @"BTH\MS_BTHBRB\6&1&0&0", "Bluetooth", Container: container),
        };
        var card = Assert.Single(Run(nodes, [new AudioEndpoint(endpoint, false, "Headset", "WH-1000XM5 Hands-Free AG Audio", 5)]));
        Assert.Equal(("audio", "WH-1000XM5", "headset", "bluetooth", "output", 0.8), (card.Kind, card.Name, card.Model, card.Connection, card.Role, card.Battery));
    }

    [Fact]
    public void JackHeadphonesAreHeadphonesOnAJack()
    {
        const string codec = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_1168&SUBSYS_104387C5&REV_1001\5&590AA42&0&0001";
        const string endpoint = "{0.0.0.00000000}.{9709e4ab-6969-4817-ad38-1704eab1c079}";
        var nodes = new List<DevNode>
        {
            new(codec, @"PCI\VEN_1022&DEV_1487&SUBSYS_87C51043&REV_00\4&231A312E&0&0441", "MEDIA", "IntcAzAudAddService"),
            new(@"SWD\MMDEVAPI\" + endpoint, codec, "AudioEndpoint", Container: "{5bda4621-b54b-544c-9aa3-692cf93ca064}"),
        };
        var card = Assert.Single(Run(nodes, [new AudioEndpoint(endpoint, false, "Headphones", "High Definition Audio Device", 3)]));
        Assert.Equal(("Headphones", "headphones", "jack", "output", (string?)null), (card.Name, card.Model, card.Connection, card.Role, card.Maker));
    }

    [Fact]
    public void MonitorSpeakersAreHdmi()
    {
        const string codec = @"HDAUDIO\FUNC_01&VEN_10DE&DEV_009E&SUBSYS_104387BC&REV_1001\5&EE802AC&0&0001";
        const string endpoint = "{0.0.0.00000000}.{6f743e11-a53f-46fd-9b8a-dee9bb706bf8}";
        var nodes = new List<DevNode> { new(codec, @"PCI\VEN_10DE&DEV_228B\4&1", "MEDIA"), new(@"SWD\MMDEVAPI\" + endpoint, codec, "AudioEndpoint") };
        var card = Assert.Single(Run(nodes, [new AudioEndpoint(endpoint, false, "VG248", "NVIDIA High Definition Audio", 9)]));
        Assert.Equal(("VG248", "monitor", "hdmi"), (card.Name, card.Model, card.Connection));
    }

    [Fact]
    public void VirtualMicrophoneAndVirtualPadAreLeftOut()
    {
        const string endpoint = "{0.0.1.00000000}.{11111111-2222-3333-4444-555555555555}";
        var nodes = new List<DevNode>
        {
            new(@"ROOT\MEDIA\0001", @"HTREE\ROOT\0", "MEDIA", "krispaudio"),
            new(@"SWD\MMDEVAPI\" + endpoint, @"ROOT\MEDIA\0001", "AudioEndpoint"),
            // ViGEm's Xbox 360 pad: a USB device on the ViGEm bus, not under a root hub.
            new(@"ROOT\SYSTEM\0002", @"HTREE\ROOT\0", "System", "ViGEmBus"),
            new(@"USB\VID_045E&PID_028E\1&2D595CA7&0&01", @"ROOT\SYSTEM\0002", "XnaComposite", "xusb22", "Controller (XBOX 360 For Windows)"),
            new(@"HID\VID_045E&PID_028E&IG_00\2&1&0&0000", @"USB\VID_045E&PID_028E\1&2D595CA7&0&01", "HIDClass", HardwareIds: ["HID_DEVICE_UP:0001_U:0005"]),
        };
        Assert.Empty(Run(nodes, [new AudioEndpoint(endpoint, true, "Microphone", "krisp microphone", 4)]));
    }

    [Fact]
    public void XboxControllerOnXInputIsXbox()
    {
        var nodes = new List<DevNode>
        {
            new(@"USB\VID_045E&PID_0B12\3032363030303130", RootHub, "XnaComposite", "xusb22", "Controller"),
        };
        var pad = Assert.Single(Run(nodes));
        Assert.Equal(("controller", "Xbox Controller", "xbox", "Microsoft", "usb"), (pad.Kind, pad.Name, pad.Model, pad.Maker, pad.Connection));
    }

    [Fact]
    public void ReceiverAndUnknownProductsGoByNameThenFirstInterface()
    {
        var cards = Run([
            // Keyboard first on the receiver, but a receiver with a mouse collection is a mouse.
            .. Usb("VID_046D&PID_C539", "1", "USB Receiver", (0, 0, 0x01, 0x06), (1, 0, 0x01, 0x02)),
            // No table entry and no telling name: its first interface decides.
            .. Usb("VID_1532&PID_00B7", "2", "Razer DeathAdder V3 Pro", (0, 0, 0x01, 0x02), (1, 0, 0x01, 0x06)),
            .. Usb("VID_1532&PID_02A5", "3", "Razer Huntsman V3 Pro TKL", (0, 0, 0x01, 0x06), (2, 0, 0x01, 0x02)),
        ]);
        Assert.Equal(3, cards.Count);
        Assert.Contains(cards, c => c is { Name: "Logitech USB Receiver", Kind: "mouse", Maker: "Logitech", Connection: "wireless" });
        Assert.Contains(cards, c => c is { Name: "Razer DeathAdder V3 Pro", Kind: "mouse", Connection: "usb" });
        Assert.Contains(cards, c => c is { Name: "Razer Huntsman V3 Pro TKL", Kind: "keyboard", Model: "tkl" });
    }

    [Fact]
    public void BluetoothControllerShowsOnlyWhileConnected()
    {
        List<DevNode> Pad(bool connected)
        {
            const string container = "{aa0e2b1c-3d4f-5a6b-7c8d-9e0f1a2b3c4d}";
            const string service = @"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6\7&1&0&A0B1C2D3E4F5_C00000000";
            return
            [
                new(@"BTHENUM\DEV_A0B1C2D3E4F5\7&1&BLUETOOTHDEVICE_A0B1C2D3E4F5", @"BTH\MS_BTHBRB\6&1", "Bluetooth", Name: "DualSense Wireless Controller",
                    Container: container, Connected: connected, Battery: 0.55),
                new(service, @"BTH\MS_BTHBRB\6&1", "HIDClass", Container: container),
                new(@"HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6\8&1&0&0000", service, "HIDClass",
                    Container: container, HardwareIds: ["HID_DEVICE_UP:0001_U:0005"]),
            ];
        }
        Assert.Empty(Run(Pad(connected: false)));
        var pad = Assert.Single(Run(Pad(connected: true)));
        Assert.Equal(("controller", "dualsense", "bluetooth", 0.55, "Sony"), (pad.Kind, pad.Model, pad.Connection, pad.Battery, pad.Maker));
        Assert.DoesNotContain("A0B1C2", pad.Id, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaptopKeyboardShowsOnlyWhenItIsTheOnlyOne()
    {
        var ownKeyboard = new DevNode(@"ACPI\MSF0001\4&1&0", @"ACPI\PNP0C09\0", "Keyboard", "i8042prt", "Standard PS/2 Keyboard", Internal: true);
        var alone = Assert.Single(Run([ownKeyboard], laptop: true));
        Assert.Equal(("keyboard", "Built-in keyboard", (string?)null), (alone.Kind, alone.Name, alone.Connection));

        var withUsb = Run([ownKeyboard, .. Usb("VID_31E3&PID_1342", "S", "Wooting 60HE v2", (1, 0, 0x01, 0x06))], laptop: true);
        Assert.Equal("Wooting 60HE v2", Assert.Single(withUsb).Name);
    }

    [Theory]
    [InlineData("A1B201C3D4E5F6", "Midnight Black")]
    [InlineData("A1B200C3D4E5F6", "White")]
    [InlineData("A1B2Z2C3D4E5F6", "Spider-Man 2")]
    [InlineData("F65C1338F10524318", "Techno Red")]        // an owner's Techno Red, as reported
    [InlineData("A1B2ZZC3D4E5F6", null)]
    public void DualSenseColorIsSerialCharactersFourAndFive(string serial, string? color) => Assert.Equal(color, ColorOf(serial));

    [Fact]
    public void SerialIsReadOnlyFromAFinishedSerialAnswer()
    {
        byte[] Reply(byte device, byte action, byte status)
        {
            var reply = new byte[64];
            (reply[0], reply[1], reply[2], reply[3]) = ((byte)0x81, device, action, status);
            "A1B201C3D4E5F6"u8.CopyTo(reply.AsSpan(4));
            return reply;
        }
        Assert.Equal("A1B201C3D4E5F6", SerialOf(Reply(0x01, 0x13, 0x02)));
        Assert.Null(SerialOf(Reply(0x01, 0x13, 0x01))); // still working
        Assert.Null(SerialOf(Reply(0x01, 0x12, 0x02))); // another command's answer
    }

    [Fact]
    public void SerialIsAskedOnlyOfFirmwareThatAnswersIt()
    {
        byte[] Info(ushort hardware, uint firmware)
        {
            var info = new byte[64];
            info[0] = 0x20;
            BitConverter.GetBytes((uint)hardware | 0x00010000u).CopyTo(info, 24);
            BitConverter.GetBytes(firmware).CopyTo(info, 28);
            return info;
        }
        Assert.True(ReadsSerial(Info(777, 65655)));
        Assert.False(ReadsSerial(Info(776, 65655)));
        Assert.False(ReadsSerial(Info(777, 65654)));
    }

    [Theory]
    [InlineData(0, "ansi")]
    [InlineData(1, "iso")]
    [InlineData(2, "jis")]
    [InlineData(3, "ansi-split")]
    [InlineData(4, "iso-split")]
    [InlineData(5, null)]
    public void WootingLayoutByte(byte value, string? layout) => Assert.Equal(layout, LayoutOf(value));

    [Fact]
    public void WootingLayoutIsReadPastEachHeader()
    {
        // A 60HE v2 with a split spacebar, numbered as report 1 (its byte 10 is 0, which would read as plain ANSI).
        byte[] numbered = [0x01, 0xD1, 0xDA, 0x13, 0x88, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDC, 0x03, 0x0C, 0x00, 0x00];
        Assert.Equal((byte)3, LayoutByte(numbered));
        // Unnumbered (its zero report id gone): byte 10, as Wooting's SDK reads it.
        byte[] unnumbered = [0xD0, 0xDA, 0x13, 0x88, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00];
        Assert.Equal((byte)1, LayoutByte(unnumbered));
        // Another command's answer, or a cut-off one, says nothing.
        Assert.Null(LayoutByte([0x01, 0xD1, 0xDA, 0x0B, 0x88, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDC, 0x03]));
        Assert.Null(LayoutByte([0x01, 0xD1, 0xDA, 0x13, 0x88]));
    }

    [Theory]
    [InlineData(0x1342, "60")]
    [InlineData(0x1300, "60")]
    [InlineData(0x1221, "full")]
    [InlineData(0x1500, null)]
    public void WootingSizeComesFromItsBaseProductId(int pid, string? size) => Assert.Equal(size, WootingSize(pid));

    private static string? AppId(string vidPid, string serial, string name, params (int Mi, int Col, int Page, int Usage)[] more) =>
        AppOf(Assert.Single(Run(Usb(vidPid, serial, name, [(0, 0, 0x01, 0x06), .. more]))))?.Id;

    [Theory]
    [InlineData("VID_31E3&PID_1342", "Wooting 60HE v2", "wootility")]
    [InlineData("VID_03EB&PID_FF01", "Wooting One", "wootility")]
    [InlineData("VID_03EB&PID_2FF4", "Atmel keyboard", null)]
    [InlineData("VID_3434&PID_0121", "Keychron Q1 Pro", "keychron")]
    [InlineData("VID_1532&PID_02A5", "Razer Huntsman V3 Pro TKL", "synapse")]
    [InlineData("VID_046D&PID_C33E", "G915 TKL", "ghub")]
    [InlineData("VID_046D&PID_B35B", "MX Keys", "options")]
    [InlineData("VID_1B1C&PID_1B49", "Corsair K70 RGB", "icue")]
    [InlineData("VID_1038&PID_1630", "SteelSeries Apex Pro TKL", "gg")]
    [InlineData("VID_04D9&PID_0169", "USB Keyboard", null)]
    public void KeyboardAppComesFromItsVendor(string vidPid, string name, string? app) => Assert.Equal(app, AppId(vidPid, "S1", name));

    [Fact]
    public void QmkBoardsGoToVialByItsSerialElseToViaByItsRawChannel()
    {
        const string vidPid = "VID_FEED&PID_6060";
        Assert.Equal("vial", AppId(vidPid, "vial:f64c2b3c", "Planck", (1, 0, 0xFF60, 0x61)));
        Assert.Equal("via", AppId(vidPid, "4A3B2C", "Planck", (1, 0, 0xFF60, 0x61)));
        Assert.Null(AppId(vidPid, "4A3B2C", "Planck", (1, 0, 0xFF00, 0x01)));
    }

    [Fact]
    public void OnlyKeyboardsHaveAnApp()
    {
        var mouse = Assert.Single(Run(Usb("VID_1532&PID_00B7", "2", "Razer DeathAdder V3 Pro", (0, 0, 0x01, 0x02))));
        Assert.Null(AppOf(mouse));
        Assert.All(KeyboardApps, app => Assert.StartsWith("https://", app.Url));
        Assert.Equal(KeyboardApps.Length, KeyboardApps.Select(app => app.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("Razer Synapse", true)]
    [InlineData("Razer Synapse 3", true)]
    [InlineData("iCUE 5.2", true)]
    [InlineData("iCUE Uninstall", false)]
    [InlineData("Uninstall Razer Synapse", false)]
    [InlineData("Razer Synapse Service", false)]
    public void MakersAppIsFoundByItsShortcutName(string link, bool found) =>
        Assert.Equal(found, ShortcutNamed(link, [.. Synapse.Shortcuts!, .. Icue.Shortcuts!]));
}
