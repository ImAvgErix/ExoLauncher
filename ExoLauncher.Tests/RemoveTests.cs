using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a game's Remove could do nothing and say it worked, or stop with no way forward:
/// - Nile says "Game isn't installed" with exit code 0 for an Amazon Games app install, and Exo said Removed;
/// - Windows lists the game's uninstaller as "Battlefield™ 2042" where the store says "Battlefield 2042", so
///   Exo refused the game's own uninstaller;
/// - a looser title match lets a different product's uninstaller at the same folder, or the same game's at
///   another folder, remove something else;
/// - a GOG game from GOG's offline installer, with no Galaxy, can never be removed, or one GOG's registry never
///   listed is handed to an uninstaller and its first registry check reads as removed.
/// </summary>
public sealed class RemoveTests
{
    [Fact]
    public void NilesNotInstalledIsNotARemoval()
    {
        Assert.True(NileCli.NotInstalled("[UNINSTALL] ERROR: Game isn't installed"));
        Assert.False(NileCli.NotInstalled("[UNINSTALL] INFO: Game removed successfully"));
        Assert.False(NileCli.NotInstalled(null));
    }

    [Theory]
    [InlineData("Battlefield™ 2042", "Battlefield 2042", true)]
    [InlineData("Tom Clancy's Rainbow Six® Siege", "tom clancys rainbow six siege", true)]
    [InlineData("Halo", "Halo 2", false)]
    [InlineData("™", "®", false)]
    [InlineData(null, "Game", false)]
    public void UninstallTitlesMatchButForCaseSpacingAndMarks(string? windows, string store, bool same) =>
        Assert.Equal(same, OfficialInstalledLibraries.SameUninstallTitle(windows, store));

    [Fact]
    public void TheGamesOwnUninstallerIsFoundAtItsExactFolder()
    {
        var game = new GameEntry { Id = "ea:bf", Title = "Battlefield 2042", Store = StoreKind.Ea, Path = @"C:\Games\Battlefield 2042" };
        var own = new OfficialInstalledLibraries.ArpUninstallRecord("Battlefield™ 2042", @"C:\Games\Battlefield 2042\", @"""C:\Games\Battlefield 2042\uninstall.exe""");

        Assert.Same(own, OfficialInstalledLibraries.SelectArpUninstall(game, [own]));
        // The same game at another folder is not this install.
        Assert.Null(OfficialInstalledLibraries.SelectArpUninstall(game,
            [own with { Location = @"D:\Games\Battlefield 2042" }]));
        // Another product registered at the game's folder means the folder is shared: nothing is chosen.
        Assert.Null(OfficialInstalledLibraries.SelectArpUninstall(game,
            [own, new OfficialInstalledLibraries.ArpUninstallRecord("Battlefield 2042 Redist", @"C:\Games\Battlefield 2042", @"C:\Games\Battlefield 2042\redist.exe")]));
    }

    [Theory]
    [InlineData(true, true, true, "GalaxyCommand")]
    [InlineData(true, false, true, "Uninstaller")]
    [InlineData(true, true, false, "NotRemovable")]
    [InlineData(true, false, false, "NotRemovable")]
    [InlineData(false, false, true, "NotRemovable")]
    public void AGogGameGoesToGalaxyOrItsOwnUninstallerOnlyWhileGogsRegistryListsIt(
        bool hasProductId, bool galaxyPresent, bool inGogRegistry, string route) =>
        Assert.Equal(route, GogAdapter.SelectUninstallRoute(
            registeredRemovalSucceeded: false, hasProductId, galaxyPresent, inGogRegistry).ToString());
}
