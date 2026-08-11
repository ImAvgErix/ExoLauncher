using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Portrait key art from the Epic Games Store public catalog / product pages.
///
/// Used for Riot titles (no public cover API) and as a backup when Steam's
/// classic library_600x900 path 404s. Prefer Steam library_capsule when an
/// app id exists — this path is for titles that only ship Epic/Riot tall art.
///
/// Matching is deliberately strict: only an exact normalised title is accepted,
/// so a search for one game can never pull another game's art.
/// </summary>
public static class EpicCatalogArt
{
    private const string Endpoint = "https://store.epicgames.com/graphql";

    /// <summary>Ask Epic to hand back a 600x900 render rather than the 1200x1600 original.</summary>
    private const string SizeQuery = "?h=900&w=600&resize=1&quality=high";

    /// <summary>
    /// Official 1200×1600 portraits for titles where GraphQL/catcache often miss
    /// (Riot installs are not Epic entitlements).
    /// </summary>
    private static readonly Dictionary<string, string> SeedPortraitUrls = new(StringComparer.Ordinal)
    {
        ["valorant"] =
            "https://cdn2.unrealengine.com/egs-valorant-riotgames-s2-1200x1600-16f0fd604676.jpg",
        ["league of legends"] =
            "https://cdn1.epicgames.com/offer/24b9b5e323bc40eea252a10cdd3b2f10/"
            + "EGS_LeagueofLegends_RiotGames_S2_1200x1600-112729f9da450fe377e11d40029c4831",
    };

    /// <summary>Known Epic product slugs when title→slug is ambiguous.</summary>
    private static readonly Dictionary<string, string> SeedProductSlugs = new(StringComparer.Ordinal)
    {
        ["valorant"] = "valorant",
        ["league of legends"] = "league-of-legends",
        ["wuthering waves"] = "wuthering-waves",
    };

    /// <summary>
    /// One request at a time, spaced out. The cover warm runs eight titles in
    /// parallel, and firing that at Epic got every lookup rejected — including
    /// ones that succeed fine on their own.
    /// </summary>
    private static readonly SemaphoreSlim Throttle = new(1, 1);
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(220);
    private static DateTimeOffset _lastCall = DateTimeOffset.MinValue;

    /// <summary>Titles Epic has no art for; avoids re-asking on every rescan.</summary>
    private static readonly HashSet<string> Misses = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object MissLock = new();

    /// <summary>
    /// Epic GraphQL answers 403 once it decides a client is scraping.
    /// Store-content product pages are a separate host and often still work.
    /// </summary>
    private static int _refusals;
    private const int RefusalLimit = 3;

    public static bool IsBlocked => Volatile.Read(ref _refusals) >= RefusalLimit;

