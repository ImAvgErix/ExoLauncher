using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ExoLauncher.Services;

/// <summary>Restrict a file DACL to the current Windows user (and SYSTEM).</summary>
internal static class ExoSessionFileAcl
{
    private const uint SeFileObject = 1;
    private const uint DaclSecurityInformation = 0x4;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const uint OwnerSecurityInformation = 0x1;
    private const uint SddlRevision1 = 1;

    internal static void RestrictToCurrentUser(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("No Windows user SID.");
        // D:P = protected DACL (no inherited ACEs). FA = FILE_ALL_ACCESS.
        // SY = Local System. Everyone / Users are omitted on purpose.
        var sddl = $"D:P(A;;FA;;;{user.Value})(A;;FA;;;SY)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl, SddlRevision1, out var sd, out _))
            throw new InvalidOperationException("Could not build a restrictive ACL.");

        try
        {
            if (!GetSecurityDescriptorDacl(sd, out var present, out var dacl, out _) ||
                !present)
                throw new InvalidOperationException("Could not read the restrictive ACL.");

            var status = SetNamedSecurityInfo(
                path,
                SeFileObject,
                DaclSecurityInformation | ProtectedDaclSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                dacl,
                IntPtr.Zero);
            if (status != 0)
                throw new InvalidOperationException("Could not apply a restrictive ACL.");
        }
        finally
        {
            LocalFree(sd);
        }
    }

    internal static string ReadSddl(string path)
    {
        var status = GetNamedSecurityInfo(
            path,
            SeFileObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            out _,
            out _,
            out _,
            out _,
            out var sd);
        if (status != 0 || sd == IntPtr.Zero)
            return string.Empty;

        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(
                    sd,
                    SddlRevision1,
                    OwnerSecurityInformation | DaclSecurityInformation,
                    out var sddlPtr,
                    out _))
                return string.Empty;
            try
            {
                return Marshal.PtrToStringUni(sddlPtr) ?? string.Empty;
            }
            finally
            {
                LocalFree(sddlPtr);
            }
        }
        finally
        {
            LocalFree(sd);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint revision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out IntPtr dacl,
        [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SetNamedSecurityInfo(
        string objectName,
        uint objectType,
        uint securityInfo,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetNamedSecurityInfo(
        string objectName,
        uint objectType,
        uint securityInfo,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
        IntPtr securityDescriptor,
        uint revision,
        uint securityInformation,
        out IntPtr stringSecurityDescriptor,
        out uint stringSecurityDescriptorLen);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
