using ExoLauncher.Ui;

namespace ExoLauncher.Tests;

/// <summary>
/// What a page or link may open outside Exo: the web, Steam's own harmless forms and two Microsoft Store pages. A steam
/// link that runs a game with launch arguments, or anything else, stays shut.
/// </summary>
public sealed class ExoUrlLaunchTests
{
    [Theory]
    [InlineData("https://store.steampowered.com/app/730")]
    [InlineData("steam://store/730")]
    [InlineData("steam://nav/games/details/730")]
    [InlineData("steam://open/downloads")]
    [InlineData("steam://openurl/https://store.steampowered.com/account/registerkey?key=AAAAA")]
    [InlineData("steam://install/730")]
    [InlineData("steam://rungameid/730")]
    [InlineData("ms-windows-store://pdp/?productid=9NBLGGH4R32N")]
    public void OpensTheseAddresses(string url) => Assert.True(ExoUrlLaunch.IsAllowed(new Uri(url)));

    [Theory]
    [InlineData("steam://run/730//+exec%20evil.cfg")]
    [InlineData("steam://rungameid/730//-console")]
    [InlineData("steam://openurl/file:///C:/Windows/System32/cmd.exe")]
    [InlineData("steam://exit")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("ms-windows-store://review/?productid=1")]
    public void RefusesTheseAddresses(string url) => Assert.False(ExoUrlLaunch.IsAllowed(new Uri(url)));
}
