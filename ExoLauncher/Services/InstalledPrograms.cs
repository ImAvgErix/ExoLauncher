using System.Collections.Concurrent;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace ExoLauncher.Services;

/// <summary>
/// A program on this PC, as Windows' Apps &amp; features knows it. <paramref name="Id"/> names where it is listed (its
/// uninstall key, or its Store package family) and is how the page asks for it again. <paramref name="Uninstall"/> is its
/// own uninstaller (null when that is gone: only its leftovers can go), <paramref name="Care"/> why Exo is careful with it
/// (<see cref="InstalledPrograms.Cares"/>), <paramref name="Replaces"/> the replaced app it is (ReplacedApps), if any.
/// </summary>
internal sealed record InstalledProgram(
    string Id, string Name, string? Publisher, string? Version, string? InstalledOn, long? Bytes, string? Location, string? Icon,
    bool Store, bool Machine, string? Uninstall, string? ProductCode, string? PackageFullName, bool Removable, string? Care, string? Replaces,
    string? Key);

/// <summary>
/// Every program Apps &amp; features lists, read as it reads them: the uninstall keys of HKLM (64- and 32-bit) and HKCU,
/// without system components, updates and patches (a parent key or a release type), and this user's Store apps that
/// aren't Windows' own or frameworks. The same program listed in two places is one. Its size is the entry's own estimate,
/// else its folder measured once (in the background; <see cref="Measure"/>).
/// </summary>
internal static class InstalledPrograms
{
    internal const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Why a program is handled with care: Exo itself and anti-cheat are never removed here (anti-cheat goes with its game);
    /// a graphics driver goes from System › Driver; runtimes, store clients and what Exo uses are removed with a warning.
    /// </summary>
    internal static class Cares
    {
        internal const string Exo = "exo", AntiCheat = "anticheat", Driver = "driver", Hardware = "hardware", Runtime = "runtime", Store = "store", ExoUses = "uses";
    }

    private static readonly string[] AntiCheatNames = ["Vanguard", "EasyAntiCheat", "Easy Anti-Cheat", "BattlEye", "FACEIT", "ESEA", "nProtect", "XIGNCODE", "PunkBuster", "Javelin", "Ricochet"];
    private static readonly string[] DriverNames = ["NVIDIA Graphics Driver", "AMD Software", "AMD Radeon Software", "Intel(R) Graphics", "Intel® Graphics", "Intel Graphics Driver"];
    private static readonly string[] RuntimeNames = ["Microsoft Visual C++", "Microsoft .NET", "Microsoft Windows Desktop Runtime", "Microsoft ASP.NET", "Microsoft Edge WebView2", "DirectX", "Microsoft GameInput", "Microsoft Update Health Tools", "OpenAL", "PhysX"];
    private static readonly string[] StoreClientNames = ["Steam", "Epic Games Launcher", "EA app", "Ubisoft Connect", "Battle.net", "GOG GALAXY", "Riot Client", "Amazon Games", "Rockstar Games Launcher", "itch", "Xbox"];
    private static readonly string[] ExoUsesPackages = ["Microsoft.GamingApp", "Microsoft.GamingServices", "Microsoft.XboxIdentityProvider", "SpotifyAB.SpotifyMusic"];
    // Store packages other apps need: the Store and its installer, winget, the Windows App Runtime, codecs.
    private static readonly string[] RuntimePackages = ["Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.Winget.Source",
        "MicrosoftCorporationII.WinAppRuntime", "Microsoft.WindowsAppRuntime", "Microsoft.WebMediaExtensions", "Microsoft.ApplicationCompatibilityEnhancements", "Microsoft.Ink.Handwriting"];
    // Windows' own placeholders that Apps & features doesn't list.
    private static readonly string[] HiddenPackages = ["Microsoft.MicrosoftEdge.Stable", "Microsoft.MicrosoftEdge"];

    private static readonly ConcurrentDictionary<string, long> Measured = new(StringComparer.OrdinalIgnoreCase);
    // Game libraries a store keeps inside its own folder: measured as the games they are, not as the store's app.
    private static readonly string[] GameLibraries = ["steamapps"];

