using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class GogLaunchRoutingTests
{
    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, true, false)]
    public void DirectExit_UsesGogdlOnlyWhenItCanRecoverTheLaunch(
        bool directWasAttempted,
        bool directIsAlive,
        bool gogdlAvailable,
        bool expected)
    {
        Assert.Equal(
            expected,
            GogAdapter.ShouldFallbackToGogdlAfterDirectExit(
                directWasAttempted,
                directIsAlive,
                gogdlAvailable));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void GogdlRoute_ReusesAnAlreadyRunningVerifiedGameInsteadOfDispatchingAgain(
        int runningProcessCount,
        bool expected)
    {
        var running = Enumerable.Range(1, runningProcessCount).ToHashSet();

        Assert.Equal(expected, GogAdapter.ShouldReuseExistingGogProcess(running));
    }

    [Fact]
    public void GogdlRoute_RequiresAShortStableNewProcessConfirmation()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(700), GogAdapter.GogdlHandoffConfirmationDelay);
    }
}
