using System.Text;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// What Windows keeps about programs, read once per scan and matched against each app's trail: values that name a
/// program by its path (or its app id), keys whose command, server or file is a program's, shortcuts with their targets,
/// and scheduled tasks with what they run.
/// </summary>
internal sealed partial class LeftoverScan
{
    private enum KeySource { Path, AppId, Service }

    private sealed record Sources(
        bool Deep,
        List<(string Key, string Name, string Text, bool Machine)> Values,
        List<(string Key, string Text, bool Machine, KeySource Kind)> Keys,
        List<(string Path, string Target, bool Machine)> Shortcuts,
        List<(string Path, string Exe)> Tasks,
        List<(string Key, string? Name, string Target, bool Machine)> Refs);

    // Windows' records of a program by its path, in a value's name or data: the name cache, compatibility, startup, recent apps.
    private static readonly string[] UserValues =
    [
        @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
        @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
        @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\ShowJumpView",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppBadgeUpdated",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppLaunch",
    ];

    private static readonly string[] MachineValues =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders",
        @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules",
    ];

    private const string UserAssist = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
    private const string Notifications = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string GameConfig = @"HKCU\System\GameConfigStore\Children";
    private static readonly (string Path, bool Machine)[] ClassRoots = [(@"HKCU\Software\Classes", false), (@"HKLM\SOFTWARE\Classes", true), (@"HKLM\SOFTWARE\WOW6432Node\Classes", true)];
    private static readonly (string Path, bool Machine)[] AppPaths =
    [
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths", false), (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", true),
        (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths", true),
    ];
    private static readonly (string Folder, bool Deep, bool Machine)[] ShortcutFolders =
    [
        (@"{Desktop}", false, false), (@"{CommonDesktop}", false, true), (Programs, true, false), (@"{CommonPrograms}", true, true),
        (@"{AppData}\Microsoft\Internet Explorer\Quick Launch", true, false),
    ];

    // UserAssist names folders by their known-folder ids (and ROT13s every name).
    private static readonly (string Id, Environment.SpecialFolder Folder)[] KnownFolders =
    [
        ("{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.SpecialFolder.ProgramFiles), ("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.SpecialFolder.ProgramFilesX86),
        ("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.SpecialFolder.Windows), ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.SpecialFolder.System),
        ("{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", Environment.SpecialFolder.LocalApplicationData), ("{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", Environment.SpecialFolder.ApplicationData),
    ];

    private Sources ReadSources(bool deep)
    {
        var values = new List<(string, string, string, bool)>();
        var keys = new List<(string, string, bool, KeySource)>();
        foreach (var path in UserValues) ReadValues(path, values, machine: false);
        foreach (var path in MachineValues) ReadValues(path, values, machine: true);
        ReadSubKeys(UserAssist, (guid, key) =>
        {
            using var count = key.OpenSubKey("Count");
            if (count is null) return;
            foreach (var name in count.GetValueNames())
                values.Add(($@"{UserAssist}\{guid}\Count", name, KnownFolder(Rot13(name)), false));
        });
        foreach (var (root, machine) in ClassRoots)
        {
            // A program's link or file handler: its class key, by the command it opens with.
            ReadSubKeys(root, (name, key) => Command(root + '\\' + name, key, machine));
            ReadSubKeys(root + @"\Applications", (name, key) => Command(root + @"\Applications\" + name, key, machine));
        }
        foreach (var (root, machine) in AppPaths)
            ReadSubKeys(root, (name, key) => { if (key.GetValue(null) is string exe) keys.Add((root + '\\' + name, exe, machine, KeySource.Path)); });
        ReadSubKeys(GameConfig, (name, key) => { if (key.GetValue("MatchedExeFullPath") is string exe) keys.Add((GameConfig + '\\' + name, exe, false, KeySource.Path)); });
        // Default programs: a client (browser, mail, media player) by its command, and an app registered for Default
        // apps by what its capabilities name.
        const string Clients = @"HKLM\SOFTWARE\Clients";
        ReadSubKeys(Clients, (type, kind) =>
        {
            foreach (var name in kind.GetSubKeyNames())
            {
                using var command = kind.OpenSubKey(name + @"\shell\open\command");
                if (command?.GetValue(null) is string text) keys.Add(($@"{Clients}\{type}\{name}", text, true, KeySource.Path));
            }
        });
        foreach (var (hive, machine) in new[] { ("HKCU", false), ("HKLM", true) })
        {
            var registered = $@"{hive}\Software\RegisteredApplications";
            try
            {
                using var key = Open(registered, writable: false);
                foreach (var name in key?.GetValueNames() ?? [])
                {
                    if (key!.GetValue(name) is not string capabilities) continue;
                    using var described = Open($@"{hive}\{capabilities}", writable: false);
                    var text = string.Join(" ", new[] { "ApplicationIcon", "ApplicationName", "ApplicationDescription" }.Select(value => described?.GetValue(value) as string).OfType<string>());
                    if (text.Length > 0) values.Add((registered, name, text, machine));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
        ReadSubKeys(Notifications, (name, _) => keys.Add((Notifications + '\\' + name, name, false, KeySource.AppId)));
        var tasks = new List<(string, string)>();
        var refs = new List<(string, string?, string, bool)>();
        if (deep)
        {
            foreach (var (root, machine) in ClassRoots)
            {
                ReadSubKeys(root + @"\CLSID", (id, key) =>
                {
                    foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                    {
                        using var file = key.OpenSubKey(server);
                        if (file?.GetValue(null) is string path) { keys.Add(($@"{root}\CLSID\{id}", path, machine, KeySource.Path)); return; }
                    }
                });
                ReadSubKeys(root + @"\TypeLib", (id, key) =>
                {
                    foreach (var version in key.GetSubKeyNames())
                    {
                        using var lcid = key.OpenSubKey(version + @"\0");
                        foreach (var bits in new[] { "win64", "win32" })
                            if (lcid?.OpenSubKey(bits)?.GetValue(null) is string path) { keys.Add(($@"{root}\TypeLib\{id}", path, machine, KeySource.Path)); return; }
                    }
                });
            }
            const string Services = @"HKLM\SYSTEM\CurrentControlSet\Services";
            ReadSubKeys(Services, (name, key) =>
            {
                if (key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string image) keys.Add((Services + '\\' + name, image, true, KeySource.Service));
            });
            foreach (var log in new[] { "Application", "System" })
                ReadSubKeys($@"{Services}\EventLog\{log}", (name, key) =>
                {
                    if (key.GetValue("EventMessageFile") is string file) keys.Add(($@"{Services}\EventLog\{log}\{name}", file, true, KeySource.Path));
                });
            // What names a COM class or a file type: shell extension handlers and their approval, extensions and Open With.
            foreach (var (root, machine) in ClassRoots)
            {
                foreach (var type in new[] { "*", "Directory", @"Directory\Background", "Folder", "Drive", "AllFilesystemObjects", "lnkfile" })
                foreach (var handlers in new[] { "ContextMenuHandlers", "DragDropHandlers", "PropertySheetHandlers", "CopyHookHandlers" })
                    ReadSubKeys($@"{root}\{type}\shellex\{handlers}", (name, key) =>
                        refs.Add(($@"{root}\{type}\shellex\{handlers}\{name}", null, key.GetValue(null) as string is { Length: > 0 } clsid ? clsid : name, machine)));
                ReadSubKeys(root, (name, key) =>
                {
                    if (!name.StartsWith('.')) return;
                    if (key.GetValue(null) is string progId && progId.Length > 0) refs.Add(($@"{root}\{name}", "", progId, machine));
                    using var openWith = key.OpenSubKey("OpenWithProgids");
                    foreach (var progIdName in openWith?.GetValueNames() ?? [])
                        if (progIdName.Length > 0) refs.Add(($@"{root}\{name}\OpenWithProgids", progIdName, progIdName, machine));
                });
            }
            foreach (var (approved, machine) in new[] { (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", true),
                         (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", true),
                         (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", false) })
                ReadValues(approved, null, refs, machine);
            const string FileExts = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
            ReadSubKeys(FileExts, (ext, key) =>
            {
                using var openWith = key.OpenSubKey("OpenWithProgids");
                foreach (var progIdName in openWith?.GetValueNames() ?? [])
                    if (progIdName.Length > 0) refs.Add(($@"{FileExts}\{ext}\OpenWithProgids", progIdName, progIdName, false));
            });
            tasks.AddRange(_isTest ? _testTaskList : ReadTasks());
        }
        var shortcuts = new List<(string, string, bool)>();
        foreach (var (folder, recurse, machine) in ShortcutFolders)
        {
            var full = Expand(folder);
            if (!Directory.Exists(full)) continue;
            foreach (var link in Matching(full, "*.lnk", recurse))
                if (ShortcutTarget(link) is { } target) shortcuts.Add((link, target, machine));
            foreach (var link in Matching(full, "*.url", recurse))
                if (InternetShortcut(link) is { } target) shortcuts.Add((link, target, machine));
        }
        return new Sources(deep, values, keys, shortcuts, tasks, refs);

        void Command(string path, RegistryKey key, bool machine)
        {
            using var command = key.OpenSubKey(@"shell\open\command");
            if (command?.GetValue(null) is string text) keys.Add((path, text, machine, KeySource.Path));
        }
    }

    private void ReadValues(string path, List<(string, string, string, bool)> into, bool machine) => ReadValues(path, into, null, machine);

    // A key's values: with their data, or as references by their names (Approved names each shell extension by its class id).
    private void ReadValues(string path, List<(string, string, string, bool)>? values, List<(string, string?, string, bool)>? names, bool machine)
    {
        try
        {
            using var key = Open(path, writable: false);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;
                values?.Add((path, name, key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "", machine));
                names?.Add((path, name, name, machine));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            AppLog.Debug($"Leftovers: {path.Split('\\')[^1]} wasn't readable ({ex.GetType().Name}).");
        }
    }

    private void ReadSubKeys(string path, Action<string, RegistryKey> each)
    {
        try
        {
            using var key = Open(path, writable: false);
            if (key is null) return;
            foreach (var name in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub is not null) each(name, sub);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
    }

    private static string Rot13(string text) => new([.. text.Select(c => c switch
    {
        >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
        >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
        _ => c,
    })]);

    private static string KnownFolder(string text)
    {
        foreach (var (id, folder) in KnownFolders)
            if (text.StartsWith(id, StringComparison.OrdinalIgnoreCase) && Environment.GetFolderPath(folder) is { Length: > 0 } path)
                return path + text[id.Length..];
        return text;
    }

    // ---- scheduled tasks (Task Scheduler 2.0, as this user sees them) ----

    // Read in a helper process: .NET's late-bound COM crashed Exo outright inside the Task Scheduler's type info (an
    // access violation no catch stops) while a Microsoft Edge removal ran. A crash or hang there now costs only the helper.
    private const string TaskScript =
        "$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);" +
        "$s=New-Object -ComObject Schedule.Service;$s.Connect();" +
        "function W($f,$d){if($d -gt 6){return};try{foreach($t in $f.GetTasks(1)){try{foreach($a in $t.Definition.Actions){" +
        "if($a.Type -eq 0 -and $a.Path){[Console]::Out.WriteLine($t.Path+[char]9+$a.Path)}}}catch{}};" +
        "foreach($g in $f.GetFolders(0)){W $g ($d+1)}}catch{}};W ($s.GetFolder('\\')) 0";

    private static List<(string Path, string Exe)> ReadTasks()
    {
        var (_, output) = RunHelper(PowerShell, ["-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(TaskScript))], TimeSpan.FromSeconds(15));
        var tasks = ParseTasks(output);
        if (output is null) AppLog.Debug("Leftovers: tasks unreadable.");
        return tasks;
    }

    /// <summary>The helper's lines, a task's path (from \) and the program it runs with a tab between: anything else is left out.</summary>
    internal static List<(string Path, string Exe)> ParseTasks(string? output)
    {
        var tasks = new List<(string, string)>();
        foreach (var line in (output ?? "").Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0 || line[0] != '\\') continue;
            var exe = line[(tab + 1)..].TrimEnd('\r');
            if (exe.Length > 0) tasks.Add((line[..tab], exe));
        }
        return tasks;
    }

    /// <summary>Whether a task is there: schtasks' exit code, the same in every Windows language.</summary>
    private static bool TaskExists(string path) => RunHelper(SchTasks, ["/query", "/tn", path], TimeSpan.FromSeconds(10)).ExitCode == 0;

    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string SchTasks => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    /// <summary>A Windows tool run hidden, its UTF-8 output read; (-1, null) when it couldn't start or ran past <paramref name="budget"/>.</summary>
    private static (int ExitCode, string? Output) RunHelper(string file, string[] args, TimeSpan budget)
    {
        var start = new System.Diagnostics.ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return (-1, null);
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(budget))
            {
                try { ProcessTermination.Kill(process, entireProcessTree: true); } catch { }
                return (-1, null);
            }
            return (process.ExitCode, output.Wait(TimeSpan.FromSeconds(2)) ? output.Result : null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, null);
        }
    }

    // ---- shortcuts ----

    /// <summary>An internet shortcut (.url) as "link|icon": its URL= and IconFile= lines; null when it has no link.</summary>
    internal static string? InternetShortcut(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) return null;
            string? url = null, icon = null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) url ??= line[4..].Trim();
                else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) icon ??= line[9..].Trim();
            }
            return url is null ? null : $"{url}|{icon}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// A shortcut's target, read from the .lnk itself (MS-SHLLINK: the LinkInfo's local base path, else the relative path
    /// from beside the shortcut); null when it names none or isn't a shortcut.
    /// </summary>
    internal static string? ShortcutTarget(string link)
    {
        try
        {
            var info = new FileInfo(link);
            if (info.Length is < 0x4C or > 1 << 20) return null;
            var bytes = File.ReadAllBytes(link);
            if (BitConverter.ToUInt32(bytes, 0) != 0x4C) return null;
            var flags = BitConverter.ToUInt32(bytes, 0x14);
            var at = 0x4C;
            if ((flags & 0x1) != 0) at += 2 + BitConverter.ToUInt16(bytes, at);
            if ((flags & 0x2) != 0)
            {
                var start = at;
                var size = (int)BitConverter.ToUInt32(bytes, start);
                var header = BitConverter.ToUInt32(bytes, start + 4);
                if ((BitConverter.ToUInt32(bytes, start + 8) & 0x1) != 0)
                {
                    if (header >= 0x24 && BitConverter.ToUInt32(bytes, start + 0x1C) is var wide and > 0) return Unicode(bytes, start + (int)wide);
                    var local = Ansi(bytes, start + (int)BitConverter.ToUInt32(bytes, start + 0x10));
                    var suffix = Ansi(bytes, start + (int)BitConverter.ToUInt32(bytes, start + 0x18));
                    return local + suffix;
                }
                at = start + size;
            }
            // StringData: the name first (when there is one), then the path relative to the shortcut.
            var unicode = (flags & 0x80) != 0;
            if ((flags & 0x4) != 0) at += 2 + BitConverter.ToUInt16(bytes, at) * (unicode ? 2 : 1);
            if ((flags & 0x8) == 0) return null;
            var count = BitConverter.ToUInt16(bytes, at);
            var relative = unicode ? Encoding.Unicode.GetString(bytes, at + 2, count * 2) : Encoding.Default.GetString(bytes, at + 2, count);
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link)!, relative));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }

        static string Unicode(byte[] bytes, int at)
        {
            var end = at;
            while (end + 1 < bytes.Length && (bytes[end] | bytes[end + 1]) != 0) end += 2;
            return Encoding.Unicode.GetString(bytes, at, end - at);
        }

        static string Ansi(byte[] bytes, int at)
        {
            var end = Array.IndexOf(bytes, (byte)0, at);
            return Encoding.Default.GetString(bytes, at, (end < 0 ? bytes.Length : end) - at);
        }
    }
}