    internal static List<InstalledProgram> List()
    {
        var found = new List<InstalledProgram>();
        foreach (var (hive, view, tag) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"), (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32"), (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU") })
            found.AddRange(Desktop(hive, view, tag));
        found.AddRange(StoreApps());
        // One program in two views (a 32-bit installer that wrote both, or HKLM and HKCU): the first listed stays.
        return found.GroupBy(program => (program.Name.ToUpperInvariant(), program.Version, program.Store))
            .Select(group => group.First())
            .OrderBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static InstalledProgram? Find(string id) => List().FirstOrDefault(program => program.Id == id);

    private static IEnumerable<InstalledProgram> Desktop(RegistryHive hive, RegistryView view, string tag)
    {
        var list = new List<InstalledProgram>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null) return list;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry is null || Read(entry, tag, name) is not { } program) continue;
                    list.Add(program);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            AppLog.Debug($"Programs: {tag} unreadable ({ex.GetType().Name}).");
        }
        return list;
    }

    private static InstalledProgram? Read(RegistryKey entry, string tag, string keyName)
    {
        if (Text(entry, "DisplayName") is not { } name) return null;
        // What Apps & features leaves out: Windows' components, and updates or patches to another product.
        if (entry.GetValue("SystemComponent") is int system && system == 1) return null;
        if (Text(entry, "ParentKeyName") is not null || Text(entry, "ParentDisplayName") is not null) return null;
        if (Text(entry, "ReleaseType") is { } release && release.Contains("Update", StringComparison.OrdinalIgnoreCase) | release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) return null;
        var msi = entry.GetValue("WindowsInstaller") is int installer && installer == 1 && Guid.TryParseExact(keyName, "B", out _);
        // A command keeps its quotes ("C:\Program Files (x86)\Steam\steam.exe" steam://uninstall/730): trimmed of them, its
        // uninstaller couldn't be found and an installed game read as one whose uninstaller is gone.
        var command = entry.GetValue("UninstallString") is string raw && raw.Trim() is { Length: > 0 } whole ? whole : msi ? $"MsiExec.exe /X{keyName}" : null;
        var location = Folder(Text(entry, "InstallLocation")) ?? FolderOf(Text(entry, "DisplayIcon")) ?? FolderOf(command);
        var bytes = entry.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : location is not null && Measured.TryGetValue(location, out var size) ? size : (long?)null;
        var publisher = Text(entry, "Publisher");
        var care = CareFor(name, publisher, null);
        var noRemove = entry.GetValue("NoRemove") is int no && no == 1;
        return new InstalledProgram(
            $"{tag}:{keyName}", name, publisher, Text(entry, "DisplayVersion"), Date(Text(entry, "InstallDate")), bytes, location,
            IconPath(Text(entry, "DisplayIcon")), Store: false, Machine: tag.StartsWith("HKLM", StringComparison.Ordinal),
            command, msi ? keyName : null, null, Removable(name, command, noRemove, care), care,
            ReplacedFor(name, null), $@"{(tag == "HKCU" ? "HKCU" : "HKLM")}\{(tag == "HKLM32" ? @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" : UninstallPath)}\{keyName}");
    }

    // Edge says NoRemove but goes from here with WebView2 kept (EdgeRemoval); WebView2 is what Exo runs on, so it stays.
    private static bool Removable(string name, string? command, bool noRemove, string? care) =>
        (!noRemove || EdgeRemoval.IsEdge(name, command)) && !EdgeRemoval.IsWebView(name) && care is not (Cares.Exo or Cares.AntiCheat or Cares.Driver);

    private static IEnumerable<InstalledProgram> StoreApps()
    {
        var list = new List<InstalledProgram>();
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUser(""))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage || package.SignatureKind == PackageSignatureKind.System) continue;
                    var id = package.Id;
                    if (HiddenPackages.Any(hidden => id.Name.Equals(hidden, StringComparison.OrdinalIgnoreCase))) continue;
                    var name = package.DisplayName;
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) continue;
                    string? location = null;
                    try { location = package.InstalledLocation?.Path; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
                    string? logo = null;
                    try { logo = package.Logo?.LocalPath; } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
                    var care = CareFor(name, package.PublisherDisplayName, id.Name);
                    list.Add(new InstalledProgram(
                        "store:" + id.FamilyName, name, package.PublisherDisplayName, $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}",
                        package.InstalledDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                        location is not null && Measured.TryGetValue(location, out var size) ? size : null, location, logo, Store: true, Machine: false,
                        null, null, id.FullName, care is not (Cares.Exo or Cares.AntiCheat or Cares.Driver), care, ReplacedFor(name, id.Name), null));
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException) { }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            AppLog.Debug("Programs: Store apps unreadable: " + ex.GetType().Name);
        }
        return list;
    }

    /// <summary>
    /// Sizes the folders of programs whose entry gives none, a few at a time, for the next listing (each folder once a
    /// run); whether any was measured. At most <paramref name="budget"/> long.
    /// </summary>
    internal static bool Measure(IEnumerable<InstalledProgram> programs, TimeSpan budget)
    {
        using var stop = new CancellationTokenSource(budget);
        var any = false;
        try
        {
            Parallel.ForEach(programs.Where(program => program.Bytes is null && program.Location is { } folder && !Measured.ContainsKey(folder) && Directory.Exists(folder)),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stop.Token },
                program =>
                {
                    long total = 0;
                    var flat = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    var folders = new Stack<DirectoryInfo>([new DirectoryInfo(program.Location!)]);
                    while (folders.TryPop(out var folder))
                    {
                        if (stop.IsCancellationRequested) return;
                        foreach (var file in folder.EnumerateFiles("*", flat)) total += file.Length;
                        // A store's game library inside its folder (Steam's steamapps) is the games, not the app.
                        foreach (var sub in folder.EnumerateDirectories("*", flat))
                            if (!GameLibraries.Contains(sub.Name, StringComparer.OrdinalIgnoreCase)) folders.Push(sub);
                    }
                    Measured[program.Location!] = total;
                    any = true;
                });
        }
        catch (OperationCanceledException) { }
        return any;
    }

    /// <summary>Why Exo is careful with a program, by its name, publisher and package (null: it isn't).</summary>
    internal static string? CareFor(string name, string? publisher, string? package)
    {
        if (name.Equals("Exo Launcher", StringComparison.OrdinalIgnoreCase)) return Cares.Exo;
        if (AntiCheatNames.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))) return Cares.AntiCheat;
        if (DriverNames.Any(word => name.StartsWith(word, StringComparison.OrdinalIgnoreCase))) return Cares.Driver;
        if (package is not null && ExoUsesPackages.Any(word => package.StartsWith(word, StringComparison.OrdinalIgnoreCase))) return Cares.ExoUses;
        if (package is not null && (RuntimePackages.Any(word => package.StartsWith(word, StringComparison.OrdinalIgnoreCase)) ||
            package.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) && (package.EndsWith("VideoExtension", StringComparison.OrdinalIgnoreCase) ||
                package.EndsWith("VideoExtensions", StringComparison.OrdinalIgnoreCase) || package.EndsWith("ImageExtension", StringComparison.OrdinalIgnoreCase))))
            return Cares.Runtime;
        if (name.Equals("Spotify", StringComparison.OrdinalIgnoreCase)) return Cares.ExoUses;
        if (RuntimeNames.Any(word => name.StartsWith(word, StringComparison.OrdinalIgnoreCase))) return Cares.Runtime;
        // Another device's driver (network, audio, chipset): removable, with a warning.
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"\bDrivers?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return Cares.Hardware;
        if (StoreClientNames.Any(word => name.Equals(word, StringComparison.OrdinalIgnoreCase) || name.StartsWith(word + " ", StringComparison.OrdinalIgnoreCase) && !name.Contains("Remover", StringComparison.OrdinalIgnoreCase)))
            return Cares.Store;
        return null;
    }

    /// <summary>The replaced app (ReplacedApps.Known) a program is: by its Apps &amp; features name or its Store package.</summary>
    internal static string? ReplacedFor(string name, string? package) => ReplacedApps.Known.FirstOrDefault(app =>
        app.Uninstall.Any(match => ReplacedApps.NameMatches(name, match)) ||
        package is not null && app.Packages.Any(known => known.Equals(package, StringComparison.OrdinalIgnoreCase)))?.Id;

    private static string? Text(RegistryKey key, string name) =>
        key.GetValue(name) is string text && text.Trim().Trim('"').Trim() is { Length: > 0 } clean ? clean : null;

    private static string? Date(string? text) =>
        text is { Length: 8 } && DateOnly.TryParseExact(text, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>An icon's file: DisplayIcon without its ",index" and quotes, variables expanded; null when there is none.</summary>
    internal static string? IconPath(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var path = Environment.ExpandEnvironmentVariables(icon.Trim());
        var comma = path.LastIndexOf(',');
        if (comma > 2 && int.TryParse(path[(comma + 1)..].Trim(), out _)) path = path[..comma];
        path = path.Trim().Trim('"');
        return Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
    }

    private static string? Folder(string? text)
    {
        if (text is null) return null;
        var path = Environment.ExpandEnvironmentVariables(text).Trim().Trim('"').TrimEnd('\\');
        return Path.IsPathFullyQualified(path) && Directory.Exists(path) && Own(path) ? path : null;
    }

    /// <summary>
    /// The folder an icon or uninstaller sits in, when that is the program's own: not Windows', the MSI cache or a shared
    /// folder (an entry without InstallLocation; BCU and Revo read it the same way).
    /// </summary>
    private static string? FolderOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var file = IconPath(command) ?? Adapters.OfficialInstalledLibraries.SplitCommand(command).FileName;
        if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file)) return null;
        var folder = Path.GetDirectoryName(file);
        return folder is not null && Own(folder) && Directory.Exists(folder) ? folder : null;
    }

    // Folders no program owns, whatever its entry says: Windows', the shared Common Files, installer caches, Microsoft's
    // own data folders, Temp.
    private static readonly string[] Shared =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Package Cache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell"),
        Path.GetTempPath(),
    ];

    /// <summary>Whether a folder can be one program's own: deep enough, and not Windows' or a shared one, nor inside one.</summary>
    internal static bool Own(string folder) => Deep(folder) &&
        !Shared.Any(shared => shared.Length > 0 && (folder.TrimEnd('\\') + '\\').StartsWith(shared.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase));

    /// <summary>Three levels down at least (C:\Program Files\App): a drive or a place itself is never a program's folder.</summary>
    internal static bool Deep(string path)
    {
        var full = path.TrimEnd('\\');
        if (full.Count(c => c == '\\') < 2) return false;
        var places = new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop,
        }.Select(Environment.GetFolderPath).Where(place => place.Length > 0);
        return !places.Any(place => string.Equals(place.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase));
    }
}
