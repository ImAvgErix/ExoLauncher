using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Services;

internal enum LeftoverKind { Folder, File, Shortcut, Registry, Service, Task }

/// <summary>
/// One thing an app left: a file, folder or shortcut, a registry key (<see cref="Value"/> null) or value, a service (its
/// key) or a scheduled task (its path). Machine: only an administrator may remove it. Likely: matched by a name alone, as
/// BCU's "questionable": shown apart and only deleted when asked for on its own.
/// </summary>
internal sealed record Leftover(string Path, bool IsKey, string? Value, bool Machine, bool Likely = false, LeftoverKind? As = null)
{
    /// <summary>Read once, while it is still there.</summary>
    internal LeftoverKind Kind { get; } = As ?? (IsKey ? LeftoverKind.Registry
        : Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ? LeftoverKind.Shortcut
        : Directory.Exists(Path) ? LeftoverKind.Folder : LeftoverKind.File);
}

/// <summary>What an app left, by kind: folders and files (with their size), registry entries (services and tasks too), shortcuts.</summary>
internal sealed record LeftoverSummary(int Folders, int Files, int Registry, int Shortcuts, long Bytes)
{
    internal int Count => Folders + Files + Registry + Shortcuts;
}

/// <summary>
/// Who an app is, for finding what it left: its own folders (<see cref="Homes"/>: anything pointing into one is its), its
/// own folder and key names (<see cref="Names"/>, sure only when a list names them, else likely), its executables'
/// names (crash reports), the ids Windows knows it by (app ids; a Store package family as "Family_"), its Apps &amp;
/// features entries and names, its publisher, its MSI product code, and folders that hold something still installed
/// (<see cref="Keep"/>: never deleted, nor anything holding them).
/// </summary>
internal sealed record Trail(string Name)
{
    internal IReadOnlyList<string> Homes { get; init; } = [];
    internal IReadOnlyList<string> Names { get; init; } = [];
    internal bool NamesSure { get; init; }
    internal IReadOnlyList<string> Exes { get; init; } = [];
    internal IReadOnlyList<string> Ids { get; init; } = [];
    internal IReadOnlyList<string> Entries { get; init; } = [];
    internal IReadOnlyList<string> DisplayNames { get; init; } = [];
    internal string? Publisher { get; init; }
    internal string? ProductCode { get; init; }
    internal IReadOnlyList<string> Keep { get; init; } = [];
}

/// <summary>
/// The deep clean, as Revo Uninstaller's advanced scan and BCU's junk search find leftovers, for any app once it is gone.
/// Each lead is tied to the app (<see cref="Trail"/>), never a word searched for across the disk:
/// <list type="bullet">
/// <item>its folders, and its names where programs keep theirs (Local, LocalLow, Roaming, ProgramData, Program Files,
/// Temp) and their keys (Software, and under its publisher), its crash dumps and crash reports, its VirtualStore copy;</item>
/// <item>anything pointing into its folders: shortcuts (desktop, Start, the taskbar, Startup), Windows' records of a
/// program by its path (the name cache, compatibility, startup, recent apps, UserAssist, Game Bar's list), link and file
/// handlers, COM classes and type libraries, App Paths, services, scheduled tasks, event log sources, SharedDLLs,
/// Installer folders and firewall rules;</item>
/// <item>its app ids (notifications, recent apps) and a Store package's own records;</item>
/// <item>its MSI registration (by product code) and Apps &amp; features entries whose uninstaller is gone.</item>
/// </list>
/// Never Windows' folders or keys, a place itself, a folder holding something still installed, a generic or company name,
/// or a name of four letters or fewer (BCU's rules). <c>deep</c> adds what is slow to read (COM, services, tasks): after an
/// uninstall, not for a passing look.
/// </summary>
internal sealed partial class LeftoverScan
{
    private readonly IReadOnlyDictionary<string, string> _places;
    private readonly string _hkcuBase, _hklmBase;
    private readonly bool _isTest;
    private Sources? _sources;

    /// <param name="places">Folder per place name. <paramref name="hkcuBase"/> and <paramref name="hklmBase"/> are prefixes
    /// under HKCU for tests (empty: the real hives); tests delete for good instead of to the Recycle Bin.</param>
    internal LeftoverScan(IReadOnlyDictionary<string, string> places, string hkcuBase = "", string hklmBase = "")
    {
        _places = places;
        _hkcuBase = hkcuBase;
        _hklmBase = hklmBase;
        _isTest = hkcuBase.Length > 0;
        Recycler = _isTest ? ForGood : Recycle;
    }

