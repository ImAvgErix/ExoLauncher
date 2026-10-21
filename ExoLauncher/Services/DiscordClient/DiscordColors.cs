namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Role colors for names on Exo's black. Discord draws names on gray, where a near-black role color
/// still shows; on pure black it vanishes. A color too dark to read is lifted toward Exo's white ink just
/// until it reads (3:1 against black); every other color is exactly the role's.
/// </summary>
internal static class DiscordColors
{
    private const double MinLuminance = 0.1; // (L + 0.05) / 0.05 = 3:1 against black

    internal static string Readable(int color)
    {
        var (r, g, b) = ((color >> 16) & 0xff, (color >> 8) & 0xff, color & 0xff);
        for (var t = 0.0; t <= 1.0; t += 0.05)
        {
            int Mix(int from, int to) => (int)Math.Round(from + (to - from) * t);
            var (mr, mg, mb) = (Mix(r, 255), Mix(g, 255), Mix(b, 255));
            if (Luminance(mr, mg, mb) >= MinLuminance) return $"#{mr:x2}{mg:x2}{mb:x2}";
        }
        return "#ffffff";
    }

    private static double Luminance(int r, int g, int b)
    {
        static double Channel(int c) { var s = c / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }
}
