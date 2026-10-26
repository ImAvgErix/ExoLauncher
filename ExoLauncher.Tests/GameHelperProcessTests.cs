using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// A game shows Playing only for the game itself. Ways it could fail: a helper its store runs from inside its folder
/// without the game (Call of Duty's install cleaner and content downloader when a DLC is removed, an installer in
/// _CommonRedist) reads as the game playing; or the game's own exe is taken for a helper and never shows Playing.
/// </summary>
public sealed class GameHelperProcessTests
{
    [Theory]
    [InlineData("codinstallcleaner")]
    [InlineData("ccsdownloader")]
    [InlineData("CODBrokerService")]
    [InlineData("CODBrokerInstaller")]
    [InlineData("bootstrapper_installer")]
    [InlineData("CODSecureAttestationWizard")]
    [InlineData("CrashLogsGenerator")]
    [InlineData("VC_redist.x64")]
    [InlineData("UnityCrashHandler64")]
    public void HelpersAreNotTheGame(string name) => Assert.True(ProcessHelper.IsNonGameProcessName(name));

    [Theory]
    [InlineData("cod")]
    [InlineData("bootstrapper")]
    [InlineData("eldenring")]
    [InlineData("Cyberpunk2077")]
    [InlineData("RocketLeague")]
    public void GamesAreGames(string name) => Assert.False(ProcessHelper.IsNonGameProcessName(name));

    [Fact]
    public void RuntimesInstalledBesideTheGameAreHelpers()
    {
        const string root = @"C:\Program Files (x86)\Steam\steamapps\common\Some Game";
        Assert.True(ProcessHelper.IsInHelperFolder(root + @"\_CommonRedist\vcredist\2022\VC_redist.x64.exe", root));
        Assert.True(ProcessHelper.IsInHelperFolder(root + @"\__Installer\installer.exe", root));
        Assert.False(ProcessHelper.IsInHelperFolder(root + @"\Binaries\Win64\Game.exe", root));
        Assert.False(ProcessHelper.IsInHelperFolder(root + @"\Game.exe", root));
    }
}
