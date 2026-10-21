using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class EpicInstalledTests
{
    [Fact]
    public void A_cached_cli_row_for_a_game_no_longer_on_disk_is_not_installed()
    {
        var onDisk = new[] { EpicAdapter.MapInstalledRow(new LegendaryCli.GameRow("Kept", "Kept", @"C:\Games\Kept", 1, true)) };
        var cached = new[]
        {
            new LegendaryCli.GameRow("Kept", "Kept", @"C:\Games\Kept", 1, true),
            new LegendaryCli.GameRow("Removed", "Removed", @"C:\Games\Removed", 1, true),
        };

        var games = EpicAdapter.EnrichWithLegendaryCli(onDisk, cached);

        Assert.Contains(games, game => game.LaunchTarget == "Kept" && game.Installed);
        Assert.DoesNotContain(games, game => game.LaunchTarget == "Removed");
    }

    [Fact]
    public void A_game_Legendary_does_not_track_is_imported_from_its_folder_before_updating()
    {
        // Legendary's own words when Epic Games Launcher installed the game (Rocket League, "Sugar").
        Assert.True(LegendaryCli.NotInstalled("[cli] ERROR: Update requested for \"Sugar\", but app not installed!"));
        Assert.False(LegendaryCli.NotInstalled("[cli] ERROR: Login failed"));
        Assert.False(LegendaryCli.NotInstalled(null));
        Assert.Equal(["import", "Sugar", @"C:\Users\Erix\Epic Games\rocketleague"], LegendaryCli.ImportArgs("Sugar", @"C:\Users\Erix\Epic Games\rocketleague"));
    }
}