    public static async Task<string?> FindPortraitUrlAsync(
        HttpClient http, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var wanted = Normalize(title);
        if (wanted.Length < 2) return null;

        // Local EGL / Legendary cache first — works offline and after GraphQL 403.
        var local = EpicCatCacheArt.FindPortraitUrl(title);
        if (local is not null) return local;

        if (SeedPortraitUrls.TryGetValue(wanted, out var seeded))
            return AppendSize(seeded);

        lock (MissLock)
        {
            if (Misses.Contains(wanted)) return null;
        }

        await Throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var since = DateTimeOffset.UtcNow - _lastCall;
            if (since < MinSpacing)
                await Task.Delay(MinSpacing - since, ct).ConfigureAwait(false);
            _lastCall = DateTimeOffset.UtcNow;

            if (!IsBlocked)
            {
                var fromGraph = await QueryAsync(http, title, wanted, ct).ConfigureAwait(false);
                if (fromGraph is not null) return fromGraph;
            }

            // Product content API survives GraphQL 403 and covers Riot titles.
            var fromProduct = await QueryStoreContentAsync(http, title, wanted, ct).ConfigureAwait(false);
            if (fromProduct is not null) return fromProduct;
            return null;
        }
        finally
        {
            Throttle.Release();
        }
    }

    /// <summary>Public for tests — product slug used against store-content API.</summary>
    public static string ProductSlug(string title)
    {
        var key = Normalize(title);
        if (SeedProductSlugs.TryGetValue(key, out var seeded)) return seeded;
        return key.Replace(' ', '-');
    }

    private static async Task<string?> QueryAsync(
        HttpClient http, string title, string wanted, CancellationToken ct)
    {
        try
        {
            var graph =
                "{Catalog{searchStore(keywords:\"" + Escape(title) +
                "\",count:8,country:\"US\",locale:\"en-US\")" +
                "{elements{title keyImages{type url}}}}}";

            // POST with an explicit browser-shaped header set. The GET form is
            // answered with 403 for this client, while the POST body form is not.
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { query = graph }),
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Origin", "https://store.epicgames.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://store.epicgames.com/");

            using var resp = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // Do not record a miss: a refused request says nothing about
                // whether the art exists.
                var refusals = Interlocked.Increment(ref _refusals);
                if (refusals == RefusalLimit)
                    AppLog.Warn("Epic catalog is refusing requests; skipping Epic art this session.");
                else
                    AppLog.Debug($"Epic catalog {(int)resp.StatusCode} for '{title}'.");
                return null;
            }
            Volatile.Write(ref _refusals, 0);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("Catalog", out var catalog) ||
                !catalog.TryGetProperty("searchStore", out var store) ||
                !store.TryGetProperty("elements", out var elements) ||
                elements.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var el in elements.EnumerateArray())
            {
                var name = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (!string.Equals(Normalize(name), wanted, StringComparison.Ordinal))
                    continue;
                if (!el.TryGetProperty("keyImages", out var images) ||
                    images.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var img in images.EnumerateArray())
                {
                    var type = img.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                    if (!type.Contains("Tall", StringComparison.OrdinalIgnoreCase)) continue;
                    var src = img.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(src)) continue;
                    return AppendSize(src);
                }
            }
            // Do not record a miss here — store-content may still have the portrait.
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Epic catalog art lookup failed for '{title}': {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Epic store-content product pages embed the official 1200×1600 portrait
    /// even when GraphQL is blocked (Riot titles live here).
    /// </summary>
    private static async Task<string?> QueryStoreContentAsync(
        HttpClient http, string title, string wanted, CancellationToken ct)
    {
        try
        {
            var slug = ProductSlug(title);
            if (string.IsNullOrWhiteSpace(slug) || slug.Length < 2) return null;
            var url = "https://store-content.ak.epicgames.com/api/en-US/content/products/" + slug;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Debug($"Epic store-content {(int)resp.StatusCode} for '{title}'.");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var hit = FindTallInProductJson(doc.RootElement);
            if (hit is not null)
            {
                AppLog.Info($"Cover: Epic store-content portrait for '{title}'.");
                return AppendSize(hit);
            }
            lock (MissLock) Misses.Add(wanted);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Epic store-content art failed for '{title}': {ex.Message}");
        }
        return null;
    }

    private static string? FindTallInProductJson(JsonElement root)
    {
        // Prefer explicit portrait fields, then any 1200x1600 CDN asset.
        foreach (var path in WalkStrings(root))
        {
            if (path.Contains("1200x1600", StringComparison.OrdinalIgnoreCase) &&
                (path.Contains("cdn1.epicgames.com", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains("cdn2.unrealengine.com", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains("epicgames.com", StringComparison.OrdinalIgnoreCase)))
                return path;
        }
        return null;
    }

    private static IEnumerable<string> WalkStrings(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                // Prefer known portrait keys first.
                foreach (var key in new[]
                         {
                             "portraitBackgroundImageUrl", "src", "url",
                         })
                {
                    if (el.TryGetProperty(key, out var preferred) &&
                        preferred.ValueKind == JsonValueKind.String &&
                        preferred.GetString() is { Length: > 0 } ps)
                        yield return ps;
                }
                foreach (var prop in el.EnumerateObject())
                {
                    foreach (var s in WalkStrings(prop.Value))
                        yield return s;
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in el.EnumerateArray())
                {
                    foreach (var s in WalkStrings(child))
                        yield return s;
                }
                break;
            case JsonValueKind.String:
                if (el.GetString() is { Length: > 0 } s0)
                    yield return s0;
                break;
        }
    }

    private static string AppendSize(string url) =>
        url.Contains('?', StringComparison.Ordinal) ? url : url + SizeQuery;

    private static string Escape(string s) =>
        s.Replace("\\", "", StringComparison.Ordinal)
         .Replace("\"", "", StringComparison.Ordinal);

    /// <summary>Lowercase alphanumerics only — "WUCHANG: Fallen Feathers" == "wuchang fallen feathers".</summary>
    private static string Normalize(string s)
    {
        var chars = s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
