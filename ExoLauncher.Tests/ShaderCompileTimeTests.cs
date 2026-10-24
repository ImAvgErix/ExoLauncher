using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways System > Shaders' queue time could be wrong (it fills in these where the engine's own estimate is missing):
/// - it ignores the engine's estimate (the rate this game's cold compiles measured, reading and preparing included);
/// - it reads the last compile's own time, which after a rewarm onto a cache that still held the game is seconds;
/// - a game without a plan yet drops out of the total, so the queue reads shorter than it is;
/// - a zero or missing time counts as a real one;
/// - one outlier game sets the guess for every unknown one (a mean instead of a median);
/// - with nothing measured at all it invents a time.
/// </summary>
public sealed class ShaderCompileTimeTests
{
    private static Dictionary<string, double> Seconds(params (string Id, TimeSpan? Estimated, long? Shaders)[] games) =>
        ScsKillerStatus.CompileSeconds(games);

    [Fact]
    public void TheEnginesEstimateIsTheTime()
    {
        var seconds = Seconds(("a", TimeSpan.FromMinutes(6), 1000), ("b", TimeSpan.FromMinutes(20), null));
        Assert.Equal(360, seconds["a"]);
        Assert.Equal(1200, seconds["b"]);
    }

    [Fact]
    public void AGameWithoutAPlanIsSizedFromItsShadersAtTheMedianRate()
    {
        var seconds = Seconds(
            ("a", TimeSpan.FromSeconds(100), 1000),    // 0.1 s per shader
            ("b", TimeSpan.FromSeconds(300), 1000),    // 0.3
            ("c", TimeSpan.FromSeconds(20_000), 1000), // an outlier: 20
            ("new", null, 5000));
        Assert.Equal(1500, seconds["new"]);
    }

    [Fact]
    public void AGameWithNothingKnownTakesTheMedianGame()
    {
        var seconds = Seconds(("a", TimeSpan.FromMinutes(2), null), ("b", TimeSpan.FromMinutes(10), null),
            ("c", TimeSpan.FromHours(5), null), ("new", null, null));
        Assert.Equal(600, seconds["new"]);
    }

    [Fact]
    public void ZeroTimesAreNotMeasurements()
    {
        var seconds = Seconds(("a", TimeSpan.FromMinutes(3), null), ("b", TimeSpan.Zero, null));
        Assert.Equal(180, seconds["a"]);
        Assert.Equal(180, seconds["b"]);
    }

    [Fact]
    public void NothingMeasuredMeansNoTimes()
    {
        Assert.Empty(Seconds(("a", null, 1000), ("b", null, null)));
    }
}
