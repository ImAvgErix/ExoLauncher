using System.Text;

namespace ExoLauncher.Adapters;

/// <summary>
/// The Steam overlay's per-game switch, where Steam keeps it: the account's
/// <c>userdata/&lt;id&gt;/config/localconfig.vdf</c> ›
/// <c>UserLocalConfigStore/apps/&lt;app&gt;/OverlayAppEnable</c> ("0" off, "1" on,
/// absent = on), at the store's top level: where Steam's own Properties toggle
/// (<c>SteamClient.Apps.ToggleEnableSteamOverlayForApp</c> in steamui) saves it, and
/// the only place it reads. The same key under <c>Software/Valve/Steam/apps</c> (where
/// play time lives) is ignored: a game set off there still got the overlay.
/// Steam holds the file in memory and writes it as it exits, so a change is
/// written only while Steam is closed. The rest of the file is kept byte for byte.
/// </summary>
internal static class SteamOverlayConfig
{
    internal const string Key = "OverlayAppEnable";
    private static readonly string[] AppsPath = ["UserLocalConfigStore", "apps"];

    /// <summary>The <c>apps</c> section of a localconfig.vdf, parsed once for <see cref="Read"/>.</summary>
    internal static SteamKeyValues Apps(string? text) =>
        string.IsNullOrEmpty(text) ? SteamKeyValues.Empty : SteamKeyValues.ParseText(text).Section(string.Join('/', AppsPath));

    // A game's accepted EULAs: "<eula id>" "<version>" under Software/Valve/Steam/apps/<app>, beside its play time, where
    // Steam writes them when you press Accept. A game whose appinfo lists a newer version asks again at launch.
    private static readonly string[] PlayPath = ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps"];

    /// <summary>The games' EULAs (from appinfo: id and version) that this localconfig.vdf hasn't accepted at that version.</summary>
    internal static Dictionary<string, List<(string Id, int Version)>> PendingEulas(string? text, IReadOnlyDictionary<string, IReadOnlyList<(string Id, int Version)>> eulas)
    {
        var played = string.IsNullOrEmpty(text) ? SteamKeyValues.Empty : SteamKeyValues.ParseText(text).Section(string.Join('/', PlayPath));
        var pending = new Dictionary<string, List<(string, int)>>(StringComparer.Ordinal);
        foreach (var (appId, list) in eulas)
            foreach (var (id, version) in list)
                if ((played.Section(appId).Number(id) ?? -1) < version) (pending.TryGetValue(appId, out var due) ? due : pending[appId] = []).Add((id, version));
        return pending;
    }

