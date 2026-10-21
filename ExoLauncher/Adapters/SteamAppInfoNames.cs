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
    private static readonly TimeSpan ReloadTtl = TimeSpan.FromMinutes(10);
    private static string? _path;
    private static DateTime _writeUtc;
    private static DateTime _loadedAtUtc;
    private static string? _failedPath;
    private static DateTime _retryAtUtc;
    private static IReadOnlyDictionary<string, Entry> _loaded =
        new Dictionary<string, Entry>(StringComparer.Ordinal);

    public readonly record struct Entry(string Name, string Type, string ParentAppId = "");

    public static IReadOnlyDictionary<string, Entry> Load(string? path)
        => LoadAt(path, DateTime.UtcNow);

    internal static IReadOnlyDictionary<string, Entry> LoadAt(string? path, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new Dictionary<string, Entry>(StringComparer.Ordinal);

        var write = File.GetLastWriteTimeUtc(path);
        lock (Gate)
        {
            var samePath = string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(_failedPath, path, StringComparison.OrdinalIgnoreCase) && nowUtc < _retryAtUtc)
                return samePath ? _loaded : new Dictionary<string, Entry>(StringComparer.Ordinal);
            if (samePath && _writeUtc == write)
                return _loaded;
            // Steam rewrites appinfo.vdf while running. Re-parsing a multi-MB
            // file on every 30s library scan is what made Steam miss the 25s
            // adapter timeout. Keep a short TTL even when mtime moved.
            if (samePath && _loaded.Count > 0 && nowUtc - _loadedAtUtc < ReloadTtl)
                return _loaded;
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != MagicV41)
                    throw new InvalidDataException("Steam appinfo header is incomplete or unsupported.");
                var parsed = Parse(bytes);
                if (parsed.Count == 0 && _loaded.Count > 0 && samePath)
                    throw new InvalidDataException("Steam appinfo did not contain a complete catalog.");
                _loaded = parsed;
            }
            catch
            {
                _failedPath = path;
                _retryAtUtc = nowUtc + TimeSpan.FromSeconds(2);
                return samePath ? _loaded : new Dictionary<string, Entry>(StringComparer.Ordinal);
            }
            _path = path;
            _writeUtc = write;
            _loadedAtUtc = nowUtc;
            _failedPath = null;
            _retryAtUtc = default;
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
        // DLC, soundtracks, mod uploaders and bonus content name their base
        // game in common/parent; a few older records use parentappid.
        var parentAppIdIdx = IndexOf(table, "parentappid");
        var parentIdx = IndexOf(table, "parent");
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
            string? parentAppId = null;
            WalkDict(
                data, ref pos, blobEnd, table, commonIdx, nameIdx, typeIdx, parentAppIdIdx, parentIdx,
                inCommon: false, ref name, ref type, ref parentAppId);
            if (!string.IsNullOrWhiteSpace(name))
                result[appId.ToString()] = new Entry(
                    name.Trim(),
                    type?.Trim() ?? "",
                    parentAppId?.Trim() ?? "");
            pos = blobEnd;
        }

        return result;
    }

    /// <summary>
    /// The whole record (<c>common</c>, <c>extended</c>, <c>depots</c> ...) of each
    /// requested app, for a game's Properties. Apps not in the file are left out.
    /// </summary>
    internal static Dictionary<uint, SteamKeyValues> ReadApps(byte[] data, IReadOnlySet<uint> appIds)
    {
        var result = new Dictionary<uint, SteamKeyValues>();
        if (appIds.Count == 0 || data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != MagicV41) return result;
        var tableOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
        if (tableOffset < 16 || tableOffset >= data.Length) return result;
        var endOfApps = (int)tableOffset;
        var table = ReadStringTable(data, endOfApps);
        if (table.Length == 0) return result;
        var pos = 16;
        while (pos + 8 <= endOfApps && result.Count < appIds.Count)
        {
            var appId = ReadU32(data, ref pos, endOfApps);
            if (appId == 0) break;
            var size = ReadU32(data, ref pos, endOfApps);
            var blobEnd = pos + (int)size;
            if (size > int.MaxValue - pos || blobEnd > endOfApps) break;
            if (appIds.Contains(appId) && pos + 60 <= blobEnd)
            {
                var record = new SteamKeyValues();
                var at = pos + 60;
                ReadSection(data, ref at, blobEnd, table, record, 0);
                // The record's single root is "appinfo"; callers read below it.
                result[appId] = record.Keys.Count == 1 ? record.Section(record.Keys[0]) : record;
            }
            pos = blobEnd;
        }
        return result;
    }

    private static void ReadSection(byte[] data, ref int pos, int end, string[] table, SteamKeyValues into, int depth)
    {
        while (pos < end)
        {
            var kind = data[pos++];
            if (kind == 8 || pos + 4 > end) return;
            var index = (int)ReadU32(data, ref pos, end);
            var key = index >= 0 && index < table.Length ? table[index] : "";
            switch (kind)
            {
                case 0:
                    if (depth > 32) return;
                    ReadSection(data, ref pos, end, table, into.Add(key), depth + 1);
                    break;
                case 1:
                    into.Set(key, ReadCString(data, ref pos, end));
                    break;
                case 2:
                    into.Set(key, ((int)ReadU32(data, ref pos, end)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case 7:
                case 10:
                    if (pos + 8 > end) return;
                    into.Set(key, kind == 7
                        ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    pos += 8;
                    break;
                case 3:
                case 4:
                case 6:
                    pos += 4;
                    break;
                case 5:
                    SkipUtf16(data, ref pos, end);
                    break;
                default:
                    return;
            }
        }
    }

    private static void WalkDict(
        byte[] data,
        ref int pos,
        int end,
        string[] table,
        int commonIdx,
        int nameIdx,
        int typeIdx,
        int parentAppIdIdx,
        int parentIdx,
        bool inCommon,
        ref string? name,
        ref string? type,
        ref string? parentAppId)
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
                        data, ref pos, end, table, commonIdx, nameIdx, typeIdx, parentAppIdIdx, parentIdx,
                        inCommon: key == commonIdx, ref name, ref type, ref parentAppId);
                    break;
                case 1:
                    var value = ReadCString(data, ref pos, end);
                    if (!inCommon) break;
                    if (key == nameIdx && name is null) name = value;
                    else if (key == typeIdx && type is null) type = value;
                    else if (IsParentKey(key, parentAppIdIdx, parentIdx) && parentAppId is null && !string.IsNullOrWhiteSpace(value) && value.Trim() != "0")
                        parentAppId = value.Trim();
                    break;
                case 2:
                    var number = ReadU32(data, ref pos, end);
                    if (inCommon && IsParentKey(key, parentAppIdIdx, parentIdx) && parentAppId is null && number != 0)
                        parentAppId = number.ToString();
                    break;
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

    private static bool IsParentKey(int key, int parentAppIdIdx, int parentIdx) =>
        (parentAppIdIdx >= 0 && key == parentAppIdIdx) || (parentIdx >= 0 && key == parentIdx);

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
