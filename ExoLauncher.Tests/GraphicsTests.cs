using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Profile Inspector import and the vendor pick could go wrong
/// silently:
/// - a real .nip (utf-16 declaration over UTF-8 with a BOM, decimal ids) is
///   refused, or a DTD / non-nip file is read as one;
/// - a text, binary or 64-bit setting is written as a Dword, or an id the
///   driver does not list is sent (the driver refuses it and the whole apply fails);
/// - a known id is shown unnamed or with the wrong value word;
/// - Undo does not put back exactly what was there (a value, or the default);
/// - a game's profile is imported to every game (the global profile), or a
///   file with several global profiles is guessed at;
/// - the wrong vendor is talked to;
/// - the DLSS override check misreads the driver's answer (a refused override logged as taken, a game
///   without DLSS logged at all), or its structure is the wrong size and the driver refuses every ask.
/// </summary>
public sealed class GraphicsTests
{
    // Shape of NVIDIA Profile Inspector's export (Common/Import, XmlSerializer).
    private const string Nip = """
        <?xml version="1.0" encoding="utf-16"?>
        <ArrayOfProfile>
          <Profile>
            <ProfileName>Sample Game</ProfileName>
            <Executeables>
              <string>samplegame.exe</string>
            </Executeables>
            <Settings>
              <ProfileSetting>
                <SettingNameInfo>Frame Rate Limiter V3</SettingNameInfo>
                <SettingID>277041154</SettingID>
                <SettingValue>141</SettingValue>
                <ValueType>Dword</ValueType>
              </ProfileSetting>
              <ProfileSetting>
                <SettingNameInfo>DLSS - Forced Preset Letter</SettingNameInfo>
                <SettingID>283385331</SettingID>
                <SettingValue>13</SettingValue>
                <ValueType>Dword</ValueType>
              </ProfileSetting>
              <ProfileSetting>
                <SettingNameInfo>Smooth Motion - Enable</SettingNameInfo>
                <SettingID>2966652096</SettingID>
                <SettingValue>1</SettingValue>
                <ValueType>Dword</ValueType>
              </ProfileSetting>
              <ProfileSetting>
                <SettingNameInfo></SettingNameInfo>
                <SettingID>305419896</SettingID>
                <SettingValue>7</SettingValue>
                <ValueType>Dword</ValueType>
              </ProfileSetting>
              <ProfileSetting>
                <SettingNameInfo>rBAR - Flags</SettingNameInfo>
                <SettingID>983231</SettingID>
                <SettingValue>example</SettingValue>
                <ValueType>String</ValueType>
              </ProfileSetting>
              <ProfileSetting>
                <SettingNameInfo>rBAR - Size Limit</SettingNameInfo>
                <SettingID>983295</SettingID>
                <SettingValue>1073741824</SettingValue>
                <ValueType>Qword</ValueType>
              </ProfileSetting>
            </Settings>
            <ExecutableFindFiles />
          </Profile>
        </ArrayOfProfile>
        """;

    private static readonly HashSet<uint> Driver = [GraphicsCatalog.FrameRateLimit, GraphicsCatalog.SrPreset, 305419896];

    [Fact]
    public void ReadsProfileInspectorExportAsSaved()
    {
        // Saved as UTF-8 with a BOM though the declaration says utf-16.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Nip)).ToArray();
        var text = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        var profiles = NvidiaProfileImport.Parse(text);
        var profile = Assert.Single(profiles!);
        Assert.Equal("Sample Game", profile.Name);
        Assert.Equal(["samplegame.exe"], profile.Executables);
        Assert.Equal(6, profile.Settings.Count);
        Assert.Equal(0xB0D384C0u, profile.Settings[2].Id);
        // A BOM left in the string reads too.
        Assert.NotNull(NvidiaProfileImport.Parse("\uFEFF" + Nip));
    }

