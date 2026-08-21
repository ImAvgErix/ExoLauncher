using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Riot;
using ExoLauncher.Models;
using ExoLauncher.Services;
using System.Net;
using Xunit;

namespace ExoLauncher.Tests;

public class RiotLaunchProcessTests
{
    [Theory]
    [InlineData("installed", true)]
    [InlineData("INSTALLED", true)]
    [InlineData("not_installed", false)]
    [InlineData("reinstalled", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsInstalledState_OnlyAcceptsExactInstalledValue(string? state, bool expected)
    {
        Assert.Equal(expected, RiotAdapter.IsInstalledState(state));
    }

    [Fact]
    public void GameProcessNames_League_UsesOnlyActualMatchExecutable()
    {
        var names = RiotAdapter.GameProcessNames("league_of_legends");

        Assert.Equal(["League of Legends"], names);
        Assert.DoesNotContain("LeagueClient", names);
        Assert.DoesNotContain("LeagueClientUx", names);
        Assert.DoesNotContain("LeagueClientUxRender", names);
        Assert.DoesNotContain("RiotClientUx", names);
        Assert.DoesNotContain("RiotClientServices", names);
    }

    [Fact]
    public void LaunchReadyProcessNames_League_CanObserveClientHandoff()
    {
        var names = RiotAdapter.LaunchReadyProcessNames("league_of_legends");

        Assert.Contains("LeagueClient", names);
        Assert.Contains("LeagueClientUx", names);
        Assert.DoesNotContain("RiotClientUx", names);
    }

    [Fact]
    public void RiotSessionIgnoredProcesses_ExcludePersistentLeagueClients()
    {
        var ignored = LaunchOrchestrator.BootstrapProcessNames(StoreKind.Riot);

        Assert.Contains("LeagueClient", ignored);
        Assert.Contains("LeagueClientUx", ignored);
        Assert.Contains("LeagueClientUxRender", ignored);
    }

    [Fact]
    public void ProtocolLaunchStores_IgnoreVendorClientProcesses()
    {
        Assert.Contains("EADesktop", LaunchOrchestrator.BootstrapProcessNames(StoreKind.Ea));
        Assert.Contains("upc", LaunchOrchestrator.BootstrapProcessNames(StoreKind.Ubisoft));
        Assert.Contains("Battle.net", LaunchOrchestrator.BootstrapProcessNames(StoreKind.BattleNet));
    }

    [Fact]
    public void RiotUiProcessNames_NeverIncludeLeagueGame()
    {
        Assert.DoesNotContain("LeagueClient", StoreWindowHider.RiotUiProcessNames);
        Assert.DoesNotContain("LeagueClientUx", StoreWindowHider.RiotUiProcessNames);
        Assert.DoesNotContain("VALORANT-Win64-Shipping", StoreWindowHider.RiotUiProcessNames);
        Assert.Contains("RiotClientUx", StoreWindowHider.RiotUiProcessNames);
    }

    [Fact]
    public async Task PreCancelledLaunch_DoesNotStartRiotClient()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RiotClientApi.ConnectAsync(
                @"C:\this-path-must-never-be-started\RiotClientServices.exe",
                TimeSpan.FromSeconds(1),
                cts.Token));
    }

    [Fact]
    public async Task PreCancelledAdapterLaunch_ReturnsCancelledWithoutResolvingAClient()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var game = new GameEntry
        {
            Id = "riot:valorant",
            Title = "VALORANT",
            Store = StoreKind.Riot,
            LaunchTarget = "valorant",
        };

        var result = await new RiotAdapter().LaunchAsync(game, new LaunchOptions(), cts.Token);

        Assert.False(result.Ok);
        Assert.Equal("Cancelled.", result.Message);
        Assert.Equal("riot", result.BackendStarted);
    }

    [Fact]
    public void LeagueWatch_EndsWhenNoClientHandoffAppearsWithinItsBound()
    {
        var start = DateTimeOffset.UtcNow;

        Assert.True(ProcessHelper.HasMissedHandoff(
            sawGame: false,
            sawHandoff: false,
            now: start.AddSeconds(90),
            handoffAppearDeadline: start.AddSeconds(90)));
    }

    [Fact]
    public void LeagueWatch_RemainsActiveAfterARealClientHandoff()
    {
        var start = DateTimeOffset.UtcNow;

        Assert.False(ProcessHelper.HasMissedHandoff(
            sawGame: false,
            sawHandoff: true,
            now: start.AddHours(3),
            handoffAppearDeadline: start.AddSeconds(90)));
    }

    [Fact]
    public async Task ColdRegistryWarmup_RetriesTransientNotInstalled_WhenLocalInstallIsVerified()
    {
        var states = new Queue<string?>([null, "not_installed", "installed"]);
        var reads = 0;

        var result = await RiotAdapter.ReadInstallStateAfterWarmupAsync(
            _ =>
            {
                reads++;
                return Task.FromResult(states.Dequeue());
            },
            verifiedLocalInstall: true,
            maxAttempts: 4,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal("installed", result);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task ColdRegistryWarmup_DoesNotRetry_WhenNoLocalInstallCanBeVerified()
    {
        var reads = 0;

        var result = await RiotAdapter.ReadInstallStateAfterWarmupAsync(
            _ =>
            {
                reads++;
                return Task.FromResult<string?>("not_installed");
            },
            verifiedLocalInstall: false,
            maxAttempts: 4,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal("not_installed", result);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ColdEligibilityWarmup_RetriesTransientFalse_WhenLocalInstallIsVerified()
    {
        var values = new Queue<bool?>([false, false, true]);
        var reads = 0;

        var result = await RiotAdapter.ReadEligibilityAfterWarmupAsync(
            _ =>
            {
                reads++;
                return Task.FromResult(values.Dequeue());
            },
            verifiedLocalInstall: true,
            maxAttempts: 4,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(3, reads);
    }

    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, true)]
    public void LaunchEndpointFallback_IsLimitedToVerifiedUnpatchedInstalls(
        bool eligible,
        bool verifiedLocalInstall,
        bool patching,
        bool expected)
    {
        Assert.Equal(
            expected,
            RiotAdapter.CanLetLaunchEndpointDecide(eligible, verifiedLocalInstall, patching));
    }

    [Fact]
    public void LaunchResponse_AlreadyLaunched_IsIdempotentSuccess_WithExistingSession()
    {
        const string body = """
            {"errorCode":"already_launched","httpStatus":423,"implementationDetails":{},"message":"already_launched: Product 'league_of_legends' patchline 'live' already launched (session ID '9qJ-nRKHEmeFhqOZXvcI')"}
            """;

        var result = RiotClientApi.InterpretLaunchResponse(HttpStatusCode.Locked, body);

        Assert.True(result.Accepted);
        Assert.True(result.AlreadyRunning);
        Assert.Equal("9qJ-nRKHEmeFhqOZXvcI", result.SessionId);
        Assert.Null(result.Error);
    }

    [Fact]
    public void LaunchResponse_UnrelatedLockedError_RemainsFailure()
    {
        const string body = "{\"errorCode\":\"patching\",\"httpStatus\":423}";

        var result = RiotClientApi.InterpretLaunchResponse(HttpStatusCode.Locked, body);

        Assert.False(result.Accepted);
        Assert.False(result.AlreadyRunning);
    }
}
