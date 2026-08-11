using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class LaunchCancellationTests
{
    [Fact]
    public async Task PreCancelledLaunch_DoesNotEnterTheAdapter()
    {
        var adapter = new CountingLaunchAdapter();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            new SettingsService(new AppSettings { AutoInstallRedistributables = false }),
            new DependencyService());
        var game = new GameEntry
        {
            Id = "local:cancelled-launch",
            Title = "Cancelled launch",
            Store = StoreKind.Local,
            Installed = true,
        };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await orchestrator.LaunchAsync(game, skipDeps: true, cancelled.Token);

        Assert.False(result.Ok);
        Assert.Contains("cancel", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, adapter.LaunchCalls);
    }

    private sealed class CountingLaunchAdapter : IStoreAdapter
    {
        public int LaunchCalls { get; private set; }
        public StoreKind Store => StoreKind.Local;
        public string Id => "local";
        public string DisplayName => "Local";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false });
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false });
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
        {
            LaunchCalls++;
            return Task.FromResult(new LaunchResult { Ok = true });
        }
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false });
        public InstallProgress GetDownloadProgress(string gameId) => new() { GameId = gameId };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
