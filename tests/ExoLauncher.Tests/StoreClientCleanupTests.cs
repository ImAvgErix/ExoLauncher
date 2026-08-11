using ExoLauncher.Adapters;
using ExoLauncher.Models;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class StoreClientCleanupTests
{
    [Theory]
    [InlineData(StoreKind.Steam)]
    [InlineData(StoreKind.Epic)]
    [InlineData(StoreKind.Gog)]
    [InlineData(StoreKind.Riot)]
    public void TargetsFor_NeverIncludesActiveProvider(StoreKind activeProvider)
    {
        var targets = StoreClientCleanup.TargetsFor(activeProvider);

        Assert.Equal(3, targets.Count);
        Assert.DoesNotContain(targets, target => target.Store == activeProvider);
    }

    [Fact]
    public void CleanupTargets_ContainLauncherShellsOnly()
    {
        var allowed = StoreClientCleanup.TargetsFor(StoreKind.Local)
            .SelectMany(target => target.ExactProcessNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("steam", allowed);
        Assert.Contains("steamwebhelper", allowed);
        Assert.Contains("EpicGamesLauncher", allowed);
        Assert.Contains("EpicWebHelper", allowed);
        Assert.Contains("GalaxyClient", allowed);
        Assert.Contains("RiotClientServices", allowed);

        string[] forbidden =
        [
            "steamservice", "GameOverlayUI", "EpicOnlineServices",
            "EOSOverlayRenderer-Win64-Shipping", "EasyAntiCheat",
            "EasyAntiCheat_EOS", "GalaxyClient Service", "vgk", "vgc", "vgm",
            "LeagueClient", "LeagueClientUx", "League of Legends",
            "VALORANT-Win64-Shipping",
        ];
        foreach (var processName in forbidden)
            Assert.DoesNotContain(processName, allowed);
    }

    [Fact]
    public async Task ExitUnused_GracefulSuccessNeverUsesFallback()
    {
        var controller = FakeController.ForAllTargets(gracefulExitSucceeds: true);

        var report = await StoreClientCleanup.ExitUnusedAsync(
            StoreKind.Steam,
            controller,
            TimeSpan.Zero);

        Assert.Equal(3, report.GracefulStoreRequests);
        Assert.Equal(0, report.RemainingStoreClients);
        Assert.DoesNotContain(StoreKind.Steam, controller.GracefulStores);
    }

    [Fact]
    public async Task ExitUnused_UnresponsiveClientsAreNeverForceKilled()
    {
        var controller = FakeController.ForAllTargets(gracefulExitSucceeds: false);
        var report = await StoreClientCleanup.ExitUnusedAsync(
            StoreKind.Steam,
            controller,
            TimeSpan.Zero);

        Assert.Equal(3, report.GracefulStoreRequests);
        Assert.Equal(3, report.RemainingStoreClients);
        Assert.DoesNotContain(controller.Events, value => value.StartsWith("force:", StringComparison.Ordinal));
        var implementation = File.ReadAllText(Path.Combine(RepoRoot(), "ExoLauncher", "Adapters", "StoreClientCleanup.cs"));
        Assert.DoesNotContain(".Kill(", implementation, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminateExact", implementation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitUnused_SkipsAnotherProviderWithAnActiveGameSession()
    {
        var controller = FakeController.ForAllTargets(gracefulExitSucceeds: false);
        using var activeEpicSession = HiddenStoreRuntime.GameSession(StoreKind.Epic);
        Assert.True(HiddenStoreRuntime.IsGameProviderActive(StoreKind.Epic));

        _ = await StoreClientCleanup.ExitUnusedAsync(
            StoreKind.Steam,
            controller,
            TimeSpan.Zero);

        Assert.DoesNotContain(StoreKind.Epic, controller.GracefulStores);
        Assert.Contains(StoreKind.Gog, controller.GracefulStores);
        Assert.Contains(StoreKind.Riot, controller.GracefulStores);
    }

    [Fact]
    public void GameSession_RegistersAndReleasesItsProvider()
    {
        Assert.False(HiddenStoreRuntime.IsGameProviderActive(StoreKind.Gog));
        using (HiddenStoreRuntime.GameSession(StoreKind.Gog))
            Assert.True(HiddenStoreRuntime.IsGameProviderActive(StoreKind.Gog));
        Assert.False(HiddenStoreRuntime.IsGameProviderActive(StoreKind.Gog));
    }

    private sealed class FakeController : IStoreClientProcessController
    {
        private readonly HashSet<string> _running;
        private readonly bool _gracefulExitSucceeds;

        private FakeController(IEnumerable<string> running, bool gracefulExitSucceeds)
        {
            _running = running.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _gracefulExitSucceeds = gracefulExitSucceeds;
        }

        public List<StoreKind> GracefulStores { get; } = [];
        public List<string> Events { get; } = [];

        public static FakeController ForAllTargets(bool gracefulExitSucceeds) =>
            new(
                StoreClientCleanup.TargetsFor(StoreKind.Local)
                    .SelectMany(target => target.ExactProcessNames),
                gracefulExitSucceeds);

        public bool IsRunning(string exactProcessName) => _running.Contains(exactProcessName);

        public void RequestGracefulExit(StoreCleanupTarget target)
        {
            GracefulStores.Add(target.Store);
            Events.Add($"graceful:{target.Store}");
            if (!_gracefulExitSucceeds) return;
            foreach (var name in target.ExactProcessNames)
                _running.Remove(name);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
