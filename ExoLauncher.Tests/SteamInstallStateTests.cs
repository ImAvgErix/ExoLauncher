using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways an installed Steam game can drop out of the library or lose its Update:
/// - an update Steam started, paused or could not finish (StateFlags 1026, 1538 clear FullyInstalled) reads as
///   not installed, so the game shows Install or disappears;
/// - a first install never finished (build 0), an uninstalled or uninstalling game reads as installed;
/// - Steam closed and no Web API key (ownership unverified) hides every update the manifests carry;
/// - a license known to be gone still shows Update.
/// </summary>
public sealed class SteamInstallStateTests
{
    [Theory]
    [InlineData("4", "123", true)]
    [InlineData("6", "123", true)]
    [InlineData("1026", "123", true)]
    [InlineData("1538", "123", true)]
    [InlineData("1026", "0", false)]
    [InlineData("1026", null, false)]
    [InlineData("1", "123", false)]
    [InlineData("2054", "123", false)]
    public void AGameMidUpdateIsStillInstalled(string flags, string? buildId, bool installed)
    {
        Assert.Equal(installed, SteamStateFlags.IsInstalledPresence(pathExists: true, flags, buildId));
        Assert.False(SteamStateFlags.IsInstalledPresence(pathExists: false, flags, buildId));
    }

    [Fact]
    public void AGameMidUpdateShowsUpdate() =>
        Assert.True(SteamStateFlags.IsUpdateAvailable("1026", SteamStateFlags.IsInstalledPresence(true, "1026", "123")));

    private static GameEntry Row(bool update) => new()
    {
        Id = "steam:10", Title = "Game", Store = StoreKind.Steam, Installed = true, UpdateAvailable = update,
        LaunchTarget = "10", SteamAppType = "game", Path = @"C:\Steam\steamapps\common\Game",
    };

    [Fact]
    public void UnverifiedOwnershipKeepsTheUpdate()
    {
        var row = Assert.Single(LibraryService.ReconcileSteamOwnership([Row(update: true)], authoritativeOwnedAppIds: null));
        Assert.True(row.UpdateAvailable);
        Assert.Equal(EntitlementState.Unverified, row.EntitlementState);
    }

    [Fact]
    public void ALicenseKnownGoneHidesTheUpdate()
    {
        var row = Assert.Single(LibraryService.ReconcileSteamOwnership([Row(update: true)], new HashSet<string>()));
        Assert.False(row.UpdateAvailable);
        Assert.Equal(EntitlementState.NotOwned, row.EntitlementState);
    }
}
