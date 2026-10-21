using System.Text;

namespace ExoLauncher.Adapters;

/// <summary>
/// One section of Steam's KeyValues: text ones (<c>appmanifest_*.acf</c>) and the
/// binary records of <c>appinfo.vdf</c> read into the same shape. Keys compare
/// case-insensitively, as Steam's own lookups do; the first of a repeated key wins.
/// </summary>
internal sealed class SteamKeyValues
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SteamKeyValues> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    internal static readonly SteamKeyValues Empty = new();

    /// <summary>The child keys in file order (values and sections).</summary>
    internal IReadOnlyList<string> Keys => _order;

    internal string? Value(string key) => _values.GetValueOrDefault(key);

    internal long? Number(string key) => long.TryParse(Value(key), out var number) ? number : null;

    /// <summary>The section at a <c>/</c>-separated path, or <see cref="Empty"/>.</summary>
    internal SteamKeyValues Section(string path)
    {
        var node = this;
        foreach (var part in path.Split('/'))
            if (!node._sections.TryGetValue(part, out node!)) return Empty;
        return node;
    }

    internal IEnumerable<(string Key, SteamKeyValues Section)> Sections =>
        _order.Where(_sections.ContainsKey).Select(key => (key, _sections[key]));

    internal void Set(string key, string value)
    {
        if (_values.ContainsKey(key) || _sections.ContainsKey(key)) return;
        _values[key] = value;
        _order.Add(key);
    }

    internal SteamKeyValues Add(string key)
    {
        if (_sections.TryGetValue(key, out var existing)) return existing;
        var section = new SteamKeyValues();
        if (_values.ContainsKey(key)) return section;
        _sections[key] = section;
        _order.Add(key);
        return section;
    }

    /// <summary>
    /// Parses text KeyValues. The outer section (<c>"AppState" { ... }</c>) is
    /// returned as the root's child; a truncated file keeps what was read.
    /// </summary>
    internal static SteamKeyValues ParseText(string text)
    {
        var root = new SteamKeyValues();
        var stack = new Stack<SteamKeyValues>();
        stack.Push(root);
        string? pendingKey = null;
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
                position++;
                stack.Push(stack.Peek().Add(pendingKey ?? ""));
                pendingKey = null;
                continue;
            }
            if (c == '}')
            {
                position++;
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
                continue;
            }
            var token = ReadToken(text, ref position);
            if (pendingKey is null) pendingKey = token;
            else
            {
                stack.Peek().Set(pendingKey, token);
                pendingKey = null;
            }
        }
        return root;
    }

    private static string ReadToken(string text, ref int position)
    {
        var builder = new StringBuilder();
        if (text[position] != '"')
        {
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"'))
                builder.Append(text[position++]);
            return builder.ToString();
        }
        position++;
        while (position < text.Length && text[position] != '"')
        {
            var c = text[position++];
            if (c == '\\' && position < text.Length)
            {
                var next = text[position++];
                builder.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
            }
            else builder.Append(c);
        }
        position++;
        return builder.ToString();
    }
}
