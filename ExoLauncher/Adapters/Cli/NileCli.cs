using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// Pure helpers for Nile argv, library JSON, session files, and stdout progress.
/// Nile is the Heroic Amazon Games backend: token stays in Nile, never in Exo.
/// https://github.com/imLinguin/nile
/// </summary>
public static partial class NileCli
{
    public sealed record GameRow(
        string ProductId,
        string Title,
        string? InstallPath,
        long? SizeBytes,
        bool Installed);

    public static string[] AuthLoginArgs() => ["auth", "--login"];

    public static string[] AuthStatusArgs() => ["auth", "--status"];

    public static string[] LibraryListArgs() => ["library", "list", "--json"];

    public static string[] LibrarySyncArgs() => ["library", "sync"];

    public static string[] InstallArgs(string productId, string? basePath = null)
    {
        var args = new List<string> { "install", productId };
        if (!string.IsNullOrWhiteSpace(basePath))
        {
            args.Add("--base-path");
            args.Add(basePath);
        }
        return args.ToArray();
    }

    public static string[] UpdateArgs(string productId) => ["update", productId];

    public static string[] VerifyArgs(string productId) => ["verify", productId];

    public static string[] LaunchArgs(string productId) => ["launch", productId];

    public static string[] UninstallArgs(string productId) => ["uninstall", productId];

    public static bool HasAnyBinary() =>
        HasAnyBinary(File.Exists, BinaryCandidates());

