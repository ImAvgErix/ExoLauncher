using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class EpicLaunchRoutingTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void EpicFallbackRoute_PrefersAuthenticatedHandoffWheneverItIsUsable(
        bool launcherAvailable,
        bool launchTargetAvailable,
        bool expectedLauncherHandoff)
    {
        Assert.Equal(
            expectedLauncherHandoff,
            EpicAdapter.SelectEpicFallbackRoute(launcherAvailable, launchTargetAvailable) ==
            EpicAdapter.EpicFallbackRoute.LauncherHandoff);
    }

    [Fact]
    public void RocketLeague_UsesVerifiedTripleBeforeBareAppNameFallbacks()
    {
        var uris = EpicAdapter.BuildEpicLaunchUris(
                "Sugar",
                "9773aa1aa54f4f7b80e44bef04986cea",
                "530145df28a24424923f5828cc9031a1")
            .ToArray();

        Assert.Equal(
            [
                "com.epicgames.launcher://apps/9773aa1aa54f4f7b80e44bef04986cea%3A530145df28a24424923f5828cc9031a1%3ASugar?action=launch&silent=true",
                "com.epicgames.launcher://apps/9773aa1aa54f4f7b80e44bef04986cea%3A530145df28a24424923f5828cc9031a1%3ASugar?action=launch",
                "com.epicgames.launcher://apps/Sugar?action=launch&silent=true",
                "com.epicgames.launcher://apps/Sugar?action=launch",
            ],
            uris);
    }

    [Fact]
    public async Task CancellationDuringUriWait_DoesNotIssueLaterLaunchRequests()
    {
        using var cts = new CancellationTokenSource();
        var attempted = new List<string>();
        var uris = EpicAdapter.BuildEpicLaunchUris(
                "Sugar",
                "9773aa1aa54f4f7b80e44bef04986cea",
                "530145df28a24424923f5828cc9031a1")
            .ToArray();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EpicAdapter.TryEpicLaunchUrisAsync(
                uris,
                (uri, _, _) =>
                {
                    attempted.Add(uri);
                    cts.Cancel();
                    return Task.FromResult<int?>(null);
                },
                cts.Token));

        Assert.Single(attempted);
        Assert.Equal(uris[0], attempted[0]);
    }

    [Fact]
    public async Task ColdEpicLaunch_WaitsForCommandListenerBeforeSubmittingRocketLeagueUri()
    {
        var probe = 0;
        var delays = new List<TimeSpan>();

        var ready = await EpicAdapter.WaitForEpicCommandListenerAsync(
            launcherRunning: () => probe >= 1,
            webHelperRunning: () => probe >= 2,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                if (delay == TimeSpan.FromMilliseconds(350)) probe++;
                return Task.CompletedTask;
            },
            CancellationToken.None,
            maxPolls: 5);

        Assert.True(ready);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(350),
                TimeSpan.FromMilliseconds(350),
                TimeSpan.FromMilliseconds(750),
            ],
            delays);
    }

    [Fact]
    public async Task ColdEpicLaunch_ReadinessProbeIsBoundedWhenClientNeverStarts()
    {
        var waits = 0;

        var ready = await EpicAdapter.WaitForEpicCommandListenerAsync(
            launcherRunning: static () => false,
            webHelperRunning: static () => false,
            delayAsync: (_, _) =>
            {
                waits++;
                return Task.CompletedTask;
            },
            CancellationToken.None,
            maxPolls: 3);

        Assert.False(ready);
        Assert.Equal(3, waits);
    }

    [Fact]
    public async Task ExistingProcessCandidate_IsNotCreditedAsANewEpicLaunch()
    {
        var confirmed = await ProcessHelper.ConfirmNewProcessCandidateAsync(
            candidatePid: 42,
            excludedProcessIds: new HashSet<int> { 42 },
            confirmationDelay: TimeSpan.Zero,
            isLive: _ => throw new Xunit.Sdk.XunitException("Existing PID must not be probed."),
            CancellationToken.None);

        Assert.Null(confirmed);
    }

    [Fact]
    public async Task ExitedProcessCandidate_IsNotCreditedAfterHandoffConfirmation()
    {
        var confirmed = await ProcessHelper.ConfirmNewProcessCandidateAsync(
            candidatePid: 43,
            excludedProcessIds: new HashSet<int>(),
            confirmationDelay: TimeSpan.Zero,
            isLive: _ => false,
            CancellationToken.None);

        Assert.Null(confirmed);
    }

    [Fact]
    public async Task LiveNewProcessCandidate_IsCreditedAfterHandoffConfirmation()
    {
        var confirmed = await ProcessHelper.ConfirmNewProcessCandidateAsync(
            candidatePid: 44,
            excludedProcessIds: new HashSet<int>(),
            confirmationDelay: TimeSpan.Zero,
            isLive: _ => true,
            CancellationToken.None);

        Assert.Equal(44, confirmed);
    }

    [Fact]
    public void DirectExit_WithOnlyBaselineProcesses_IsNotCredited()
    {
        var pid = ProcessHelper.SelectDirectLaunchProcessId(
            starterPid: 45,
            starterAliveAtSettle: false,
            processIdsBeforeLaunch: new HashSet<int> { 9 },
            liveProcessIdsUnderInstallRoot: [9]);

        Assert.Null(pid);
    }

    [Fact]
    public void DirectLaunch_CreditsTheNewStableStarter()
    {
        var pid = ProcessHelper.SelectDirectLaunchProcessId(
            starterPid: 46,
            starterAliveAtSettle: true,
            processIdsBeforeLaunch: new HashSet<int> { 9 },
            liveProcessIdsUnderInstallRoot: [9, 46]);

        Assert.Equal(46, pid);
    }

    [Fact]
    public async Task PreCancelledLaunch_PropagatesBeforeAnyEpicRouteCanStart()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var game = new ExoLauncher.Models.GameEntry
        {
            Id = "epic:cancelled-fixture",
            Title = "Cancelled fixture",
            Store = ExoLauncher.Models.StoreKind.Epic,
            LaunchTarget = "cancelled-fixture",
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EpicAdapter().LaunchAsync(game, new LaunchOptions(), cts.Token));
    }
}
