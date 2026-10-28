using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// When each program was last opened, from what Windows keeps: Explorer's UserAssist (every program this user started from
/// Start, the taskbar, a shortcut or Explorer, its path ROT13'd, with the last run's time; empty when Windows' "track app
/// launches" privacy switch is off) and Prefetch (one file per exe, written as it starts; readable when Exo runs as
/// administrator). Updaters, helpers and services don't count: they run without anyone opening the app. A record Windows
/// stopped keeping (tracking switched off, SysMain stopped) is frozen at its last day and would call every app unused, so it
/// isn't read. No record means "not known", never "never". Read only.
/// </summary>
internal static partial class AppsUsage
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
    // Its two lists: programs, and the shortcuts that started them.
    private static readonly string[] Lists = ["{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}", "{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}"];

    /// <summary>When things last ran, UTC: by full exe path or Store app family (UserAssist), and by exe name (Prefetch).</summary>
    internal sealed record Runs(IReadOnlyDictionary<string, DateTime> Paths, IReadOnlyDictionary<string, DateTime> Exes);

    // Exes in a program's folder that run without anyone opening it.
    private static readonly string[] NotOpened = ["unins", "setup", "update", "crash", "helper", "report", "install", "elevate", "service", "svc", "agent", "daemon", "notif", "vc_redist", "dotnet", "broker", "host"];

    internal static Runs Read() => new(UserAssist(), Prefetch());

    private static Dictionary<string, DateTime> UserAssist()
    {
        var runs = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // Settings › Privacy › "Let Windows improve Start and search results by tracking app launches", off.
        using (var advanced = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
            if (advanced?.GetValue("Start_TrackProgs") is int track && track == 0) return runs;
        foreach (var list in Lists)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{list}\Count");
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is not byte[] data || LastRun(data) is not { } at) continue;
                    if (Target(Rot13(name)) is not { } target) continue;
                    if (!runs.TryGetValue(target, out var known) || at > known) runs[target] = at;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return runs;
    }

    /// <summary>Prefetch's files ("STEAM.EXE-1A2B3C4D.pf"): each exe name to when its newest one was written; empty without access.</summary>
    private static Dictionary<string, DateTime> Prefetch()
    {
        var runs = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch")).EnumerateFiles("*.pf"))
                if (PrefetchExe(file.Name) is { } exe && (!runs.TryGetValue(exe, out var known) || file.LastWriteTimeUtc > known)) runs[exe] = file.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        // Windows writes Prefetch all day; nothing new in two days means it stopped (SysMain off), and what's there is stale.
        return runs.Count > 0 && DateTime.UtcNow - runs.Values.Max() < TimeSpan.FromDays(2) ? runs : [];
    }

    /// <summary>"STEAM.EXE-1A2B3C4D.pf" is steam.exe; null for a name that isn't a Prefetch file's.</summary>
    internal static string? PrefetchExe(string fileName) =>
        fileName.LastIndexOf('-') is var dash and > 4 && fileName.EndsWith(".pf", StringComparison.OrdinalIgnoreCase) && fileName[..dash].EndsWith(".EXE", StringComparison.OrdinalIgnoreCase)
            ? fileName[..dash] : null;

    /// <summary>
    /// When <paramref name="program"/> was last opened: its Store app, or the newest run of an exe in its folder (and the
    /// folders just under it) that isn't its updater or a helper; null when Windows has no record.
    /// </summary>
    internal static DateTime? LastOpened(InstalledProgram program, Runs runs, Func<string, IEnumerable<string>>? exesIn = null)
    {
        if (program.Store && program.Id.StartsWith("store:", StringComparison.Ordinal))
            return runs.Paths.TryGetValue(program.Id["store:".Length..], out var at) ? at : null;
        if (program.Location is not { Length: > 3 } folder) return null;
        var inside = folder.TrimEnd('\\') + '\\';
        DateTime? last = null;
        void Saw(DateTime at) { if (last is null || at > last) last = at; }
        foreach (var (path, at) in runs.Paths)
            if (path.StartsWith(inside, StringComparison.OrdinalIgnoreCase) && Opened(Path.GetFileName(path))) Saw(at);
        if (runs.Exes.Count > 0)
            foreach (var exe in (exesIn ?? ExesIn)(folder))
                if (Opened(exe) && runs.Exes.TryGetValue(exe, out var at)) Saw(at);
        return last;
    }

    private static bool Opened(string exe) => !NotOpened.Any(word => exe.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The exe names in a folder and the folders just under it, a few hundred at most.</summary>
    private static IEnumerable<string> ExesIn(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles("*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true })
                    .Take(300).Select(file => file.Name).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The last run's time in an entry (Windows 7 and later: 72 bytes, a FILETIME at 60); null when it never ran.</summary>
    internal static DateTime? LastRun(byte[] data)
    {
        if (data.Length < 68) return null;
        var time = BitConverter.ToInt64(data, 60);
        return time > 0 && time < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(time) : null;
    }

    internal static string Rot13(string text) => string.Create(text.Length, text, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            span[i] = c switch
            {
                >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                _ => c,
            };
        }
    });

    /// <summary>
    /// An entry's program: a path whose first part may be a known folder's id ("{6D809377-…}\Steam\steam.exe"), a shortcut
    /// (its target), or a Store app ("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App": its family).
    /// </summary>
    private static string? Target(string entry)
    {
        if (entry.Contains('!') && !entry.Contains('\\')) return entry[..entry.IndexOf('!')];
        var path = entry;
        if (path.StartsWith('{') && path.IndexOf('}') is var end and > 0 && Guid.TryParse(path[1..end], out var id))
        {
            if (KnownFolder(id) is not { } folder) return null;
            path = folder + path[(end + 1)..];
        }
        if (!Path.IsPathFullyQualified(path)) return null;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return LeftoverScan.ShortcutTarget(path) is { } linked && Path.IsPathFullyQualified(linked) ? linked : null;
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static readonly Dictionary<Guid, string?> Folders = [];

    private static string? KnownFolder(Guid id)
    {
        lock (Folders)
        {
            if (Folders.TryGetValue(id, out var known)) return known;
            var found = SHGetKnownFolderPath(id, 0, 0, out var pointer) == 0;
            // Freed whether it was found or not, as the call asks.
            var path = found ? Marshal.PtrToStringUni(pointer) : null;
            if (pointer != 0) Marshal.FreeCoTaskMem(pointer);
            return Folders[id] = path;
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}
