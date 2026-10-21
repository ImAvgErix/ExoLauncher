using System.Diagnostics;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// An app Exo does the job of. <paramref name="Packages"/> are Microsoft Store package names, <paramref name="Uninstall"/>
/// matches its Apps &amp; features entries by name, <paramref name="Markers"/> are files that mean it is still there without
/// either (a copy whose entry is gone). <paramref name="Paths"/>, <paramref name="Keys"/> and <paramref name="Values"/>
/// (<c>key|name</c>) are what it leaves behind; <paramref name="Set"/> (<c>key|name=dword</c>) is written once it is gone.
/// Paths start with a place (<c>{LocalAppData}</c>); keys with HKCU or HKLM.
/// </summary>
internal sealed record ReplacedApp(
    string Id,
    string Name,
    string Detail,
    string[] Packages,
    string[] Uninstall,
    string[] Markers,
    string[] Paths,
    string[] Keys,
    string[] Values,
    string[] Set);

internal sealed record ReplacedAppState(string Id, string Name, string Detail, bool Installed, int Leftovers, long Bytes, bool NeedsAdmin);

internal sealed record ReplacedAppRemoval(bool Ok, string? Message, int Removed, int Left);

/// <summary>The Microsoft Store side, apart so tests run without touching this PC's apps.</summary>
internal interface IAppPackages
{
    IReadOnlyList<string> Find(string name);
    Task<bool> RemoveAsync(string fullName, CancellationToken ct);
}

internal sealed class WindowsAppPackages : IAppPackages
{
    public IReadOnlyList<string> Find(string name)
    {
        try
        {
            return new Windows.Management.Deployment.PackageManager().FindPackagesForUser("")
                .Where(package => package.Id.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !package.IsFramework)
                .Select(package => package.Id.FullName).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Debug("Replaced apps: package check failed: " + ex.GetType().Name);
            return [];
        }
    }

    public async Task<bool> RemoveAsync(string fullName, CancellationToken ct)
    {
        try
        {
            // Removed for this user with its data (LocalAppData\Packages\<family>): no admin rights, Windows closes it first.
            var result = await new Windows.Management.Deployment.PackageManager().RemovePackageAsync(fullName).AsTask(ct).ConfigureAwait(false);
            if (result.ExtendedErrorCode is null) return true;
            AppLog.Warn($"Replaced apps: {fullName} was not removed: 0x{result.ExtendedErrorCode.HResult:X8}");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"Replaced apps: {fullName} was not removed: {ex.GetType().Name}");
            return false;
        }
    }
}

/// <summary>
/// Deep removal of the apps Exo replaces, as Revo Uninstaller does it: the app's own uninstaller (or its Store package),
/// then every folder, file, registry key and value it leaves. Only what is listed here, never a search of the disk, so
/// nothing that belongs to something else goes with it. Spotify (Music's engine), the Xbox app (Xbox games) and the
/// NVIDIA driver stay: Exo needs them. A leftover of an app that is gone is junk; <see cref="CleanLeftovers"/> clears the
/// ones in this user's folders on its own, and a removal asks Windows once for what lies in Program Files or HKLM.
/// </summary>
internal sealed class ReplacedApps
{
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Programs = @"{AppData}\Microsoft\Windows\Start Menu\Programs";

