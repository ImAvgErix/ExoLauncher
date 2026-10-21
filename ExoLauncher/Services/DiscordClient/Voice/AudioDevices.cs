using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Services.Hid;
using static ExoLauncher.Helpers.CoreAudioInterop;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// The microphones and speakers Windows has, by id and the name Windows shows, for Discord's
/// Input Device and Output Device menus; the endpoint a call opens (the chosen one, else Windows' default);
/// and, for Voice Tune, which microphone that is.
/// </summary>
internal static partial class AudioDevices
{
    internal sealed record Device(string Id, string Name);

    /// <summary>
    /// A microphone as the hardware tells it: the endpoint's name ("Microphone (Elgato Wave:3)"), the device's
    /// own (the product string a USB device reports, when it has one), its adapter's, its USB ids, the bus its
    /// audio device is on (USB; BTHHFENUM or BTHENUM for Bluetooth; HDAUDIO and the like for built-in; ROOT or
    /// SWD for software) and the rate Windows runs it at.
    /// </summary>
    internal sealed record MicIdentity(string EndpointName, string? DeviceName, string? InterfaceName, ushort? UsbVendor, ushort? UsbProduct, string? Bus, int? NativeSampleRate);

    // PKEY_Device_FriendlyName, PKEY_Device_DeviceDesc, PKEY_DeviceInterface_FriendlyName, PKEY_AudioEngine_DeviceFormat,
    // DEVPKEY_Device_BusReportedDeviceDesc and DEVPKEY_Device_InstanceId.
    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey DeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly PropertyKey InterfaceFriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    private static readonly PropertyKey DeviceFormat = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);
    private static readonly PropertyKey BusReportedDeviceDesc = new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);
    private static readonly PropertyKey InstanceId = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    // PKEY_AudioEndpoint_FormFactor: Headphones 3, Headset 5, Handset 6 (Speakers 1, a monitor's speakers 9, ...).
    private static readonly PropertyKey FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    internal static List<Device> List(bool capture)
    {
        var devices = new List<Device>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(capture ? EDataFlow.ECapture : EDataFlow.ERender, DeviceStateActive, out var collection) != 0) return devices;
            collection.GetCount(out var count);
            for (var i = 0; i < count; i++)
                if (collection.Item(i, out var device) == 0 && device.GetId(out var id) == 0)
                    devices.Add(new Device(id, NameOf(device) ?? id));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            AppLog.Info($"Discord voice: listing {(capture ? "microphones" : "speakers")} failed (0x{ex.HResult:X8}).");
        }
        return devices;
    }

    /// <summary>
    /// The chosen device when it is still there, else Windows' default device (Sound settings' "Choose your input/output
    /// device"), as Discord's Default is. The default communications device was used before: on a PC where that is a
    /// webcam's mic or a monitor's speakers, a call opened the wrong one.
    /// </summary>
    internal static IMMDevice? Open(IMMDeviceEnumerator enumerator, bool capture, string? chosen)
    {
        if (!string.IsNullOrEmpty(chosen) && enumerator.GetDevice(chosen, out var picked) == 0 && picked is not null
            && picked.GetState(out var state) == 0 && state == DeviceStateActive)
            return picked;
        return enumerator.GetDefaultAudioEndpoint(capture ? EDataFlow.ECapture : EDataFlow.ERender, ERole.EConsole, out var device) == 0 ? device : null;
    }

    /// <summary>
    /// The microphone a call opens, followed from its endpoint (whose device node is SWD\MMDEVAPI\ and its id)
    /// to the audio device under it, and for USB to the ids in that device's instance id or its parent's.
    /// Null when there is no microphone.
    /// </summary>
    internal static MicIdentity? Identify(string? chosenId)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (Open(enumerator, capture: true, chosenId) is not { } endpoint || endpoint.GetId(out var id) != 0) return null;
            var store = StoreOf(endpoint);
            string? deviceName = null, bus = null;
            ushort? vendor = null, product = null;
            if (CM_Locate_DevNode(out var node, @"SWD\MMDEVAPI\" + id, 0) == 0)
                // The audio device, then its parent: a USB audio function's composite device, or a vendor driver's USB device.
                for (var level = 0; level < 2 && HidInterop.CM_Get_Parent(out node, node, 0) == 0 && DevNodeString(node, InstanceId) is { } instance; level++)
                {
                    var enumeratorName = instance.Split('\\')[0].ToUpperInvariant();
                    if (level == 0)
                    {
                        deviceName = DevNodeString(node, BusReportedDeviceDesc) ?? DevNodeString(node, FriendlyName) ?? DevNodeString(node, DeviceDesc);
                        bus = enumeratorName;
                        if (bus.StartsWith("BTH", StringComparison.Ordinal)) break; // the Bluetooth radio's own USB ids lie above it
                    }
                    if (enumeratorName == "USB" && UsbIds().Match(instance) is { Success: true } ids)
                    {
                        vendor = ushort.Parse(ids.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        product = ushort.Parse(ids.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        bus = "USB";
                        break;
                    }
                }
            return new MicIdentity((store is null ? null : StringOf(store, FriendlyName)) ?? id, deviceName,
                store is null ? null : StringOf(store, InterfaceFriendlyName), vendor, product, bus, store is null ? null : RateOf(store));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            AppLog.Info($"Discord voice: identifying the microphone failed (0x{ex.HResult:X8}).");
            return null;
        }
    }

    /// <summary>
    /// Whether these speakers are on your head (headphones, a headset or a handset, as the device tells Windows), so no
    /// echo of the call can reach the microphone. Unknown counts as speakers.
    /// </summary>
    internal static bool OnYourHead(IMMDevice device)
    {
        if (StoreOf(device) is not { } store) return false;
        var key = FormFactor;
        var value = new PropVariant();
        if (store.GetValue(ref key, ref value) != 0) return false;
        try { return value.Type == 19 && (int)value.Pointer is 3 or 5 or 6; } // VT_UI4
        finally { PropVariantClear(ref value); }
    }

    /// <summary>The speakers a call opens (the chosen ones, else Windows' default) are on your head (reads the device: not for the audio threads).</summary>
    internal static bool OnYourHead(string? chosenOutput)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            return Open(enumerator, capture: false, chosenOutput) is { } device && OnYourHead(device);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return false; }
    }

    private static string? NameOf(IMMDevice device) => StoreOf(device) is { } store ? StringOf(store, FriendlyName) : null;

    private static IPropertyStore? StoreOf(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var storePointer) != 0 || storePointer == 0) return null;
        try { return (IPropertyStore)Marshal.GetObjectForIUnknown(storePointer); }
        finally { Marshal.Release(storePointer); }
    }

    private static string? StringOf(IPropertyStore store, PropertyKey key)
    {
        var value = new PropVariant();
        if (store.GetValue(ref key, ref value) != 0) return null;
        try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; } // VT_LPWSTR
        finally { PropVariantClear(ref value); }
    }

    /// <summary>The device format's rate: a VT_BLOB (its size, then a pointer) holding a WAVEFORMATEX, nSamplesPerSec 4 bytes in.</summary>
    private static int? RateOf(IPropertyStore store)
    {
        var key = DeviceFormat;
        var value = new PropVariant();
        if (store.GetValue(ref key, ref value) != 0) return null;
        try { return value.Type == 65 && (uint)value.Pointer >= 8 && value.Extra != 0 ? Marshal.ReadInt32(value.Extra, 4) : null; }
        finally { PropVariantClear(ref value); }
    }

    private static string? DevNodeString(uint node, PropertyKey key)
    {
        var buffer = new byte[512];
        var size = buffer.Length;
        if (CM_Get_DevNode_Property(node, ref key, out var type, buffer, ref size, 0) != 0 || type != 0x12) return null; // DEVPROP_TYPE_STRING
        var text = Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0').Trim();
        return text.Length > 0 ? text : null;
    }

    [GeneratedRegex("VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})")]
    private static partial Regex UsbIds();

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, int id)
    {
        public Guid Format = format;
        public int Id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public nint Pointer;
        public nint Extra;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariant value);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern int CM_Locate_DevNode(out uint node, string instanceId, int flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern int CM_Get_DevNode_Property(uint node, ref PropertyKey key, out uint type, byte[] buffer, ref int size, int flags);
}
