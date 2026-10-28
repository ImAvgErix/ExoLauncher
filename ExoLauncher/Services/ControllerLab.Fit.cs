namespace ExoLauncher.Services;

/// <summary>
/// Exo's own calibration on top of the controller's: centers corrected from what Exo measures at rest until each axis
/// is within half an 8-bit step, and on an Edge each axis's two ends fitted to how far the sticks really go, with the
/// range first opened wider so a push never hits the end while Exo measures. The fine-tune values are each axis's low
/// end (left X, left Y, right X, right Y), then their high ends, then their centers; every function keeps an end on its
/// side of the center. Covered by ControllerFitTests.
/// </summary>
internal static partial class ControllerLab
{
    /// <summary>An axis at rest within this of its center (% of its half travel) is centered: half of one 8-bit step.</summary>
    internal const double CenterTolerance = 0.4;

    /// <summary>
    /// The centers moved by what Exo measured at rest: each axis's offset (% of its half travel, in left X, left Y,
    /// right X, right Y order), positive toward its high end, taken from the half of the range it sits in.
    /// </summary>
    internal static ushort[] Recentered(IReadOnlyList<ushort> values, IReadOnlyList<double> offsets)
    {
        var next = values.ToArray();
        for (var axis = 0; axis < 4; axis++)
        {
            int low = values[axis], high = values[axis + 4], center = values[axis + 8];
            var off = offsets[axis] / 100;
            var span = off >= 0 ? high - center : center - low;
            next[axis + 8] = (ushort)Math.Clamp((int)Math.Round(center + off * span), low + 1, high - 1);
        }
        return next;
    }

    /// <summary>Each end pushed <paramref name="by"/> of its half range further from the center, at most to 0 and <paramref name="top"/>.</summary>
    internal static ushort[] Widened(IReadOnlyList<ushort> values, double by, int top)
    {
        var next = values.ToArray();
        for (var axis = 0; axis < 4; axis++)
        {
            int low = values[axis], high = values[axis + 4], center = values[axis + 8];
            next[axis] = (ushort)Math.Max(0, (int)Math.Round(center - (center - low) * (1 + by)));
            next[axis + 4] = (ushort)Math.Min(top, (int)Math.Round(center + (high - center) * (1 + by)));
        }
        return next;
    }

    /// <summary>
    /// How far each stick went toward each end of each axis while rolled (share of the half travel, 0-1, in left X, left
    /// Y, right X, right Y order); null when one end was hit (the range wasn't wide enough to measure it) or not reached
    /// past half.
    /// </summary>
    internal static (double[] Low, double[] High)? Reaches(IReadOnlyList<(byte X, byte Y)> left, IReadOnlyList<(byte X, byte Y)> right)
    {
        if (left.Count == 0 || right.Count == 0) return null;
        var low = new double[4];
        var high = new double[4];
        foreach (var (axis, values) in new[] { (0, left.Select(s => s.X)), (1, left.Select(s => s.Y)), (2, right.Select(s => s.X)), (3, right.Select(s => s.Y)) })
        {
            var list = values.ToArray();
            low[axis] = (127.5 - list.Min()) / 127.5;
            high[axis] = (list.Max() - 127.5) / 127.5;
        }
        return low.Concat(high).Any(r => r is >= 0.999 or < 0.5) ? null : (low, high);
    }

    /// <summary>
    /// The ends fitted to the reach measured on <paramref name="wide"/>: each where the stick really got to, brought
    /// <paramref name="margin"/> in, so a normal push gives full output, the same in every direction.
    /// </summary>
    internal static ushort[] Fitted(IReadOnlyList<ushort> wide, IReadOnlyList<double> low, IReadOnlyList<double> high, double margin)
    {
        var next = wide.ToArray();
        for (var axis = 0; axis < 4; axis++)
        {
            int center = wide[axis + 8];
            next[axis] = (ushort)Math.Clamp((int)Math.Round(center - (center - wide[axis]) * low[axis] * (1 - margin)), 0, center - 1);
            next[axis + 4] = (ushort)Math.Max(center + 1, (int)Math.Round(center + (wide[axis + 4] - center) * high[axis] * (1 - margin)));
        }
        return next;
    }
}
