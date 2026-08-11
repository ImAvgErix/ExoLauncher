using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class StoreWindowHiderOwnershipTests
{
    [Fact]
    public void OverlappingGuardsRestoreTrackedStylesOnlyAfterFinalRestoringGuardStops()
    {
        var ownership = new WindowSuppressionOwnership();
        var restores = 0;

        ownership.Acquire(); // short install/auth scope
        ownership.Acquire(); // longer game-session scope

        Assert.False(ownership.Release(restoreOnStop: true, () => restores++));
        Assert.Equal(0, restores);
        Assert.Equal(1, ownership.ActiveOwners);

        Assert.True(ownership.Release(restoreOnStop: true, () => restores++));
        Assert.Equal(1, restores);
        Assert.Equal(0, ownership.ActiveOwners);
    }

    [Fact]
    public void FinalNonRestoringGuardLeavesStylesSuppressed()
    {
        var ownership = new WindowSuppressionOwnership();
        var restores = 0;

        ownership.Acquire();
        ownership.Acquire();

        Assert.False(ownership.Release(restoreOnStop: true, () => restores++));
        Assert.False(ownership.Release(restoreOnStop: false, () => restores++));
        Assert.Equal(0, restores);
        Assert.Equal(0, ownership.ActiveOwners);
    }

    [Fact]
    public void ReleasingAnAlreadyReleasedGuardIsIdempotent()
    {
        var ownership = new WindowSuppressionOwnership();
        var restores = 0;

        ownership.Acquire();
        Assert.True(ownership.Release(restoreOnStop: true, () => restores++));
        Assert.False(ownership.Release(restoreOnStop: true, () => restores++));

        Assert.Equal(1, restores);
        Assert.Equal(0, ownership.ActiveOwners);
    }
}
