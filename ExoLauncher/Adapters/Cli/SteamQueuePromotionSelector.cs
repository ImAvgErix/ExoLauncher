using System.Globalization;
using System.Text;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// A single OCR line and its image-space bounding box. Bounds use left/top inclusive and
/// right/bottom exclusive coordinates, matching normal screen-capture APIs.
/// </summary>
public readonly record struct SteamQueueOcrLine(
    string Text,
    int Left,
    int Top,
    int Right,
    int Bottom);

/// <summary>A bright-pixel coordinate from a screenshot of Steam's Downloads view.</summary>
public readonly record struct SteamQueuePixel(int X, int Y);

/// <summary>A screen-space point that an automation host may invoke.</summary>
public readonly record struct SteamQueueClickPoint(int X, int Y);

/// <summary>Why no safe, target-scoped Downloads action was selected.</summary>
public enum SteamQueuePromotionSelectionFailure
{
    None,
    InvalidInput,
    TargetNotFound,
    AmbiguousTarget,
    NoActionIcon,
    AmbiguousActionIcon,
}

/// <summary>
/// The result of selecting a Steam Downloads action. A caller must only click when
/// <see cref="IsSuccess"/> is true; every uncertain state is deliberately a failure.
/// </summary>
public readonly record struct SteamQueuePromotionSelection(
    SteamQueuePromotionSelectionFailure Failure,
    SteamQueueClickPoint? ClickPoint)
{
    public bool IsSuccess => Failure == SteamQueuePromotionSelectionFailure.None && ClickPoint is not null;

    internal static SteamQueuePromotionSelection Success(SteamQueueClickPoint point) =>
        new(SteamQueuePromotionSelectionFailure.None, point);

    internal static SteamQueuePromotionSelection Failed(SteamQueuePromotionSelectionFailure failure) =>
        new(failure, null);
}

/// <summary>
/// Finds the one safe "Download now" click target in a Steam Downloads screenshot.
/// It intentionally has no UI or process side effects, so callers can test the exact
/// selection before asking an automation layer to invoke a point.
/// </summary>
public static class SteamQueuePromotionSelector
{
    // Steam's per-row action controls live in the far-right third of Downloads. Keeping
    // this narrow avoids mistaking title text or a generic Downloads toolbar control for
    // the selected game's action.
    private const int ActionGutterNumerator = 2;
    private const int ActionGutterDenominator = 3;

    // A modest connection radius joins the anti-aliased pixels of one icon, but does not
    // merge separate row controls. Shape bounds reject small timestamp glyphs.
    private const int ClusterJoinDistance = 3;
    private const int MinimumClusterPixels = 12;
    private const int MinimumIconWidth = 8;
    private const int MinimumIconHeight = 8;
    private const int MaximumIconWidth = 40;
    private const int MaximumIconHeight = 40;
    private const int MaximumBrightPixels = 40_000;

    /// <summary>
    /// Select an action point only when exactly one OCR row is an exact normalized title
    /// match and exactly one bright icon cluster lies in that row's right action gutter.
    /// A title occurrence elsewhere, a partial match, duplicate title rows, or multiple
    /// possible icons always fails rather than risking another game's download.
    /// </summary>
    public static SteamQueuePromotionSelection Select(
        string exactTitle,
        IReadOnlyList<SteamQueueOcrLine> ocrLines,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<SteamQueuePixel> brightPixels)
    {
        if (imageWidth <= 0 || imageHeight <= 0 ||
            ocrLines is null || brightPixels is null ||
            brightPixels.Count > MaximumBrightPixels)
            return SteamQueuePromotionSelection.Failed(SteamQueuePromotionSelectionFailure.InvalidInput);

        var target = NormalizeWholeLine(exactTitle);
        if (target.Length == 0)
            return SteamQueuePromotionSelection.Failed(SteamQueuePromotionSelectionFailure.InvalidInput);

        SteamQueueOcrLine? targetRow = null;
        foreach (var line in ocrLines)
        {
            if (!IsValidLine(line, imageWidth, imageHeight))
                return SteamQueuePromotionSelection.Failed(SteamQueuePromotionSelectionFailure.InvalidInput);

            // No Contains/StartsWith logic here: the full OCR line must be the game title.
            if (!string.Equals(NormalizeWholeLine(line.Text), target, StringComparison.Ordinal))
                continue;

            if (targetRow is not null)
                return SteamQueuePromotionSelection.Failed(SteamQueuePromotionSelectionFailure.AmbiguousTarget);
            targetRow = line;
        }

        if (targetRow is null)
            return SteamQueuePromotionSelection.Failed(SteamQueuePromotionSelectionFailure.TargetNotFound);

        var row = targetRow.Value;
        var gutterStart = (imageWidth * ActionGutterNumerator + ActionGutterDenominator - 1) /
                          ActionGutterDenominator;
        // OCR bounds cover the title glyphs, while Steam centers its icon in the
        // full row about one text-height lower. Expand only far enough to cover
        // that same row; adjacent download rows are much farther apart.
        var rowHeight = row.Bottom - row.Top;
        var rowBandTop = Math.Max(0, row.Top - 8);
        var rowBandBottom = Math.Min(imageHeight, row.Bottom + Math.Max(18, rowHeight * 2));
        var candidates = new HashSet<SteamQueuePixel>();
        foreach (var pixel in brightPixels)
        {
            if (pixel.X < 0 || pixel.X >= imageWidth || pixel.Y < 0 || pixel.Y >= imageHeight)
                return SteamQueuePromotionSelection.Failed(SteamQueuePromotionSelectionFailure.InvalidInput);

            if (pixel.X >= gutterStart && pixel.Y >= rowBandTop && pixel.Y < rowBandBottom)
                candidates.Add(pixel);
        }

        var clusters = FindIconClusters(candidates);
        return clusters.Count switch
        {
            0 => SteamQueuePromotionSelection.Failed(SteamQueuePromotionSelectionFailure.NoActionIcon),
            > 1 => SteamQueuePromotionSelection.Failed(SteamQueuePromotionSelectionFailure.AmbiguousActionIcon),
            _ => SteamQueuePromotionSelection.Success(CenterOf(clusters[0])),
        };
    }

