using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Reads GOG Galaxy's <c>GameTimes</c> table from <c>galaxy-2.0.db</c> without
/// taking a SQLite package dependency. Galaxy locks the live file, so the
/// reader always copies it first.
/// </summary>
internal static class GogGalaxySqlite
{
    public sealed record GameTime(string ProductId, int Minutes, DateTimeOffset? LastPlayedUtc);

    /// <summary>
    /// Every readable GameTimes row. Callers fold minutes and last-played in one
    /// pass; Galaxy's database is copied per call, so a second read is not free.
    /// </summary>
    public static IReadOnlyList<GameTime> LoadAll()
    {
        var rows = new List<GameTime>();
        foreach (var path in CandidateDatabasePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var copy = CopyUnlocked(path);
                if (copy is null) continue;
                try
                {
                    foreach (var row in ReadGameTimes(copy))
                        rows.Add(row);
                }
                finally
                {
                    TryDelete(copy);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("GOG Galaxy sqlite read failed: " + ex.Message);
            }
        }

        return rows;
    }

    /// <summary>
    /// Galaxy 2.0 stores playtime in <c>GameTimes</c>. Achievement unlocks are
    /// not in that table; names that contain "achievement" are listed so a
    /// future schema change is obvious in tests.
    /// </summary>
    internal static bool IsAchievementTableName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var compact = name.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        return compact.Contains("achievement", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> ListTableNames(string databasePath) =>
        ReadSchema(databasePath).Select(table => table.Name).ToArray();

    internal static IEnumerable<string> CandidateDatabasePaths()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(programData, "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");
        yield return Path.Combine(local, "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");
    }

