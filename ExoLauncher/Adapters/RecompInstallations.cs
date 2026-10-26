using System.Diagnostics;
using Microsoft.Win32;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// Native PC recompilations and ports of console games (Zelda 64: Recompiled, Ship of Harkinian, Unleashed Recompiled).
/// Each is a portable zip with no installer, uninstall entry or shortcut, so it is found by its own executable's name: in
/// Windows' records of the programs this user ran, in what is running, and a folder or two down in Downloads, on the
/// Desktop and in Library's game folders. Exo only finds and starts them: the game each is built from is the player's own
/// copy, which Exo never looks inside, copies or fetches.
/// </summary>
internal static class RecompInstallations
{
    /// <param name="Title">Library's name for it: the game, then the project.</param>
    /// <param name="Game">The original game's exact name (its cover art).</param>
    /// <param name="Exes">Its executables' file names.</param>
    /// <param name="Companion">A file its release ships beside the executable: a name as plain as Starship.exe counts only with it.</param>
    /// <param name="Data">Where it keeps saves and settings (an N64Recomp game also the player's copy of the game), when not beside the executable.</param>
    /// <param name="Release">How its release zip's name, and so the folder it is unpacked to, begins.</param>
    internal sealed record Recomp(string Title, string Game, string[] Exes, string? Companion, string[] Data, string Release);

    // Names from each project's source and release assets.
    internal static readonly Recomp[] Known =
    [
        // github.com/Zelda64Recomp/Zelda64Recomp: Zelda64Recompiled-v1.2.2-Windows.zip
        new("Zelda: Majora's Mask (Zelda 64: Recompiled)", "The Legend of Zelda: Majora's Mask", ["Zelda64Recompiled.exe"], null,
            [@"%LOCALAPPDATA%\Zelda64Recompiled"], "Zelda64Recompiled"),
        // github.com/BanjoRecomp/BanjoRecomp: BanjoRecompiled-v1.0.0-Windows.zip
        new("Banjo-Kazooie (Banjo: Recompiled)", "Banjo-Kazooie", ["BanjoRecompiled.exe"], null, [@"%LOCALAPPDATA%\BanjoRecompiled"], "BanjoRecompiled"),
        // DK64Recompiled-Windows-Release-1.0.0.zip
        new("Donkey Kong 64 (DK64: Recompiled)", "Donkey Kong 64", ["DK64Recompiled.exe"], null, [@"%LOCALAPPDATA%\DK64Recompiled"], "DK64Recompiled"),
        // github.com/DinosaurPlanetRecomp/dino-recomp: DinosaurPlanetRecompiled-v1.0.0-Windows-x64.zip; its data folder is named otherwise.
        new("Dinosaur Planet (Recompiled)", "Dinosaur Planet", ["DinosaurPlanetRecompiled.exe"], null, [@"%LOCALAPPDATA%\DinoPlanetRecompiled"], "DinosaurPlanetRecompiled"),
        // github.com/hedge-dev/UnleashedRecomp: UnleashedRecomp-Windows.zip; its own installer puts the game beside it.
        new("Sonic Unleashed (Unleashed Recompiled)", "Sonic Unleashed", ["UnleashedRecomp.exe"], null, [@"%APPDATA%\UnleashedRecomp"], "UnleashedRecomp"),
        // HarbourMasters' ports keep saves and the archive made from the game beside the executable.
        // github.com/HarbourMasters/Shipwright: SoH-Dewey-Bravo-Win64.zip
        new("Zelda: Ocarina of Time (Ship of Harkinian)", "The Legend of Zelda: Ocarina of Time", ["soh.exe"], "soh.o2r", [], "SoH"),
        // github.com/HarbourMasters/2ship2harkinian: 2Ship-Battler-Bravo-Win64.zip
        new("Zelda: Majora's Mask (2 Ship 2 Harkinian)", "The Legend of Zelda: Majora's Mask", ["2ship.exe"], "2ship.o2r", [], "2Ship"),
        // github.com/HarbourMasters/Starship: Starship-Barnard-Alfa-Windows.zip
        new("Star Fox 64 (Starship)", "Star Fox 64", ["Starship.exe"], "starship.o2r", [], "Starship"),
        // github.com/HarbourMasters/SpaghettiKart: spaghetti-windows.zip
        new("Mario Kart 64 (SpaghettiKart)", "Mario Kart 64", ["Spaghettify.exe"], "spaghetti.o2r", [], "spaghetti"),
        // github.com/HarbourMasters/Ghostship: Nautilus-Alfa-Win64.zip
        new("Super Mario 64 (Ghostship)", "Super Mario 64", ["Ghostship.exe"], "ghostship.o2r", [], "Nautilus"),
        // github.com/coop-deluxe/sm64coopdx: sm64coopdx_Windows.zip
        new("Super Mario 64 (sm64coopdx)", "Super Mario 64", ["sm64coopdx.exe"], null, [@"%APPDATA%\sm64coopdx", @"%APPDATA%\sm64ex-coop"], "sm64coopdx"),
        // github.com/sm64pc/sm64ex: built by the player, named for the region.
        new("Super Mario 64 (sm64ex)", "Super Mario 64", ["sm64.us.f3dex2e.exe", "sm64.eu.f3dex2e.exe", "sm64.jp.f3dex2e.exe"], null, [@"%APPDATA%\sm64ex"], "sm64ex"),
        // github.com/fgsfdsfgs/perfect_dark: pd-x86_64-windows.zip; saves beside it.
        new("Perfect Dark (PC port)", "Perfect Dark",
            ["pd.x86_64.exe", "pd.i686.exe", "pd.pal.x86_64.exe", "pd.pal.i686.exe", "pd.jpn.x86_64.exe", "pd.jpn.i686.exe"], null, [], "pd-"),
    ];

