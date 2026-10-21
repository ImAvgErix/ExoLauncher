using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ExoLauncher.Helpers;

/// <summary>
/// Spotify's optimization: the rules that paint Spotify's color washes
/// and fixed grey surfaces (<see cref="Surface"/>), found in the stylesheets
/// being patched. Spotify's class names are hashed
/// per build, so the overrides are derived from the build on disk rather than
/// written by hand; every Spotify update re-derives them.
///
/// A gradient layer is a wash, and is dropped, when it has a colored stop,
/// reads a per-page color variable (artwork colors, the wash utility, noise),
/// or fades to one of Spotify's grey bases (#121212 and its neighbours), which
/// shows as a grey band on true black. Kept: black or transparent scrims
/// (text over artwork), fades to the base tokens Exo already made black,
/// flat tints (identical stops), light greys (drag guides, rings), animated
/// loading shimmers, masks, and conic or repeating gradients (progress rings,
/// guides). Image layers in the same declaration stay.
/// </summary>
internal static class SpotifyGradients
{
    private static readonly Regex Rule = new(@"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex Color = new(
        @"#[0-9a-fA-F]{3,8}\b|rgba?\([^)]*\)|oklch\([^)]*\)|\btransparent\b|\bblack\b|\bwhite\b|var\(--[\w-]+",
        RegexOptions.Compiled);

    /// Per-page and decorative color variables: a gradient reading one is a wash.
    private static readonly string[] WashVariables =
    [
        "--color-from", "--color-to", "--bg-color", "--radial-color", "--gradient-color", "--cinema-mode",
        "--background-color", "--extracted", "--edge-glow", "--core-color", "--onboarding", "--carousel",
        "--background-noise", "--bgColor", "--window-header-bg", "--lyrics-color", "--card-color",
        "--home-chip-highlight", "--essential-bright-accent", "--text-bright-accent", "--decorative",
    ];

    /// Surface tokens Exo's patch pins to #000: a fade to one is a fade to black.
    private static readonly string[] BlackVariables =
    [
        "--background-base", "--background-elevated-base", "--background-tinted-base",
        "--background-unsafe-for-small-text",
    ];

    internal static string Overrides(IEnumerable<string> stylesheets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new StringBuilder();
        foreach (var css in stylesheets)
        {
            foreach (Match rule in Rule.Matches(StripComments(css)))
            {
                var selector = rule.Groups["selector"].Value;
                // A preceding @charset/@import statement ends at its semicolon.
                selector = selector[(selector.LastIndexOf(';') + 1)..].Trim();
                if (!IsStyleSelector(selector)) continue;
                var body = rule.Groups["body"].Value;
                // Loading shimmers loop forever; they only show while content
                // loads and carry no color.
                if (body.Contains("infinite", StringComparison.Ordinal)) continue;
                foreach (var (property, value) in Declarations(body))
                {
                    if (property is "background" or "background-color")
                        foreach (var part in SplitTopLevel(selector, ','))
                            if (Surface(part.Trim(), body, value) is { } surface && seen.Add(part.Trim() + "{" + surface + "}"))
                                output.Append(part.Trim()).Append('{').Append(surface).Append("}\n");
                    if (property is not ("background" or "background-image")) continue;
                    if (!value.Contains("gradient(", StringComparison.Ordinal) &&
                        !value.Contains("var(--dither", StringComparison.Ordinal) &&
                        !value.Contains("var(--background-noise", StringComparison.Ordinal) &&
                        !value.Contains("data:image/svg+xml;base64,", StringComparison.Ordinal)) continue;
                    if (Reduce(value, property == "background-image") is not { } kept) continue;
                    var line = selector + "{background-image:" + kept + "!important}";
                    if (seen.Add(line)) output.Append(line).Append('\n');
                }
            }
        }
        return output.ToString();
    }

    private static readonly Regex StateSelector = new(
        @":(hover|focus|focus-within|focus-visible|active)\b|\[data-context-menu-open", RegexOptions.Compiled);
    private static readonly Regex SelectedSelector = new(
        @":checked\b|\[aria-(selected|checked|current|expanded|pressed)|\[data-(active|selected)", RegexOptions.Compiled);
    private static readonly Regex Size = new(
        @"(?<![\w-])(width|height|inline-size|block-size)\s*:\s*(?<px>\d+(\.\d+)?)px", RegexOptions.Compiled);