    /// <summary>Moves a file or folder to the Recycle Bin: null when it went, else why it stayed. Tests delete for good, or set their own.</summary>
    internal Func<string, string?> Recycler { get; init; }

    /// <summary>Each one Exo refused to delete, with why, said plainly ("too big for the Recycle Bin").</summary>
    internal Dictionary<Leftover, string> Stayed { get; } = [];

    // Why one stayed, said after "stayed: ".
    internal const string Barred = "Exo doesn't delete there";
    internal const string Linked = "it's a link, or inside one";
    internal const string Moved = "it now leads somewhere else";
    internal const string Unquotable = "its name can't be passed to Windows safely";
    internal const string TooBig = "too big for the Recycle Bin";
    internal const string NoBin = "its drive has no Recycle Bin";
    internal const string BinOff = "the Recycle Bin is off for its drive";
    internal const string NotRecycled = "in use, or it didn't go to the Recycle Bin";
    internal const string InUse = "in use, or Windows' permission was declined";

    internal static Dictionary<string, string> ThisPc() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ["LocalLow"] = Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!, "LocalLow"),
        ["AppData"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ["Desktop"] = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ["CommonDesktop"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        ["CommonPrograms"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ["ProgramFilesX86"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ["Temp"] = Path.GetTempPath().TrimEnd('\\'),
    };

    internal const string Programs = @"{AppData}\Microsoft\Windows\Start Menu\Programs";
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly string[] NameRoots = [@"{LocalAppData}", @"{LocalAppData}\Programs", @"{LocalLow}", @"{AppData}", @"{ProgramData}", @"{ProgramFiles}", @"{ProgramFilesX86}", @"{Temp}"];
    private static readonly string[] MachineRoots = ["{ProgramData}", "{ProgramFiles}", "{ProgramFilesX86}", "{CommonDesktop}", "{CommonPrograms}", "HKLM"];

    // Names no app owns alone (BCU's generic and company names, and Windows' own folders): a company's key or folder holds
    // all its programs.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "install", "installer", "settings", "config", "configuration", "users", "data", "common", "common files", "shared", "tools", "update", "updates",
        "updater", "app", "apps", "program", "programs", "games", "game", "launcher", "client", "cache", "temp", "logs", "bin", "x64", "x86",
        "microsoft", "windows", "windowsapps", "packages", "package cache", "uninstall information", "directx", "nvidia corporation", "amd",
        "intel", "google", "mozilla", "adobe", "steam", "epic games", "riot games", "ubisoft", "electronic arts", "valve", "software", "classes",
        "wow6432node", "policies", "clients", "system", "drivers", "runtime", "runtimes", "plugins", "extensions", "services", "helper",
        "microsoft corporation", "nvidia", "intel corporation", "advanced micro devices, inc.", "google llc", "mozilla corporation", "adobe inc.",
        "adobe systems incorporated", "apple", "apple inc.", "oracle", "oracle corporation", "valve corporation", "epic games, inc.", "riot games, inc.",
        "ubisoft entertainment", "blizzard entertainment", "activision", "bethesda softworks", "rockstar games", "unity technologies", "jetbrains",
        "autodesk", "logitech", "razer", "razer inc.", "corsair", "asus", "asustek computer inc.", "realtek", "realtek semiconductor corp.", "samsung",
        "lenovo", "dell", "dell inc.", "hp inc.", "hewlett-packard", "western digital", "seagate", "steelseries", "elgato", "dolby", "khronos group",
        "python software foundation", "meta", "oculus",
    };

    /// <summary>Everything that app left, each once (none inside another that goes), only what is there now.</summary>
    internal List<Leftover> Find(Trail trail, bool deep)
    {
        var homes = Homes(trail.Homes);
        var keep = trail.Keep.Select(path => path.TrimEnd('\\') + '\\').ToList();
        // A name alone (likely) is never a generic or company name, nor the publisher's own: its key holds all its programs.
        var names = trail.Names.Where(name => OneName(name) && (trail.NamesSure || name.Length > 4 && !Generic.Contains(name) &&
            !string.Equals(name, trail.Publisher?.Trim(), StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var likely = !trail.NamesSure;
        var list = new List<Leftover>();
        foreach (var home in homes) list.Add(new(home.TrimEnd('\\'), false, null, MachinePlace(home)));
        foreach (var name in names)
        {
            foreach (var root in NameRoots) list.Add(new(Expand(root + '\\' + name), false, null, MachinePlace(root), likely));
            list.Add(new(@"HKCU\Software\" + name, true, null, false, likely));
            list.Add(new(@"HKLM\SOFTWARE\" + name, true, null, true, likely));
            list.Add(new(@"HKLM\SOFTWARE\WOW6432Node\" + name, true, null, true, likely));
            if (trail.Publisher is { Length: > 4 } publisher && OneName(publisher) && !Generic.Contains(publisher) && !publisher.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new($@"HKCU\Software\{publisher}\{name}", true, null, false, likely));
                list.Add(new($@"HKLM\SOFTWARE\{publisher}\{name}", true, null, true, likely));
                foreach (var root in new[] { "{LocalAppData}", "{AppData}", "{ProgramData}" }) list.Add(new(Expand($@"{root}\{publisher}\{name}"), false, null, MachinePlace(root), likely));
            }
        }
        // Crash dumps and Windows Error Reporting's reports, by the executable's name.
        foreach (var exe in trail.Exes.Where(exe => exe.Length > 4 && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dump in Matching(Expand(@"{LocalAppData}\CrashDumps"), exe + ".*.dmp")) list.Add(new(dump, false, null, false));
            foreach (var (root, machine) in new[] { (@"{LocalAppData}\Microsoft\Windows\WER", false), (@"{ProgramData}\Microsoft\Windows\WER", true) })
            foreach (var store in new[] { "ReportArchive", "ReportQueue" })
            foreach (var report in Folders(Expand($@"{root}\{store}"), $"AppCrash_{exe}_*"))
                list.Add(new(report, false, null, machine));
        }
        // A program in Program Files that wrote where it may not was given its own copy there (VirtualStore).
        foreach (var home in homes)
            if (home.Length > 3 && home[1] == ':')
                list.Add(new(Expand(@"{LocalAppData}\VirtualStore") + home[2..].TrimEnd('\\'), false, null, false));
        // A Store app's own records, by its package family.
        foreach (var family in trail.Ids.Where(id => id.EndsWith('_')).Select(id => id.TrimEnd('_')))
            foreach (var (path, isKey) in StoreRecords(family)) list.Add(new(path, isKey, null, false));
        list.AddRange(DeadEntries(trail));
        foreach (var entry in trail.Entries) list.Add(new(entry, true, null, entry.StartsWith("HKLM", StringComparison.Ordinal)));
        if (trail.ProductCode is { } product && Packed(product) is { } packed)
        {
            list.Add(new($@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}", true, null, true));
            list.Add(new($@"HKLM\SOFTWARE\Classes\Installer\Features\{packed}", true, null, true));
            list.Add(new($@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\{packed}", true, null, true));
            list.Add(new($@"HKCU\Software\Microsoft\Installer\Products\{packed}", true, null, false));
            list.Add(new($@"HKCU\Software\Microsoft\Installer\Features\{packed}", true, null, false));
        }
        var ids = trail.Ids;
        if (homes.Count > 0 || ids.Count > 0)
        {
            var sources = _sources ??= ReadSources(deep);
            if (deep && !sources.Deep) sources = _sources = ReadSources(deep);
            foreach (var (key, name, text, machine) in sources.Values)
                if (PointsInto(name, homes) || PointsInto(text, homes) ||
                    ids.Any(id => name.Contains(id, StringComparison.OrdinalIgnoreCase) || text.Contains(id, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new(key, true, name, machine));
            foreach (var (key, text, machine, kind) in sources.Keys)
                if (PointsInto(text, homes) || kind == KeySource.AppId && ids.Any(id => text.StartsWith(id, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new(key, true, null, machine, false, kind == KeySource.Service ? LeftoverKind.Service : null));
            // A shortcut by where it points; an internet shortcut (a store's game: steam://rungameid/730) also by its link.
            foreach (var (path, target, machine) in sources.Shortcuts)
                if (PointsInto(target, homes) || ids.Any(id => target.StartsWith(id + '|', StringComparison.OrdinalIgnoreCase)))
                    list.Add(new(path, false, null, machine));
            foreach (var (path, exe) in sources.Tasks)
                if (PointsInto(exe, homes)) list.Add(new(path, false, null, true, false, LeftoverKind.Task));
            // What names the app's COM classes and file types (Revo's moderate scan): its shell extensions' entries and
            // their approval, a file type it was the handler of, its Open With entries.
            var classes = list.Where(item => item.IsKey && item.Value is null && item.Path.Contains(@"\CLSID\{", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Path[(item.Path.LastIndexOf('\\') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var progIds = list.Where(item => item.IsKey && item.Value is null && ClassRoots.Any(root => item.Path.StartsWith(root.Path + '\\', StringComparison.OrdinalIgnoreCase)) &&
                    item.Path.Count(c => c == '\\') == ClassRoots.First(root => item.Path.StartsWith(root.Path + '\\', StringComparison.OrdinalIgnoreCase)).Path.Count(c => c == '\\') + 1)
                .Select(item => item.Path[(item.Path.LastIndexOf('\\') + 1)..]).Where(name => !name.StartsWith('{') && !name.StartsWith('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, name, target, machine) in sources.Refs)
                if (classes.Contains(target) || progIds.Contains(target))
                    list.Add(name is null ? new(key, true, null, machine) : new(key, true, name, machine));
        }
        // An MSI's upgrade code still naming it.
        if (trail.ProductCode is { } code && Packed(code) is { } upgraded)
        {
            const string Upgrades = @"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes";
            ReadSubKeys(Upgrades, (upgrade, key) =>
            {
                if (key.GetValueNames().Contains(upgraded, StringComparer.OrdinalIgnoreCase))
                    list.Add(key.GetValueNames().Length == 1 ? new($@"{Upgrades}\{upgrade}", true, null, true) : new($@"{Upgrades}\{upgrade}", true, upgraded, true));
            });
        }
        // A startup entry that goes takes Task Manager's on/off record of it along.
        foreach (var run in list.Where(item => item.Value is { } name && item.Path.EndsWith(@"\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase)).ToList())
            foreach (var approved in new[] { "Run", "Run32" })
                list.Add(new(run.Path.Replace(@"\CurrentVersion\Run", @"\CurrentVersion\Explorer\StartupApproved\" + approved, StringComparison.OrdinalIgnoreCase), true, run.Value, run.Machine));
        return Outermost(list.Where(item => Allowed(item, keep) && Exists(item)).ToList());
    }

    /// <summary>
    /// The app's own folders, each with a trailing backslash, as Windows reads them (".." and doubled slashes resolved): deep
    /// enough, never a place itself or Windows'.
    /// </summary>
    internal List<string> Homes(IEnumerable<string> paths) => paths
        .Select(Expand)
        .Where(Path.IsPathFullyQualified)
        .Select(path => Full(path)?.TrimEnd('\\') ?? "")
        .Where(path => path.Count(c => c == '\\') >= 2 && !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && !UnderWindows(path) &&
            !_places.Values.Any(place => string.Equals(place.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase)))
        .Select(path => path + '\\')
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    /// <summary>One folder or key name, never a path: "Microsoft\Windows" or ".." would name another's (a program's name is anyone's to set).</summary>
    private static bool OneName(string name) =>
        name.Length > 0 && name.Trim() == name && !name.EndsWith('.') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// What may go at all: never Windows' folders, a place, a folder holding something still installed or Exo's own, a
    /// shallow key or one of the keys every program shares.
    /// </summary>
    private bool Allowed(Leftover item, List<string> keep)
    {
        if (item.Kind == LeftoverKind.Task) return item.Path.StartsWith('\\');
        if (item.IsKey)
        {
            var parts = item.Path.Split('\\');
            if (parts.Length < 3 || item.Value is null && parts.Length == 3 && Generic.Contains(parts[2])) return false;
            if (item.Value is null && Protected.Any(key => item.Path.Equals(key, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }
        var path = item.Path.TrimEnd('\\');
        // Written as Windows reads it: a "..", a trailing dot or a doubled slash would lead somewhere the words don't say.
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Full(path), path, StringComparison.OrdinalIgnoreCase) ||
            path.Count(c => c == '\\') < 2 || UnderWindows(path)) return false;
        if (_places.Values.Any(place => string.Equals(place.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase))) return false;
        var mine = path + '\\';
        if (keep.Any(other => other.StartsWith(mine, StringComparison.OrdinalIgnoreCase) || mine.StartsWith(other, StringComparison.OrdinalIgnoreCase))) return false;
        if (!_isTest && (PathHelper.AppDataDir.TrimEnd('\\') + '\\').StartsWith(mine, StringComparison.OrdinalIgnoreCase)) return false;
        // A recomp's data folder holds its saves and the user's own copy of the game: never anything's leftover.
        if (!_isTest && Adapters.RecompInstallations.DataFolders().Select(folder => folder.TrimEnd('\\') + '\\')
                .Any(data => data.StartsWith(mine, StringComparison.OrdinalIgnoreCase) || mine.StartsWith(data, StringComparison.OrdinalIgnoreCase))) return false;
        try { if (Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.System)) return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        return true;
    }

    // Keys that hold every program's: never deleted whole, whatever points at them.
    private static readonly string[] Protected =
    [
        @"HKCU\Software", @"HKLM\SOFTWARE", @"HKCU\Software\Classes", @"HKLM\SOFTWARE\Classes", @"HKLM\SOFTWARE\WOW6432Node", @"HKCU\Software\Microsoft",
        @"HKLM\SOFTWARE\Microsoft", @"HKCU\Software\Classes\CLSID", @"HKLM\SOFTWARE\Classes\CLSID", @"HKLM\SYSTEM\CurrentControlSet\Services",
        @"HKCU\Software\Classes\*", @"HKLM\SOFTWARE\Classes\*", @"HKCU\Software\Classes\Directory", @"HKLM\SOFTWARE\Classes\Directory",
        @"HKCU\Software\Classes\Folder", @"HKLM\SOFTWARE\Classes\Folder", @"HKCU\Software\Classes\exefile", @"HKLM\SOFTWARE\Classes\exefile",
    ];

    private static bool UnderWindows(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 && (path.TrimEnd('\\') + '\\').StartsWith(windows.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether only an administrator may change it: a machine place, by its name or its path, or HKLM.</summary>
    private bool MachinePlace(string path) =>
        MachineRoots.Any(place => path.StartsWith(place, StringComparison.Ordinal)) ||
        MachineRoots.Where(place => place.StartsWith('{')).Select(Expand).Any(root => !root.StartsWith('{') &&
            (path.TrimEnd('\\') + '\\').StartsWith(root.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase));

    /// <summary>What Windows keeps is read again on the next find (after anything was deleted).</summary>
    internal void Forget() => _sources = null;

    /// <summary>Whether a path, a command line or a value's name points into one of the folders (variables expanded, either slash).</summary>
    internal static bool PointsInto(string? text, List<string> homes)
    {
        if (string.IsNullOrEmpty(text) || homes.Count == 0) return false;
        var plain = Environment.ExpandEnvironmentVariables(text).Replace('/', '\\');
        return homes.Any(home => plain.Contains(home, StringComparison.OrdinalIgnoreCase) ||
            plain.EndsWith(home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            plain.Contains(home.TrimEnd('\\') + '"', StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The app's Apps &amp; features entries (by display name) whose uninstaller is gone.</summary>
    private IEnumerable<Leftover> DeadEntries(Trail trail)
    {
        if (trail.DisplayNames.Count == 0) yield break;
        foreach (var (hive, view) in new[] { ("HKCU", Uninstall), ("HKLM", Uninstall), ("HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") })
        {
            var entries = new List<string>();
            try
            {
                using var key = Open(hive + '\\' + view, writable: false);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var entry = key.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display || !trail.DisplayNames.Any(match => ReplacedApps.NameMatches(display, match))) continue;
                    var command = (entry.GetValue("QuietUninstallString") ?? entry.GetValue("UninstallString")) as string;
                    if (string.IsNullOrWhiteSpace(command) || !File.Exists(Adapters.OfficialInstalledLibraries.SplitCommand(command).FileName))
                        entries.Add(name);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { continue; }
            foreach (var name in entries) yield return new(hive + '\\' + view + '\\' + name, true, null, hive == "HKLM");
        }
    }

    /// <summary>A Store package's records Windows keeps per user, and its data folder.</summary>
    private IEnumerable<(string Path, bool IsKey)> StoreRecords(string family)
    {
        const string AppModel = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel";
        yield return ($@"{AppModel}\SystemAppData\{family}", true);
        yield return ($@"{AppModel}\Repository\Families\{family}", true);
        yield return (Expand(@"{LocalAppData}\Packages\" + family), false);
    }

    /// <summary>An MSI product code as Windows Installer keys it: each group's hex reversed (the last two by byte pairs).</summary>
    internal static string? Packed(string productCode)
    {
        if (!Guid.TryParse(productCode, out var guid)) return null;
        var text = guid.ToString("N").ToUpperInvariant();
        var builder = new StringBuilder(32);
        builder.Append(Reverse(text[..8])).Append(Reverse(text[8..12])).Append(Reverse(text[12..16]));
        for (var i = 16; i < 32; i += 2) builder.Append(text[i + 1]).Append(text[i]);
        return builder.ToString();

        static string Reverse(string part) => new([.. part.Reverse()]);
    }

    /// <summary>Each leftover once, and none inside another that goes (a shortcut in a folder, a value or key in a key).</summary>
    internal static List<Leftover> Outermost(List<Leftover> list)
    {
        var unique = list.DistinctBy(item => (item.IsKey, item.Kind == LeftoverKind.Task, item.Path.ToUpperInvariant(), item.Value?.ToUpperInvariant()))
            // A sure find beats the same thing found by name.
            .GroupBy(item => (item.IsKey, item.Path.ToUpperInvariant(), item.Value?.ToUpperInvariant())).Select(group => group.OrderBy(item => item.Likely).First()).ToList();
        var folders = unique.Where(item => item.Kind == LeftoverKind.Folder).Select(item => item.Path.TrimEnd('\\') + '\\').ToList();
        var keys = unique.Where(item => item.IsKey && item.Value is null).Select(item => item.Path + '\\').ToList();
        return unique.Where(item => !Inside(item)).ToList();

        bool Inside(Leftover item) => item.Kind == LeftoverKind.Task ? false : item.IsKey
            ? keys.Any(key => (item.Path + '\\').StartsWith(key, StringComparison.OrdinalIgnoreCase) && (item.Value is not null || (item.Path + '\\').Length > key.Length))
            : folders.Any(folder => item.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

    internal static Dictionary<Leftover, long> Sizes(IEnumerable<Leftover> items) =>
        items.ToDictionary(item => item, item => item.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut ? Size(item.Path) : 0);

    internal static LeftoverSummary Summarize(IReadOnlyCollection<Leftover> items, IReadOnlyDictionary<Leftover, long> sizes) => new(
        items.Count(item => item.Kind == LeftoverKind.Folder), items.Count(item => item.Kind == LeftoverKind.File),
        items.Count(item => item.Kind is LeftoverKind.Registry or LeftoverKind.Service or LeftoverKind.Task), items.Count(item => item.Kind == LeftoverKind.Shortcut),
        items.Sum(item => sizes.GetValueOrDefault(item)));

    internal static long Size(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    // ---- what is there, and taking it away ----

    internal bool Exists(Leftover item)
    {
        if (item.Kind == LeftoverKind.Task) return _isTest ? _testTasks.Contains(item.Path) : TaskExists(item.Path);
        if (!item.IsKey) return Directory.Exists(item.Path) || File.Exists(item.Path);
        try
        {
            using var key = Open(item.Path, writable: false);
            return key is not null && (item.Value is null || key.GetValueNames().Contains(item.Value, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Why one may not go now, or null when it may: checked again right before it goes, as this user, so what goes (an
    /// administrator's rd included) is what was found. Refused: a place Exo never deletes; a link (a junction, symbolic link
    /// or mount point) at it or on the way down from its place, which whoever may write there could point into Windows; a
    /// path Windows opens somewhere other than where its words say (every link followed), or one that leads where Exo never
    /// deletes. Another kind of reparse point isn't a link (OneDrive keeps each synced file and folder as one) and still
    /// has to open where it says.
    /// </summary>
    internal string? Recheck(Leftover item)
    {
        if (!Allowed(item, [])) return Barred;
        if (item.IsKey || item.Kind == LeftoverKind.Task) return null;
        var path = item.Path.TrimEnd('\\');
        if (!Directory.Exists(path) && !File.Exists(path)) return null;
        var root = Root(path);
        for (var step = path; step.Length > root.Length + 1; step = Path.GetDirectoryName(step) ?? root)
            if (IsLink(step)) return Linked;
        if (FinalPath(path) is not { } final || FinalPath(root + '\\') is not { } top ||
            !final.Equals(top.TrimEnd('\\') + path[root.Length..], StringComparison.OrdinalIgnoreCase)) return Moved;
        return Allowed(item with { Path = final }, []) ? null : Barred;
    }

    // The deepest place it lies in, else its drive's root; without a trailing backslash. What is above it is Windows' or
    // the user's own, never the app's.
    private string Root(string path) =>
        _places.Values.Select(place => place.TrimEnd('\\')).Where(place => place.Length > 0 && path.StartsWith(place + '\\', StringComparison.OrdinalIgnoreCase))
            .MaxBy(place => place.Length) ?? (Path.GetPathRoot(path) ?? "").TrimEnd('\\');

    // A junction, symbolic link or mount point (.NET reads a target only from those); unreadable counts as one.
    private static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Where Windows opens a path, every link on the way followed (GetFinalPathNameByHandle); null when it can't be opened.</summary>
    private static string? FinalPath(string path)
    {
        // Attributes only, shared every way; backup semantics so a folder opens too.
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new char[512];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length >= buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length) return null;
        var final = new string(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..] : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    private bool Stay(Leftover item, string why)
    {
        Stayed[item] = why;
        AppLog.Debug($"Leftovers: one stayed: {why}.");
        return false;
    }

    /// <summary>Why these, still there, stayed (<see cref="InUse"/> for those Exo didn't refuse); null when Exo refused none of them.</summary>
    internal string? Why(IReadOnlyCollection<Leftover> left) =>
        left.Any(Stayed.ContainsKey) ? string.Join("; ", left.Select(item => Stayed.GetValueOrDefault(item) ?? InUse).Distinct()) : null;

    /// <summary>
    /// Deletes one this user may, once <see cref="Recheck"/> passes: a file or folder only to the Recycle Bin, as the
    /// confirmation says (one the bin doesn't take stays, unless it's gone anyway), a key or value after <see cref="Backup"/>
    /// wrote it down. One Exo refused is in <see cref="Stayed"/>.
    /// </summary>
    internal bool Delete(Leftover item)
    {
        try
        {
            if (item.Kind == LeftoverKind.Task) return false;
            if (Recheck(item) is { } refused) return Stay(item, refused);
            if (!item.IsKey)
            {
                if (!Directory.Exists(item.Path) && !File.Exists(item.Path)) return true;
                var why = Recycler(item.Path);
                return !Directory.Exists(item.Path) && !File.Exists(item.Path) || Stay(item, why ?? NotRecycled);
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
            AppLog.Debug($"Leftovers: one stayed ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>
    /// One cmd step for a leftover only an administrator may remove, or null for one that stays (in <see cref="Stayed"/>):
    /// one <see cref="Recheck"/> refuses now, so what the administrator deletes is what was found, or one cmd can't name
    /// safely. Every name must stay inside double quotes, where &amp;, | and &lt; &gt; are only text; what quotes don't
    /// hold is refused: a quote, % (expanded even inside them), ! (delayed expansion), ^ and every control character (a
    /// line break ends the command). A folder goes with rd /s, which removes a link inside it (a junction, a symbolic link)
    /// without going into it; a file with del only while it is still a file (del given a folder empties it). What it names
    /// came from the app's own trail, never from the page.
    /// </summary>
    internal string? AdminCommand(Leftover item)
    {
        var why = $"{item.Path}{item.Value}".Any(c => char.IsControl(c) || c is '"' or '%' or '!' or '^') ? Unquotable : Recheck(item);
        if (why is not null)
        {
            Stay(item, why);
            return null;
        }
        if (item.Kind == LeftoverKind.Task) return $"schtasks /delete /tn \"{item.Path}\" /f";
        if (item.Kind == LeftoverKind.Service) return $"sc stop \"{item.Path.Split('\\')[^1]}\" & sc delete \"{item.Path.Split('\\')[^1]}\"";
        if (!item.IsKey)
            return Directory.Exists(item.Path) ? $"rd /s /q \"{item.Path}\""
                : File.Exists(item.Path) ? $"(if not exist \"{item.Path}\\*\" del /f /q \"{item.Path}\")" : null;
        var key = _isTest && item.Path.StartsWith("HKLM", StringComparison.Ordinal) ? @"HKCU\" + Join(_hklmBase, item.Path[5..]) : item.Path;
        return item.Value is null ? $"reg delete \"{key}\" /f" : item.Value.Length == 0 ? $"reg delete \"{key}\" /ve /f" : $"reg delete \"{key}\" /v \"{item.Value}\" /f";
    }

    /// <summary>
    /// Writes the registry leftovers down before they go (reg export of each key, a value's with its key) in Exo's
    /// cleanup folder: a .reg the user can double-click to bring them back. The newest 30 are kept. Null in tests or with none.
    /// </summary>
    internal string? Backup(string app, IEnumerable<Leftover> items)
    {
        var keys = items.Where(item => item.IsKey && item.Kind != LeftoverKind.Task).Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (_isTest || keys.Count == 0) return null;
        try
        {
            var folder = Path.Combine(PathHelper.AppDataDir, "cleanup");
            Directory.CreateDirectory(folder);
            var safe = new string(app.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : '_').ToArray()).Trim();
            var file = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss} {safe}.reg");
            var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
            foreach (var key in keys)
            {
                var part = Path.Combine(Path.GetTempPath(), $"exo-{Guid.NewGuid():N}.reg");
                using (var export = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("reg.exe", $"export \"{key}\" \"{part}\" /y")
                       { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
                    export?.WaitForExit(15_000);
                if (!File.Exists(part)) continue;
                var lines = File.ReadAllText(part).Split("\r\n", 2);
                text.Append(lines.Length > 1 ? lines[1] : "");
                File.Delete(part);
            }
            File.WriteAllText(file, text.ToString(), Encoding.Unicode);
            foreach (var old in new DirectoryInfo(folder).GetFiles("*.reg").OrderByDescending(f => f.CreationTimeUtc).Skip(30)) old.Delete();
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Debug("Leftovers: backup not written: " + ex.GetType().Name);
            return null;
        }
    }

    internal string Expand(string path)
    {
        var end = path.IndexOf('}');
        return path.StartsWith('{') && end > 0 && _places.TryGetValue(path[1..end], out var root) ? root + path[(end + 1)..] : path;
    }

    internal RegistryKey? Open(string path, bool writable)
    {
        var (hive, sub) = Hive(path);
        return hive.OpenSubKey(sub, writable);
    }

    internal (RegistryKey Hive, string Sub) Hive(string path) => path[..4] switch
    {
        "HKCU" => (Registry.CurrentUser, Join(_hkcuBase, path[5..])),
        "HKLM" when _isTest => (Registry.CurrentUser, Join(_hklmBase, path[5..])),
        "HKLM" => (Registry.LocalMachine, path[5..]),
        _ => throw new InvalidOperationException("Unexpected hive."),
    };

    private static string Join(string prefix, string path) => prefix.Length == 0 ? path : prefix + '\\' + path;

    private static IEnumerable<string> Matching(string folder, string pattern, bool deep = false)
    {
        if (!Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = deep, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> Folders(string folder, string pattern)
    {
        if (!Directory.Exists(folder)) return [];
        try { return Directory.EnumerateDirectories(folder, pattern, new EnumerationOptions { IgnoreInaccessible = true }).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // ---- the Recycle Bin ----

    /// <summary>
    /// To the Recycle Bin, quietly: null when it went, else why it stayed. Told not to ask, Windows deletes for good what
    /// its bin can't take, so that is looked at first (<see cref="RecycleBinRefuses(string)"/>) and stays.
    /// </summary>
    private static string? Recycle(string path)
    {
        if (RecycleBinRefuses(path) is { } why) return why;
        var done = false;
        var thread = new Thread(() =>
        {
            var operation = new ShFileOp { wFunc = 3, pFrom = path + "\0\0", fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400 | 0x0200 };
            done = SHFileOperation(ref operation) == 0 && !operation.fAnyOperationsAborted;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return done ? null : NotRecycled;
    }

    // Tests: for good (a junction goes on its own; Directory.Delete never follows one).
    private static string? ForGood(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
        return null;
    }

    /// <summary>
    /// Why Windows would delete it for good rather than keep it: its drive's Recycle Bin as Windows keeps it (Recycle Bin ›
    /// Properties, per drive). A drive Windows never used the bin on has no size saved yet: 1% of the drive, a cautious guess.
    /// </summary>
    private static string? RecycleBinRefuses(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            using var bin = Volume() is { } id ? Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{id}") : null;
            var capacity = bin?.GetValue("MaxCapacity") is int megabytes ? megabytes * (1L << 20) : drive.TotalSize / 100;
            return RecycleBinRefuses(Size(path), drive.DriveType, bin?.GetValue("NukeOnDelete") is 1, capacity);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { return NoBin; }

        // The volume's id, as the bin's settings are keyed: {GUID} of \\?\Volume{GUID}\.
        string? Volume()
        {
            var mount = new char[1024];
            var name = new char[64];
            if (!GetVolumePathNameW(path, mount, (uint)mount.Length) || !GetVolumeNameForVolumeMountPointW(new string(mount).TrimEnd('\0'), name, (uint)name.Length)) return null;
            var text = new string(name).TrimEnd('\0');
            var open = text.IndexOf('{');
            var close = text.IndexOf('}');
            return open >= 0 && close > open ? text[open..(close + 1)] : null;
        }
    }

    /// <summary>Only a fixed drive keeps a Recycle Bin, it can be set to remove files at once, and nothing bigger than it goes in. Null when it takes it.</summary>
    internal static string? RecycleBinRefuses(long bytes, DriveType drive, bool removesAtOnce, long capacity) =>
        drive != DriveType.Fixed ? NoBin : removesAtOnce ? BinOff : bytes > capacity ? TooBig : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, [Out] char[] volumePath, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, uint length);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref ShFileOp operation);

    // ---- tests ----

    private readonly HashSet<string> _testTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, string Exe)> _testTaskList = [];

    /// <summary>Tests: a scheduled task as if Windows' Task Scheduler had it.</summary>
    internal void AddTestTask(string path, string exe)
    {
        _testTasks.Add(path);
        _testTaskList.Add((path, exe));
    }
}
