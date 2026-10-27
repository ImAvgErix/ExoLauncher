using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo could get Steam Input wrong for a player:
/// - turning it off for a game that reads only Xbox controllers (the DualSense would then do nothing there);
/// - losing the value it replaced, so turning the switch off doesn't put Steam back as it was;
/// - writing over a change the player made in Steam after Exo set it;
/// - a pick made in Exo losing to that rule when it is the newer one.
/// </summary>
public sealed class SteamInputConfigTests
{
    private const string Native = "1593500";   // God of War: PlayStation controller support
    private const string XboxOnly = "1343240"; // Thymesia: none
    private const string OptedIn = "730";      // a game set to "on" in Steam before Exo

    private static SteamKeyValues Apps(params (string App, string Value)[] values) =>
        SteamKeyValues.ParseText("\"UserLocalConfigStore\"\n{\n\"apps\"\n{\n"
            + string.Concat(values.Select(v => $"\"{v.App}\"\n{{\n\"UseSteamControllerConfig\"\t\t\"{v.Value}\"\n}}\n")) + "}\n}\n")
        .Section("UserLocalConfigStore/apps");

    private static readonly Dictionary<string, string> None = [];

    [Fact]
    public void OnTurnsItOffOnlyWhereTheGameReadsTheController()
    {
        var (writes, record, _) = SteamInputConfig.Plan(Apps((OptedIn, "2")), [Native, OptedIn], on: true, None, None);
        Assert.Equal("0", writes[Native]);
        Assert.Equal("0", writes[OptedIn]);
        Assert.Equal("0", writes[SteamInputConfig.DesktopLayout]);
        Assert.False(writes.ContainsKey(XboxOnly));
        Assert.Equal(">0", record[Native]);       // there was no line
        Assert.Equal("2>0", record[OptedIn]);
    }

    [Fact]
    public void OffPutsBackExactlyWhatWasThere()
    {
        var apps = Apps((Native, "0"), (OptedIn, "0"), (SteamInputConfig.DesktopLayout, "0"));
        var record = new Dictionary<string, string> { [Native] = ">0", [OptedIn] = "2>0", [SteamInputConfig.DesktopLayout] = "1>0" };
        var (writes, next, _) = SteamInputConfig.Plan(apps, [Native, OptedIn], on: false, None, record);
        // God of War had no line: it is taken out again, not set to "1".
        Assert.Equal(new Dictionary<string, string> { [Native] = "", [OptedIn] = "2", [SteamInputConfig.DesktopLayout] = "1" }, writes);
        Assert.Empty(next);
    }

    [Fact]
    public void AChangeMadeInSteamStaysSteams()
    {
        // Exo set God of War native; the player then turned Steam Input on for it in Steam.
        var apps = Apps((Native, "2"));
        var (writes, record, choices) = SteamInputConfig.Plan(apps, [Native], on: true, new Dictionary<string, string> { [Native] = "native" },
            new Dictionary<string, string> { [Native] = "1>0" });
        Assert.False(writes.ContainsKey(Native));
        Assert.False(record.ContainsKey(Native));
        Assert.False(choices.ContainsKey(Native));
    }

    [Fact]
    public void APickJustMadeInExoWins()
    {
        var apps = Apps((Native, "2"));
        var (writes, record, _) = SteamInputConfig.Plan(apps, [Native], on: true, new Dictionary<string, string> { [Native] = "native" },
            new Dictionary<string, string> { [Native] = ">0" }, fresh: new HashSet<string> { Native });
        Assert.Equal("0", writes[Native]);
        Assert.Equal(">0", record[Native]);
    }

    [Fact]
    public void AnXboxOnlyGameCanBeGivenSteamInputByHand()
    {
        var (writes, record, _) = SteamInputConfig.Plan(Apps(), [], on: false, new Dictionary<string, string> { [XboxOnly] = "steam" }, None);
        Assert.Equal("2", writes[XboxOnly]);
        Assert.Equal(">2", record[XboxOnly]);
    }

    [Fact]
    public void TheFileTakesOnlyRealValues()
    {
        var text = "\"UserLocalConfigStore\"\n{\n\t\"apps\"\n\t{\n\t}\n}\n";
        var written = SteamInputConfig.Write(text, new Dictionary<string, string> { [Native] = "0" });
        Assert.Equal("0", SteamInputConfig.Read(SteamKeyValues.ParseText(written).Section("UserLocalConfigStore/apps"), Native));
        // Taken out again: the key's line goes, the section stays as Steam would leave it.
        var back = SteamInputConfig.Write(written, new Dictionary<string, string> { [Native] = "" });
        Assert.Null(SteamInputConfig.Read(SteamKeyValues.ParseText(back).Section("UserLocalConfigStore/apps"), Native));
        Assert.DoesNotContain("UseSteamControllerConfig", back);
        Assert.Throws<ArgumentException>(() => SteamInputConfig.Write(text, new Dictionary<string, string> { [Native] = "3" }));
        Assert.Throws<ArgumentException>(() => SteamInputConfig.Write(text, new Dictionary<string, string> { ["../x"] = "0" }));
    }
}
