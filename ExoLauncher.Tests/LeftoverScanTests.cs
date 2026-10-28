using System.Diagnostics;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Tests;

/// <summary>
/// The deep clean for any app (System › Apps), in a sandbox: folders under a temp root, keys under a test key in HKCU.
/// Ways it could fail: what points into the app's folder (a service, a COM class, App Paths, UserAssist's ROT13 record, a
/// scheduled task) is missed, or another program's is taken; a folder or key matched only by name is deleted with the
/// sure ones instead of shown apart, or deleted without the user seeing which; a generic or company name, the publisher's
/// own, or a name that is a path ("Microsoft\Windows", "..") matches; a folder that holds something still installed goes;
/// the MSI registration is looked for under the wrong key; a junction at or above a leftover (one the user can write
/// to, as in ProgramData) carries an administrator's rd or del into what it leads to; one the Recycle Bin doesn't take
/// is deleted for good anyway; a quote, %, !, ^ or control character in a name breaks out of the elevated cmd.
/// </summary>
public sealed class LeftoverScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-leftovers-" + Guid.NewGuid().ToString("N"));
    private readonly string _hkcu;
    private readonly string _hklm;

    public LeftoverScanTests()
    {
        var test = @"Software\ExoLauncherTests\" + Guid.NewGuid().ToString("N");
        _hkcu = test + @"\HKCU";
        _hklm = test + @"\HKLM";
        foreach (var place in Places.Values) Directory.CreateDirectory(place);
    }

    private Dictionary<string, string> Places => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAppData"] = Path.Combine(_root, "Local"),
        ["LocalLow"] = Path.Combine(_root, "LocalLow"),
        ["AppData"] = Path.Combine(_root, "Roaming"),
        ["Desktop"] = Path.Combine(_root, "Desktop"),
        ["CommonDesktop"] = Path.Combine(_root, "PublicDesktop"),
        ["CommonPrograms"] = Path.Combine(_root, "CommonPrograms"),
        ["ProgramFiles"] = Path.Combine(_root, "Program Files"),
        ["ProgramData"] = Path.Combine(_root, "ProgramData"),
        ["Temp"] = Path.Combine(_root, "Temp"),
    };

    private string Place(string place, string rest) => Path.Combine(Places[place], rest);

    private string File_(string place, string rest)
    {
        var path = Place(place, rest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private RegistryKey Key(string hive, string path) => Registry.CurrentUser.CreateSubKey((hive == "HKLM" ? _hklm : _hkcu) + '\\' + path);

    private const string Product = "{12345678-90AB-CDEF-1234-567890ABCDEF}";

    [Fact]
    public void WhatPointsIntoTheAppIsSureWhatOnlyHasItsNameIsLikelyAndNothingElseIsFound()
    {
        var home = Place("ProgramFiles", "Acme Paint");
        File_("ProgramFiles", @"Acme Paint\acmepaint.exe");
        var other = File_("ProgramFiles", @"Other\other.exe");
        using (var service = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\AcmeUpdate")) service.SetValue("ImagePath", $"\"{home}\\updater.exe\" /service");
        using (var service = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\OtherSvc")) service.SetValue("ImagePath", other);
        using (var server = Key("HKLM", @"SOFTWARE\Classes\CLSID\{AAAAAAAA-0000-0000-0000-000000000001}\InprocServer32")) server.SetValue("", home + @"\shell.dll");
        using (var server = Key("HKLM", @"SOFTWARE\Classes\CLSID\{AAAAAAAA-0000-0000-0000-000000000002}\InprocServer32")) server.SetValue("", other);
        using (var paths = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths\acmepaint.exe")) paths.SetValue("", home + @"\acmepaint.exe");
        using (var paths = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths\other.exe")) paths.SetValue("", other);
        using (var assist = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count"))
        {
            assist.SetValue(Rot13(home + @"\acmepaint.exe"), new byte[] { 1 });
            assist.SetValue(Rot13(other), new byte[] { 1 });
        }
        Key("HKLM", @"SOFTWARE\Classes\Installer\Products\87654321BA09FEDC2143658709BADCFE").Dispose();
        File_("AppData", @"Acme Paint\settings.ini");
        Key("HKCU", @"Software\Acme Software\Acme Paint").Dispose();
        Key("HKCU", @"Software\Acme Software\Other Tool").Dispose();
        File_("AppData", @"Data\keep.txt");
        File_("LocalAppData", @"CrashDumps\acmepaint.exe.4242.dmp");
        var scan = new LeftoverScan(Places, _hkcu, _hklm);
        scan.AddTestTask(@"\Acme\Update", home + @"\updater.exe");
        scan.AddTestTask(@"\Other\Run", other);

        var found = scan.Find(new Trail("Acme Paint")
        {
            Homes = [home], Names = ["Acme Paint", "Data"], Exes = ["acmepaint.exe"], Publisher = "Acme Software", ProductCode = Product,
        }, deep: true);

        string Of(Leftover item) => item.Value is null ? item.Path : item.Path + '|' + item.Value;
        var sure = found.Where(item => !item.Likely).Select(Of).ToList();
        var likely = found.Where(item => item.Likely).Select(Of).ToList();
        Assert.Contains(home, sure);
        Assert.Contains(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeUpdate", sure);
        Assert.Contains(@"HKLM\SOFTWARE\Classes\CLSID\{AAAAAAAA-0000-0000-0000-000000000001}", sure);
        Assert.Contains(@"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\acmepaint.exe", sure);
        Assert.Contains(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count|" + Rot13(home + @"\acmepaint.exe"), sure);
        Assert.Contains(@"HKLM\SOFTWARE\Classes\Installer\Products\87654321BA09FEDC2143658709BADCFE", sure);
        Assert.Contains(Place("LocalAppData", @"CrashDumps\acmepaint.exe.4242.dmp"), sure);
        Assert.Contains(@"\Acme\Update", sure);
        Assert.Equal([Place("AppData", "Acme Paint"), @"HKCU\Software\Acme Software\Acme Paint"], likely.Order());
        // Nothing of the other program's, and no generic name.
        Assert.DoesNotContain(found, item => Of(item).Contains("Other", StringComparison.OrdinalIgnoreCase) || Of(item).Contains(Rot13(other), StringComparison.Ordinal));
        Assert.DoesNotContain(found, item => item.Path.EndsWith(@"\Data", StringComparison.OrdinalIgnoreCase));
        // A service and a task go by their own commands, in the step Windows asks permission for.
        Assert.Equal("sc stop \"AcmeUpdate\" & sc delete \"AcmeUpdate\"", scan.AdminCommand(found.Single(item => item.Kind == LeftoverKind.Service)));
        Assert.Equal("schtasks /delete /tn \"\\Acme\\Update\" /f", scan.AdminCommand(found.Single(item => item.Kind == LeftoverKind.Task)));
    }

    // BCU's rule: an app's folder that holds another installed program (a shared tree) stays, and so does anything holding it.
    [Fact]
    public void AFolderHoldingSomethingStillInstalledStays()
    {
        var home = Place("ProgramFiles", @"Acme\Paint");
        File_("ProgramFiles", @"Acme\Paint\paint.exe");
        File_("ProgramFiles", @"Acme\Paint\Plugins\Brush\brush.exe");
        var found = new LeftoverScan(Places, _hkcu, _hklm).Find(new Trail("Acme Paint")
        {
            Homes = [home], Keep = [Place("ProgramFiles", @"Acme\Paint\Plugins\Brush")],
        }, deep: false);
        Assert.DoesNotContain(found, item => item.Path.Equals(home, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AProductCodeIsPackedAsWindowsInstallerKeysIt() =>
        Assert.Equal("87654321BA09FEDC2143658709BADCFE", LeftoverScan.Packed(Product));

    [Theory]
    [InlineData("7-Zip 26.01 (x64)", "7-Zip")]
    [InlineData("Stremio version 5.0.27", "Stremio")]
    [InlineData("Grok Bot 0.75.8", "Grok Bot")]
    [InlineData("Python 3.13.15 (64-bit)", "Python")]
    [InlineData("Devin (User)", "Devin")]
    [InlineData("Counter-Strike 2", "Counter-Strike 2")]
    public void AProgramsBareNameLosesItsVersionAndBitness(string name, string bare) => Assert.Equal(bare, WebHostBridge.BareName(name));

    // A program's name is anyone's to set: its publisher's key holds all its programs, a company's too, and a name that is
    // a path names another's.
    [Fact]
    public void ANameThatIsItsPublishersACompanysOrAPathIsNoLead()
    {
        foreach (var key in new[] { @"SOFTWARE\Acme Studio", @"SOFTWARE\Logitech", @"SOFTWARE\Microsoft\Windows", @"SOFTWARE\Acme Paint" }) Key("HKLM", key).Dispose();
        Directory.CreateDirectory(Place("ProgramData", "Acme Studio"));
        var found = new LeftoverScan(Places, _hkcu, _hklm).Find(new Trail("Acme Paint")
        {
            Names = ["Acme Studio", "Logitech", @"Microsoft\Windows", @"Acme\..\..", "Acme Paint"], Publisher = "Acme Studio",
        }, deep: false);
        Assert.Equal([@"HKLM\SOFTWARE\Acme Paint"], found.Select(item => item.Path));
        Assert.True(found.Single().Likely);
    }

    // What the user is asked before a clean names each leftover found by name alone, so they can leave it out.
    [Fact]
    public void TheConfirmationListsWhatWasFoundByNameOnly()
    {
        var sure = new Leftover(@"HKCU\Software\Acme Paint\Sure", true, null, false);
        var likely = Enumerable.Range(0, 10).Select(i => new Leftover($@"HKCU\Software\Acme {(char)('A' + i)}{new string('z', i)}", true, null, false, Likely: true)).ToList();
        List<Leftover> chosen = [sure, .. likely.AsEnumerable().Reverse()];
        var body = WebHostBridge.CleanBody("Acme Paint", chosen, LeftoverScan.Sizes(chosen));
        Assert.All(likely.Take(8), item => Assert.Contains(item.Path, body));
        Assert.All(likely.Skip(8), item => Assert.DoesNotContain(item.Path, body));
        Assert.Contains("and 2 more", body);
        Assert.DoesNotContain(sure.Path, body);
        Assert.DoesNotContain("by name", WebHostBridge.CleanBody("Acme Paint", [sure], LeftoverScan.Sizes([sure])));
    }

    // A folder an app left in ProgramData is the user's to write: swapped for a junction (or one put above it), an
    // administrator's rd would go wherever it leads. Checked again right before it goes, it stays, and so does that.
    [Fact]
    public void ALinkAtOrAboveALeftoverIsRefusedAndWhatItLeadsToStays()
    {
        var keep = Outside();
        var link = Place("ProgramData", "Acme Paint");
        Assert.SkipUnless(Junction(link, Path.Combine(_root, "Outside")), "Windows didn't make a junction here");
        var scan = new LeftoverScan(Places, _hkcu, _hklm);
        var itself = new Leftover(link, false, null, true);
        var below = new Leftover(Path.Combine(link, "Acme"), false, null, true);

        Assert.Null(scan.AdminCommand(itself));
        Assert.Null(scan.AdminCommand(below));
        Assert.False(scan.Delete(below with { Machine = false }));

        Assert.True(File.Exists(keep));
        Assert.Equal(LeftoverScan.Linked, scan.Why([itself, below]));
        // A path whose words lead elsewhere is refused too; a real folder there still goes.
        var dotted = new Leftover(Place("ProgramData", @"Real\..\..\Outside"), false, null, true);
        Assert.Null(scan.AdminCommand(dotted));
        Assert.Equal(LeftoverScan.Barred, scan.Why([dotted]));
        var real = Place("ProgramData", "Real");
        Directory.CreateDirectory(real);
        Assert.Equal($"rd /s /q \"{real}\"", scan.AdminCommand(new Leftover(real, false, null, true)));
    }

    // The elevated step itself, run here as this user on the test's own folders: rd /s takes a link inside a folder (or
    // a folder swapped for one after the check) without going into it, and del takes a file only while it is one.
    [Fact]
    public void TheAdministratorsStepNeverGoesIntoALink()
    {
        var keep = Outside();
        var outside = Path.Combine(_root, "Outside");
        var folder = Place("ProgramData", "Acme Paint");
        var log = File_("ProgramData", @"Acme Paint\log.txt");
        Assert.SkipUnless(Junction(Path.Combine(folder, "Link"), outside), "Windows didn't make a junction here");
        var scan = new LeftoverScan(Places, _hkcu, _hklm);
        var rd = scan.AdminCommand(new Leftover(folder, false, null, true));
        Assert.Equal($"rd /s /q \"{folder}\"", rd);
        var file = File_("ProgramData", @"Acme Tools\notes.txt");
        var del = scan.AdminCommand(new Leftover(file, false, null, true));
        Assert.Equal($"(if not exist \"{file}\\*\" del /f /q \"{file}\")", del);

        Cmd(rd!);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(keep));
        // Swapped for a junction while Windows asked for permission (to keep.txt's own folder, which del would empty).
        File.Delete(file);
        Assert.True(Junction(file, Path.GetDirectoryName(keep)!));
        Cmd(del!);
        Assert.True(File.Exists(keep));
        Directory.CreateDirectory(folder);
        Assert.True(Junction(Path.Combine(folder, "Swapped"), outside));
        Cmd($"rd /s /q \"{Path.Combine(folder, "Swapped")}\"");
        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(log));
    }

    // The confirmation says the Recycle Bin: one it doesn't take stays, and says why.
    [Fact]
    public void OneTheRecycleBinDoesntTakeStays()
    {
        var file = File_("AppData", @"Acme Paint\settings.ini");
        var folder = new Leftover(Place("AppData", "Acme Paint"), false, null, false);
        var scan = new LeftoverScan(Places, _hkcu, _hklm) { Recycler = _ => LeftoverScan.TooBig };

        Assert.False(scan.Delete(folder));
        Assert.True(File.Exists(file));
        Assert.Equal(LeftoverScan.TooBig, scan.Why([folder]));
        // A bin that claims it went while it is still there is no success either.
        var silent = new LeftoverScan(Places, _hkcu, _hklm) { Recycler = _ => null };
        Assert.False(silent.Delete(folder));
        Assert.Equal(LeftoverScan.NotRecycled, silent.Why([folder]));
        // One gone already is gone, whatever the bin says.
        Assert.True(scan.Delete(new Leftover(Place("AppData", "Gone"), false, null, false)));
    }

    [Fact]
    public void OnlyAFixedDrivesRecycleBinTakesItAndOnlyWhenItFits()
    {
        Assert.Null(LeftoverScan.RecycleBinRefuses(100, DriveType.Fixed, false, 100));
        Assert.Equal(LeftoverScan.TooBig, LeftoverScan.RecycleBinRefuses(101, DriveType.Fixed, false, 100));
        Assert.Equal(LeftoverScan.BinOff, LeftoverScan.RecycleBinRefuses(1, DriveType.Fixed, true, 100));
        Assert.Equal(LeftoverScan.NoBin, LeftoverScan.RecycleBinRefuses(1, DriveType.Network, false, 100));
        Assert.Equal(LeftoverScan.NoBin, LeftoverScan.RecycleBinRefuses(1, DriveType.Removable, false, 100));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("%")]
    [InlineData("!")]
    [InlineData("^")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\u0001")]
    [InlineData("\u001b")]
    [InlineData("\u007f")]
    [InlineData("\u0085")]
    public void TheElevatedCommandRefusesWhatQuotesDontHold(string bad)
    {
        var scan = new LeftoverScan(Places, _hkcu, _hklm);
        Assert.NotNull(scan.AdminCommand(new Leftover(@"HKLM\SOFTWARE\Acme Paint", true, "Path", true)));
        var key = new Leftover(@"HKLM\SOFTWARE\Acme" + bad + "Paint", true, null, true);
        Assert.Null(scan.AdminCommand(key));
        Assert.Null(scan.AdminCommand(new Leftover(@"HKLM\SOFTWARE\Acme Paint", true, "Path" + bad, true)));
        Assert.Equal(LeftoverScan.Unquotable, scan.Why([key]));
    }

    // Outside\Acme\keep.txt under the root: what a link leads to.
    private string Outside()
    {
        var keep = Path.Combine(_root, @"Outside\Acme\keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
        File.WriteAllText(keep, "x");
        return keep;
    }

    private readonly List<string> _links = [];

    // A real junction, as any user may make one; false where Windows didn't.
    private bool Junction(string link, string target)
    {
        _links.Add(link);
        Cmd($"mklink /J \"{link}\" \"{target}\"");
        return Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null;
    }

    [Fact]
    public void TasksAreReadFromTheHelpersLines()
    {
        var tasks = LeftoverScan.ParseTasks("\\MicrosoftEdgeUpdateTaskMachineUA\t%ProgramFiles(x86)%\\Microsoft\\EdgeUpdate\\MicrosoftEdgeUpdate.exe\r\n" +
            "\\Vendor\\Café\tC:\\Users\\José\\app.exe\r\nWARNING: a stray line\r\n\\NoProgram\t\r\n");
        Assert.Equal([("\\MicrosoftEdgeUpdateTaskMachineUA", "%ProgramFiles(x86)%\\Microsoft\\EdgeUpdate\\MicrosoftEdgeUpdate.exe"),
            ("\\Vendor\\Café", "C:\\Users\\José\\app.exe")], tasks);
        Assert.Empty(LeftoverScan.ParseTasks(null));   // the helper didn't run: nothing, not a crash
    }

    private static void Cmd(string command)
    {
        using var cmd = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c " + command)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath(),
        });
        cmd?.WaitForExit(30_000);
    }

    private static string Rot13(string text) => new([.. text.Select(c => c switch
    {
        >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
        >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
        _ => c,
    })]);

    public void Dispose()
    {
        // A junction first, on its own: Directory.Delete's walk takes one for a volume's mount point, fails, and leaves the root.
        foreach (var link in _links)
            try { Directory.Delete(link); } catch (IOException) { }
        try { Directory.Delete(_root, true); } catch (IOException) { }
        Registry.CurrentUser.DeleteSubKeyTree(_hkcu[..^5], false);
    }
}
