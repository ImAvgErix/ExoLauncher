using System.Globalization;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// A game's requirements against this PC: each row in short ("GTX 1660 · RX 590"
/// for "Nvidia GTX 1660, Radeon RX 590"), this PC's part beside it, whether it
/// meets Minimum or Recommended, and one verdict for the whole PC.
/// Graphics cards and processors are placed on one rough performance scale per
/// kind (a GTX 1060 is 100; an i5-8400 is 78), so a part is compared with the
/// same maker's part the game names when it names one. A row it cannot read
/// keeps Steam's own words and is not judged.
/// </summary>
internal sealed partial class StoreService
{
    /// <summary>This PC as the requirements see it; null or zero where it is not known.</summary>
    /// <remarks>DirectX is the highest version the graphics card runs (12 or 11); Online, whether Windows has a network.</remarks>
    internal sealed record ThisPc(string? Cpu, string? Gpu, int RamGb, int Windows, double? FreeGb, double? VramGb = null, int DirectX = 0, bool? Online = null);

    /// <summary>One row as shown: the requirement in short, this PC's part, and "recommended", "minimum", "below" or null (not judged).</summary>
    internal sealed record SpecRow(string Label, string? Minimum, string? Recommended, string? Mine, string? Meets);

    internal sealed record SpecFit(IReadOnlyList<SpecRow> Rows, string? Verdict);

    private sealed record Part(string Name, string Maker, double Score);

    private static readonly string[] Shown = ["OS", "Processor", "Graphics", "Memory", "Storage", "DirectX", "Network", "VR Support"];

    internal static SpecFit FitSpecs(IReadOnlyList<Spec> specs, ThisPc? pc)
    {
        var notes = specs.FirstOrDefault(s => s.Label == "Additional Notes");
        var rows = specs.Where(s => s.Label is not ("Additional Notes" or "Sound Card")).ToList();
        // "SSD with 161 GB available space at launch" in the notes, and no storage line.
        if (!rows.Any(s => s.Label == "Storage") && (Size(notes?.Minimum) is not null || Size(notes?.Recommended) is not null))
            rows.Add(new Spec("Storage", Size(notes?.Minimum) is null ? null : notes!.Minimum, Size(notes?.Recommended) is null ? null : notes!.Recommended));
        var hasRecommended = rows.Any(s => s.Recommended is not null);
        var judged = new List<int>();
        var outRows = new List<SpecRow>();
        foreach (var s in rows.OrderBy(s => Array.IndexOf(Shown, s.Label) is var i and >= 0 ? i : Shown.Length))
        {
            var recommended = s.Recommended ?? (hasRecommended ? s.Minimum : null);
            var (minShort, minNeed) = Read(s.Label, s.Minimum, notes?.Minimum, pc);
            var (recShort, recNeed) = Read(s.Label, recommended, notes?.Recommended ?? notes?.Minimum, pc);
            var (mine, own) = Mine(s.Label, pc);
            var level = Level(own, minNeed, recNeed, s.Label is "Processor" or "Graphics" ? 0.95 : 1);
            // A card asked for only by its memory ("1 GB VRAM"): this PC's card's memory against it.
            if (level is null && s.Label == "Graphics" && minNeed is null && pc?.VramGb is { } vram && Gpus(s.Minimum ?? "").Count == 0)
                level = Level(vram, Size(s.Minimum), recNeed is null && Gpus(recommended ?? "").Count == 0 ? Size(recommended) : null, 1);
            if (level is { } l && s.Label is "OS" or "Processor" or "Graphics" or "Memory") judged.Add(l);
            outRows.Add(new SpecRow(s.Label, minShort, s.Recommended is null ? null : recShort, mine, level switch { 2 => "recommended", 1 => "minimum", 0 => "below", _ => null }));
        }
        // A verdict only when the processor or the graphics card could be judged: memory and Windows alone say little.
        var verdict = outRows.Any(r => r.Meets is not null && r.Label is "Processor" or "Graphics") && judged.Count > 0
            ? judged.Min() switch { 2 => "recommended", 1 => "minimum", _ => "below" }
            : null;
        return new SpecFit(outRows, verdict);
    }

