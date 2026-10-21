using ExoLauncher.Helpers;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Every screen gets the 1080p layout. It fails if a 1080p screen (or a laptop under it) is zoomed at all, a 1440p or
/// 4K screen gets a zoom that doesn't bring it back to 1080 scaled pixels, or a huge one is zoomed past 3.
/// </summary>
public sealed class PageScaleTests
{
    [Theory]
    [InlineData(1080, 1)]       // 1080p at 100%
    [InlineData(864, 1)]        // 1080p at 125%
    [InlineData(1120, 1)]       // within 5%: left as it is
    [InlineData(1440, 1.333)]   // 1440p at 100%, 4K at 150%
    [InlineData(2160, 2)]       // 4K at 100%
    [InlineData(4320, 3)]       // 8K at 100%: at most 3
    public void ZoomBringsTheScreenTo1080(double screenHeight, double zoom) =>
        Assert.Equal(zoom, PageScale.ZoomFor(screenHeight));
}
