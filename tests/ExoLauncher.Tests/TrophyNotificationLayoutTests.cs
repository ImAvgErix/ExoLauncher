using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class TrophyNotificationLayoutTests
{
    [Theory]
    [InlineData(0d, 0d, 124, 224)]
    [InlineData(0.5d, 0d, 760, 224)]
    [InlineData(1d, 0d, 1396, 224)]
    [InlineData(0d, 0.5d, 124, 542)]
    [InlineData(0.5d, 0.5d, 760, 542)]
    [InlineData(1d, 0.5d, 1396, 542)]
    [InlineData(0d, 1d, 124, 860)]
    [InlineData(0.5d, 1d, 760, 860)]
    [InlineData(1d, 1d, 1396, 860)]
    public void CanonicalAnchorsMapAcrossTheUsableWorkArea(double x, double y, int expectedLeft, int expectedTop)
    {
        var bounds = TrophyNotificationLayout.Calculate(
            workX: 100,
            workY: 200,
            workWidth: 1720,
            workHeight: 800,
            requestedWidth: 400,
            requestedHeight: 116,
            normalizedX: x,
            normalizedY: y,
            requestedMargin: 24);

        Assert.Equal(expectedLeft, bounds.Left);
        Assert.Equal(expectedTop, bounds.Top);
        Assert.Equal(400, bounds.Width);
        Assert.Equal(116, bounds.Height);
    }

    [Theory]
    [InlineData(0.24d, 0.24d, 124, 224)]
    [InlineData(0.25d, 0.74d, 760, 542)]
    [InlineData(0.75d, 0.75d, 1396, 860)]
    public void LegacyCoordinatesSnapToTheNearestCanonicalAnchor(double x, double y, int expectedLeft, int expectedTop)
    {
        var bounds = TrophyNotificationLayout.Calculate(100, 200, 1720, 800, 400, 116, x, y, 24);

        Assert.Equal(expectedLeft, bounds.Left);
        Assert.Equal(expectedTop, bounds.Top);
    }

    [Fact]
    public void OutOfRangePlacementIsClampedInsideWorkArea()
    {
        var bounds = TrophyNotificationLayout.Calculate(0, 0, 1000, 700, 432, 116, -4, 8, 24);

        Assert.Equal(24, bounds.Left);
        Assert.Equal(560, bounds.Top);
        Assert.InRange(bounds.Left + bounds.Width, 1, 1000);
        Assert.InRange(bounds.Top + bounds.Height, 1, 700);
    }

    [Fact]
    public void TinyWorkAreaStillProducesValidBounds()
    {
        var bounds = TrophyNotificationLayout.Calculate(30, 40, 220, 90, 432, 116, double.NaN, double.PositiveInfinity);

        Assert.Equal(new TrophyNotificationBounds(30, 40, 220, 90), bounds);
    }
}