    /// <summary>2 at Recommended, 1 at Minimum, 0 below Minimum; null when either side could not be read. Cards and processors get a twentieth of slack: their scales are rough.</summary>
    private static int? Level(double? own, double? minimum, double? recommended, double slack)
    {
        if (own is not { } mine) return null;
        if (recommended is { } r && mine >= r * slack) return 2;
        if (minimum is { } m) return mine >= m * slack ? 1 : 0;
        return null;
    }

    /// <summary>A requirement in short, and the number it asks for on this PC's scale (for a card or processor, the one from this PC's maker when it names one).</summary>
    private static (string? Short, double? Need) Read(string label, string? text, string? notes, ThisPc? pc)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        switch (label)
        {
            case "Graphics":
            {
                var parts = Gpus(text);
                if (parts.Count == 0) return (Size(text) is { } vram ? $"{Amount(vram)} VRAM" : Tidy(text), null);
                return (Join(parts), Need(parts, pc?.Gpu is { } g ? Gpus(g).FirstOrDefault()?.Maker : null));
            }
            case "Processor":
            {
                var parts = Cpus(text);
                if (parts.Count == 0) return (Tidy(text), null);
                return (Join(parts), Need(parts, pc?.Cpu is { } c ? Cpus(c).FirstOrDefault()?.Maker : null));
            }
            case "Memory":
                return Size(text) is { } ram ? (Amount(ram), ram) : (Tidy(text), null);
            case "Storage":
            {
                if (Size(text) is not { } gb) return (Tidy(text), null);
                var ssd = SsdRequired(text) || SsdRequired(notes);
                return ($"{Amount(gb)}{(ssd ? " SSD" : "")}", gb);
            }
            case "OS":
                return Windows(text) is { } os ? ("Windows " + os.Name, os.Version) : (Tidy(text), null);
            case "DirectX":
                // "Version 11", "DirectX 12", "9.0c": the version's whole number is what a card must run.
                return DirectX().Match(text) is { Success: true } dx
                    ? ("DirectX " + dx.Groups[1].Value.ToLowerInvariant(), double.TryParse(Regex.Match(dx.Groups[1].Value, @"^\d+").Value, CultureInfo.InvariantCulture, out var version) ? version : null)
                    : (Tidy(text), null);
            case "Network":
                // Broadband or any internet connection: a connection is what is asked.
                return (Regex.IsMatch(text, "broadband", RegexOptions.IgnoreCase) ? "Broadband" : Regex.IsMatch(text, "internet", RegexOptions.IgnoreCase) ? "Internet" : Tidy(text), 1);
            default:
                return (Tidy(text), null);
        }
    }

    /// <summary>This PC's part for a row, and its number on the row's scale.</summary>
    private static (string? Shown, double? Own) Mine(string label, ThisPc? pc)
    {
        if (pc is null) return (null, null);
        switch (label)
        {
            case "Graphics" when pc.Gpu is { } gpu:
            {
                var part = Gpus(gpu).FirstOrDefault();
                // A laptop card runs about a fifth slower than the desktop card of its name.
                var score = part is null ? (double?)null : part.Score * (Regex.IsMatch(gpu, "laptop|mobile|max-q", RegexOptions.IgnoreCase) ? 0.8 : 1);
                return (part?.Name ?? gpu, score);
            }
            case "Processor" when pc.Cpu is { } cpu:
            {
                var part = Cpus(cpu).FirstOrDefault();
                return (part?.Name ?? cpu, part?.Score);
            }
            case "Memory" when pc.RamGb > 0:
                return ($"{pc.RamGb} GB", pc.RamGb);
            case "Storage" when pc.FreeGb is { } free:
                return ($"{Amount(Math.Floor(free))} free", free);
            case "OS" when pc.Windows > 0:
                return ($"Windows {pc.Windows}", pc.Windows);
            case "DirectX" when pc.DirectX > 0:
                return ($"DirectX {pc.DirectX}", pc.DirectX);
            case "Network" when pc.Online is { } online:
                return (online ? "Online" : "Offline", online ? 1 : 0);
            default:
                return (null, null);
        }
    }

    /// <summary>What the game asks of this PC's maker's part when it names one, else the least it names: any one of them is enough.</summary>
    private static double? Need(List<Part> parts, string? maker)
    {
        var scored = parts.Where(p => p.Score > 0).ToList();
        var same = scored.Where(p => p.Maker == maker).ToList();
        var pick = same.Count > 0 ? same : scored;
        return pick.Count > 0 ? pick.Min(p => p.Score) : null;
    }

    /// <summary>The first card or processor named from each maker, which is the least the game asks of that maker.</summary>
    private static string Join(List<Part> parts) => string.Join(" · ", parts.DistinctBy(p => p.Maker).Select(p => p.Name));

    /// <summary>The first size a text names, in GB ("8 GB RAM", "2.5GB", "8192 MB", "200MB").</summary>
    private static double? Size(string? text)
    {
        if (text is null || SizeAmount().Match(text) is not { Success: true } m) return null;
        var n = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return m.Groups[2].Value.ToUpperInvariant() switch { "MB" => n / 1024, "TB" => n * 1024, _ => n };
    }

    private static string Amount(double gb) =>
        gb < 1 ? $"{Math.Round(gb * 1024)} MB" : $"{Math.Round(gb, 1).ToString(CultureInfo.InvariantCulture)} GB";

    /// <summary>"SSD required", "60 SSD GB": yes. "SSD recommended", "SSD (Preferred)", "HDD supported": no.</summary>
    private static bool SsdRequired(string? text) =>
        text is not null && Regex.IsMatch(text, @"\bSSD\b|solid state", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(text, @"SSD[^.,;]{0,24}(recommend|prefer|optional|better|suggest)|(recommend|prefer)[^.,;]{0,40}(SSD|solid state)|install the game on SSD", RegexOptions.IgnoreCase);

    /// <summary>The oldest Windows a text accepts ("Windows 7/8/10" is 7, "Windows 10/11" is 10).</summary>
    private static (string Name, double Version)? Windows(string text)
    {
        var at = text.IndexOf("windows", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var versions = WindowsVersion().Matches(text[at..])
            .Select(m => m.Value.ToUpperInvariant() switch { "XP" => ("XP", 5.1), "VISTA" => ("Vista", 6.0), var v => (v, double.Parse(v, CultureInfo.InvariantCulture)) })
            .ToList();
        return versions.Count > 0 ? versions.MinBy(v => v.Item2) : null;
    }

    /// <summary>Steam's words, a little tidier: no "or equivalent", no trailing punctuation.</summary>
    private static string Tidy(string text) =>
        Spaces().Replace(Regex.Replace(text, @"\s*\(?\b(or (better|higher|greater|equivalent|newer|above|later)|and above)\b\)?", "", RegexOptions.IgnoreCase), " ").Trim(' ', ',', '.', ';');

    // ---- Graphics cards ----

    private static readonly Dictionary<string, double> NvidiaScore = Scale(
        "710:8 730:10 740:18 750:40 750TI:47 760:63 770:75 780:85 780TI:98 950:60 960:66 970:92 980:108 980TI:130 " +
        "1030:35 1050:55 1050TI:66 1060:100 1070:135 1070TI:150 1080:160 1080TI:210 1630:50 1650:82 1650S:105 1660:115 1660S:128 1660TI:130 " +
        "2060:150 2060S:172 2070:180 2070S:200 2080:215 2080S:225 2080TI:270 3050:130 3060:190 3060TI:230 3070:265 3070TI:280 3080:330 3080TI:370 3090:380 3090TI:410 " +
        "4060:220 4060TI:260 4070:340 4070S:385 4070TI:410 4070TIS:440 4080:500 4080S:510 4090:640 5050:200 5060:265 5060TI:310 5070:420 5070TI:520 5080:580 5090:800 " +
        "6600:1 6800:2 7600:2 7800:3 7900:4 430:6 440:8 450:12 460:25 470:30 480:36 550TI:22 560:28 560TI:33 570:40 580:46 640:15 650:22 650TI:33 660:50 660TI:57 670:70 680:77 690:100 8600:4 8800:9 9600:7 9800:11");

    private static readonly Dictionary<string, double> AmdScore = Scale(
        "RX460:45 RX470:88 RX480:97 RX550:30 RX560:47 RX570:92 RX580:100 RX590:108 VEGA56:140 VEGA64:155 VII:190 " +
        "RX5500XT:105 RX5600XT:150 RX5700:165 RX5700XT:185 RX6400:70 RX6500XT:80 RX6600:175 RX6600XT:195 RX6650XT:205 RX6700:215 RX6700XT:240 RX6750XT:250 " +
        "RX6800:295 RX6800XT:340 RX6900XT:365 RX6950XT:385 RX7600:210 RX7600XT:220 RX7700XT:300 RX7800XT:350 RX7900GRE:390 RX7900XT:455 RX7900XTX:530 " +
        "RX9060XT:290 RX9070:450 RX9070XT:500 R7250:22 R7260X:40 R7360:38 R7370:55 R9270:58 R9270X:62 R9280:72 R9280X:82 R9290:98 R9290X:105 R9380:75 R9380X:82 R9390:108 R9390X:115 " +
        "HD2600:3 HD3600:4 HD3870:6 HD4850:12 HD4870:14 HD5570:8 HD5670:12 HD5770:20 HD5870:30 HD6850:26 HD6870:28 HD6950:38 HD6970:42 " +
        "HD7750:28 HD7770:35 HD7790:42 HD7850:52 HD7870:62 HD7950:72 HD7970:82");

    private static readonly Dictionary<string, double> ArcScore = Scale("A310:45 A380:65 A580:165 A750:190 A770:205 B570:210 B580:240");

    private static Dictionary<string, double> Scale(string table) =>
        table.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':'))
            .ToDictionary(x => x[0], x => double.Parse(x[1], CultureInfo.InvariantCulture));

    /// <summary>
    /// The cards a text names, one per alternative ("GTX 970 / 1060", "R9 280(3GB), AMD Radeon R9 290", "Nvidia 2060 Super / RX 5700 XT").
    /// A bare model number after a maker's card is that maker's ("GeForce 8600/9600GT", "Radeon HD2600/3600").
    /// </summary>
    private static List<Part> Gpus(string text)
    {
        var parts = new List<Part>();
        string? last = null;
        var upper = Regex.Replace(text.ToUpperInvariant(), @"\((SUPER|TI)\)", " $1");
        foreach (var alt in Alternatives().Split(upper))
        {
            var part = Gpu(alt, last);
            if (part is null) continue;
            parts.Add(part);
            last = part.Maker == "amd" && part.Name.StartsWith("HD", StringComparison.Ordinal) ? "amd-hd" : part.Maker;
        }
        return parts;
    }

    private static Part? Gpu(string s, string? previous)
    {
        if (NvidiaModel().Match(s) is { Success: true } nv) return Nvidia(nv.Groups[1].Value, nv.Groups[2].Value, nv.Groups[3].Value);
        if (NvidiaBare().Match(s) is { Success: true } nb) return Nvidia("", nb.Groups[1].Value, nb.Groups[2].Value);
        if (Regex.Match(s, @"\bVEGA\s*(56|64)\b") is { Success: true } vega) return Amd("VEGA" + vega.Groups[1].Value, "RX Vega " + vega.Groups[1].Value);
        if (Regex.IsMatch(s, @"\bRADEON\s+VII\b")) return Amd("VII", "Radeon VII");
        if (Regex.Match(s, @"\bRX\s*-?\s*(\d{3,4})\s*-?\s*(XTX|XT|GRE)?\b") is { Success: true } rx)
            return Amd("RX" + rx.Groups[1].Value + rx.Groups[2].Value, $"RX {rx.Groups[1].Value}{(rx.Groups[2].Length > 0 ? " " + rx.Groups[2].Value : "")}");
        if (Regex.Match(s, @"\bR([579])\s*-?\s*(\d{3})(X)?\b") is { Success: true } r)
            return Amd($"R{r.Groups[1].Value}{r.Groups[2].Value}{r.Groups[3].Value}", $"R{r.Groups[1].Value} {r.Groups[2].Value}{r.Groups[3].Value}");
        if (Regex.Match(s, @"(?:\bHD\s*-?\s*|\b(?:AMD|ATI|RADEON)\s+(?:RADEON\s+)?(?:HD\s*)?)(\d{4})\b") is { Success: true } hd)
            return Amd("HD" + hd.Groups[1].Value, "HD " + hd.Groups[1].Value);
        if (Regex.Match(s, @"\bARC\s*-?\s*([AB])\s*(\d{3})\b") is { Success: true } arc)
            return new Part($"Arc {arc.Groups[1].Value}{arc.Groups[2].Value}", "intel", ArcScore.GetValueOrDefault(arc.Groups[1].Value + arc.Groups[2].Value));
        if (Regex.Match(s, @"\b(UHD|HD|IRIS(?:\s+(?:XE|PLUS|PRO))?|INTEGRATED)\s*GRAPHICS\s*(\d{3,4})?") is { Success: true } igpu && (s.Contains("INTEL", StringComparison.Ordinal) || igpu.Groups[1].Value != "HD" || igpu.Groups[2].Success))
        {
            var model = igpu.Groups[2].Value;
            var kind = igpu.Groups[1].Value == "INTEGRATED" ? "HD" : igpu.Groups[1].Value;
            var score = kind.StartsWith("IRIS", StringComparison.Ordinal) ? 25 : model.Length == 4 ? 6 : model.StartsWith('7') ? 15 : 12;
            var name = kind.StartsWith("IRIS", StringComparison.Ordinal) ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kind.ToLowerInvariant()) : kind;
            return new Part($"Intel {name}{(model.Length > 0 ? " " + model : "")}", "intel", score);
        }
        // "1060" after "GTX 970 /", "3600" after "Radeon HD2600/".
        if (previous is not null && Regex.Match(s, @"^\s*(\d{3,4})\s*(TI|SUPER)?(?!\d)") is { Success: true } bare)
            return previous switch
            {
                "nvidia" => Nvidia("", bare.Groups[1].Value, bare.Groups[2].Value),
                "amd-hd" => Amd("HD" + bare.Groups[1].Value, "HD " + bare.Groups[1].Value),
                _ => null,
            };
        return null;
    }

    private static Part? Nvidia(string prefix, string number, string suffix)
    {
        var n = int.Parse(number, CultureInfo.InvariantCulture);
        var tier = suffix.Replace("SUPER", "S", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (!NvidiaScore.TryGetValue(number + tier, out var score) && !NvidiaScore.TryGetValue(number, out score)) score = 0;
        if (score == 0 && prefix.Length == 0) return null; // "Nvidia 1 GB": not a card
        prefix = prefix.Length > 0 ? prefix
            : n >= 2000 && n < 6000 ? "RTX"
            : n >= 1000 && n < 2000 ? (n == 1030 ? "GT" : "GTX")
            : n >= 6000 ? "GeForce" // the GeForce 6000 to 9000 series: 7600, 8600, 9800
            : n % 100 < 50 ? "GT" : "GTX";
        var shownTier = suffix.Length == 0 ? "" : " " + string.Join(' ', suffix.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x == "TI" ? "Ti" : "Super"));
        return new Part($"{prefix} {number}{shownTier}", "nvidia", score);
    }

    private static Part Amd(string key, string name) => new(name, "amd", AmdScore.GetValueOrDefault(key));

    // ---- Processors ----

    private static readonly Dictionary<int, double> IntelGeneration = new()
    {
        [1] = 30, [2] = 40, [3] = 45, [4] = 50, [5] = 52, [6] = 58, [7] = 62, [8] = 78, [9] = 82, [10] = 92, [11] = 100, [12] = 125, [13] = 140, [14] = 145,
    };

    private static readonly Dictionary<char, double> RyzenSeries = new()
    {
        ['1'] = 62, ['2'] = 68, ['3'] = 85, ['4'] = 82, ['5'] = 110, ['7'] = 135, ['8'] = 125, ['9'] = 150,
    };

    private static double Tier(char t) => t switch { '3' => 0.72, '7' => 1.18, '9' => 1.32, _ => 1 };

    /// <summary>The processors a text names ("Core i7-6700 or Ryzen 5 1600", "Intel I5 4690 / AMD FX 8350", "AMD r5 3600").</summary>
    private static List<Part> Cpus(string text)
    {
        var parts = new List<Part>();
        foreach (var alt in Alternatives().Split(text.ToUpperInvariant()))
            if (Cpu(alt) is { } part) parts.Add(part);
        return parts;
    }

    private static Part? Cpu(string s)
    {
        if (Regex.Match(s, @"\bULTRA\s*([579])\s*-?\s*(\d{3})([A-Z]{0,2})\b") is { Success: true } ultra)
        {
            var score = (ultra.Groups[2].Value[0] == '1' ? 120 : 145) * Tier(ultra.Groups[1].Value[0]) * Mobile(ultra.Groups[3].Value);
            return new Part($"Core Ultra {ultra.Groups[1].Value} {ultra.Groups[2].Value}{ultra.Groups[3].Value}", "intel", score);
        }
        if (Regex.Match(s, @"\bI([3579])\s*-?\s*(\d{3,5})([A-Z]{0,3})\b") is { Success: true } core)
        {
            var digits = core.Groups[2].Value;
            var generation = digits.Length == 3 ? 1 : digits.Length == 4 ? digits[0] - '0' : int.Parse(digits[..2], CultureInfo.InvariantCulture);
            if (!IntelGeneration.TryGetValue(generation, out var base_)) return null;
            var suffix = core.Groups[3].Value;
            var score = base_ * Tier(core.Groups[1].Value[0]) * (suffix.Contains('K') ? 1.04 : 1) * Mobile(suffix);
            return new Part($"i{core.Groups[1].Value}-{digits}{suffix}", "intel", score);
        }
        if (Regex.Match(s, @"\b(?:RYZEN|R)\s*([3579])\s*-?\s*(\d{4})\s*(X3D|XT|X|GE|G|U|HS|HX|H)?\b") is { Success: true } ryzen
            && RyzenSeries.TryGetValue(ryzen.Groups[2].Value[0], out var series))
        {
            var suffix = ryzen.Groups[3].Value;
            var score = series * Tier(ryzen.Groups[1].Value[0]) * suffix switch { "X3D" => 1.15, "X" or "XT" => 1.03, "G" or "GE" => 0.95, _ => Mobile(suffix) };
            return new Part($"Ryzen {ryzen.Groups[1].Value} {ryzen.Groups[2].Value}{suffix}", "amd", score);
        }
        if (Regex.Match(s, @"\bFX\s*-?\s*(\d{4})\b") is { Success: true } fx)
            return new Part("FX-" + fx.Groups[1].Value, "amd", fx.Groups[1].Value[0] switch { '4' => 35, '6' => 42, '8' => 50, '9' => 55, _ => 0 });
        if (Regex.Match(s, @"\bCORE\s*2\s*(QUAD|DUO)\b(?:[^/]*?\b([QE]\d{4})\b)?") is { Success: true } c2)
            return new Part($"Core 2 {(c2.Groups[1].Value == "QUAD" ? "Quad" : "Duo")}{(c2.Groups[2].Success ? " " + c2.Groups[2].Value : "")}", "intel", c2.Groups[1].Value == "QUAD" ? 25 : 15);
        if (Regex.IsMatch(s, @"\bPHENOM\b"))
            return new Part(Regex.IsMatch(s, @"\bPHENOM\s+II\b") ? "Phenom II" : "Phenom", "amd", Regex.IsMatch(s, @"\bPHENOM\s+II\b") ? 30 : 22);
        return null;
    }

    private static double Mobile(string suffix) => suffix.Contains('U') ? 0.7 : suffix.StartsWith('H') ? 0.9 : 1;

    // "GTX 970 / 1060", "A or B", "A, B", "A; B": each one is enough.
    [GeneratedRegex(@"\s*(?:/|,|;|\||\bOR\b|\bor\b)\s*")]
    private static partial Regex Alternatives();

    [GeneratedRegex(@"\b(RTX|GTX|GT)\s*-?\s*(\d{3,4})\s*-?\s*((?:TI|SUPER)(?:\s+SUPER)?)?(?![\d])")]
    private static partial Regex NvidiaModel();

    [GeneratedRegex(@"\b(?:GEFORCE|NVIDIA)\s+(?:GEFORCE\s+)?(\d{3,4})\s*-?\s*((?:TI|SUPER)(?:\s+SUPER)?)?(?![\d])")]
    private static partial Regex NvidiaBare();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(?:SSD\s*)?(GB|MB|TB|G)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeAmount();

    [GeneratedRegex(@"(?<![\d.])(XP|Vista|11|10|8\.1|8|7)(?![\d.])", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsVersion();

    [GeneratedRegex(@"(\d{1,2}(?:\.\d)?[a-c]?)", RegexOptions.IgnoreCase)]
    private static partial Regex DirectX();
}
