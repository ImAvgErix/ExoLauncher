using ExoLauncher.Adapters;
using ExoLauncher.Models;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>Drives shipped LocalAdapter install/launch entry paths with a real fixture folder.</summary>
public class LocalAdapterTests : IDisposable
{
    private readonly string _fixture;

    public LocalAdapterTests()
    {
        _fixture = Path.Combine(Path.GetTempPath(), "exo-launcher-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fixture);
        File.WriteAllText(Path.Combine(_fixture, "Game.exe"), "MZ-fake");
        File.WriteAllText(Path.Combine(_fixture, "readme.txt"), "fixture");
    }

    public void Dispose()
    {
        try { Directory.Delete(_fixture, recursive: true); } catch { }
    }

    [Fact]
    public async Task InstallAsync_RegistersFolderWithExe()
    {
        var adapter = new LocalAdapter();
        var game = new GameEntry
        {
            Id = "local:fixture",
            Title = "Fixture",
            Store = StoreKind.Local,
            Installed = false,
            Owned = true,
            CanInstall = true,
        };

        InstallProgress? last = null;
        var progress = new Progress<InstallProgress>(p => last = p);

        var result = await adapter.InstallAsync(game, _fixture, progress);

        Assert.True(result.Ok, result.Message);
        Assert.NotNull(result.Path);
        Assert.NotNull(last);
        Assert.Equal(InstallPhase.Completed, last!.Phase);
    }

    [Fact]
    public async Task InstallAsync_MissingFolder_FailsHonestly()
    {
        var adapter = new LocalAdapter();
        var game = new GameEntry
        {
            Id = "local:missing",
            Title = "Missing",
            Store = StoreKind.Local,
            Installed = false,
            Owned = true,
            CanInstall = true,
        };

        var result = await adapter.InstallAsync(game, Path.Combine(_fixture, "does-not-exist"), null);
        Assert.False(result.Ok);
        Assert.Contains("folder", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_MissingExe_Fails()
    {
        var adapter = new LocalAdapter();
        var game = new GameEntry
        {
            Id = "local:none",
            Title = "None",
            Store = StoreKind.Local,
            Installed = false,
            LaunchTarget = Path.Combine(_fixture, "nope.exe"),
        };

        var result = await adapter.LaunchAsync(game, new LaunchOptions());
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task GetLibraryAsync_ReturnsList()
    {
        var adapter = new LocalAdapter();
        var list = await adapter.GetLibraryAsync();
        Assert.NotNull(list);
    }
}
