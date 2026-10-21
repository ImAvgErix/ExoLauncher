using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Steam overlay switch could be written wrong (the fixture follows this PC's
/// localconfig.vdf: the switch under UserLocalConfigStore › apps, tabs, LF):
/// - the key lands in the wrong section (Software › Valve › Steam › apps, which Steam ignores,
///   a same-named key under another game or deeper), or a second copy is added instead of the
///   value being replaced;
/// - a game with no section, or a file with no apps section, is skipped or breaks the braces;
/// - anything else in the file changes (other games, other keys, layout);
/// - a value other than an app id reaches the file;
/// - the plan writes a game that is already right, fights a switch changed in Steam itself,
///   forgets a write Steam has not taken yet, or a choice in Exo loses to the default.
/// </summary>
public sealed class SteamOverlayConfigTests
{
    private const string LocalConfig =
        "\"UserLocalConfigStore\"\n{\n\t\"system\"\n\t{\n\t\t\"InGameOverlayShowFPSCorner\"\t\t\"1\"\n\t}\n" +
        // Where play time lives: a switch here is not Steam's (1593500 is still on).
        "\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
        "\t\t\t\t\t\"1593500\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n" +
        "\t\"apps\"\n\t{\n" +
        "\t\t\"730\"\n\t\t{\n\t\t\t\"cloud\"\n\t\t\t{\n\t\t\t\t\"OverlayAppEnable\"\t\t\"1\"\n\t\t\t}\n\t\t}\n" +
        "\t\t\"3768760\"\n\t\t{\n\t\t\t\"OverlayAppEnable\"\t\t\"1\"\n\t\t\t\"Playtime\"\t\t\"12\"\n\t\t}\n" +
        "\t}\n\t\"LastPlayedTimesSyncTime\"\t\t\"1\"\n}\n";

    [Fact]
    public void ReplacesAnExistingSwitchInPlace()
    {
        var written = SteamOverlayConfig.Write(LocalConfig, new Dictionary<string, bool> { ["3768760"] = false });
        Assert.Equal(LocalConfig.Replace("\"OverlayAppEnable\"\t\t\"1\"\n\t\t\t\"Playtime\"", "\"OverlayAppEnable\"\t\t\"0\"\n\t\t\t\"Playtime\""), written);
        Assert.False(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(written), "3768760"));
    }

    [Fact]
    public void ReadsOnlyTheSwitchSteamReads()
    {
        // 1593500 is off only under Software › Valve › Steam › apps, which Steam ignores: it still gets the overlay.
        Assert.Null(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(LocalConfig), "1593500"));
    }

    [Fact]
    public void AddsTheKeyToAGameWhoseOnlySameNamedKeyIsDeeper()
    {
        // 730's "OverlayAppEnable" sits under "cloud": not the game's own switch.
        Assert.Null(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(LocalConfig), "730"));
        var written = SteamOverlayConfig.Write(LocalConfig, new Dictionary<string, bool> { ["730"] = false });
        Assert.Contains("\t\t\t}\n\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n\t\t}\n\t\t\"3768760\"", written);
        Assert.False(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(written), "730"));
        Assert.True(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(written), "3768760"));
        Assert.Equal(LocalConfig, written.Replace("\t\t\t}\n\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n", "\t\t\t}\n"));
    }

    [Fact]
    public void AddsAGameAndMissingSectionsInSteamsLayout()
    {
        var written = SteamOverlayConfig.Write(LocalConfig, new Dictionary<string, bool> { ["1593500"] = false });
        Assert.Contains("\t\t\"1593500\"\n\t\t{\n\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n\t\t}\n\t}\n\t\"LastPlayedTimesSyncTime\"", written);
        Assert.False(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(written), "1593500"));
        // The ignored copy under Software stays as it was.
        Assert.Contains("\t\t\t\t\t\"1593500\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}", written);

        const string bare = "\"UserLocalConfigStore\"\n{\n\t\"system\"\n\t{\n\t}\n}\n";
        var built = SteamOverlayConfig.Write(bare, new Dictionary<string, bool> { ["10"] = false, ["20"] = true });
        var apps = SteamOverlayConfig.Apps(built);
        Assert.False(SteamOverlayConfig.Read(apps, "10"));
        Assert.True(SteamOverlayConfig.Read(apps, "20"));
        Assert.Equal(built.Count(c => c == '{'), built.Count(c => c == '}'));
        Assert.StartsWith("\"UserLocalConfigStore\"\n{\n\t\"system\"\n\t{\n\t}\n\t\"apps\"\n\t{\n", built);
        // Written twice, still one key.
        Assert.Equal(built, SteamOverlayConfig.Write(built, new Dictionary<string, bool> { ["10"] = false }));
    }

    [Fact]
    public void AcceptsAPendingEulaWhereSteamKeepsItAndOnlyOnce()
    {
        var eulas = new Dictionary<string, IReadOnlyList<(string Id, int Version)>>
        {
            ["1593500"] = [("1593500_eula_0", 4)],
            ["3768760"] = [("3768760_eula_1", 1)],
        };
        var pending = SteamOverlayConfig.PendingEulas(LocalConfig, eulas);
        Assert.Equal(["1593500", "3768760"], pending.Keys.Order());
        var written = SteamOverlayConfig.AcceptEulas(LocalConfig, pending);
        // Beside the game's play time under Software › Valve › Steam › apps, as Steam's Accept writes it.
        Assert.Contains("\t\t\t\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n\t\t\t\t\t\t\"1593500_eula_0\"\t\t\"4\"\n", written);
        Assert.Empty(SteamOverlayConfig.PendingEulas(written, eulas));
        Assert.Equal(written, SteamOverlayConfig.AcceptEulas(written, SteamOverlayConfig.PendingEulas(written, eulas)));
        // A newer version than the one accepted asks again.
        var newer = new Dictionary<string, IReadOnlyList<(string Id, int Version)>> { ["1593500"] = [("1593500_eula_0", 5)] };
        Assert.Single(SteamOverlayConfig.PendingEulas(written, newer));
        Assert.Throws<ArgumentException>(() => SteamOverlayConfig.AcceptEulas(LocalConfig, new Dictionary<string, List<(string, int)>> { ["1"] = [("x\"\n}", 1)] }));
    }

    [Fact]
    public void RefusesAnythingButAnAppId()
    {
        Assert.Throws<ArgumentException>(() => SteamOverlayConfig.Write(LocalConfig, new Dictionary<string, bool> { ["730\"\n}"] = false }));
        Assert.Throws<ArgumentException>(() => SteamOverlayConfig.Write(LocalConfig, new Dictionary<string, bool> { [""] = false }));
    }

    [Fact]
    public void PlanWritesOnlyWhatDiffersAndNeverFightsSteam()
    {
        var apps = SteamOverlayConfig.Apps(LocalConfig);
        // Default off: 3768760 (on in Steam) and 1593500 (no key Steam reads, so on) are written off; nothing else.
        var (writes, record) = SteamOverlayConfig.Plan(apps, ["3768760", "1593500"], new Dictionary<string, string>(), defaultOn: false);
        Assert.Equal(["1593500", "3768760"], writes.Keys.Order());
        Assert.All(writes.Values, Assert.False);
        Assert.Equal("off?", record["3768760"]);

        // Default on: a game without a key is already on; nothing to write.
        Assert.Empty(SteamOverlayConfig.Plan(apps, ["1593500"], new Dictionary<string, string>(), defaultOn: true).Writes);

        // Exo set it off and Steam has it on again: changed in Steam, so Steam's value stays.
        var adopted = SteamOverlayConfig.Plan(apps, ["3768760"], new Dictionary<string, string> { ["3768760"] = "off" }, defaultOn: false);
        Assert.Empty(adopted.Writes);
        Assert.Equal("on", adopted.Record["3768760"]);

        // A write Steam has not taken yet ("?") is written again, not adopted back.
        var pending = SteamOverlayConfig.Plan(apps, ["3768760"], new Dictionary<string, string> { ["3768760"] = "off?" }, defaultOn: true);
        Assert.False(pending.Writes["3768760"]);

        // A choice in Properties wins over the default and the record.
        var chosen = SteamOverlayConfig.Plan(apps, [], new Dictionary<string, string> { ["730"] = "on" }, defaultOn: false,
            new Dictionary<string, bool> { ["730"] = false });
        Assert.False(chosen.Writes["730"]);
        Assert.Equal("off?", chosen.Record["730"]);
    }
}
