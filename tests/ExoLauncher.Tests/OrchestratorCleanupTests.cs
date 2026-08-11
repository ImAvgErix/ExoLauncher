using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.Achievements;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Drives shipped LaunchOrchestrator so CloseStoreClientsAfterLaunch actually
/// invokes adapter CleanupAfterExitAsync (not a re-implementation).
/// </summary>
public class OrchestratorCleanupTests
{
    [Fact]
    public async Task LaunchAsync_CapturesAchievementBaselineBeforeStoreHandoff()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "exo-achievement-launch-" + Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        try
        {
            var provider = new BlockingAchievementProvider();
            var adapter = new BaselineOrderAdapter();
            using var achievements = new AchievementService([provider], statePath, TimeSpan.FromHours(1));
            var orchestrator = new LaunchOrchestrator(
                new IStoreAdapter[] { adapter },
                new SettingsService(new AppSettings { AutoInstallRedistributables = false }),
                new DependencyService(),
                achievements);
            var game = new GameEntry
            {
                Id = "local:baseline-order",
                Title = "Baseline order",
                Store = StoreKind.Local,
                Installed = true,
            };

            var launch = orchestrator.LaunchAsync(game, skipDeps: true);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(adapter.LaunchCalled.Task.IsCompleted);

            provider.Release.TrySetResult();
            var result = await launch.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(result.Ok);
            Assert.True(adapter.LaunchCalled.Task.IsCompleted);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(statePath)!, recursive: true); } catch { }
        }
    }

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

    private sealed class BaselineOrderAdapter : IStoreAdapter
    {
        public TaskCompletionSource LaunchCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            LaunchCalled.TrySetResult();
            return Task.FromResult(new LaunchResult { Ok = false, Message = "fixture handoff stopped" });
        }
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false });
        public InstallProgress GetDownloadProgress(string gameId) => new() { GameId = gameId };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class BlockingAchievementProvider : IAchievementProvider
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "fixture";
        public StoreKind Store => StoreKind.Local;
        public AchievementProviderCapabilities Capabilities => AchievementProviderCapabilities.Snapshot;
        public bool Supports(GameEntry game) => game.Store == StoreKind.Local;
        public async Task<AchievementSnapshot> GetSnapshotAsync(GameEntry game, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new AchievementSnapshot
            {
                GameId = game.Id,
                ProviderId = Id,
                SourceGameId = game.Id,
                CoverageKey = "fixture:account",
                Coverage = AchievementCoverageStatus.Partial,
                Capabilities = Capabilities,
                ReportedTotal = 1,
                ReportedUnlocked = 0,
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };
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
