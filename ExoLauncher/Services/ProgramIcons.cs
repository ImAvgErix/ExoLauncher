using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// The file a program's icon comes from (System › Apps), as Windows' own Installed apps finds one: its entry's DisplayIcon,
/// else Windows Installer's product icon, its Start menu shortcut, its own exe in its folder, its uninstaller (Inno Setup's
/// carries the app's icon), and last another program of its publisher that shares its first word (Krisp Audio Drivers
/// takes Krisp's, Xbox Service the Xbox app's). Only files Windows draws as icons: an .exe, .dll, .ico or a Store logo.
/// </summary>
internal static partial class ProgramIcons
{
    private static readonly string[] Drawn = [".exe", ".dll", ".ico", ".png"];
    // Exes in a program's folder that aren't the program: its installer, updater, crash reporter and runtimes.
    private static readonly string[] NotTheApp = ["unins", "uninst", "setup", "update", "crash", "helper", "report", "vc_redist", "vcredist", "dotnet", "install", "elevate", "notification_helper"];
    // Shortcuts beside a program's own in its Start menu folder.
    private static readonly string[] NotItsShortcut = ["uninstall", "readme", "read me", "help", "manual", "website", "documentation", "license", "release notes", "changelog", "prompt", "reference", "tutorial", "online"];
    // A first word too common to say two programs belong together.
    private static readonly string[] Generic = ["microsoft", "windows", "the", "intel", "amd", "nvidia", "realtek", "google", "apple", "adobe", "steam", "update", "driver"];

    /// <summary>The icon's file for <paramref name="program"/>, or null when Windows has none for it.</summary>
    internal static string? Find(InstalledProgram program, IReadOnlyList<InstalledProgram> all, StartMenu menu) =>
        Own(program, menu) ?? Sibling(program, all);

    private static string? Own(InstalledProgram program, StartMenu menu) =>
        program.Icon ?? ProductIcon(program.ProductCode) ?? menu.For(program) ?? MainExe(program) ?? Uninstaller(program);

