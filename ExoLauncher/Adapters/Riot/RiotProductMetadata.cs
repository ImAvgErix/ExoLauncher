using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ExoLauncher.Adapters.Riot;

/// <summary>
/// Riot Client's own per-product files under
/// <c>%ProgramData%\Riot Games\Metadata\&lt;product&gt;.live\</c>. Read only:
/// install path and repair flag (product_settings.yaml), update badge
/// (update-status.json), exact size (the patcher's SQLite file list) and the
/// product icon. No API call, no process start. The lockfile in the same
/// folder is never opened.
/// </summary>
internal static class RiotProductMetadata
{
    private const string Patchline = "live";
    private const long MaxTextBytes = 256 * 1024;

    internal sealed record ProductSettings(string? InstallFullPath, string? InstallRoot, bool ShouldRepair);

    private sealed record SizeReading(DateTime WrittenUtc, long Length, long? Bytes);

    private static readonly ConcurrentDictionary<string, SizeReading> Sizes = new(StringComparer.OrdinalIgnoreCase);

    internal static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "Metadata");

    // Product ids are Riot's lowercase slugs; anything else never reaches the disk.
    private static string? ProductFile(string product, string suffix, string? root) =>
        IsSlug(product)
            ? Path.Combine(root ?? DefaultRoot, $"{product}.{Patchline}", $"{product}.{Patchline}.{suffix}")
            : null;

    private static bool IsSlug(string product) =>
        product.Length is > 0 and <= 64 &&
        product.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    internal static ProductSettings? ReadSettings(string product, string? root = null) =>
        ReadText(ProductFile(product, "product_settings.yaml", root)) is { } yaml ? ParseSettings(yaml) : null;

    /// <summary>
    /// True when Riot says the product needs patching before play: an update
    /// is available or required, or the client flagged it for repair.
    /// </summary>
    internal static bool NeedsUpdate(string product, string? root = null)
    {
        if (ReadSettings(product, root)?.ShouldRepair == true) return true;
        return ReadText(ProductFile(product, "update-status.json", root)) is { } json && ParseUpdateStatus(json);
    }

    /// <summary>
    /// Sum of file sizes in the patcher's manifest database, or null when the
    /// manifest does not describe the whole install. League patches its game
    /// folder through a separate <c>.game_patch</c> line that has no file
    /// list, so its database covers only the client.
    /// </summary>
    internal static long? ReadManifestSize(string product, string? root = null)
    {
        var db = ProductFile(product, "db", root);
        if (db is null) return null;
        if (Directory.Exists(Path.Combine(root ?? DefaultRoot, $"{product}.{Patchline}.game_patch"))) return null;
        FileInfo info;
        try
        {
            info = new FileInfo(db);
            if (!info.Exists || info.Length == 0) return null;
        }
        catch { return null; }

        if (Sizes.TryGetValue(db, out var cached) &&
            cached.WrittenUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
            return cached.Bytes;

        var bytes = SumManifest(db);
        Sizes[db] = new SizeReading(info.LastWriteTimeUtc, info.Length, bytes);
        return bytes;
    }

    /// <summary>Riot's own product icon, written next to the metadata.</summary>
    internal static string? IconPath(string product, string? root = null)
    {
        var ico = ProductFile(product, "ico", root);
        try { return ico is not null && File.Exists(ico) ? ico : null; }
        catch { return null; }
    }

    internal static long? SumManifest(string dbPath)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 1;
            command.CommandText = "SELECT COUNT(*), TOTAL(size) FROM files";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) == 0) return null;
            var total = reader.GetDouble(1);
            return total > 0 && total < long.MaxValue ? (long)total : null;
        }
        catch
        {
            // Locked mid-patch, schema changed or not SQLite: callers fall back.
            return null;
        }
    }

    /// <summary>Top-level scalar keys only; nested blocks are skipped.</summary>
    internal static ProductSettings ParseSettings(string yaml)
    {
        string? full = null, root = null;
        var repair = false;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '#' or '-') continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = Unquote(line[(colon + 1)..].Trim());
            switch (key)
            {
                case "product_install_full_path": full = NormalizePath(value); break;
                case "product_install_root": root = NormalizePath(value); break;
                case "should_repair": repair = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        return new ProductSettings(full, root, repair);
    }

    internal static bool ParseUpdateStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.Object)
                return false;
            return IsTrue(status, "updateAvailable") || IsTrue(status, "updateRequired");
        }
        catch { return false; }
    }

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Replace('/', '\\').TrimEnd('\\');
        try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null; }
        catch { return null; }
    }

    private static string? Unquote(string value)
    {
        if (value.Length == 0 || value is "null" or "~") return null;
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");
        return value;
    }

    private static string? ReadText(string? path)
    {
        if (path is null) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxTextBytes) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }
}
