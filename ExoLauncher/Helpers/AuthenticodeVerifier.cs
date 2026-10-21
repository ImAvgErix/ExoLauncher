using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ExoLauncher.Helpers;

/// <summary>Validates the embedded signature and exact publisher using Windows trust policy.</summary>
internal static class AuthenticodeVerifier
{
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static bool HasPublisher(string path, params string[] expectedPublishers)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path) || !WinTrustValid(path)) return false;
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return expectedPublishers.Contains(publisher, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool WinTrustValid(string path)
    {
        var pathPointer = IntPtr.Zero;
        var filePointer = IntPtr.Zero;
        var dataPointer = IntPtr.Zero;
        var actionPointer = IntPtr.Zero;
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUni(path);
            var file = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
            };
            filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = filePointer,
                StateAction = 0, // WTD_STATEACTION_IGNORE
                ProviderFlags = 0x1000, // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            dataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPointer, false);
            actionPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(WinTrustActionGenericVerifyV2, actionPointer, false);
            return WinVerifyTrust(new IntPtr(-1), actionPointer, dataPointer) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (actionPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(actionPointer);
            if (dataPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(dataPointer);
            if (filePointer != IntPtr.Zero) Marshal.FreeCoTaskMem(filePointer);
            if (pathPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WinVerifyTrust(IntPtr window, IntPtr actionId, IntPtr trustData);

}