    private static bool IsValidLine(SteamQueueOcrLine line, int imageWidth, int imageHeight) =>
        line.Text is not null &&
        line.Left >= 0 && line.Top >= 0 &&
        line.Right > line.Left && line.Bottom > line.Top &&
        line.Right <= imageWidth && line.Bottom <= imageHeight;

    private static List<List<SteamQueuePixel>> FindIconClusters(HashSet<SteamQueuePixel> pixels)
    {
        var clusters = new List<List<SteamQueuePixel>>();
        while (pixels.Count > 0)
        {
            var seed = pixels.First();
            pixels.Remove(seed);
            var queue = new Queue<SteamQueuePixel>();
            var cluster = new List<SteamQueuePixel>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                var pixel = queue.Dequeue();
                cluster.Add(pixel);
                for (var y = pixel.Y - ClusterJoinDistance; y <= pixel.Y + ClusterJoinDistance; y++)
                {
                    for (var x = pixel.X - ClusterJoinDistance; x <= pixel.X + ClusterJoinDistance; x++)
                    {
                        var neighbor = new SteamQueuePixel(x, y);
                        if (pixels.Remove(neighbor))
                            queue.Enqueue(neighbor);
                    }
                }
            }

            if (IsMeaningfulIconCluster(cluster))
                clusters.Add(cluster);
        }

        return clusters;
    }

    private static bool IsMeaningfulIconCluster(IReadOnlyList<SteamQueuePixel> cluster)
    {
        if (cluster.Count < MinimumClusterPixels) return false;
        var width = cluster.Max(p => p.X) - cluster.Min(p => p.X) + 1;
        var height = cluster.Max(p => p.Y) - cluster.Min(p => p.Y) + 1;
        return width is >= MinimumIconWidth and <= MaximumIconWidth &&
               height is >= MinimumIconHeight and <= MaximumIconHeight;
    }

    private static SteamQueueClickPoint CenterOf(IReadOnlyList<SteamQueuePixel> cluster)
    {
        long totalX = 0;
        long totalY = 0;
        foreach (var pixel in cluster)
        {
            totalX += pixel.X;
            totalY += pixel.Y;
        }

        return new SteamQueueClickPoint(
            (int)Math.Round(totalX / (double)cluster.Count, MidpointRounding.AwayFromZero),
            (int)Math.Round(totalY / (double)cluster.Count, MidpointRounding.AwayFromZero));
    }

    private static string NormalizeWholeLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var source = value.Normalize(NormalizationForm.FormKC);
        var normalized = new StringBuilder(source.Length);
        var previousWasWhitespace = false;
        foreach (var c in source)
        {
            if (char.IsWhiteSpace(c))
            {
                if (normalized.Length > 0 && !previousWasWhitespace)
                    normalized.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            normalized.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            previousWasWhitespace = false;
        }

        return normalized.ToString().Trim();
    }
}
