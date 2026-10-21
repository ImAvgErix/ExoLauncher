namespace ExoLauncher.Tests;

/// <summary>
/// Ways the anti-cheat hard stop can fail:
/// - a known anti-cheat name, or a game's _BE/_EAC launcher, is not recognised;
/// - the match depends on case or a trailing ".exe";
/// - an ordinary game or launcher is mistaken for anti-cheat and can never be closed;
/// - a tree kill checks only the root and ends an anti-cheat child;
/// - an anti-cheat root still lets its tree be ended;
/// - the root is ended before its children (they re-parent and survive).
/// </summary>
public sealed class ProcessTerminationTests
{
    [Theory]
    [InlineData("vgk")] [InlineData("vgc")] [InlineData("vgtray")]
    [InlineData("EasyAntiCheat")] [InlineData("EasyAntiCheat_EOS")] [InlineData("start_protected_game")]
    [InlineData("EAAntiCheat.GameService")] [InlineData("EAAntiCheat.GameServiceLauncher")] [InlineData("EAAntiCheat.Installer")]
    [InlineData("BEService")] [InlineData("BEService_x64")] [InlineData("BattlEye_Launcher")]
    [InlineData("faceit")] [InlineData("FACEITService")] [InlineData("faceitclient")]
    [InlineData("PnkBstrA")] [InlineData("PnkBstrB")]
    [InlineData("xigncode3")] [InlineData("xhunter1")]
    [InlineData("GameMon64")] [InlineData("GameGuard.des")] [InlineData("SGuard64")]
    [InlineData("FortniteClient-Win64-Shipping_BE")] [InlineData("FortniteClient-Win64-Shipping_EAC")]
    [InlineData("FortniteClient-Win64-Shipping_EAC_EOS")] [InlineData("DayZ_BE")]
    [InlineData("vgk.exe")] [InlineData("VGTRAY.EXE")] [InlineData(" vgc ")]
    public void RecognisesAntiCheat(string name) => Assert.True(ProcessTermination.IsAntiCheat(name));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("  ")]
    [InlineData("FortniteClient-Win64-Shipping")] [InlineData("VALORANT-Win64-Shipping")]
    [InlineData("cs2")] [InlineData("RiotClientServices")] [InlineData("steam")] [InlineData("Discord")]
    [InlineData("Tribe")] [InlineData("Maybe")] [InlineData("eac")] [InlineData("Theseac")]    public void LeavesOrdinaryProcessesAlone(string? name) => Assert.False(ProcessTermination.IsAntiCheat(name));

    [Fact]
    public void TreeSkipsAntiCheatChildrenAndEndsTheRootLast()
    {
        var (kill, refused) = ProcessTermination.PlanTree((1, "Game_Launcher"),
            [(4, "CrashHandler"), (3, "EasyAntiCheat"), (2, "Game-Win64-Shipping_BE")]);
        Assert.Equal([4, 1], kill);
        Assert.Equal([3, 2], refused);
    }

    [Fact]
    public void AntiCheatRootRefusesTheWholeTree()
    {
        var (kill, refused) = ProcessTermination.PlanTree((1, "start_protected_game"), [(3, "Game"), (2, "helper")]);
        Assert.Empty(kill);
        Assert.Equal([1, 3, 2], refused);
    }

    [Fact]
    public void PlainTreeEndsEveryChildBeforeTheRoot()
    {
        var (kill, refused) = ProcessTermination.PlanTree((1, "setup"), [(3, "child2"), (2, null)]);
        Assert.Equal([3, 2, 1], kill);
        Assert.Empty(refused);
    }
}