    /// <summary>Accepts these EULAs as Steam's Accept button does; nothing else in the file changes.</summary>
    internal static string AcceptEulas(string text, IReadOnlyDictionary<string, List<(string Id, int Version)>> eulas)
    {
        foreach (var (appId, list) in eulas)
        {
            if (appId.Length is 0 or > 10 || !appId.All(char.IsAsciiDigit)) throw new ArgumentException("Not a Steam app id.", nameof(eulas));
            foreach (var (id, version) in list)
            {
                if (id.Length is 0 or > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) throw new ArgumentException("Not a EULA id.", nameof(eulas));
                text = SetValue(text, [.. PlayPath, appId], id, version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return text;
    }

    /// <summary>The game's overlay switch; null when Steam has none written (Steam's default, on).</summary>
    internal static bool? Read(SteamKeyValues apps, string appId) =>
        apps.Section(appId).Value(Key) switch { "0" => false, "1" => true, _ => null };

    /// <summary>
    /// What Exo writes, and its record afterwards. The record holds each game's
    /// switch as Exo set it ("on"/"off", with "?" while Steam has not taken it yet).
    /// A game Exo has no record of gets <paramref name="defaultOn"/>; a choice made
    /// in Exo wins; a game whose switch was changed in Steam itself after Exo set it
    /// keeps Steam's value, so Exo never fights the player.
    /// </summary>
    internal static (Dictionary<string, bool> Writes, Dictionary<string, string> Record) Plan(
        SteamKeyValues apps,
        IEnumerable<string> installed,
        IReadOnlyDictionary<string, string> record,
        bool defaultOn,
        IReadOnlyDictionary<string, bool>? choices = null)
    {
        var writes = new Dictionary<string, bool>(StringComparer.Ordinal);
        var next = new Dictionary<string, string>(record, StringComparer.OrdinalIgnoreCase);
        foreach (var appId in installed.Concat(choices?.Keys ?? []).Distinct(StringComparer.Ordinal))
        {
            var current = Read(apps, appId);
            bool desired;
            if (choices is not null && choices.TryGetValue(appId, out var chosen)) desired = chosen;
            else if (record.TryGetValue(appId, out var kept) && kept.TrimEnd('?') is "on" or "off")
            {
                var keptOn = kept.StartsWith("on", StringComparison.Ordinal);
                desired = !kept.EndsWith('?') && current is { } changed && changed != keptOn ? changed : keptOn;
            }
            else desired = defaultOn;
            var word = desired ? "on" : "off";
            if ((current ?? true) != desired)
            {
                writes[appId] = desired;
                word += "?";
            }
            next[appId] = word;
        }
        return (writes, next);
    }

    /// <summary>
    /// The file with each game's switch set. A missing game or section is added in
    /// Steam's own layout (tabs, one key per line); nothing else changes.
    /// </summary>
    internal static string Write(string text, IReadOnlyDictionary<string, bool> apps)
    {
        foreach (var (appId, on) in apps)
        {
            if (appId.Length is 0 or > 10 || !appId.All(char.IsAsciiDigit)) throw new ArgumentException("Not a Steam app id.", nameof(apps));
            text = SetValue(text, [.. AppsPath, appId], Key, on ? "1" : "0");
        }
        return text;
    }

    /// <summary>The VDF text with <paramref name="key"/> under <paramref name="path"/> set to <paramref name="value"/> (escaped as Steam writes it).</summary>
    internal static string SetValue(string text, string[] path, string key, string value)
    {
        // Each pass adds one missing section; the path is at most six deep.
        for (var pass = 0; pass <= path.Length; pass++)
        {
            var scan = Scan(text, path, key);
            if (scan.ValueStart >= 0) return string.Concat(text.AsSpan(0, scan.ValueStart), value, text.AsSpan(scan.ValueEnd));
            var depth = scan.Depth;
            var indent = new string('\t', depth);
            if (depth == path.Length)
                return text.Insert(scan.InsertAt, $"{indent}\"{key}\"\t\t\"{value}\"\n");
            text = text.Insert(scan.InsertAt, $"{indent}\"{path[depth]}\"\n{indent}{{\n{indent}}}\n");
        }
        throw new InvalidDataException("localconfig.vdf did not take the section.");
    }

    /// <summary>The VDF text without <paramref name="key"/>'s line under <paramref name="path"/> (unchanged when it isn't there).</summary>
    internal static string RemoveValue(string text, string[] path, string key)
    {
        var scan = Scan(text, path, key);
        if (scan.ValueStart < 0) return text;
        var start = text.LastIndexOf('\n', scan.ValueStart) + 1;
        var end = text.IndexOf('\n', scan.ValueEnd);
        end = end < 0 ? text.Length : end + 1;
        return text.Remove(start, end - start);
    }

    /// <summary>
    /// Where <paramref name="key"/> sits under <paramref name="path"/>: its value's
    /// span, or how deep the path exists and where the next line goes in it (just
    /// before the deepest section's closing brace, at the start of its line).
    /// </summary>
    private static (int ValueStart, int ValueEnd, int Depth, int InsertAt) Scan(string text, string[] path, string key)
    {
        var stack = new List<string>();
        var matched = 0;
        var deepestClose = -1;
        string? pending = null;
        var position = 0;
        while (position < text.Length)
        {
            var c = text[position];
            if (char.IsWhiteSpace(c)) { position++; continue; }
            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
                continue;
            }
            if (c == '{')
            {
                stack.Add(pending ?? "");
                pending = null;
                position++;
                continue;
            }
            if (c == '}')
            {
                // The deepest matched section is closing: the key or the next section goes here.
                if (stack.Count == matched && matched > 0 && OnPath(stack, path, matched))
                {
                    deepestClose = position;
                    break;
                }
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                pending = null;
                position++;
                continue;
            }
            var start = c == '"' ? position + 1 : position;
            var end = TokenEnd(text, ref position);
            var token = text[start..end];
            if (pending is null)
            {
                pending = token;
                // Entering the next section on the path once its brace opens.
                if (stack.Count == matched && matched < path.Length && OnPath(stack, path, matched) &&
                    token.Equals(path[matched], StringComparison.OrdinalIgnoreCase) && NextIsBrace(text, position))
                    matched++;
            }
            else
            {
                if (stack.Count == path.Length && matched == path.Length && OnPath(stack, path, matched) &&
                    pending.Equals(key, StringComparison.OrdinalIgnoreCase) && text[start - 1] == '"')
                    return (start, end, matched, -1);
                pending = null;
            }
        }
        if (matched == 0) return (-1, -1, 0, InsertPoint(text, text.Length));
        if (deepestClose < 0) throw new InvalidDataException("localconfig.vdf ends inside a section.");
        return (-1, -1, matched, InsertPoint(text, deepestClose));
    }

    private static bool OnPath(List<string> stack, string[] path, int count)
    {
        if (stack.Count < count) return false;
        for (var i = 0; i < count; i++)
            if (!stack[i].Equals(path[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool NextIsBrace(string text, int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        return position < text.Length && text[position] == '{';
    }

    /// <summary>The start of the line that holds <paramref name="at"/> (a closing brace), so a new line goes above it.</summary>
    private static int InsertPoint(string text, int at)
    {
        var lineStart = at;
        while (lineStart > 0 && text[lineStart - 1] is '\t' or ' ') lineStart--;
        if (lineStart > 0 && text[lineStart - 1] == '\n') return lineStart;
        // A brace that shares its line: break the line first by inserting at the brace itself.
        return at;
    }

    /// <summary>Moves past one token and returns the end of its content (before a closing quote).</summary>
    private static int TokenEnd(string text, ref int position)
    {
        if (text[position] != '"')
        {
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"')) position++;
            return position;
        }
        position++;
        while (position < text.Length && text[position] != '"')
            position += text[position] == '\\' && position + 1 < text.Length ? 2 : 1;
        var end = position;
        if (position < text.Length) position++;
        return end;
    }

    /// <summary>Writes the file next to itself and swaps it in, so Steam never reads half of it.</summary>
    internal static void WriteFile(string path, string text)
    {
        var tmp = path + ".exo-tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }
}
