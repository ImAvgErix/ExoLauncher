using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// A game's PC requirements, read out of the HTML Steam stores them as into
/// label, minimum and recommended, in the order the minimum list gives them.
/// Steam's HTML comes in many shapes: a list of "&lt;strong&gt;Label:&lt;/strong&gt; value"
/// items, the label and value both inside the &lt;strong&gt;, lines split by &lt;br&gt;,
/// or one paragraph of comma-separated parts per column ("500 mhz processor, 96mb ram");
/// labels vary too ("Video Card", "Hard Disk Space", "OS *"), so each is read to one name.
/// </summary>
internal sealed partial class StoreService
{
    private static IReadOnlyList<Spec> ReadSpecs(JsonElement details) =>
        details.TryGetProperty("pc_requirements", out var req) && req.ValueKind == JsonValueKind.Object
            ? ParseSpecs(Str(req, "minimum"), Str(req, "recommended"))
            : [];

    /// <summary>Steam's minimum and recommended HTML as one row per label. A minimum that carries both columns (older games) is split.</summary>
    internal static IReadOnlyList<Spec> ParseSpecs(string? minimumHtml, string? recommendedHtml)
    {
        var (minimum, recommendedInMinimum) = SpecLines(minimumHtml);
        // The recommended column opens with its own "Recommended:" heading, so its lines are the second list.
        var (untitled, titled) = SpecLines(recommendedHtml);
        var recommended = untitled.Concat(titled).ToList();
        if (recommended.Count == 0) recommended = recommendedInMinimum;
        return minimum.Select(x => x.Label).Concat(recommended.Select(x => x.Label)).Distinct()
            .Select(label => new Spec(label,
                minimum.FirstOrDefault(x => x.Label == label).Value,
                recommended.FirstOrDefault(x => x.Label == label).Value))
            .ToList();
    }

    /// <summary>The labeled lines of one column; a "Recommended:" heading inside it starts the second list.</summary>
    private static (List<(string Label, string Value)> Lines, List<(string Label, string Value)> Recommended) SpecLines(string? html)
    {
        var first = new List<(string, string)>();
        var second = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(html)) return (first, second);
        var target = first;
        string? pending = null;
        foreach (var raw in SpecBreak().Replace(html, "\n").Split('\n'))
        {
            var line = Clean(raw);
            if (line.Length == 0) continue;
            if (SpecHeading().Match(line) is { Success: true } heading)
            {
                if (heading.Groups[1].Value.StartsWith("rec", StringComparison.OrdinalIgnoreCase)) target = second;
                pending = null;
                AddUnlabeled(target, heading.Groups[2].Value);
                continue;
            }
            if (SpecLabel().Match(line) is { Success: true } m)
            {
                var label = SpecName(m.Groups[1].Value);
                var value = m.Groups[2].Value.Trim();
                if (value.Length == 0) pending = label;
                else Add(target, label, value);
                continue;
            }
            // "<strong>Processor:</strong><br>Intel Core i5": the value on the line after its label.
            if (pending is not null) { Add(target, pending, line); pending = null; }
        }
        return (first, second);
    }

    private static void Add(List<(string Label, string Value)> lines, string label, string value)
    {
        if (lines.Any(x => x.Label == label)) return;
        lines.Add((label, value));
    }

    /// <summary>A paragraph of parts with no labels ("500 mhz processor, 96mb ram, 16mb video card, Windows XP"): each part by what it names.</summary>
    private static void AddUnlabeled(List<(string Label, string Value)> lines, string text)
    {
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var label = part switch
            {
                _ when Regex.IsMatch(part, @"\bwindows\b", RegexOptions.IgnoreCase) => "OS",
                _ when Regex.IsMatch(part, @"video|graphics|gpu|geforce|radeon", RegexOptions.IgnoreCase) => "Graphics",
                _ when Regex.IsMatch(part, @"\bram\b|memory", RegexOptions.IgnoreCase) => "Memory",
                _ when Regex.IsMatch(part, @"processor|cpu|\bghz\b|\bmhz\b", RegexOptions.IgnoreCase) => "Processor",
                _ when Regex.IsMatch(part, @"disk|storage|space", RegexOptions.IgnoreCase) => "Storage",
                _ when Regex.IsMatch(part, @"internet|broadband", RegexOptions.IgnoreCase) => "Network",
                _ => null,
            };
            if (label is not null) Add(lines, label, part);
        }
    }

    /// <summary>One name per kind of line, whatever the page calls it.</summary>
    private static string SpecName(string label)
    {
        var key = label.Replace("*", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
        return key switch
        {
            "os" or "operating system" or "system" => "OS",
            "processor" or "cpu" or "processors" => "Processor",
            "memory" or "ram" or "system memory" => "Memory",
            "graphics" or "video card" or "video" or "gpu" or "graphics card" or "video memory" => "Graphics",
            "directx" or "direct x" or "directx version" => "DirectX",
            "storage" or "hard drive" or "hard disk space" or "hard disk" or "hdd" or "disk space" or "free disk space" or "hard drive space" => "Storage",
            "network" or "internet" => "Network",
            "sound card" or "sound" or "audio" => "Sound Card",
            "additional notes" or "notes" or "other" or "other requirements" => "Additional Notes",
            "vr support" => "VR Support",
            _ => key.Length == 0 ? label : char.ToUpperInvariant(key[0]) + key[1..],
        };
    }

    /// <summary>Text of one line: tags gone, entities read, the ® and ™ marks and zero-width marks gone, one space between words.</summary>
    private static string Clean(string html) =>
        Spaces().Replace(Marks().Replace(WebUtility.HtmlDecode(Tag().Replace(html, " ")), ""), " ").Trim();

    [GeneratedRegex(@"<\s*(?:br|/?li|/?p|/?ul|/?h\d)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SpecBreak();

    [GeneratedRegex(@"^(minimum|recommended|required|requirements)\b\s*:?\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecHeading();

    [GeneratedRegex(@"^([^:]{1,40}):\s*(.*)$")]
    private static partial Regex SpecLabel();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[®™​-‍﻿]")]
    private static partial Regex Marks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
