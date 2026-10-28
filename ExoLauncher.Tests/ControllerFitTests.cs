using static ExoLauncher.Services.ControllerLab;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo's own calibration could leave sticks worse:
/// - a center moved the wrong way, or by the other half's span;
/// - a widened range wrapping below 0 or past the controller's top;
/// - a fitted end landing past its center, or reach read from a stick that hit the end (it could have gone further);
/// - edges left uneven: one direction reaching full output only on a hard push.
/// </summary>
public sealed class ControllerFitTests
{
    // The user's Edge after its own range routine, with Exo's margin (October 26, 2026).
    private static readonly ushort[] Edge = [518, 740, 603, 718, 3340, 3445, 3420, 3432, 1959, 2080, 1992, 2082];

    [Fact]
    public void ACenterMovesTowardWhereTheStickRests()
    {
        // Left X rests 1% toward its high end; left Y 2% toward its low end; the right stick is centered.
        var next = Recentered(Edge, [1.0, -2.0, 0, 0]);
        Assert.Equal(1959 + (int)Math.Round(0.01 * (3340 - 1959)), next[8]);
        Assert.Equal(2080 - (int)Math.Round(0.02 * (2080 - 740)), next[9]);
        Assert.Equal((Edge[10], Edge[11]), (next[10], next[11]));
        Assert.Equal(Edge[..8], next[..8]);                     // ends stay
    }

    [Fact]
    public void AWidenedRangeStaysInsideTheController()
    {
        var wide = Widened(Edge, 0.2, 4095);
        Assert.Equal((int)Math.Round(1959 - (1959 - 518) * 1.2), wide[0]);
        Assert.True(wide[4] <= 4095);
        Assert.Equal(Edge[8..], wide[8..]);
        Assert.All(Widened([10, 10, 10, 10, 4090, 4090, 4090, 4090, 2048, 2048, 2048, 2048], 0.5, 4095)[..8], v => Assert.InRange(v, 0, 4095));
    }

    [Fact]
    public void EveryEdgeGetsFullOutputJustBeforeWhereTheStickReallyGoes()
    {
        var wide = Widened(Edge, 0.2, 4095);
        double[] low = [0.80, 0.86, 0.83, 0.84], high = [0.88, 0.79, 0.82, 0.85];   // uneven, as real sticks are
        var fit = Fitted(wide, low, high, EdgeMargin);
        for (var axis = 0; axis < 4; axis++)
        {
            int center = wide[axis + 8];
            // Where the stick got to, in raw units; the fitted end sits 5% of that inside it, on each side.
            var reachLow = (center - wide[axis]) * low[axis];
            var reachHigh = (wide[axis + 4] - center) * high[axis];
            Assert.InRange(center - fit[axis], reachLow * 0.94, reachLow * 0.96);
            Assert.InRange(fit[axis + 4] - center, reachHigh * 0.94, reachHigh * 0.96);
        }
    }

    [Fact]
    public void ReachIsReadOnlyFromSticksThatDidntHitTheEnd()
    {
        (byte, byte)[] rolled = [(20, 128), (230, 128), (128, 15), (128, 240)];
        var reach = Reaches(rolled, rolled)!.Value;
        Assert.Equal((127.5 - 20) / 127.5, reach.Low[0], 3);
        Assert.Equal((240 - 127.5) / 127.5, reach.High[1], 3);
        Assert.Null(Reaches([(0, 128), (230, 128), (128, 15), (128, 240)], rolled));    // hit the end: it could have gone further
        Assert.Null(Reaches([(100, 128), (150, 128), (128, 100), (128, 150)], rolled)); // never past half
    }
}
