using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Exo Notes as an app for text files, only when you ask (Notes' "Open text files with Exo"): Exo offers itself for .txt,
/// .md and .log in Windows' Open with and its Default apps, for this user (HKCU, no admin), then opens Windows' own Default
/// apps page on Exo, where you choose. Windows keeps the choice itself (UserChoice is signed), so Exo never sets it, and
/// notepad.exe stays Windows'. Withdraw takes every key back; Exo's uninstaller withdraws too.
/// </summary>
internal static class NoteTextFiles
{
    internal const string ProgId = "ExoLauncher.Note";
    /// <summary>The name Windows' Default apps lists, and its link's registeredAppUser.</summary>
    internal const string AppName = "Exo Notes";
    private const string Capabilities = @"Software\ExoLauncher\Notes\Capabilities";
    internal static readonly string[] Extensions = [".txt", ".md", ".log"];

    /// <summary>Exo is offered for text files, and whether Windows opens .txt with it now.</summary>
    internal static (bool Offered, bool Default) State()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId);
            return (key is not null, OpensTxt());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return (false, false); }
    }

    /// <summary>The app Windows opens .txt with, as you chose it (Windows' own record, read only).</summary>
    private static bool OpensTxt()
    {
        const string exts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.txt\";
        foreach (var choice in new[] { "UserChoiceLatest", "UserChoice", @"UserChoiceLatest\ProgId" })
        {
            using var key = Registry.CurrentUser.OpenSubKey(exts + choice);
            if (key?.GetValue("ProgId") is string id) return string.Equals(id, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>Exo in Open with and Default apps for the text types; true when every key was written.</summary>
    internal static bool Offer(string exe)
    {
        try
        {
            using (var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                progId.SetValue("", "Text document");
                using (var icon = progId.CreateSubKey("DefaultIcon")) icon.SetValue("", @"%SystemRoot%\System32\imageres.dll,-102", RegistryValueKind.ExpandString);
                using var command = progId.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{exe}\" --note \"%1\"");
            }
            foreach (var extension in Extensions)
            {
                using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids");
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            using (var capabilities = Registry.CurrentUser.CreateSubKey(Capabilities))
            {
                capabilities.SetValue("ApplicationName", AppName);
                capabilities.SetValue("ApplicationDescription", "Notes and text files, in Exo");
                capabilities.SetValue("ApplicationIcon", $"\"{exe}\",0");
                using var files = capabilities.CreateSubKey("FileAssociations");
                foreach (var extension in Extensions) files.SetValue(extension, ProgId);
            }
            using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) registered.SetValue(AppName, Capabilities);
            Changed();
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn("Notes: text files not offered: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Every key <see cref="Offer"/> wrote, gone; Windows' own records stay Windows'.</summary>
    internal static void Withdraw()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, throwOnMissingSubKey: false);
            foreach (var extension in Extensions)
            {
                using var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\ExoLauncher\Notes", throwOnMissingSubKey: false);
            using (var exo = Registry.CurrentUser.OpenSubKey(@"Software\ExoLauncher"))
                if (exo is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\ExoLauncher", throwOnMissingSubKey: false);
            using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
                registered?.DeleteValue(AppName, throwOnMissingValue: false);
            Changed();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn("Notes: text files not withdrawn: " + ex.GetType().Name);
        }
    }

    /// <summary>Windows' Default apps page, on Exo Notes: you pick it for .txt there.</summary>
    internal static bool OpenDefaultApps()
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(AppName)) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn("Notes: Default apps did not open: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Tells Explorer the associations changed, so Open with shows it at once.</summary>
    private static void Changed() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>The file a command line asks Notes to open: the path after --note, as Windows passes it.</summary>
    internal static string? NoteArgument(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (string.Equals(args[i], "--note", StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0) return args[i + 1];
        return null;
    }

    /// <summary>A whole command line (an activation's Arguments) as Windows splits it.</summary>
    internal static string[] Split(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally { LocalFree(argv); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
