using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class EntitlementAuthorizationTests
{
    [Theory]
    [InlineData(EntitlementState.NotOwned, "not owned")]
    [InlineData(EntitlementState.Unverified, "could not be verified")]
    public async Task ExplicitlyBlockedEntitlement_StopsLaunchUpdateAndInstallBeforeAdapterWork(
        EntitlementState entitlementState,
        string expectedMessage)
    {
        var adapter = new RecordingAdapter();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { adapter },
            new SettingsService(new AppSettings { AutoInstallRedistributables = false }),
            new DependencyService());
        var game = new GameEntry
        {
            Id = "local:entitlement-fixture",
            Title = "Entitlement fixture",
            Store = StoreKind.Local,
            Installed = true,
            Owned = false,
            CanInstall = false,
            EntitlementState = entitlementState,
            Path = Path.GetTempPath(),
        };

        var launch = await orchestrator.LaunchAsync(game);
        var update = await orchestrator.UpdateAsync(game);
        var install = await orchestrator.InstallAsync(game);

        Assert.False(launch.Ok);
        Assert.False(update.Ok);
        Assert.False(install.Ok);
        Assert.Contains(expectedMessage, launch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedMessage, update.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedMessage, install.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, adapter.LaunchCalls);
        Assert.Equal(0, adapter.UpdateCalls);
        Assert.Equal(0, adapter.InstallCalls);
    }

    private sealed class RecordingAdapter : IStoreAdapter
    {
        public int LaunchCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int InstallCalls { get; private set; }
        public StoreKind Store => StoreKind.Local;
        public string Id => "local";
        public string DisplayName => "Local";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default)
        {
            InstallCalls++;
            return Task.FromResult(new InstallResult { Ok = true });
        }
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default)
        {
            UpdateCalls++;
            return Task.FromResult(new InstallResult { Ok = true });
        }
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
        {
            LaunchCalls++;
            return Task.FromResult(new LaunchResult { Ok = true });
        }
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = true });
        public InstallProgress GetDownloadProgress(string gameId) =>
            new() { GameId = gameId, Phase = InstallPhase.Idle };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
