using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using Xunit;

namespace ExoLauncher.Tests;

public class EpicEglMergeTests
{
    [Fact]
    public void MapInstalledRow_DoesNotTurnMachineInstallIntoAccountOwnership()
    {
        var row = new LegendaryCli.GameRow(
            "Sugar",
            "Rocket League",
            @"C:\Games\RocketLeague",
            1234,
            Installed: true);

        var game = EpicAdapter.MapInstalledRow(row, hasLegendary: true);

        Assert.True(game.Installed);
        Assert.False(game.Owned);
        Assert.False(game.CanInstall);
        Assert.Equal("Sugar", game.LaunchTarget);
    }

    [Fact]
    public void Overlay_PromotesLegendaryOwned_WhenEglInstalled()
    {
        var owned = new List<GameEntry>
        {
            new()
            {
                Id = "epic:Sugar",
                Title = "Rocket League",
                Store = StoreKind.Epic,
                Installed = false,
                Owned = true,
                CanInstall = true,
                LaunchTarget = "Sugar",
                Status = "Not installed",
            },
            new()
            {
                Id = "epic:Control",
                Title = "Control",
                Store = StoreKind.Epic,
                Installed = false,
                Owned = true,
                CanInstall = true,
                LaunchTarget = "Control",
            },
        };

        var egl = new List<GameEntry>
        {
            new()
            {
                Id = "epic:Sugar",
                Title = "Rocket LeagueAr",
                Store = StoreKind.Epic,
                Installed = true,
                Owned = true,
                Path = @"C:\Program Files\Epic Games\rocketleague",
                LaunchTarget = "Sugar",
                SizeBytes = 1234,
            },
        };

        var merged = EpicEglMerge.ApplyInstalledOverlays(owned, egl);

        Assert.Equal(2, merged.Count);
        var rl = Assert.Single(merged, g => g.LaunchTarget == "Sugar");
        Assert.True(rl.Installed);
        Assert.False(rl.CanInstall);
        Assert.Equal(@"C:\Program Files\Epic Games\rocketleague", rl.Path);
        Assert.Equal("Rocket League", rl.Title);
        Assert.Equal("Ready", rl.Status);

        var control = Assert.Single(merged, g => g.LaunchTarget == "Control");
        Assert.False(control.Installed);
    }

    [Fact]
    public void Overlay_AddsEglOnlyInstall_WhenMissingFromLegendary()
    {
        var owned = new List<GameEntry>();
        var egl = new List<GameEntry>
        {
            new()
            {
                Id = "epic:Sugar",
                Title = "Rocket League",
                Store = StoreKind.Epic,
                Installed = true,
                Owned = false,
                CanInstall = false,
                Path = @"C:\Games\RL",
                LaunchTarget = "Sugar",
            },
        };

        var merged = EpicEglMerge.ApplyInstalledOverlays(owned, egl);
        var rl = Assert.Single(merged);
        Assert.True(rl.Installed);
        Assert.False(rl.Owned);
        Assert.False(rl.CanInstall);
        Assert.Equal("Sugar", rl.LaunchTarget);
    }

    [Fact]
    public void Overlay_DoesNotTurnMatchingMachineHistoryIntoOwnership()
    {
        var history = new[]
        {
            new GameEntry
            {
                Id = "epic:Sugar",
                Title = "Rocket League",
                Store = StoreKind.Epic,
                Installed = false,
                Owned = false,
                CanInstall = false,
                LaunchTarget = "Sugar",
            },
        };
        var installed = new[]
        {
            new GameEntry
            {
                Id = "epic:Sugar",
                Title = "Rocket League",
                Store = StoreKind.Epic,
                Installed = true,
                Owned = false,
                Path = @"C:\Games\RL",
                LaunchTarget = "Sugar",
            },
        };

        var game = Assert.Single(EpicEglMerge.ApplyInstalledOverlays(history, installed));

        Assert.True(game.Installed);
        Assert.False(game.Owned);
        Assert.False(game.CanInstall);
    }

    [Fact]
    public void Overlay_DoesNotDowngrade_AlreadyInstalledLegendary()
    {
        var owned = new List<GameEntry>
        {
            new()
            {
                Id = "epic:Hades",
                Title = "Hades",
                Store = StoreKind.Epic,
                Installed = true,
                Path = @"D:\Legendary\Hades",
                LaunchTarget = "Hades",
            },
        };
        var egl = new List<GameEntry>
        {
            new()
            {
                Id = "epic:Hades",
                Title = "Hades",
                Store = StoreKind.Epic,
                Installed = true,
                Path = @"C:\Epic\Hades",
                LaunchTarget = "Hades",
            },
        };

        var merged = EpicEglMerge.ApplyInstalledOverlays(owned, egl);
        var hades = Assert.Single(merged);
        Assert.Equal(@"D:\Legendary\Hades", hades.Path);
    }

    [Fact]
    public void Overlay_KeepsLegendaryPath_ButORsEglUpdateFlag()
    {
        var owned = new List<GameEntry>
        {
            new()
            {
                Id = "epic:Hades",
                Title = "Hades",
                Store = StoreKind.Epic,
                Installed = true,
                Path = @"D:\Legendary\Hades",
                LaunchTarget = "Hades",
            },
        };
        var egl = new List<GameEntry>
        {
            new()
            {
                Id = "epic:Hades",
                Title = "Hades",
                Store = StoreKind.Epic,
                Installed = true,
                UpdateAvailable = true,
                Path = @"C:\Epic\Hades",
                LaunchTarget = "Hades",
                Status = "Update",
            },
        };

        var merged = EpicEglMerge.ApplyInstalledOverlays(owned, egl);
        var hades = Assert.Single(merged);
        Assert.Equal(@"D:\Legendary\Hades", hades.Path);
        Assert.True(hades.UpdateAvailable);
        Assert.Equal("Update", hades.Status);
    }

    [Theory]
    [InlineData("Rocket LeagueAr", "Sugar", "Rocket League")]
    [InlineData("Rocket Leaguer", "Sugar", "Rocket League")]
    [InlineData("Control", "Control", "Control")]
    public void NormalizeEpicTitle_FixesRocketLeague(string display, string app, string expected)
    {
        Assert.Equal(expected, EpicEglMerge.NormalizeEpicTitle(display, app));
    }
}
