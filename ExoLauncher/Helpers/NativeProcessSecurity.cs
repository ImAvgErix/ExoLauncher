using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>Applies process-wide loader hardening before WinUI loads.</summary>
public static class NativeProcessSecurity
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string? pathName);

    /// <summary>
    /// Remove the current directory from native DLL search without breaking
    /// unpackaged WinUI / Windows App SDK. A blanket SetDefaultDllDirectories
    /// call strips package-graph paths and crashes XAML (missing themeresources.xaml).
    /// </summary>
    public static void HardenDllSearch()
    {
        try
        {
            _ = SetDllDirectory(string.Empty);
        }
        catch
        {
            // Must not prevent boot.
        }
    }
}
