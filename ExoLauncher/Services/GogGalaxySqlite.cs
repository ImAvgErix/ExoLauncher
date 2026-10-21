using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ExoLauncher.Services;

/// <summary>Reads Galaxy with SQLite's read-only transaction support, including committed WAL pages.</summary>
internal static class GogGalaxySqlite
{
    public sealed record GameTime(string ProductId, int Minutes, DateTimeOffset? LastPlayedUtc);
    internal sealed record TableInfo(string Name, string? Sql);

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
        if (value.StartsWith("gog_", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        if (value.Length is 0 or > 20 || !value.All(char.IsAsciiDigit)) return false;
        productId = value;
        return true;
    }

    internal static IReadOnlyList<GameTime> ReadGameTimes(string databasePath)
    {
        using var snapshot = OpenSnapshot(databasePath);
        // Galaxy 2.0 keeps last played in its own table, keyed gog_<id>.
        var lastPlayed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var row in snapshot.ReadTable("LastPlayedDates"))
        {
            if (!TryParseReleaseKey(Value(row, "gameReleaseKey", "releaseKey"), out var id) ||
                !TryParseStamp(Value(row, "lastPlayedDate", "lastPlayed"), out var when)) continue;
            if (!lastPlayed.TryGetValue(id, out var current) || when > current) lastPlayed[id] = when;
        }
        var rows = new List<GameTime>();
        foreach (var row in snapshot.ReadTable("GameTimes"))
        {
            // Galaxy schemas have used both releaseKey/time and product-specific names.
            // Never infer minutes from a row id or choose a numeric release key as duration.
            var key = Value(row, "releaseKey", "release_key", "gameReleaseKey", "game_release_key", "productId", "product_id", "gameId", "game_id");
            if (!TryParseReleaseKey(key, out var productId)) continue;
            // Galaxy 2.0 names it minutesInGame; the others are older or third-party shapes.
            var minutesText = Value(row, "minutesInGame", "minutes", "time", "timePlayed", "time_played", "playtime");
            if (!int.TryParse(minutesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
                minutes is <= 0 or >= 100_000_000) continue;
            DateTimeOffset? last = null;
            var stamp = Value(row, "lastPlayed", "lastPlayedUtc", "last_played", "last_played_time", "lastSession", "last_session");
            if (TryParseStamp(stamp, out var parsed)) last = parsed;
            else if (lastPlayed.TryGetValue(productId, out var dated)) last = dated;
            rows.Add(new GameTime(productId, minutes, last));
        }
        return rows;
    }

    /// <summary>Unix seconds or a date; same rule as GalaxyPlatformTimes.</summary>
    internal static bool TryParseStamp(string? stamp, out DateTimeOffset when)
    {
        when = default;
        if (string.IsNullOrWhiteSpace(stamp)) return false;
        if (long.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            if (unix is <= 1_000_000_000 or >= 4_102_444_800) return false;
            when = DateTimeOffset.FromUnixTimeSeconds(unix);
            return true;
        }
        if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when)) return false;
        return when.Year is > 2000 and < 2100;
    }

    internal static string? Value(IReadOnlyDictionary<string, string?> row, params string[] keys)
    {
        foreach (var key in keys)
            if (row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    internal static ReadSnapshot OpenSnapshot(string databasePath) => new(databasePath);

    /// <summary>One coherent transaction for every table in a consumer's snapshot; never copies the main file alone.</summary>
    internal sealed class ReadSnapshot : IDisposable
    {
        internal const int MaxRowsPerTable = 100_000;
        internal const int MaxValueBytes = 1_048_576;
        private const int MaxTables = 1024;
        private const int MaxTotalCharacters = 16_777_216;
        private const long MaxDatabaseBytes = 536_870_912;
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private int _characters;
        public IReadOnlyList<TableInfo> Schema { get; }

        internal ReadSnapshot(string databasePath)
        {
            var path = Path.GetFullPath(databasePath);
            if (new FileInfo(path).Length > MaxDatabaseBytes ||
                (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > MaxDatabaseBytes))
                throw new InvalidDataException("Galaxy database exceeds the read limit.");
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private,
                Pooling = false, DefaultTimeout = 1,
            }.ToString());
            try
            {
                _connection.Open();
                SQLitePCL.raw.sqlite3_limit(_connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, MaxValueBytes);
                SQLitePCL.raw.sqlite3_limit(_connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_COLUMN, 128);
                SQLitePCL.raw.sqlite3_progress_handler(_connection.Handle, 1000,
                    _ => _elapsed.Elapsed > TimeSpan.FromSeconds(3) ? 1 : 0, this);
                using (var setup = _connection.CreateCommand())
                {
                    setup.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF; PRAGMA cache_size=-2048;";
                    setup.ExecuteNonQuery();
                }
                _transaction = _connection.BeginTransaction(deferred: true);
                using var command = Command("SELECT name, sql FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' LIMIT 1025");
                using var reader = command.ExecuteReader();
                var schema = new List<TableInfo>();
                while (reader.Read())
                {
                    CheckDeadline();
                    if (schema.Count >= MaxTables) throw new InvalidDataException("Galaxy schema exceeds the read limit.");
                    var name = reader.GetString(0);
                    var sql = reader.IsDBNull(1) ? null : reader.GetString(1);
                    _characters += name.Length + (sql?.Length ?? 0);
                    if (_characters > MaxTotalCharacters) throw new InvalidDataException("Galaxy schema exceeds the text limit.");
                    schema.Add(new TableInfo(name, sql));
                }
                Schema = schema;
            }
            catch { _connection.Dispose(); throw; }
        }

        public IReadOnlyList<IReadOnlyDictionary<string, string?>> ReadTable(string tableName)
        {
            CheckDeadline();
            var table = Schema.FirstOrDefault(item => item.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
            if (table is null) return Array.Empty<IReadOnlyDictionary<string, string?>>();
            // Only ordinary tables are supported; provider extensions and virtual tables are never invoked.
            if (table.Sql is null || table.Sql.TrimStart().StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported Galaxy table.");
            using var command = Command("SELECT * FROM \"" + table.Name.Replace("\"", "\"\"", StringComparison.Ordinal) +
                                        "\" LIMIT " + (MaxRowsPerTable + 1).ToString(CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            var rows = new List<IReadOnlyDictionary<string, string?>>();
            while (reader.Read())
            {
                CheckDeadline();
                if (rows.Count >= MaxRowsPerTable) throw new InvalidDataException("Galaxy table exceeds the row limit.");
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    var value = reader.IsDBNull(column) || reader.GetFieldType(column) == typeof(byte[])
                        ? null : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture);
                    _characters += value?.Length ?? 0;
                    if (_characters > MaxTotalCharacters) throw new InvalidDataException("Galaxy snapshot exceeds the text limit.");
                    row[reader.GetName(column)] = value;
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// Release key and the raw JSON of its title piece (GamePieces joined to
        /// GamePieceTypes 'title' / 'originalTitle'). Empty when Galaxy's schema
        /// lacks those tables; only these two columns are read, so a big
        /// GamePieces table is never loaded whole.
        /// </summary>
        public IReadOnlyList<(string ReleaseKey, string Value)> ReadTitlePieces()
        {
            CheckDeadline();
            bool Has(string table, params string[] columns)
            {
                var sql = Schema.FirstOrDefault(item => item.Name.Equals(table, StringComparison.OrdinalIgnoreCase))?.Sql;
                return sql is not null && !sql.TrimStart().StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase) &&
                       columns.All(column => sql.Contains(column, StringComparison.OrdinalIgnoreCase));
            }
            if (!Has("GamePieces", "releaseKey", "gamePieceTypeId", "value") || !Has("GamePieceTypes", "id", "type"))
                return Array.Empty<(string, string)>();
            using var command = Command(
                "SELECT p.releaseKey, p.value FROM GamePieces p JOIN GamePieceTypes t ON t.id = p.gamePieceTypeId " +
                "WHERE t.type IN ('title', 'originalTitle') ORDER BY t.type DESC LIMIT " +
                (MaxRowsPerTable + 1).ToString(CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            var rows = new List<(string, string)>();
            while (reader.Read())
            {
                CheckDeadline();
                if (rows.Count >= MaxRowsPerTable) throw new InvalidDataException("Galaxy title pieces exceed the row limit.");
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetFieldType(1) == typeof(byte[])) continue;
                var key = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "";
                var value = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "";
                _characters += key.Length + value.Length;
                if (_characters > MaxTotalCharacters) throw new InvalidDataException("Galaxy snapshot exceeds the text limit.");
                rows.Add((key, value));
            }
            return rows;
        }

        private SqliteCommand Command(string sql)
        {
            var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = sql;
            command.CommandTimeout = 1;
            return command;
        }

        private void CheckDeadline()
        {
            if (_elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Galaxy snapshot read timed out.");
        }

        public void Dispose()
        {
            try { _transaction.Dispose(); }
            finally { _connection.Dispose(); }
        }
    }
}
