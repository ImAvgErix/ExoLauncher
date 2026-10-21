using System.Buffers.Binary;
using System.Text;
using ExoLauncher.Adapters;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a game's Properties could be misread or written wrong (fixtures follow the
/// shapes of this PC's real files: Marvel Rivals' and Counter-Strike 2's manifests,
/// libraryfolders.vdf, appinfo.vdf records, Legendary's installed.json/config.ini):
/// - acf: a DLC switched off in UserConfig but not yet in MountedConfig reads as on;
///   a DLC depot (dlcappid) is missed as installed; escaped paths keep their double backslash;
///   a repeated key takes the last value; a truncated file throws;
/// - appinfo: a DLC whose depots sit in its own record (hasdepotsindlc) is left out; two
///   same-size depots for one DLC count twice; a DLC with no files (cosmetics, upgrades, a
///   family-shared game's DLC) is left out; a DLC the account does not hold gets a switch; an
///   unreleased DLC with no record is listed; a depot for another OS or language counts;
/// - branches: a coded branch is offered before its code was entered; the default is missing;
/// - languages: one Steam has no code for is offered; the depot bytes of another language count;
/// - helper answer: a failed or garbled answer reads as "everything off";
/// - Legendary: tags of an unselected component count as installed; a required component can
///   be switched off; config.ini loses other sections, comments or keys when one key is set,
///   or a second section of the same name is created; a bad app name reaches a path or URL.
/// </summary>
public sealed class GamePropertiesTests
{
    private const string MarvelRivalsAcf = """
        "AppState"
        {
        	"appid"		"2767030"
        	"name"		"Marvel Rivals"
        	"StateFlags"		"4"
        	"installdir"		"MarvelRivals"
        	"SizeOnDisk"		"74083193346"
        	"AutoUpdateBehavior"		"0"
        	"AllowOtherDownloadsWhileRunning"		"0"
        	"InstalledDepots"
        	{
        		"2767031"
        		{
        			"manifest"		"8373084957953552998"
        			"size"		"74083193346"
        		}
        	}
        	"SharedDepots"
        	{
        		"228989"		"228980"
        	}
        	"UserConfig"
        	{
        		"DisabledDLC"		"4406010"
        	}
        	"MountedConfig"
        	{
        		"DisabledDLC"		"4406010"
        	}
        }
        """;

    // Counter-Strike 2's shape: a DLC depot installed (dlcappid) and a DLC switched off that Steam has not applied yet.
    private const string CounterStrikeAcf = """
        "AppState"
        {
        	"appid"		"730"
        	"SizeOnDisk"		"65024032313"
        	"AutoUpdateBehavior"		"2"
        	"AllowOtherDownloadsWhileRunning"		"1"
        	"InstalledDepots"
        	{
        		"2347770" { "manifest" "1" "size" "65024032313" }
        		"2347779" { "manifest" "2" "size" "185781686" "dlcappid" "2387920" }
        	}
        	"UserConfig"
        	{
        		"language"		"german"
        		"BetaKey"		"csgo_legacy"
        		"DisabledDLC"		"2279721"
        	}
        	"MountedConfig"
        	{
        		"language"		"english"
        	}
        }
        """;

    [Fact]
    public void KeyValuesReadSectionsEscapesAndKeepTheFirstOfARepeatedKey()
    {
        var root = SteamKeyValues.ParseText("""
            "libraryfolders"
            {
              "0" { "path" "c:\\program files (x86)\\steam" "label" "" }
              "1" { "path" "D:\\SteamLibrary" }
              "dup" "first"
              "dup" "second"
              // a comment line
              "quote" "say \"hi\""
            }
            """);
        var folders = root.Section("libraryfolders");
        Assert.Equal(@"c:\program files (x86)\steam", folders.Section("0").Value("path"));
        Assert.Equal(@"D:\SteamLibrary", folders.Section("1").Value("PATH"));
        Assert.Equal("first", folders.Value("dup"));
        Assert.Equal("say \"hi\"", folders.Value("quote"));
        Assert.Same(SteamKeyValues.Empty, folders.Section("missing/deeper"));
        // A truncated file keeps what was read.
        Assert.Equal("1", SteamKeyValues.ParseText("\"AppState\" { \"appid\" \"1\" \"UserConfig\" {").Section("AppState").Value("appid"));
    }