    /// <summary>
    /// A surface Spotify paints with a fixed grey instead of a token (menus,
    /// popovers, dialogs, tooltips, hover rows, artwork placeholders), or with
    /// the elevated token Exo darkens: the color it gets on true black.
    /// Base greys (#0a0a0a–#1e1e1e) become #000 and raised greys
    /// (#1f1f1f–#2a2a2a) #0d0d0d, with Exo's #1c1c1c hairline on rounded
    /// boxes so a card or menu keeps its edge. Hover and focus are #141414,
    /// a selected item #1c1c1c, and a one- or two-pixel line #1c1c1c, so
    /// nothing that marked a state or a divider turns invisible. Lighter
    /// greys (slider tracks, scrollbar thumbs, the off state of a toggle)
    /// and colors are left alone.
    /// </summary>
    private static string? Surface(string selector, string body, string value)
    {
        if (selector.Length == 0) return null;
        var pseudo = selector.Contains("::", StringComparison.Ordinal) ||
            selector.Contains(":before", StringComparison.Ordinal) || selector.Contains(":after", StringComparison.Ordinal);
        var image = body.Contains("background-size", StringComparison.Ordinal) || body.Contains("background-position", StringComparison.Ordinal);
        var rounded = body.Contains("border-radius", StringComparison.Ordinal);
        if (value.StartsWith("var(--background-elevated-base", StringComparison.Ordinal))
        {
            // Exo pins the token itself; only the edge is added here.
            return !pseudo && !image && rounded && !StateSelector.IsMatch(selector) && !SelectedSelector.IsMatch(selector) &&
                Smallest(body) is not (> 0 and <= 16) ? "box-shadow:inset 0 0 0 1px #1c1c1c" : null;
        }
        if (value.Contains('(') && !value.StartsWith("rgb", StringComparison.Ordinal)) return null;
        if (Parse(value.Trim().ToLowerInvariant()) is not { } color) return null;
        var (r, g, b, a) = color;
        var hi = Math.Max(r, Math.Max(g, b));
        var lo = Math.Min(r, Math.Min(g, b));
        if (hi - lo > 8 || a < 0xb0 || hi < 0x0a || hi > 0x2a) return null;
        if (StateSelector.IsMatch(selector)) return "background-color:#141414!important";
        if (SelectedSelector.IsMatch(selector)) return "background-color:#1c1c1c!important";
        if (Smallest(body) is > 0 and <= 2) return "background-color:#1c1c1c!important";
        if (hi < 0x1f) return "background-color:#000!important";
        return !pseudo && !image && rounded && Smallest(body) is not (> 0 and <= 16)
            ? "background-color:#0d0d0d!important;box-shadow:inset 0 0 0 1px #1c1c1c"
            : "background-color:#0d0d0d!important";
    }

    /// The smallest width or height the rule sets in pixels; 0 when it sets none.
    private static double Smallest(string body)
    {
        var smallest = 0d;
        foreach (Match size in Size.Matches(body))
        {
            var px = double.Parse(size.Groups["px"].Value, CultureInfo.InvariantCulture);
            if (smallest == 0 || px < smallest) smallest = px;
        }
        return smallest;
    }

    private static string StripComments(string css) => Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    private static bool IsStyleSelector(string selector) =>
        selector.Length > 0 && selector[0] != '@' && !selector.Contains(';') &&
        !Regex.IsMatch(selector, @"^(from|to|[\d.%\s,]+)$");

