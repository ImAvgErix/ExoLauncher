using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services.Hid;
using Microsoft.Win32.SafeHandles;
using static ExoLauncher.Helpers.CoreAudioInterop;
using static ExoLauncher.Services.Hid.HidInterop;

namespace ExoLauncher.Services;

/// <summary>
/// Peripherals' reads of Windows: every present device node (cfgmgr32), Core Audio's default speakers and
/// microphone, and two answers a device gives once per plug-in: a DualSense's body color (its serial, through the
/// test command that reads it) and a Wooting's layout (its device config). Neither changes anything on the device,
/// neither runs while a game does, and a failure leaves the field empty. Watching: device nodes and interfaces
/// arriving or leaving, and Core Audio's endpoints and defaults, debounced to one devices.changed a second.
/// </summary>
internal static partial class Peripherals
{
    /// <summary>The cards now. Blocks on cfgmgr32, Core Audio and (once per device) a device's answer: not for the UI thread.</summary>
    internal static List<Peripheral> List(Func<bool> gameRunning)
    {
        var cards = Classify(Tree(), DefaultEndpoints(), PcHealth.Laptop(PcHealth.NativePower.HasBattery(), null));
        var present = cards.Select(c => c.Source).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in Answers.Keys.Where(k => !present.Contains(k))) Answers.TryRemove(gone, out _);
        return [.. cards.Select(card => Answered(card, gameRunning))];
    }

    // ------------------------------------------------------------------ the device tree

    private static readonly Guid DeviceProps = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly DevPropKey DescKey = new(DeviceProps, 2), HardwareIdsKey = new(DeviceProps, 3), ServiceKey = new(DeviceProps, 6),
        ClassKey = new(DeviceProps, 9), FriendlyKey = new(DeviceProps, 14);
    private static readonly DevPropKey BusReportedKey = new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);
    private static readonly DevPropKey ContainerKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly DevPropKey LocalKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 4); // DEVPKEY_Device_InLocalMachineContainer
    private static readonly DevPropKey ConnectedKey = new(new Guid("83da6326-97a6-4088-9453-a1923f573b29"), 15); // DEVPKEY_Device_IsConnected
    private static readonly DevPropKey BatteryKey = new(new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), 2); // DEVPKEY_Bluetooth_Battery, a percent

    /// <summary>Every present device node with what classification reads; names only where a product can be (USB, Bluetooth, ACPI).</summary>
    internal static List<DevNode> Tree()
    {
        const uint present = 0x100; // CM_GETIDLIST_FILTER_PRESENT
        if (CM_Get_Device_ID_List_Size(out var length, null, present) != 0 || length == 0) return [];
        var buffer = new char[length];
        if (CM_Get_Device_ID_List(null, buffer, length, present) != 0) return [];
        var nodes = new List<DevNode>();
        var idBuffer = new char[256];
        foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CM_Locate_DevNode(out var node, id, 0) != 0) continue;
            var parent = CM_Get_Parent(out var up, node, 0) == 0 && CM_Get_Device_ID(up, idBuffer, idBuffer.Length, 0) == 0
                ? new string(idBuffer, 0, Math.Max(0, Array.IndexOf(idBuffer, '\0'))) : null;
            var enumerator = id.Split('\\')[0].ToUpperInvariant();
            var bluetooth = enumerator.StartsWith("BTH", StringComparison.Ordinal);
            var named = bluetooth || enumerator is "USB" or "ACPI";
            nodes.Add(new DevNode(id, parent, String(node, ClassKey), String(node, ServiceKey),
                named ? String(node, BusReportedKey) ?? String(node, FriendlyKey) ?? String(node, DescKey) : null,
                Raw(node, ContainerKey, 0x0D) is { Length: 16 } guid ? new Guid(guid).ToString("B") : null,
                named && Raw(node, LocalKey, 0x11) is [not 0, ..],
                enumerator == "HID" ? StringList(node, HardwareIdsKey) : null,
                bluetooth ? Raw(node, ConnectedKey, 0x11) is [var connected, ..] ? connected != 0 : null : null,
                bluetooth && Raw(node, BatteryKey, 0x03) is [var percent, ..] && percent <= 100 ? percent / 100.0 : null));
        }
        return nodes;
    }

    private static byte[]? Raw(uint node, DevPropKey key, uint expected)
    {
        var data = new byte[1024];
        var size = (uint)data.Length;
        return CM_Get_DevNode_Property(node, ref key, out var type, data, ref size, 0) == 0 && type == expected ? data[..(int)size] : null;
    }

    private static string? String(uint node, DevPropKey key) =>
        Raw(node, key, 0x12) is { } data && Encoding.Unicode.GetString(data).TrimEnd('\0').Trim() is { Length: > 0 } text ? text : null;

    private static List<string>? StringList(uint node, DevPropKey key) =>
        Raw(node, key, 0x2012) is { } data ? [.. Encoding.Unicode.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries)] : null;

    // ------------------------------------------------------------------ Core Audio

    private static readonly PropertyKey EndpointNameKey = new(DeviceProps, 2); // PKEY_Device_DeviceDesc: "Headphones"
    private static readonly PropertyKey AdapterKey = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2); // PKEY_DeviceInterface_FriendlyName
    private static readonly PropertyKey FormFactorKey = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0); // PKEY_AudioEndpoint_FormFactor

    /// <summary>Windows' default speakers and microphone (Sound settings' output and input), each when there is one.</summary>
    internal static List<AudioEndpoint> DefaultEndpoints()
    {
        var list = new List<AudioEndpoint>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            foreach (var capture in new[] { false, true })
            {
                if (enumerator.GetDefaultAudioEndpoint(capture ? EDataFlow.ECapture : EDataFlow.ERender, ERole.EConsole, out var device) != 0
                    || device is null || device.GetId(out var id) != 0) continue;
                var store = StoreOf(device);
                list.Add(new AudioEndpoint(id, capture, (store is null ? null : StringOf(store, EndpointNameKey)) ?? (capture ? "Microphone" : "Speakers"),
                    store is null ? null : StringOf(store, AdapterKey), store is null ? null : UIntOf(store, FormFactorKey)));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            AppLog.Info($"Devices: reading the default audio devices failed (0x{ex.HResult:X8}).");
        }
        return list;
    }

    private static IPropertyStore? StoreOf(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var pointer) != 0 || pointer == 0) return null;
        try { return (IPropertyStore)Marshal.GetObjectForIUnknown(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static string? StringOf(IPropertyStore store, PropertyKey key)
    {
        var value = new PropVariant();
        if (store.GetValue(ref key, ref value) != 0) return null;
        try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer)?.Trim() is { Length: > 0 } text ? text : null : null; } // VT_LPWSTR
        finally { PropVariantClear(ref value); }
    }

    private static int? UIntOf(IPropertyStore store, PropertyKey key)
    {
        var value = new PropVariant();
        if (store.GetValue(ref key, ref value) != 0) return null;
        try { return value.Type == 19 ? unchecked((int)(long)value.Pointer) : null; } // VT_UI4: the low 32 bits
        finally { PropVariantClear(ref value); }
    }

    // ------------------------------------------------------------------ what a device answers

    private static readonly Guid HidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030"); // GUID_DEVINTERFACE_HID
    /// <summary>Per product instance while it is plugged in: its color or layout, null when it gave none.</summary>
    private static readonly ConcurrentDictionary<string, string?> Answers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object AskGate = new();

    private static Peripheral Answered(Peripheral card, Func<bool> gameRunning)
    {
        var color = card is { Connection: "usb", VendorId: 0x054C, ProductId: 0x0CE6 or 0x0DF2 };
        var layout = card is { Kind: "keyboard", Connection: "usb", VendorId: 0x31E3 };
        if (!color && !layout || card.Source is not { } source) return card;
        if (!Answers.TryGetValue(source, out var answer))
        {
            if (Playing(gameRunning)) return card; // asked again once the game is closed
            lock (AskGate)
                if (!Answers.TryGetValue(source, out answer))
                {
                    answer = Bounded(() => color
                        ? DualSenseSerial(card) is { } serial ? ColorOf(serial) : null
                        : WootingConfig(card) is { } config && LayoutByte(config) is { } value ? LayoutOf(value) : null);
                    Answers[source] = answer;
                }
        }
        return color ? card with { Color = answer } : card with { Layout = answer };
    }

    private static bool Playing(Func<bool> gameRunning)
    {
        try { return gameRunning(); }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException) { return true; }
    }

    /// <summary>A device read that gives up after three seconds: hid.dll's control transfers block.</summary>
    private static string? Bounded(Func<string?> read)
    {
        var task = Task.Run(() =>
        {
            try { return read(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException or ArgumentException)
            {
                return null;
            }
        });
        return task.Wait(TimeSpan.FromSeconds(3)) ? task.Result : null;
    }

    /// <summary>
    /// A DualSense's serial on USB: its firmware info (feature report 0x20) says whether it reads it; then the test
    /// command for it (feature report 0x80: device 0x01 system, action 0x13 read serial) and the answer (feature report
    /// 0x81), polled every 50 ms, at most 20 times. Null when it can't or doesn't say.
    /// </summary>
    internal static string? DualSenseSerial(Peripheral card)
    {
        if (card.Collections.FirstOrDefault(c => c is { Page: 0x01, Usage: 0x05 }) is not { } pad || InterfacePath(pad.Id) is not { } path) return null;
        using var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        var length = handle.IsInvalid ? 0 : Caps(handle)?.FeatureReportByteLength ?? 0;
        if (length < 64) return null;
        var info = new byte[length];
        info[0] = 0x20;
        // The gate is the DualSense's; a DualSense Edge numbers its hardware and firmware its own way (this PC's: 534 and
        // 0x0100008B, from 2025) and answers the same command.
        if (!HidD_GetFeature(handle, info, length) || card.ProductId == 0x0CE6 && !ReadsSerial(info)) return null;
        var ask = new byte[length];
        ask[0] = 0x80;
        ask[1] = 0x01; // system
        ask[2] = 0x13; // read the serial
        if (!HidD_SetFeature(handle, ask, length)) return null;
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(50);
            var reply = new byte[length];
            reply[0] = 0x81;
            if (HidD_GetFeature(handle, reply, length) && SerialOf(reply) is { } serial) return serial;
        }
        return null;
    }

    /// <summary>
    /// A Wooting's device config: on its config collection (usage page 0x1337, or 0xFF55 on keyboards that number
    /// their reports), the feature command 19 after the magic word (0xD0 0xDA; 0xD1 0xDA numbered as report 1), and its
    /// answer as input reports within a second. What comes back: the answer's bytes as the config collection numbers
    /// them (report 1 keeps its id byte; unnumbered reports lose their zero).
    /// </summary>
    internal static byte[]? WootingConfig(Peripheral card)
    {
        if (card.Collections.FirstOrDefault(c => c.Page is 0x1337 or 0xFF55) is not { } config || InterfacePath(config.Id) is not { } path) return null;
        var numbered = config.Page == 0xFF55;
        // The reader opens first: Windows keeps the answer for a handle that was open when it came.
        var readHandle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagOverlapped, 0);
        if (readHandle.IsInvalid) { readHandle.Dispose(); return null; }
        using var reader = new FileStream(readHandle, FileAccess.Read, 0, isAsync: true);
        using var control = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (control.IsInvalid || Caps(control) is not { } caps || caps.FeatureReportByteLength < 8 || caps.InputReportByteLength < 12) return null;
        var command = new byte[caps.FeatureReportByteLength];
        command[0] = numbered ? (byte)1 : (byte)0;
        command[1] = numbered ? (byte)0xD1 : (byte)0xD0;
        command[2] = 0xDA;
        command[3] = 19; // the device config
        if (!HidD_SetFeature(control, command, command.Length)) return null;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var answer = new List<byte>();
        var report = new byte[caps.InputReportByteLength];
        while (answer.Count < 64)
        {
            var n = reader.ReadAsync(report, limit.Token).AsTask().GetAwaiter().GetResult();
            if (n <= 0) continue;
            if (numbered && report[0] == 1) return report[..n];
            if (!numbered && report[0] == 0) answer.AddRange(report[1..n]);
        }
        return [.. answer];
    }

    private static string? InterfacePath(string instanceId)
    {
        var guid = HidInterface;
        if (CM_Get_Device_Interface_List_Size(out var length, ref guid, instanceId, 0) != 0 || length <= 1) return null;
        var buffer = new char[length];
        if (CM_Get_Device_Interface_List(ref guid, instanceId, buffer, length, 0) != 0) return null;
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static HidpCaps? Caps(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsed)) return null;
        try { return HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess ? caps : null; }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    // ------------------------------------------------------------------ watching

    private static readonly object WatchGate = new();
    private static Timer? _debounce;
    private static CmNotifyCallback? _notified;
    private static nint _instances, _interfaces;
    private static IMMDeviceEnumerator? _audio;
    private static AudioWatch? _audioWatch;

    /// <summary>
    /// From the first call on: a device node or interface arriving or leaving (a USB plug, a Bluetooth device
    /// connecting) and Core Audio's endpoints and defaults changing call changed once things settle (a second).
    /// Callbacks come on Windows' threads; changed runs on a timer's.
    /// </summary>
    internal static void Watch(Action changed)
    {
        lock (WatchGate)
        {
            if (_debounce is not null) return;
            _debounce = new Timer(_ =>
            {
                try { changed(); }
                catch (Exception ex) { AppLog.Debug("Devices: change handler failed: " + ex.Message); }
            });
            _notified = (_, _, _, _, _) => { Poke(); return 0; };
            // CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE with every instance; CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE with every class.
            var instances = new CmNotifyFilter { CbSize = 416, Flags = 0x2, FilterType = 2 };
            var interfaces = new CmNotifyFilter { CbSize = 416, Flags = 0x1, FilterType = CmNotifyFilterTypeDeviceInterface };
            var a = CM_Register_Notification(ref instances, 0, _notified, out _instances);
            var b = CM_Register_Notification(ref interfaces, 0, _notified, out _interfaces);
            if (a != 0 || b != 0) AppLog.Info($"Devices: device notifications unavailable (CR {a}, {b}).");
            try
            {
                _audio = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                _audioWatch = new AudioWatch();
                var client = Marshal.GetComInterfaceForObject(_audioWatch, typeof(AppVolume.IMMNotificationClient));
                if (_audio.RegisterEndpointNotificationCallback(client) < 0) Marshal.Release(client);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                AppLog.Info($"Devices: audio device notifications unavailable (0x{ex.HResult:X8}).");
            }
        }
    }

    private static void Poke() => _debounce?.Change(1000, Timeout.Infinite);

    [ComVisible(true)]
    private sealed class AudioWatch : AppVolume.IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState) { Poke(); return 0; }
        public int OnDeviceAdded(string deviceId) { Poke(); return 0; }
        public int OnDeviceRemoved(string deviceId) { Poke(); return 0; }
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId) { if (role == ERole.EConsole) Poke(); return 0; }
        public int OnPropertyValueChanged(string deviceId, AppVolume.PropertyKey key) => 0;
    }

    // ------------------------------------------------------------------ declarations

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

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

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    private static extern uint CM_Get_Device_ID_List_Size(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW")]
    private static extern uint CM_Get_Device_ID_List(string? filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint node, ref DevPropKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static extern uint CM_Get_Device_Interface_List_Size(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static extern uint CM_Get_Device_Interface_List(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);
}
