using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways asking Windows for the fast graphics chip could harm or mislead:
/// - another setting kept in the same value (SwapEffectUpgradeEnable=1;) is lost when GpuPreference is written;
/// - Undo deletes a value that held something before, or leaves the one that held nothing;
/// - Undo overwrites a program the user set themselves after Exo, or one Exo never touched;
/// - a Riot game, an uninstaller, a crash reporter or an anti-cheat's program gets the setting;
/// - a program outside the game's folder (a launch path that wanders) gets it;
/// - the record is lost with the process, so Undo has nothing to go back to.
/// </summary>
public sealed class GpuPreferenceTests : IDisposable
{
    private sealed class FakeStore : GpuPreference.IStore
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string program) => Values.GetValueOrDefault(program);
        public void Set(string program, string data) => Values[program] = data;
        public void Delete(string program) => Values.Remove(program);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-gpupref-" + Guid.NewGuid().ToString("N"));
    private readonly FakeStore _store = new();
    private readonly GpuPreference _pref;

    public GpuPreferenceTests()
    {
        Directory.CreateDirectory(_dir);
        _pref = new GpuPreference(_store, Path.Combine(_dir, "gpu-preference.json"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Theory]
    [InlineData(null, "GpuPreference=2;")]
    [InlineData("", "GpuPreference=2;")]
    [InlineData("GpuPreference=1;", "GpuPreference=2;")]
    [InlineData("SwapEffectUpgradeEnable=1;", "GpuPreference=2;SwapEffectUpgradeEnable=1;")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=0;", "GpuPreference=2;SwapEffectUpgradeEnable=1;")]
    public void TheValueTakesTheChoiceAndKeepsTheRest(string? before, string after) => Assert.Equal(after, GpuPreference.With(before, 2));

    [Theory]
    [InlineData("GpuPreference=2;", 2)]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=1;", 1)]
    [InlineData("SwapEffectUpgradeEnable=1;", null)]
    [InlineData(null, null)]
    public void TheChoiceIsReadFromTheValue(string? data, int? choice) => Assert.Equal(choice, GpuPreference.Preference(data));

    [Fact]
    public void UndoPutsEachProgramBackAsItWas()
    {
        var plain = Touch(@"game\a.exe");
        var tuned = Touch(@"game\b.exe");
        _store.Values[tuned] = "SwapEffectUpgradeEnable=1;GpuPreference=1;";

        Assert.Equal(2, _pref.Use([plain, tuned]));
        Assert.Equal("GpuPreference=2;", _store.Values[plain]);
        Assert.Equal("GpuPreference=2;SwapEffectUpgradeEnable=1;", _store.Values[tuned]);

        Assert.Equal(2, _pref.Undo());
        Assert.False(_store.Values.ContainsKey(plain));                                               // it had nothing: nothing now
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=1;", _store.Values[tuned]);               // it had a value: that value
    }

    [Fact]
    public void AProgramAlreadyOnTheFastChipIsNeitherChangedNorTakenOnByUndo()
    {
        var mine = Touch(@"game\a.exe");
        _store.Values[mine] = "GpuPreference=2;";
        Assert.Equal(0, _pref.Use([mine]));
        Assert.Equal(0, _pref.Undo());
        Assert.Equal("GpuPreference=2;", _store.Values[mine]);
    }

    [Fact]
    public void AProgramTheUserChangedAfterExoIsLeftAsTheyHaveIt()
    {
        var program = Touch(@"game\a.exe");
        _pref.Use([program]);
        _store.Values[program] = "GpuPreference=1;";
        Assert.Equal(0, _pref.Undo());
        Assert.Equal("GpuPreference=1;", _store.Values[program]);
    }

    [Fact]
    public void TheRecordSurvivesANewInstance()
    {
        var program = Touch(@"game\a.exe");
        _store.Values[program] = "GpuPreference=0;";
        _pref.Use([program]);
        var later = new GpuPreference(_store, Path.Combine(_dir, "gpu-preference.json"));
        Assert.Equal(1, later.Undo());
        Assert.Equal("GpuPreference=0;", _store.Values[program]);
    }

    [Fact]
    public void OnlyTheGamesOwnProgramsAreChosen()
    {
        var game = new GameEntry { Id = "steam:1", Title = "A Game", Store = StoreKind.Steam, Installed = true, Path = Path.Combine(_dir, "game"), LaunchTarget = "1" };
        var main = Touch(@"game\Game.exe");
        var shipping = Touch(@"game\Binaries\Win64\Game-Win64-Shipping.exe");
        Touch(@"game\unins000.exe");
        Touch(@"game\CrashReportClient.exe");
        Touch(@"game\vcredist_x64.exe");
        Touch(@"game\EasyAntiCheat\EasyAntiCheat_Setup.exe");
        Touch(@"game\easyanticheat_eos_setup.exe");
        Touch(@"game\start_protected_game.exe");
        Touch(@"game\Data\deep\Other.exe");   // not in a usual place

        var chosen = GpuPreference.ProgramsOf(game);
        Assert.Equal([main, shipping], chosen.OrderBy(path => path.Length));
    }

    [Fact]
    public void ARiotGameIsNeverTouched()
    {
        var game = new GameEntry { Id = "riot:valorant", Title = "VALORANT", Store = StoreKind.Riot, Installed = true, Path = Path.Combine(_dir, "riot") };
        Touch(@"riot\VALORANT.exe");
        Assert.Empty(GpuPreference.ProgramsOf(game));
    }

    [Fact]
    public void ALaunchPathOutsideTheGamesFolderIsIgnored()
    {
        var outside = Touch(@"elsewhere\Evil.exe");
        var game = new GameEntry { Id = "local:x", Title = "X", Store = StoreKind.Local, Installed = true, Path = Path.Combine(_dir, "game"), LaunchTarget = outside };
        Directory.CreateDirectory(game.Path!);
        Assert.Empty(GpuPreference.ProgramsOf(game));
    }
}
