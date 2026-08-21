using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class EpicLibraryScanTests
{
    [Fact]
    public async Task LegendaryCliCancel_DoesNotThrow_AndLeavesNativeEglRows()
    {
        var native = new List<GameEntry>
        {
            new()
            {
                Id = "epic:Sugar",
                Title = "Rocket League",
                Store = StoreKind.Epic,
                Installed = true,
                Path = @"C:\Program Files\Epic Games\rocketleague",
                LaunchTarget = "Sugar",
            },
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cliRows = await EpicAdapter.TryParseLegendaryListAsync(
            _ => throw new OperationCanceledException(cts.Token),
            cts.Token);

        Assert.Empty(cliRows);
        var merged = EpicAdapter.EnrichWithLegendaryCli(native, cliRows, hasLegendary: true);
        var rl = Assert.Single(merged);
        Assert.Equal("Sugar", rl.LaunchTarget);
        Assert.True(rl.Installed);
    }

    [Fact]
    public void EnrichWithLegendaryCli_KeepsEglInstall_WhenCliOmitsIt()
    {
        var native = new List<GameEntry>
        {
            new()
            {
                Id = "epic:Sugar",
                Title = "Rocket League",
                Store = StoreKind.Epic,
                Installed = true,
                Path = @"C:\Program Files\Epic Games\rocketleague",
                LaunchTarget = "Sugar",
            },
        };
        var cli = new[]
        {
            new LegendaryCli.GameRow("Control", "Control", @"D:\Legendary\Control", 1, true),
        };

        var merged = EpicAdapter.EnrichWithLegendaryCli(native, cli, hasLegendary: true);

        Assert.Contains(merged, g => g.LaunchTarget == "Sugar" && g.Installed);
        Assert.Contains(merged, g => g.LaunchTarget == "Control" && g.Installed);
    }

    [Fact]
    public void ParseLibraryJson_ReadsLegendaryInstalledObject()
    {
        const string json = """
            {
              "Sugar": {
                "app_name": "Sugar",
                "title": "Rocket League",
                "install_path": "C:\\\\Program Files\\\\Epic Games\\\\rocketleague",
                "install_size": 42788409447
              }
            }
            """;

        var rows = LegendaryCli.ParseLibraryJson(json, forceInstalled: true);
        var sugar = Assert.Single(rows);
        Assert.Equal("Sugar", sugar.AppName);
        Assert.Equal("Rocket League", sugar.Title);
        Assert.True(sugar.Installed);
        Assert.Equal(42788409447, sugar.SizeBytes);
    }

    [Fact]
    public void LegendaryInstalledJsonCandidates_ShareLegendaryUserRoots()
    {
        var users = EpicPlaytime.LegendaryUserJsonCandidates()
            .Select(path => Path.GetDirectoryName(path))
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(dir => Path.Combine(dir!, "installed.json"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var installed = EpicAdapter.LegendaryInstalledJsonCandidates().ToList();
        Assert.NotEmpty(installed);
        Assert.All(installed, path => Assert.Contains(path, users));
        Assert.Contains(installed, path => path.EndsWith("installed.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsEglUpdatePending_UsesOnlyLocalIncompleteOrValidationFlags()
    {
        Assert.True(EpicAdapter.IsEglUpdatePending("""{"bIsIncompleteInstall":true,"bNeedsValidation":false}"""));
        Assert.True(EpicAdapter.IsEglUpdatePending("""{"bIsIncompleteInstall":false,"bNeedsValidation":true}"""));
        Assert.False(EpicAdapter.IsEglUpdatePending("""{"bIsIncompleteInstall":false,"bNeedsValidation":false,"PendingManifestPath":"C:\\Games\\Pending\\x.manifest"}"""));
    }
}
