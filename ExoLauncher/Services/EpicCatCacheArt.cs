using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Offline portrait and landscape URLs from Epic Games Launcher's local catalog
/// cache (<c>catcache.bin</c> — base64 JSON of entitlements with keyImages).
/// Read before any network call, and the only Epic source left once the store
/// starts refusing requests.
/// </summary>
public static class EpicCatCacheArt
{
    private const string SizeQuery = "?h=900&w=600&resize=1&quality=high";

    /// <summary>Banner render size. Epic keeps the source aspect, so this stays landscape.</summary>
    private const string WideSizeQuery = "?h=720&w=1280&resize=1&quality=high";

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _tallByTitle;
    private static Dictionary<string, string>? _wideByTitle;
    private static DateTime _loadedUtc = DateTime.MinValue;

    public static string? FindPortraitUrl(params string?[] keys)
    {
        EnsureLoaded();
        Dictionary<string, string>? map;
        lock (Gate) map = _tallByTitle;
        return Lookup(map, keys);
    }

    /// <summary>Offline landscape key art (OfferImageWide / DieselStoreFrontWide).</summary>
    public static string? FindWideUrl(params string?[] keys)
    {
        EnsureLoaded();
        Dictionary<string, string>? map;
        lock (Gate) map = _wideByTitle;
        return Lookup(map, keys);
    }

    private static string? Lookup(Dictionary<string, string>? map, string?[] keys)
    {
        if (map is null || map.Count == 0) return null;
        foreach (var lookup in PortraitLookupKeys(keys))
        {
            if (map.TryGetValue(lookup, out var url)) return url;
        }

        return null;
    }

    /// <summary>Public for tests — indexes one catcache entitlement object's tall art.</summary>
    internal static void IndexCatalogElement(Dictionary<string, string> map, string json)
    {
        using var doc = JsonDocument.Parse(json);
        TryAdd(map, wide: null, doc.RootElement);
    }

    /// <summary>Public for tests — indexes one catcache entitlement object's wide art.</summary>
    internal static void IndexCatalogElementWide(Dictionary<string, string> wide, string json)
    {
        using var doc = JsonDocument.Parse(json);
        TryAdd(tall: null, wide, doc.RootElement);
    }

