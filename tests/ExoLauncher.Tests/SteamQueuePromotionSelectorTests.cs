using ExoLauncher.Adapters.Cli;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamQueuePromotionSelectorTests
{
    private const int Width = 1200;
    private const int Height = 800;

    [Fact]
    public void DeadlockRow_SelectsOnlyDeadlocksRightGutterIcon_NotMecchas()
    {
        var selection = SteamQueuePromotionSelector.Select(
            "Deadlock",
            [
                new SteamQueueOcrLine("Deadlock", 130, 180, 340, 220),
                new SteamQueueOcrLine("MECCHA CHAMELEON", 130, 300, 470, 340),
            ],
            Width,
            Height,
            [
                // Deadlock's control in the target row.
                .. Icon(1060, 226),
                // A separate valid-looking control for MECCHA: must be ignored.
                .. Icon(1060, 346),
            ]);

        Assert.True(selection.IsSuccess);
        Assert.Equal(new SteamQueueClickPoint(1065, 231), selection.ClickPoint);
    }

    [Fact]
    public void SubstringTitle_IsNotATargetMatch()
    {
        var selection = SteamQueuePromotionSelector.Select(
            "Deadlock",
            [new SteamQueueOcrLine("Deadlock Playtest", 130, 180, 400, 220)],
            Width,
            Height,
            Icon(1060, 226));

        Assert.False(selection.IsSuccess);
        Assert.Equal(SteamQueuePromotionSelectionFailure.TargetNotFound, selection.Failure);
    }

    [Fact]
    public void DuplicateExactTargetRows_FailsClosed()
    {
        var selection = SteamQueuePromotionSelector.Select(
            "Deadlock",
            [
                new SteamQueueOcrLine("Deadlock", 130, 180, 340, 220),
                new SteamQueueOcrLine("  DEADLOCK  ", 130, 300, 340, 340),
            ],
            Width,
            Height,
            Icon(1060, 226));

        Assert.False(selection.IsSuccess);
        Assert.Equal(SteamQueuePromotionSelectionFailure.AmbiguousTarget, selection.Failure);
    }

    [Fact]
    public void TargetWithoutARightGutterIcon_FailsClosed()
    {
        var selection = SteamQueuePromotionSelector.Select(
            "Deadlock",
            [new SteamQueueOcrLine("Deadlock", 130, 180, 340, 220)],
            Width,
            Height,
            // Bright pixels in title area are intentionally not action evidence.
            Icon(330, 226));

        Assert.False(selection.IsSuccess);
        Assert.Equal(SteamQueuePromotionSelectionFailure.NoActionIcon, selection.Failure);
    }

    [Fact]
    public void MultipleRightGutterIcons_FailsClosed()
    {
        var selection = SteamQueuePromotionSelector.Select(
            "Deadlock",
            [new SteamQueueOcrLine("Deadlock", 130, 180, 340, 220)],
            Width,
            Height,
            [.. Icon(1020, 226), .. Icon(1120, 226)]);

        Assert.False(selection.IsSuccess);
        Assert.Equal(SteamQueuePromotionSelectionFailure.AmbiguousActionIcon, selection.Failure);
    }

    [Fact]
    public void CounterStrike2_UsesExactWholeLineNormalizationAndItsOwnAction()
    {
        var selection = SteamQueuePromotionSelector.Select(
            "Counter-Strike 2",
            [
                new SteamQueueOcrLine(" counter-strike   2 ", 128, 412, 400, 456),
                new SteamQueueOcrLine("Counter-Strike 2 Dedicated Server", 128, 520, 580, 564),
            ],
            Width,
            Height,
            [
                .. Icon(1080, 466),
                .. Icon(1080, 574),
            ]);

        Assert.True(selection.IsSuccess);
        Assert.Equal(new SteamQueueClickPoint(1085, 471), selection.ClickPoint);
    }

    private static SteamQueuePixel[] Icon(int x, int y)
    {
        var pixels = new List<SteamQueuePixel>();
        for (var py = y; py < y + 11; py++)
        for (var px = x; px < x + 11; px++)
        {
            if ((px + py) % 2 == 0)
                pixels.Add(new SteamQueuePixel(px, py));
        }
        return [.. pixels];
    }
}
