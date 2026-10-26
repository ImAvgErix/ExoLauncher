using System.Diagnostics;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// An app Exo does the job of. <paramref name="Packages"/> are Microsoft Store package names, <paramref name="Uninstall"/>
/// matches its Apps &amp; features entries by name, <paramref name="Markers"/> are files that mean it is still there without
/// either (a copy whose entry is gone). <paramref name="Paths"/>, <paramref name="Keys"/> and <paramref name="Values"/>
/// (<c>key|name</c>) are what it leaves behind; <paramref name="Set"/> (<c>key|name=dword</c>) is written once it is gone.
/// <paramref name="Names"/> are its own folder and key names where programs keep theirs, and <paramref name="AppIds"/> the
/// ids Windows knows it by (notifications, recent apps): the deep clean's leads (<see cref="LeftoverScan"/>).
/// <paramref name="Undo"/> are keys its <paramref name="Set"/> wrote that go again if it is installed again.
/// Paths start with a place (<c>{LocalAppData}</c>); keys with HKCU or HKLM. A Set value is a DWORD when it is a number,
/// else text ({System} is Windows' System32).
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
    string[] Set,
    string[]? Names = null,
    string[]? AppIds = null,
    string[]? Undo = null);

/// <summary><paramref name="Found"/>: what is there now. <paramref name="Cleared"/>: what this scan or removal just cleared.</summary>
internal sealed record ReplacedAppState(string Id, string Name, string Detail, bool Installed, int Leftovers, long Bytes, bool NeedsAdmin,
    LeftoverSummary Found, LeftoverSummary? Cleared);