    internal static IEnumerable<string> PortraitLookupKeys(params string?[] raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            var wanted = Normalize(item);
            if (wanted.Length < 2) continue;
            foreach (var key in TitleLookupKeys(wanted))
            {
                if (seen.Add(key)) yield return key;
            }
        }
    }

    internal static IEnumerable<string> TitleLookupKeys(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)) yield break;
        yield return normalizedTitle;
        if (normalizedTitle is "fortnite" or "fortnite battle royale" or "fn" or "fortniteclient")
        {
            yield return "fortnite";
            yield return "fortnite battle royale";
        }
        if (normalizedTitle is "teamfight tactics" or "tft" or "lion")
        {
            yield return "teamfight tactics";
            yield return "tft";
        }
        if (normalizedTitle is "legends of runeterra" or "legendsofruneterra" or "lor" or "bacon")
        {
            yield return "legends of runeterra";
            yield return "legendsofruneterra";
        }
        if (normalizedTitle is "league of legends" or "lol")
            yield return "league of legends";
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            // Refresh at most every 10 minutes — EGL rewrites the file on sync.
            if (_tallByTitle is not null && DateTime.UtcNow - _loadedUtc < TimeSpan.FromMinutes(10))
                return;
            var tall = new Dictionary<string, string>(StringComparer.Ordinal);
            var wide = new Dictionary<string, string>(StringComparer.Ordinal);
            LoadMaps(tall, wide);
            _tallByTitle = tall;
            _wideByTitle = wide;
            _loadedUtc = DateTime.UtcNow;
            if (tall.Count > 0 || wide.Count > 0)
                AppLog.Debug($"Epic catcache: {tall.Count} tall, {wide.Count} wide covers.");
        }
    }

    private static void LoadMaps(Dictionary<string, string> tall, Dictionary<string, string> wide)
    {
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
                    TryAdd(tall, wide, el);
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Epic catcache read failed ({path}): {ex.Message}");
            }
        }

        // Legendary offline metadata (same keyImages when present).
        TryLoadLegendaryMetadata(tall, wide);
    }

    private static void TryAdd(
        Dictionary<string, string>? tall, Dictionary<string, string>? wide, JsonElement el)
    {
        var title = el.TryGetProperty("title", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(title)) return;
        // Skip marketplace / engine noise.
        if (CoverArtService.LooksLikeEngineAsset(title)) return;

        if (!el.TryGetProperty("keyImages", out var images) ||
            images.ValueKind != JsonValueKind.Array)
            return;

        var tallUrl = PickKeyImage(images, wantWide: false);
        var wideUrl = PickKeyImage(images, wantWide: true);
        if (tall is not null && tallUrl is not null)
            IndexEveryKey(tall, el, title, Sized(tallUrl, SizeQuery));
        if (wide is not null && wideUrl is not null)
            IndexEveryKey(wide, el, title, Sized(wideUrl, WideSizeQuery));
    }

    /// <summary>
    /// Best tall or wide keyImage. Tall keeps first-listed order (that is the
    /// shipped portrait choice); wide is ranked because a store front image is a
    /// banner while a box-art wide is often just a crop.
    /// </summary>
    internal static string? PickKeyImage(JsonElement images, bool wantWide)
    {
        string? best = null;
        var bestRank = 0;
        foreach (var img in images.EnumerateArray())
        {
            // Legendary writes lowercase keys; hand-rolled metadata sometimes does not.
            var type = img.TryGetProperty("type", out var ty) ? ty.GetString() ?? ""
                : img.TryGetProperty("Type", out var ty2) ? ty2.GetString() ?? ""
                : "";
            var src = img.TryGetProperty("url", out var u) ? u.GetString()
                : img.TryGetProperty("Url", out var u2) ? u2.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(src)) continue;
            var rank = wantWide ? WideRank(type) : TallRank(type, src);
            if (rank <= bestRank) continue;
            bestRank = rank;
            best = src;
        }
        return best;
    }

    private static int TallRank(string type, string url)
    {
        if (type.Contains("Tall", StringComparison.OrdinalIgnoreCase)) return 3;
        if (url.Contains("1200x1600", StringComparison.OrdinalIgnoreCase)) return 2;
        return 0;
    }

    private static int WideRank(string type)
    {
        if (type.Contains("Tall", StringComparison.OrdinalIgnoreCase)) return 0;
        if (!type.Contains("Wide", StringComparison.OrdinalIgnoreCase)) return 0;
        if (type.Contains("OfferImageWide", StringComparison.OrdinalIgnoreCase)) return 4;
        if (type.Contains("DieselStoreFrontWide", StringComparison.OrdinalIgnoreCase)) return 3;
        if (type.Contains("DieselGameBoxWide", StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }

    private static string Sized(string url, string query) =>
        url.Contains('?', StringComparison.Ordinal) ? url : url + query;

    private static void IndexEveryKey(
        Dictionary<string, string> map, JsonElement el, string title, string url)
    {
        Index(map, title, url);
        if (el.TryGetProperty("entitlementName", out var ent) &&
            ent.GetString() is { Length: > 0 } entitlement)
            Index(map, entitlement, url);
        if (el.TryGetProperty("id", out var idEl) &&
            idEl.GetString() is { Length: > 0 } id)
            Index(map, id, url);
        if (el.TryGetProperty("releaseInfo", out var release) &&
            release.ValueKind == JsonValueKind.Array)
        {
            foreach (var info in release.EnumerateArray())
            {
                if (info.ValueKind != JsonValueKind.Object) continue;
                if (info.TryGetProperty("appId", out var appEl) &&
                    appEl.GetString() is { Length: > 0 } appId)
                    Index(map, appId, url);
            }
        }
    }

    private static void Index(Dictionary<string, string> map, string name, string url)
    {
        var key = Normalize(name);
        if (key.Length < 2 || map.ContainsKey(key)) return;
        map[key] = url;
    }

    private static void TryLoadLegendaryMetadata(
        Dictionary<string, string> tall, Dictionary<string, string> wide)
    {
        try
        {
            foreach (var dir in LegendaryMetadataDirs())
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        var root = doc.RootElement;
                        // Legendary wraps catalog under "metadata" sometimes.
                        if (root.TryGetProperty("metadata", out var meta))
                            TryAddLegendary(tall, wide, meta);
                        else
                            TryAddLegendary(tall, wide, root);
                    }
                    catch { /* skip one file */ }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Legendary metadata art scan failed: " + ex.Message);
        }
    }

    private static IEnumerable<string> LegendaryMetadataDirs()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            yield return Path.Combine(profile, ".config", "legendary", "metadata");
            yield return Path.Combine(profile, ".config", "heroic", "legendaryConfig", "legendary", "metadata");
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roaming))
        {
            yield return Path.Combine(roaming, "legendary", "metadata");
            yield return Path.Combine(roaming, "heroic", "legendaryConfig", "legendary", "metadata");
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            yield return Path.Combine(local, "legendary", "metadata");
            yield return Path.Combine(local, "heroic", "legendaryConfig", "legendary", "metadata");
            yield return Path.Combine(local, "ExoLauncher", "legendary", "metadata");
        }
    }

    private static void TryAddLegendary(
        Dictionary<string, string> tall, Dictionary<string, string> wide, JsonElement root)
    {
        var title = root.TryGetProperty("title", out var t) ? t.GetString()
            : root.TryGetProperty("Title", out var t2) ? t2.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(title) || CoverArtService.LooksLikeEngineAsset(title)) return;
        if (!root.TryGetProperty("keyImages", out var images) &&
            !root.TryGetProperty("KeyImages", out images))
            return;
        if (images.ValueKind != JsonValueKind.Array) return;

        var tallUrl = PickKeyImage(images, wantWide: false);
        if (tallUrl is not null) Index(tall, title, Sized(tallUrl, SizeQuery));
        var wideUrl = PickKeyImage(images, wantWide: true);
        if (wideUrl is not null) Index(wide, title, Sized(wideUrl, WideSizeQuery));
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EpicGamesLauncher", "Saved", "Catalog", "catcache.bin");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EpicGamesLauncher", "Saved", "CatCache", "catcache.bin");
    }

    private static string Normalize(string s)
    {
        var chars = s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
