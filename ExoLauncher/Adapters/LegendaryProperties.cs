using System.Text.Json;

namespace ExoLauncher.Adapters;

/// <summary>One choice of an Epic game's optional content (Legendary's selective download list).</summary>
internal sealed record EpicComponent(string Id, string Name, IReadOnlyList<string> Tags, bool Required);

/// <summary>
/// What Legendary keeps for an Epic game's Properties, read the way Legendary
/// reads it: the install tags in <c>installed.json</c> (what is on disk), the
/// per-game <c>install_tags</c> in <c>config.ini</c> (what its next install or
/// repair downloads, deleting files no longer tagged), the selective-download
/// list from Legendary's API, and <c>info --json</c> for each tag's size.
/// </summary>
internal static class LegendaryProperties
{
    /// <summary>The install tags Legendary recorded for an installed game, or null when it has none.</summary>
    internal static IReadOnlyList<string>? InstalledTags(string? installedJson, string appName)
    {
        if (string.IsNullOrWhiteSpace(installedJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(installedJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(appName, out var game) || game.ValueKind != JsonValueKind.Object ||
                !game.TryGetProperty("install_tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
                return null;
            return tags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String).Select(tag => tag.GetString() ?? "").ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Legendary's selective-download list: <c>{"__required": {"tags": [...]}, "&lt;id&gt;": {"name": ..., "tags": [...]}}</c>.
    /// Entries without tags are left out; the required one comes first.
    /// </summary>
    internal static IReadOnlyList<EpicComponent> ParseSdl(string? json)
    {
        var result = new List<EpicComponent>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object ||
                    !entry.Value.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) continue;
                var list = tags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String).Select(tag => tag.GetString() ?? "").ToList();
                var required = entry.Name == "__required";
                var name = entry.Value.TryGetProperty("name", out var title) && title.ValueKind == JsonValueKind.String
                    ? title.GetString()?.Trim() : null;
                result.Add(new EpicComponent(entry.Name, string.IsNullOrWhiteSpace(name) ? required ? "Game" : entry.Name : name, list, required));
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return result.OrderByDescending(component => component.Required).ToList();
    }

    /// <summary>Each install tag's size on disk from <c>legendary info --json</c> (<c>manifest.tag_disk_size</c>).</summary>
    internal static IReadOnlyDictionary<string, long> TagSizes(string? infoJson)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(infoJson)) return sizes;
        try
        {
            using var doc = JsonDocument.Parse(infoJson);
            if (!doc.RootElement.TryGetProperty("manifest", out var manifest) || manifest.ValueKind != JsonValueKind.Object ||
                !manifest.TryGetProperty("tag_disk_size", out var list) || list.ValueKind != JsonValueKind.Array)
                return sizes;
            foreach (var item in list.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.String &&
                    item.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes))
                    sizes[tag.GetString() ?? ""] = bytes;
        }
        catch (JsonException) { }
        return sizes;
    }

    /// <summary>A component is on when every tag it needs is installed.</summary>
    internal static bool IsSelected(EpicComponent component, IReadOnlyCollection<string> installed) =>
        component.Required || component.Tags.All(installed.Contains);

    /// <summary>A component's bytes: its tags' sizes, counting a tag another selected component shares once.</summary>
    internal static long? ComponentBytes(EpicComponent component, IReadOnlyDictionary<string, long> sizes) =>
        component.Tags.All(sizes.ContainsKey) && component.Tags.Count > 0 ? component.Tags.Distinct().Sum(tag => sizes[tag]) : null;

    /// <summary>The tags Legendary installs for a selection: the required ones and each chosen component's.</summary>
    internal static IReadOnlyList<string> TagsFor(IReadOnlyList<EpicComponent> components, IReadOnlySet<string> chosenIds) =>
        components.Where(component => component.Required || chosenIds.Contains(component.Id))
            .SelectMany(component => component.Tags)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// <paramref name="ini"/> with <c>key = value</c> set in <c>[section]</c>; every
    /// other line, comment and section stays as it was. Legendary writes the
    /// same file, so the caller only writes while Legendary is not running.
    /// </summary>
    internal static string SetIniValue(string? ini, string section, string key, string value)
    {
        var lines = (ini ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var start = lines.FindIndex(line => IsSection(line, section));
        var entry = $"{key} = {value}";
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add(entry);
        }
        else
        {
            var end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            var existing = lines.FindIndex(start + 1, end - start - 1, line => KeyOf(line) == key);
            if (existing >= 0) lines[existing] = entry;
            else
            {
                var insert = end;
                while (insert > start + 1 && lines[insert - 1].Trim().Length == 0) insert--;
                lines.Insert(insert, entry);
            }
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    internal static string? GetIniValue(string? ini, string section, string key)
    {
        var inSection = false;
        foreach (var line in (ini ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[')) { inSection = IsSection(trimmed, section); continue; }
            if (inSection && KeyOf(line) == key) return trimmed[(trimmed.IndexOfAny(['=', ':']) + 1)..].Trim();
        }
        return null;
    }

    private static bool IsSection(string line, string section) =>
        line.Trim() is { Length: > 2 } text && text[0] == '[' && text[^1] == ']' &&
        text[1..^1].Trim().Equals(section, StringComparison.Ordinal);

    private static string? KeyOf(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#' or '[') return null;
        var at = trimmed.IndexOfAny(['=', ':']);
        return at <= 0 ? null : trimmed[..at].Trim();
    }

    /// <summary>Legendary's app names are short ids; anything else never reaches a path, URL or INI section.</summary>
    internal static bool IsAppName(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && value[0] != '.';
}
