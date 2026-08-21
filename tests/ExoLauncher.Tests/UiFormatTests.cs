using ExoLauncher.Models;
using ExoLauncher.Ui;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class UiFormatTests
{
    [Fact]
    public void BuyUrl_IsNullForOwnedOrInstalledTitles()
    {
        Assert.Null(UiFormat.BuyUrl(Steam("730", installed: true)));
        Assert.Null(UiFormat.BuyUrl(Steam("730", owned: true)));
        Assert.Null(UiFormat.BuyUrl(Steam("730", owned: true, canInstall: true)));
        Assert.Equal("steam://store/730", UiFormat.BuyUrl(Steam("730")));
        Assert.Equal("https://playvalorant.com/", UiFormat.BuyUrl(new GameEntry
        {
            Id = "riot:valorant",
            Title = "VALORANT",
            Store = StoreKind.Riot,
            Owned = false,
            LaunchTarget = "valorant",
        }));
    }

    [Fact]
    public void ResolvePrimaryAction_UpdateAndOwnedInstallBeatStalePlay()
    {
        Assert.Equal("update", UiFormat.ResolvePrimaryAction(Steam("1", installed: true, update: true)));
        Assert.Equal("install", UiFormat.ResolvePrimaryAction(Steam("1", owned: true)));
        Assert.Equal("play", UiFormat.ResolvePrimaryAction(Steam("1", installed: true)));
        Assert.Equal("none", UiFormat.ResolvePrimaryAction(Steam("1")));
    }

    [Fact]
    public void ContradictoryUnownedInstallClaimFallsBackToBuy()
    {
        var refunded = Steam("730", canInstall: true);

        Assert.Equal("steam://store/730", UiFormat.BuyUrl(refunded));
        Assert.Equal("none", UiFormat.ResolvePrimaryAction(refunded));
    }

    [Fact]
    public void ExplicitlyRevokedInstalledTitle_CannotPlayAndOffersBuyAgain()
    {
        var refunded = Steam(
            "730",
            installed: true,
            owned: false,
            entitlementState: EntitlementState.NotOwned);

        Assert.Equal("none", refunded.PrimaryAction);
        Assert.Equal("none", UiFormat.ResolvePrimaryAction(refunded));
        Assert.Equal("steam://store/730", UiFormat.BuyUrl(refunded));
        Assert.Equal("Buy again", UiFormat.PrimaryLabel(refunded, transferring: false, running: false));
    }

    [Fact]
    public void UnverifiedInstalledTitle_CannotPlayAndDoesNotPretendItWasRevoked()
    {
        var unavailable = Steam(
            "730",
            installed: true,
            owned: false,
            entitlementState: EntitlementState.Unverified);

        Assert.Equal("none", unavailable.PrimaryAction);
        Assert.Equal("none", UiFormat.ResolvePrimaryAction(unavailable));
        Assert.Null(UiFormat.BuyUrl(unavailable));
        Assert.Equal("Unavailable", UiFormat.PrimaryLabel(unavailable, transferring: false, running: false));
    }

    [Fact]
    public void MenuWidth_IsFourHundred() => Assert.Equal(400, UiFormat.MenuWidth);

    private static GameEntry Steam(
        string app,
        bool installed = false,
        bool owned = false,
        bool canInstall = false,
        bool update = false,
        EntitlementState entitlementState = EntitlementState.Unknown) =>
        new()
        {
            Id = "steam:" + app,
            Title = app,
            Store = StoreKind.Steam,
            Installed = installed,
            Owned = owned,
            CanInstall = canInstall,
            UpdateAvailable = update,
            EntitlementState = entitlementState,
            LaunchTarget = app,
        };
}
