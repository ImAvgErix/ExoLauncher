using System.Diagnostics;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

internal static class SteamInstallation
{
    internal static string? ResolveRoot() => SelectRoot(Candidates(), File.Exists);

    // Candidates may be an install directory, a SteamExe value, or an App Paths
    // executable. A leftover directory must not mask a valid install elsewhere.
    internal static string? SelectRoot(IEnumerable<string?> candidates, Func<string, bool> fileExists)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var path = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'))
                    .Replace('/', Path.DirectorySeparatorChar);
                if (!Path.IsPathFullyQualified(path)) continue;
                var root = path.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(path) : path;
                if (!string.IsNullOrWhiteSpace(root) && fileExists(Path.Combine(root, "steam.exe")))
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            }
            catch { /* Continue past stale or malformed registrations. */ }
        }
        return null;
    }

    private static IEnumerable<string?> Candidates()
    {
        // A running client is authoritative when a user moved or reinstalled it.
        Process[] processes;
        try { processes = Process.GetProcessesByName("steam"); }
        catch { processes = []; }
        foreach (var process in processes)
        {
            string? executable;
            using (process) executable = ProcessHelper.TryGetExecutablePath(process);
            yield return executable;
        }
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var name in new[] { "SteamExe", "SteamPath", "InstallPath" })
                    yield return ReadValue(hive, view, @"Software\Valve\Steam", name);
                yield return ReadValue(hive, view,
                    @"Software\Microsoft\Windows\CurrentVersion\App Paths\steam.exe", "");
            }
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
    }

    private static string? ReadValue(RegistryHive hive, RegistryView view, string path, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch { return null; }
    }
}
