using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the title search for games Steam does not sell can pick the wrong cover:
/// - a near name (McOsu for osu!) is taken;
/// - an unverified duplicate beats SteamGridDB's verified entry;
/// - case, marks or punctuation ("OSU", "osu!") make the exact name miss;
/// - an empty or broken answer throws.
/// </summary>
public sealed class SteamGridDbArtTests
{
    private const string Search = """
        {"success":true,"data":[
          {"id":11,"name":"McOsu","verified":true},
          {"id":12,"name":"osu!","verified":false},
          {"id":13,"name":"osu!","verified":true},
          {"id":14,"name":"osu!mania tools","verified":true}]}
        """;

    [Theory]
    [InlineData("osu!")]
    [InlineData("OSU")]
    [InlineData(" osu! ")]
    public void ExactTitleTakesTheVerifiedEntry(string title) =>
        Assert.Equal(13, SteamGridDbArt.ParseExactTitleGameId(Search, title));

    [Theory]
    [InlineData("osu mania")]
    [InlineData("Mc")]
    [InlineData("")]
    public void NearNamesAreNeverTaken(string title) =>
        Assert.Equal(0, SteamGridDbArt.ParseExactTitleGameId(Search, title));

    [Fact]
    public void UnverifiedExactNameStillCounts() =>
        Assert.Equal(21, SteamGridDbArt.ParseExactTitleGameId("""{"data":[{"id":21,"name":"Some Local Game"}]}""", "some local game"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"success":false}""")]
    public void BrokenAnswersFindNothing(string? json) =>
        Assert.Equal(0, SteamGridDbArt.ParseExactTitleGameId(json, "osu!"));
}