    internal static readonly ReplacedApp[] Known =
    [
        new("discord", "Discord", "Exo has its own Discord: servers, DMs and calls",
            [], ["Discord", "Discord PTB", "Discord Canary"],
            [@"{LocalAppData}\Discord\Update.exe", @"{LocalAppData}\DiscordPTB\Update.exe", @"{LocalAppData}\DiscordCanary\Update.exe"],
            [@"{LocalAppData}\Discord", @"{LocalAppData}\DiscordPTB", @"{LocalAppData}\DiscordCanary",
             @"{AppData}\discord", @"{AppData}\discordptb", @"{AppData}\discordcanary",
             Programs + @"\Discord Inc", @"{Desktop}\Discord.lnk", @"{Desktop}\Discord PTB.lnk", @"{Desktop}\Discord Canary.lnk"],
            [@"HKCU\Software\Discord", @"HKCU\Software\Classes\discord", @"HKCU\" + Uninstall + @"\Discord",
             @"HKCU\" + Uninstall + @"\DiscordPTB", @"HKCU\" + Uninstall + @"\DiscordCanary"],
            [@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run|Discord", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run|Discord"],
            []),
        new("snipping", "Snipping Tool", "Exo snips with Print Screen and edits in Captures",
            ["Microsoft.ScreenSketch"], [], [], [], [], [],
            // Print Screen no longer opens Windows' snip, so Exo's takes it.
            [@"HKCU\Control Panel\Keyboard|PrintScreenKeyForSnippingEnabled=0"]),
        new("photos", "Photos", "Exo shows and trims your captures",
            ["Microsoft.Windows.Photos", "Microsoft.PhotosLegacy"], [], [], [], [], [], []),
        new("gamebar", "Xbox Game Bar", "Exo records clips and replays",
            ["Microsoft.XboxGamingOverlay"], [], [], [], [], [],
            // Game Bar's recording and its button off, so Windows never asks for the removed app (ms-gamingoverlay).
            [@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR|AppCaptureEnabled=0", @"HKCU\System\GameConfigStore|GameDVR_Enabled=0",
             @"HKCU\Software\Microsoft\GameBar|UseNexusForGameBarEnabled=0"]),
        new("clipchamp", "Clipchamp", "Exo trims and shares your clips",
            ["Clipchamp.Clipchamp"], [], [], [], [], [], []),
        new("nvidia", "NVIDIA App", "Exo updates the driver and sets its profile; the driver stays",
            [], ["NVIDIA App", "NVIDIA GeForce Experience"],
            [@"{ProgramFiles}\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe", @"{ProgramFiles}\NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe"],
            [@"{ProgramFiles}\NVIDIA Corporation\NVIDIA App", @"{ProgramData}\NVIDIA Corporation\NVIDIA App", @"{LocalAppData}\NVIDIA Corporation\NVIDIA App",
             @"{ProgramFiles}\NVIDIA Corporation\NVIDIA GeForce Experience", @"{ProgramData}\NVIDIA Corporation\GeForce Experience",
             @"{LocalAppData}\NVIDIA Corporation\GeForce Experience", @"{CommonPrograms}\NVIDIA Corporation\NVIDIA App.lnk",
             @"{CommonPrograms}\NVIDIA Corporation\GeForce Experience.lnk", @"{CommonDesktop}\NVIDIA App.lnk"],
            [@"HKCU\Software\NVIDIA Corporation\NVIDIA App", @"HKLM\SOFTWARE\NVIDIA Corporation\NVIDIA App", @"HKLM\SOFTWARE\NVIDIA Corporation\Global\GFExperience"],
            [], []),
    ];

    private readonly IReadOnlyDictionary<string, string> _places;
    private readonly string _hkcuBase;
    private readonly string _hklmBase;
    private readonly IAppPackages _packages;
    private readonly Func<string, CancellationToken, Task<string?>> _runUninstaller;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<bool>> _removeAsAdmin;
    private readonly bool _isTest;

    /// <param name="places">Folder per place name. <paramref name="hkcuBase"/> and <paramref name="hklmBase"/> are prefixes
    /// under HKCU for tests (empty: the real hives).</param>
    internal ReplacedApps(IReadOnlyDictionary<string, string> places, IAppPackages packages,
        Func<string, CancellationToken, Task<string?>> runUninstaller, Func<IReadOnlyList<string>, CancellationToken, Task<bool>> removeAsAdmin,
        string hkcuBase = "", string hklmBase = "")
    {
        _places = places;
        _packages = packages;
        _runUninstaller = runUninstaller;
        _removeAsAdmin = removeAsAdmin;
        _hkcuBase = hkcuBase;
        _hklmBase = hklmBase;
        _isTest = hkcuBase.Length > 0;
    }

    internal static ReplacedApps ForThisPc() => new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ["AppData"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ["Desktop"] = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ["CommonDesktop"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        ["CommonPrograms"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    }, new WindowsAppPackages(), RunUninstallerAsync, RemoveAsAdminAsync);

    internal IReadOnlyList<ReplacedAppState> Scan() => Known.Select(app =>
    {
        var left = Leftovers(app);
        var bytes = left.Where(item => !item.IsKey).Sum(item => Size(item.Path));
        return new ReplacedAppState(app.Id, app.Name, app.Detail, Installed(app), left.Count, bytes, left.Any(item => item.Machine));
    }).ToList();

    /// <summary>Clears what apps that are gone left in this user's folders and keys; the count it cleared.</summary>
    internal int CleanLeftovers()
    {
        var cleared = 0;
        foreach (var app in Known.Where(app => !Installed(app)))
        foreach (var item in Leftovers(app).Where(item => !item.Machine))
            if (Delete(item)) cleared++;
        if (cleared > 0) AppLog.Info($"Replaced apps: cleared {cleared} leftovers of apps that are gone.");
        return cleared;
    }

    internal async Task<ReplacedAppRemoval> RemoveAsync(string id, CancellationToken ct)
    {
        var app = Known.FirstOrDefault(known => known.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (app is null) return new(false, "Exo doesn't know that app.", 0, 0);
        var problem = (string?)null;
        Close(app);
        foreach (var package in app.Packages.SelectMany(_packages.Find).ToList())
            if (!await _packages.RemoveAsync(package, ct).ConfigureAwait(false)) problem ??= $"Windows didn't remove {app.Name}.";
        foreach (var command in UninstallCommands(app))
        {
            var failed = await _runUninstaller(command, ct).ConfigureAwait(false);
            if (failed is not null) problem ??= failed;
        }
        Close(app);
        if (Installed(app))
            return new(false, problem ?? $"{app.Name} is still installed.", 0, Leftovers(app).Count);

        var removed = 0;
        var machine = new List<Leftover>();
        foreach (var item in Leftovers(app))
        {
            if (item.Machine) machine.Add(item);
            else if (Delete(item)) removed++;
        }
        if (machine.Count > 0 && await _removeAsAdmin(machine.Select(AdminCommand).ToList(), ct).ConfigureAwait(false))
            removed += machine.Count(item => !Exists(item));
        foreach (var set in app.Set) Write(set);
        var left = Leftovers(app).Count;
        AppLog.Info($"Replaced apps: {app.Name} removed with {removed} leftovers; {left} left.");
        return new(left == 0, left == 0 ? null : $"{left} of {app.Name}'s leftovers need Windows' permission.", removed, left);
    }

    private bool Installed(ReplacedApp app) =>
        app.Packages.Any(name => _packages.Find(name).Count > 0) || UninstallCommands(app).Count > 0 || app.Markers.Any(marker => File.Exists(Expand(marker)));

    /// <summary>The app's own uninstallers from its Apps &amp; features entries, the quiet one where it has one.</summary>
    private List<string> UninstallCommands(ReplacedApp app)
    {
        var commands = new List<string>();
        if (app.Uninstall.Length == 0) return commands;
        foreach (var (hive, prefix) in new[] { (Registry.CurrentUser, _hkcuBase), (_isTest ? Registry.CurrentUser : Registry.LocalMachine, _hklmBase) })
        foreach (var path in new[] { Uninstall, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var key = hive.OpenSubKey(Join(prefix, path));
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var entry = key.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display || !app.Uninstall.Any(match => NameMatches(display, match))) continue;
                    // An entry whose uninstaller is gone is a leftover itself, not an installed app.
                    if ((entry.GetValue("QuietUninstallString") ?? entry.GetValue("UninstallString")) is string command && !string.IsNullOrWhiteSpace(command) &&
                        File.Exists(Adapters.OfficialInstalledLibraries.SplitCommand(command).FileName))
                        commands.Add(command);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
        return commands;
    }

    /// <summary>"NVIDIA App" matches "NVIDIA App 11.0.4" but not "NVIDIA Apparatus".</summary>
    internal static bool NameMatches(string display, string name) =>
        display.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        display.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase) && char.IsDigit(display[name.Length + 1]);

    private sealed record Leftover(string Path, bool IsKey, string? Value, bool Machine);

    private List<Leftover> Leftovers(ReplacedApp app)
    {
        var list = new List<Leftover>();
        foreach (var path in app.Paths)
        {
            var full = Expand(path);
            var item = new Leftover(full, false, null, !path.StartsWith("{LocalAppData}", StringComparison.Ordinal) &&
                                                       !path.StartsWith("{AppData}", StringComparison.Ordinal) && !path.StartsWith("{Desktop}", StringComparison.Ordinal));
            if (Exists(item)) list.Add(item);
        }
        foreach (var key in app.Keys)
        {
            var item = new Leftover(key, true, null, key.StartsWith("HKLM", StringComparison.Ordinal));
            if (Exists(item)) list.Add(item);
        }
        foreach (var value in app.Values)
        {
            var bar = value.IndexOf('|');
            var item = new Leftover(value[..bar], true, value[(bar + 1)..], value.StartsWith("HKLM", StringComparison.Ordinal));
            if (Exists(item)) list.Add(item);
        }
        return list;
    }

    private bool Exists(Leftover item)
    {
        if (!item.IsKey) return Directory.Exists(item.Path) || File.Exists(item.Path);
        try
        {
            using var key = Open(item.Path, writable: false);
            return key is not null && (item.Value is null || key.GetValue(item.Value) is not null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return false; }
    }

    private bool Delete(Leftover item)
    {
        try
        {
            if (!item.IsKey)
            {
                // A link (junction) goes on its own; Directory.Delete never follows one into its target.
                if (Directory.Exists(item.Path)) Directory.Delete(item.Path, recursive: true);
                else File.Delete(item.Path);
                return true;
            }
            var (hive, sub) = Hive(item.Path);
            if (item.Value is null)
            {
                hive.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                return true;
            }
            using var key = hive.OpenSubKey(sub, writable: true);
            key?.DeleteValue(item.Value, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Debug($"Replaced apps: a leftover stayed ({ex.GetType().Name}).");
            return false;
        }
    }

    private void Write(string set)
    {
        var bar = set.IndexOf('|');
        var equals = set.IndexOf('=', bar);
        try
        {
            var (hive, sub) = Hive(set[..bar]);
            using var key = hive.CreateSubKey(sub, writable: true);
            key.SetValue(set[(bar + 1)..equals], int.Parse(set[(equals + 1)..], System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Debug("Replaced apps: a setting wasn't written: " + ex.GetType().Name);
        }
    }

    /// <summary>One cmd step for a leftover only an administrator may remove. Paths come from <see cref="Known"/>, never from outside.</summary>
    private string AdminCommand(Leftover item)
    {
        if (item.Path.Contains('"') || item.Path.Contains('%')) throw new InvalidOperationException("Unexpected leftover path.");
        if (!item.IsKey) return Directory.Exists(item.Path) ? $"rd /s /q \"{item.Path}\"" : $"del /f /q \"{item.Path}\"";
        var key = _isTest ? @"HKCU\" + Join(_hklmBase, item.Path[5..]) : item.Path;
        return item.Value is null ? $"reg delete \"{key}\" /f" : $"reg delete \"{key}\" /v \"{item.Value}\" /f";
    }

    /// <summary>Closes the app where it runs from its own folders, so its files can go.</summary>
    private void Close(ReplacedApp app)
    {
        var folders = app.Paths.Select(Expand).Where(Directory.Exists).Select(folder => folder.TrimEnd('\\') + '\\').ToList();
        if (folders.Count == 0) return;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var exe = process.MainModule?.FileName;
                    if (exe is null || !folders.Any(folder => exe.StartsWith(folder, StringComparison.OrdinalIgnoreCase))) continue;
                    if (ProcessTermination.Kill(process, entireProcessTree: true)) process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
    }

    private string Expand(string path)
    {
        var end = path.IndexOf('}');
        return path.StartsWith('{') && end > 0 && _places.TryGetValue(path[1..end], out var root) ? root + path[(end + 1)..] : path;
    }

    private RegistryKey? Open(string path, bool writable)
    {
        var (hive, sub) = Hive(path);
        return hive.OpenSubKey(sub, writable);
    }

    private (RegistryKey Hive, string Sub) Hive(string path) => path[..4] switch
    {
        "HKCU" => (Registry.CurrentUser, Join(_hkcuBase, path[5..])),
        "HKLM" when _isTest => (Registry.CurrentUser, Join(_hklmBase, path[5..])),
        "HKLM" => (Registry.LocalMachine, path[5..]),
        _ => throw new InvalidOperationException("Unexpected hive."),
    };

    private static string Join(string prefix, string path) => prefix.Length == 0 ? path : prefix + '\\' + path;

    private static long Size(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Runs an app's own uninstaller and waits for it; the reason it failed, or null.</summary>
    private static async Task<string?> RunUninstallerAsync(string command, CancellationToken ct)
    {
        using var process = Adapters.OfficialInstalledLibraries.StartUninstallString(command, out var declined);
        if (declined) return "Windows' permission was declined.";
        if (process is null) return "The app's uninstaller didn't start.";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "The app's uninstaller didn't finish."; }
        // Squirrel (Discord) and NVIDIA's uninstallers hand off to a second process and return at once: give them a moment.
        await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Removes machine-wide leftovers in one step Windows asks permission for.</summary>
    private static async Task<bool> RemoveAsAdminAsync(IReadOnlyList<string> commands, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c " + string.Join(" & ", commands))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,
            });
            if (process is null) return false;
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }
    }
}
