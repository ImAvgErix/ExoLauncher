using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class LibraryServiceReliabilityTests
{
    [Fact]
    public async Task GetLibraryAsync_PreservesLastGoodStoreWhenThatScanFails()
    {
        var steam = new FlakyAdapter(StoreKind.Steam, "steam", "Steam", new GameEntry
        {
            Id = "steam:123",
            Title = "Known Steam Game",
            Store = StoreKind.Steam,
            Installed = true,
            LaunchTarget = "123",
            Path = Path.GetTempPath(),
        });
        var epic = new FlakyAdapter(StoreKind.Epic, "epic", "Epic", new GameEntry
        {
            Id = "epic:known",
            Title = "Known Epic Game",
            Store = StoreKind.Epic,
            Installed = true,
            LaunchTarget = "known",
            Path = Path.GetTempPath(),
        });
        var library = new LibraryService(new IStoreAdapter[] { steam, epic }, new SettingsService());

        var first = await library.GetLibraryAsync(force: true);
        Assert.Contains(first, g => g.Id == "epic:known");

        epic.FailScans = true;
        var second = await library.GetLibraryAsync(force: true);

        Assert.Contains(second, g => g.Id == "epic:known");
        Assert.Contains(second, g => g.Id == "steam:123");
    }

    [Fact]
    public async Task GetLibraryAsync_SerializesConcurrentScans()
    {
        var adapter = new FlakyAdapter(StoreKind.Steam, "steam", "Steam", new GameEntry
        {
            Id = "steam:456",
            Title = "Concurrent Game",
            Store = StoreKind.Steam,
            Installed = true,
            LaunchTarget = "456",
            Path = Path.GetTempPath(),
        });
        var library = new LibraryService(new IStoreAdapter[] { adapter }, new SettingsService());

        await Task.WhenAll(library.GetLibraryAsync(), library.GetLibraryAsync());

        Assert.Equal(1, adapter.ScanCount);
    }

    [Fact]
    public async Task GetLibraryAsync_CoalescesConcurrentForcedScans()
    {
        var adapter = new BlockingAdapter();
        var library = new LibraryService(new IStoreAdapter[] { adapter }, new SettingsService());

        var first = library.GetLibraryAsync(force: true);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = library.GetLibraryAsync(force: true);
        adapter.Release.TrySetResult();

        await Task.WhenAll(first, second);

        Assert.Equal(1, adapter.ScanCount);
    }

    [Fact]
    public async Task RefreshDerivedStateAsync_ReenrichesWithoutRescanningStores()
    {
        var adapter = new FlakyAdapter(StoreKind.Epic, "epic", "Epic", new GameEntry
        {
            Id = "epic:derived-refresh",
            Title = "Derived Refresh",
            Store = StoreKind.Epic,
            Installed = true,
            LaunchTarget = "derived-refresh",
            Path = Path.GetTempPath(),
        });
        var settings = new SettingsService();
        var library = new LibraryService(new IStoreAdapter[] { adapter }, settings);

        await library.GetLibraryAsync(force: true);
        settings.ToggleFavorite("epic:derived-refresh");

        var refreshed = await library.RefreshDerivedStateAsync();

        Assert.Equal(1, adapter.ScanCount);
        Assert.True(Assert.Single(refreshed).IsFavorite);
    }

    [Fact]
    public void StoreMatrix_SeparatesBundledBackendFromVisibleClient()
    {
        var gog = new BackendOnlyGogAdapter();
        var library = new LibraryService(new IStoreAdapter[] { gog }, new SettingsService());

        var status = Assert.Single(library.StoreMatrix());

        Assert.True(status.agentPresent);
        Assert.False(status.clientPresent);
        Assert.Equal("Not installed", status.detail);
    }

    [Fact]
    public void StoreMatrix_SeparatesHeadlessEpicBackendFromVisibleClient()
    {
        var epic = new BackendOnlyEpicAdapter();
        var library = new LibraryService(new IStoreAdapter[] { epic }, new SettingsService());

        var status = Assert.Single(library.StoreMatrix());

        Assert.True(status.agentPresent);
        Assert.False(status.clientPresent);
        Assert.Equal("Not installed", status.detail);
    }

    private sealed class FlakyAdapter : IStoreAdapter
    {
        private readonly GameEntry _game;

        public FlakyAdapter(StoreKind store, string id, string displayName, GameEntry game)
        {
            Store = store;
            Id = id;
            DisplayName = displayName;
            _game = game;
        }

        public StoreKind Store { get; }
        public string Id { get; }
        public string DisplayName { get; }
        public bool FailScans { get; set; }
        public int ScanCount { get; private set; }
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
        {
            ScanCount++;
            if (FailScans) throw new InvalidOperationException("fixture scan failure");
            return Task.FromResult<IReadOnlyList<GameEntry>>(new[] { _game });
        }

        public Task<InstallResult> InstallAsync(
            GameEntry game,
            string? installPath,
            IProgress<InstallProgress>? progress,
            CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });

        public Task<InstallResult> UpdateAsync(
            GameEntry game,
            IProgress<InstallProgress>? progress,
            CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });

        public Task<LaunchResult> LaunchAsync(
            GameEntry game,
            LaunchOptions options,
            CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = false, Message = "not used" });

        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });

        public InstallProgress GetDownloadProgress(string gameId) =>
            new() { GameId = gameId, Phase = InstallPhase.Idle };

        public Task CleanupAfterExitAsync(
            GameEntry game,
            LaunchOptions options,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class BlockingAdapter : IStoreAdapter
    {
        private int _scanCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ScanCount => Volatile.Read(ref _scanCount);
        public StoreKind Store => StoreKind.Steam;
        public string Id => "steam";
        public string DisplayName => "Steam";
        public bool IsAgentPresent() => true;

        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });

        public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _scanCount);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new[]
            {
                new GameEntry
                {
                    Id = "steam:coalesced",
                    Title = "Coalesced Game",
                    Store = StoreKind.Steam,
                    Installed = true,
                    LaunchTarget = "coalesced",
                    Path = Path.GetTempPath(),
                },
            };
        }

        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public InstallProgress GetDownloadProgress(string gameId) =>
            new() { GameId = gameId, Phase = InstallPhase.Idle };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class BackendOnlyGogAdapter : IStoreAdapter, IStoreClientPresence
    {
        public StoreKind Store => StoreKind.Gog;
        public string Id => "gog";
        public string DisplayName => "GOG";
        public bool IsAgentPresent() => true;
        public bool IsClientPresent() => false;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public InstallProgress GetDownloadProgress(string gameId) =>
            new() { GameId = gameId, Phase = InstallPhase.Idle };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class BackendOnlyEpicAdapter : IStoreAdapter, IStoreClientPresence
    {
        public StoreKind Store => StoreKind.Epic;
        public string Id => "epic";
        public string DisplayName => "Epic";
        public bool IsAgentPresent() => true;
        public bool IsClientPresent() => false;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public InstallProgress GetDownloadProgress(string gameId) =>
            new() { GameId = gameId, Phase = InstallPhase.Idle };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
