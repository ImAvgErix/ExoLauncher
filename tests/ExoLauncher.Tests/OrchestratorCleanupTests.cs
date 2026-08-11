using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Drives shipped LaunchOrchestrator so CloseStoreClientsAfterLaunch actually
/// invokes adapter CleanupAfterExitAsync (not a re-implementation).
/// </summary>
public class OrchestratorCleanupTests
{
    private sealed class TrackingAdapter : IStoreAdapter
    {
        public int CleanupCalls;
        public StoreKind Store => StoreKind.Local;
        public string Id => "local";
        public string DisplayName => "Local";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = true });
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = true });
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = true, Message = "launched", ProcessId = null });
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = true });
        public InstallProgress GetDownloadProgress(string gameId) => new() { GameId = gameId, Phase = InstallPhase.Idle };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
        {
            if (options.CloseStoreUiAfterExit) CleanupCalls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LaunchAsync_InvokesCleanup_WhenCloseStoreUiEnabled()
    {
        var adapter = new TrackingAdapter();
        var settings = new SettingsService();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            settings,
            new DependencyService());

        var game = new GameEntry
        {
            Id = "local:fixture",
            Title = "Fixture",
            Store = StoreKind.Local,
            Installed = true,
        };

        var result = await orchestrator.LaunchAsync(game);
        Assert.True(result.Ok);

        // ScheduleCleanupAsync delays 4s then cleans up.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (adapter.CleanupCalls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        Assert.True(adapter.CleanupCalls >= 1, "CleanupAfterExitAsync was never called after launch.");
    }
}