    private static readonly Dictionary<string, Recomp> ByExe = Known.SelectMany(recomp => recomp.Exes.Select(exe => (exe, recomp)))
        .ToDictionary(pair => pair.exe, pair => pair.recomp, StringComparer.OrdinalIgnoreCase);

    /// <summary>The recomp this executable is, by its name; one with a companion only when that is beside it.</summary>
    internal static Recomp? Match(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || !ByExe.TryGetValue(Path.GetFileName(exe), out var recomp)) return null;
        return recomp.Companion is null || Path.GetDirectoryName(exe) is { Length: > 0 } folder && File.Exists(Path.Combine(folder, recomp.Companion)) ? recomp : null;
    }

    /// <summary>The recomp a Library game is, by what it launches.</summary>
    internal static Recomp? For(GameEntry game) => game.Store == StoreKind.Local ? Match(game.LaunchTarget) : null;

    /// <summary>Where the recomps keep saves and the player's game: never a leftover of anything.</summary>
    internal static IEnumerable<string> DataFolders() => Known.SelectMany(recomp => recomp.Data).Select(Environment.ExpandEnvironmentVariables);

    internal static GameEntry? Entry(string exe)
    {
        if (Match(exe) is not { } recomp) return null;
        var folder = LocalAdapter.GameFolderFor(exe);
        return new GameEntry
        {
            // By its folder, as the folder scan named it before it knew the recomp: its playtime stays.
            Id = LocalAdapter.StableLocalId(folder, Path.GetFileName(folder)), Title = recomp.Title, Store = StoreKind.Local,
            Installed = true, Owned = true, CanInstall = false, Path = folder, LaunchTarget = exe, Status = "Ready",
            SizeBytes = InstalledSizeCache.Get(folder), Deps = [],
            LaunchNote = $"Native PC port of {recomp.Game}. Uses your own copy of the game.",
        };
    }

    /// <summary>A folder named like a release: Zelda64Recompiled-v1.2.2-Windows, SoH-Dewey-Bravo-Win64.</summary>
    internal static bool Released(string folder) => Known.Any(recomp => folder.StartsWith(recomp.Release, StringComparison.OrdinalIgnoreCase));

    private static readonly EnumerationOptions Shallow = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };

    /// <summary>
    /// The recomps' executables in a folder, and one folder down when either is named like a release (a zip unpacked into
    /// a folder of its own name, or a folder of releases).
    /// </summary>
    internal static List<string> FindIn(string folder)
    {
        var found = Executables(folder);
        var release = Released(Path.GetFileName(folder));
        foreach (var sub in Folders(folder))
            if (release || Released(Path.GetFileName(sub))) found.AddRange(Executables(sub));
        return found;

        static List<string> Executables(string dir)
        {
            try { return [.. Directory.EnumerateFiles(dir, "*.exe", Shallow).Where(exe => Match(exe) is not null)]; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
        }
    }

    private static List<string> Folders(string dir)
    {
        try { return [.. Directory.EnumerateDirectories(dir, "*", Shallow)]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }

    internal static IReadOnlyList<GameEntry> Scan(CancellationToken cancellation = default)
    {
        var exes = new List<string>(Ran());
        foreach (var shelf in Shelves().Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var folder in Folders(shelf))
            {
                cancellation.ThrowIfCancellationRequested();
                exes.AddRange(FindIn(folder));
            }
        return Read(exes, cancellation);
    }

    /// <summary>The recomps among paths and records that name one: each once, still there, and in a folder of its own.</summary>
    internal static IReadOnlyList<GameEntry> Read(IEnumerable<string> records, CancellationToken cancellation = default)
    {
        var games = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            cancellation.ThrowIfCancellationRequested();
            if (ExePath(record) is { } exe && seen.Add(exe) && ByExe.ContainsKey(Path.GetFileName(exe)) && File.Exists(exe) && Placed(exe) && Entry(exe) is { } game)
                games.Add(game);
        }
        return games;
    }

    /// <summary>The .exe path a record names: a value's name with Windows' suffix (.FriendlyAppName), a command line, a path.</summary>
    internal static string? ExePath(string? record)
    {
        if (string.IsNullOrWhiteSpace(record)) return null;
        var value = Environment.ExpandEnvironmentVariables(record.Trim()).TrimStart('"');
        var end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        value = value[..(end + 4)];
        try { return Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>
    /// In a folder that can be the game's: not loose in Downloads, on the Desktop or in Documents, not Temp (a recomp
    /// opened inside its zip runs from there), a drive or Windows.
    /// </summary>
    internal static bool Placed(string exe)
    {
        var folder = Path.TrimEndingDirectorySeparator(LocalAdapter.GameFolderFor(exe));
        return LocalAdapter.IsSafePortableRegistrationRoot(folder) &&
            !(folder + '\\').StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
            !Shelves().Append(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
                .Any(shelf => shelf.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(shelf), folder, StringComparison.OrdinalIgnoreCase));
    }

    // Where a downloaded zip is unpacked: Downloads (where Explorer says it is, when moved) and the Desktop.
    private static IEnumerable<string> Shelves()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string? moved = null;
        try
        {
            moved = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                "{374DE290-123F-4565-9164-39C4925E467B}", null) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { }
        if (!string.IsNullOrWhiteSpace(moved)) yield return Environment.ExpandEnvironmentVariables(moved);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    // Windows' records of what this user ran, then what is running now.
    private static IEnumerable<string> Ran()
    {
        var found = new LeftoverScan(LeftoverScan.ThisPc()).ProgramsRun();
        var names = ByExe.Keys.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (names.Contains(process.ProcessName) && ProcessHelper.TryGetExecutablePath(process) is { } path) found.Add(path);
                }
                catch (InvalidOperationException) { /* it closed */ }
            }
        }
        return found;
    }
}
