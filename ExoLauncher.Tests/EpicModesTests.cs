using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Fortnite's modes can show up wrong (the new Epic Games Launcher lists
/// LEGO Fortnite Odyssey and Save the World as installations under hex ids):
/// - each mode gets its own card, titled with its hex id;
/// - the mode with the longer title, or a hex id, is kept as the game;
/// - two different games are folded together;
/// - an installation without a namespace is dropped;
/// - the modes' hours are lost from the game's card.
/// </summary>
public sealed class EpicModesTests
{
    private static EpicLauncherV2.Installation Install(string app, string? ns) => new(app, ns, null, @"C:\Games\" + app, null, true, null);

    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fortnite"] = "Fortnite",
        ["94bc5ec13f8f438c97fdbef3e9019e27"] = "LEGO Fortnite Odyssey",
        ["Sugar"] = "Rocket League",
    };

    [Fact]
    public void FortniteModesFoldIntoFortnite()
    {
        var installs = new[]
        {
            Install("94bc5ec13f8f438c97fdbef3e9019e27", "fn"),
            Install("Fortnite", "fn"),
            Install("aa31f9e94e844b299ca757d1d0b97a09", "fn"),
            Install("Sugar", "9773aa1aa54f4f7b80e44bef04986cea"),
        };
        var (kept, modes) = EpicLauncherV2.FoldModes(installs, i => Titles.GetValueOrDefault(i.AppName));
        Assert.Equal(["Fortnite", "Sugar"], kept.Select(i => i.AppName).Order());
        Assert.Equal(["94bc5ec13f8f438c97fdbef3e9019e27", "aa31f9e94e844b299ca757d1d0b97a09"], modes["Fortnite"].Order());
        Assert.False(modes.ContainsKey("Sugar"));
    }

    [Fact]
    public void AllHexNamespaceKeepsTheShortestTitle()
    {
        var titles = new Dictionary<string, string> { ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"] = "LEGO Fortnite Odyssey", ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = "Fortnite" };
        var (kept, _) = EpicLauncherV2.FoldModes(
            [Install("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "fn"), Install("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "fn")],
            i => titles.GetValueOrDefault(i.AppName));
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Assert.Single(kept).AppName);
    }

    [Fact]
    public void NoNamespaceKeepsEachInstall()
    {
        var (kept, modes) = EpicLauncherV2.FoldModes([Install("A", null), Install("B", null), Install("C", "")], _ => null);
        Assert.Equal(3, kept.Count);
        Assert.Empty(modes);
    }

    [Theory]
    [InlineData("94bc5ec13f8f438c97fdbef3e9019e27", true)]
    [InlineData("AA31F9E94E844B299CA757D1D0B97A09", true)]
    [InlineData("Fortnite", false)]
    [InlineData("94bc5ec13f8f438c97fdbef3e9019e2", false)]
    [InlineData("94bc5ec13f8f438c97fdbef3e9019e2g", false)]
    public void HexIds(string value, bool hex) => Assert.Equal(hex, EpicLauncherV2.IsHexId(value));

    [Fact]
    public void ModeHoursCountForTheGame()
    {
        EpicPlaytime.RegisterModes("FortniteTest", ["mode-a-test", "mode-b-test"]);
        var game = new GameEntry { Id = "epic:FortniteTest", Title = "Fortnite Test", Store = StoreKind.Epic, LaunchTarget = "FortniteTest" };
        var minutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["FortniteTest"] = 300, ["mode-a-test"] = 40, ["mode-b-test"] = 2 };
        Assert.Equal(342, EpicPlaytime.MinutesFor(game, minutes));
    }
}
