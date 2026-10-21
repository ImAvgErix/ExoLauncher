using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Core Audio (WASAPI session) COM declarations shared by the store audio
/// silencer and the music per-app volume service. Every method preserves its
/// HRESULT so callers decide what is fatal.
/// </summary>
internal static class CoreAudioInterop
{
    internal const uint CoinitMultithreaded = 0x0;
    internal const uint CoinitApartmentThreaded = 0x2;
    internal const int ClsctxAll = 23;
    internal const uint DeviceStateActive = 0x00000001;
    /// Passed by ref into session calls; suppresses our own change callbacks.
    internal static Guid EventContext = new("1A7163C8-5D32-4CBE-A00B-44F9559A1A21");

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    internal enum EDataFlow { ERender, ECapture, EAll }
    internal enum ERole { EConsole, EMultimedia, ECommunications }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        [PreserveSig]
        int OpenPropertyStore(int accessMode, out IntPtr properties);
        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    internal interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out int deviceCount);
        [PreserveSig]
        int Item(int deviceNumber, out IMMDevice device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    internal interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(IntPtr groupingParam, uint streamFlags, out IAudioSessionControl sessionControl);
        [PreserveSig]
        int GetSimpleAudioVolume(IntPtr groupingParam, uint streamFlags, out ISimpleAudioVolume audioVolume);
        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
        [PreserveSig]
        int RegisterSessionNotification(IAudioSessionNotification sessionNotification);
        [PreserveSig]
        int UnregisterSessionNotification(IAudioSessionNotification sessionNotification);
        [PreserveSig]
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
        [PreserveSig]
        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);
        [PreserveSig]
        int GetSession(int sessionCount, out IAudioSessionControl session);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
    internal interface IAudioSessionNotification
    {
        [PreserveSig]
        int OnSessionCreated(IAudioSessionControl newSession);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    internal interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out int state);
        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig]
        int GetGroupingParam(out Guid groupingParam);
        [PreserveSig]
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    internal interface IAudioSessionControl2 : IAudioSessionControl
    {
        [PreserveSig]
        new int GetState(out int state);
        [PreserveSig]
        new int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig]
        new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig]
        new int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig]
        new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig]
        new int GetGroupingParam(out Guid groupingParam);
        [PreserveSig]
        new int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig]
        new int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig]
        new int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig]
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        [PreserveSig]
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        [PreserveSig]
        int GetProcessId(out uint processId);
        [PreserveSig]
        int IsSystemSoundsSession();
        [PreserveSig]
        int SetDuckingPreference(bool optOut);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float levelNorm, ref Guid eventContext);
        [PreserveSig]
        int GetMasterVolume(out float levelNorm);
        [PreserveSig]
        int SetMute(bool isMuted, ref Guid eventContext);
        [PreserveSig]
        int GetMute(out bool isMuted);
    }
}
