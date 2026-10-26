using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Microsoft.Win32;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Library finds native PC ports (Zelda 64: Recompiled, Ship of Harkinian) by their own executables. Ways it could fail: a
/// plain name (Starship.exe) claims an unrelated game without its port's archive beside it; a port is titled by its
/// release folder ("Zelda64Recompiled-v1.2.2-Windows") or missed when that folder holds another of its name; a port's
/// data folder is taken from its executable's name (Dinosaur Planet's is DinoPlanetRecompiled), so a leftover scan
/// could offer its saves and the player's game; a record Windows keeps (the name cache's ".FriendlyAppName" suffix,
/// UserAssist's ROT13, Game Bar's path) isn't read as the path it names; one run from inside its zip (Temp) is listed.
/// </summary>
public sealed class RecompInstallationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-recomp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string File_(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return path;
    }

    [Fact]
    public void PlainNameCountsOnlyWithItsArchiveBesideIt()
    {
        var starship = File_(@"Starship\Starship.exe");
        Assert.Null(RecompInstallations.Match(starship));
        Assert.Empty(RecompInstallations.FindIn(Path.Combine(_root, "Starship")));

        File_(@"Starship\starship.o2r");
        Assert.Equal("Star Fox 64 (Starship)", RecompInstallations.Match(starship)?.Title);
        Assert.Equal(starship, Assert.Single(RecompInstallations.FindIn(Path.Combine(_root, "Starship"))));
    }

    [Fact]
    public void ReleaseFolderNamesNeverTitleThePort()
    {
        var zelda = File_(@"Zelda64Recompiled-v1.2.2-Windows\Zelda64Recompiled.exe");
        var game = RecompInstallations.Entry(zelda);
        Assert.NotNull(game);
        Assert.Equal("Zelda: Majora's Mask (Zelda 64: Recompiled)", game.Title);
        Assert.Equal(Path.Combine(_root, "Zelda64Recompiled-v1.2.2-Windows"), game.Path);
        Assert.Equal(zelda, game.LaunchTarget);
        Assert.Equal(StoreKind.Local, game.Store);
        Assert.Equal("Native PC port of The Legend of Zelda: Majora's Mask. Uses your own copy of the game.", game.LaunchNote);

        Assert.True(RecompInstallations.Released("Zelda64Recompiled-v1.2.2-Windows"));
        Assert.True(RecompInstallations.Released("DK64Recompiled-Windows-Release-1.0.1"));
        Assert.True(RecompInstallations.Released("SoH-Dewey-Bravo-Win64"));
        Assert.True(RecompInstallations.Released("Nautilus-Alfa-Win64"));
        Assert.False(RecompInstallations.Released("Saved Games"));
    }

    [Fact]
    public void AReleaseFolderIsLookedIntoOneFolderDown()
    {
        // Extract All puts a zip's own folder inside one of the same name; a folder of releases holds several.
        var nested = File_(@"Zelda64Recompiled-v1.2.2-Windows\Zelda64Recompiled-v1.2.2-Windows\Zelda64Recompiled.exe");
        Assert.Equal(nested, Assert.Single(RecompInstallations.FindIn(Path.Combine(_root, "Zelda64Recompiled-v1.2.2-Windows"))));

        var soh = File_(@"Ports\SoH-Dewey-Bravo-Win64\soh.exe");
        File_(@"Ports\SoH-Dewey-Bravo-Win64\soh.o2r");
        var dino = File_(@"Ports\DinosaurPlanetRecompiled-v1.0.0-Windows-x64\DinosaurPlanetRecompiled.exe");
        File_(@"Ports\Mine\BanjoRecompiled.exe");
        var found = RecompInstallations.FindIn(Path.Combine(_root, "Ports"));
        Assert.Equal(new[] { dino, soh }, found.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("Dinosaur Planet (Recompiled)", RecompInstallations.Entry(dino)?.Title);
    }

    [Fact]
    public async Task AddedPortKeepsItsTitleAndStaysWhereItIs()
    {
        // Its folder holds its saves and what it made from the player's game: never copied, even with copying on.
        var exe = File_(@"Ports\2Ship-Battler-Bravo-Win64\2ship.exe");
        File_(@"Ports\2Ship-Battler-Bravo-Win64\2ship.o2r");
        var catalog = new InstalledGameCatalog(Path.Combine(_root, "installed-games.json"));
        var adapter = new LocalAdapter(null, catalog, copyPortableIntoLibrary: true, managedLibraryRoot: Path.Combine(_root, "Library"));

        var result = await adapter.InstallAsync(LocalAdapter.CreateAddPortableEntry(), exe, null, TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Message);
        var game = Assert.Single(catalog.GetInstalledGames(StoreKind.Local));
        Assert.Equal("Zelda: Majora's Mask (2 Ship 2 Harkinian)", game.Title);
        Assert.Equal(exe, game.LaunchTarget);
        Assert.Equal(Path.GetDirectoryName(exe), game.Path);
        Assert.False(Directory.Exists(Path.Combine(_root, "Library")));
    }

    [Fact]
    public void DataFoldersAreTheOnesThePortsWrite()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var data = RecompInstallations.DataFolders().ToList();
        Assert.Contains(Path.Combine(local, "DinoPlanetRecompiled"), data, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(local, "DinosaurPlanetRecompiled"), data, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(local, "Zelda64Recompiled"), data, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnleashedRecomp"), data, StringComparer.OrdinalIgnoreCase);
        Assert.All(data, folder => Assert.True(Path.IsPathFullyQualified(folder), folder));
    }

    [Fact]
    public void WindowsRecordsAreReadAsThePathsTheyName()
    {
        Assert.Equal(@"D:\Games\Zelda\Zelda64Recompiled.exe", RecompInstallations.ExePath(@"D:\Games\Zelda\Zelda64Recompiled.exe.FriendlyAppName"));
        Assert.Equal(@"D:\Ports\SoH\soh.exe", RecompInstallations.ExePath("\"D:\\Ports\\SoH\\soh.exe\" --fullscreen"));
        Assert.Null(RecompInstallations.ExePath("Microsoft.Windows.Explorer"));
        Assert.Null(RecompInstallations.ExePath(@"soh.exe"));

        // Run from inside its zip: Explorer unpacks it to Temp, which is no game's folder.
        var temp = File_(@"Temp1_Zelda64Recompiled-v1.2.2-Windows.zip\Zelda64Recompiled.exe");
        Assert.NotNull(RecompInstallations.Match(temp));
        Assert.Empty(RecompInstallations.Read([temp + ".FriendlyAppName"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ProgramsRunReadsTheNameCacheUserAssistAndGameBar()
    {
        var test = @"Software\ExoLauncherTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var mui = Registry.CurrentUser.CreateSubKey(test + @"\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"))
                mui.SetValue(@"D:\Games\Zelda\Zelda64Recompiled.exe.FriendlyAppName", "Zelda64Recompiled");
            using (var assist = Registry.CurrentUser.CreateSubKey(test + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count"))
                assist.SetValue(Rot13(@"D:\Ports\SoH\soh.exe"), new byte[] { 1 });
            using (var bar = Registry.CurrentUser.CreateSubKey(test + @"\System\GameConfigStore\Children\0b1c7f2e"))
                bar.SetValue("MatchedExeFullPath", @"D:\Ports\Starship\Starship.exe");

            var scan = new LeftoverScan(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), test, test + "-HKLM");
            var paths = scan.ProgramsRun().Select(RecompInstallations.ExePath).OfType<string>().ToList();
            Assert.Contains(@"D:\Games\Zelda\Zelda64Recompiled.exe", paths);
            Assert.Contains(@"D:\Ports\SoH\soh.exe", paths);
            Assert.Contains(@"D:\Ports\Starship\Starship.exe", paths);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(test, throwOnMissingSubKey: false); }
    }

    private static string Rot13(string text) => new([.. text.Select(c => c switch
    {
        >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
        >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
        _ => c,
    })]);
}
