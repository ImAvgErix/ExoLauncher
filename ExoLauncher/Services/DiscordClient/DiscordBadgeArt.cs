using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The art of Discord's Server Tag badges, read from Discord's own web client at runtime (nothing of it ships with
/// Exo). Discord draws each of its 41 badges as a 16×16 pixel-art SVG in one chunk of its client (module 620757, chunk
/// 806354, October 2026), tinting it with the colors picked. Exo finds that chunk through the chunk map at the end of
/// Discord's current web.js (a range request for its last part), fetches it once (it is named by its content, so it
/// never changes), and reads each badge's shapes and tint values out of it without running any of it. A change in
/// Discord's build that moves the chunk or reshapes the module makes this answer null, and the picker shows names.
/// </summary>
internal static partial class DiscordBadgeArt
{
    private const string Chunk = "806354", Module = "620757(A,h,v){";
    // Discord's badge names in its preset order (its GuildTagBadge enum).
    private static readonly string[] Names =
    [
        "SWORD", "WATER_DROP", "SKULL", "TOADSTOOL", "MOON", "LIGHTNING", "LEAF", "HEART", "FIRE", "COMPASS", "CROSSHAIRS", "FLOWER", "FORCE", "GEM", "LAVA",
        "PSYCHIC", "SMOKE", "SNOW", "SOUND", "SUN", "WIND", "BUNNY", "DOG", "FROG", "GOAT", "CAT", "DIAMOND", "CROWN", "TROPHY", "MONEY_BAG", "DOLLAR_SIGN",
        "CLOVER", "BLOSSOM", "POTTED_PLANT", "MAPLE", "WILTED_FLOWER", "BUTTERFLY", "SNAIL", "CATERPILLAR", "SPIDER", "BEE",
    ];
    private static readonly HashSet<string> Tags = ["path", "g", "defs", "clipPath", "rect", "radialGradient", "linearGradient", "stop"];
    private static readonly HashSet<string> Attributes =
        ["d", "fill", "opacity", "fillRule", "clipRule", "clipPath", "id", "x", "y", "width", "height", "cx", "cy", "r", "fx", "fy", "offset", "stopColor", "stopOpacity", "gradientUnits", "gradientTransform", "transform"];
    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromSeconds(20));
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static JsonObject? _art;

    /// <summary>Every badge's shapes and tint values (see <see cref="Parse"/>), or null when Discord's client can't be read.</summary>
    internal static async Task<JsonObject?> ArtAsync(CancellationToken ct)
    {
        if (_art is { } known) return known;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_art is { } again) return again;
            var html = await Http.GetStringAsync("https://discord.com/app", ct).ConfigureAwait(false);
            if (WebJs().Match(html) is not { Success: true } web) return null;
            // The chunk map sits in the last part of web.js: ask for that part alone.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://discord.com/assets/{web.Value}");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(null, 600_000);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var tail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (Regex.Match(tail, "[{,]" + Chunk + ":\"([0-9a-f]{16})\"") is not { Success: true } hash) return null;
            if (await DiscordSounds.AssetAsync("art", hash.Groups[1].Value, "js").ConfigureAwait(false) is not { } path) return null;
            var art = Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            if (art is not null) _art = art;
            else AppLog.Info("Discord's server tag badges could not be read from its client (chunk " + hash.Groups[1].Value + ").");
            return art;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            AppLog.Debug("Discord badge art not fetched: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// { badges: [{ id, nodes, primary: { lum, weights }, secondary? }] } from the chunk's source: each node is
    /// { tag, attrs, children? }; an attribute is a string, { p: i } (the i-th primary tint), { s: i } (secondary), or
    /// { id: true } / { url: true } for the badge's own gradient id. Null when the module isn't where or what it was.
    /// </summary>
    internal static JsonObject? Parse(string chunk)
    {
        var start = chunk.IndexOf(Module, StringComparison.Ordinal);
        var end = start < 0 ? -1 : chunk.IndexOf("function v7(", start, StringComparison.Ordinal);
        if (end < 0) return null;
        var module = chunk[start..];
        var badges = new JsonArray();
        foreach (Match c in Case().Matches(module[(end - start)..]))
        {
            var id = Array.IndexOf(Names, c.Groups[1].Value);
            if (id < 0 || Badge(module, c.Groups[2].Value) is not { } badge) continue;
            badge["id"] = id;
            badges.Add(badge);
        }
        return badges.Count > 0 ? new JsonObject { ["badges"] = badges } : null;
    }

    // One badge's component: its tint call (which constants feed it) and its svg's children.
    private static JsonObject? Badge(string module, string component)
    {
        var at = module.IndexOf("function " + component + "(A){", StringComparison.Ordinal);
        if (at < 0) return null;
        var tint = Tint().Match(module, at);
        if (!tint.Success || tint.Index > at + 600) return null;
        var secondary = tint.Groups["st"].Success ? tint.Groups["st"].Value : null;
        if (Svg().Match(module, tint.Index) is not { Success: true } svg) return null;
        var reader = new Reader(module, svg.Index, at, tint.Groups["pt"].Value, secondary);
        if (reader.Element() is not { } root || root["children"] is not JsonArray nodes) return null;
        var badge = new JsonObject
        {
            ["nodes"] = nodes.DeepClone(),
            ["primary"] = Ramp(module, at, tint.Groups["pl"].Value, tint.Groups["pw"].Value),
        };
        if (secondary is not null) badge["secondary"] = Ramp(module, at, tint.Groups["sl"].Value, tint.Groups["sw"].Value);
        return badge["primary"] is null || (secondary is not null && badge["secondary"] is null) ? null : badge;
    }

    // A tint's luminances ([.1,.3]) and weights ([{base:5,tint:1}]), from the module's constants of those names.
    private static JsonObject? Ramp(string module, int before, string luminances, string weights)
    {
        if (Constant(module, before, luminances) is not { } lum || Constant(module, before, weights) is not { } weight) return null;
        var lums = new JsonArray();
        foreach (Match n in Number().Matches(lum)) lums.Add(double.Parse(n.Value, CultureInfo.InvariantCulture));
        var pairs = new JsonArray();
        foreach (Match w in Weight().Matches(weight))
            pairs.Add(new JsonObject { ["base"] = double.Parse(w.Groups[1].Value, CultureInfo.InvariantCulture), ["tint"] = double.Parse(w.Groups[2].Value, CultureInfo.InvariantCulture) });
        // Discord pairs each luminance with the weight at its place; a badge may list a weight more than it uses.
        return lums.Count > 0 && pairs.Count >= lums.Count ? new JsonObject { ["lum"] = lums, ["weights"] = pairs } : null;
    }

    // A module constant (an array) declared before a component: the nearest declaration of that name above it.
    private static string? Constant(string module, int before, string name)
    {
        string? found = null;
        foreach (Match m in Regex.Matches(module, "[,; ]" + Regex.Escape(name) + @"=(\[[^\]]*\])"))
        {
            if (m.Index >= before) break;
            found = m.Groups[1].Value;
        }
        return found;
    }

    /// <summary>Reads the minified JSX calls of one svg: (0,l.jsx)("tag",{key:value,…,children:[…]}).</summary>
    private sealed class Reader(string s, int at, int component, string primary, string? secondary)
    {
        private int _i = at;

        internal JsonObject? Element()
        {
            if (!Eat("(0,") || Ident() is null || !Eat(".jsx") || !(Eat("s)(") || Eat(")("))) return null;
            var tag = Str();
            if (tag is null || !(tag == "svg" || Tags.Contains(tag)) || !Eat(",{")) return null;
            var node = new JsonObject { ["tag"] = tag };
            var attrs = new JsonObject();
            while (!Eat("}"))
            {
                if (_i >= s.Length) return null;
                if (Eat("..."))
                {
                    // A spread of the props (the svg's ...(0,M.A)(V)): skipped.
                    SkipExpression();
                }
                else
                {
                    var key = Ident();
                    if (key is null || !Eat(":")) return null;
                    if (key == "children")
                    {
                        if (Children() is not { } children) return null;
                        node["children"] = children;
                    }
                    else
                    {
                        var value = Value();
                        if (value is null) return null;
                        if (Attributes.Contains(key) && (value.GetValueKind() != JsonValueKind.String || Safe(value.GetValue<string>()))) attrs[key] = value;
                    }
                }
                Eat(",");
            }
            if (!Eat(")")) return null;
            node["attrs"] = attrs;
            return node;
        }

        // children:[element,…] or children:element.
        private JsonArray? Children()
        {
            var children = new JsonArray();
            if (!Eat("["))
            {
                var only = Element();
                return only is null ? null : new JsonArray(only);
            }
            while (!Eat("]"))
            {
                if (Element() is not { } child) return null;
                children.Add(child);
                Eat(",");
            }
            return children;
        }

        // "text" | `url(#${id})` | a number | ident[n] | ident?.[n]??value | ident (the badge's gradient id).
        private JsonNode? Value()
        {
            if (Peek('"')) return Str();
            if (Eat("`url(#${"))
                return Ident() is not null && Eat("})`") ? new JsonObject { ["url"] = true } : null;
            if (NumberAt().Match(s, _i) is { Success: true } n)
            {
                _i += n.Length;
                return n.Value;
            }
            var name = Ident();
            if (name is null) return null;
            if (!Eat("?.[") && !Eat("[")) return new JsonObject { ["id"] = true };
            var index = NumberAt().Match(s, _i);
            if (!index.Success) return null;
            _i += index.Length;
            if (!Eat("]")) return null;
            var slot = Slot(name, int.Parse(index.Value, CultureInfo.InvariantCulture));
            // A fallback for a missing tint (??"#FD7531"): the picker always has both, so the tint wins.
            if (Eat("??") && Value() is null) return null;
            return slot ?? "none";
        }

        // A tint's i-th color, or a fixed color from a module constant (a base color array).
        private JsonNode? Slot(string name, int index)
        {
            if (name == primary) return new JsonObject { ["p"] = index };
            if (name == secondary) return new JsonObject { ["s"] = index };
            if (Constant(s, component, name) is not { } list) return null;
            var colors = Color().Matches(list);
            return index < colors.Count ? colors[index].Groups[1].Value : null;
        }

        // A d path, a color, an id or a length: never markup or script.
        private static bool Safe(string text) =>
            text.Length <= 4000 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or ',' or '-' or '#' or '%' or '(' or ')' or '_');

        private bool Peek(char c) => _i < s.Length && s[_i] == c;

        private bool Eat(string text)
        {
            if (string.CompareOrdinal(s, _i, text, 0, text.Length) != 0) return false;
            _i += text.Length;
            return true;
        }

        private string? Ident()
        {
            var from = _i;
            while (_i < s.Length && (char.IsAsciiLetterOrDigit(s[_i]) || s[_i] is '_' or '$')) _i++;
            return _i > from ? s[from.._i] : null;
        }

        private string? Str()
        {
            if (!Eat("\"")) return null;
            var text = new StringBuilder();
            while (_i < s.Length && s[_i] != '"')
            {
                if (s[_i] == '\\') return null;
                text.Append(s[_i++]);
            }
            return Eat("\"") ? text.ToString() : null;
        }

        // Past one expression, up to the comma or close brace that ends it.
        private void SkipExpression()
        {
            var depth = 0;
            for (; _i < s.Length; _i++)
            {
                var c = s[_i];
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']') depth--;
                else if (c == '}') { if (depth == 0) return; depth--; }
                else if (c == ',' && depth == 0) return;
            }
        }
    }

    [GeneratedRegex(@"web\.[0-9a-f]{16}\.js")]
    private static partial Regex WebJs();

    [GeneratedRegex(@"case [\w$]+\.bl\.([A-Z_]+):return\(0,[\w$]+\.jsx\)\(([\w$]+),")]
    private static partial Regex Case();

    [GeneratedRegex(@"\{primaryColorsTransformed:(?<pt>[\w$]+)(?:,secondaryColorsTransformed:(?<st>[\w$]+))?\}=[\w$]+\(\{primaryBaseColors:[\w$]+,primaryTintColor:[\w$]+,primaryTintLuminances:(?<pl>[\w$]+),primaryLuminanceWeights:(?<pw>[\w$]+)(?:,secondaryBaseColors:[\w$]+,secondaryTintColor:[\w$]+,secondaryTintLuminances:(?<sl>[\w$]+),secondaryLuminanceWeights:(?<sw>[\w$]+))?\}\)")]
    private static partial Regex Tint();

    [GeneratedRegex(@"\(0,[\w$]+\.jsxs\)\(""svg"",\{")]
    private static partial Regex Svg();

    [GeneratedRegex(@"""(#[0-9A-Fa-f]{3,8})""")]
    private static partial Regex Color();

    [GeneratedRegex(@"-?\d*\.?\d+")]
    private static partial Regex Number();

    [GeneratedRegex(@"\G-?\d*\.?\d+")]
    private static partial Regex NumberAt();

    [GeneratedRegex(@"\{base:(-?\d*\.?\d+),tint:(-?\d*\.?\d+)\}")]
    private static partial Regex Weight();
}