internal sealed record ReplacedAppRemoval(bool Ok, string? Message, int Removed, int Left, LeftoverSummary? Cleared = null);

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
/// then every folder, file, registry key and value it leaves, and once it is gone, the deep clean (<see cref="LeftoverScan"/>,
/// what Revo's advanced scan and BCU find). Only what is tied to the app, never a search of the disk by a word, so nothing
/// that belongs to something else goes with it. Spotify (Music's engine), the Xbox app (Xbox games) and the NVIDIA driver
/// stay: Exo needs them. A leftover of an app that is gone is junk; <see cref="CleanLeftovers"/> clears the ones in this
/// user's folders on its own, and a removal asks Windows once for what lies in Program Files or HKLM, and to take a Store
/// app out of Windows' image for every user, so a feature update can't bring it back (Win11Debloat and WinUtil do the same).
/// </summary>
internal sealed partial class ReplacedApps
{
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Programs = LeftoverScan.Programs;

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
            [],
            ["Discord", "DiscordPTB", "DiscordCanary"],
            ["com.squirrel.Discord.Discord", "com.squirrel.DiscordPTB.DiscordPTB", "com.squirrel.DiscordCanary.DiscordCanary"]),
        new("snipping", "Snipping Tool", "Exo snips with Print Screen and edits in Captures",
            ["Microsoft.ScreenSketch"], [], [], [], [], [],
            // Print Screen no longer opens Windows' snip, so Exo's takes it.
            [@"HKCU\Control Panel\Keyboard|PrintScreenKeyForSnippingEnabled=0"]),
        new("photos", "Photos", "Exo shows and trims your captures",
            ["Microsoft.Windows.Photos", "Microsoft.PhotosLegacy"], [], [], [], [], [], []),
        new("gamebar", "Xbox Game Bar", "Exo records clips and replays",
            ["Microsoft.XboxGamingOverlay", "Microsoft.XboxGameOverlay"], [], [], [], [], [],
            // Game Bar's recording and its button off, so Windows never asks for the removed app (ms-gamingoverlay); its
            // ms-gamebar links open nothing instead of asking which app should (Win11Debloat's fix), undone if it comes back.
            [@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR|AppCaptureEnabled=0", @"HKCU\System\GameConfigStore|GameDVR_Enabled=0",
             @"HKCU\Software\Microsoft\GameBar|UseNexusForGameBarEnabled=0",
             @"HKCU\Software\Classes\ms-gamebar|=URL:ms-gamebar", @"HKCU\Software\Classes\ms-gamebar|URL Protocol=", @"HKCU\Software\Classes\ms-gamebar|NoOpenWith=",
             @"HKCU\Software\Classes\ms-gamebar\shell\open\command|={System}\systray.exe",
             @"HKCU\Software\Classes\ms-gamebarservices|=URL:ms-gamebarservices", @"HKCU\Software\Classes\ms-gamebarservices|URL Protocol=",
             @"HKCU\Software\Classes\ms-gamebarservices|NoOpenWith=", @"HKCU\Software\Classes\ms-gamebarservices\shell\open\command|={System}\systray.exe"],
            Undo: [@"HKCU\Software\Classes\ms-gamebar", @"HKCU\Software\Classes\ms-gamebarservices"]),
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

    private readonly LeftoverScan _scan;
    private readonly IAppPackages _packages;
    private readonly Func<string, CancellationToken, Task<string?>> _runUninstaller;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<bool>> _removeAsAdmin;
    private readonly string _hkcuBase;
    private readonly string _hklmBase;
    private readonly bool _isTest;
    private IReadOnlyList<string>? _keep;

    /// <param name="places">Folder per place name. <paramref name="hkcuBase"/> and <paramref name="hklmBase"/> are prefixes
    /// under HKCU for tests (empty: the real hives).</param>
    internal ReplacedApps(IReadOnlyDictionary<string, string> places, IAppPackages packages,
        Func<string, CancellationToken, Task<string?>> runUninstaller, Func<IReadOnlyList<string>, CancellationToken, Task<bool>> removeAsAdmin,
        string hkcuBase = "", string hklmBase = "")
    {
        _scan = new LeftoverScan(places, hkcuBase, hklmBase);
        _packages = packages;
        _runUninstaller = runUninstaller;
        _removeAsAdmin = removeAsAdmin;
        _hkcuBase = hkcuBase;
        _hklmBase = hklmBase;
        _isTest = hkcuBase.Length > 0;
    }

    internal static ReplacedApps ForThisPc() => new(LeftoverScan.ThisPc(), new WindowsAppPackages(),
        (command, ct) => UninstallRun.RunAsync(command, TimeSpan.FromMinutes(10), ct), RemoveAsAdminAsync);

    /// <param name="cleared">What a clean just cleared, per app id, said with each.</param>
    internal IReadOnlyList<ReplacedAppState> Scan(IReadOnlyDictionary<string, LeftoverSummary>? cleared = null) => Known.Select(app =>
    {
        var installed = Installed(app);
        if (installed) Reinstalled(app);
        var left = Leftovers(app, installed);
        var found = LeftoverScan.Summarize(left, LeftoverScan.Sizes(left));
        return new ReplacedAppState(app.Id, app.Name, app.Detail, installed, left.Count, found.Bytes, left.Any(item => item.Machine), found,
            cleared?.GetValueOrDefault(app.Id));
    }).ToList();

    /// <summary>Clears what apps that are gone left in this user's folders and keys; what it cleared, per app.</summary>
    internal Dictionary<string, LeftoverSummary> CleanLeftovers()
    {
        var cleared = new Dictionary<string, LeftoverSummary>();
        foreach (var app in Known.Where(app => !Installed(app)))
        {
            var mine = Leftovers(app, installed: false).Where(item => !item.Machine && !item.Likely).ToList();
            if (mine.Count == 0) continue;
            var sizes = LeftoverScan.Sizes(mine);
            _scan.Backup(app.Name, mine);
            var gone = mine.Where(_scan.Delete).ToList();
            if (gone.Count > 0) cleared[app.Id] = LeftoverScan.Summarize(gone, sizes);
        }
        _scan.Forget();
        if (cleared.Count > 0) AppLog.Info($"Replaced apps: cleared {cleared.Values.Sum(summary => summary.Count)} leftovers of apps that are gone.");
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
            return new(false, problem ?? $"{app.Name} is still installed.", 0, Leftovers(app, installed: true).Count);

        // Gone: the deep clean, what it left and what Windows keeps about it.
        _scan.Forget();
        var list = Leftovers(app, installed: false, deep: true).Where(item => !item.Likely).ToList();
        var sizes = LeftoverScan.Sizes(list);
        _scan.Backup(app.Name, list);
        var removed = new List<Leftover>();
        var machine = new List<Leftover>();
        foreach (var item in list)
        {
            if (item.Machine) machine.Add(item);
            else if (_scan.Delete(item)) removed.Add(item);
        }
        var commands = machine.Select(_scan.AdminCommand).OfType<string>().ToList();
        // Out of Windows' image too, for every user, so a feature update or a new account doesn't bring it back.
        commands.AddRange(app.Packages.Select(package =>
            $"powershell.exe -NoProfile -NonInteractive -Command \"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq '{package}' | Remove-AppxProvisionedPackage -Online -AllUsers | Out-Null\""));
        var allowed = commands.Count > 0 && await _removeAsAdmin(commands, ct).ConfigureAwait(false);
        if (allowed) removed.AddRange(machine.Where(item => !_scan.Exists(item)));
        foreach (var set in app.Set) Write(set);
        _scan.Forget();
        var remaining = Leftovers(app, installed: false).Where(item => !item.Likely).ToList();
        var left = remaining.Count;
        var why = _scan.Why(remaining);
        var cleared = LeftoverScan.Summarize(removed, sizes);
        AppLog.Info($"Replaced apps: {app.Name} removed with {removed.Count} leftovers ({cleared.Folders} folders, {cleared.Files} files, " +
            $"{cleared.Registry} registry entries, {cleared.Shortcuts} shortcuts); {left} left{(why is null ? "" : $" ({why})")}" +
            $"{(app.Packages.Length > 0 ? allowed ? "; taken out of Windows' image" : "; still in Windows' image" : "")}.");
        return new(left == 0, left == 0 ? null : why is null ? $"{left} of {app.Name}'s leftovers need Windows' permission." : $"{left} of {app.Name}'s leftovers stayed: {why}.",
            removed.Count, left, cleared);
    }

    // Squirrel (Discord) can't delete its own Update.exe: uninstalled, it leaves it with a .dead file beside it, to go at
    // the next start. A marker beside one is no install (3.15.4 read it as one, and stopped before the clean).
    private bool Installed(ReplacedApp app) =>
        app.Packages.Any(name => _packages.Find(name).Count > 0) || UninstallCommands(app).Count > 0 ||
        app.Markers.Select(_scan.Expand).Any(marker => File.Exists(marker) && !File.Exists(Path.Combine(Path.GetDirectoryName(marker)!, ".dead")));

    /// <summary>An app removed and installed again: what its removal wrote that would stop it working goes (Game Bar's links).</summary>
    private void Reinstalled(ReplacedApp app)
    {
        foreach (var key in app.Undo ?? [])
        {
            try
            {
                using var command = _scan.Open(key + @"\shell\open\command", writable: false);
                if (command?.GetValue(null) is string text && text.EndsWith("systray.exe", StringComparison.OrdinalIgnoreCase))
                    _scan.Delete(new Leftover(key, true, null, false));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
    }

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
                using var key = hive.OpenSubKey(prefix.Length == 0 ? path : prefix + '\\' + path);
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

    /// <summary>A shortcut's target (<see cref="LeftoverScan.ShortcutTarget"/>).</summary>
    internal static string? ShortcutTarget(string link) => LeftoverScan.ShortcutTarget(link);

    /// <summary>Who the app is, for the deep clean: its listed folders, names, executables, ids and entries.</summary>
    private Trail TrailFor(ReplacedApp app) => new(app.Name)
    {
        Homes = [.. app.Paths.Where(path => !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))],
        Names = app.Names ?? [],
        NamesSure = true,
        Exes = [.. (app.Names ?? []).Select(name => name + ".exe")],
        Ids = [.. (app.AppIds ?? []).Concat(app.Packages.Select(package => package + '_'))],
        DisplayNames = app.Uninstall,
        // What is still installed stays, and so does anything holding it (BCU's rule): the NVIDIA tree holds the driver's.
        Keep = _isTest ? [] : _keep ??= [.. InstalledPrograms.List().Where(program => !program.Store && program.Replaces != app.Id && program.Location is not null).Select(program => program.Location!),
            .. Adapters.RecompInstallations.DataFolders()],
    };

    /// <param name="installed">Whether the app is still there: only a gone app's traces are its leftovers.</param>
    /// <param name="deep">Also what is slow to read (COM, services, tasks): after a removal.</param>
    private List<Leftover> Leftovers(ReplacedApp app, bool installed, bool deep = false)
    {
        var list = new List<Leftover>();
        foreach (var path in app.Paths)
        {
            var item = new Leftover(_scan.Expand(path), false, null, MachinePlace(path));
            if (_scan.Exists(item)) list.Add(item);
        }
        foreach (var key in app.Keys)
        {
            var item = new Leftover(key, true, null, key.StartsWith("HKLM", StringComparison.Ordinal));
            if (_scan.Exists(item)) list.Add(item);
        }
        foreach (var value in app.Values)
        {
            var bar = value.IndexOf('|');
            var item = new Leftover(value[..bar], true, value[(bar + 1)..], value.StartsWith("HKLM", StringComparison.Ordinal));
            if (_scan.Exists(item)) list.Add(item);
        }
        if (!installed) list.AddRange(_scan.Find(TrailFor(app), deep));
        return LeftoverScan.Outermost(list);
    }

    private static bool MachinePlace(string path) =>
        new[] { "{ProgramData}", "{ProgramFiles}", "{ProgramFilesX86}", "{CommonDesktop}", "{CommonPrograms}", "HKLM" }.Any(place => path.StartsWith(place, StringComparison.Ordinal));

    private void Write(string set)
    {
        var bar = set.IndexOf('|');
        var equals = set.IndexOf('=', bar);
        try
        {
            var (hive, sub) = _scan.Hive(set[..bar]);
            using var key = hive.CreateSubKey(sub, writable: true);
            var text = set[(equals + 1)..];
            if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
                key.SetValue(set[(bar + 1)..equals], number, RegistryValueKind.DWord);
            else
                key.SetValue(set[(bar + 1)..equals], text.Replace("{System}", Environment.SystemDirectory, StringComparison.Ordinal), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Debug("Replaced apps: a setting wasn't written: " + ex.GetType().Name);
        }
    }

    /// <summary>Closes the app where it runs from its own folders, so its files can go.</summary>
    private void Close(ReplacedApp app)
    {
        var folders = app.Paths.Select(_scan.Expand).Where(Directory.Exists).Select(folder => folder.TrimEnd('\\') + '\\').ToList();
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

    /// <summary>
    /// Runs the steps only an administrator may in one cmd Windows asks permission for once (several when they don't fit
    /// one command line, which cmd caps at 8191 characters).
    /// </summary>
    internal static async Task<bool> RemoveAsAdminAsync(IReadOnlyList<string> commands, CancellationToken ct)
    {
        var batches = new List<List<string>> { new() };
        foreach (var command in commands)
        {
            if (batches[^1].Count > 0 && batches[^1].Sum(step => step.Length + 3) + command.Length > 7800) batches.Add([]);
            batches[^1].Add(command);
        }
        foreach (var batch in batches)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c " + string.Join(" & ", batch))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Environment.SystemDirectory,
                });
                if (process is null) return false;
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }
        }
        return true;
    }
}
