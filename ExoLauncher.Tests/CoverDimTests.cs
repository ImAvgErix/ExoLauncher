using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// CoverArtService.DimPixel — the not-installed look baked into grid thumbnails
/// (&amp;dim=1). Ways this could fail silently:
/// - wrong saturation weights (any matrix but the CSS spec's 0.2126/0.7152/0.0722
///   dim cards differently from the filter they replaced, and nobody notices a
///   few percent of color),
/// - applied in the wrong space (linear light instead of the sRGB-encoded values
///   CSS filters use, which dims mid-tones far less),
/// - order flipped (brightness before saturate changes nothing here — brightness
///   is a scalar multiply — but a future matrix that isn't might rely on it),
/// - channels swapped (the pixels are BGRA; r/b transposed shows as tinted art),
/// - rounding drift (a floor where the spec's round-half-away is expected shifts
///   flat colors by one step).
/// Expected values are the CSS feColorMatrix saturate(0.45) followed by
/// brightness(0.42), evaluated by hand for each channel.
/// </summary>
public sealed class CoverDimTests
{
    [Theory]
    [InlineData(255, 255, 255, 107, 107, 107)] // white stays gray: every row sums to 0.42
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(255, 0, 0, 61, 13, 13)]        // pure red drains to a dim red
    [InlineData(0, 255, 0, 42, 90, 42)]
    [InlineData(0, 0, 255, 4, 4, 52)]
    [InlineData(128, 64, 200, 44, 32, 58)]
    public void DimPixelMatchesCssFilterMath(byte r, byte g, byte b, byte er, byte eg, byte eb)
    {
        var (R, G, B) = CoverArtService.DimPixel(r, g, b);
        Assert.Equal((er, eg, eb), (R, G, B));
    }
}
