using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>Minimal process hardening before WinUI starts.</summary>
public static class NativeProcessSecurity
{
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const uint LoadLibrarySearchUserDirs = 0x00000400;
    private const uint LoadLibrarySearchApplicationDir = 0x00000200;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    /// <summary>
    /// Drop the current working directory from native DLL search while keeping
    /// the Windows App SDK package graph usable for unpackaged WinUI.
    /// </summary>
    public static void HardenDllSearch()
    {
        try
        {
            SetDefaultDllDirectories(
                LoadLibrarySearchDefaultDirs |
                LoadLibrarySearchSystem32 |
                LoadLibrarySearchUserDirs |
                LoadLibrarySearchApplicationDir);
        }
        catch { /* best-effort on older kernels */ }
    }
}
