using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Portrait and landscape key art from the Epic Games Store public catalog /
/// product pages.
///
/// Used for Riot titles (no public cover API) and as a backup when Steam's
/// classic library_600x900 path 404s. Prefer Steam library_capsule when an
/// app id exists — this path is for titles that only ship Epic/Riot art.
///
/// Matching is deliberately strict: only an exact normalised title is accepted,
/// so a search for one game can never pull another game's art.
/// </summary>
public static class EpicCatalogArt
{
    private const string Endpoint = "https://store.epicgames.com/graphql";

    /// <summary>Ask Epic to hand back a 600x900 render rather than the 1200x1600 original.</summary>
    private const string SizeQuery = "?h=900&w=600&resize=1&quality=high";

    /// <summary>Banner render size. Epic keeps the source aspect, so this stays landscape.</summary>
    private const string WideSizeQuery = "?h=720&w=1280&resize=1&quality=high";

    /// <summary>Portrait renders Epic publishes at 1200×1600.</summary>
    private static readonly string[] TallMarkers = ["1200x1600"];

    /// <summary>Landscape store-front renders, widest first.</summary>
    private static readonly string[] WideMarkers = ["2560x1440", "1920x1080", "1280x720"];

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
        ["teamfight tactics"] =
            "https://cdn2.unrealengine.com/egs-teamfighttactics-riotgames-s2-1200x1600-c59beab2d1cc.jpg",
        ["tft"] =
            "https://cdn2.unrealengine.com/egs-teamfighttactics-riotgames-s2-1200x1600-c59beab2d1cc.jpg",
        ["legends of runeterra"] =
            "https://cdn2.unrealengine.com/egs-legendsofruneterra-riotgames-s2-1200x1600-cb50412d551e.jpg",
        ["legendsofruneterra"] =
            "https://cdn2.unrealengine.com/egs-legendsofruneterra-riotgames-s2-1200x1600-cb50412d551e.jpg",
        ["fortnite"] =
            "https://cdn1.epicgames.com/item/fn/FNBR_41-00_C7S3_EGS_Launcher_Blade_1200x1600_1200x1600-bb85122f4d784ace973ba1f147d76711",
        ["fortnite battle royale"] =
            "https://cdn1.epicgames.com/item/fn/FNBR_41-00_C7S3_EGS_Launcher_Blade_1200x1600_1200x1600-bb85122f4d784ace973ba1f147d76711",
    };

    /// <summary>Known Epic product slugs when title→slug is ambiguous.</summary>
    private static readonly Dictionary<string, string> SeedProductSlugs = new(StringComparer.Ordinal)
    {
        ["valorant"] = "valorant",
        ["league of legends"] = "league-of-legends",
        ["teamfight tactics"] = "teamfight-tactics",
        ["tft"] = "teamfight-tactics",
        ["legends of runeterra"] = "legends-of-runeterra",
        ["fortnite"] = "fortnite",
        ["fortnite battle royale"] = "fortnite",
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

    /// <summary>Consecutive refused calls before the catalog is paused.</summary>
    internal const int RefusalLimit = 3;

    private static int _refusals;
    private static int _pauses;
    private static long _pausedUntilTicks;

    /// <summary>
    /// True only while a refusal pause is still running. Epic answering 403 says
    /// nothing about whether the art exists, so the pause expires on its own
    /// instead of killing Epic art for the whole session.
    /// </summary>
    public static bool IsBlocked =>
        Volatile.Read(ref _pausedUntilTicks) > DateTimeOffset.UtcNow.UtcTicks;

    /// <summary>How long each successive pause lasts. Bounded, never permanent.</summary>
    internal static TimeSpan RefusalBackoffFor(int pauses) => pauses switch
    {
        <= 1 => TimeSpan.FromMinutes(5),
        2 => TimeSpan.FromMinutes(10),
        3 => TimeSpan.FromMinutes(20),
        _ => TimeSpan.FromMinutes(30),
    };

    /// <summary>One refused catalog call. Enough in a row pause GraphQL for a window.</summary>
    internal static void NoteRefusal()
    {
        if (Interlocked.Increment(ref _refusals) < RefusalLimit) return;
        Volatile.Write(ref _refusals, 0);
        var window = RefusalBackoffFor(Interlocked.Increment(ref _pauses));
        Volatile.Write(ref _pausedUntilTicks, DateTimeOffset.UtcNow.Add(window).UtcTicks);
        AppLog.Warn(
            $"Epic catalog is refusing requests; pausing catalog lookups for {window.TotalMinutes:0} min. "
            + "Local Epic catalog art still applies.");
    }

    /// <summary>A call that got through clears the streak and any running pause.</summary>
    internal static void ResetBackoff()
    {
        Volatile.Write(ref _refusals, 0);
        Volatile.Write(ref _pauses, 0);
        Volatile.Write(ref _pausedUntilTicks, 0);
    }

    public static Task<string?> FindPortraitUrlAsync(
        HttpClient http, string title, CancellationToken ct = default) =>
        FindPortraitUrlAsync(http, title, extraKeys: null, ct);

    public static Task<string?> FindPortraitUrlAsync(
        HttpClient http,
        string title,
        IEnumerable<string?>? extraKeys,
        CancellationToken ct = default) =>
        FindArtUrlAsync(http, title, extraKeys, wide: false, ct);

    /// <summary>Landscape key art for banners. Offline catalog first, then the store.</summary>
    public static Task<string?> FindWideUrlAsync(
        HttpClient http,
        string title,
        IEnumerable<string?>? extraKeys,
        CancellationToken ct = default) =>
        FindArtUrlAsync(http, title, extraKeys, wide: true, ct);

    private static async Task<string?> FindArtUrlAsync(
        HttpClient http,
        string title,
        IEnumerable<string?>? extraKeys,
        bool wide,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) && extraKeys is null) return null;
        var wanted = Normalize(title ?? "");
        var lookup = extraKeys is null
            ? new[] { title }
            : extraKeys.Prepend(title).ToArray();

        // Local EGL / Legendary cache first — works offline and during a pause.
        var local = wide
            ? EpicCatCacheArt.FindWideUrl(lookup)
            : EpicCatCacheArt.FindPortraitUrl(lookup);
        if (local is not null) return local;

        foreach (var key in lookup)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var normalized = Normalize(key);
            if (normalized.Length < 2) continue;
            if (!wide && SeedPortraitUrls.TryGetValue(normalized, out var seeded))
                return AppendSize(seeded, SizeQuery);
            if (wanted.Length < 2) wanted = normalized;
        }

        if (wanted.Length < 2) return null;

        var missKey = (wide ? "wide|" : "tall|") + wanted;
        lock (MissLock)
        {
            if (Misses.Contains(missKey)) return null;
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
                var fromGraph = await QueryAsync(http, title ?? wanted, wanted, wide, ct)
                    .ConfigureAwait(false);
                if (fromGraph is not null) return fromGraph;
            }

            // Product content API survives GraphQL 403 and covers Riot titles.
            return await QueryStoreContentAsync(http, title ?? wanted, missKey, wide, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            Throttle.Release();
        }
    }

    /// <summary>Official 1200×1600 portraits that do not need GraphQL or catcache.</summary>
    public static string? TrySeedPortraitUrl(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var wanted = Normalize(title);
        return SeedPortraitUrls.TryGetValue(wanted, out var url) ? AppendSize(url, SizeQuery) : null;
    }

    /// <summary>Public for tests — product slug used against store-content API.</summary>
    public static string ProductSlug(string title)
    {
        var key = Normalize(title);
        if (SeedProductSlugs.TryGetValue(key, out var seeded)) return seeded;
        return key.Replace(' ', '-');
    }

    private static async Task<string?> QueryAsync(
        HttpClient http, string title, string wanted, bool wide, CancellationToken ct)
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
                AppLog.Debug($"Epic catalog {(int)resp.StatusCode} for '{title}'.");
                NoteRefusal();
                return null;
            }
            ResetBackoff();
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
                var hit = EpicCatCacheArt.PickKeyImage(images, wide);
                if (hit is not null) return AppendSize(hit, wide ? WideSizeQuery : SizeQuery);
            }
            // Do not record a miss here — store-content may still have the art.
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Epic catalog art lookup failed for '{title}': {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Epic store-content product pages embed the official portrait and
    /// store-front renders even when GraphQL is blocked (Riot titles live here).
    /// </summary>
    private static async Task<string?> QueryStoreContentAsync(
        HttpClient http, string title, string missKey, bool wide, CancellationToken ct)
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
                // 404 means this slug is not an Epic product; that is per-title
                // and worth remembering. Anything else may be transient.
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    lock (MissLock) Misses.Add(missKey);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var hit = FindInProductJson(doc.RootElement, wide);
            if (hit is not null)
            {
                AppLog.Info($"Cover: Epic store-content {(wide ? "banner" : "portrait")} for '{title}'.");
                return AppendSize(hit, wide ? WideSizeQuery : SizeQuery);
            }
            lock (MissLock) Misses.Add(missKey);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Epic store-content art failed for '{title}': {ex.Message}");
        }
        return null;
    }

    private static string? FindInProductJson(JsonElement root, bool wide)
    {
        var preferred = wide
            ? new[] { "backgroundImageUrl", "landscapeBackgroundImageUrl", "src", "url" }
            : new[] { "portraitBackgroundImageUrl", "src", "url" };
        var strings = WalkStrings(root, preferred).ToList();
        foreach (var marker in wide ? WideMarkers : TallMarkers)
        {
            foreach (var candidate in strings)
            {
                if (!candidate.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
                if (CoverArtService.IsOfficialEpicPortraitCdn(candidate)) return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> WalkStrings(JsonElement el, string[] preferredKeys)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                // Prefer known art keys first.
                foreach (var key in preferredKeys)
                {
                    if (el.TryGetProperty(key, out var preferred) &&
                        preferred.ValueKind == JsonValueKind.String &&
                        preferred.GetString() is { Length: > 0 } ps)
                        yield return ps;
                }
                foreach (var prop in el.EnumerateObject())
                {
                    foreach (var s in WalkStrings(prop.Value, preferredKeys))
                        yield return s;
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in el.EnumerateArray())
                {
                    foreach (var s in WalkStrings(child, preferredKeys))
                        yield return s;
                }
                break;
            case JsonValueKind.String:
                if (el.GetString() is { Length: > 0 } s0)
                    yield return s0;
                break;
        }
    }

    private static string AppendSize(string url, string query) =>
        url.Contains('?', StringComparison.Ordinal) ? url : url + query;

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
