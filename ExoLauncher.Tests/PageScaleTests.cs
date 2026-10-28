using ExoLauncher.Helpers;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Every screen gets the 1080p layout. It fails if a 1080p screen (or a laptop under it) is zoomed at all, either way
/// up, a 1440p or 4K screen gets a zoom that doesn't bring it back to 1080p's size, a screen stood on end is zoomed by
/// its long side (a 1080p screen on end was laid out 607 pixels wide), or a huge one is zoomed past 3.
/// </summary>
public sealed class PageScaleTests
{
    [Theory]
    [InlineData(1920, 1080, 1)]       // 1080p at 100%
    [InlineData(1536, 864, 1)]        // 1080p at 125%
    [InlineData(1990, 1120, 1)]       // within 5%: left as it is
    [InlineData(2560, 1440, 1.333)]   // 1440p at 100%, 4K at 150%
    [InlineData(3840, 2160, 2)]       // 4K at 100%
    [InlineData(7680, 4320, 3)]       // 8K at 100%: at most 3
    [InlineData(1080, 1920, 1)]       // 1080p stood on end
    [InlineData(1440, 2560, 1.333)]   // 1440p stood on end
    [InlineData(2160, 3840, 2)]       // 4K stood on end
    [InlineData(3440, 1440, 1.333)]   // ultrawide 1440p
    [InlineData(2560, 1080, 1)]       // ultrawide 1080p
    [InlineData(1920, 1200, 1)]       // 16:10: 1080p's width, a little taller
    [InlineData(2560, 1600, 1.333)]   // 16:10 1600p: laid out 1920x1200
    public void ZoomBringsTheScreenTo1080p(double screenWidth, double screenHeight, double zoom) =>
        Assert.Equal(zoom, PageScale.ZoomFor(screenWidth, screenHeight));

    [Theory]
    [InlineData("1440", 2560, 1440)]
    [InlineData("1080x1920", 1080, 1920)]
    [InlineData(" 2560 X 1600 ", 2560, 1600)]
    public void ReadsAScreenToTry(string text, double width, double height)
    {
        Assert.True(PageScale.TryScreen(text, out var screen));
        Assert.Equal((width, height), screen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("big")]
    [InlineData("0")]
    [InlineData("1920x")]
    [InlineData("1x2x3")]
    public void IgnoresAnythingElse(string? text) =>
        Assert.False(PageScale.TryScreen(text, out _));
}
