using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Battle.net's last played can come out wrong (Battle.net.config, Games map):
/// - a game is matched by the config's short key ("pro") instead of its ServerUid ("prometheus");
/// - a number written as a string, or a zero or future time, becomes a date;
/// - a broken or missing file throws;
/// - a game's install row does not pick the date up.
/// </summary>
public sealed class BattleNetLastPlayedTests
{
    private const string Config = """
        {
          "Games": {
            "pro": { "ServerUid": "prometheus", "LastPlayed": "1760000000" },
            "wow": { "LastPlayed": 1750000000 },
            "s2": { "ServerUid": "s2", "LastPlayed": "0" },
            "fen": { "ServerUid": "fenris", "LastPlayed": "99999999999" },
            "odin": { "ServerUid": "odin" }
          }
        }
        """;

    [Fact]
    public void ReadsByServerUidThenKey()
    {
        var times = OfficialInstalledLibraries.BattleNetLastPlayed(Config);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), times["prometheus"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750000000), times["wow"]);
        Assert.False(times.ContainsKey("pro"));
    }

    [Fact]
    public void SkipsUnsetFutureAndMissingTimes()
    {
        var times = OfficialInstalledLibraries.BattleNetLastPlayed(Config);
        Assert.False(times.ContainsKey("s2"));
        Assert.False(times.ContainsKey("fenris"));
        Assert.False(times.ContainsKey("odin"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"Games":[]}""")]
    public void BrokenConfigsGiveNothing(string? json) => Assert.Empty(OfficialInstalledLibraries.BattleNetLastPlayed(json));

    [Fact]
    public void InstallRowsTakeTheirDate()
    {
        var dir = Path.GetTempPath();
        var games = OfficialInstalledLibraries.ParseBattleNetInstalls(
            [new OfficialInstalledLibraries.BattleNetInstallRecord("prometheus", dir, "Overwatch 2")],
            _ => true,
            OfficialInstalledLibraries.BattleNetLastPlayed(Config));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), Assert.Single(games).LastPlayedUtc);
    }
}