    public static bool HasAnyBinary(Func<string, bool> fileExists, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && fileExists(candidate))
                return true;
        }
        return false;
    }

    public static IEnumerable<string> BinaryCandidates()
    {
        foreach (var name in new[] { "nile.exe", "nile" })
        {
            var resolved = CliRunner.ResolveOnPath(name);
            if (!string.IsNullOrWhiteSpace(resolved))
                yield return resolved;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(local, "ExoLauncher", "tools", "nile.exe");
        yield return Path.Combine(local, "nile", "nile.exe");
        yield return Path.Combine(local, "heroic", "nile.exe");
        yield return Path.Combine(roaming, "heroic", "nile.exe");
    }

    public static IEnumerable<string> ConfigRoots()
    {
        var nileConfig = Environment.GetEnvironmentVariable("NILE_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(nileConfig))
            yield return Path.Combine(nileConfig, "nile");

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            yield return Path.Combine(xdg, "nile");

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(roaming, "nile");
        yield return Path.Combine(roaming, "heroic", "nile_config", "nile");
        yield return Path.Combine(local, "heroic", "nile_config", "nile");
        yield return Path.Combine(user, ".config", "nile");
        yield return Path.Combine(user, ".config", "heroic", "nile_config", "nile");
    }

    public static bool HasLocalSession() =>
        HasLocalSession(ConfigRoots(), File.Exists, path =>
        {
            try { return File.ReadAllText(path); }
            catch { return null; }
        });

    public static bool HasLocalSession(
        IEnumerable<string> configRoots,
        Func<string, bool> fileExists,
        Func<string, string?> readText)
    {
        foreach (var root in configRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var currentUser = Path.Combine(root, "current_user.json");
            if (fileExists(currentUser) && IsCurrentUserSession(readText(currentUser)))
                return true;
            var legacyUser = Path.Combine(root, "user.json");
            if (fileExists(legacyUser) && IsLegacyUserSession(readText(legacyUser)))
                return true;
        }
        return false;
    }

    public static bool IsCurrentUserSession(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var userId = ReadString(doc.RootElement, "user_id")
                         ?? ReadString(doc.RootElement, "userId");
            return !string.IsNullOrWhiteSpace(userId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsLegacyUserSession(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsAuthenticatedStatusResponse(int exitCode, string stdout)
    {
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return false;
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(stdout));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (doc.RootElement.TryGetProperty("LoggedIn", out var loggedIn))
            {
                return loggedIn.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => loggedIn.GetString() is "true" or "True" or "1",
                    _ => false,
                };
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IReadOnlyList<GameRow> ParseLibraryJson(string json)
    {
        var rows = new List<GameRow>();
        if (string.IsNullOrWhiteSpace(json)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var row = MapLibraryRow(el);
                if (row is not null) rows.Add(row);
            }
        }
        catch (JsonException)
        {
            return rows;
        }
        return rows;
    }

    public static IReadOnlyList<GameRow> ParseInstalledJson(string json)
    {
        var rows = new List<GameRow>();
        if (string.IsNullOrWhiteSpace(json)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(el, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var path = ReadString(el, "path");
                long? size = null;
                if (el.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var bytes))
                    size = bytes;
                rows.Add(new GameRow(id, id, path, size, true));
            }
        }
        catch (JsonException)
        {
            return rows;
        }
        return rows;
    }

    public static IReadOnlyList<GameRow> ReadCachedLibrary(
        IEnumerable<string> configRoots,
        Func<string, bool> fileExists,
        Func<string, string?> readText)
    {
        foreach (var root in configRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var libraryPath = Path.Combine(root, "library.json");
            if (!fileExists(libraryPath)) continue;
            var owned = ParseLibraryJson(readText(libraryPath) ?? "");
            if (owned.Count == 0) continue;
            var installedPath = Path.Combine(root, "installed.json");
            var installed = fileExists(installedPath)
                ? ParseInstalledJson(readText(installedPath) ?? "")
                : Array.Empty<GameRow>();
            return MergeOwnedAndInstalled(owned, installed);
        }
        return Array.Empty<GameRow>();
    }

    public static IReadOnlyList<GameRow> MergeOwnedAndInstalled(
        IEnumerable<GameRow> owned,
        IEnumerable<GameRow> installed)
    {
        var map = new Dictionary<string, GameRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in owned)
            map[row.ProductId] = row with { Installed = false, InstallPath = null, SizeBytes = null };
        foreach (var row in installed)
        {
            if (map.TryGetValue(row.ProductId, out var ownedRow))
            {
                map[row.ProductId] = ownedRow with
                {
                    Installed = true,
                    InstallPath = row.InstallPath,
                    SizeBytes = row.SizeBytes ?? ownedRow.SizeBytes,
                };
            }
            else
            {
                map[row.ProductId] = row with { Installed = true };
            }
        }
        return map.Values.OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Nile progress: <c>= Progress: 45.23 12345/67890</c> and
    /// <c>+ Download - 12.34 MiB/s</c>.
    /// </summary>
    public static bool TryParseProgressLine(
        string line,
        out double? percent,
        out double? bytesPerSecond,
        out string status)
    {
        percent = null;
        bytesPerSecond = null;
        status = line;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var progress = ProgressRegex().Match(line);
        if (progress.Success &&
            double.TryParse(progress.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            percent = Math.Clamp(pct, 0, 100);
        }

        var speed = SpeedRegex().Match(line);
        if (speed.Success &&
            double.TryParse(speed.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            var scale = speed.Groups[2].Value.ToLowerInvariant() switch
            {
                "kib" or "kb" => 1024d,
                "mib" or "mb" => 1024d * 1024,
                "gib" or "gb" => 1024d * 1024 * 1024,
                _ => 1d,
            };
            bytesPerSecond = rate * scale;
        }

        return percent is not null || bytesPerSecond is not null;
    }

    public static InstallProgress ToProgress(string gameId, string line)
    {
        TryParseProgressLine(line, out var pct, out var bps, out var status);
        return new InstallProgress
        {
            GameId = gameId,
            Phase = InstallPhase.Installing,
            Percent = pct,
            BytesPerSecond = bps,
            Status = status,
            CanCancel = true,
        };
    }

    private static GameRow? MapLibraryRow(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var product = el.TryGetProperty("product", out var productEl) && productEl.ValueKind == JsonValueKind.Object
            ? productEl
            : el;
        var id = ReadString(product, "id") ?? ReadString(el, "id");
        var title = ReadString(product, "title") ?? ReadString(el, "title") ?? id;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;
        return new GameRow(id, title.Trim(), null, null, false);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ExtractJsonObject(string stdout)
    {
        var start = stdout.IndexOf('{');
        var end = stdout.LastIndexOf('}');
        return start >= 0 && end > start ? stdout[start..(end + 1)] : stdout;
    }

    [GeneratedRegex(@"Progress:\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*(KiB|MiB|GiB|KB|MB|GB)/s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeedRegex();
}
