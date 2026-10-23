using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Services;

/// <summary>
/// Interrupts' reads of Windows (cfgmgr32: the present PCI devices, their PCI interrupt properties, driver and
/// allocated IRQs; the volume and disk IOCTLs for Windows' drive) and its one elevated write (reg.exe through cmd,
/// so Windows asks once for every device). Reads need no administrator rights.
/// </summary>
internal sealed partial class InterruptMode
{
    private const string MsiKey = @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
    private const string PriorityKey = @"\Device Parameters\Interrupt Management\Affinity Policy";

    internal sealed class NativeDevices : IDevices
    {
        private static readonly Guid PciDevice = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
        private static readonly Guid DeviceProps = new("a45c254e-df1c-4efd-8020-67d146a850e0");
        private static readonly Guid DriverProps = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
        private static readonly DevPropKey BaseClassKey = new(PciDevice, 3), SubClassKey = new(PciDevice, 4), ProgIfKey = new(PciDevice, 5);
        private static readonly DevPropKey SupportKey = new(PciDevice, 14), MessageMaxKey = new(PciDevice, 15);
        private static readonly DevPropKey DescKey = new(DeviceProps, 2), ServiceKey = new(DeviceProps, 6), FriendlyKey = new(DeviceProps, 14);
        private static readonly DevPropKey RankKey = new(DriverProps, 14);
        private static readonly DevPropKey InstanceIdKey = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
        private static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b"); // GUID_DEVINTERFACE_DISK

        public IReadOnlyList<Device> List()
        {
            const uint flags = 0x1 | 0x100; // CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT
            if (CM_Get_Device_ID_List_Size(out var length, "PCI", flags) != 0 || length == 0) return [];
            var buffer = new char[length];
            if (CM_Get_Device_ID_List("PCI", buffer, length, flags) != 0) return [];
            var list = new List<Device>();
            foreach (var id in MultiSz(buffer))
            {
                if (CM_Locate_DevNode(out var node, id, 0) != 0) continue;
                var problem = CM_Get_DevNode_Status(out _, out var code, node, 0) == 0 ? code : uint.MaxValue;
                list.Add(new Device(id, String(node, FriendlyKey) ?? String(node, DescKey) ?? "PCI device", String(node, ServiceKey), problem,
                    Byte(node, BaseClassKey), Byte(node, SubClassKey), Byte(node, ProgIfKey), UInt(node, SupportKey), UInt(node, MessageMaxKey),
                    UInt(node, RankKey), ReadMode(node), Value(id, MsiKey, MsiName), Value(id, PriorityKey, PriorityName)));
            }
            return list;
        }

        public string? Parent(string id)
        {
            if (CM_Locate_DevNode(out var node, id, 0) != 0 || CM_Get_Parent(out var parent, node, 0) != 0) return null;
            var buffer = new char[400];
            return CM_Get_Device_ID(parent, buffer, (uint)buffer.Length, 0) == 0 ? new string(buffer).TrimEnd('\0') : null;
        }

        public IReadOnlyList<string> BootDisks()
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
            if (string.IsNullOrEmpty(root)) return [];
            var numbers = VolumeDisks(@"\\.\" + root);
            if (numbers.Count == 0) return [];
            var guid = DiskInterface;
            if (CM_Get_Device_Interface_List_Size(out var length, ref guid, null, 0) != 0 || length == 0) return [];
            var buffer = new char[length];
            if (CM_Get_Device_Interface_List(ref guid, null, buffer, length, 0) != 0) return [];
            var disks = new List<string>();
            foreach (var path in MultiSz(buffer))
            {
                if (DiskNumber(path) is not { } number || !numbers.Contains(number)) continue;
                var key = InstanceIdKey;
                var data = new byte[800];
                var size = (uint)data.Length;
                if (CM_Get_Device_Interface_Property(path, ref key, out var type, data, ref size, 0) == 0 && type == 0x12)
                    disks.Add(Encoding.Unicode.GetString(data, 0, (int)size).TrimEnd('\0'));
            }
            return disks;
        }

