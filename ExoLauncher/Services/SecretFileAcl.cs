using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ExoLauncher.Services;

/// <summary>Restrict a file DACL to the current Windows user (and SYSTEM).</summary>
internal static class SecretFileAcl
{
    private const uint SeFileObject = 1;
    private const uint DaclSecurityInformation = 0x4;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