    [Fact]
    public void PlanWritesOnlyDriverDwordsAndNamesThem()
    {
        var plan = NvidiaProfileImport.PlanFor(NvidiaProfileImport.Parse(Nip)![0], Driver);
        Assert.Equal([GraphicsCatalog.FrameRateLimit, GraphicsCatalog.SrPreset, 305419896u], plan.Writes.Select(change => change.Id).Order());
        var frame = plan.Writes.Single(change => change.Id == GraphicsCatalog.FrameRateLimit);
        Assert.Equal(("Max frame rate", "141 fps"), (frame.Name, frame.Words));
        Assert.Equal("Preset M", plan.Writes.Single(change => change.Id == GraphicsCatalog.SrPreset).Words);
        Assert.Equal(1, plan.Unknown);      // 0x12345678
        Assert.Equal(2, plan.LeftOut);      // String and Qword
        Assert.Equal(1, plan.NotInDriver);  // Smooth Motion on a driver that does not list it
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<?xml version=\"1.0\"?><Something/>")]
    [InlineData("<!DOCTYPE x [<!ENTITY a \"b\">]><ArrayOfProfile><Profile><ProfileName>&a;</ProfileName></Profile></ArrayOfProfile>")]
    public void RefusesWhatIsNotAnExport(string text) => Assert.Null(NvidiaProfileImport.Parse(text));

    [Fact]
    public void ImportsOnlyTheGlobalProfile()
    {
        static NvidiaProfileImport.Profile Of(string name, params string[] exes) => new(name, exes, []);
        var game = Of("Other", "other.exe");
        // A game's profile is never applied to every game.
        Assert.Null(NvidiaProfileImport.PickGlobal([game], out var reason));
        Assert.Contains("for a game", reason);
        Assert.Equal("Base", NvidiaProfileImport.PickGlobal([game, Of("Base")], out _)!.Name);
        Assert.Null(NvidiaProfileImport.PickGlobal([Of("A"), Of("B")], out reason));
        Assert.Contains("more than one", reason);
    }

    [Fact]
    public void UndoPutsBackValuesAndDefaults()
    {
        var before = new Dictionary<uint, uint?> { [1] = 60, [2] = null };
        var undo = NvidiaProfileImport.UndoWrites(before, [2, 1, 3, 1]);
        Assert.Equal([new(1, 60), new(2, null), new(3, null)], undo);
    }

    [Theory]
    [InlineData(0x00A879CFu, 0x08416747u, "Vertical sync", "Off")]
    [InlineData(0x1057EB71u, 1u, "Power management mode", "Prefer maximum performance")]
    [InlineData(GraphicsCatalog.FgPreset, 0x00FFFFFEu, "DLSS Frame Generation preset", "Recommended")]
    [InlineData(GraphicsCatalog.MfgCount, 3u, "Frame Generation multiplier", "4x")]
    [InlineData(GraphicsCatalog.SmoothMotion, 1u, "Smooth Motion", "On")]
    public void NamesKnownSettings(uint id, uint value, string name, string words)
    {
        Assert.Equal(name, GraphicsCatalog.NameOf(id));
        Assert.Equal(words, GraphicsCatalog.ValueOf(id, value));
    }

    [Fact]
    public void UnknownSettingHasNoName() => Assert.Null(GraphicsCatalog.NameOf(0x12345678));

    [Theory]
    [InlineData(new[] { "NVIDIA GeForce RTX 3070" }, new[] { "nvidia" })]
    [InlineData(new[] { "AMD Radeon RX 7900 XTX", "AMD Radeon(TM) Graphics" }, new[] { "amd" })]
    [InlineData(new[] { "Intel(R) Arc(TM) A770 Graphics" }, new[] { "intel" })]
    [InlineData(new[] { "Intel(R) UHD Graphics 770", "NVIDIA GeForce RTX 4090" }, new[] { "nvidia", "intel" })]
    [InlineData(new[] { "Microsoft Basic Display Adapter" }, new string[0])]
    public void FindsVendors(string[] adapters, string[] vendors) => Assert.Equal(vendors, GraphicsService.Vendors(adapters));

    [Fact]
    public void ShowsOnlyAVendorWhoseDriverAnswers()
    {
        Assert.Equal("nvidia", GraphicsService.ActiveVendor(["nvidia", "intel"], nvapi: true, adlx: false, igcl: true));
        Assert.Equal("intel", GraphicsService.ActiveVendor(["nvidia", "intel"], nvapi: false, adlx: false, igcl: true));
        Assert.Null(GraphicsService.ActiveVendor(["amd"], nvapi: false, adlx: false, igcl: false));
        Assert.Null(GraphicsService.ActiveVendor([], nvapi: true, adlx: true, igcl: true));
    }

    [Fact]
    public void TheDlssOverrideCheckSaysWhatTook()
    {
        const ulong created = 1 << 9, enabled = 1 << 1, selected = 1 << 4, denied = 1 << 17;
        var none = new NvidiaProfiles.NgxState(0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.False(NvidiaProfiles.RunsDlss(none));
        var taken = none with { Sr = created | enabled | selected, Preset = 11, Ratio = 0.5f, Mode = 0 };
        Assert.True(NvidiaProfiles.RunsDlss(taken));
        Assert.Equal("Super Resolution overridden; Preset K, mode 0, scale 0.5", NvidiaProfiles.Describe(taken));
        var refused = none with { Sr = created | denied, Fg = created, FgPreset = 3, FgCount = 1, FgMode = 2 };
        Assert.Equal("Super Resolution the game's own (override refused, 0x20200); Frame Generation the game's own; Driver default, mode 0, scale 0; frame generation Preset C, count 1, mode 2",
            NvidiaProfiles.Describe(refused));
    }

    // The driver answers for a process with no DLSS (this one): asked right (96-byte V2 structure), it has no data (not a refusal), read as nothing running.
    [Fact]
    public void TheDriverAnswersTheDlssOverrideAsk()
    {
        Assert.SkipUnless(NvidiaProfiles.Available, "no NVIDIA driver on this PC");
        var state = NvidiaProfiles.DlssState(Environment.ProcessId);
        Assert.NotNull(state);
        Assert.False(NvidiaProfiles.RunsDlss(state!.Value));
    }
}
