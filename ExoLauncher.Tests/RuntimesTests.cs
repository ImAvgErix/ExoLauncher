using System.Security.Cryptography;
using ExoLauncher.Models;
using ExoLauncher.Services;
using static ExoLauncher.Services.DependencyService;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Game runtimes could be silently wrong:
/// - detection: an older version reads as up to date (or a newer one as older, so Exo offers a downgrade), a version
///   compares as text, the AIO's own uninstall entries or Windows Installer's three-part versions are misread, v14's
///   record outlives its files, DirectX's partial runtime reads as complete, PhysX shows on a PC without an NVIDIA card;
/// - .NET: a preview or a file from another host, or one without a SHA-512, is offered; the fallback outlives Microsoft's list;
/// - per game: a store's record or a game's redist folder maps to the wrong runtime or architecture;
/// - install: the plan runs out of order, bulk installs what Microsoft no longer supports, Repair misses a Visual C++, the
///   catalog pins a URL on an unlisted host or a malformed hash, or a file that isn't the maker's is let through.
/// </summary>
public sealed class RuntimesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("exo-runtimes-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Snapshot Pc(
        (string Key, string Name, string? Version)[]? uninstall = null,
        Dictionary<string, string>? files = null,
        Dictionary<string, string>? values = null,
        string[]? x64 = null, string[]? x86 = null, bool nvidia = false, bool arm64 = false) =>
        new(uninstall?.Select(e => new UninstallEntry(e.Key, e.Name, e.Version)).ToArray() ?? [],
            files ?? [], values ?? [], x64 ?? [], x86 ?? [], nvidia, arm64, "feature level 12_1 on Test GPU");

    private static RuntimeStatus Of(IReadOnlyList<RuntimeStatus> statuses, string id) => Assert.Single(statuses, s => s.Runtime.Id == id);

    private static readonly Dictionary<string, string> AllDirectX =
        DirectXFiles.SelectMany(file => new[] { "sys:" + file, "wow:" + file }).ToDictionary(key => key, _ => "");

    [Fact]
    public void AnEmptyPcMissesEveryRuntimeAndShowsOnlyWhatApplies()
    {
        var statuses = Evaluate(Pc());
        Assert.All(statuses.Where(s => s.Runtime.IsVisualCpp), s => Assert.Equal(State.Missing, s.State));
        Assert.DoesNotContain(statuses, s => s.Runtime.Id is "vc14-arm64" or "physx" or "physx-legacy");
        Assert.Equal(State.Info, Of(statuses, "d3d12").State);
        Assert.Equal("feature level 12_1 on Test GPU", Of(statuses, "d3d12").Detail);
        Assert.Contains(Evaluate(Pc(nvidia: true, arm64: true)), s => s.Runtime.Id == "physx");
        Assert.Contains(Evaluate(Pc(nvidia: true, arm64: true)), s => s.Runtime.Id == "vc14-arm64");
    }

    [Theory]
    [InlineData("v14.44.35211.00", "Outdated")]
    [InlineData("v14.51.36247.00", "Current")]
    [InlineData("v14.52.37025.00", "Current")] // newer than Microsoft's permalink serves (the AIO's): never a downgrade
    [InlineData("v14.9.1.00", "Outdated")] // 9 < 51 as numbers, not as text
    public void VisualCpp14ComparesItsRecordedVersion(string recorded, string expected)
    {
        var statuses = Evaluate(Pc(values: new() { ["vc14:x64"] = recorded }, files: new() { ["sys:vcruntime140.dll"] = "14.0.0.0" }));
        Assert.Equal(Enum.Parse<State>(expected), Of(statuses, "vc14-x64").State);
    }

    [Fact]
    public void VisualCpp14WhoseFilesAreGoneIsMissing() =>
        Assert.Equal(State.Missing, Of(Evaluate(Pc(values: new() { ["vc14:x64"] = "v14.51.36247.00" })), "vc14-x64").State);

    [Fact]
    public void VisualCpp2005ReadsWindowsInstallerVersionsAndShowsFileVersions()
    {
        var aio = Evaluate(Pc(uninstall:
        [
            ("vcredist08_x86", "Microsoft Visual C++ 2005 Redistributable (x86)", "8.0.61187"),
            ("{ad8a2fa1-06e7-4b0d-927d-6e54b3d31028}", "Microsoft Visual C++ 2005 Redistributable (x64)", "8.0.59192"),
        ]));
        Assert.Equal(State.Current, Of(aio, "vc2005-x86").State);
        Assert.Equal("8.0.50727.6229", Of(aio, "vc2005-x86").Found!.Shown);
        Assert.Equal(State.Outdated, Of(aio, "vc2005-x64").State);
        Assert.Equal("8.0.50727.4053", Of(aio, "vc2005-x64").Found!.Shown);
        // The x64 entry never counts as the x86 one.
        var x64Only = Evaluate(Pc(uninstall: [("{ad8a2fa1-06e7-4b0d-927d-6e54b3d31028}", "Microsoft Visual C++ 2005 Redistributable (x64)", "8.0.61000")]));
        Assert.Equal(State.Missing, Of(x64Only, "vc2005-x86").State);
        Assert.Equal(State.Current, Of(x64Only, "vc2005-x64").State);
    }

    [Fact]
    public void OlderVisualCppNamesAndThreePartVersionsCompare()
    {
        var statuses = Evaluate(Pc(uninstall:
        [
            ("{9BE518E6-ECC6-35A9-88E4-87755C07200F}", "Microsoft Visual C++ 2008 Redistributable - x86 9.0.30729.17", "9.0.30729"),
            ("vcredist09_x64", "Microsoft Visual C++ 2008 Redistributable (x64)", "9.0.30729.7523"),
            ("{CF2BEA3C-26EA-32F8-AA9B-331F7E34BA97}", "Microsoft Visual C++ 2012 x64 Minimum Runtime - 11.0.61135", "11.0.61135"),
            ("{BD95A8CD-1D9F-35AD-981A-3E7925026EBB}", "Microsoft Visual C++ 2012 x86 Minimum Runtime - 11.0.50727", "11.0.50727"),
            ("{53CF6934-A98D-3D84-9146-FC4EDF3D5641}", "Microsoft Visual C++ 2013 x64 Minimum Runtime - 12.0.40664", "12.0.40664"),
        ], files: new() { ["sys:msvcr100.dll"] = "10.0.40219.473", ["wow:msvcr100.dll"] = "10.0.40219.1" }));
        Assert.Equal(State.Outdated, Of(statuses, "vc2008-x86").State);
        Assert.Equal(State.Current, Of(statuses, "vc2008-x64").State);
        Assert.Equal(State.Current, Of(statuses, "vc2010-x64").State);
        Assert.Equal(State.Outdated, Of(statuses, "vc2010-x86").State);
        Assert.Equal(State.Current, Of(statuses, "vc2012-x64").State);
        Assert.Equal(State.Outdated, Of(statuses, "vc2012-x86").State);
        Assert.Equal(State.Current, Of(statuses, "vc2013-x64").State);
        Assert.Equal(State.Missing, Of(statuses, "vc2013-x86").State);
    }

    [Fact]
    public void DirectXIsCompletePartlyThereOrMissing()
    {
        Assert.Equal(State.Current, Of(Evaluate(Pc(files: AllDirectX)), "directx").State);
        var partial = Evaluate(Pc(files: new() { ["sys:xinput1_3.dll"] = "", ["wow:xinput1_3.dll"] = "" }));
        Assert.Equal(State.Outdated, Of(partial, "directx").State);
        Assert.Equal($"2 of {DirectXFiles.Length * 2} files", Of(partial, "directx").Found!.Shown);
        Assert.Equal(State.Missing, Of(Evaluate(Pc()), "directx").State);
    }

    [Fact]
    public void DotnetComparesAgainstMicrosoftsListElseTheCatalog()
    {
        var pc = Pc(x64: ["8.0.33", "10.0.13", "11.0.0-rc.3.26522.148"], values: new() { ["ndp4"] = "528040", ["ndp35"] = "1" });
        var offline = Evaluate(pc, now: new DateTime(2026, 10, 26));
        Assert.Equal(State.Outdated, Of(offline, "dotnet10-x64").State); // catalog: 10.0.14
        Assert.Equal(State.Current, Of(offline, "dotnet8-x64").State);
        Assert.Equal(State.Missing, Of(offline, "dotnet9-x64").State);
        Assert.True(Of(offline, "dotnet6-x64").EndOfSupport);
        Assert.False(Of(offline, "dotnet8-x64").EndOfSupport);
        Assert.True(Of(Evaluate(pc, now: new DateTime(2026, 11, 11)), "dotnet8-x64").EndOfSupport);
        Assert.Equal(State.Outdated, Of(offline, "netfx481").State);
        Assert.Equal("4.8", Of(offline, "netfx481").Found!.Shown);
        Assert.Equal(State.Current, Of(offline, "netfx35").State);

        var newer = new DotnetRelease("10.0.15", false, new Dictionary<string, (string, string)> { ["x64"] = ("https://builds.dotnet.microsoft.com/x.exe", new string('A', 128)) });
        var online = Evaluate(Pc(x64: ["10.0.14"]), new Dictionary<string, DotnetRelease> { ["10-x64"] = newer });
        Assert.Equal(State.Outdated, Of(online, "dotnet10-x64").State);
        Assert.Equal("10.0.15", Of(online, "dotnet10-x64").Latest);
    }

    [Fact]
    public void PlanRunsInOrderAndLeavesOutWhatMicrosoftNoLongerSupports()
    {
        var statuses = Evaluate(Pc(nvidia: true, values: new() { ["vc14:x64"] = "v14.40.1.00", ["vc14:x86"] = "v14.51.36247.00", ["webview2"] = "1.0" },
            files: new() { ["sys:vcruntime140.dll"] = "", ["wow:vcruntime140.dll"] = "" }), now: new DateTime(2026, 10, 26));
        var missing = Plan(statuses, "missing", null).Select(s => s.Runtime.Id).ToList();
        Assert.DoesNotContain("dotnet6-x64", missing);
        Assert.DoesNotContain("vc14-x64", missing); // older, not missing
        Assert.DoesNotContain(missing, id => id is "d3d12" or "vulkan" or "webview2");
        Assert.Equal(["netfx35", "netfx481", "vc2005-x86"], missing.Take(3));
        Assert.True(missing.IndexOf("vc2013-x64") < missing.IndexOf("directx"));
        Assert.True(missing.IndexOf("directx") < missing.IndexOf("dotnet10-x64"));
        Assert.True(missing.IndexOf("xna40") < missing.IndexOf("physx"));
        Assert.Equal("gameinput", missing[^1]);

        var update = Plan(statuses, "update", null).Select(s => s.Runtime.Id).ToList();
        Assert.Contains("vc14-x64", update);
        Assert.DoesNotContain("vc14-x86", update);

        // A game that names an unsupported one still gets it.
        Assert.Equal(["dotnet6-x64"], Plan(statuses, "missing", ["dotnet6-x64", "vc14-x86"]).Select(s => s.Runtime.Id));

        var repair = Plan(statuses, "repair", null).Select(s => s.Runtime.Id).ToList();
        Assert.Equal(statuses.Where(s => s.Runtime.IsVisualCpp).OrderBy(s => s.Runtime.Order).Select(s => s.Runtime.Id), repair);
        Assert.Contains("vc14-x86", repair); // up to date, installed again
    }

    [Fact]
    public void TheCatalogPinsEveryDownloadOnAListedHost()
    {
        Assert.Equal(Catalog.Count, Catalog.Select(r => r.Id).Distinct().Count());
        foreach (var r in Catalog.Where(r => r.Url is not null))
        {
            Assert.True(IsInstallerHost(new Uri(r.Url!)), r.Id + " downloads from an unlisted host");
            if (r.Id == "webview2") continue; // its bootstrapper is evergreen: Microsoft's signature decides
            Assert.Matches("^[0-9A-F]{64}$", r.Sha256);
            Assert.True(r.Size > 0, r.Id);
        }
        Assert.All(Catalog.Where(r => r.Kind == Kind.Zip), r => Assert.False(string.IsNullOrEmpty(r.Entry)));
    }

    private const string IndexSample = """
        { "releases-index": [
          { "channel-version": "11.0", "latest-release": "11.0.0-rc.3", "support-phase": "go-live", "releases.json": "x" },
          { "channel-version": "10.0", "latest-release": "10.0.14", "support-phase": "active" },
          { "channel-version": "9.0", "latest-release": "9.0.22", "support-phase": "maintenance" },
          { "channel-version": "7.0", "latest-release": "7.0.20", "support-phase": "eol" },
          { "channel-version": "6.0", "latest-release": "6.0.36", "support-phase": "eol" }
        ] }
        """;

    // Trimmed from builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/10.0.14/release.json (October 25, 2026).
    private const string ReleaseSample = """
        { "channel-version": "10.0", "release": { "release-version": "10.0.14", "windowsdesktop": { "version": "10.0.14", "files": [
          { "name": "windowsdesktop-runtime-win-arm64.exe", "rid": "win-arm64", "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-arm64.exe", "hash": "3ea03fbaf7281affa959db52299d2dd198879a10d7db1b58ae09246399f421fa51ed7fb45eca4d4742b98f7d34c0a2d9d412cb6707474a2743b32405ce10a965" },
          { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe", "hash": "337dacab1636219147b3fe75e451bf5b1c17bca2c86328266d40c4ad1ff0a1e8391c4746ca7a5b702b65b61e8bfe834566cfc1b548cc10aef90b441357b10b69" },
          { "name": "windowsdesktop-runtime-win-x64.zip", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.zip", "hash": "3214913702264452ac4b283b6fb2f6c14ab748cea36c7561608245c4a7d8cc10436a160032038865d039a016eab4975f65f1ae906de7615953fbec760f764ac4" },
          { "name": "windowsdesktop-runtime-win-x86.exe", "rid": "win-x86", "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x86.exe", "hash": "630c9a4a903349e51a887c28469f3866493054190f806d0aff4528de26435c69f0c98e2c6a1fea04752523b35e9320b1f0ce2fc379a9a3e54fd4a206f2740d32" }
        ] } } }
        """;

    [Fact]
    public void DotnetsReleaseListGivesTheSupportedChannelsAndTheirInstallers()
    {
        var channels = ParseDotnetIndex(IndexSample);
        Assert.Equal(["10.0", "9.0", "6.0"], channels.Select(c => c.Channel)); // no preview, no 7.0
        Assert.True(channels.Single(c => c.Channel == "6.0").EndOfSupport);

        var release = ParseDotnetRelease(ReleaseSample, endOfSupport: false)!;
        Assert.Equal("10.0.14", release.Version);
        Assert.Equal(["x64", "x86"], release.Files.Keys.Order());
        Assert.Equal("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe", release.Files["x64"].Url);
        Assert.StartsWith("337DACAB", release.Files["x64"].Sha512);
    }

    [Theory]
    [InlineData("http://builds.dotnet.microsoft.com/a/windowsdesktop-runtime-win-x64.exe", true)]
    [InlineData("https://evil.example/windowsdesktop-runtime-win-x64.exe", true)]
    [InlineData("https://dotnetcli.azureedge.net/a/windowsdesktop-runtime-win-x64.exe", true)]
    [InlineData("https://builds.dotnet.microsoft.com/a/windowsdesktop-runtime-win-x64.exe", false)]
    public void ADotnetInstallerFromElsewhereOrWithoutItsHashIsLeftOut(string url, bool hashed)
    {
        const string x64Url = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe";
        const string x64Hash = "337dacab1636219147b3fe75e451bf5b1c17bca2c86328266d40c4ad1ff0a1e8391c4746ca7a5b702b65b61e8bfe834566cfc1b548cc10aef90b441357b10b69";
        var json = ReleaseSample.Replace(x64Url, url).Replace(x64Hash, hashed ? x64Hash : "337dacab");
        var release = ParseDotnetRelease(json, false)!;
        Assert.False(release.Files.ContainsKey("x64"));
        Assert.True(release.Files.ContainsKey("x86"));
    }

    [Theory]
    [InlineData(@"vcredist\2010\vcredist_x64.exe", "vc2010-x64")]
    [InlineData(@"vcredist\2005\vcredist_x86.exe", "vc2005-x86")]
    [InlineData(@"vcredist\2022\Microsoft Visual C++ 2022 x64.cmd", "vc14-x64")]
    [InlineData(@"vcredist\2015\vc_redist.x86.exe", "vc14-x86")]
    [InlineData(@"vcredist\2013", "vc2013-x86,vc2013-x64")]
    [InlineData(@"_Installer\VC\vc_redist.x64.exe", "vc14-x64")]
    [InlineData(@"Engine\Extras\Redist\en-us\UEPrereqSetup_x64.exe", "vc14-x86,vc14-x64,directx")]
    [InlineData(@"DirectX\Jun2010\DXSETUP.exe", "directx")]
    [InlineData(@"DotNet\3.5\dotnetfx35.exe", "netfx35")]
    [InlineData(@"DotNet\4.8\ndp48-x86-x64-allos-enu.exe", "netfx481")]
    [InlineData(@"XNA\4.0\xnafx40_redist.msi", "xna40")]
    [InlineData(@"OpenAL\2.0.7.0\oalinst.exe", "openal")]
    [InlineData(@"PhysX\9.14.0702\PhysX-9.14.0702-SystemSoftware.msi", "physx")]
    [InlineData("windowsdesktop-runtime-8.0.33-win-x64.exe", "dotnet8-x64")]
    [InlineData("DirectX12Agility", "")]
    [InlineData("CrashReporter.exe", "")]
    public void AHintNamesItsRuntimes(string hint, string expected) =>
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), MapHint(hint));

    [Fact]
    public void GogDependenciesWithoutAnArchitectureAreTheThirtyTwoBitOnes()
    {
        var dependencies = GogDependencies("""{ "version": 2, "dependencies": ["MSVC2017", "MSVC2017_x64", "DirectX", "DotNet35"] }""").ToList();
        var ids = dependencies.SelectMany(d => MapHint(d, ["x86"])).ToList();
        Assert.Equal(["vc14-x86", "vc14-x64", "directx", "netfx35"], ids);
    }

    [Fact]
    public void SteamsSharedDepotsAndInstallScriptsNameTheirRuntimes()
    {
        const string acf = """
            "AppState"
            {
                "appid"		"359550"
                "SharedDepots"
                {
                    "228983"		"228980"
                    "228989"		"228980"
                    "228990"		"228980"
                    "229999"		"228980"
                }
            }
            """;
        Assert.Equal(["vc2010-x86", "vc2010-x64", "vc14-x86", "vc14-x64", "directx"], SteamSharedDepots(acf));

        const string script = """
            "installscript"
            {
                "Run Process"
                {
                    "dxsetup"
                    {
                        "process 1"		"%INSTALLDIR%\\_CommonRedist\\DirectX\\Jun2010\\DXSETUP.exe"
                        "command 1"		"/silent"
                    }
                    "x64 14.51.36247.0"
                    {
                        "process 1"		"%INSTALLDIR%\\_CommonRedist\\vcredist\\2022\\Microsoft Visual C++ 2022 x64.cmd"
                    }
                }
            }
            """;
        Assert.Equal(["directx", "vc14-x64"], InstallScriptHints(script).SelectMany(h => MapHint(h)));
    }

    [Fact]
    public void AGamesOwnRedistFolderNamesWhatItShips()
    {
        var game = Path.Combine(_folder, "Game");
        foreach (var file in new[] { @"_CommonRedist\vcredist\2010\vcredist_x64.exe", @"_CommonRedist\DirectX\Jun2010\DXSETUP.exe", @"_CommonRedist\DirectX\Jun2010\Jun2010_d3dx9_43_x64.cab", @"__redist\MSVC2019_x64\VC_redist.x64.exe", @"Binaries\Game.exe" })
        {
            var path = Path.Combine(game, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0]);
        }
        Directory.CreateDirectory(Path.Combine(game, "_CommonRedist", "DotNet", "3.5"));
        var ids = RuntimesFor(new GameEntry { Id = "local:x", Title = "Game", Store = StoreKind.Local, Path = game });
        Assert.Equal(new[] { "directx", "netfx35", "vc14-x64", "vc2010-x64" }, ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EpicsPrerequisitePathNamesItBest()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{ "PrereqName": "UE Prerequisites", "PrereqPath": "Engine/Extras/Redist/en-us/UEPrereqSetup_x64.exe" }""");
        Assert.Equal(["Engine/Extras/Redist/en-us/UEPrereqSetup_x64.exe"], EpicPrereqHints(doc.RootElement, "PrereqName", "PrereqPath"));
    }

    // .NET's own runtime library: Authenticode-signed by Microsoft (as ".NET", O=Microsoft Corporation) on every test machine.
    private static readonly string Signed = typeof(object).Assembly.Location;

    private static Runtime Pinned(string sha256, params string[] publishers) =>
        Find("vc2010-x64")! with { Sha256 = sha256, Publishers = publishers.Length == 0 ? ["Microsoft"] : publishers };

    [Fact]
    public void OnlyTheMakersExactFileIsLetThrough()
    {
        var hash = HashOf(Signed, SHA256.Create());
        Verify(Signed, Pinned(hash), hash, null); // Microsoft's own file, its own hash: runs

        // The right file under another maker's name, or with another pinned hash: refused.
        Assert.Throws<InvalidDataException>(() => Verify(Signed, Pinned(hash, "NVIDIA Corporation"), hash, null));
        Assert.Throws<InvalidDataException>(() => Verify(Signed, Pinned(new string('0', 64)), new string('0', 64), null));

        // A file that isn't signed by Microsoft, even with its hash pinned: refused.
        var unsigned = Path.Combine(_folder, "vc_redist.x64.exe");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0, 0, 1, 2, 3]);
        var unsignedHash = HashOf(unsigned, SHA256.Create());
        var refused = Assert.Throws<InvalidDataException>(() => Verify(unsigned, Pinned(unsignedHash), unsignedHash, null));
        Assert.Contains("isn't signed by Microsoft", refused.Message);
    }

    [Fact]
    public void TheElevatedScriptRunsEachStepInOrderAfterCheckingItAgain()
    {
        var vc2005 = Find("vc2005-x86")!;
        var netfx35 = Find("netfx35")!;
        var directx = Find("directx")!;
        var script = Script(@"C:\Users\O'Brien\AppData\Local\Temp\ExoLauncherDependencies\abc",
        [
            (netfx35, false, null, null),
            (vc2005, true, @"C:\stage\vc2005-x86.exe", "AA"),
            (directx, false, @"C:\stage\directx.exe", "BB"),
        ]);
        Assert.Contains("'C:\\Users\\O''Brien\\AppData", script); // quoted for PowerShell
        var netfx = script.IndexOf("Step 'netfx35'", StringComparison.Ordinal);
        var vc = script.IndexOf("Step 'vc2005-x86'", StringComparison.Ordinal);
        var dx = script.IndexOf("Step 'directx'", StringComparison.Ordinal);
        Assert.True(netfx >= 0 && netfx < vc && vc < dx);
        Assert.Contains("Take 'vc2005-x86.exe' 'AA'", script);
        Assert.Contains("REINSTALL=ALL", script); // repair's own switches
        Assert.Contains("/FeatureName:NetFx3", script);
        Assert.Contains("DXSETUP.exe", script);
        Assert.Contains("SetAccessRuleProtection", script); // the folder it runs from is administrators' only
    }

    [Theory]
    [InlineData(1603, "1603")]
    [InlineData(1618, "Another installation")]
    [InlineData(-2146498529, "0x800F081F")]
    [InlineData(-1001, "changed after Exo checked it")]
    [InlineData(42, "code 42")]
    public void ExitCodesReadAsPlainWords(int code, string expected) => Assert.Contains(expected, Words(code));
}
