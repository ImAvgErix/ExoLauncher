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

    public static string[] AuthLogoutArgs() => ["auth", "--logout"];

    /// <summary>
    /// The signed-in Amazon name from Nile's user.json (the customer_info Amazon
    /// returned at sign-in). Tokens are not read.
    /// </summary>
    public static string? ReadDisplayName(IEnumerable<string> configRoots, Func<string, string?> readText)
    {
        foreach (var root in configRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var json = readText(Path.Combine(root, "user.json"));
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("extensions", out var extensions) &&
                    extensions.ValueKind == JsonValueKind.Object &&
                    extensions.TryGetProperty("customer_info", out var customer) &&
                    customer.ValueKind == JsonValueKind.Object &&
                    (ReadString(customer, "given_name") ?? ReadString(customer, "name"))?.Trim() is { Length: > 0 and <= 128 } name)
                    return name;
            }
            catch (JsonException) { /* next root */ }
        }
        return null;
    }

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

    public static string[] LaunchArgs(string productId, string? extraArgs = null)
    {
        var extra = WindowsCommandLine.Parse(extraArgs);
        // Nile uses argparse.parse_known_args; -- prevents game options from
        // being consumed as Nile's --wrapper, --json, --wine, etc. Its main
        // function still consumes these two debug flags from unknown args.
        if (extra.Any(value => value is "-d" or "--debug"))
            throw new ArgumentException("Nile reserves -d and --debug and cannot forward them to this game. Remove those launch arguments.");
        return extra.Count == 0 ? ["launch", productId] : ["launch", productId, "--", .. extra];
    }

    public static string[] UninstallArgs(string productId) => ["uninstall", productId];

    /// <summary>Nile's "Game isn't installed": it removed nothing, and says so with exit code 0.</summary>
    public static bool NotInstalled(string? output) =>
        output is not null && output.Contains("isn't installed", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// The folder Nile itself uses when Exo runs it (NILE_CONFIG_PATH, else
    /// XDG_CONFIG_HOME, else roaming AppData, each with <c>nile</c>): the first of <see cref="ConfigRoots"/>.
    /// </summary>
    public static string OwnConfigRoot() => ConfigRoots().First();

    public static bool HasLocalSession(
        IEnumerable<string> configRoots,
        Func<string, bool> fileExists,
        Func<string, string?> readText) =>
        SessionRoot(configRoots, fileExists, readText) is not null;

    /// <summary>The first config folder that holds a Nile session; only its library is the signed-in account's.</summary>
    public static string? SessionRoot(
        IEnumerable<string> configRoots,
        Func<string, bool> fileExists,
        Func<string, string?> readText)
    {
        foreach (var root in configRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var currentUser = Path.Combine(root, "current_user.json");
            if (fileExists(currentUser) && IsCurrentUserSession(readText(currentUser)))
                return root;
            var legacyUser = Path.Combine(root, "user.json");
            if (fileExists(legacyUser) && IsLegacyUserSession(readText(legacyUser)))
                return root;
        }
        return null;
    }

    /// <summary>
    /// The signed-in Amazon account id from Nile's session files
    /// (current_user.json user_id, or user.json customer_id /
    /// extensions.customer_info.user_id); "session" when a session exists but
    /// names no id; null when signed out.
    /// </summary>
    public static string? ActiveAccountId(IEnumerable<string> configRoots, Func<string, string?> readText)
    {
        foreach (var root in configRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var current = readText(Path.Combine(root, "current_user.json"));
            if (IsCurrentUserSession(current))
                return ReadAccountId(current, ["user_id"], ["userId"]) ?? "session";
            var legacy = readText(Path.Combine(root, "user.json"));
            if (IsLegacyUserSession(legacy))
                return ReadAccountId(legacy, ["customer_id"], ["extensions", "customer_info", "user_id"]) ?? "session";
        }
        return null;
    }

    private static string? ReadAccountId(string? json, params string[][] paths)
    {
        try
        {
            using var doc = JsonDocument.Parse(json!);
            foreach (var path in paths)
            {
                var at = doc.RootElement;
                var found = true;
                foreach (var name in path)
                {
                    if (at.ValueKind != JsonValueKind.Object || !at.TryGetProperty(name, out at)) { found = false; break; }
                }
                if (found && at.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(at.GetString()))
                    return at.GetString()!.Trim();
            }
        }
        catch (JsonException) { }
        return null;
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
                if (el.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number && sizeEl.TryGetInt64(out var bytes))
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
        // Another folder's library.json may be a different account's (Heroic's, or one signed out).
        if (SessionRoot(configRoots, fileExists, readText) is not { } root) return Array.Empty<GameRow>();
        var libraryPath = Path.Combine(root, "library.json");
        if (!fileExists(libraryPath)) return Array.Empty<GameRow>();
        var owned = ParseLibraryJson(readText(libraryPath) ?? "");
        if (owned.Count == 0) return Array.Empty<GameRow>();
        var installedPath = Path.Combine(root, "installed.json");
        var installed = fileExists(installedPath)
            ? ParseInstalledJson(readText(installedPath) ?? "")
            : Array.Empty<GameRow>();
        return MergeOwnedAndInstalled(owned, installed);
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

    /// <summary>One Nile output line as Exo progress; <paramref name="status"/> is Exo's own wording.</summary>
    public static InstallProgress ToProgress(string gameId, string line, string status) => new()
    {
        GameId = gameId,
        Phase = InstallPhase.Installing,
        Percent = CliProgress.Percent(line),
        BytesPerSecond = CliProgress.DownloadBytesPerSecond(line),
        Status = status,
        CanCancel = true,
    };

    private static GameRow? MapLibraryRow(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var product = el.TryGetProperty("product", out var productEl) && productEl.ValueKind == JsonValueKind.Object
            ? productEl
            : el;
        var id = ReadString(product, "id") ?? ReadString(el, "id");
        var title = ReadString(product, "title") ?? ReadString(el, "title") ?? id;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;
        // Games are Twitch:FuelGame; in-game content and DLC entitlements are
        // not a library title.
        var productLine = ReadString(product, "productLine");
        if (!string.IsNullOrWhiteSpace(productLine) &&
            (productLine.Contains("Entitlement", StringComparison.OrdinalIgnoreCase) ||
             productLine.Contains("DLC", StringComparison.OrdinalIgnoreCase)))
            return null;
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
}
