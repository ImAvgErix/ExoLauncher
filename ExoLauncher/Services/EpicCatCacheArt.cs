using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Offline portrait URLs from Epic Games Launcher's local catalog cache
/// (<c>catcache.bin</c> — base64 JSON of entitlements with keyImages).
/// Used when store GraphQL refuses requests (403) or before any network call.
/// </summary>
public static class EpicCatCacheArt
{
    private const string SizeQuery = "?h=900&w=600&resize=1&quality=high";

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _tallByTitle;
    private static DateTime _loadedUtc = DateTime.MinValue;

    public static string? FindPortraitUrl(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var wanted = Normalize(title);
        if (wanted.Length < 2) return null;

        EnsureLoaded();
        Dictionary<string, string>? map;
        lock (Gate) map = _tallByTitle;
        if (map is null || map.Count == 0) return null;
        return map.TryGetValue(wanted, out var url) ? url : null;
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            // Refresh at most every 10 minutes — EGL rewrites the file on sync.
            if (_tallByTitle is not null && DateTime.UtcNow - _loadedUtc < TimeSpan.FromMinutes(10))
                return;
            _tallByTitle = LoadMap();
            _loadedUtc = DateTime.UtcNow;
            if (_tallByTitle.Count > 0)
                AppLog.Debug($"Epic catcache: {_tallByTitle.Count} tall covers.");
        }
    }

    private static Dictionary<string, string> LoadMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var info = new FileInfo(path);
                if (info.Length < 64 || info.Length > 40 * 1024 * 1024) continue;
                var b64 = File.ReadAllText(path).Trim();
                if (b64.Length < 8) continue;
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var el in doc.RootElement.EnumerateArray())
                    TryAdd(map, el);
                if (map.Count > 0) return map;
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Epic catcache read failed ({path}): {ex.Message}");
            }
        }

        // Legendary offline metadata (same tall keyImages when present).
        TryLoadLegendaryMetadata(map);
        return map;
    }

    private static void TryAdd(Dictionary<string, string> map, JsonElement el)
    {
        var title = el.TryGetProperty("title", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(title)) return;
        // Skip marketplace / engine noise.
        if (CoverArtService.LooksLikeEngineAsset(title)) return;

        if (!el.TryGetProperty("keyImages", out var images) ||
            images.ValueKind != JsonValueKind.Array)
            return;

        string? tall = null;
        foreach (var img in images.EnumerateArray())
        {
            var type = img.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
            if (!type.Contains("Tall", StringComparison.OrdinalIgnoreCase) &&
                !type.Contains("DieselGameBoxTall", StringComparison.OrdinalIgnoreCase) &&
                !type.Contains("OfferImageTall", StringComparison.OrdinalIgnoreCase))
                continue;
            var src = img.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(src)) continue;
            tall = src;
            break;
        }
        if (tall is null) return;

        var key = Normalize(title);
        if (key.Length < 2 || map.ContainsKey(key)) return;
        map[key] = tall.Contains('?', StringComparison.Ordinal) ? tall : tall + SizeQuery;
    }

    private static void TryLoadLegendaryMetadata(Dictionary<string, string> map)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "legendary", "metadata");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    // Legendary wraps catalog under "metadata" sometimes.
                    if (root.TryGetProperty("metadata", out var meta))
                        TryAddLegendary(map, meta);
                    else
                        TryAddLegendary(map, root);
                }
                catch { /* skip one file */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Legendary metadata art scan failed: " + ex.Message);
        }
    }

    private static void TryAddLegendary(Dictionary<string, string> map, JsonElement root)
    {
        var title = root.TryGetProperty("title", out var t) ? t.GetString()
            : root.TryGetProperty("Title", out var t2) ? t2.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(title) || CoverArtService.LooksLikeEngineAsset(title)) return;
        if (!root.TryGetProperty("keyImages", out var images) &&
            !root.TryGetProperty("KeyImages", out images))
            return;
        if (images.ValueKind != JsonValueKind.Array) return;

        foreach (var img in images.EnumerateArray())
        {
            var type = img.TryGetProperty("type", out var ty) ? ty.GetString()
                : img.TryGetProperty("Type", out var ty2) ? ty2.GetString()
                : "";
            type ??= "";
            if (!type.Contains("Tall", StringComparison.OrdinalIgnoreCase)) continue;
            var src = img.TryGetProperty("url", out var u) ? u.GetString()
                : img.TryGetProperty("Url", out var u2) ? u2.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(src)) continue;
            var key = Normalize(title);
            if (key.Length < 2 || map.ContainsKey(key)) return;
            map[key] = src.Contains('?', StringComparison.Ordinal) ? src : src + SizeQuery;
            return;
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EpicGamesLauncher", "Saved", "Catalog", "catcache.bin");
    }

    private static string Normalize(string s)
    {
        var chars = s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
