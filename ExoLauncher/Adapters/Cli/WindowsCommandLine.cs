using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExoLauncher.Adapters.Cli;

/// <summary>Parse Windows argument syntax without ever invoking a command shell.</summary>
internal static class WindowsCommandLine
{
    internal static IReadOnlyList<string> Parse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        // CommandLineToArgvW treats the executable specially. Prefix one so
        // leading whitespace and quotes retain normal argument semantics.
        var pointer = CommandLineToArgvW("exo.exe " + arguments, out var count);
        if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new string[Math.Max(0, count - 1)];
            for (var i = 1; i < count; i++)
                result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally { LocalFree(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