    [Fact]
    public void ADlcFoundOnlyInTheGamesDepotsIsListedToo()
    {
        // Call of Duty's Multiplayer pack is in no DLC list, only on its depots (dlcappid), as Steam's Installed Content finds it.
        var app = SteamKeyValues.ParseText("""
            "appinfo" { "extended" { "listofdlc" "10,20" } "depots" { "101" { "dlcappid" "30" } "102" { "dlcappid" "30" } "103" { } } }
            """).Section("appinfo");
        Assert.Equal([10u, 20u, 30u], SteamGameProperties.DlcIds(app));
    }

    [Fact]
    public void ManifestReadsTheChoiceNotWhatIsMounted()
    {
        var rivals = SteamGameProperties.ReadManifest(MarvelRivalsAcf);
        Assert.Equal(74083193346, rivals.SizeOnDisk);
        Assert.Equal([4406010u], rivals.DisabledDlc);
        Assert.Empty(rivals.InstalledDlc);
        Assert.Equal("", rivals.Language);
        Assert.Equal(0, rivals.AutoUpdate);

        var cs = SteamGameProperties.ReadManifest(CounterStrikeAcf);
        Assert.Equal([2279721u], cs.DisabledDlc);
        Assert.Equal([2387920u], cs.InstalledDlc);
        Assert.Equal("german", cs.Language);
        Assert.Equal("csgo_legacy", cs.Beta);
        Assert.Equal(2, cs.AutoUpdate);
        Assert.Equal(1, cs.Background);
    }

    [Fact]
    public void FindsTheManifestInAnyLibraryFolder()
    {
        const string folders = """
            "libraryfolders" { "0" { "path" "c:\\program files (x86)\\steam" } "1" { "path" "D:\\SteamLibrary" } }
            """;
        var wanted = Path.Combine(@"D:\SteamLibrary", "steamapps", "appmanifest_2767030.acf");
        Assert.Equal(wanted, SteamGameProperties.FindManifest(@"c:\program files (x86)\steam", "2767030", folders, path => path == wanted));
        Assert.Null(SteamGameProperties.FindManifest(@"c:\steam", "1", null, _ => false));
    }

    // String table for the appinfo fixtures.
    private static readonly string[] Table =
    [
        "appinfo", "common", "name", "extended", "listofdlc", "depots", "hasdepotsindlc", "manifests", "public", "size",
        "dlcappid", "config", "oslist", "language", "branches", "description", "pwdrequired", "supported_languages",
        "supported", "english", "german", "klingon", "2767031", "2767033", "4406011", "4406012", "4406020", "4406030",
        "1.0", "secret", "4406031", "4406032", "3768761",
    ];

    private static int K(string key) => Array.IndexOf(Table, key) is var index and >= 0 ? index : throw new ArgumentException(key);

    private sealed class Kv
    {
        private readonly MemoryStream _s = new();
        private Kv Key(byte kind, string key) { _s.WriteByte(kind); _s.Write(BitConverter.GetBytes(K(key))); return this; }
        internal Kv Open(string key) => Key(0, key);
        internal Kv Str(string key, string value) { Key(1, key); _s.Write(Encoding.UTF8.GetBytes(value)); _s.WriteByte(0); return this; }
        internal Kv Int(string key, uint value) { Key(2, key); _s.Write(BitConverter.GetBytes(value)); return this; }
        internal Kv Depot(string id, long size, string? dlc = null, string? language = null)
        {
            Open(id);
            if (dlc is not null) Int("dlcappid", uint.Parse(dlc));
            if (language is not null) Open("config").Str("language", language).Close();
            return Open("manifests").Open("public").Str("size", size.ToString()).Close().Close().Close();
        }
        internal Kv Close() { _s.WriteByte(8); return this; }
        internal byte[] Bytes => _s.ToArray();
    }

