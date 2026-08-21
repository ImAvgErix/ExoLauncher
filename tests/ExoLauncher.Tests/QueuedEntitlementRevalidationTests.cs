using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class QueuedEntitlementRevalidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedInstallOrUpdate_AccountSwitchFailsBeforeAdapterWork(bool update)
    {
        var adapter = new ScopedQueueAdapter { AccountScope = "scope-a" };
        var fresh = Target(update);
        var resolverCalls = 0;
        var orchestrator = Create(adapter, (id, _) =>
        {
            Interlocked.Increment(ref resolverCalls);
            return Task.FromResult<GameEntry?>(fresh);
        });

        var active = orchestrator.UpdateAsync(BusyGame());
        await adapter.BusyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = update
            ? await orchestrator.UpdateAsync(Target(update: true))
            : await orchestrator.InstallAsync(Target(update: false));
        Assert.True(queued.Queued);

        adapter.AccountScope = "scope-b";
        adapter.ReleaseBusy.TrySetResult();
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilIdle(orchestrator);

        Assert.Equal(0, resolverCalls);
        Assert.Equal(0, adapter.TargetInstallCalls);
        Assert.Equal(0, adapter.TargetUpdateCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedInstallOrUpdate_RefundFailsAfterFreshLibraryResolution(bool update)
    {
        var adapter = new ScopedQueueAdapter { AccountScope = "scope-a" };
        var fresh = WithEntitlement(Target(update), EntitlementState.NotOwned, owned: false);
        var resolverCalls = 0;
        var orchestrator = Create(adapter, (id, _) =>
        {
            Interlocked.Increment(ref resolverCalls);
            Assert.Equal("steam:target", id);
            return Task.FromResult<GameEntry?>(fresh);
        });

        var active = orchestrator.UpdateAsync(BusyGame());
        await adapter.BusyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = update
            ? await orchestrator.UpdateAsync(Target(update: true))
            : await orchestrator.InstallAsync(Target(update: false));
        Assert.True(queued.Queued);

        adapter.ReleaseBusy.TrySetResult();
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilIdle(orchestrator);

        Assert.Equal(1, resolverCalls);
        Assert.Equal(0, adapter.TargetInstallCalls);
        Assert.Equal(0, adapter.TargetUpdateCalls);
    }

    [Fact]
    public async Task QueuedUpdate_AccountSwitchDuringFreshResolutionStillFailsClosed()
    {
        var adapter = new ScopedQueueAdapter { AccountScope = "scope-a" };
        var orchestrator = Create(adapter, (id, _) =>
        {
            adapter.AccountScope = "scope-b";
            return Task.FromResult<GameEntry?>(Target(update: true));
        });

        var active = orchestrator.UpdateAsync(BusyGame());
        await adapter.BusyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await orchestrator.UpdateAsync(Target(update: true))).Queued);

        adapter.ReleaseBusy.TrySetResult();
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilIdle(orchestrator);

        Assert.Equal(0, adapter.TargetUpdateCalls);
    }

    [Fact]
    public async Task LibraryRevalidation_ForcesFreshCurrentAccountRow()
    {
        var adapter = new ScopedQueueAdapter
        {
            AccountScope = "scope-a",
            LibraryRows = new[] { Target(update: true) },
        };
        var library = new LibraryService(new IStoreAdapter[] { adapter }, new SettingsService());
        _ = await library.GetLibraryAsync(force: true);
        adapter.LibraryRows = new[]
        {
            WithEntitlement(Target(update: true), EntitlementState.NotOwned, owned: false),
        };

        var current = await library.RevalidateActionGameAsync("steam:target");

        Assert.NotNull(current);
        Assert.Equal(EntitlementState.NotOwned, current!.EntitlementState);
        Assert.True(adapter.LibraryScans >= 2);
    }

    [Fact]
    public async Task LibraryRevalidation_DoesNotAuthorizeFromLastGoodAfterStoreScanFailure()
    {
        var adapter = new ScopedQueueAdapter
        {
            AccountScope = "scope-a",
            LibraryRows = new[] { Target(update: true) },
        };
        var library = new LibraryService(new IStoreAdapter[] { adapter }, new SettingsService());
        _ = await library.GetLibraryAsync(force: true);
        adapter.FailLibraryScans = true;

        var current = await library.RevalidateActionGameAsync("steam:target");

        Assert.Null(current);
        Assert.True(adapter.LibraryScans >= 2);
    }

    private static LaunchOrchestrator Create(
        ScopedQueueAdapter adapter,
        Func<string, CancellationToken, Task<GameEntry?>> resolver) =>
        new(
            new IStoreAdapter[] { adapter },
            new SettingsService(new AppSettings { AutoInstallRedistributables = false }),
            new DependencyService(),
            achievements: null,
            stopGame: null,
            beginQuietGameSession: _ => NoopDisposable.Instance,
            missingDependencies: _ => Array.Empty<DependencyInfo>(),
            revalidateQueuedGame: resolver);

    private static async Task WaitUntilIdle(LaunchOrchestrator orchestrator)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (orchestrator.IsBusy && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.False(orchestrator.IsBusy);
    }

    private static GameEntry BusyGame() => new()
    {
        Id = "steam:busy",
        Title = "Busy fixture",
        Store = StoreKind.Steam,
        Installed = true,
        Owned = true,
        EntitlementState = EntitlementState.Owned,
        UpdateAvailable = true,
        LaunchTarget = "busy",
    };

    private static GameEntry Target(bool update) => new()
    {
        Id = "steam:target",
        Title = "Queued target",
        Store = StoreKind.Steam,
        Installed = update,
        Owned = true,
        EntitlementState = EntitlementState.Owned,
        UpdateAvailable = update,
        CanInstall = !update,
        LaunchTarget = "target",
    };

    private static GameEntry WithEntitlement(
        GameEntry game,
        EntitlementState entitlementState,
        bool owned) => new()
    {
        Id = game.Id,
        Title = game.Title,
        Store = game.Store,
        Installed = game.Installed,
        Owned = owned,
        EntitlementState = entitlementState,
        UpdateAvailable = game.UpdateAvailable,
        CanInstall = game.CanInstall,
        LaunchTarget = game.LaunchTarget,
    };

    private sealed class ScopedQueueAdapter : IStoreAdapter, IStoreAccountScope
    {
        public string? AccountScope { get; set; }
        public TaskCompletionSource BusyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBusy { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TargetInstallCalls { get; private set; }
        public int TargetUpdateCalls { get; private set; }
        public IReadOnlyList<GameEntry> LibraryRows { get; set; } = Array.Empty<GameEntry>();
        public int LibraryScans { get; private set; }
        public bool FailLibraryScans { get; set; }
        public StoreKind Store => StoreKind.Steam;
        public string Id => "steam";
        public string DisplayName => "Steam fixture";
        public string? GetActiveAccountScope() => AccountScope;
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
        {
            LibraryScans++;
            if (FailLibraryScans) throw new IOException("fixture scan failure");
            return Task.FromResult(LibraryRows);
        }
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default)
        {
            if (game.Id == "steam:target") TargetInstallCalls++;
            return Task.FromResult(new InstallResult { Ok = true, Message = "installed" });
        }
        public async Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default)
        {
            if (game.Id == "steam:busy")
            {
                BusyStarted.TrySetResult();
                await ReleaseBusy.Task.WaitAsync(ct);
            }
            else if (game.Id == "steam:target")
            {
                TargetUpdateCalls++;
            }
            return new InstallResult { Ok = true, Message = "updated" };
        }
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public InstallProgress GetDownloadProgress(string gameId) =>
            new() { GameId = gameId, Phase = InstallPhase.Idle };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
