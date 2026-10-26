using ExoLauncher.Helpers;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the SDR-in-HDR profile could go wrong without anyone seeing it: the same screen gives a different profile
/// file each second (its name is a hash of its content), so Apply reinstalls and re-associates it on every look;
/// the curve falls somewhere or lifts above SDR white; Windows' color system refuses the profile.
/// </summary>
public sealed class SdrInHdrTests
{
    [Fact]
    public void TheSameScreenMakesTheSameProfileAtAnyTime()
    {
        var first = SdrInHdr.Generate(240, 0.05, 800);
        Thread.Sleep(1100); // across a clock second: a timestamp in the header would show here
        Assert.Equal(first, SdrInHdr.Generate(240, 0.05, 800));
        Assert.NotEqual(first, SdrInHdr.Generate(200, 0.05, 800));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(80.0)]
    [InlineData(203.0)]
    [InlineData(1000.0)]
    [InlineData(10000.0)]
    public void PqRoundTrips(double nits) => Assert.Equal(nits, SdrInHdr.PqToNits(SdrInHdr.NitsToPq(nits)), 6);

    [Fact]
    public void HdrAboveSdrWhiteIsUntouched()
    {
        const double white = 200;
        foreach (var nits in new[] { 200.0, 400, 1000, 4000 })
        {
            var pq = SdrInHdr.NitsToPq(nits);
            Assert.Equal(pq, SdrInHdr.Map(pq, white), 9);
        }
    }

    [Fact]
    public void ShadowsBelowSdrWhiteGetDarkerLikeGammaTwoTwo()
    {
        const double white = 200;
        // 1% of SDR white through sRGB: gamma 2.2 puts it lower, the fix for lifted, grey blacks.
        var pq = SdrInHdr.NitsToPq(2);
        var mapped = SdrInHdr.PqToNits(SdrInHdr.Map(pq, white));
        Assert.True(mapped < 2, $"expected darker than 2 nits, got {mapped}");
        Assert.True(mapped > 0);
        Assert.Equal(white, SdrInHdr.PqToNits(SdrInHdr.Map(SdrInHdr.NitsToPq(white * 0.999999), white)), 0);
    }

    [Fact]
    public void CurveRisesEverywhere()
    {
        var last = -1.0;
        for (var i = 0; i < 1024; i++)
        {
            var value = SdrInHdr.Map(i / 1023.0, 240);
            Assert.True(value >= last, $"falls at {i}");
            last = value;
        }
    }

    [Fact]
    public void ProfileCarriesItsLuminanceAndWindowsAcceptsIt()
    {
        var icc = SdrInHdr.Generate(240, 0.05, 800);
        var lum = SdrInHdr.Luminance(icc);
        Assert.NotNull(lum);
        Assert.Equal(0.05, lum!.Value.Min, 3);
        Assert.Equal(800, lum.Value.Max, 3);
        Assert.True(SdrInHdr.IsValid(icc), "Windows' color system rejected the profile");
    }
}
