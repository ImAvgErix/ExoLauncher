using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways System > Shaders' queue time could be wrong (it is summed from these):
/// - it ignores how long the game's last compile on this PC took, or the engine's estimate;
/// - a game without a plan yet drops out of the total, so the queue reads shorter than it is;
/// - a zero or missing time counts as a real one;
/// - one outlier game sets the guess for every unknown one (a mean instead of a median);
/// - with nothing measured at all it invents a time.
/// </summary>
public sealed class ShaderCompileTimeTests
{
    private static Dictionary<string, double> Seconds(params (string Id, TimeSpan? Last, TimeSpan? Estimated, long? Shaders)[] games) =>
        ScsKillerStatus.CompileSeconds(games);

    [Fact]
    public void TheLastCompileWinsOverTheEstimate()
    {
        var seconds = Seconds(("a", TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(6), 1000), ("b", null, TimeSpan.FromMinutes(20), null));
        Assert.Equal(240, seconds["a"]);
        Assert.Equal(1200, seconds["b"]);
    }

    [Fact]
    public void AGameWithoutAPlanIsSizedFromItsShadersAtTheMedianRate()
    {
        var seconds = Seconds(
            ("a", null, TimeSpan.FromSeconds(100), 1000),    // 0.1 s per shader
            ("b", null, TimeSpan.FromSeconds(300), 1000),    // 0.3
            ("c", null, TimeSpan.FromSeconds(20_000), 1000), // an outlier: 20
            ("new", null, null, 5000));
        Assert.Equal(1500, seconds["new"]);
    }

    [Fact]
    public void AGameWithNothingKnownTakesTheMedianGame()
    {
        var seconds = Seconds(("a", null, TimeSpan.FromMinutes(2), null), ("b", null, TimeSpan.FromMinutes(10), null),
            ("c", null, TimeSpan.FromHours(5), null), ("new", null, null, null));
        Assert.Equal(600, seconds["new"]);
    }

    [Fact]
    public void ZeroTimesAreNotMeasurements()
    {
        var seconds = Seconds(("a", TimeSpan.Zero, TimeSpan.FromMinutes(3), null), ("b", TimeSpan.Zero, TimeSpan.Zero, null));
        Assert.Equal(180, seconds["a"]);
        Assert.Equal(180, seconds["b"]);
    }

    [Fact]
    public void NothingMeasuredMeansNoTimes()
    {
        Assert.Empty(Seconds(("a", null, null, 1000), ("b", null, null, null)));
    }
}
