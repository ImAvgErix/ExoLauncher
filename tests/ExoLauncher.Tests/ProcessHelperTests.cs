using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ProcessHelperTests
{
    [Fact]
    public void IsPathUnderRoot_RejectsSiblingWithSamePrefix()
    {
        Assert.True(ProcessHelper.IsPathUnderRoot(
            @"C:\Games\Beast\Beast.exe",
            @"C:\Games\Beast"));
        Assert.False(ProcessHelper.IsPathUnderRoot(
            @"C:\Games\Beast-Backup\Beast.exe",
            @"C:\Games\Beast"));
    }

    [Fact]
    public void UnobservedGameTimeout_UsesShortGraceAfterKnownSeedDies()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(12),
            ProcessHelper.GetUnobservedGameTimeout(
                TimeSpan.FromSeconds(90),
                seedProcessWasObserved: true,
                observedSeedGoneGrace: TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public void UnobservedGameTimeout_KeepsProtocolTimeoutWithoutASeed()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(90),
            ProcessHelper.GetUnobservedGameTimeout(
                TimeSpan.FromSeconds(90),
                seedProcessWasObserved: false,
                observedSeedGoneGrace: TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public void DirectLaunchConfirmation_DoesNotCreditAnOldProcessAfterTheStarterDies()
    {
        var confirmed = ProcessHelper.SelectDirectLaunchProcessId(
            starterPid: 41,
            starterAliveAtSettle: false,
            processIdsBeforeLaunch: new HashSet<int> { 77 },
            liveProcessIdsUnderInstallRoot: new[] { 77 });

        Assert.Null(confirmed);
    }

    [Fact]
    public void DirectLaunchConfirmation_CreditsANewInstallRootProcessAfterBootstrapHandoff()
    {
        var confirmed = ProcessHelper.SelectDirectLaunchProcessId(
            starterPid: 41,
            starterAliveAtSettle: false,
            processIdsBeforeLaunch: new HashSet<int> { 77 },
            liveProcessIdsUnderInstallRoot: new[] { 77, 88 });

        Assert.Equal(88, confirmed);
    }

    [Fact]
    public void DirectLaunchConfirmation_CreditsStarterOnlyWhenItSurvivesTheSettle()
    {
        var confirmed = ProcessHelper.SelectDirectLaunchProcessId(
            starterPid: 41,
            starterAliveAtSettle: true,
            processIdsBeforeLaunch: new HashSet<int> { 77 },
            liveProcessIdsUnderInstallRoot: new[] { 77 });

        Assert.Equal(41, confirmed);
    }

    [Fact]
    public void DirectLaunchConfirmation_DoesNotTreatTheStarterAsItsOwnHandoff()
    {
        var confirmed = ProcessHelper.SelectDirectLaunchProcessId(
            starterPid: 41,
            starterAliveAtSettle: false,
            processIdsBeforeLaunch: new HashSet<int>(),
            liveProcessIdsUnderInstallRoot: new[] { 41 });

        Assert.Null(confirmed);
    }

    [Fact]
    public void DirectLaunchSettlement_RejectsAChildThatDoesNotSurviveToTheDeadline()
    {
        var confirmed = ProcessHelper.SelectSettledDirectLaunchProcessId(
            starterPid: 41,
            starterAliveAtSettle: false,
            observedHandoffPid: 88,
            liveProcessIdsAtSettle: new[] { 99 });

        Assert.Null(confirmed);
    }

    [Fact]
    public void DirectLaunchSettlement_CreditsAChildThatRemainsLiveAtTheDeadline()
    {
        var confirmed = ProcessHelper.SelectSettledDirectLaunchProcessId(
            starterPid: 41,
            starterAliveAtSettle: false,
            observedHandoffPid: 88,
            liveProcessIdsAtSettle: new[] { 88, 99 });

        Assert.Equal(88, confirmed);
    }

    [Fact]
    public void DirectLaunchSettlement_DoesNotCreditAChildSeenOnlyAtTheDeadline()
    {
        var confirmed = ProcessHelper.SelectSettledDirectLaunchProcessId(
            starterPid: 41,
            starterAliveAtSettle: false,
            observedHandoffPid: null,
            liveProcessIdsAtSettle: new[] { 88 });

        Assert.Null(confirmed);
    }
}
