using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using System.Collections.Concurrent;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class OrchestratorCancelTests
{
    [Fact]
    public async Task CancelledWorkCannotPublishCompletedAfterCancel()
    {
        var adapter = new IgnoringCancelAdapter();
        var settings = SettingsWithoutAutomaticDependencies();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            settings,
            new DependencyService());

        var game = new GameEntry
        {
            Id = "local:cancel-fixture",
            Title = "Cancel fixture",
            Store = StoreKind.Local,
            Installed = false,
            CanInstall = true,
        };

        var install = orchestrator.InstallAsync(game);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancel = orchestrator.Cancel();
        var result = await install;

        Assert.True((bool)cancel.GetType().GetProperty("ok")!.GetValue(cancel)!);
        Assert.False(result.Ok);
        Assert.Contains("cancel", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(InstallPhase.Cancelled, orchestrator.CurrentProgress.Phase);
    }

    [Fact]
    public async Task CancelledBackendMustFinishBeforeAReplacementJobCanStart()
    {
        var adapter = new ReplacementRaceAdapter();
        var settings = SettingsWithoutAutomaticDependencies();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            settings,
            new DependencyService());
        var firstGame = CreateInstallableGame("local:first-race-fixture");
        var secondGame = CreateInstallableGame("local:second-race-fixture");

        var first = orchestrator.InstallAsync(firstGame);
        await adapter.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(GetOk(orchestrator.Cancel()));
        Assert.True(orchestrator.IsBusy);

        var blocked = await orchestrator.InstallAsync(secondGame).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(blocked.Ok);
        Assert.Contains("another", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(adapter.SecondStarted.Task.IsCompleted);

        adapter.ReleaseFirst.TrySetResult();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(firstResult.Ok);
        Assert.False(orchestrator.IsBusy);

        var second = orchestrator.InstallAsync(secondGame);
        await adapter.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            Assert.True(orchestrator.IsBusy);
            Assert.Equal(secondGame.Id, orchestrator.CurrentProgress.GameId);
            Assert.True(GetOk(orchestrator.Cancel()));
        }
        finally
        {
            adapter.ReleaseSecond.TrySetResult();
        }

        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(secondResult.Ok);
        Assert.Equal(InstallPhase.Cancelled, orchestrator.CurrentProgress.Phase);
        Assert.Equal(secondGame.Id, orchestrator.CurrentProgress.GameId);
    }

    [Fact]
    public async Task AdapterExceptionPublishesFailedAndReleasesJob()
    {
        var settings = SettingsWithoutAutomaticDependencies();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { new ThrowingAdapter() },
            settings,
            new DependencyService());
        var game = CreateInstallableGame("local:throwing-fixture");

        var result = await orchestrator.InstallAsync(game);

        Assert.False(result.Ok);
        Assert.Equal("Backend exploded.", result.Message);
        Assert.False(orchestrator.IsBusy);
        Assert.Equal(InstallPhase.Failed, orchestrator.CurrentProgress.Phase);
        Assert.Equal(game.Id, orchestrator.CurrentProgress.GameId);
        Assert.False(GetOk(orchestrator.Cancel()));
    }

    [Fact]
    public async Task UninstallCannotRaceAnActiveUpdate()
    {
        var adapter = new UpdateUninstallRaceAdapter();
        var settings = SettingsWithoutAutomaticDependencies();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            settings,
            new DependencyService());
        var game = new GameEntry
        {
            Id = "local:update-uninstall-race",
            Title = "Update uninstall race",
            Store = StoreKind.Local,
            Installed = true,
            UpdateAvailable = true,
        };

        var update = orchestrator.UpdateAsync(game);
        await adapter.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var uninstall = await orchestrator.UninstallAsync(game).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(uninstall.Ok);
        Assert.Contains("another", uninstall.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Volatile.Read(ref adapter.UninstallCalls));

        adapter.ReleaseUpdate.TrySetResult();
        var updateResult = await update.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(updateResult.Ok);
    }

    [Fact]
    public async Task RunningGameBlocksSameGameUpdateAndUninstall()
    {
        var adapter = new ActiveSessionAdapter();
        var settings = SettingsWithoutAutomaticDependencies();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            settings,
            new DependencyService());
        var game = new GameEntry
        {
            Id = "local:active-session",
            Title = "Active session",
            Store = StoreKind.Local,
            Installed = true,
            UpdateAvailable = true,
        };

        var launch = await orchestrator.LaunchAsync(game, skipDeps: true);
        Assert.True(launch.Ok);

        var update = await orchestrator.UpdateAsync(game, skipDeps: true);
        var uninstall = await orchestrator.UninstallAsync(game);
        var duplicateLaunch = await orchestrator.LaunchAsync(game, skipDeps: true);

        Assert.False(update.Ok);
        Assert.False(uninstall.Ok);
        Assert.False(duplicateLaunch.Ok);
        Assert.Contains("close", update.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("close", uninstall.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already", duplicateLaunch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Volatile.Read(ref adapter.UpdateCalls));
        Assert.Equal(0, Volatile.Read(ref adapter.UninstallCalls));
    }

    [Fact]
    public async Task StopBeforeWatcherObservation_ReleasesSessionOnceAndAllowsImmediateReplay()
    {
        var adapter = new ActiveSessionAdapter();
        var settings = SettingsWithoutAutomaticDependencies();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            settings,
            new DependencyService(),
            achievements: null,
            stopGame: static (_, _) => Task.FromResult(new GameStopResult(true, "Game closed.")));
        var game = new GameEntry
        {
            Id = "local:stop-before-observation",
            Title = "Stop before observation",
            Store = StoreKind.Local,
            Installed = true,
        };
        var completions = 0;
        orchestrator.GameSessionCompleted += _ => Interlocked.Increment(ref completions);

        Assert.True((await orchestrator.LaunchAsync(game, skipDeps: true)).Ok);
        // ScheduleCleanupAsync is still in its initial handoff delay here;
        // there is no observed game process/playtime tick to wait on.
        var stopped = await orchestrator.StopGameAsync(game).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stopped.Ok);
        Assert.Equal(1, Volatile.Read(ref completions));

        var replay = await orchestrator.LaunchAsync(game, skipDeps: true)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(replay.Ok);

        // Do not leave the replay's delayed watcher alive after the test.
        Assert.True((await orchestrator.StopGameAsync(game).WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        Assert.Equal(2, Volatile.Read(ref completions));
    }

    [Fact]
    public async Task Stop_DoesNotWaitForNativeQuietGuardToFinishDisposing()
    {
        var adapter = new ActiveSessionAdapter();
        var scopes = new ConcurrentQueue<BlockingDisposable>();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            SettingsWithoutAutomaticDependencies(),
            new DependencyService(),
            achievements: null,
            stopGame: static (_, _) => Task.FromResult(new GameStopResult(true, "Game closed.")),
            beginQuietGameSession: _ =>
            {
                var scope = new BlockingDisposable();
                scopes.Enqueue(scope);
                return scope;
            });
        var game = new GameEntry
        {
            Id = "local:slow-native-guard",
            Title = "Slow native guard",
            Store = StoreKind.Local,
            Installed = true,
        };

        Assert.True((await orchestrator.LaunchAsync(game, skipDeps: true)).Ok);
        Assert.True(scopes.TryPeek(out var firstScope));

        var stopped = await orchestrator.StopGameAsync(game).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stopped.Ok);
        await firstScope!.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(firstScope.DisposeCompleted.Task.IsCompleted);

        // Logical ownership is already released even though the native hook is
        // still leaving its message pump, so the user can immediately replay.
        Assert.True((await orchestrator.LaunchAsync(game, skipDeps: true)
            .WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        Assert.True((await orchestrator.StopGameAsync(game)
            .WaitAsync(TimeSpan.FromSeconds(2))).Ok);

        foreach (var scope in scopes)
            scope.ReleaseDispose.TrySetResult();
        await Task.WhenAll(scopes.Select(scope => scope.DisposeCompleted.Task))
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static GameEntry CreateInstallableGame(string id) => new()
    {
        Id = id,
        Title = id,
        Store = StoreKind.Local,
        Installed = false,
        CanInstall = true,
    };

    private static SettingsService SettingsWithoutAutomaticDependencies() =>
        new(new AppSettings { AutoInstallRedistributables = false });

    private static bool GetOk(object result) =>
        (bool)result.GetType().GetProperty("ok")!.GetValue(result)!;

    private sealed class BlockingDisposable : IDisposable
    {
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            DisposeStarted.TrySetResult();
            ReleaseDispose.Task.GetAwaiter().GetResult();
            DisposeCompleted.TrySetResult();
        }
    }

    private sealed class IgnoringCancelAdapter : IStoreAdapter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StoreKind Store => StoreKind.Local;
        public string Id => "local";
        public string DisplayName => "Local";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());

        public async Task<InstallResult> InstallAsync(
            GameEntry game,
            string? installPath,
            IProgress<InstallProgress>? progress,
            CancellationToken ct = default)
        {
            progress?.Report(new InstallProgress
            {
                GameId = game.Id,
                Phase = InstallPhase.Installing,
                Percent = 20,
                Status = "Working",
            });
            Started.TrySetResult();
            await Task.Delay(50);
            progress?.Report(new InstallProgress
            {
                GameId = game.Id,
                Phase = InstallPhase.Installing,
                Percent = 80,
                Status = "Still working",
            });
            return new InstallResult { Ok = true, Message = "Installed." };
        }

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

    private sealed class ReplacementRaceAdapter : IStoreAdapter
    {
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StoreKind Store => StoreKind.Local;
        public string Id => "local";
        public string DisplayName => "Local";
        public bool IsAgentPresent() => true;

        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });

        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());

        public async Task<InstallResult> InstallAsync(
            GameEntry game,
            string? installPath,
            IProgress<InstallProgress>? progress,
            CancellationToken ct = default)
        {
            progress?.Report(new InstallProgress
            {
                GameId = game.Id,
                Phase = InstallPhase.Installing,
                Percent = 50,
                Status = "Waiting",
            });

            var first = game.Id.Contains("first", StringComparison.Ordinal);
            (first ? FirstStarted : SecondStarted).TrySetResult();
            await (first ? ReleaseFirst.Task : ReleaseSecond.Task);
            return new InstallResult { Ok = true, Message = "Installed." };
        }

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

    private sealed class ThrowingAdapter : IStoreAdapter
    {
        public StoreKind Store => StoreKind.Local;
        public string Id => "local";
        public string DisplayName => "Local";
        public bool IsAgentPresent() => true;

        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });

        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());

        public Task<InstallResult> InstallAsync(
            GameEntry game,
            string? installPath,
            IProgress<InstallProgress>? progress,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Backend exploded.");

        public Task<InstallResult> UpdateAsync(
            GameEntry game,
            IProgress<InstallProgress>? progress,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Backend exploded.");

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

    private sealed class UpdateUninstallRaceAdapter : IStoreAdapter
    {
        public TaskCompletionSource UpdateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseUpdate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int UninstallCalls;

        public StoreKind Store => StoreKind.Local;
        public string Id => "local";
        public string DisplayName => "Local";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
        public Task<InstallResult> InstallAsync(
            GameEntry game,
            string? installPath,
            IProgress<InstallProgress>? progress,
            CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });

        public async Task<InstallResult> UpdateAsync(
            GameEntry game,
            IProgress<InstallProgress>? progress,
            CancellationToken ct = default)
        {
            UpdateStarted.TrySetResult();
            await ReleaseUpdate.Task.WaitAsync(ct);
            return new InstallResult { Ok = true, Message = "Updated." };
        }

        public Task<LaunchResult> LaunchAsync(
            GameEntry game,
            LaunchOptions options,
            CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = false, Message = "not used" });

        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
        {
            Interlocked.Increment(ref UninstallCalls);
            return Task.FromResult(new InstallResult { Ok = true, Message = "Uninstalled." });
        }

        public InstallProgress GetDownloadProgress(string gameId) =>
            new() { GameId = gameId, Phase = InstallPhase.Idle };
        public Task CleanupAfterExitAsync(
            GameEntry game,
            LaunchOptions options,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ActiveSessionAdapter : IStoreAdapter
    {
        public int UpdateCalls;
        public int UninstallCalls;

        public StoreKind Store => StoreKind.Local;
        public string Id => "local";
        public string DisplayName => "Local";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
        public Task<InstallResult> InstallAsync(
            GameEntry game,
            string? installPath,
            IProgress<InstallProgress>? progress,
            CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UpdateAsync(
            GameEntry game,
            IProgress<InstallProgress>? progress,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref UpdateCalls);
            return Task.FromResult(new InstallResult { Ok = true, Message = "Updated." });
        }
        public Task<LaunchResult> LaunchAsync(
            GameEntry game,
            LaunchOptions options,
            CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = true, Message = "Running." });
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
        {
            Interlocked.Increment(ref UninstallCalls);
            return Task.FromResult(new InstallResult { Ok = true, Message = "Uninstalled." });
        }
        public InstallProgress GetDownloadProgress(string gameId) =>
            new() { GameId = gameId, Phase = InstallPhase.Idle };
        public Task CleanupAfterExitAsync(
            GameEntry game,
            LaunchOptions options,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
