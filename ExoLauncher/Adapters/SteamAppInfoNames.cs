using System.Buffers.Binary;
using System.Text;

namespace ExoLauncher.Adapters;

/// <summary>
/// Reads Steam's <c>appcache/appinfo.vdf</c> (v41 / magic 0x07564429) for
/// local app names. Used so owned-not-installed library rows have titles
/// without hitting the store network during a library scan.
/// </summary>
internal static class SteamAppInfoNames
{
    private const uint MagicV41 = 0x07564429;
    private static readonly object Gate = new();
    private static string? _path;
    private static DateTime _writeUtc;
    private static IReadOnlyDictionary<string, Entry> _loaded =
        new Dictionary<string, Entry>(StringComparer.Ordinal);

    public readonly record struct Entry(string Name, string Type)
    {
        public bool IsPlayableTitle
        {
            get
            {
                var t = (Type ?? "").Trim().ToLowerInvariant();
                return t is "" or "game" or "application" or "demo";
            }
        }
    }

    public static IReadOnlyDictionary<string, Entry> Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new Dictionary<string, Entry>(StringComparer.Ordinal);

        var write = File.GetLastWriteTimeUtc(path);
        lock (Gate)
        {
            if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) && _writeUtc == write)
                return _loaded;
            try
            {
                _loaded = Parse(File.ReadAllBytes(path));
            }
            catch
            {
                _loaded = new Dictionary<string, Entry>(StringComparer.Ordinal);
            }
            _path = path;
            _writeUtc = write;
            return _loaded;
        }
    }

    internal static IReadOnlyDictionary<string, Entry> Parse(byte[] data)
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (data.Length < 16) return result;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != MagicV41) return result;
        var tableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
        if (tableOffset < 16 || tableOffset >= data.Length) return result;
        var endOfApps = (int)tableOffset;
        var table = ReadStringTable(data, endOfApps);
        if (table.Length == 0) return result;
        var commonIdx = IndexOf(table, "common");
        var nameIdx = IndexOf(table, "name");
        var typeIdx = IndexOf(table, "type");
        if (commonIdx < 0 || nameIdx < 0) return result;

        var pos = 16;
        while (pos + 8 <= endOfApps)
        {
            var appId = ReadU32(data, ref pos, endOfApps);
            if (appId == 0) break;
            var size = ReadU32(data, ref pos, endOfApps);
            var blobEnd = pos + (int)size;
            if (size > int.MaxValue - pos || blobEnd > endOfApps || blobEnd > data.Length)
                break;
            var headerEnd = pos + 60;
            if (headerEnd > blobEnd)
            {
                pos = blobEnd;
                continue;
            }

            pos = headerEnd;
            string? name = null;
            string? type = null;
            WalkDict(data, ref pos, blobEnd, table, commonIdx, nameIdx, typeIdx, inCommon: false, ref name, ref type);
            if (!string.IsNullOrWhiteSpace(name))
                result[appId.ToString()] = new Entry(name.Trim(), type?.Trim() ?? "");
            pos = blobEnd;
        }

        return result;
    }

    private static void WalkDict(
        byte[] data,
        ref int pos,
        int end,
        string[] table,
        int commonIdx,
        int nameIdx,
        int typeIdx,
        bool inCommon,
        ref string? name,
        ref string? type)
    {
        while (pos < end)
        {
            var kind = data[pos++];
            if (kind == 8) return;
            if (pos + 4 > end) return;
            var key = (int)ReadU32(data, ref pos, end);
            switch (kind)
            {
                case 0:
                    WalkDict(
                        data, ref pos, end, table, commonIdx, nameIdx, typeIdx,
                        inCommon: key == commonIdx, ref name, ref type);
                    break;
                case 1:
                    var value = ReadCString(data, ref pos, end);
                    if (!inCommon) break;
                    if (key == nameIdx && name is null) name = value;
                    else if (key == typeIdx && type is null) type = value;
                    break;
                case 2:
                case 3:
                case 4:
                case 6:
                    pos += 4;
                    break;
                case 7:
                case 10:
                    pos += 8;
                    break;
                case 5:
                    SkipUtf16(data, ref pos, end);
                    break;
                default:
                    return;
            }
        }
    }

    private static string[] ReadStringTable(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return [];
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        if (count is < 1 or > 1_000_000) return [];
        var table = new string[count];
        var pos = offset + 4;
        for (var i = 0; i < count; i++)
        {
            if (pos >= data.Length) return [];
            table[i] = ReadCString(data, ref pos, data.Length);
        }
        return table;
    }

    private static int IndexOf(string[] table, string value)
    {
        for (var i = 0; i < table.Length; i++)
        {
            if (string.Equals(table[i], value, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static uint ReadU32(byte[] data, ref int pos, int end)
    {
        if (pos + 4 > end || pos + 4 > data.Length) return 0;
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;
        return value;
    }

    private static string ReadCString(byte[] data, ref int pos, int end)
    {
        var start = pos;
        while (pos < end && pos < data.Length && data[pos] != 0)
            pos++;
        var text = start < pos
            ? Encoding.UTF8.GetString(data, start, pos - start)
            : "";
        if (pos < end && pos < data.Length && data[pos] == 0)
            pos++;
        return text;
    }

    private static void SkipUtf16(byte[] data, ref int pos, int end)
    {
        while (pos + 1 < end && pos + 1 < data.Length)
        {
            if (data[pos] == 0 && data[pos + 1] == 0)
            {
                pos += 2;
                return;
            }
            pos += 2;
        }
    }
}
