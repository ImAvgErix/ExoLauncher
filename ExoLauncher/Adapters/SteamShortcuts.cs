using System.Buffers.Binary;
using System.Text;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam's non-Steam games: the account's userdata/&lt;id&gt;/config/shortcuts.vdf, binary key-values (00 a section,
/// 01 a string, 02 an int32, 08 a section's end; keys and strings UTF-8 ending in 00). Exo adds one shortcut per game
/// the player sends through Steam Input (a game that reads Xbox controllers only). The shortcut starts the game's own
/// program, so Steam starts the game itself: Steam applies a game's Steam Input to the window in front, and only for
/// what it started. Exo records each shortcut's app id and a stamp of the program, folder and options it wrote; a
/// shortcut is Exo's while it still has them. Exo updates or takes out only its own, and one the player changed in
/// Steam is the player's from then on. Every other entry and field goes back byte for byte. Steam rewrites the file as
/// it exits, so it is written with the overlay switch while Steam is closed. Covered by SteamShortcutsTests: a write
/// that loses another shortcut or a field, a player's own shortcut changed or taken out, a deleted one put back, an id
/// Steam would compute differently.
/// </summary>
internal static class SteamShortcuts
{
    private const byte Section = 0x00, Text = 0x01, Int32 = 0x02, Float32 = 0x03, Pointer = 0x04, Color = 0x06, UInt64 = 0x07, Int64 = 0x0A, End = 0x08;

    /// <summary>One key-value as the file has it: a section's entries, a string, or a number's bytes.</summary>
    internal sealed class Node(byte type, string key)
    {
        public byte Type { get; } = type;
        public string Key { get; set; } = key;
        public string? Value { get; set; }
        public byte[]? Bytes { get; set; }
        public List<Node>? Entries { get; set; }
    }

    /// <summary>
    /// A game Exo sends through Steam: its Exo id, the name Steam shows, and what the shortcut starts (a program, its
    /// folder, its options), with an icon (the game's exe) when there is one.
    /// </summary>
    internal sealed record Wanted(string GameId, string Name, string Exe, string StartDir, string LaunchOptions, string? Icon = null);

    /// <summary>A shortcut Exo wrote: Steam's app id for it, and the stamp of what it starts (<see cref="StampOf(string, string, string)"/>).</summary>
    internal readonly record struct Kept(uint AppId, uint Stamp);

    /// <summary>The file's top level; an empty file reads as none. An InvalidDataException when it isn't Steam's format.</summary>
    internal static List<Node> Read(ReadOnlySpan<byte> file)
    {
        var at = 0;
        var top = ReadEntries(file, ref at, topLevel: true);
        if (at != file.Length) throw new InvalidDataException("shortcuts.vdf has bytes after its end.");
        return top;
    }

    internal static byte[] Write(IReadOnlyList<Node> top)
    {
        using var stream = new MemoryStream();
        WriteEntries(stream, top);
        return stream.ToArray();
    }

    /// <summary>
    /// The id Steam gives a shortcut, as Steam computes it for one added in its own window: CRC-32 of the program (as the
    /// file holds it, quotes and all) and the name, with the top bit set. Steam keeps it in the entry's appid.
    /// </summary>
    internal static uint AppId(string exe, string name) => Helpers.Crc32.Of(Encoding.UTF8.GetBytes(exe + name)) | 0x80000000;

    /// <summary>steam://rungameid/&lt;this&gt; starts the shortcut: its app id above, the "shortcut" type below.</summary>
    internal static ulong GameId(uint appId) => ((ulong)appId << 32) | 0x02000000;

    /// <summary>A path as Steam keeps a shortcut's program and folder: in quotes.</summary>
    internal static string Quoted(string path) => $"\"{path}\"";

    /// <summary>What a shortcut starts, in one number: a change Exo didn't make shows as another stamp.</summary>
    internal static uint StampOf(string exe, string startDir, string launchOptions) =>
        Helpers.Crc32.Of(Encoding.UTF8.GetBytes($"{exe}\0{startDir}\0{launchOptions}"));

    /// <summary>
    /// The file with Exo's shortcuts as wanted, and Exo's record once it lands; File is null when the file already has
    /// them. A recorded shortcut no longer wanted is taken out only while it is still Exo's; one whose game now starts
    /// differently (moved, new options) is updated, its id and name kept. One the player deleted in Steam, or changed,
    /// is the player's: it leaves the record, is never put back or taken out, and its game comes back in Dropped so
    /// Exo's choice for it goes off too.
    /// </summary>
    internal static (byte[]? File, Dictionary<string, Kept> Record, List<string> Dropped) Plan(
        byte[]? file, IReadOnlyList<Wanted> wanted, IReadOnlyDictionary<string, Kept> record)
    {
        var top = file is { Length: > 0 } ? Read(file) : [];
        var shortcuts = top.FirstOrDefault(n => n.Type == Section && n.Key.Equals("shortcuts", StringComparison.OrdinalIgnoreCase));
        if (shortcuts is null)
        {
            shortcuts = new Node(Section, "shortcuts") { Entries = [] };
            top.Add(shortcuts);
        }
        var entries = shortcuts.Entries!;
        var changed = false;
        var next = new Dictionary<string, Kept>(StringComparer.OrdinalIgnoreCase);
        var dropped = new List<string>();
        var wantedIds = wanted.Select(w => w.GameId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (gameId, kept) in record)
            if (!wantedIds.Contains(gameId))
                changed |= entries.RemoveAll(e => IsExos(e, kept)) > 0;

        var used = entries.Select(IdOf).Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        foreach (var game in wanted)
        {
            var exe = Quoted(game.Exe);
            var startDir = Quoted(game.StartDir);
            var stamp = StampOf(exe, startDir, game.LaunchOptions);
            if (record.TryGetValue(game.GameId, out var kept))
            {
                if (entries.FirstOrDefault(e => IsExos(e, kept)) is not { } entry)
                {
                    dropped.Add(game.GameId);
                    continue;
                }
                if (kept.Stamp != stamp)
                {
                    SetText(entry, "Exe", exe);
                    SetText(entry, "StartDir", startDir);
                    SetText(entry, "LaunchOptions", game.LaunchOptions);
                    SetText(entry, "icon", game.Icon ?? "");
                    changed = true;
                }
                next[game.GameId] = kept with { Stamp = stamp };
                continue;
            }
            var name = game.Name.Trim().Length > 0 ? game.Name.Trim() : game.GameId;
            var appId = AppId(exe, name);
            for (var n = 2; used.Contains(appId) && n < 100; n++) appId = AppId(exe, name = $"{game.Name.Trim()} ({n})");
            if (used.Contains(appId)) continue;
            entries.Add(NewShortcut(appId, name, exe, startDir, game));
            used.Add(appId);
            next[game.GameId] = new Kept(appId, stamp);
            changed = true;
        }

        if (changed)
            for (var i = 0; i < entries.Count; i++) entries[i].Key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (changed ? Write(top) : null, next, dropped);
    }

    /// <summary>An entry's app id, or null when it has none.</summary>
    internal static uint? IdOf(Node entry) =>
        entry.Entries?.FirstOrDefault(f => f.Type == Int32 && f.Key.Equals("appid", StringComparison.OrdinalIgnoreCase))?.Bytes is { Length: 4 } b
            ? BinaryPrimitives.ReadUInt32LittleEndian(b) : null;

    private static Node? Field(Node entry, string key) =>
        entry.Entries?.FirstOrDefault(f => f.Type == Text && f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static void SetText(Node entry, string key, string value)
    {
        if (Field(entry, key) is { } field) field.Value = value;
        else entry.Entries!.Add(new Node(Text, key) { Value = value });
    }

    /// <summary>Still the shortcut Exo wrote: its id, and it starts what Exo set.</summary>
    private static bool IsExos(Node entry, Kept kept) =>
        IdOf(entry) == kept.AppId &&
        StampOf(Field(entry, "Exe")?.Value ?? "", Field(entry, "StartDir")?.Value ?? "", Field(entry, "LaunchOptions")?.Value ?? "") == kept.Stamp;

    /// <summary>A shortcut with the fields Steam writes for one added in its own window, and its defaults.</summary>
    private static Node NewShortcut(uint appId, string name, string exe, string startDir, Wanted game)
    {
        Node S(string key, string value) => new(Text, key) { Value = value };
        Node I(string key, uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            return new Node(Int32, key) { Bytes = bytes };
        }
        return new Node(Section, "")
        {
            Entries =
            [
                I("appid", appId), S("AppName", name), S("Exe", exe), S("StartDir", startDir), S("icon", game.Icon ?? ""),
                S("ShortcutPath", ""), S("LaunchOptions", game.LaunchOptions),
                // The overlay as Steam leaves it for a shortcut: Steam Input reaches the game through it.
                I("IsHidden", 0), I("AllowDesktopConfig", 1), I("AllowOverlay", 1), I("OpenVR", 0), I("Devkit", 0),
                S("DevkitGameID", ""), I("DevkitOverrideAppID", 0), I("LastPlayTime", 0), S("FlatpakAppID", ""),
                new Node(Section, "tags") { Entries = [] },
            ],
        };
    }

    private static List<Node> ReadEntries(ReadOnlySpan<byte> file, ref int at, bool topLevel)
    {
        var entries = new List<Node>();
        while (true)
        {
            if (at >= file.Length)
            {
                if (topLevel) return entries;
                throw new InvalidDataException("shortcuts.vdf ends inside a section.");
            }
            var type = file[at++];
            if (type == End) return entries;
            var node = new Node(type, ReadString(file, ref at));
            switch (type)
            {
                case Section: node.Entries = ReadEntries(file, ref at, topLevel: false); break;
                case Text: node.Value = ReadString(file, ref at); break;
                case Int32 or Float32 or Pointer or Color: node.Bytes = Take(file, ref at, 4); break;
                case UInt64 or Int64: node.Bytes = Take(file, ref at, 8); break;
                default: throw new InvalidDataException($"shortcuts.vdf has a value of type {type:X2}, which Exo doesn't know.");
            }
            entries.Add(node);
        }
    }

    private static string ReadString(ReadOnlySpan<byte> file, ref int at)
    {
        var end = file[at..].IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("shortcuts.vdf has a string with no end.");
        var text = Encoding.UTF8.GetString(file.Slice(at, end));
        at += end + 1;
        return text;
    }

    private static byte[] Take(ReadOnlySpan<byte> file, ref int at, int count)
    {
        if (at + count > file.Length) throw new InvalidDataException("shortcuts.vdf ends inside a number.");
        var bytes = file.Slice(at, count).ToArray();
        at += count;
        return bytes;
    }

    private static void WriteEntries(Stream stream, IReadOnlyList<Node> entries)
    {
        foreach (var node in entries)
        {
            stream.WriteByte(node.Type);
            WriteString(stream, node.Key);
            switch (node.Type)
            {
                case Section: WriteEntries(stream, node.Entries ?? []); break;
                case Text: WriteString(stream, node.Value ?? ""); break;
                default: stream.Write(node.Bytes ?? throw new InvalidDataException("A number with no bytes.")); break;
            }
        }
        stream.WriteByte(End);
    }

    private static void WriteString(Stream stream, string text)
    {
        if (text.Contains('\0')) throw new InvalidDataException("A shortcut's text can't hold a zero byte.");
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.WriteByte(0);
    }
}
