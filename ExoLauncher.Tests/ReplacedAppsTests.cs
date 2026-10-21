using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Tests;

/// <summary>
/// Deep removal of the apps Exo replaces, in a sandbox: folders under a temp root and keys under a test key in HKCU.
/// Ways it could fail: a removal leaves the app's folders, keys or Run value behind; something that isn't the app's (a
/// sibling folder, the NVIDIA driver's key) goes with it; an app that is still installed reads as leftovers and they are
/// cleared under it; an Apps &amp; features entry whose uninstaller is gone keeps the app "installed" forever; a leftover
/// in Program Files is deleted without Windows' permission (it can't be) instead of in the one elevated step; a declined
/// permission prompt reads as success; Game Bar's removal leaves Windows asking for the removed app.
/// </summary>
public sealed class ReplacedAppsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-replaced-" + Guid.NewGuid().ToString("N"));
    private readonly string _hkcu;
    private readonly string _hklm;
    private readonly FakePackages _packages = new();
    private readonly List<string> _uninstalled = [];
    private readonly List<string> _admin = [];
    private bool _adminAllowed = true;

    public ReplacedAppsTests()
    {
        var test = @"Software\ExoLauncherTests\" + Guid.NewGuid().ToString("N");
        _hkcu = test + @"\HKCU";
        _hklm = test + @"\HKLM";
        foreach (var place in Places.Values) Directory.CreateDirectory(place);
    }

    private Dictionary<string, string> Places => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAppData"] = Path.Combine(_root, "Local"),
        ["AppData"] = Path.Combine(_root, "Roaming"),
        ["Desktop"] = Path.Combine(_root, "Desktop"),
        ["CommonDesktop"] = Path.Combine(_root, "PublicDesktop"),
        ["CommonPrograms"] = Path.Combine(_root, "CommonPrograms"),
        ["ProgramFiles"] = Path.Combine(_root, "Program Files"),
        ["ProgramData"] = Path.Combine(_root, "ProgramData"),
    };

    private ReplacedApps Apps() => new(Places, _packages,
        (command, _) =>
        {
            _uninstalled.Add(command);
            // A real uninstaller: removes its exe and its entry.
            var exe = Adapters.OfficialInstalledLibraries.SplitCommand(command).FileName;
            File.Delete(exe);
            Registry.CurrentUser.DeleteSubKeyTree(_hkcu + @"\Software\Microsoft\Windows\CurrentVersion\Uninstall\Discord", false);
            return Task.FromResult<string?>(null);
        },
        (commands, _) =>
        {
            _admin.AddRange(commands);
            if (!_adminAllowed) return Task.FromResult(false);
            foreach (var command in commands)
            {
                var path = command.Split('"')[1];
                if (command.StartsWith("rd ", StringComparison.Ordinal)) Directory.Delete(path, true);
                else if (command.StartsWith("del ", StringComparison.Ordinal)) File.Delete(path);
                else Registry.CurrentUser.DeleteSubKeyTree(path[5..], false);
            }
            return Task.FromResult(true);
        },
        _hkcu, _hklm);

    private string Place(string place, string rest) => Path.Combine(Places[place], rest);

    private void File_(string place, string rest)
    {
        var path = Place(place, rest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private RegistryKey Key(string hive, string path) => Registry.CurrentUser.CreateSubKey((hive == "HKLM" ? _hklm : _hkcu) + '\\' + path);

    private void InstallDiscord()
    {
        File_("LocalAppData", @"Discord\Update.exe");
        File_("LocalAppData", @"Discord\app-1.0.9\Discord.exe");
        File_("AppData", @"discord\Local Storage\leveldb\000003.log");
        File_("AppData", @"Microsoft\Windows\Start Menu\Programs\Discord Inc\Discord.lnk");
        File_("Desktop", "Discord.lnk");
        using (var entry = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Discord"))
        {
            entry.SetValue("DisplayName", "Discord");
            entry.SetValue("UninstallString", $"\"{Place("LocalAppData", @"Discord\Update.exe")}\" --uninstall");
        }
        using (var run = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            run.SetValue("Discord", "Update.exe --processStart Discord.exe");
            run.SetValue("Steam", "steam.exe -silent");
        }
        Key("HKCU", @"Software\Discord").Dispose();
        using var protocol = Key("HKCU", @"Software\Classes\discord\shell\open\command");
        protocol.SetValue("", "Discord.exe --url %1");
    }

    [Fact]
    public async Task DiscordGoesWithEverythingItLeftAndNothingElse()
    {
        InstallDiscord();
        File_("LocalAppData", @"DiscordBot\keep.txt");
        File_("AppData", @"Spotify\Spotify.exe");
        var apps = Apps();
        var before = apps.Scan().Single(app => app.Id == "discord");
        Assert.True(before.Installed);
        Assert.True(before.Leftovers >= 8);
        Assert.False(before.NeedsAdmin);

        var result = await apps.RemoveAsync("discord", CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Single(_uninstalled);
        Assert.Empty(_admin);
        Assert.False(Directory.Exists(Place("LocalAppData", "Discord")));
        Assert.False(Directory.Exists(Place("AppData", "discord")));
        Assert.False(Directory.Exists(Place("AppData", @"Microsoft\Windows\Start Menu\Programs\Discord Inc")));
        Assert.False(File.Exists(Place("Desktop", "Discord.lnk")));
        Assert.Null(Registry.CurrentUser.OpenSubKey(_hkcu + @"\Software\Discord"));
        Assert.Null(Registry.CurrentUser.OpenSubKey(_hkcu + @"\Software\Classes\discord"));
        using var run = Registry.CurrentUser.OpenSubKey(_hkcu + @"\Software\Microsoft\Windows\CurrentVersion\Run");
        Assert.Null(run!.GetValue("Discord"));
        Assert.NotNull(run.GetValue("Steam"));
        Assert.True(File.Exists(Place("LocalAppData", @"DiscordBot\keep.txt")));
        Assert.True(File.Exists(Place("AppData", @"Spotify\Spotify.exe")));
        var after = apps.Scan().Single(app => app.Id == "discord");
        Assert.False(after.Installed);
        Assert.Equal(0, after.Leftovers);
    }

    [Fact]
    public void LeftoversOfAnInstalledAppStayAndOnlyAGoneAppsAreCleared()
    {
        InstallDiscord();
        File_("LocalAppData", @"NVIDIA Corporation\NVIDIA App\cache.bin");
        File_("ProgramData", @"NVIDIA Corporation\NVIDIA App\log.txt");
        File_("ProgramData", @"NVIDIA Corporation\Drs\nvdrsdb0.bin");
        var cleared = Apps().CleanLeftovers();

        Assert.Equal(1, cleared);
        Assert.False(Directory.Exists(Place("LocalAppData", @"NVIDIA Corporation\NVIDIA App")));
        Assert.True(File.Exists(Place("ProgramData", @"NVIDIA Corporation\NVIDIA App\log.txt"))); // needs Windows' permission: asked for only on a removal
        Assert.True(File.Exists(Place("ProgramData", @"NVIDIA Corporation\Drs\nvdrsdb0.bin"))); // the driver's
        Assert.True(File.Exists(Place("AppData", @"discord\Local Storage\leveldb\000003.log"))); // Discord is still installed
    }

    [Fact]
    public void AnEntryWhoseUninstallerIsGoneIsALeftover()
    {
        using (var entry = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Discord"))
        {
            entry.SetValue("DisplayName", "Discord");
            entry.SetValue("UninstallString", $"\"{Place("LocalAppData", @"Discord\Update.exe")}\" --uninstall");
        }
        var apps = Apps();
        Assert.False(apps.Scan().Single(app => app.Id == "discord").Installed);
        Assert.Equal(1, apps.CleanLeftovers());
        Assert.Null(Registry.CurrentUser.OpenSubKey(_hkcu + @"\Software\Microsoft\Windows\CurrentVersion\Uninstall\Discord"));
    }

    [Fact]
    public async Task MachineLeftoversGoInOneStepWindowsAsksFor()
    {
        File_("ProgramFiles", @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe");
        File_("ProgramData", @"NVIDIA Corporation\NVIDIA App\log.txt");
        File_("ProgramFiles", @"NVIDIA Corporation\Display.NvContainer\NVDisplay.Container.exe");
        Key("HKLM", @"SOFTWARE\NVIDIA Corporation\Global\GFExperience").Dispose();
        Key("HKLM", @"SOFTWARE\NVIDIA Corporation\Global\NVTweak").Dispose();
        using (var entry = Key("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}_Display.NvApp"))
        {
            entry.SetValue("DisplayName", "NVIDIA App 11.0.4");
            entry.SetValue("UninstallString", $"\"{Place("ProgramFiles", @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe")}\" -uninstall");
        }
        var apps = Apps();
        Assert.True(apps.Scan().Single(app => app.Id == "nvidia").NeedsAdmin);

        var result = await apps.RemoveAsync("nvidia", CancellationToken.None);

        // The fake uninstaller removed the exe; the entry it leaves is an orphan, the rest went in the elevated step.
        Assert.True(result.Ok, result.Message);
        Assert.Single(_uninstalled);
        Assert.NotEmpty(_admin);
        Assert.All(_admin, command => Assert.DoesNotContain("NvContainer", command));
        Assert.False(Directory.Exists(Place("ProgramData", @"NVIDIA Corporation\NVIDIA App")));
        Assert.True(File.Exists(Place("ProgramFiles", @"NVIDIA Corporation\Display.NvContainer\NVDisplay.Container.exe")));
        Assert.NotNull(Registry.CurrentUser.OpenSubKey(_hklm + @"\SOFTWARE\NVIDIA Corporation\Global\NVTweak"));
        Assert.Null(Registry.CurrentUser.OpenSubKey(_hklm + @"\SOFTWARE\NVIDIA Corporation\Global\GFExperience"));
    }

    [Fact]
    public async Task ADeclinedPermissionIsNotSuccess()
    {
        File_("ProgramData", @"NVIDIA Corporation\NVIDIA App\log.txt");
        _adminAllowed = false;
        var result = await Apps().RemoveAsync("nvidia", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(1, result.Left);
        Assert.True(File.Exists(Place("ProgramData", @"NVIDIA Corporation\NVIDIA App\log.txt")));
    }

    [Fact]
    public async Task GameBarGoesAndWindowsStopsAskingForIt()
    {
        _packages.Installed.Add("Microsoft.XboxGamingOverlay_7.325.1.0_x64__8wekyb3d8bbwe");
        _packages.Installed.Add("Microsoft.GamingApp_2509.1001.27.0_x64__8wekyb3d8bbwe");
        var apps = Apps();
        Assert.True(apps.Scan().Single(app => app.Id == "gamebar").Installed);

        var result = await apps.RemoveAsync("gamebar", CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(["Microsoft.XboxGamingOverlay_7.325.1.0_x64__8wekyb3d8bbwe"], _packages.Removed);
        using var dvr = Registry.CurrentUser.OpenSubKey(_hkcu + @"\Software\Microsoft\Windows\CurrentVersion\GameDVR");
        Assert.Equal(0, dvr!.GetValue("AppCaptureEnabled"));
        using var store = Registry.CurrentUser.OpenSubKey(_hkcu + @"\System\GameConfigStore");
        Assert.Equal(0, store!.GetValue("GameDVR_Enabled"));
    }

    [Theory]
    [InlineData("NVIDIA App", "NVIDIA App", true)]
    [InlineData("NVIDIA App 11.0.4.526", "NVIDIA App", true)]
    [InlineData("NVIDIA Apparatus", "NVIDIA App", false)]
    [InlineData("NVIDIA App Helper", "NVIDIA App", false)]
    [InlineData("Discord PTB", "Discord", false)]
    public void UninstallEntriesMatchByName(string display, string name, bool matches) =>
        Assert.Equal(matches, ReplacedApps.NameMatches(display, name));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
        Registry.CurrentUser.DeleteSubKeyTree(_hkcu[..^5], false);
    }

    private sealed class FakePackages : IAppPackages
    {
        public List<string> Installed { get; } = [];
        public List<string> Removed { get; } = [];

        public IReadOnlyList<string> Find(string name) => Installed.Where(full => full.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)).ToList();

        public Task<bool> RemoveAsync(string fullName, CancellationToken ct)
        {
            Removed.Add(fullName);
            Installed.Remove(fullName);
            return Task.FromResult(true);
        }
    }
}