    private static byte[] AppInfo(params (uint AppId, byte[] Kv)[] apps)
    {
        var body = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); body.Write(b); }
        body.Write(new byte[16]);
        foreach (var (appId, kv) in apps)
        {
            U32(appId);
            U32((uint)(60 + kv.Length));
            body.Write(new byte[60]);
            body.Write(kv);
        }
        U32(0);
        var tableOffset = body.Length;
        U32((uint)Table.Length);
        foreach (var s in Table) { body.Write(Encoding.UTF8.GetBytes(s)); body.WriteByte(0); }
        var data = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x07564429);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), tableOffset);
        return data;
    }

    /// <summary>
    /// Marvel Rivals' shape: the HD pack's two equal depots live in its own record; a second DLC
    /// brings files in the game's depots; a third has no files; a fourth (never on this PC) has files.
    /// </summary>
    private static byte[] RivalsAppInfo() => AppInfo(
        (2767030, new Kv().Open("appinfo")
            .Open("common").Str("name", "Marvel Rivals")
                .Open("supported_languages").Open("english").Str("supported", "true").Close()
                    .Open("german").Str("supported", "true").Close().Open("klingon").Str("supported", "true").Close().Close()
            .Close()
            .Open("extended").Str("listofdlc", "4406010,4406020,4406030,4406040").Close()
            .Open("depots").Str("hasdepotsindlc", "1")
                .Depot("2767031", 74083193346)
                .Depot("2767033", 2_000_000_000, language: "german")
                .Depot("4406020", 5_000_000, dlc: "4406020")
                .Open("branches")
                    .Open("public").Close()
                    .Open("1.0").Str("description", "Version 1.0").Close()
                    .Open("secret").Str("description", "Testers").Str("pwdrequired", "1").Close()
                .Close()
            .Close()
            .Close().Bytes),
        (4406010, new Kv().Open("appinfo")
            .Open("common").Str("name", "Marvel Rivals - High-Resolution Pack").Close()
            .Open("depots").Depot("4406011", 22376494559, dlc: "4406010").Depot("4406012", 22376494559, dlc: "4406010").Close()
            .Close().Bytes),
        (4406030, new Kv().Open("appinfo").Open("common").Str("name", "Costume").Close().Close().Bytes),
        (4406040, new Kv().Open("appinfo").Open("common").Str("name", "Unowned").Close()
            .Open("depots").Depot("4406031", 1_000, dlc: "4406040").Close().Close().Bytes));

    [Fact]
    public void ReadsOnlyTheRequestedAppRecords()
    {
        var records = SteamAppInfoNames.ReadApps(RivalsAppInfo(), new HashSet<uint> { 2767030, 4406010 });
        Assert.Equal([2767030u, 4406010u], records.Keys.Order());
        Assert.Equal("Marvel Rivals", records[2767030].Section("common").Value("name"));
        Assert.Equal("1", records[2767030].Section("depots").Value("hasdepotsindlc"));
        Assert.Empty(SteamAppInfoNames.ReadApps(new byte[8], new HashSet<uint> { 1 }));
    }

    [Fact]
    public void DlcWithFilesOnThisPcAreListedWithTheirSize()
    {
        var data = RivalsAppInfo();
        var app = SteamAppInfoNames.ReadApps(data, new HashSet<uint> { 2767030 })[2767030];
        var dlcIds = SteamGameProperties.DlcIds(app).ToHashSet();
        Assert.Equal([4406010u, 4406020u, 4406030u, 4406040u], dlcIds.Order());
        var records = SteamAppInfoNames.ReadApps(data, dlcIds);
        var manifest = SteamGameProperties.ReadManifest(MarvelRivalsAcf) with { InstalledDlc = new HashSet<uint> { 4406020 } };

        var content = SteamGameProperties.DlcContent(app, records, manifest, live: null);
        Assert.Equal(4, content.Count);
        var pack = content.Single(dlc => dlc.Id == 4406010);
        Assert.Equal("Marvel Rivals - High-Resolution Pack", pack.Name);
        Assert.Equal(22376494559, pack.SizeBytes);
        Assert.False(pack.Enabled);
        Assert.False(pack.Installed);
        Assert.True(pack.Toggle);
        var other = content.Single(dlc => dlc.Id == 4406020);
        Assert.True(other.Enabled);
        Assert.True(other.Toggle);
        Assert.Equal(5_000_000, other.SizeBytes);
        // Without Steam to ask, a DLC that is not on this PC is listed with no switch and no claim of ownership.
        Assert.All(content.Where(dlc => dlc.Id is 4406030 or 4406040), dlc => { Assert.False(dlc.Toggle); Assert.Null(dlc.Owned); });
        Assert.Equal([4406010u, 4406020u], content.Take(2).Select(dlc => dlc.Id).Order());

        // The live client wins over the manifest (a change Steam has not written yet).
        var live = new SteamLiveConfig(null, null, null, null, null, null, null, [], null, null,
            new Dictionary<uint, bool> { [4406010] = true });
        Assert.True(SteamGameProperties.DlcContent(app, records, manifest, live).Single(dlc => dlc.Id == 4406010).Enabled);
    }

    /// <summary>
    /// 007 First Light's shape (app 3768760, shared through Steam Family on this PC): 21 DLC
    /// listed, none with depots; Steam holds records for the 11 the family owns and answers
    /// BIsAppDlcInstalled true for exactly those. Exo used to list none of them.
    /// </summary>
    [Fact]
    public void DlcWithoutFilesAreListedWithWhetherTheAccountHoldsThem()
    {
        var data = AppInfo(
            (3768760, new Kv().Open("appinfo")
                .Open("common").Str("name", "007 First Light").Close()
                .Open("extended").Str("listofdlc", "3950810,3950870,3954230,3986520,4707780").Close()
                .Open("depots").Depot("3768761", 57291721105).Close()
                .Close().Bytes),
            (3950810, new Kv().Open("appinfo").Open("common").Str("name", "007 First Light - Gleaming Earphones (Gadget Skin)").Close().Close().Bytes),
            (3950870, new Kv().Open("appinfo").Open("common").Str("name", "007 First Light - Day of the Dead (Exclusive Outfit)").Close().Close().Bytes),
            (3986520, new Kv().Open("appinfo").Open("common").Str("name", "007 First Light - Deluxe Edition Upgrade").Close().Close().Bytes));
        var app = SteamAppInfoNames.ReadApps(data, new HashSet<uint> { 3768760 })[3768760];
        var records = SteamAppInfoNames.ReadApps(data, SteamGameProperties.DlcIds(app).ToHashSet());
        var manifest = SteamGameProperties.ReadManifest(MarvelRivalsAcf) with { InstalledDlc = new HashSet<uint>(), DisabledDlc = new HashSet<uint>() };
        var live = SteamGameProperties.ParseLive("""
            {"ok":true,"dlc":[{"id":3950810,"enabled":true,"installed":true},{"id":3950870,"enabled":true,"installed":true},{"id":3954230,"enabled":true,"installed":false},{"id":3986520,"enabled":true,"installed":false},{"id":4707780,"enabled":true,"installed":false}]}
            """);

        var content = SteamGameProperties.DlcContent(app, records, manifest, live);
        // Unreleased or hidden DLC (no record, no license) stay out; the account's own come first.
        Assert.Equal([3950870u, 3950810u, 3986520u], content.Select(dlc => dlc.Id));
        Assert.Equal([true, true, false], content.Select(dlc => dlc.Owned));
        Assert.All(content, dlc => Assert.False(dlc.Toggle));
        Assert.Equal("007 First Light - Deluxe Edition Upgrade", content[2].Name);
    }

    [Fact]
    public void BranchesHideACodedBranchUntilItIsCurrent()
    {
        var app = SteamAppInfoNames.ReadApps(RivalsAppInfo(), new HashSet<uint> { 2767030 })[2767030];
        Assert.Equal(["", "1.0"], SteamGameProperties.Branches(app, "").Select(branch => branch.Name));
        Assert.Equal("Version 1.0", SteamGameProperties.Branches(app, "").Last().Description);
        Assert.Equal(["", "1.0", "secret"], SteamGameProperties.Branches(app, "secret").Select(branch => branch.Name));
        Assert.Equal(["", "1.0", "gone"], SteamGameProperties.Branches(app, "gone").Select(branch => branch.Name));
    }

    [Fact]
    public void LanguagesHaveSteamCodesAndTheirOwnDepotBytes()
    {
        var app = SteamAppInfoNames.ReadApps(RivalsAppInfo(), new HashSet<uint> { 2767030 })[2767030];
        var languages = SteamGameProperties.Languages(app);
        Assert.Equal(["english", "german"], languages.Select(language => language.Name));
        Assert.Equal([0, 1], languages.Select(language => language.Code));
        Assert.Equal([0L, 2_000_000_000L], languages.Select(language => language.DepotBytes));
        Assert.Equal(1, SteamGameProperties.LanguageCode("German"));
        Assert.Equal(-1, SteamGameProperties.LanguageCode("klingon"));
        Assert.Equal("Portuguese (Brazil)", SteamGameProperties.LanguageName("brazilian"));
    }

    [Fact]
    public void ParsesTheHelpersAnswerAndRejectsAnythingElse()
    {
        const string answer = """
            Steam IPC starting.
            {"ok":true,"autoUpdate":0,"background":2,"language":0,"beta":"","cloudAccount":true,"cloud":false,"folder":0,"folders":["c:\\program files (x86)\\steam","D:\\SteamLibrary"],"moveDone":5,"moveTotal":10,"dlc":[{"id":4406010,"enabled":false,"installed":false}]}
            """;
        var live = SteamGameProperties.ParseLive(answer)!;
        Assert.Equal(2, live.Background);
        Assert.False(live.Cloud);
        Assert.Equal(2, live.Folders.Count);
        Assert.Equal(10, live.MoveTotal);
        Assert.False(live.DlcEnabled[4406010]);
        Assert.Null(SteamGameProperties.ParseLive("{\"ok\":false}"));
        Assert.Null(SteamGameProperties.ParseLive("{not json"));
        Assert.Null(SteamGameProperties.ParseLive(""));
    }

    private const string InstalledJson = """
        {
          "Fortnite": { "app_name": "Fortnite", "install_tags": ["", "chunk0", "chunk10"], "install_path": "C:\\Games\\Fortnite" },
          "Other": { "app_name": "Other" }
        }
        """;

    // The shape of Legendary's selective download data (its built-in Fortnite list).
    private const string FortniteSdl = """
        {
          "__required": { "tags": ["", "chunk0", "chunk10"] },
          "stw": { "name": "Fortnite Save the World", "tags": ["chunk11", "chunk11optional"] },
          "hd_textures": { "name": "High Resolution Textures", "tags": ["chunk10optional"] },
          "lang_de": { "name": "(Language Pack) Deutsch", "tags": ["chunk2"] },
          "broken": { "name": "no tags" }
        }
        """;

    [Fact]
    public void EpicComponentsReadTheirStateFromTheInstalledTags()
    {
        var installed = LegendaryProperties.InstalledTags(InstalledJson, "Fortnite")!;
        Assert.Equal(["", "chunk0", "chunk10"], installed);
        Assert.Null(LegendaryProperties.InstalledTags(InstalledJson, "Other"));
        Assert.Null(LegendaryProperties.InstalledTags("[]", "Fortnite"));

        var components = LegendaryProperties.ParseSdl(FortniteSdl);
        Assert.Equal(["__required", "stw", "hd_textures", "lang_de"], components.Select(component => component.Id));
        Assert.True(components[0].Required);
        Assert.False(LegendaryProperties.IsSelected(components.Single(c => c.Id == "hd_textures"), installed));
        Assert.True(LegendaryProperties.IsSelected(components[0], []));

        var withTextures = LegendaryProperties.TagsFor(components, new HashSet<string> { "hd_textures" });
        Assert.Equal(["", "chunk0", "chunk10", "chunk10optional"], withTextures);
        Assert.Equal(["", "chunk0", "chunk10"], LegendaryProperties.TagsFor(components, new HashSet<string>()));
        Assert.Empty(LegendaryProperties.ParseSdl("not json"));
    }

    [Fact]
    public void EpicTagSizesComeFromLegendaryInfo()
    {
        const string info = """
            {"game": {"app_name": "Fortnite"}, "manifest": {"tag_disk_size": [{"tag": "", "size": 100, "count": 3}, {"tag": "chunk10optional", "size": 25000, "count": 9}], "tag_download_size": []}}
            """;
        var sizes = LegendaryProperties.TagSizes(info);
        Assert.Equal(25000, sizes["chunk10optional"]);
        var textures = LegendaryProperties.ParseSdl(FortniteSdl).Single(c => c.Id == "hd_textures");
        Assert.Equal(25000, LegendaryProperties.ComponentBytes(textures, sizes));
        Assert.Null(LegendaryProperties.ComponentBytes(LegendaryProperties.ParseSdl(FortniteSdl).Single(c => c.Id == "stw"), sizes));
        Assert.Empty(LegendaryProperties.TagSizes("{}"));
    }

    private const string ConfigIni = """
        [Legendary]
        ; Disables the automatic update check
        disable_update_check = false

        [Fortnite]
        install_tags = ,chunk0
        wrapper = something

        [Other]
        start_params = -windowed
        """;

    [Fact]
    public void IniWriteChangesOneKeyAndKeepsEverythingElse()
    {
        var replaced = LegendaryProperties.SetIniValue(ConfigIni, "Fortnite", "install_tags", ",chunk0,chunk10optional");
        Assert.Equal(",chunk0,chunk10optional", LegendaryProperties.GetIniValue(replaced, "Fortnite", "install_tags"));
        Assert.Equal("something", LegendaryProperties.GetIniValue(replaced, "Fortnite", "wrapper"));
        Assert.Equal("-windowed", LegendaryProperties.GetIniValue(replaced, "Other", "start_params"));
        Assert.Contains("; Disables the automatic update check", replaced);
        Assert.Single(replaced.Split('\n'), line => line.Trim() == "[Fortnite]");

        var added = LegendaryProperties.SetIniValue(replaced, "Other", "install_tags", "");
        Assert.Equal("", LegendaryProperties.GetIniValue(added, "Other", "install_tags"));
        Assert.Equal("-windowed", LegendaryProperties.GetIniValue(added, "Other", "start_params"));

        var fresh = LegendaryProperties.SetIniValue(null, "Game", "install_tags", ",a");
        Assert.Equal("[Game]\r\ninstall_tags = ,a\r\n", fresh);
        Assert.Null(LegendaryProperties.GetIniValue(fresh, "Missing", "install_tags"));
    }

    [Fact]
    public void IniWriteOnACopyInATempFolder()
    {
        var folder = Path.Combine(TestDataDirectory.Root, "legendary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var config = Path.Combine(folder, "config.ini");
        File.WriteAllText(config, ConfigIni);
        File.WriteAllText(config, LegendaryProperties.SetIniValue(File.ReadAllText(config), "Fortnite", "install_tags", ",chunk0"));
        var text = File.ReadAllText(config);
        Assert.Equal(",chunk0", LegendaryProperties.GetIniValue(text, "Fortnite", "install_tags"));
        Assert.Equal("false", LegendaryProperties.GetIniValue(text, "Legendary", "disable_update_check"));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void NamesAndChangesThatReachPathsAreChecked()
    {
        Assert.True(LegendaryProperties.IsAppName("Fortnite"));
        Assert.True(LegendaryProperties.IsAppName("a019487eae0a4b2e"));
        Assert.False(LegendaryProperties.IsAppName("../user"));
        Assert.False(LegendaryProperties.IsAppName("a b"));
        Assert.False(LegendaryProperties.IsAppName(".hidden"));
        Assert.Equal(("4406010", true), WebHostBridge.ParseContentChange("4406010:on"));
        Assert.Equal(("hd_textures", false), WebHostBridge.ParseContentChange("hd_textures:off"));
        Assert.Null(WebHostBridge.ParseContentChange("4406010"));
        Assert.Null(WebHostBridge.ParseContentChange("../x:on"));
        Assert.Null(WebHostBridge.ParseContentChange(":on"));
    }

    [Fact]
    public void EpicListsOnlyGamesLegendaryKnowsHaveOptionalContent()
    {
        const string version = """{"data": {"game_overrides": {"sdl_config": {"Fortnite": 14, "Ginger": 1}}}, "last_update": 1}""";
        Assert.True(WebHostBridge.EpicListsSdl(version, "Fortnite"));
        Assert.False(WebHostBridge.EpicListsSdl(version, "Other"));
        Assert.False(WebHostBridge.EpicListsSdl("{}", "Fortnite"));
        Assert.True(WebHostBridge.EpicHasCloudSaves("""{"metadata": {"customAttributes": {"CloudSaveFolder": {"type": "STRING", "value": "{AppData}/Game"}}}}"""));
        Assert.False(WebHostBridge.EpicHasCloudSaves("""{"metadata": {"customAttributes": {}}}"""));
    }
}