    private static IEnumerable<(string Property, string Value)> Declarations(string body)
    {
        foreach (var part in SplitTopLevel(body, ';'))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var value = part[(colon + 1)..].Trim();
            if (value.EndsWith("!important", StringComparison.Ordinal)) continue;
            yield return (part[..colon].Trim(), value);
        }
    }

    /// The image layers to keep, or null when nothing is a wash.
    private static string? Reduce(string value, bool imageProperty)
    {
        var layers = SplitTopLevel(value, ',');
        var kept = new List<string>();
        var dropped = false;
        foreach (var layer in layers)
        {
            foreach (var image in Images(layer, imageProperty))
            {
                if (IsWash(image)) { dropped = true; continue; }
                kept.Add(image);
            }
        }
        if (!dropped) return null;
        return kept.Count == 0 ? "none" : string.Join(",", kept);
    }

    /// The image functions in one layer: gradients, url(), and (only in
    /// background-image, where a variable is always an image) var().
    private static IEnumerable<string> Images(string layer, bool imageProperty)
    {
        for (var i = 0; i < layer.Length; i++)
        {
            var open = layer.IndexOf('(', i);
            if (open < 0) yield break;
            var nameStart = open;
            while (nameStart > 0 && (char.IsLetterOrDigit(layer[nameStart - 1]) || layer[nameStart - 1] == '-')) nameStart--;
            var name = layer[nameStart..open];
            var close = Close(layer, open);
            if (close < 0) yield break;
            i = close;
            var inner = layer[open..close];
            if (name.EndsWith("gradient", StringComparison.Ordinal) || name == "url" ||
                (name == "var" && (imageProperty || inner.Contains("image", StringComparison.Ordinal) ||
                                   inner.StartsWith("(--dither", StringComparison.Ordinal) ||
                                   inner.StartsWith("(--background-noise", StringComparison.Ordinal))))
                yield return layer[nameStart..(close + 1)];
        }
    }

    private static int Close(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool IsWash(string image)
    {
        // Spotify's film grain: the noise variable, the wash utility's dither
        // slot that carries it, and the inline SVG turbulence it is made of.
        if (image.StartsWith("var(--dither", StringComparison.Ordinal) ||
            image.StartsWith("var(--background-noise", StringComparison.Ordinal))
            return true;
        if (image.StartsWith("url(", StringComparison.Ordinal)) return IsNoise(image);
        if (!image.Contains("gradient(", StringComparison.Ordinal)) return false;
        if (image.StartsWith("conic", StringComparison.Ordinal) || image.StartsWith("repeating", StringComparison.Ordinal))
            return false;
        var stops = new List<string>();
        foreach (Match color in Color.Matches(image))
        {
            var token = color.Value;
            if (token.StartsWith("var(", StringComparison.Ordinal))
            {
                if (WashVariables.Any(prefix => token.StartsWith("var(" + prefix, StringComparison.Ordinal))) return true;
                if (BlackVariables.Any(prefix => token.StartsWith("var(" + prefix, StringComparison.Ordinal))) { stops.Add("black"); continue; }
                continue; // layout variables (sizes, positions) are not stops
            }
            stops.Add(token.ToLowerInvariant());
        }
        if (stops.Count == 0) return false;
        var parsed = stops.Select(Parse).ToList();
        if (parsed.Any(stop => stop is null)) return true;
        // A tint or band: one color at visible alphas throughout (hover
        // tints, hairlines drawn as gradients, highlighted segments).
        if (stops.Count > 1 && parsed.All(stop => stop!.Value.A > 0 &&
                (stop.Value.R, stop.Value.G, stop.Value.B) == (parsed[0]!.Value.R, parsed[0]!.Value.G, parsed[0]!.Value.B)))
            return false;
        foreach (var stop in parsed)
        {
            if (stop is not { } rgba) return true;
            var (r, g, b, a) = rgba;
            if (a == 0) continue;
            var hi = Math.Max(r, Math.Max(g, b));
            var lo = Math.Min(r, Math.Min(g, b));
            if (hi - lo > 8) return true;          // colored
            if (hi > 0 && hi <= 0x30) return true; // Spotify's grey bases
        }
        return false;
    }

    private static bool IsNoise(string url)
    {
        const string prefix = "url(data:image/svg+xml;base64,";
        if (!url.StartsWith(prefix, StringComparison.Ordinal)) return false;
        try
        {
            var svg = Encoding.UTF8.GetString(Convert.FromBase64String(url[prefix.Length..^1].Trim('"', '\'')));
            return svg.Contains("feTurbulence", StringComparison.Ordinal);
        }
        catch (FormatException) { return false; }
    }

    private static (int R, int G, int B, int A)? Parse(string token)
    {
        if (token is "transparent") return (0, 0, 0, 0);
        if (token is "black") return (0, 0, 0, 255);
        if (token is "white") return (255, 255, 255, 255);
        if (token.StartsWith('#'))
        {
            var hex = token[1..];
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(c => new string(c, 2)));
            if (hex.Length is not (6 or 8)) return null;
            int Byte(int at) => int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (Byte(0), Byte(2), Byte(4), hex.Length == 8 ? Byte(6) : 255);
        }
        var numbers = Regex.Matches(token, @"[\d.]+%?").Select(m => m.Value).ToArray();
        if (token.StartsWith("rgb", StringComparison.Ordinal) && numbers.Length >= 3)
        {
            int Channel(string v) => v.EndsWith('%')
                ? (int)(double.Parse(v[..^1], CultureInfo.InvariantCulture) * 2.55)
                : (int)double.Parse(v, CultureInfo.InvariantCulture);
            var alpha = numbers.Length > 3 ? Alpha(numbers[3]) : 255;
            return (Channel(numbers[0]), Channel(numbers[1]), Channel(numbers[2]), alpha);
        }
        if (token.StartsWith("oklch", StringComparison.Ordinal) && numbers.Length >= 3)
        {
            // Only the class matters: black, a dark grey base, a light grey,
            // or colored (any chroma).
            var lightness = numbers[0].EndsWith('%')
                ? double.Parse(numbers[0][..^1], CultureInfo.InvariantCulture) / 100
                : double.Parse(numbers[0], CultureInfo.InvariantCulture);
            var chroma = double.Parse(numbers[1].TrimEnd('%'), CultureInfo.InvariantCulture);
            var alpha = numbers.Length > 3 ? Alpha(numbers[3]) : 255;
            if (chroma > 0.01) return (255, 0, 0, alpha);
            var grey = lightness <= 0 ? 0 : lightness < 0.35 ? 0x20 : 0x80;
            return (grey, grey, grey, alpha);
        }
        return null;
    }

    private static int Alpha(string value) => value.EndsWith('%')
        ? (int)(double.Parse(value[..^1], CultureInfo.InvariantCulture) * 2.55)
        : (int)(double.Parse(value, CultureInfo.InvariantCulture) * 255);

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }
}