        private static int? Value(string id, string subkey, string name)
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id + subkey);
                return key?.GetValue(name) is int value ? value : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }

        /// <summary>From the IRQs Windows gave the device: a message interrupt's number is negative (Device Manager shows 0xFFFFFFxx).</summary>
        private static Mode ReadMode(uint node)
        {
            if (CM_Get_First_Log_Conf(out var conf, node, 2 /* ALLOC_LOG_CONF */) != 0) return Mode.None;
            var line = false;
            var msi = false;
            nuint current = conf;
            var owned = false;
            try
            {
                while (CM_Get_Next_Res_Des(out var next, current, 4 /* ResType_IRQ */, out _, 0) == 0)
                {
                    if (owned) CM_Free_Res_Des_Handle(current);
                    current = next;
                    owned = true;
                    if (CM_Get_Res_Des_Data_Size(out var size, next, 0) != 0 || size < 16) continue;
                    var data = new byte[size];
                    if (CM_Get_Res_Des_Data(next, data, size, 0) != 0) continue;
                    if (BitConverter.ToInt32(data, 12) < 0) msi = true; // IRQ_DES.IRQD_Alloc_Num
                    else line = true;
                }
            }
            finally
            {
                if (owned) CM_Free_Res_Des_Handle(current);
                CM_Free_Log_Conf_Handle(conf);
            }
            return msi ? Mode.Msi : line ? Mode.Line : Mode.None;
        }

        private static List<uint> VolumeDisks(string volume)
        {
            using var handle = Open(volume);
            if (handle.IsInvalid) return [];
            var data = new byte[8 + 24 * 16]; // VOLUME_DISK_EXTENTS: a count, then DISK_EXTENTs of 24 bytes
            if (!DeviceIoControl(handle, 0x00560000 /* IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS */, IntPtr.Zero, 0, data, data.Length, out _, IntPtr.Zero)) return [];
            var count = Math.Min(BitConverter.ToInt32(data, 0), 16);
            return Enumerable.Range(0, count).Select(i => BitConverter.ToUInt32(data, 8 + i * 24)).ToList();
        }

        private static uint? DiskNumber(string path)
        {
            using var handle = Open(path);
            if (handle.IsInvalid) return null;
            var data = new byte[12]; // STORAGE_DEVICE_NUMBER: type, number, partition
            return DeviceIoControl(handle, 0x002D1080 /* IOCTL_STORAGE_GET_DEVICE_NUMBER */, IntPtr.Zero, 0, data, data.Length, out _, IntPtr.Zero)
                ? BitConverter.ToUInt32(data, 4) : null;
        }

        /// <summary>No access asked: these IOCTLs only describe the device, so no administrator rights are needed.</summary>
        private static SafeFileHandle Open(string path) => CreateFile(path, 0, 0x3 /* share read, write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);

        private static IEnumerable<string> MultiSz(char[] buffer) => new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);

        private static byte[]? Raw(uint node, DevPropKey key, uint expected)
        {
            var data = new byte[512];
            var size = (uint)data.Length;
            return CM_Get_DevNode_Property(node, ref key, out var type, data, ref size, 0) == 0 && type == expected ? data[..(int)size] : null;
        }

        private static string? String(uint node, DevPropKey key) =>
            Raw(node, key, 0x12) is { } data && Encoding.Unicode.GetString(data).TrimEnd('\0').Trim() is { Length: > 0 } text ? text : null;

        private static uint? UInt(uint node, DevPropKey key) => Raw(node, key, 0x7) is { Length: >= 4 } data ? BitConverter.ToUInt32(data) : null;

        /// <summary>The PCI class codes are bytes that Windows stores as UINT32.</summary>
        private static byte? Byte(uint node, DevPropKey key) => UInt(node, key) is <= 0xFF and var value ? (byte)value : null;
    }

    /// <summary>One cmd.exe, run as administrator, chaining a reg.exe per device: Windows asks once.</summary>
    internal sealed class ElevatedWriter : IWriter
    {
        public string? Write(IReadOnlyList<(string Id, string Name, int? Value)> values)
        {
            if (values.Count == 0) return null;
            if (values.Any(v => !PciId().IsMatch(v.Id))) return "Exo didn't recognize a device, so nothing changed.";
            var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
            var commands = values.Select(v =>
            {
                // Only Exo's two names reach reg.exe, each under its own key.
                var (subkey, name) = v.Name == PriorityName ? (PriorityKey, PriorityName) : (MsiKey, MsiName);
                var key = $"\"HKLM\\SYSTEM\\CurrentControlSet\\Enum\\{v.Id}{subkey}\"";
                return v.Value is { } value
                    ? $"\"{reg}\" add {key} /v {name} /t REG_DWORD /d {value} /f /reg:64"
                    : $"\"{reg}\" delete {key} /v {name} /f /reg:64";
            });
            try
            {
                // /s: cmd strips only the outer quotes, so each quoted path stays whole.
                using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"" + string.Join(" & ", commands) + "\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Environment.SystemDirectory,
                });
                if (process is null) return "Windows didn't start the change.";
                if (!process.WaitForExit(TimeSpan.FromSeconds(60))) return "The change didn't finish.";
                return null;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return "Windows' permission was declined, so nothing changed.";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AppLog.Warn("Interrupts: the change didn't start: " + ex.Message);
                return "Windows didn't start the change.";
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    private static extern uint CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW")]
    private static extern uint CM_Get_Device_ID_List(string filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint node, ref DevPropKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern uint CM_Get_Device_ID(uint node, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_First_Log_Conf(out nuint conf, uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Next_Res_Des(out nuint next, nuint current, uint forResource, out uint resource, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Res_Des_Data_Size(out uint size, nuint resDes, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Res_Des_Data(nuint resDes, [Out] byte[] buffer, uint size, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Free_Res_Des_Handle(nuint resDes);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Free_Log_Conf_Handle(nuint conf);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static extern uint CM_Get_Device_Interface_List_Size(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static extern uint CM_Get_Device_Interface_List(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_PropertyW")]
    private static extern uint CM_Get_Device_Interface_Property(string path, ref DevPropKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