    /// <summary>Windows Installer's ARPPRODUCTICON, kept under C:\Windows\Installer\{code}.</summary>
    private static string? ProductIcon(string? productCode)
    {
        if (productCode is null || LeftoverScan.Packed(productCode) is not { } packed) return null;
        foreach (var (hive, path) in new[] { (Registry.LocalMachine, @"SOFTWARE\Classes\Installer\Products\"), (Registry.CurrentUser, @"Software\Microsoft\Installer\Products\") })
        {
            try
            {
                using var key = hive.OpenSubKey(path + packed);
                if (Drawable(InstalledPrograms.IconPath(key?.GetValue("ProductIcon") as string)) is { } icon) return icon;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    /// <summary>The program's own exe in its folder: the one named like it, else the largest that isn't its installer's.</summary>
    private static string? MainExe(InstalledProgram program)
    {
        if (program.Location is not { } folder) return null;
        try
        {
            var name = Plain(program.Name);
            var exes = new[] { folder, Path.Combine(folder, "bin") }.Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Take(200))
                .Where(exe => !NotTheApp.Any(word => Path.GetFileName(exe).Contains(word, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return exes.FirstOrDefault(exe => Plain(Path.GetFileNameWithoutExtension(exe)) is { Length: > 1 } stem && (name.Contains(stem) || stem.Contains(name)))
                ?? exes.OrderByDescending(exe => new FileInfo(exe).Length).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The uninstaller itself, when it is the program's own exe (not msiexec or rundll32 in Windows' folder).</summary>
    private static string? Uninstaller(InstalledProgram program)
    {
        if (program.Uninstall is not { } command) return null;
        var file = Adapters.OfficialInstalledLibraries.SplitCommand(command).FileName;
        if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file) || !File.Exists(file)) return null;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return file.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase) ? null : Drawable(file);
    }

    private static string? Sibling(InstalledProgram program, IReadOnlyList<InstalledProgram> all)
    {
        if (FirstWord(program.Name) is not { } word || Generic.Contains(word)) return null;
        return all.Where(other => other.Id != program.Id && other.Icon is not null && FirstWord(other.Name) == word &&
                (program.Publisher is null || other.Publisher is null || SamePublisher(program.Publisher, other.Publisher)))
            .Select(other => other.Icon).FirstOrDefault();
    }

    private static bool SamePublisher(string a, string b) => FirstWord(a) is { } first && first == FirstWord(b);

    private static string? FirstWord(string text) => Plain(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    private static string? Drawable(string? path) =>
        path is not null && Drawn.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && File.Exists(path) ? path : null;

    /// <summary>A name to compare by: lower case, without versions, bitness or what's in brackets ("Python 3.13 (64-bit)" is "python").</summary>
    internal static string Plain(string text)
    {
        var plain = Brackets().Replace(text.ToLowerInvariant(), " ");
        plain = Versions().Replace(plain, " ");
        plain = Bits().Replace(plain, " ");
        return Spaces().Replace(NotWord().Replace(plain, " "), " ").Trim();
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")] private static partial Regex Brackets();
    [GeneratedRegex(@"\bv?\d+(\.\d+)+\b")] private static partial Regex Versions();
    [GeneratedRegex(@"\b(x64|x86|arm64|64-bit|32-bit|64 bit|32 bit)\b")] private static partial Regex Bits();
    [GeneratedRegex(@"[^\p{L}\p{N}.+ ]")] private static partial Regex NotWord();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    /// <summary>
    /// The Start menu's shortcuts (all users' and this user's), read once per icons request: each one's name, its folder's
    /// name, and the icon it shows (its own icon location, else its target).
    /// </summary>
    internal sealed class StartMenu
    {
        private readonly List<(string Name, string Folder, string? Icon, string? Target)> _links = [];

        internal StartMenu()
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (var link in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).Take(3000))
                    {
                        var target = LeftoverScan.ShortcutTarget(link);
                        if ((Drawable(ShortcutIcon(link)) ?? Drawable(target)) is not { } icon) continue;
                        var folder = Path.GetDirectoryName(link)!;
                        _links.Add((Plain(Path.GetFileNameWithoutExtension(link)), folder.Equals(root, StringComparison.OrdinalIgnoreCase) ? "" : Plain(Path.GetFileName(folder)), icon, target));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>The program's shortcut: one into its folder, one named as it is, or its own in a folder named for it.</summary>
        internal string? For(InstalledProgram program)
        {
            var name = Plain(program.Name);
            if (name.Length < 2) return null;
            if (program.Location is { } folder)
            {
                var inside = folder.TrimEnd('\\') + '\\';
                if (_links.FirstOrDefault(link => link.Target?.StartsWith(inside, StringComparison.OrdinalIgnoreCase) == true && !Aside(link.Name)) is { Icon: { } hit }) return hit;
            }
            if (_links.FirstOrDefault(link => link.Name == name) is { Icon: { } named }) return named;
            var own = _links.Where(link => link.Folder.Length > 1 && (link.Folder == name || link.Folder.StartsWith(name + " ", StringComparison.Ordinal)) && !Aside(link.Name)).ToList();
            return own.FirstOrDefault(link => link.Name.Contains(name, StringComparison.Ordinal)).Icon ?? own.FirstOrDefault().Icon;
        }

        private static bool Aside(string name) => NotItsShortcut.Any(word => name.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>
    /// A shortcut's own icon (MS-SHLLINK StringData's ICON_LOCATION, variables expanded), or null when it names none: then
    /// Windows draws its target's.
    /// </summary>
    internal static string? ShortcutIcon(string link)
    {
        try
        {
            var bytes = File.ReadAllBytes(link);
            if (bytes.Length < 0x4C || BitConverter.ToUInt32(bytes, 0) != 0x4C) return null;
            var flags = BitConverter.ToUInt32(bytes, 0x14);
            if ((flags & 0x40) == 0) return null;
            var at = 0x4C;
            if ((flags & 0x1) != 0) at += 2 + BitConverter.ToUInt16(bytes, at);
            if ((flags & 0x2) != 0) at += (int)BitConverter.ToUInt32(bytes, at);
            var unicode = (flags & 0x80) != 0;
            // NAME, RELATIVE_PATH, WORKING_DIR and ARGUMENTS come first, each when its flag is set.
            foreach (var flag in new uint[] { 0x4, 0x8, 0x10, 0x20 })
                if ((flags & flag) != 0) at += 2 + BitConverter.ToUInt16(bytes, at) * (unicode ? 2 : 1);
            var count = BitConverter.ToUInt16(bytes, at);
            var icon = unicode ? Encoding.Unicode.GetString(bytes, at + 2, count * 2) : Encoding.Default.GetString(bytes, at + 2, count);
            return InstalledPrograms.IconPath(icon);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
