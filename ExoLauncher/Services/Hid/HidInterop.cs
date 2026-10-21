using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Services.Hid;

/// <summary>hid.dll, setupapi and cfgmgr32: just what listing, opening and talking to a HID collection takes.</summary>
internal static class HidInterop
{
    internal const int DigcfPresent = 0x02;
    internal const int DigcfDeviceInterface = 0x10;
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x1;
    internal const uint FileShareWrite = 0x2;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagOverlapped = 0x40000000;
    internal const int HidpStatusSuccess = 0x00110000;

    internal enum ReportType { Input = 0, Output = 1, Feature = 2 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevinfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>HIDP_BUTTON_CAPS and HIDP_VALUE_CAPS are both 72 bytes; the report ID is the byte at offset 2.</summary>
    internal const int CapsSize = 72;
    internal const int CapsReportIdOffset = 2;

    [DllImport("hid.dll")]
    internal static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetSerialNumberString(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsed);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_FreePreparsedData(nint preparsed);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetCaps(nint preparsed, out HidpCaps caps);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetButtonCaps(ReportType type, byte[] caps, ref ushort length, nint preparsed);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetValueCaps(ReportType type, byte[] caps, ref ushort length, nint preparsed);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_SetFeature(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator, nint parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(nint set, nint devInfo, ref Guid classGuid, int index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref SpDeviceInterfaceData data, nint detail, int detailSize, out int required, ref SpDevinfoData devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("cfgmgr32.dll")]
    internal static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_Device_ID(uint devInst, char[] buffer, int length, int flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    // Device arrival and removal for the HID interface class.
    internal const int CmNotifyFilterTypeDeviceInterface = 0;

    [StructLayout(LayoutKind.Explicit, Size = 416)]
    internal struct CmNotifyFilter
    {
        [FieldOffset(0)] public int CbSize;
        [FieldOffset(4)] public int Flags;
        [FieldOffset(8)] public int FilterType;
        [FieldOffset(12)] public int Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CmNotifyCallback(nint notify, nint context, int action, nint eventData, int eventDataSize);

    [DllImport("cfgmgr32.dll")]
    internal static extern int CM_Register_Notification(ref CmNotifyFilter filter, nint context, CmNotifyCallback callback, out nint notify);

    [DllImport("cfgmgr32.dll")]
    internal static extern int CM_Unregister_Notification(nint notify);
}
