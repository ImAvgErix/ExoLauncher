using System.Diagnostics;
using Microsoft.Win32;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>Finds installed stable and lazer clients without running installers or updaters.</summary>
internal static class OsuInstallations
{
    internal static bool IsExecutable(string? path) =>
        string.Equals(Path.GetFileName(path), "osu!.exe", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<GameEntry> Scan(CancellationToken cancellation = default) => Read(Candidates(), cancellation);

    internal static IReadOnlyList<GameEntry> Read(IEnumerable<string> candidates, CancellationToken cancellation = default)
    {
        var games = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            var game = Read(candidate);
            if (game?.Path is not null && seen.Add(game.Path)) games.Add(game);
        }
        return games;
    }

    internal static GameEntry? Read(string candidate)
    {
        try
        {
            var root = RootForPath(candidate);
            if (root is null) return null;
            var executable = FindExecutable(root);
            if (executable is null) return null;
            var executableRoot = Path.GetDirectoryName(executable)!;
            var lazer = Path.GetFileName(root).Equals("osulazer", StringComparison.OrdinalIgnoreCase) ||
                !executableRoot.Equals(root, StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(root, "osu.Game.dll"));
            var title = lazer ? "osu! (lazer)" : "osu!";
            return new GameEntry {
                Id = LocalAdapter.StableLocalId(root, title), Title = title, Store = StoreKind.Local,
                Installed = true, Owned = true, CanInstall = false, Path = root, LaunchTarget = executable,
                Status = "Ready", Deps = Array.Empty<string>(), LaunchNote = "Opens the installed osu! client.",
            };
        }
        catch { return null; }
    }

    internal static string? RootForPath(string candidate)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            var value = Environment.ExpandEnvironmentVariables(candidate.Trim());
            if (value.StartsWith('"')) {
                var end = value.IndexOf('"', 1);
                if (end < 0) return null;
                value = value[1..end];
            } else {
                var end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (end >= 0) value = value[..(end + 4)];
            }
            if (!Path.IsPathFullyQualified(value)) return null;
            if (Path.GetExtension(value).Equals(".exe", StringComparison.OrdinalIgnoreCase)) {
                if (!IsExecutable(value) && !Path.GetFileName(value).Equals("Update.exe", StringComparison.OrdinalIgnoreCase)) return null;
                value = Path.GetDirectoryName(value)!;
            }
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            var folder = Path.GetFileName(root);
            if (folder.Equals("current", StringComparison.OrdinalIgnoreCase) || AppVersion(folder) is not null)
                root = Path.GetDirectoryName(root)!;
            return root;
        }
        catch { return null; }
    }

    internal static string? FindExecutable(string root)
    {
        try
        {
            // Velopack's current path remains stable across updates. Older
            // Squirrel installs keep numeric app-version directories.
            var current = Path.Combine(root, "current", "osu!.exe");
            if (File.Exists(current)) return current;
            if (Directory.Exists(root)) {
                var versions = Directory.EnumerateDirectories(root, "app-*", SearchOption.TopDirectoryOnly)
                    .Select(path => (path, version: AppVersion(Path.GetFileName(path))))
                    .Where(item => item.version is not null).OrderByDescending(item => item.version);
                foreach (var (path, _) in versions) {
                    var executable = Path.Combine(path, "osu!.exe");
                    if (File.Exists(executable)) return executable;
                }
            }
            var direct = Path.Combine(root, "osu!.exe");
            return File.Exists(direct) ? direct : null;
        }
        catch { return null; }
    }

    private static Version? AppVersion(string folder) => folder.StartsWith("app-", StringComparison.OrdinalIgnoreCase) &&
        Version.TryParse(folder[4..].Split('-')[0], out var version) ? version : null;

    private static IEnumerable<string> Candidates()
    {
        var roots = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var parent in new[] { local, Path.Combine(user, "Games"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            foreach (var name in new[] { "osu!", "osu", "osulazer" }) roots.Add(Path.Combine(parent, name));
        foreach (var drive in DriveInfo.GetDrives()) {
            try {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                foreach (var name in new[] { "osu!", "osu", "osulazer" }) {
                    roots.Add(Path.Combine(drive.RootDirectory.FullName, name));
                    roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games", name));
                }
            } catch { }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) {
            try {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                foreach (var protocol in new[] { "osu", "osulazer" }) {
                    using var key = registry.OpenSubKey($@"SOFTWARE\Classes\{protocol}\shell\open\command");
                    if (key?.GetValue(null) is string command) roots.Add(command);
                }
                using var uninstall = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in uninstall?.GetSubKeyNames() ?? []) {
                    using var key = uninstall?.OpenSubKey(name);
                    var display = key?.GetValue("DisplayName") as string;
                    if (!(display?.Equals("osu!", StringComparison.OrdinalIgnoreCase) == true ||
                        display?.StartsWith("osu!lazer", StringComparison.OrdinalIgnoreCase) == true ||
                        display?.StartsWith("osu! lazer", StringComparison.OrdinalIgnoreCase) == true ||
                        name.Equals("osulazer", StringComparison.OrdinalIgnoreCase))) continue;
                    foreach (var field in new[] { "InstallLocation", "DisplayIcon" })
                        if (key?.GetValue(field) is string path) roots.Add(path);
                }
            } catch { }
        }
        foreach (var process in Process.GetProcessesByName("osu!")) {
            using (process) try {
                if (process.MainModule?.FileName is string path) roots.Add(path);
            } catch { }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
