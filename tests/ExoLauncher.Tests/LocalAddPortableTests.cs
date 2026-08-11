using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Local/portable adapter is a first-class DRM-free backend. The UI keeps the
/// uninstalled local:add action out of the installed-only library view.
/// </summary>
public class LocalAddPortableTests : IDisposable
{
    private readonly string _fixture;

    public LocalAddPortableTests()
    {
        _fixture = Path.Combine(Path.GetTempPath(), "exo-local-add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fixture);
        File.WriteAllText(Path.Combine(_fixture, "PortableGame.exe"), "MZ-fake");
    }

    public void Dispose()
    {
        try { Directory.Delete(_fixture, recursive: true); } catch { }
        try
        {
            var lib = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExoLauncher", "Games", Path.GetFileName(_fixture));
            if (Directory.Exists(lib)) Directory.Delete(lib, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void AddPortableId_IsNotMockPrefix()
    {
        Assert.Equal("local:add", LocalAdapter.AddPortableId);
        Assert.False(LocalAdapter.AddPortableId.StartsWith("mock:", StringComparison.OrdinalIgnoreCase));
        var entry = LocalAdapter.CreateAddPortableEntry();
        Assert.True(entry.CanInstall);
        Assert.False(entry.Installed);
        Assert.Equal(StoreKind.Local, entry.Store);
        Assert.Equal("install", entry.PrimaryAction);
    }

    [Fact]
    public async Task GetLibraryAsync_IncludesLocalAddPortableAction()
    {
        var adapter = new LocalAdapter();
        var library = new LibraryService(new IStoreAdapter[] { adapter }, new SettingsService());
        var games = await library.GetLibraryAsync(force: true);
        Assert.Contains(games, g => g.Id == LocalAdapter.AddPortableId);
        Assert.All(games, g => Assert.Equal(StoreKind.Local, g.Store));
    }

    [Fact]
    public async Task LocalAdapter_StillExposesAddPortableEntry()
    {
        var adapter = new LocalAdapter();
        var games = await adapter.GetLibraryAsync();
        var add = Assert.Single(games, g => g.Id == LocalAdapter.AddPortableId);
        Assert.True(add.CanInstall);
    }

    [Fact]
    public async Task Orchestrator_InstallsAddPortable_WithFolderPath()
    {
        var adapter = new LocalAdapter();
        var settings = new SettingsService();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            settings,
            new DependencyService());

        var entry = LocalAdapter.CreateAddPortableEntry();
        Assert.False(entry.Id.StartsWith("mock:", StringComparison.OrdinalIgnoreCase));

        InstallProgress? last = null;
        var result = await orchestrator.InstallAsync(entry, _fixture);

        Assert.True(result.Ok, result.Message);
        Assert.NotNull(result.Path);
        Assert.True(Directory.Exists(result.Path));

        var direct = await adapter.InstallAsync(entry, _fixture, new Progress<InstallProgress>(p => last = p));
        Assert.True(direct.Ok, direct.Message);
    }

    [Fact]
    public async Task Orchestrator_RejectsMockLocal_ButAcceptsAddPortable()
    {
        var adapter = new LocalAdapter();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            new SettingsService(),
            new DependencyService());

        var mock = new GameEntry
        {
            Id = "mock:celeste",
            Title = "Celeste",
            Store = StoreKind.Local,
            Installed = false,
            CanInstall = true,
        };
        var mockResult = await orchestrator.InstallAsync(mock, _fixture);
        Assert.False(mockResult.Ok);
        Assert.Contains("Demo", mockResult.Message, StringComparison.OrdinalIgnoreCase);

        var real = await orchestrator.InstallAsync(LocalAdapter.CreateAddPortableEntry(), _fixture);
        Assert.True(real.Ok, real.Message);
    }
}