    internal static bool TryParseReleaseKey(string? raw, out string productId)
    {
        productId = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var value = raw.Trim();
        const string prefix = "gog_";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..];
        if (value.Length is 0 or > 20 || !value.All(char.IsDigit)) return false;
        productId = value;
        return true;
    }

    internal static IReadOnlyList<GameTime> ReadGameTimes(string databasePath)
    {
        using var stream = File.OpenRead(databasePath);
        var db = new SqliteTableReader(stream);
        return db.ReadGameTimes();
    }

    /// <summary>
    /// sqlite_master rows. Used to probe friend tables whose names shift
    /// between Galaxy versions. Never writes.
    /// </summary>
    internal static IReadOnlyList<TableInfo> ReadSchema(string databasePath)
    {
        using var stream = File.OpenRead(databasePath);
        return new SqliteTableReader(stream).ReadSchema();
    }

    internal static IReadOnlyList<IReadOnlyDictionary<string, string?>> ReadTable(
        string databasePath, string tableName)
    {
        using var stream = File.OpenRead(databasePath);
        return new SqliteTableReader(stream).ReadNamedTable(tableName);
    }

    internal sealed record TableInfo(string Name, string? Sql);

    internal sealed record Column(string Name, bool IntegerPrimaryKey);

    /// <summary>Column list from a CREATE TABLE statement. Missing or odd SQL yields nothing.</summary>
    internal static IReadOnlyList<Column> ParseCreateTable(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Column>();
        var open = sql.IndexOf('(');
        var close = sql.LastIndexOf(')');
        if (open < 0 || close <= open) return Array.Empty<Column>();
        var body = sql[(open + 1)..close];
        var columns = new List<Column>();
        foreach (var part in SplitTopLevel(body))
        {
            var tokens = Tokenize(part);
            if (tokens.Count == 0) continue;
            if (IsConstraint(tokens[0])) continue;
            var name = Unquote(tokens[0]);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var rest = string.Join(' ', tokens.Skip(1));
            var pk = rest.Contains("INTEGER", StringComparison.OrdinalIgnoreCase) &&
                     rest.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
            columns.Add(new Column(name, pk));
        }

        return columns;
    }

    internal static IReadOnlyList<Column> FallbackColumns(string tableName) =>
        tableName.ToLowerInvariant() switch
        {
            "users" => [new Column("id", true), new Column("username", false), new Column("userId", false)],
            "friends" => [new Column("userId", false), new Column("friendId", false)],
            "userpresence" or "userspresence" or "friendpresence" or "presence" =>
            [
                new Column("userId", false),
                new Column("presence_state", false),
                new Column("game_id", false),
                new Column("game_title", false),
            ],
            _ => Array.Empty<Column>(),
        };

    private static bool IsConstraint(string token) =>
        token.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("CHECK", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                yield return body[start..i];
                start = i + 1;
            }
        }

        if (start < body.Length) yield return body[start..];
    }

    private static List<string> Tokenize(string part)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < part.Length)
        {
            while (i < part.Length && char.IsWhiteSpace(part[i])) i++;
            if (i >= part.Length) break;
            if (part[i] is '"' or '\'' or '[' or '`')
            {
                var end = part[i] == '[' ? ']' : part[i];
                var open = i++;
                while (i < part.Length && part[i] != end) i++;
                if (i < part.Length) i++;
                tokens.Add(part[open..i]);
                continue;
            }

            var from = i;
            while (i < part.Length && !char.IsWhiteSpace(part[i]) && part[i] != ',') i++;
            tokens.Add(part[from..i]);
        }

        return tokens;
    }

    private static string Unquote(string token)
    {
        token = token.Trim();
        if (token.Length >= 2 &&
            ((token[0] == '"' && token[^1] == '"') ||
             (token[0] == '\'' && token[^1] == '\'') ||
             (token[0] == '`' && token[^1] == '`') ||
             (token[0] == '[' && token[^1] == ']')))
            return token[1..^1];
        return token;
    }

    internal static string? CopyUnlocked(string path)
    {
        var dest = Path.Combine(Path.GetTempPath(), "exo-gog-galaxy-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = File.Create(dest);
            source.CopyTo(target);
            return dest;
        }
        catch
        {
            TryDelete(dest);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* temp copy */ }
    }

    /// <summary>Minimal SQLite table walker for GameTimes (text + int columns).</summary>
    private sealed class SqliteTableReader
    {
        private readonly byte[] _data;
        private readonly int _pageSize;

        public SqliteTableReader(Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _data = ms.ToArray();
            if (_data.Length < 100 || Encoding.ASCII.GetString(_data, 0, 16) != "SQLite format 3\0")
                throw new InvalidDataException("Not a SQLite 3 database.");
            var rawSize = ReadUInt16(16);
            _pageSize = rawSize == 1 ? 65536 : rawSize;
            if (_pageSize < 512 || _data.Length < _pageSize)
                throw new InvalidDataException("Unsupported SQLite page size.");
        }

        public IReadOnlyList<TableInfo> ReadSchema()
        {
            var tables = new List<TableInfo>();
            foreach (var (_, cells) in WalkTable(1))
            {
                if (cells.Count < 2) continue;
                var type = AsText(cells[0]);
                if (!string.Equals(type, "table", StringComparison.OrdinalIgnoreCase)) continue;
                var name = AsText(cells[1]) ?? AsText(cells.Count > 2 ? cells[2] : null);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                    continue;
                tables.Add(new TableInfo(name, cells.Count > 4 ? AsText(cells[4]) : null));
            }

            return tables;
        }

        public IReadOnlyList<IReadOnlyDictionary<string, string?>> ReadNamedTable(string tableName)
        {
            if (!TryFindTable(tableName, out var rootPage, out var sql))
                return Array.Empty<IReadOnlyDictionary<string, string?>>();
            var columns = ParseCreateTable(sql).ToList();
            if (columns.Count == 0) columns.AddRange(FallbackColumns(tableName));
            if (columns.Count == 0) return Array.Empty<IReadOnlyDictionary<string, string?>>();
            var pk = columns.FirstOrDefault(column => column.IntegerPrimaryKey);
            var rows = new List<IReadOnlyDictionary<string, string?>>();
            foreach (var (rowid, cells) in WalkTable(rootPage))
            {
                var values = cells.ToList();
                var idSlot = pk is not null ||
                             (columns.Count > 0 && columns[0].Name.Equals("id", StringComparison.OrdinalIgnoreCase));
                if (idSlot && values.Count == columns.Count - 1)
                    values.Insert(0, rowid);
                else if (idSlot && values.Count == columns.Count && values[0] is null)
                    values[0] = rowid;
                var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columns.Count && i < values.Count; i++)
                    map[columns[i].Name] = Stringify(values[i]);
                if (map.Count > 0) rows.Add(map);
            }

            return rows;
        }

        public IReadOnlyList<GameTime> ReadGameTimes()
        {
            if (!TryFindTableRoot("GameTimes", out var rootPage))
                return Array.Empty<GameTime>();

            var rows = new List<GameTime>();
            foreach (var (_, cells) in WalkTable(rootPage))
            {
                if (cells.Count == 0) continue;
                var key = cells.Select(AsText).FirstOrDefault(text => TryParseReleaseKey(text, out _));
                if (key is null || !TryParseReleaseKey(key, out var productId)) continue;
                var minutes = cells.Select(AsInt64).Where(value => value is > 0 and < 100_000_000).Select(value => (int)value!).FirstOrDefault();
                if (minutes <= 0) continue;
                DateTimeOffset? last = null;
                foreach (var unix in cells.Select(AsInt64))
                {
                    if (unix is > 1_000_000_000 and < 2_200_000_000)
                    {
                        try { last = DateTimeOffset.FromUnixTimeSeconds(unix.Value); }
                        catch { /* ignore */ }
                    }
                }

                rows.Add(new GameTime(productId, minutes, last));
            }

            return rows;
        }

        private bool TryFindTableRoot(string tableName, out int rootPage) =>
            TryFindTable(tableName, out rootPage, out _);

        private bool TryFindTable(string tableName, out int rootPage, out string? sql)
        {
            rootPage = 0;
            sql = null;
            foreach (var (_, cells) in WalkTable(1))
            {
                if (cells.Count < 4) continue;
                var type = AsText(cells[0]);
                var name = AsText(cells[1]) ?? AsText(cells[2]);
                if (!string.Equals(type, "table", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase)) continue;
                var page = AsInt64(cells[3]);
                if (page is > 0 and < int.MaxValue)
                {
                    rootPage = (int)page.Value;
                    sql = cells.Count > 4 ? AsText(cells[4]) : null;
                    return true;
                }
            }

            return false;
        }

        private IEnumerable<(long RowId, IReadOnlyList<object?> Cells)> WalkTable(int pageNumber)
        {
            if (pageNumber <= 0) yield break;
            var offset = (pageNumber - 1) * _pageSize;
            if (offset < 0 || offset + 8 >= _data.Length) yield break;
            var headerOffset = pageNumber == 1 ? offset + 100 : offset;
            if (headerOffset + 8 >= _data.Length) yield break;
            var pageType = _data[headerOffset];
            var cellCount = ReadUInt16(headerOffset + 3);
            if (pageType == 0x05)
            {
                for (var i = 0; i < cellCount; i++)
                {
                    var ptr = ReadUInt16(headerOffset + 12 + i * 2);
                    var cell = offset + ptr;
                    var pos = cell;
                    ReadVarint(ref pos);
                    var child = ReadInt32(pos);
                    foreach (var row in WalkTable(child))
                        yield return row;
                }

                var right = ReadInt32(headerOffset + 8);
                foreach (var row in WalkTable(right))
                    yield return row;
                yield break;
            }

            if (pageType != 0x0d) yield break;
            for (var i = 0; i < cellCount; i++)
            {
                var ptr = ReadUInt16(headerOffset + 8 + i * 2);
                var pos = offset + ptr;
                if (pos < 0 || pos >= _data.Length) continue;
                var payloadSize = (int)ReadVarint(ref pos);
                var rowid = ReadVarint(ref pos);
                if (payloadSize <= 0 || pos + payloadSize > _data.Length) continue;
                var payload = _data.AsSpan(pos, payloadSize);
                yield return (rowid, DecodeRecord(payload));
            }
        }

        private static List<object?> DecodeRecord(ReadOnlySpan<byte> payload)
        {
            var pos = 0;
            var headerSize = (int)ReadVarint(payload, ref pos);
            if (headerSize < 1 || headerSize > payload.Length) return [];
            var serialTypes = new List<long>();
            while (pos < headerSize && pos < payload.Length)
                serialTypes.Add(ReadVarint(payload, ref pos));

            var values = new List<object?>(serialTypes.Count);
            var dataPos = headerSize;
            foreach (var serial in serialTypes)
            {
                if (dataPos > payload.Length) break;
                values.Add(ReadSerial(payload, ref dataPos, serial));
            }

            return values;
        }

        private static object? ReadSerial(ReadOnlySpan<byte> payload, ref int pos, long serial)
        {
            if (serial == 0) return null;
            if (serial is >= 1 and <= 6)
            {
                var width = serial switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 6, _ => 8 };
                var value = ReadBigInt(payload, pos, width);
                pos += width;
                return value;
            }

            if (serial == 7)
            {
                pos += 8;
                return null;
            }

            if (serial == 8) return 0L;
            if (serial == 9) return 1L;
            if (serial >= 12 && serial % 2 == 0)
            {
                var len = (int)((serial - 12) / 2);
                pos += len;
                return null;
            }

            if (serial >= 13 && serial % 2 == 1)
            {
                var len = (int)((serial - 13) / 2);
                if (pos + len > payload.Length) return null;
                var text = Encoding.UTF8.GetString(payload.Slice(pos, len));
                pos += len;
                return text;
            }

            return null;
        }

        private static string? AsText(object? value) => value as string;

        private static long? AsInt64(object? value) => value is long n ? n : null;

        private static string? Stringify(object? value) => value switch
        {
            null => null,
            string text => text,
            long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        private ushort ReadUInt16(int offset) =>
            (ushort)((_data[offset] << 8) | _data[offset + 1]);

        private int ReadInt32(int offset) =>
            (_data[offset] << 24) | (_data[offset + 1] << 16) | (_data[offset + 2] << 8) | _data[offset + 3];

        private long ReadVarint(ref int pos) => ReadVarint(_data, ref pos);

        private static long ReadVarint(byte[] data, ref int pos) =>
            ReadVarint(data.AsSpan(), ref pos);

        private static long ReadVarint(ReadOnlySpan<byte> data, ref int pos)
        {
            long value = 0;
            for (var i = 0; i < 9 && pos < data.Length; i++)
            {
                var b = data[pos++];
                if (i == 8)
                {
                    value = (value << 8) | b;
                    break;
                }

                value = (value << 7) | (b & 0x7FL);
                if ((b & 0x80) == 0) break;
            }

            return value;
        }

        private static long ReadBigInt(ReadOnlySpan<byte> data, int offset, int width)
        {
            long value = 0;
            for (var i = 0; i < width && offset + i < data.Length; i++)
                value = (value << 8) | data[offset + i];
            var bits = width * 8;
            if (bits < 64)
            {
                var sign = 1UL << (bits - 1);
                if (((ulong)value & sign) != 0)
                    value = unchecked((long)((ulong)value | ~((1UL << bits) - 1)));
            }
            return value;
        }
    }
}
