using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Cover art: download to disk; prefer virtual-host URLs for the UI (lightweight, many tiles).
/// Steam / Epic portrait CDNs are safe first-paint stand-ins while the disk cache warms.
/// Uncached with no official CDN → null → monogram.
/// </summary>
public static class CoverArtService
{
    // Keep first paint clear of speculative cover downloads. Search explicitly
    // opts out because those results were requested by the user just now.
    internal static readonly TimeSpan FirstPaintCoverWarmDelay = TimeSpan.FromMilliseconds(50);
    internal const int BackgroundWarmConcurrency = 4;
    internal const int RequestedWarmConcurrency = 4;
    internal const int SearchWarmConcurrency = 4;
    internal const int RequestedWarmNotificationBatchSize = 4;

    internal enum ArtworkWarmIntent
    {
        Library,
        SearchPortrait,
        UserRefetch,
    }

    internal readonly record struct CacheMaintenancePolicy(
        long HighWaterBytes,
        long LowWaterBytes,
        int HighWaterFiles,
        int LowWaterFiles,
        TimeSpan MaxUnreferencedAge,
        TimeSpan MinimumEvictionAge);

    internal readonly record struct CacheMaintenanceResult(
        int ExaminedFiles,
        int DeletedFiles,
        long DeletedBytes,
        int RemainingFiles,
        long RemainingBytes);

    internal static readonly CacheMaintenancePolicy DefaultCacheMaintenancePolicy = new(
        HighWaterBytes: 512L * 1024 * 1024,
        LowWaterBytes: 384L * 1024 * 1024,
        HighWaterFiles: 2_048,
        LowWaterFiles: 1_536,
        MaxUnreferencedAge: TimeSpan.FromDays(90),
        MinimumEvictionAge: TimeSpan.FromMinutes(15));

    private static readonly HttpClient CoverHttp = CreateCoverHttpClient();
    private static readonly SemaphoreSlim SearchArtworkGate = new(
        SearchWarmConcurrency,
        SearchWarmConcurrency);
    private static int _cacheMaintenanceStarted;
    private static readonly object CacheMaintenanceGate = new();
    private static GameEntry[] _activeCacheMaintenanceLibrary = [];
    private static readonly HashSet<string> CacheImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif",
    };

    public static string CacheRoot => Path.Combine(PathHelper.AppDataDir, "covers");

    /// <summary>Legacy cache URL host. Native tiles resolve this back to a local file.</summary>
    public const string VirtualHost = "covers.exo-launcher.local";

    /// <summary>Virtual-host base URL emitted for large cache files (CSP-allowlisted).</summary>
    public static string VirtualHostOrigin => $"https://{VirtualHost}";

    /// <summary>
    /// Cap for <see cref="TryDataUrl"/> only. Grid tiles use the virtual host
    /// (or Steam CDN) — <see cref="PreferLocalArt"/> never inlines posters into RPC.
    /// </summary>
    public const int MaxDataUrlBytes = 512_000;

    /// <summary>Steam 404 placeholders are ~870B JPEGs — never treat those as covers.</summary>
    public const int MinCoverBytes = 12_000;

    /// <summary>
    /// Vertical Steam library posters only (2:3). Prefer 1x tile size so WebView
    /// does not decode 1200×1800 bitmaps for 200px cards. 2x is last-resort.
    /// Never header/capsule — those are landscape and look bad on portrait cards.
    /// </summary>
    private static readonly string[] SteamPosterTemplates =
    [
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900.jpg",
        "https://cdn.akamai.steamstatic.com/steam/apps/{0}/library_600x900.jpg",
        "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900.jpg",
        "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900.jpg",
        "https://steamcdn-a.akamaihd.net/steam/apps/{0}/library_600x900.jpg",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900_2x.jpg",
        "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900_2x.jpg",
        "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900_2x.jpg",
        "https://steamcdn-a.akamaihd.net/steam/apps/{0}/library_600x900_2x.jpg",
    ];

    /// <summary>
    /// Newer Steam apps publish portrait art as hashed library_capsule under
    /// store_item_assets — classic …/library_600x900.jpg often 404s even when
    /// the official 600×900 poster exists.
    /// </summary>
    private static readonly string[] SteamLibraryCapsuleCdnPrefixes =
    [
        "https://shared.akamai.steamstatic.com/store_item_assets/",
        "https://shared.fastly.steamstatic.com/store_item_assets/",
        "https://shared.cloudflare.steamstatic.com/store_item_assets/",
    ];

    /// <summary>
    /// Hard titles search often misses (Epic exclusives still on Steam CDN, short names, etc.).
    /// Key = NormalizeTitleKey. Value = Steam app id used only for cover art.
    /// </summary>
    private static readonly Dictionary<string, string> SeedTitleSteamIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Epic store / short-name titles where Steam search often returns empty,
        // but library_600x900 art still lives on steamstatic CDN.
        ["rocket league"] = "252950",
        ["meccha chameleon"] = "4704690",
        ["mecha chameleon"] = "4704690",
        ["beast of reincarnation"] = "2001760",
        ["wuthering waves"] = "3513350",
        ["wuwa"] = "3513350",
        ["nba 2k26"] = "3472040",
        ["nba2k26"] = "3472040",
        ["grand theft auto v"] = "271590",
        ["gta v"] = "271590",
        ["gta 5"] = "271590",
        ["gtav"] = "271590",
        ["red dead redemption 2"] = "1174180",
        ["rdr2"] = "1174180",
        ["cyberpunk 2077"] = "1091500",
        ["elden ring"] = "1245620",
        ["hades"] = "1145360",
        ["hades ii"] = "1145350",
        ["hades 2"] = "1145350",
        ["among us"] = "945360",
        ["fall guys"] = "1904540",
        ["the witcher 3 wild hunt"] = "292030",
        ["witcher 3"] = "292030",
        ["assassins creed valhalla"] = "2208920",
        ["assassins creed odyssey"] = "812140",
        ["control"] = "870780",
        ["death stranding"] = "1850570",
        ["death stranding directors cut"] = "1850570",
        ["horizon zero dawn"] = "1151640",
        ["horizon forbidden west complete edition"] = "2420110",
        ["horizon forbidden west"] = "2420110",
        ["god of war"] = "1593500",
        ["god of war ragnarok"] = "2322010",
        ["marvels spider man remastered"] = "1817070",
        ["spider man remastered"] = "1817070",
        ["ghost of tsushima directors cut"] = "2215430",
        ["ghost of tsushima"] = "2215430",
        ["star wars jedi fallen order"] = "1172380",
        ["star wars jedi survivor"] = "1774580",
        ["it takes two"] = "1426210",
        ["a plague tale innocence"] = "752590",
        ["a plague tale requiem"] = "1182900",
        ["metro exodus"] = "412020",
        ["borderlands 3"] = "397540",
        ["outer wilds"] = "753640",
        ["subnautica"] = "264710",
        ["deep rock galactic"] = "548430",
        ["no mans sky"] = "275850",
        ["palworld"] = "1623730",
        ["helldivers 2"] = "553850",
        ["baldurs gate 3"] = "1086940",
        ["bg3"] = "1086940",
        ["black myth wukong"] = "2358720",
        ["monster hunter wilds"] = "2246340",
        ["destiny 2"] = "1085660",
        ["warframe"] = "230410",
        ["path of exile"] = "238960",
        ["path of exile 2"] = "2694490",
        ["diablo iv"] = "2344520",
        ["overwatch 2"] = "2357570",
        ["apex legends"] = "1172470",
        ["pubg battlegrounds"] = "578080",
        ["playerunknowns battlegrounds"] = "578080",
        ["dota 2"] = "570",
        ["counter strike 2"] = "730",
        ["cs2"] = "730",
        ["team fortress 2"] = "440",
        ["terraria"] = "105600",
        ["stardew valley"] = "413150",
        ["sea of thieves"] = "1172620",
        ["forza horizon 5"] = "1551360",
        ["forza horizon 4"] = "1293830",
        ["halo infinite"] = "1240440",
        ["gears 5"] = "1097840",
        ["microsoft flight simulator"] = "1250410",
        ["flight simulator"] = "1250410",
        ["the finals"] = "2073850",
        ["delta force"] = "2507950",
        ["marvel rivals"] = "2767030",
        ["schedule i"] = "3164500",
        ["peak"] = "3527290",
        ["repo"] = "3241660",
        ["inzoi"] = "2456740",
        ["split fiction"] = "2001120",
        ["clair obscur expedition 33"] = "1903340",
        ["expedition 33"] = "1903340",
        ["kingdom come deliverance 2"] = "1771300",
        ["stalker 2 heart of chornobyl"] = "1643320",
        ["indiana jones and the great circle"] = "2677660",
        ["south of midnight"] = "1934570",
        ["doom the dark ages"] = "3017860",
        ["assasins creed shadows"] = "3159330",
        ["assassins creed shadows"] = "3159330",
        ["silent hill 2"] = "2124490",
        ["silent hill 2 remake"] = "2124490",
        ["metaphor refantazio"] = "2679460",
        ["ff7 rebirth"] = "2909400",
        ["final fantasy vii rebirth"] = "2909400",
        ["dragon age the veilguard"] = "1845910",
        ["space marine 2"] = "2183900",
        ["warhammer 40000 space marine 2"] = "2183900",
    };

    private static readonly ConcurrentDictionary<string, byte> WarmInFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ArtworkOperationGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (long Len, long Mtime, int W, int H)> ImageSizeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> TitleSteamMap = new(StringComparer.OrdinalIgnoreCase);
    private const string GameTitleBindingPrefix = "@title:";
    /// <summary>Steam app ids whose CDN posters 404'd — do not keep dead URLs on tiles.</summary>
    private static readonly ConcurrentDictionary<string, byte> DeadSteamCdn = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Hashed library_capsule CDN URLs from GetItems — first-paint that actually 200s.</summary>
    private static readonly ConcurrentDictionary<string, string> SteamCapsuleCdn = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Steam community icon hashes from GetItems, used only when no poster exists.</summary>
    private static readonly ConcurrentDictionary<string, string> SteamCommunityIcon = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Titles already reported as having no portrait; keeps the log to one line each.</summary>
    private static readonly ConcurrentDictionary<string, byte> NoArtLogged = new(StringComparer.OrdinalIgnoreCase);
    private static string TitleMapPath => Path.Combine(CacheRoot, "title-steam-map.json");

    public static IReadOnlyList<GameEntry> WithCovers(IEnumerable<GameEntry> games) =>
        games.Select(WithCover).ToList();

    public static GameEntry WithCover(GameEntry g)
    {
        var preferred = ResolvePreferredUrl(g);

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            if (string.Equals(preferred, g.CoverUrl, StringComparison.Ordinal))
                return g;
            return CloneWithCover(g, preferred, CoverSourceFor(g, preferred));
        }

        // Official portrait CDN while disk cache warms — Steam library_600x900,
        // or Epic/Riot tall art from the local EGL catalog / seeded URLs.
        var provisional = ProvisionalStorePosterUrl(g);
        if (provisional is not null)
            return CloneWithCover(g, provisional, CoverSourceFor(g, provisional));

        if (!string.IsNullOrWhiteSpace(g.CoverUrl) &&
            g.CoverUrl.StartsWith(VirtualHostOrigin + "/", StringComparison.OrdinalIgnoreCase) &&
            TryResolveLocalFile(g.CoverUrl) is null)
            return CloneWithCover(g, coverUrl: null, coverSource: null);

        if (!string.IsNullOrWhiteSpace(g.CoverUrl) && !IsUiLoadableCoverUrl(g.CoverUrl))
            return CloneWithCover(g, coverUrl: null, coverSource: null);

        return g;
    }

    /// <summary>
    /// Immediate official portrait URL for search / first paint. Disk cache +
    /// virtual host still win via <see cref="ResolvePreferredUrl"/> when present.
    /// </summary>
    public static string? ProvisionalStorePosterUrl(GameEntry g)
    {
        var steam = ProvisionalSteamPosterUrl(g);
        if (steam is not null) return steam;
        var epic = ProvisionalEpicPosterUrl(g);
        if (epic is not null) return epic;
        if (g.Store == StoreKind.Gog && IsAllowlistedCdnCover(g.CoverUrl))
            return g.CoverUrl;
        return null;
    }

    /// <summary>
    /// Immediate official Steam portrait URL (library_600x900) for search / first paint.
    /// Disk cache + virtual host still win via <see cref="ResolvePreferredUrl"/> when present.
    /// </summary>
    public static string? ProvisionalSteamPosterUrl(GameEntry g)
    {
        var appId = SteamAppId(g) ?? MappedSteamAppId(g);
        if (appId is null) return null;
        if (SteamCapsuleCdn.TryGetValue(appId, out var hashed) &&
            !string.IsNullOrWhiteSpace(hashed) &&
            IsOfficialSteamPortraitCdn(hashed))
            return hashed;
        if (DeadSteamCdn.ContainsKey(appId)) return null;
        return SteamPortraitCdnUrl(appId);
    }

    public static string SteamPortraitCdnUrl(string appId) =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg";

    /// <summary>
    /// Epic Games Store tall art for Epic/Riot titles. EGL catcache first so a
    /// friend's Fortnite cover is the local catalog, not a network scrape.
    /// </summary>
    public static string? ProvisionalEpicPosterUrl(GameEntry g)
    {
        if (g.Store is not StoreKind.Epic and not StoreKind.Riot) return null;
        var local = EpicCatCacheArt.FindPortraitUrl(g.Title, g.LaunchTarget, EpicArtifactSuffix(g));
        if (IsOfficialEpicPortraitCdn(local)) return local;
        var seed = EpicCatalogArt.TrySeedPortraitUrl(g.Title);
        if (IsOfficialEpicPortraitCdn(seed)) return seed;
        return IsOfficialEpicPortraitCdn(g.CoverUrl) ? g.CoverUrl : null;
    }

    /// <summary>True for official Steam portrait CDN paths (not heroes / wide capsules).</summary>
    public static bool IsOfficialSteamPortraitCdn(string? url)
    {
        if (!TryParseHttpsDefaultPort(url, out var uri) || !IsSteamImageHost(uri)) return false;
        var path = uri.AbsolutePath;
        if (path.Contains("library_hero", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/header.jpg", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("capsule_231", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("capsule_184", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("capsule_616", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("capsule_sm", StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Contains("library_600x900", StringComparison.OrdinalIgnoreCase)
               || path.Contains("library_capsule", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowlistedCdnCover(string? url)
    {
        if (!TryParseHttpsDefaultPort(url, out _)) return false;
        if (IsOfficialSteamPortraitCdn(url) || IsOfficialEpicPortraitCdn(url)) return true;
        return HttpsHostIs(url, "ddragon.leagueoflegends.com")
               || HttpsHostIs(url, "images.gog-statics.com", "gog-statics.com")
               || HttpsHostIs(url, "riotgames.com", "playvalorant.com", "leagueoflegends.com")
               || HttpsHostIs(url, "store-images.s-microsoft.com", "images-eds-ssl.xboxlive.com")
               || HttpsHostIs(url, "ubisoft.com", "ubi.com")
               || HttpsHostIs(url, "ea.com", "origin.com")
               || HttpsHostIs(url, "blizzard.com", "battle.net");
    }

    private static bool HttpsHostIs(string? url, params string[] hosts)
    {
        if (!TryParseHttpsDefaultPort(url, out var uri)) return false;
        foreach (var host in hosts)
        {
            if (HostMatches(uri, host)) return true;
        }
        return false;
    }

    private static bool TryParseHttpsDefaultPort(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !parsed.IsDefaultPort ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            string.IsNullOrWhiteSpace(parsed.IdnHost))
            return false;
        uri = parsed;
        return true;
    }

    private static bool HostMatches(Uri uri, string host) =>
        uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);

    private static bool IsSteamImageHost(Uri uri) =>
        HostMatches(uri, "steamstatic.com") ||
        uri.IdnHost.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase);

    internal static bool IsApprovedArtworkDownloadUrl(string? url)
    {
        if (!TryParseHttpsDefaultPort(url, out var uri)) return false;
        if (IsSteamImageHost(uri)) return true;
        return HostMatches(uri, "epicgames.com") ||
               HostMatches(uri, "unrealengine.com") ||
               HostMatches(uri, "gog-statics.com") ||
               HostMatches(uri, "riotgames.com") ||
               HostMatches(uri, "playvalorant.com") ||
               HostMatches(uri, "leagueoflegends.com") ||
               HostMatches(uri, "store-images.s-microsoft.com") ||
               HostMatches(uri, "images-eds-ssl.xboxlive.com") ||
               HostMatches(uri, "ubisoft.com") ||
               HostMatches(uri, "ubi.com") ||
               HostMatches(uri, "ea.com") ||
               HostMatches(uri, "origin.com") ||
               HostMatches(uri, "blizzard.com") ||
               HostMatches(uri, "battle.net");
    }

    /// <summary>
    /// Best available art for a game: official native cache → validated local folder art.
    /// </summary>
    public static string? ResolvePreferredUrl(GameEntry g)
    {
        // Gather every portrait file this game could use, then pick the sharpest.
        // Landscape art has a separate hero_* cache and ResolveWideArtUrl path.
        var candidates = new List<string>();

        // Steam cache (native app id, or mapped Steam id for Epic/etc. covers only).
        var appId = SteamAppId(g) ?? MappedSteamAppId(g);
        if (appId is not null)
        {
            var dest = Path.Combine(CacheRoot, appId + ".jpg");
            var dest2x = Path.Combine(CacheRoot, appId + "_2x.jpg");
            DiscardIfLandscape(dest);
            DiscardIfLandscape(dest2x);
            if (!HasPortraitArt(appId))
                TryImportSteamLibraryCachePoster(appId, dest2x, dest);
            candidates.Add(dest);
            candidates.Add(dest2x);
        }

        // Riot ships no public cover endpoint; its art arrives from the Epic
        // catalog warm and is cached under the product id.
        if (g.Store == StoreKind.Riot)
        {
            var productId = RiotProductId(g);
            if (!string.IsNullOrWhiteSpace(productId))
            {
                foreach (var safe in CacheIdentityCandidates(productId))
                {
                    foreach (var ext in new[] { ".jpg", ".png", ".jpeg", ".webp" })
                        candidates.Add(Path.Combine(CacheRoot, "riot_" + safe + ext));
                    candidates.Add(Path.Combine(CacheRoot, "riot_" + safe + "_card.png"));
                }
            }
        }

        // Per-id cache slug — also where portrait art fetched by title lands,
        // so a Steam title with only hero art can still gain a real poster.
        foreach (var slug in CacheIdentityCandidates(g.Id))
        {
            foreach (var ext in new[] { ".jpg", ".png", ".jpeg", ".webp" })
                candidates.Add(Path.Combine(CacheRoot, slug + ext));
        }

        // GOG product art from the official GOG cache.
        if (g.Store == StoreKind.Gog)
        {
            var gogId = GogProductId(g);
            if (gogId is not null)
                candidates.Add(Path.Combine(CacheRoot, "gog_" + gogId + ".jpg"));
        }

        candidates.Add(Path.Combine(CacheRoot, GameIconArt.CacheFileName(g.Id)));
        var steamId = SteamAppId(g) ?? MappedSteamAppId(g);
        if (steamId is not null)
            candidates.Add(Path.Combine(CacheRoot, GameIconArt.CacheFileName("steam:" + steamId)));

        var best = PickBestArt(candidates);
        if (best is not null)
        {
            var url = PreferLocalArt(best, Path.GetFileName(best));
            if (url is not null) return url;
        }

        // 4) Local install folder art
        if (!string.IsNullOrWhiteSpace(g.Path) && Directory.Exists(g.Path))
        {
            foreach (var name in new[]
                     {
                         "cover.jpg", "cover.png", "library.jpg", "poster.png",
                         "icon.png", "cover.jpeg", "game.ico", "icon.ico",
                     })
            {
                var hit = Path.Combine(g.Path, name);
                if (!IsValidImageFile(hit) && !hit.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (hit.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && !File.Exists(hit))
                    continue;
                try
                {
                    if (!File.Exists(hit)) continue;
                    var isIco = hit.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);
                    if (!isIco && !IsPortraitCover(hit)) continue;

                    Directory.CreateDirectory(CacheRoot);
                    var safe = isIco
                        ? GameIconArt.CacheFileName(g.Id)
                        : "local_" + Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes(hit.ToLowerInvariant())))[..16]
                          + Path.GetExtension(hit);
                    var dest = Path.Combine(CacheRoot, safe);
                    if (!File.Exists(dest) || !IsPortraitCover(dest))
                    {
                        if (isIco)
                        {
                            if (!GameIconArt.TryWritePlateFromImage(hit, dest))
                                continue;
                        }
                        else
                            File.Copy(hit, dest, overwrite: true);
                    }
                    if (!IsPortraitCover(dest))
                    {
                        DiscardIfLandscape(dest);
                        continue;
                    }
                    var url = PreferLocalArt(dest, safe);
                    if (url is not null) return url;
                }
                catch { /* try next */ }
            }
        }

        return null;
    }

    private static string? MappedSteamAppId(GameEntry g)
    {
        if (g.Store is StoreKind.Minecraft or StoreKind.Roblox) return null;
        EnsureTitleMapLoaded();
        var titleKeys = TitleLookupKeys(g.Title);
        foreach (var key in titleKeys)
        {
            if (!SeedTitleSteamIds.TryGetValue(key, out var seed) || !IsUsableAppId(seed))
                continue;

            // Seeds are authoritative. Refresh the per-game entry as well so a
            // corrected seed replaces a stale mapping loaded from disk.
            if (TitleSteamMap.TryGetValue(g.Id, out var stale) &&
                IsUsableAppId(stale) &&
                !string.Equals(stale, seed, StringComparison.Ordinal))
            {
                InvalidateSlugPortrait(g.Id);
            }
            BindGameTitleMap(g, seed);
            TitleSteamMap[key] = seed;
            return seed;
        }

        if (TryGetTitleBoundGameMap(g, titleKeys, out var byId))
            return byId;
        foreach (var key in titleKeys)
        {
            if (TitleSteamMap.TryGetValue(key, out var byTitle) && IsUsableAppId(byTitle))
            {
                BindGameTitleMap(g, byTitle);
                return byTitle;
            }
        }
        // Prefix seed match only: "grand theft auto v epic" → "grand theft auto v"
        // Avoid short substring traps (e.g. "control" inside unrelated titles).
        string? partial = null;
        var partialLen = 0;
        foreach (var key in titleKeys)
        {
            foreach (var (seedKey, seedId) in SeedTitleSteamIds)
            {
                if (!IsUsableAppId(seedId) || seedKey.Length < 6) continue;
                if (key.StartsWith(seedKey, StringComparison.Ordinal) || seedKey.StartsWith(key, StringComparison.Ordinal))
                {
                    if (seedKey.Length > partialLen)
                    {
                        partialLen = seedKey.Length;
                        partial = seedId;
                    }
                }
            }
        }
        return partial;
    }

    /// <summary>
    /// Exact catalog identity already established by the native store id,
    /// the resolved cover URL, or Exo's persisted title-to-Steam art binding.
    /// This never performs a live title search and must not be used as proof
    /// that the user owns a Steam license.
    /// </summary>
    internal static string? MetadataSteamAppId(GameEntry game) =>
        SteamAppId(game) ?? ExtractSteamAppIdFromUrl(game.CoverUrl) ?? MappedSteamAppId(game);

    internal static string GameTitleBindingKey(string gameId) =>
        GameTitleBindingPrefix + gameId;

    internal static string NormalizedTitleBinding(string title)
    {
        var cleaned = CleanSearchTitle(title);
        var normalized = NormalizeTitleKey(cleaned);
        return string.IsNullOrWhiteSpace(normalized)
            ? NormalizeTitleKey(SplitCamelTitle(title))
            : normalized;
    }

    private static void BindGameTitleMap(GameEntry game, string appId)
    {
        TitleSteamMap[game.Id] = appId;
        var binding = NormalizedTitleBinding(game.Title);
        if (binding.Length > 0)
            TitleSteamMap[GameTitleBindingKey(game.Id)] = binding;
    }

    /// <summary>
    /// Per-game ids are only reusable for the title they were resolved from.
    /// Legacy rows migrate when their normalized-title entry confirms the same
    /// app id; otherwise they are discarded so title lookup can self-heal.
    /// </summary>
    private static bool TryGetTitleBoundGameMap(
        GameEntry game,
        IReadOnlyList<string> titleKeys,
        out string appId)
    {
        appId = string.Empty;
        if (!TitleSteamMap.TryGetValue(game.Id, out var cached) || !IsUsableAppId(cached))
            return false;

        var bindingKey = GameTitleBindingKey(game.Id);
        var expected = NormalizedTitleBinding(game.Title);
        if (expected.Length == 0)
        {
            InvalidateGameTitleMap(game.Id);
            return false;
        }

        if (TitleSteamMap.TryGetValue(bindingKey, out var boundTitle))
        {
            if (string.Equals(boundTitle, expected, StringComparison.Ordinal))
            {
                appId = cached;
                return true;
            }

            InvalidateGameTitleMap(game.Id);
            return false;
        }

        // Legacy flat maps wrote both the game id and normalized title. That
        // matching pair is enough evidence to add the new binding in place.
        if (titleKeys.Any(key =>
                TitleSteamMap.TryGetValue(key, out var byTitle) &&
                string.Equals(byTitle, cached, StringComparison.Ordinal)))
        {
            TitleSteamMap[bindingKey] = expected;
            appId = cached;
            return true;
        }

        InvalidateGameTitleMap(game.Id);
        return false;
    }

    private static void InvalidateGameTitleMap(string gameId)
    {
        TitleSteamMap.TryRemove(gameId, out _);
        TitleSteamMap.TryRemove(GameTitleBindingKey(gameId), out _);
        InvalidateSlugPortrait(gameId);
    }

    private static void InvalidateSlugPortrait(string gameId)
    {
        foreach (var slug in CacheIdentityCandidates(gameId))
        {
            foreach (var extension in new[] { ".jpg", ".png", ".jpeg", ".webp" })
                TryDelete(Path.Combine(CacheRoot, slug + extension));
        }
    }

    /// <summary>
    /// Map keys for a title: raw normalize plus camel/digit-split so
    /// <c>ForzaHorizon5</c> hits the same seed as <c>Forza Horizon 5</c>.
    /// </summary>
    private static IReadOnlyList<string> TitleLookupKeys(string title)
    {
        var key = NormalizeTitleKey(title);
        if (string.IsNullOrWhiteSpace(key)) return [];
        var split = NormalizeTitleKey(SplitCamelTitle(title));
        if (string.IsNullOrWhiteSpace(split) || split.Equals(key, StringComparison.Ordinal))
            return [key];
        return [key, split];
    }

    private static bool IsUsableAppId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id != "0" && id.All(char.IsDigit);

    private static string? GogProductId(GameEntry g)
    {
        if (g.Store != StoreKind.Gog) return null;
        if (g.Id.StartsWith("gog:", StringComparison.OrdinalIgnoreCase))
        {
            var id = g.Id["gog:".Length..];
            if (id.All(char.IsDigit)) return id;
        }
        if (!string.IsNullOrWhiteSpace(g.LaunchTarget) && g.LaunchTarget.All(char.IsDigit))
            return g.LaunchTarget;
        return null;
    }

    private static string NormalizeTitleKey(string title)
    {
        var chars = title.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        return new string(chars).Trim();
    }

    private static void EnsureTitleMapLoaded()
    {
        if (!TitleSteamMap.IsEmpty) return;
        try
        {
            if (!File.Exists(TitleMapPath)) return;
            var json = File.ReadAllText(TitleMapPath);
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value.GetString();
                if (string.IsNullOrWhiteSpace(v)) continue;
                // Negatives: "0:<unix>" active for NegativeTitleMapTtl; legacy "0" and expired entries retry.
                if (p.Name.StartsWith('!') && !IsActiveNegativeCache(v)) continue;
                TitleSteamMap[p.Name] = v;
            }
        }
        catch { /* ignore */ }

        // Seeds are authoritative cover mappings. Assignment also replaces stale
        // persisted values when a seed is corrected in a later release.
        foreach (var (k, v) in SeedTitleSteamIds)
        {
            if (IsUsableAppId(v))
                TitleSteamMap[k] = v;
        }
    }

    /// <summary>How long a failed Steam title lookup stays blocked before retry.</summary>
    internal static readonly TimeSpan NegativeTitleMapTtl = TimeSpan.FromHours(18);

    private static string NegativeSteamMapValue() =>
        "0:" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>True when a negative map value should still block Steam title retries.</summary>
    internal static bool IsActiveNegativeCache(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        // Legacy bare "0" — do not load (allow retry).
        if (value == "0") return false;
        if (!value.StartsWith("0:", StringComparison.Ordinal)) return false;
        if (!long.TryParse(value.AsSpan(2), out var unix)) return false;
        try
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix);
            return age < NegativeTitleMapTtl;
        }
        catch
        {
            return false;
        }
    }

    private static void PersistTitleMap(string? path = null)
    {
        try
        {
            var destination = path ?? TitleMapPath;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var obj = new Dictionary<string, string>(TitleSteamMap, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(destination, JsonSerializer.Serialize(obj));
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// True when the cover URL is a local cache file, a virtual-host cache name,
    /// or an official Steam/Epic/GOG/Riot portrait CDN.
    /// </summary>
    public static bool IsUiLoadableCoverUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith(VirtualHostOrigin + "/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = url[(VirtualHostOrigin.Length + 1)..];
            return rest.Length > 0
                   && !rest.Contains("..", StringComparison.Ordinal)
                   && !rest.Contains('/')
                   && !rest.Contains('\\');
        }
        // Classic /steam/apps/{id}/library_600x900.jpg is a first-paint guess that
        // 404s on newer titles. Search used to treat it as resolved art and skip
        // the hashed library_capsule warm — leave it allowlisted, not loadable.
        if (IsProvisionalSteamPosterCdn(url)) return false;
        if (IsAllowlistedCdnCover(url)) return true;
        return false;
    }

    /// <summary>
    /// Unhashed Steam library_600x900 CDN. Safe to show, not safe to treat as done.
    /// </summary>
    public static bool IsProvisionalSteamPosterCdn(string? url)
    {
        if (!IsOfficialSteamPortraitCdn(url) || url is null) return false;
        if (url.Contains("store_item_assets", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Contains("library_capsule", StringComparison.OrdinalIgnoreCase)) return false;
        return url.Contains("library_600x900", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Official Epic portrait CDN hosts used by catalog keyImages.</summary>
    public static bool IsOfficialEpicPortraitCdn(string? url)
    {
        return TryParseHttpsDefaultPort(url, out var uri) &&
               (HostMatches(uri, "epicgames.com") || HostMatches(uri, "unrealengine.com"));
    }

    private static string? EpicArtifactSuffix(GameEntry g)
    {
        if (g.Id.StartsWith("epic:", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = g.Id["epic:".Length..].Trim();
            return suffix.Length > 0 ? suffix : null;
        }
        return null;
    }

    /// <summary>
    /// Virtual-host filename for grid tiles (fast JSON). Never inlines posters into RPC.
    /// </summary>
    public static string? PreferLocalArt(string path, string virtualFileName)
    {
        // Prefer a sibling .jpg when the primary file is a huge PNG.
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            var jpgSibling = Path.ChangeExtension(path, ".jpg");
            if (IsValidImageFile(jpgSibling))
            {
                var siblingName = Path.GetFileName(Path.ChangeExtension(virtualFileName, ".jpg"));
                var siblingUrl = $"{VirtualHostOrigin}/{siblingName}";
                if (IsUiLoadableCoverUrl(siblingUrl)) return siblingUrl;
            }
        }

        if (!IsValidImageFile(path)) return null;
        var safeName = Path.GetFileName(virtualFileName);
        if (string.IsNullOrWhiteSpace(safeName)) return null;
        var url = $"{VirtualHostOrigin}/{safeName}";
        return IsUiLoadableCoverUrl(url) ? url : null;
    }

    /// <summary>
    /// Maps a virtual-host cover URL back to the on-disk cache file so native
    /// surfaces (achievement toasts) can paint the same poster the library shows.
    /// </summary>
    public static string? TryResolveLocalFile(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith(VirtualHostOrigin + "/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = url[(VirtualHostOrigin.Length + 1)..];
            if (rest.Length == 0 || rest.Contains("..", StringComparison.Ordinal) ||
                rest.Contains('/') || rest.Contains('\\'))
                return null;
            var path = Path.Combine(CacheRoot, rest);
            return File.Exists(path) ? path : null;
        }

        try
        {
            if (Path.IsPathFullyQualified(url) && File.Exists(url))
                return url;
        }
        catch { }

        return null;
    }

    /// <summary>Bitmap source for native tiles: disk cache, else an official HTTPS portrait.</summary>
    public static Uri? TryImageUri(GameEntry game)
    {
        var file = TryResolveLocalFile(game.CoverUrl);
        if (file is not null) return new Uri(file);
        return TryImageUri(game.CoverUrl);
    }

    public static Uri? TryImageUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var file = TryResolveLocalFile(url);
        if (file is not null) return new Uri(file);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(uri.IdnHost, VirtualHost, StringComparison.OrdinalIgnoreCase))
            return null;
        return IsUiLoadableCoverUrl(url) ? uri : null;
    }

    public static IReadOnlyList<string> SteamHeroUrls(GameEntry game)
    {
        var appId = SteamAppId(game) ?? ExtractSteamAppIdFromUrl(game.CoverUrl);
        if (string.IsNullOrWhiteSpace(appId)) return Array.Empty<string>();
        return SteamHeroCdnUrls(appId);
    }

    private static IReadOnlyList<string> SteamHeroCdnUrls(string appId) =>
    [
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_hero_2x.jpg",
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_hero.jpg",
    ];

    /// <summary>
    /// Cache file for landscape store art. The UI builds the same virtual-host
    /// name from the game id, so banners need no extra bridge field.
    /// </summary>
    // The React shell historically computes this legacy name synchronously.
    // Keep it readable for existing installs while every native-only cache key
    // below uses CollisionSafeCacheId. A future DTO migration can move heroes
    // without breaking offline caches or old packaged shells.
    public static string WideArtFileName(string gameId) => "hero_" + LegacySanitizeId(gameId) + ".jpg";

    /// <summary>Banners crop, never letterbox, so wide art has to be clearly landscape.</summary>
    public const double MinWideAspect = 1.2;

    /// <summary>True when the file is real landscape art (not a poster, not a sliver).</summary>
    public static bool IsWideArt(string path)
    {
        var size = ReadImageSize(path);
        if (size is null) return false;
        var (w, h) = size.Value;
        if (w < 400 || h < 140) return false;
        return w / (double)h >= MinWideAspect;
    }

    /// <summary>Virtual-host URL for this title's cached landscape art, else null.</summary>
    public static string? ResolveWideArtUrl(GameEntry g)
    {
        var name = WideArtFileName(g.Id);
        var path = Path.Combine(CacheRoot, name);
        if (!IsValidImageFile(path) || !IsWideArt(path)) return null;
        return PreferLocalArt(path, name);
    }

    /// <summary>
    /// Banners only ever show what the user is actually on: installed or pinned
    /// titles. Everything else falls back to the washed portrait in the UI.
    /// </summary>
    public static bool ShouldWarmWideArt(GameEntry game)
    {
        if (string.Equals(game.Id, "local:add", StringComparison.OrdinalIgnoreCase)) return false;
        if (LooksLikeEngineAsset(game.Title)) return false;
        return game.Installed || game.IsFavorite;
    }

    /// <summary>
    /// Tiles are 2:3. Anything wider than this is hero art, and either cropping
    /// or letterboxing it looks stretched, so it is not used as a cover at all.
    /// </summary>
    public const double MaxCoverAspect = 0.90;

    /// <summary>Read pixel dimensions from the file header (no imaging dependency).</summary>
    public static (int Width, int Height)? ReadImageSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var mtime = info.LastWriteTimeUtc.Ticks;
            var len = info.Length;
            if (ImageSizeCache.TryGetValue(path, out var cached) &&
                cached.Len == len && cached.Mtime == mtime)
                return (cached.W, cached.H);

            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            var sig = br.ReadBytes(8);
            if (sig.Length < 8) return null;

            // PNG: IHDR width/height are big-endian at a fixed offset.
            if (sig[0] == 0x89 && sig[1] == 0x50 && sig[2] == 0x4E && sig[3] == 0x47)
            {
                fs.Position = 16;
                var w = ReadBigEndianInt32(br);
                var h = ReadBigEndianInt32(br);
                if (w > 0 && h > 0)
                {
                    ImageSizeCache[path] = (len, mtime, w, h);
                    return (w, h);
                }
                return null;
            }

            // GIF: logical screen width/height are little-endian after the signature.
            if (sig[0] == (byte)'G' && sig[1] == (byte)'I' && sig[2] == (byte)'F' &&
                sig[3] == (byte)'8' && (sig[4] == (byte)'7' || sig[4] == (byte)'9') && sig[5] == (byte)'a')
            {
                var w = sig[6] | (sig[7] << 8);
                var h = br.ReadUInt16();
                if (w > 0 && h > 0)
                {
                    ImageSizeCache[path] = (len, mtime, w, h);
                    return (w, h);
                }
                return null;
            }

            // WebP: RIFF....WEBP + VP8X / VP8  chunk. GOG vertical covers are often webp.
            if (sig[0] == (byte)'R' && sig[1] == (byte)'I' && sig[2] == (byte)'F' && sig[3] == (byte)'F')
            {
                fs.Position = 8;
                var four = br.ReadBytes(4);
                if (four.Length == 4 && four[0] == (byte)'W' && four[1] == (byte)'E'
                    && four[2] == (byte)'B' && four[3] == (byte)'P')
                {
                    var chunk = br.ReadBytes(4);
                    _ = br.ReadInt32();
                    if (chunk.Length == 4)
                    {
                        int w = 0, h = 0;
                        var tag = System.Text.Encoding.ASCII.GetString(chunk);
                        if (tag == "VP8X")
                        {
                            _ = br.ReadBytes(4);
                            var wMinus = br.ReadByte() | (br.ReadByte() << 8) | (br.ReadByte() << 16);
                            var hMinus = br.ReadByte() | (br.ReadByte() << 8) | (br.ReadByte() << 16);
                            w = wMinus + 1;
                            h = hMinus + 1;
                        }
                        else if (tag == "VP8 ")
                        {
                            _ = br.ReadBytes(3);
                            if (br.ReadByte() == 0x9D && br.ReadByte() == 0x01 && br.ReadByte() == 0x2A)
                            {
                                w = br.ReadUInt16() & 0x3FFF;
                                h = br.ReadUInt16() & 0x3FFF;
                            }
                        }
                        if (w > 0 && h > 0)
                        {
                            ImageSizeCache[path] = (len, mtime, w, h);
                            return (w, h);
                        }
                    }
                }
            }

            // JPEG: walk SOF markers. Prefer the largest frame — many Steam/Epic
            // posters embed an 88×132 (or similar) EXIF thumbnail before the real
            // 600×900 SOF; returning the thumb made real portraits look like
            // landscape (or tiny) and get discarded.
            if (sig[0] == 0xFF && sig[1] == 0xD8)
            {
                fs.Position = 2;
                int bestW = 0, bestH = 0;
                while (fs.Position < fs.Length - 8)
                {
                    if (br.ReadByte() != 0xFF) continue;
                    var marker = br.ReadByte();
                    while (marker == 0xFF) marker = br.ReadByte();
                    if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                    // Start-of-scan is followed by entropy-coded bytes rather
                    // than ordinary length-prefixed segments. The frame size
                    // has already appeared by then; walking compressed bytes as
                    // markers can seek past EOF and discard a valid result.
                    if (marker is 0xDA or 0xD9) break;
                    var length = (br.ReadByte() << 8) | br.ReadByte();
                    if (length < 2) break;
                    var isSof = marker is >= 0xC0 and <= 0xCF
                                && marker is not 0xC4 and not 0xC8 and not 0xCC;
                    if (isSof)
                    {
                        br.ReadByte(); // precision
                        var h = (br.ReadByte() << 8) | br.ReadByte();
                        var w = (br.ReadByte() << 8) | br.ReadByte();
                        if (w > 0 && h > 0 && (long)w * h > (long)bestW * bestH)
                        {
                            bestW = w;
                            bestH = h;
                        }
                        var remain = length - 2 - 5; // payload left after precision+h+w
                        if (remain > 0) fs.Position += remain;
                        continue;
                    }
                    fs.Position += length - 2;
                }
                if (bestW > 0 && bestH > 0)
                {
                    ImageSizeCache[path] = (len, mtime, bestW, bestH);
                    return (bestW, bestH);
                }
            }
        }
        catch
        {
            // Unreadable header — treat as unknown rather than failing the cover.
        }
        return null;
    }

    private static int ReadBigEndianInt32(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        return b.Length < 4 ? 0 : (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }

    /// <summary>
    /// Tiles render around 300px wide on a scaled display, so anything narrower
    /// than this is being upscaled and looks soft.
    /// </summary>
    public const int MinCoverWidth = 500;

    public static bool IsSharpEnough(string path)
    {
        var size = ReadImageSize(path);
        return size is null || size.Value.Width >= MinCoverWidth;
    }

    /// <summary>
    /// Best cover for a tile: the highest-ranked portrait poster. Landscape
    /// hero/header art is reserved for <see cref="ResolveWideArtUrl"/>.
    /// </summary>
    public static string? PickBestArt(IEnumerable<string> paths)
    {
        return paths
            .Where(IsValidImageFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsPortraitCover)
            .OrderByDescending(CoverRank)
            .FirstOrDefault();
    }

    /// <summary>Higher is better among portrait files (sharper / taller).</summary>
    public static double CoverRank(string path)
    {
        var size = ReadImageSize(path);
        if (size is null) return 0;
        var (w, h) = size.Value;
        if (w <= 0 || h <= 0) return 0;
        var aspect = w / (double)h;
        if (aspect > MaxCoverAspect) return 0;
        double shape = aspect <= 0.75 ? 3000 : 2500;
        var sharp = w >= MinCoverWidth ? 80 : 0;
        var tile = path.EndsWith("_2x.jpg", StringComparison.OrdinalIgnoreCase) ? 0 : 80;
        return shape + Math.Min(h, 900) + sharp + tile;
    }

    /// <summary>True when the file is a 2:3 (or taller) poster. Unknown size = no.</summary>
    public static bool IsPortraitCover(string path)
    {
        var size = ReadImageSize(path);
        if (size is null) return false;
        return size.Value.Width / (double)size.Value.Height <= MaxCoverAspect;
    }

    /// <summary>
    /// Delete a confirmed landscape file from a portrait-cache slot.
    /// Unknown-size files are left untouched.
    /// </summary>
    public static void DiscardIfLandscape(string path)
    {
        try
        {
            if (!IsValidImageFile(path)) return;
            var size = ReadImageSize(path);
            if (size is null || size.Value.Height <= 0) return;
            if (size.Value.Width / (double)size.Value.Height > MaxCoverAspect)
                File.Delete(path);
        }
        catch
        {
            // Cache cleanup is best effort; selection still rejects the file.
        }
    }

    public static bool IsValidImageFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length < MinCoverBytes || info.Length > 8 * 1024 * 1024) return false;
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            var n = fs.Read(header);
            if (n < 3) return false;
            return IsCoverImageBytes(header[..n]);
        }
        catch
        {
            return false;
        }
    }

    internal const int MaximumDecodedImageSide = 8_192;
    internal const long MaximumDecodedPixelsPerFrame = 40_000_000;
    internal const long MaximumDecodedPixelsTotal = 80_000_000;
    internal const uint MaximumDecodedFrames = 300;

    /// <summary>
    /// Validates the complete encoded payload with Windows Imaging Component.
    /// Header dimensions are checked first so a tiny decompression-bomb header
    /// never reaches a codec. CopyPixels then forces every accepted frame to be
    /// decoded before a file can be promoted into Exo-owned storage.
    /// </summary>
    internal static bool TryFullyDecodeImage(
        string path,
        int maximumSide,
        out (int Width, int Height) dimensions)
    {
        dimensions = default;
        if (maximumSide <= 0 || maximumSide > MaximumDecodedImageSide) return false;
        var headerSize = ReadImageSize(path);
        if (headerSize is null || !DimensionsAreSafe(headerSize.Value.Width, headerSize.Value.Height, maximumSide))
            return false;
        if (!HasCompleteImageContainer(path)) return false;

        object? factoryObject = null;
        IWicBitmapDecoder? decoder = null;
        try
        {
            var factoryType = Type.GetTypeFromCLSID(WicImagingFactoryClsid, throwOnError: true);
            factoryObject = Activator.CreateInstance(factoryType!);
            if (factoryObject is not IWicImagingFactory factory) return false;
            var decoderResult = factory.CreateDecoderFromFilename(
                    path,
                    IntPtr.Zero,
                    GenericRead,
                    WicDecodeMetadataCacheOnDemand,
                    out decoder);
            if (decoderResult < 0 || decoder is null) return false;
            var frameCountResult = decoder.GetFrameCount(out var frameCount);
            if (frameCountResult < 0 ||
                frameCount is 0 or > MaximumDecodedFrames)
                return false;

            long totalPixels = 0;
            var widest = 0;
            var tallest = 0;
            for (uint index = 0; index < frameCount; index++)
            {
                IWicBitmapSource? frame = null;
                IWicFormatConverter? converter = null;
                try
                {
                    var frameResult = decoder.GetFrame(index, out frame);
                    if (frameResult < 0 || frame is null) return false;
                    var sizeResult = frame.GetSize(out var width, out var height);
                    if (sizeResult < 0 ||
                        width > int.MaxValue || height > int.MaxValue ||
                        !DimensionsAreSafe((int)width, (int)height, maximumSide))
                        return false;

                    var pixels = checked((long)width * height);
                    totalPixels = checked(totalPixels + pixels);
                    if (totalPixels > MaximumDecodedPixelsTotal) return false;
                    widest = Math.Max(widest, (int)width);
                    tallest = Math.Max(tallest, (int)height);

                    var converterResult = factory.CreateFormatConverter(out converter);
                    if (converterResult < 0 || converter is null) return false;
                    var pixelFormat = WicPixelFormat32BppBgra;
                    var initializeResult = converter.Initialize(
                            frame,
                            ref pixelFormat,
                            WicBitmapDitherTypeNone,
                            IntPtr.Zero,
                            0,
                            WicBitmapPaletteTypeCustom);
                    if (initializeResult < 0) return false;

                    var stride = checked(width * 4);
                    var rowsPerCopy = Math.Max(1u, Math.Min(height, (4u * 1024 * 1024) / stride));
                    var bufferSize = checked(stride * rowsPerCopy);
                    var buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
                    try
                    {
                        for (uint y = 0; y < height; y += rowsPerCopy)
                        {
                            var rowCount = Math.Min(rowsPerCopy, height - y);
                            var rect = new WicRect(0, checked((int)y), checked((int)width), checked((int)rowCount));
                            var copyResult = converter.CopyPixels(
                                    ref rect,
                                    stride,
                                    checked(stride * rowCount),
                                    buffer);
                            if (copyResult < 0) return false;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    ReleaseCom(converter);
                    ReleaseCom(frame);
                }
            }

            if (widest <= 0 || tallest <= 0) return false;
            dimensions = (widest, tallest);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseCom(decoder);
            ReleaseCom(factoryObject);
        }
    }

    private static bool DimensionsAreSafe(int width, int height, int maximumSide) =>
        width > 0 && height > 0 && width <= maximumSide && height <= maximumSide &&
        (long)width * height <= MaximumDecodedPixelsPerFrame;

    private static bool HasCompleteImageContainer(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length < 12) return false;
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);

            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            {
                Span<byte> tail = stackalloc byte[12];
                stream.Position = stream.Length - tail.Length;
                stream.ReadExactly(tail);
                return tail[0] == 0 && tail[1] == 0 && tail[2] == 0 && tail[3] == 0 &&
                       tail[4] == (byte)'I' && tail[5] == (byte)'E' &&
                       tail[6] == (byte)'N' && tail[7] == (byte)'D';
            }

            if (header[0] == 0xFF && header[1] == 0xD8)
            {
                var tailLength = checked((int)Math.Min(64, stream.Length));
                var tail = new byte[tailLength];
                stream.Position = stream.Length - tailLength;
                stream.ReadExactly(tail);
                for (var index = tail.Length - 2; index >= 0; index--)
                {
                    if (tail[index] == 0xFF && tail[index + 1] == 0xD9) return true;
                }
                return false;
            }

            if (header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F')
            {
                stream.Position = stream.Length - 1;
                return stream.ReadByte() == 0x3B;
            }

            if (header[0] == (byte)'R' && header[1] == (byte)'I' &&
                header[2] == (byte)'F' && header[3] == (byte)'F' &&
                header[8] == (byte)'W' && header[9] == (byte)'E' &&
                header[10] == (byte)'B' && header[11] == (byte)'P')
            {
                var declared = BitConverter.ToUInt32(header[4..8]);
                return (ulong)declared + 8 == (ulong)stream.Length;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { _ = Marshal.FinalReleaseComObject(value); }
        catch { }
    }

    private const uint GenericRead = 0x80000000;
    private const int WicDecodeMetadataCacheOnDemand = 0;
    private const int WicBitmapDitherTypeNone = 0;
    private const int WicBitmapPaletteTypeCustom = 0;
    private static readonly Guid WicImagingFactoryClsid = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid WicPixelFormat32BppBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WicRect(int x, int y, int width, int height)
    {
        public readonly int X = x;
        public readonly int Y = y;
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [ComImport]
    [Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicImagingFactory
    {
        [PreserveSig]
        int CreateDecoderFromFilename(
            [MarshalAs(UnmanagedType.LPWStr)] string fileName,
            IntPtr vendor,
            uint desiredAccess,
            int metadataOptions,
            [MarshalAs(UnmanagedType.Interface)] out IWicBitmapDecoder decoder);

        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder();
        void CreatePalette();

        [PreserveSig]
        int CreateFormatConverter([MarshalAs(UnmanagedType.Interface)] out IWicFormatConverter converter);
    }

    [ComImport]
    [Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicBitmapDecoder
    {
        void QueryCapability();
        void Initialize();
        void GetContainerFormat();
        void GetDecoderInfo();
        void CopyPalette();
        void GetMetadataQueryReader();
        void GetPreview();
        void GetColorContexts();
        void GetThumbnail();

        [PreserveSig]
        int GetFrameCount(out uint count);

        [PreserveSig]
        int GetFrame(uint index, [MarshalAs(UnmanagedType.Interface)] out IWicBitmapSource frame);
    }

    [ComImport]
    [Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicBitmapSource
    {
        [PreserveSig]
        int GetSize(out uint width, out uint height);

        void GetPixelFormat();
        void GetResolution();
        void CopyPalette();

        [PreserveSig]
        int CopyPixels(ref WicRect rect, uint stride, uint bufferSize, IntPtr buffer);
    }

    [ComImport]
    [Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicFormatConverter
    {
        [PreserveSig]
        int GetSize(out uint width, out uint height);

        void GetPixelFormat();
        void GetResolution();
        void CopyPalette();

        [PreserveSig]
        int CopyPixels(ref WicRect rect, uint stride, uint bufferSize, IntPtr buffer);

        [PreserveSig]
        int Initialize(
            [MarshalAs(UnmanagedType.Interface)] IWicBitmapSource source,
            ref Guid destinationFormat,
            int dither,
            IntPtr palette,
            double alphaThresholdPercent,
            int paletteTranslate);

        void CanConvert();
    }

    /// <summary>True when URL is unknown CDN/http (strip from UI).</summary>
    public static bool IsUnreliableCoverUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        if (TryParseHttpsDefaultPort(url, out var uri) &&
            uri.IdnHost.Equals(VirtualHost, StringComparison.OrdinalIgnoreCase))
            return false;
        // Allowlisted art CDNs are fine (CSP + isSafeCoverUrl).
        if (IsAllowlistedCdnCover(url)) return false;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Optional small data-URL helper for tests / single-tile use.</summary>
    public static string? TryDataUrl(string path)
    {
        try
        {
            if (!IsValidImageFile(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > MaxDataUrlBytes) return null;
            var bytes = File.ReadAllBytes(path);
            var mime = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                : path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
                : "image/jpeg";
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    public static string? SteamAppId(GameEntry g)
    {
        if (g.Store != StoreKind.Steam) return null;
        if (!string.IsNullOrWhiteSpace(g.LaunchTarget) && g.LaunchTarget.All(char.IsDigit))
            return g.LaunchTarget;
        if (g.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
        {
            var id = g.Id["steam:".Length..];
            if (id.All(char.IsDigit)) return id;
        }
        return null;
    }

    /// <summary>Pull app id from steamstatic / steamcdn library poster URLs.</summary>
    public static string? ExtractSteamAppIdFromUrl(string? url)
    {
        if (!TryParseHttpsDefaultPort(url, out var uri) || !IsSteamImageHost(uri)) return null;
        // …/steam/apps/123456/library_600x900…
        const string marker = "/apps/";
        var path = uri.AbsolutePath;
        var i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var start = i + marker.Length;
        var end = start;
        while (end < path.Length && char.IsDigit(path[end])) end++;
        if (end <= start) return null;
        return path[start..end];
    }

    public static string? RiotProductId(GameEntry g)
    {
        if (g.Store != StoreKind.Riot) return null;
        if (!string.IsNullOrWhiteSpace(g.LaunchTarget)) return g.LaunchTarget;
        if (g.Id.StartsWith("riot:", StringComparison.OrdinalIgnoreCase))
            return g.Id["riot:".Length..];
        return null;
    }

    /// <summary>
    /// Riot has no stable first-party public cover endpoint for the fixed catalog.
    /// Keep the monogram unless an official asset has already been cached locally.
    /// </summary>
    public static IReadOnlyList<string> RiotCoverUrls(string productId) =>
        Array.Empty<string>();

    private sealed record CacheFileSnapshot(
        string Path,
        string Name,
        long Length,
        DateTimeOffset LastWriteUtc);

    private static void StartCacheMaintenanceOnce(IReadOnlyCollection<GameEntry> activeLibrary)
    {
        if (Interlocked.CompareExchange(ref _cacheMaintenanceStarted, 1, 0) != 0) return;
        var snapshot = activeLibrary.ToArray();
        var cacheRoot = CacheRoot;
        var titleMapPath = Path.Combine(cacheRoot, "title-steam-map.json");
        _ = Task.Run(() =>
        {
            try
            {
                var result = RunCacheMaintenance(
                    cacheRoot,
                    snapshot,
                    DateTimeOffset.UtcNow,
                    DefaultCacheMaintenancePolicy);
                // WithCover may have migrated legacy per-game mappings before
                // the warm starts. Persist those bindings even when every art
                // file is already present and there are no download tasks.
                PersistTitleMap(titleMapPath);
                if (result.DeletedFiles > 0)
                {
                    AppLog.Info(
                        $"Cover cache maintenance removed {result.DeletedFiles} files " +
                        $"({result.DeletedBytes / (1024d * 1024d):0.0} MB); " +
                        $"remaining={result.RemainingFiles}/{result.RemainingBytes / (1024d * 1024d):0.0} MB.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Cover cache maintenance failed: " + ex.Message);
            }
        });
    }

    private static CacheMaintenanceResult RunPostWriteCacheMaintenance()
    {
        lock (CacheMaintenanceGate)
        {
            return RunCacheMaintenance(
                CacheRoot,
                _activeCacheMaintenanceLibrary,
                DateTimeOffset.UtcNow,
                DefaultCacheMaintenancePolicy);
        }
    }

    internal static CacheMaintenanceResult RunPostWriteCacheMaintenance(
        string cacheRoot,
        IEnumerable<GameEntry> activeLibrary,
        DateTimeOffset nowUtc,
        CacheMaintenancePolicy policy)
    {
        lock (CacheMaintenanceGate)
            return RunCacheMaintenance(cacheRoot, activeLibrary, nowUtc, policy);
    }

    internal static void NotifyOwnedArtworkWrite() => _ = RunPostWriteCacheMaintenance();

    /// <summary>
    /// Bounded cache maintenance. Active-library files and user profile images
    /// are never eviction candidates; exact legacy Steam duplicates are safe to
    /// remove only while an identical canonical poster remains.
    /// </summary>
    internal static CacheMaintenanceResult RunCacheMaintenance(
        string cacheRoot,
        IEnumerable<GameEntry> activeLibrary,
        DateTimeOffset nowUtc,
        CacheMaintenancePolicy policy)
    {
        if (!IsValidMaintenancePolicy(policy) || string.IsNullOrWhiteSpace(cacheRoot))
            return default;

        try
        {
            var root = Path.GetFullPath(cacheRoot);
            if (!Directory.Exists(root)) return default;

            var files = EnumerateCacheImages(root);
            if (files.Count == 0) return default;

            var (protectedNames, directReferences) = BuildProtectedCacheNames(
                root,
                activeLibrary ?? Array.Empty<GameEntry>());
            var alive = new HashSet<string>(
                files.Select(file => file.Path),
                StringComparer.OrdinalIgnoreCase);
            var byName = files.ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
            var remainingBytes = files.Sum(file => file.Length);
            var remainingFiles = files.Count;
            var crossedByteHighWater = remainingBytes > policy.HighWaterBytes;
            var crossedFileHighWater = remainingFiles > policy.HighWaterFiles;
            var deletedBytes = 0L;
            var deletedFiles = 0;

            bool Delete(CacheFileSnapshot file)
            {
                if (!alive.Contains(file.Path) || !TryDeleteUnchanged(file)) return false;
                alive.Remove(file.Path);
                remainingBytes -= file.Length;
                remainingFiles--;
                deletedBytes += file.Length;
                deletedFiles++;
                return true;
            }

            // Proven redundant aliases are not an eviction: the exact bytes
            // remain in the canonical Steam poster file. Never remove the URL
            // an active row is currently holding.
            foreach (var file in files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (directReferences.Contains(file.Name) ||
                    IsNeverEvictCacheName(file.Name) ||
                    nowUtc - file.LastWriteUtc < policy.MinimumEvictionAge)
                    continue;
                if (!TryGetDuplicateReplacements(file.Name, out var replacementNames)) continue;

                foreach (var replacementName in replacementNames)
                {
                    if (!byName.TryGetValue(replacementName, out var replacement) ||
                        !alive.Contains(replacement.Path) ||
                        !FilesAreIdentical(file, replacement))
                        continue;
                    _ = Delete(file);
                    break;
                }
            }

            var evictionCandidates = files
                .Where(file => alive.Contains(file.Path))
                .Where(file => !protectedNames.Contains(file.Name))
                .Where(file => !IsNeverEvictCacheName(file.Name))
                .Where(file => nowUtc - file.LastWriteUtc >= policy.MinimumEvictionAge)
                .OrderBy(file => file.LastWriteUtc)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Age expiry is independent of pressure. Active art is protected,
            // so a title can remain untouched in the cache for years while it
            // is still part of the user's library.
            foreach (var file in evictionCandidates)
            {
                if (nowUtc - file.LastWriteUtc < policy.MaxUnreferencedAge) break;
                _ = Delete(file);
            }

            var reduceBytes = crossedByteHighWater;
            var reduceFiles = crossedFileHighWater;
            if (reduceBytes || reduceFiles)
            {
                foreach (var file in evictionCandidates)
                {
                    if ((!reduceBytes || remainingBytes <= policy.LowWaterBytes) &&
                        (!reduceFiles || remainingFiles <= policy.LowWaterFiles))
                        break;
                    _ = Delete(file);
                }
            }

            return new CacheMaintenanceResult(
                files.Count,
                deletedFiles,
                deletedBytes,
                remainingFiles,
                remainingBytes);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Cover cache maintenance failed: " + ex.Message);
            return default;
        }
    }

    private static bool IsValidMaintenancePolicy(CacheMaintenancePolicy policy) =>
        policy.HighWaterBytes > 0 &&
        policy.LowWaterBytes >= 0 &&
        policy.LowWaterBytes < policy.HighWaterBytes &&
        policy.HighWaterFiles > 0 &&
        policy.LowWaterFiles >= 0 &&
        policy.LowWaterFiles < policy.HighWaterFiles &&
        policy.MaxUnreferencedAge >= TimeSpan.Zero &&
        policy.MinimumEvictionAge >= TimeSpan.Zero;

    private static List<CacheFileSnapshot> EnumerateCacheImages(string root)
    {
        var files = new List<CacheFileSnapshot>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var name = Path.GetFileName(path);
                if (!CacheImageExtensions.Contains(Path.GetExtension(name)) ||
                    IsTemporaryCacheName(name))
                    continue;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 0) continue;
                files.Add(new CacheFileSnapshot(
                    info.FullName,
                    name,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
            catch
            {
                // A cache file can disappear while the snapshot is built.
            }
        }
        return files;
    }

    private static (HashSet<string> Protected, HashSet<string> Direct) BuildProtectedCacheNames(
        string root,
        IEnumerable<GameEntry> activeLibrary)
    {
        var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in activeLibrary)
        {
            var direct = TryGetReferencedCacheFileName(root, game.CoverUrl);
            if (direct is not null)
            {
                protectedNames.Add(direct);
                directReferences.Add(direct);
            }

            AddIdentityCacheNames(
                protectedNames,
                game.Id,
                game.Store,
                game.LaunchTarget,
                ExtractSteamAppIdFromUrl(game.CoverUrl));
            foreach (var variant in game.Variants)
            {
                AddIdentityCacheNames(
                    protectedNames,
                    variant.Id,
                    variant.Store,
                    variant.LaunchTarget,
                    steamAppIdFromCover: null);
            }
        }
        return (protectedNames, directReferences);
    }

    private static void AddIdentityCacheNames(
        HashSet<string> names,
        string gameId,
        StoreKind store,
        string? launchTarget,
        string? steamAppIdFromCover)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        foreach (var slug in CacheIdentityCandidates(gameId))
        {
            foreach (var extension in CacheImageExtensions)
                names.Add(slug + extension);
        }
        names.Add(WideArtFileName(gameId));
        names.Add(GameIconArt.CacheFileName(gameId));

        string? steamAppId = steamAppIdFromCover;
        if (store == StoreKind.Steam)
        {
            if (!string.IsNullOrWhiteSpace(launchTarget) && launchTarget.All(char.IsDigit))
                steamAppId = launchTarget;
            else if (gameId.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
            {
                var fromId = gameId["steam:".Length..];
                if (fromId.All(char.IsDigit)) steamAppId = fromId;
            }
        }
        if (!string.IsNullOrWhiteSpace(steamAppId) && steamAppId.All(char.IsDigit))
        {
            names.Add(steamAppId + ".jpg");
            names.Add(steamAppId + "_2x.jpg");
        }

        if (store == StoreKind.Riot)
        {
            var productId = !string.IsNullOrWhiteSpace(launchTarget)
                ? launchTarget
                : gameId.StartsWith("riot:", StringComparison.OrdinalIgnoreCase)
                    ? gameId["riot:".Length..]
                    : null;
            if (!string.IsNullOrWhiteSpace(productId))
            {
                foreach (var safe in CacheIdentityCandidates(productId))
                {
                    var riot = "riot_" + safe;
                    foreach (var extension in CacheImageExtensions)
                        names.Add(riot + extension);
                    names.Add(riot + "_card.png");
                }
            }
        }

        if (store == StoreKind.Gog && gameId.StartsWith("gog:", StringComparison.OrdinalIgnoreCase))
        {
            var productId = gameId["gog:".Length..];
            if (productId.All(char.IsDigit)) names.Add("gog_" + productId + ".jpg");
        }
    }

    private static string? TryGetReferencedCacheFileName(string root, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            if (value.StartsWith(VirtualHostOrigin + "/", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(value, UriKind.Absolute);
                var name = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
                return IsSafeCacheFileName(name) ? name : null;
            }

            string? path = null;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uriValue) && uriValue.IsFile)
                path = uriValue.LocalPath;
            else if (Path.IsPathFullyQualified(value))
                path = value;
            if (path is null) return null;

            var fullPath = Path.GetFullPath(path);
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            var fileName = Path.GetFileName(fullPath);
            return IsSafeCacheFileName(fileName) ? fileName : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeCacheFileName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        !name.Contains("..", StringComparison.Ordinal) &&
        !name.Contains('/') &&
        !name.Contains('\\') &&
        CacheImageExtensions.Contains(Path.GetExtension(name));

    private static bool IsNeverEvictCacheName(string name) =>
        name.Equals("title-steam-map.json", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("profile-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("custom-cover-", StringComparison.OrdinalIgnoreCase) ||
        IsTemporaryCacheName(name);

    private static bool IsTemporaryCacheName(string name) =>
        name.StartsWith('~') ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".download", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetDuplicateReplacements(string name, out IReadOnlyList<string> replacements)
    {
        replacements = Array.Empty<string>();
        if (name.StartsWith("steam_", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            var appId = name["steam_".Length..^".jpg".Length];
            if (appId.Length > 0 && appId.All(char.IsDigit))
            {
                replacements = [appId + ".jpg", appId + "_2x.jpg"];
                return true;
            }
        }

        const string highResSuffix = "_2x.jpg";
        if (name.EndsWith(highResSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var appId = name[..^highResSuffix.Length];
            if (appId.Length > 0 && appId.All(char.IsDigit))
            {
                replacements = [appId + ".jpg"];
                return true;
            }
        }
        return false;
    }

    private static bool FilesAreIdentical(CacheFileSnapshot left, CacheFileSnapshot right)
    {
        if (left.Length != right.Length || left.Length < 0) return false;
        try
        {
            using var leftStream = new FileStream(
                left.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.SequentialScan);
            using var rightStream = new FileStream(
                right.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.SequentialScan);
            var leftBuffer = new byte[64 * 1024];
            var rightBuffer = new byte[64 * 1024];
            while (true)
            {
                var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
                var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
                if (leftRead != rightRead) return false;
                if (leftRead == 0) return true;
                if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeleteUnchanged(CacheFileSnapshot snapshot)
    {
        try
        {
            var current = new FileInfo(snapshot.Path);
            if (!current.Exists || current.Length != snapshot.Length ||
                current.LastWriteTimeUtc != snapshot.LastWriteUtc.UtcDateTime)
                return false;
            File.Delete(snapshot.Path);
            ImageSizeCache.TryRemove(snapshot.Path, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Task WarmCacheAsync(
        IEnumerable<GameEntry> games,
        Action? onBatchDone = null,
        bool requested = false,
        bool deferForFirstPaint = false)
        => WarmCacheCoreAsync(
            games,
            requested ? ArtworkWarmIntent.UserRefetch : ArtworkWarmIntent.Library,
            onBatchDone,
            deferForFirstPaint,
            CancellationToken.None);

    internal static Task WarmSearchPortraitCacheAsync(
        IEnumerable<GameEntry> games,
        CancellationToken cancellationToken,
        Action? onBatchDone = null)
        => WarmCacheCoreAsync(
            games,
            ArtworkWarmIntent.SearchPortrait,
            onBatchDone,
            deferForFirstPaint: false,
            cancellationToken);

    private static Task WarmCacheCoreAsync(
        IEnumerable<GameEntry> games,
        ArtworkWarmIntent intent,
        Action? onBatchDone,
        bool deferForFirstPaint,
        CancellationToken cancellationToken)
    {
        var list = games
            .Where(g => !string.Equals(g.Id, "local:add", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (list.Count == 0) return Task.CompletedTask;

        if (intent == ArtworkWarmIntent.Library)
        {
            lock (CacheMaintenanceGate)
                _activeCacheMaintenanceLibrary = list.ToArray();
            StartCacheMaintenanceOnce(list);
        }

        return Task.Run(async () =>
        {
            try
            {
                if (deferForFirstPaint && intent == ArtworkWarmIntent.Library)
                    await Task.Delay(FirstPaintCoverWarmDelay, cancellationToken).ConfigureAwait(false);

                Directory.CreateDirectory(CacheRoot);
                // A visible title with a poster but no banner remains a candidate;
                // a title with every applicable cache file does no work at all.
                var candidates = list.Where(game => NeedsWarm(game, intent)).ToList();
                if (candidates.Count == 0) return;
                var changedSinceNotify = 0;
                // Requested search results can publish incrementally, but still
                // map a small batch instead of serialising the result set per title.
                // A whole-library background warm publishes once when it settles.
                var notifyEvery = intent == ArtworkWarmIntent.Library
                    ? int.MaxValue
                    : RequestedWarmNotificationBatchSize;
                var concurrency = intent switch
                {
                    ArtworkWarmIntent.SearchPortrait => SearchWarmConcurrency,
                    ArtworkWarmIntent.UserRefetch => RequestedWarmConcurrency,
                    _ => BackgroundWarmConcurrency,
                };
                using var ownedGate = intent == ArtworkWarmIntent.SearchPortrait
                    ? null
                    : new SemaphoreSlim(concurrency);
                var gate = ownedGate ?? SearchArtworkGate;
                var tasks = new List<Task>();
                var anyChanged = 0;

                void NotifyMaybe(bool changed)
                {
                    if (!changed) return;
                    if (Interlocked.Increment(ref changedSinceNotify) < notifyEvery) return;
                    if (Interlocked.Exchange(ref changedSinceNotify, 0) == 0) return;
                    try { onBatchDone?.Invoke(); } catch { /* */ }
                }

                foreach (var g in candidates)
                {
                    // Do not cancel in-flight library warm when search starts —
                    // skip titles already warming.
                    if (!WarmInFlight.TryAdd(g.Id, 0)) continue;

                    tasks.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            var changed = false;
                            await RunSerializedArtworkOperationAsync(g.Id, async () =>
                            {
                                changed = await WarmOneAsync(CoverHttp, g, intent, cancellationToken)
                                    .ConfigureAwait(false);
                            }, cancellationToken).ConfigureAwait(false);
                            if (changed) Interlocked.Exchange(ref anyChanged, 1);
                            NotifyMaybe(changed);
                        }
                        finally
                        {
                            WarmInFlight.TryRemove(g.Id, out _);
                            gate.Release();
                        }
                    }, cancellationToken));
                }

                if (tasks.Count == 0) return;
                AppLog.Info($"Cover warm started for {tasks.Count} titles. intent={intent.ToString().ToLowerInvariant()} deferred={deferForFirstPaint.ToString().ToLowerInvariant()}");
                await Task.WhenAll(tasks).ConfigureAwait(false);
                PersistTitleMap();
                if (Volatile.Read(ref anyChanged) != 0)
                    _ = RunPostWriteCacheMaintenance();
                if (Interlocked.Exchange(ref changedSinceNotify, 0) > 0)
                    try { onBatchDone?.Invoke(); } catch { /* */ }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Cover warm failed: " + ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Serializes every writer for one exact game id. User refetch and background
    /// warm share this gate so deletion can never race a download's final move.
    /// </summary>
    internal static async Task RunSerializedArtworkOperationAsync(
        string gameId,
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrWhiteSpace(gameId)) throw new ArgumentException("Missing game id.", nameof(gameId));
        var gate = ArtworkOperationGates.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await operation().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Removes only cache names computed from the selected source identities.
    /// Names also used by another active game are protected, as are uploaded
    /// profile/custom pictures and the title map.
    /// </summary>
    internal static int EvictComputedCacheFiles(
        string cacheRoot,
        IEnumerable<GameEntry> targets,
        IEnumerable<GameEntry> activeLibrary)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot)) return 0;
        var targetRows = (targets ?? Array.Empty<GameEntry>()).ToArray();
        if (targetRows.Length == 0) return 0;
        var targetIds = targetRows.Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetNames = targetRows
            .SelectMany(ComputedCacheFileNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var protectedByOtherGames = (activeLibrary ?? Array.Empty<GameEntry>())
            .Where(row => !targetIds.Contains(row.Id))
            .SelectMany(ComputedCacheFileNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deleted = 0;
        try
        {
            var root = Path.GetFullPath(cacheRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var name in targetNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsSafeCacheFileName(name) || IsNeverEvictCacheName(name) ||
                    protectedByOtherGames.Contains(name))
                    continue;
                var path = Path.GetFullPath(Path.Combine(root, name));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                try
                {
                    File.Delete(path);
                    ImageSizeCache.TryRemove(path, out _);
                    deleted++;
                }
                catch { /* an in-use exact file remains valid */ }
            }
        }
        catch { /* cache absence/path failure is a clean no-op */ }
        return deleted;
    }

    /// <summary>
    /// Exact, serialized refetch for one visual card. It never clears a custom
    /// override; callers expose Reset separately when the custom cover is active.
    /// </summary>
    public static async Task RefetchComputedAsync(
        IEnumerable<GameEntry> targets,
        IEnumerable<GameEntry> activeLibrary,
        CancellationToken ct = default)
    {
        var rows = (targets ?? Array.Empty<GameEntry>())
            .GroupBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (rows.Length == 0) return;

        var acquired = new List<SemaphoreSlim>(rows.Length);
        try
        {
            foreach (var row in rows)
            {
                var gate = ArtworkOperationGates.GetOrAdd(row.Id, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct).ConfigureAwait(false);
                acquired.Add(gate);
            }

            _ = EvictComputedCacheFiles(CacheRoot, rows, activeLibrary);
            var anyChanged = false;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                ClearArtworkResolutionState(row);
                anyChanged |= await WarmOneAsync(
                        CoverHttp,
                        row,
                        ArtworkWarmIntent.UserRefetch,
                        ct)
                    .ConfigureAwait(false);
            }
            PersistTitleMap();
            if (anyChanged) _ = RunPostWriteCacheMaintenance();
        }
        finally
        {
            for (var i = acquired.Count - 1; i >= 0; i--) acquired[i].Release();
        }
    }

    private static IReadOnlySet<string> ComputedCacheFileNames(GameEntry game)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentityCacheNames(
            names,
            game.Id,
            game.Store,
            game.LaunchTarget,
            ExtractSteamAppIdFromUrl(game.CoverUrl));
        var steamId = SteamAppId(game) ?? MappedSteamAppId(game);
        if (steamId is not null)
        {
            names.Add(steamId + ".jpg");
            names.Add(steamId + "_2x.jpg");
            names.Add("steam_" + steamId + ".jpg");
            names.Add("steam_" + steamId + "_2x.jpg");
            names.Add(GameIconArt.CacheFileName("steam:" + steamId));
        }
        return names;
    }

    private static void ClearArtworkResolutionState(GameEntry game)
    {
        NoArtLogged.TryRemove(game.Id, out _);
        var steamId = SteamAppId(game) ?? MappedSteamAppId(game);
        if (steamId is not null) DeadSteamCdn.TryRemove(steamId, out _);
    }

    private static HttpClient CreateCoverHttpClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ExoLauncher/1.0");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/*,application/json,*/*");
        return http;
    }

    internal static async Task<bool> DownloadValidatedImageAsync(
        HttpClient http,
        string url,
        string destination,
        int minimumBytes,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (minimumBytes < 0 || maximumBytes <= 0 || minimumBytes > maximumBytes ||
            !IsApprovedArtworkDownloadUrl(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var current))
            return false;

        const int maximumRedirects = 5;
        for (var redirect = 0; redirect <= maximumRedirects; redirect++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (HostMatches(current, "epicgames.com") || HostMatches(current, "unrealengine.com"))
            {
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Referer", "https://store.epicgames.com/");
                request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");
            }

            using var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? current;
            if (!IsApprovedArtworkDownloadUrl(finalUri.AbsoluteUri)) return false;

            if (IsRedirectStatus(response.StatusCode))
            {
                if (redirect == maximumRedirects || response.Headers.Location is null) return false;
                var next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(finalUri, response.Headers.Location);
                if (!IsApprovedArtworkDownloadUrl(next.AbsoluteUri)) return false;
                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode) return false;
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is < 0 || declaredLength > maximumBytes) return false;

            var destinationDirectory = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(destinationDirectory)) return false;
            Directory.CreateDirectory(destinationDirectory);
            var extension = Path.GetExtension(destination);
            var temporary = Path.Combine(
                destinationDirectory,
                "~" + Path.GetFileNameWithoutExtension(destination) + "." + Guid.NewGuid().ToString("N") +
                ".tmp" + extension);
            try
            {
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                await using (var input = await response.Content
                                 .ReadAsStreamAsync(readDeadline.Token)
                                 .ConfigureAwait(false))
                await using (var output = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    var buffer = new byte[64 * 1024];
                    long written = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, readDeadline.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        written += read;
                        if (written > maximumBytes) return false;
                        await output.WriteAsync(buffer.AsMemory(0, read), readDeadline.Token).ConfigureAwait(false);
                    }
                    if (written < minimumBytes) return false;
                    await output.FlushAsync(readDeadline.Token).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                if (!TryFullyDecodeImage(temporary, MaximumDecodedImageSide, out _)) return false;
                ImageSizeCache.TryRemove(destination, out _);
                File.Move(temporary, destination, overwrite: true);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        return false;
    }

    private static bool IsRedirectStatus(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.MovedPermanently or
            System.Net.HttpStatusCode.Found or
            System.Net.HttpStatusCode.SeeOther or
            System.Net.HttpStatusCode.TemporaryRedirect or
            System.Net.HttpStatusCode.PermanentRedirect;

    private static async Task<bool> WarmOneAsync(
        HttpClient http,
        GameEntry g,
        ArtworkWarmIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hadPortrait = HasCachedPortrait(g);
        var hadWide = ResolveWideArtUrl(g) is not null;

        var hasPortrait = hadPortrait;
        if (!hasPortrait)
        {
            _ = await WarmPortraitAsync(http, g, intent != ArtworkWarmIntent.Library, cancellationToken)
                .ConfigureAwait(false);
            hasPortrait = HasCachedPortrait(g);
        }
        if (!hasPortrait)
            LogNoArt(g);

        // Best-effort, and only after the poster: banners never hold the library.
        var hasWide = hadWide;
        if (!hasWide && WarmIntentIncludesWideArt(intent))
        {
            _ = await WarmWideArtAsync(
                    http,
                    g,
                    intent == ArtworkWarmIntent.UserRefetch,
                    cancellationToken)
                .ConfigureAwait(false);
            hasWide = ResolveWideArtUrl(g) is not null;
        }
        return (!hadPortrait && hasPortrait) || (!hadWide && hasWide);
    }

    private static async Task<bool> WarmPortraitAsync(
        HttpClient http,
        GameEntry g,
        bool requested,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Minecraft and Roblox have official Microsoft Store posters. A Steam
        // title search for "Roblox" binds junk and would skip that catalog.
        if (g.Store is StoreKind.Minecraft or StoreKind.Roblox)
        {
            if (await DownloadMicrosoftStorePortraitAsync(http, g, cancellationToken).ConfigureAwait(false))
                return true;
            return TryExtractGameIcon(g);
        }

        // Steam native app id
        var appId = SteamAppId(g);
        if (appId is not null)
        {
            var ok = await DownloadSteamPosterAsync(http, appId, g, cancellationToken).ConfigureAwait(false);
            if (!ok) DeadSteamCdn[appId] = 0;
            else DeadSteamCdn.TryRemove(appId, out _);
            // Steam had no portrait poster (only 3:1 hero art) — ask Epic's
            // catalog for the publisher's real box art instead.
            if (!ok || !HasPortraitArt(appId))
                ok |= await DownloadEpicPortraitAsync(http, g, requested, cancellationToken).ConfigureAwait(false);
            return ok;
        }

        // Mapped Steam CDN art for every other store (covers only). Seed map
        // first, then live title search — that is what fills Xbox / EA / GOG /
        // Ubisoft / Battle.net / Amazon / Rockstar / local tiles.
        var mapped = MappedSteamAppId(g);
        mapped ??= await ResolveSteamAppIdByTitleAsync(http, g, cancellationToken).ConfigureAwait(false);
        if (mapped is not null)
        {
            var ok = await DownloadSteamPosterAsync(http, mapped, g, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                BindGameTitleMap(g, mapped);
                DeadSteamCdn.TryRemove(mapped, out _);
                return true;
            }
            DeadSteamCdn[mapped] = 0;
        }

        if (await DownloadMicrosoftStorePortraitAsync(http, g, cancellationToken).ConfigureAwait(false))
            return true;

        // Riot: Epic portrait first (fast when catcache hits), then theme card.
        if (g.Store == StoreKind.Riot)
        {
            var epic = await DownloadEpicPortraitAsync(http, g, requested, cancellationToken).ConfigureAwait(false);
            if (epic) return true;
            if (await DownloadRiotThemeArtAsync(http, g, cancellationToken).ConfigureAwait(false)) return true;
            return TryExtractGameIcon(g);
        }

        var downloaded = await DownloadGogArtAsync(http, g, cancellationToken).ConfigureAwait(false);

        // Local folder files
        if (!string.IsNullOrWhiteSpace(g.Path) && Directory.Exists(g.Path))
            _ = WithCover(g);

        if (!downloaded)
            downloaded = await DownloadEpicPortraitAsync(http, g, requested, cancellationToken).ConfigureAwait(false);

        if (!downloaded)
            downloaded = TryExtractGameIcon(g);

        return downloaded;
    }

    /// <summary>
    /// Landscape art for banners, cached as <c>hero_&lt;id&gt;.jpg</c>. Store art
    /// only — a title with none keeps the UI's washed-poster fallback.
    /// </summary>
    private static async Task<bool> WarmWideArtAsync(
        HttpClient http,
        GameEntry g,
        bool requested,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!requested && !ShouldWarmWideArt(g)) return false;

        var dest = Path.Combine(CacheRoot, WideArtFileName(g.Id));
        if (IsValidImageFile(dest) && IsWideArt(dest)) return true;
        // A leftover that is not landscape would stretch across every banner.
        TryDelete(dest);

        try
        {
            // Steam's own hero art: the local client cache is free, the CDN is one hop.
            var appId = SteamAppId(g) ?? MappedSteamAppId(g);
            if (appId is not null)
            {
                if (TryImportSteamLibraryHero(appId, dest)) return true;
                if (await TryDownloadWideAsync(http, SteamHeroCdnUrls(appId), dest, cancellationToken).ConfigureAwait(false))
                    return true;
            }

            // Epic wide key images: offline entitlement catalog first, then the store.
            if (g.Store is StoreKind.Epic or StoreKind.Riot)
            {
                var local = EpicCatCacheArt.FindWideUrl(g.Title, g.LaunchTarget, EpicArtifactSuffix(g));
                if (local is not null &&
                    await TryDownloadWideAsync(http, [local], dest, cancellationToken).ConfigureAwait(false))
                    return true;

                var catalog = await EpicCatalogArt
                    .FindWideUrlAsync(
                        http,
                        g.Title,
                        new[] { g.LaunchTarget, EpicArtifactSuffix(g) },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (catalog is not null &&
                    await TryDownloadWideAsync(http, [catalog], dest, cancellationToken).ConfigureAwait(false))
                    return true;
            }

            if (g.Store == StoreKind.Gog)
            {
                var gog = await GogWideUrlsAsync(http, g, cancellationToken).ConfigureAwait(false);
                if (await TryDownloadWideAsync(http, gog, dest, cancellationToken).ConfigureAwait(false))
                    return true;
            }

            // Riot's own theme manifest names the art its client shows.
            if (g.Store == StoreKind.Riot &&
                await DownloadRiotWideArtAsync(http, g, dest, cancellationToken).ConfigureAwait(false))
                return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Wide art failed for '{g.Title}': {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Download landscape art and keep it only when it really is landscape.
    /// </summary>
    private static async Task<bool> TryDownloadWideAsync(
        HttpClient http,
        IEnumerable<string> urls,
        string dest,
        CancellationToken cancellationToken)
    {
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!await TryDownloadAnyAsync(http, [url], dest, cancellationToken).ConfigureAwait(false)) continue;
            if (IsWideArt(dest)) return true;
            TryDelete(dest);
        }
        return false;
    }

    /// <summary>Steam already stores library_hero locally for anything opened in Steam.</summary>
    private static bool TryImportSteamLibraryHero(string appId, string dest)
    {
        if (!IsUsableAppId(appId)) return false;
        try
        {
            var steamRoot = TryFindSteamInstallPath();
            if (steamRoot is null) return false;
            var root = Path.Combine(steamRoot, "appcache", "librarycache", appId);
            if (!Directory.Exists(root)) return false;

            foreach (var name in new[] { "library_hero_2x.jpg", "library_hero.jpg" })
            {
                foreach (var src in Directory.EnumerateFiles(root, name, SearchOption.AllDirectories))
                {
                    if (new FileInfo(src).Length < MinCoverBytes) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(src, dest, overwrite: true);
                    if (IsWideArt(dest))
                    {
                        AppLog.Info($"Banner: local Steam hero art for app {appId}.");
                        return true;
                    }
                    TryDelete(dest);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Local Steam hero import fail for {appId}: {ex.Message}");
        }
        return false;
    }

    private static async Task<IReadOnlyList<string>> GogWideUrlsAsync(
        HttpClient http,
        GameEntry g,
        CancellationToken cancellationToken)
    {
        var gogId = GogProductId(g);
        if (gogId is null) return Array.Empty<string>();
        var urls = new List<string>();

        try
        {
            using var v2 = await http.GetAsync(
                    "https://api.gog.com/v2/games/" + gogId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (v2.IsSuccessStatusCode)
            {
                var json = await v2.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                urls.AddRange(ParseGogV2BackgroundUrls(json));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("GOG v2 background fail: " + ex.Message);
        }

        try
        {
            using var resp = await http.GetAsync(
                    "https://api.gog.com/products/" + gogId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("images", out var images))
                {
                    foreach (var key in new[] { "background", "galaxyBackground" })
                    {
                        if (!images.TryGetProperty(key, out var el) ||
                            el.ValueKind != JsonValueKind.String)
                            continue;
                        var path = el.GetString();
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        if (path.StartsWith("//", StringComparison.Ordinal)) path = "https:" + path;
                        if (IsAllowlistedCdnCover(path)) urls.Add(path);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("GOG background API fail: " + ex.Message);
        }

        return urls;
    }

    /// <summary>GOG Galaxy v2 background hrefs with {formatter} expanded to a banner.</summary>
    internal static IReadOnlyList<string> ParseGogV2BackgroundUrls(string json)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("_links", out var links))
                return urls;
            foreach (var key in new[] { "backgroundImage", "galaxyBackgroundImage" })
            {
                if (!links.TryGetProperty(key, out var node)) continue;
                var href = node.TryGetProperty("href", out var h) ? h.GetString() : null;
                if (string.IsNullOrWhiteSpace(href)) continue;
                foreach (var fmt in new[] { "_bg_crop_1920x655", "" })
                {
                    var url = href.Replace("{formatter}", fmt, StringComparison.Ordinal);
                    if (url.Contains("{formatter}", StringComparison.Ordinal)) continue;
                    if (!IsAllowlistedCdnCover(url)) continue;
                    urls.Add(url);
                    if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                        urls.Add(url[..^5] + ".jpg");
                }
            }
        }
        catch
        {
            // Malformed payload — caller tries the next source.
        }
        return urls;
    }

    private static async Task<bool> DownloadRiotWideArtAsync(
        HttpClient http,
        GameEntry g,
        string dest,
        CancellationToken cancellationToken)
    {
        var product = RiotProductId(g);
        if (string.IsNullOrWhiteSpace(product)) return false;
        try
        {
            using var api = Adapters.Riot.RiotClientApi.TryConnect();
            if (api is null) return false;

            var manifestUrl = await api
                .GetThemeManifestUrlAsync(product, Adapters.Cli.RiotCli.DefaultPatchline, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(manifestUrl)) return false;

            using var resp = await http.GetAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var urls = ReadThemeWideImages(doc.RootElement)
                .Select(relative => new Uri(new Uri(manifestUrl), relative).AbsoluteUri)
                .ToList();
            if (urls.Count == 0) return false;

            if (!await TryDownloadWideAsync(http, urls, dest, cancellationToken).ConfigureAwait(false)) return false;
            AppLog.Info($"Banner: Riot theme art for '{g.Title}'.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Riot wide theme art failed for '{g.Title}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Landscape theme art keys the Riot client uses for its own backgrounds.</summary>
    internal static IReadOnlyList<string> ReadThemeWideImages(JsonElement root)
    {
        var list = new List<string>();

        void Add(JsonElement parent, string key)
        {
            if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
                return;
            if (el.GetString() is { Length: > 0 } value && !list.Contains(value, StringComparer.Ordinal))
                list.Add(value);
        }

        if (root.TryGetProperty("game_library", out var lib) && lib.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "background_image", "game_background_image", "hero_image" })
                Add(lib, key);
        }
        foreach (var key in new[] { "background_image", "splash_image", "product_image" })
            Add(root, key);
        return list;
    }

    /// <summary>
    /// One honest line per title Exo could not find a poster for, so the log
    /// says which stores actually have nothing rather than hiding the gap.
    /// </summary>
    private static void LogNoArt(GameEntry g)
    {
        if (!NoArtLogged.TryAdd(g.Id, 0)) return;
        AppLog.Info($"Cover: no portrait for '{g.Title}' ({g.Store}) — {PortraitGapReason(g)}.");
    }

    private static string PortraitGapReason(GameEntry g)
    {
        var appId = SteamAppId(g) ?? MappedSteamAppId(g);
        if (appId is not null)
            return $"Steam app {appId} publishes no portrait poster and the local Steam cache has none";
        if (g.Store is StoreKind.Epic or StoreKind.Riot)
            return EpicCatalogArt.IsBlocked
                ? "no tall key image in the local Epic catalog, and Epic store lookups are paused"
                : "no tall key image in the local Epic catalog or the Epic store";
        if (g.Store == StoreKind.Gog)
            return "the GOG product API returned no vertical cover";
        if (g.Installed && GameIconArt.FindExecutable(g) is not null)
            return "no store portrait; the executable icon is the fallback";
        return "no Steam title match and the store publishes no portrait Exo can key";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* */ }
    }

    /// <summary>
    /// Library tiles that should get a disk cover. Engine plugins and the
    /// Add-portable row never go online.
    /// </summary>
    public static bool ShouldWarmLibraryCover(GameEntry game)
    {
        if (string.Equals(game.Id, "local:add", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(game.Title, "Add portable game", StringComparison.OrdinalIgnoreCase))
            return false;
        if (LooksLikeEngineAsset(game.Title)) return false;
        return game.Installed || game.IsFavorite || game.CanInstall || game.Owned;
    }

    /// <summary>
    /// Background work is complete only when a portrait exists and any title
    /// eligible for a visible banner also has wide art.
    /// </summary>
    internal static bool NeedsBackgroundWarm(GameEntry game)
        => NeedsWarm(game, ArtworkWarmIntent.Library);

    internal static bool WarmIntentIncludesWideArt(ArtworkWarmIntent intent) =>
        intent is ArtworkWarmIntent.Library or ArtworkWarmIntent.UserRefetch;

    private static bool NeedsWarm(GameEntry game, ArtworkWarmIntent intent)
    {
        if (intent == ArtworkWarmIntent.Library && !ShouldWarmLibraryCover(game)) return false;
        if (!HasCachedPortrait(game)) return true;
        if (!WarmIntentIncludesWideArt(intent)) return false;
        if (ResolveWideArtUrl(game) is not null) return false;
        return intent == ArtworkWarmIntent.UserRefetch || ShouldWarmWideArt(game);
    }

    /// <summary>Official GOG statics already on the row, never an invented {id}_product_tile URL.</summary>
    internal static IReadOnlyList<string> GogCoverCandidateUrls(GameEntry g)
    {
        var list = new List<string>();
        var raw = g.CoverUrl;
        if (string.IsNullOrWhiteSpace(raw)) return list;
        if (raw.StartsWith("//", StringComparison.Ordinal))
            raw = "https:" + raw;
        if (!IsAllowlistedCdnCover(raw)) return list;
        list.Add(raw);
        if (raw.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            list.Add(raw[..^5] + ".jpg");
        return list;
    }

    /// <summary>GOG Galaxy v2 image hrefs with {formatter} expanded to a tall cover.</summary>
    internal static IReadOnlyList<string> ParseGogV2CoverUrls(string json)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("_links", out var links))
                return urls;
            foreach (var key in new[] { "boxArtImage", "image" })
            {
                if (!links.TryGetProperty(key, out var node)) continue;
                var href = node.TryGetProperty("href", out var h) ? h.GetString() : null;
                if (string.IsNullOrWhiteSpace(href)) continue;
                foreach (var fmt in new[] { "_glx_vertical_cover", "_product_tile_256_2x", "" })
                {
                    var url = href.Replace("{formatter}", fmt, StringComparison.Ordinal);
                    if (url.Contains("{formatter}", StringComparison.Ordinal)) continue;
                    urls.Add(url);
                    if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                        urls.Add(url[..^5] + ".jpg");
                }
            }
        }
        catch
        {
            // Malformed payload — caller tries the next source.
        }
        return urls;
    }

    internal static bool IsCoverImageBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 3) return false;
        if (bytes[0] == (byte)'<') return false;
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) return true;
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50) return true;
        return bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[8] == (byte)'W';
    }

    private static async Task<bool> DownloadGogArtAsync(
        HttpClient http,
        GameEntry g,
        CancellationToken cancellationToken)
    {
        var gogId = GogProductId(g);
        if (gogId is null) return false;

        var dest = Path.Combine(CacheRoot, "gog_" + gogId + ".jpg");
        if (IsValidImageFile(dest) && IsPortraitCover(dest))
            return true;
        DiscardIfLandscape(dest);

        if (await TryDownloadAnyAsync(http, GogCoverCandidateUrls(g), dest, cancellationToken).ConfigureAwait(false))
        {
            if (IsPortraitCover(dest)) return true;
            DiscardIfLandscape(dest);
        }

        try
        {
            using var v2 = await http.GetAsync(
                    "https://api.gog.com/v2/games/" + gogId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (v2.IsSuccessStatusCode)
            {
                var json = await v2.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (await TryDownloadAnyAsync(http, ParseGogV2CoverUrls(json), dest, cancellationToken).ConfigureAwait(false))
                {
                    if (IsPortraitCover(dest)) return true;
                    DiscardIfLandscape(dest);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("GOG v2 cover fail: " + ex.Message);
        }

        try
        {
            using var resp = await http.GetAsync(
                    "https://api.gog.com/products/" + gogId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("images", out var images)) return false;
            foreach (var key in new[] { "logo2x", "logo", "icon", "sidebarIcon2x", "sidebarIcon" })
            {
                if (!images.TryGetProperty(key, out var el)) continue;
                var path = el.GetString();
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (path.StartsWith("//", StringComparison.Ordinal))
                    path = "https:" + path;
                if (!await TryDownloadAnyAsync(http, new[] { path }, dest, cancellationToken).ConfigureAwait(false))
                    continue;
                if (IsPortraitCover(dest)) return true;
                DiscardIfLandscape(dest);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("GOG cover API fail: " + ex.Message);
        }

        return false;
    }

    /// <summary>
    /// Engine plugins, sample projects, and editor tools are entitlements, not
    /// games; they never need cover art and must not be looked up online.
    /// </summary>
    private static readonly string[] NonGameTitleMarkers =
    [
        "plugin", "sample", "starter game", "template", "toolkit", " tool",
        "system c++", "blueprint", "component", "framework", "editor",
        "optimisation", "optimization", "shader", "unreal engine",
    ];

    public static bool LooksLikeEngineAsset(string title)
    {
        var t = (title ?? "").ToLowerInvariant();
        return NonGameTitleMarkers.Any(m => t.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>Online art is worth a request only for titles Exo actually shows.</summary>
    private static bool ShouldSeekOnlineArt(GameEntry g, bool requested)
    {
        if (LooksLikeEngineAsset(g.Title)) return false;
        // Installed / favorite / installable titles, Riot's small catalog, or a
        // user search hit. Engine assets never reach here.
        return requested || g.Installed || g.IsFavorite || g.CanInstall || g.Owned ||
               g.Store == StoreKind.Riot;
    }

    /// <summary>True when a real portrait poster is already cached for this app id.</summary>
    private static bool HasPortraitArt(string appId)
    {
        foreach (var name in new[] { appId + ".jpg", appId + "_2x.jpg" })
        {
            var path = Path.Combine(CacheRoot, name);
            if (IsValidImageFile(path) && IsPortraitCover(path)) return true;
        }
        return false;
    }

    /// <summary>True when ResolvePreferredUrl already points at a portrait file.</summary>
    private static bool HasCachedPortrait(GameEntry g)
    {
        var path = TryResolveLocalFile(ResolvePreferredUrl(g));
        return path is not null && IsPortraitCover(path);
    }

    private static async Task<bool> DownloadMicrosoftStorePortraitAsync(
        HttpClient http,
        GameEntry g,
        CancellationToken cancellationToken)
    {
        var productId = MicrosoftStoreArt.ProductIdFor(g);
        if (string.IsNullOrWhiteSpace(productId)) return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dest = Path.Combine(CacheRoot, SanitizeId(g.Id) + ".jpg");
            if (IsValidImageFile(dest) && IsPortraitCover(dest) && IsSharpEnough(dest))
                return true;
            DiscardIfLandscape(dest);

            using var response = await http.GetAsync(
                    MicrosoftStoreArt.CatalogUrl(productId),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var urls = MicrosoftStoreArt.PortraitUrlsFromCatalog(json);
            if (urls.Count == 0) return false;
            if (!await TryDownloadAnyAsync(http, urls, dest, cancellationToken).ConfigureAwait(false))
                return false;
            if (!IsPortraitCover(dest))
            {
                DiscardIfLandscape(dest);
                return false;
            }
            AppLog.Info($"Cover: Microsoft Store portrait for '{g.Title}'.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Microsoft Store portrait failed for '{g.Title}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Publisher box art from Epic's public catalog, matched on exact title.
    /// </summary>
    private static async Task<bool> DownloadEpicPortraitAsync(
        HttpClient http,
        GameEntry g,
        bool requested,
        CancellationToken cancellationToken)
    {
        // Only titles the user can actually see. An Epic account carries every
        // Unreal Marketplace entitlement it has ever claimed — asking about all
        // 143 of them in one burst is what got this machine refused by Epic.
        if (!ShouldSeekOnlineArt(g, requested)) return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dest = Path.Combine(CacheRoot, EpicArtFileName(g));
            if (IsValidImageFile(dest) && IsPortraitCover(dest) && IsSharpEnough(dest))
                return true;
            DiscardIfLandscape(dest);

            var url = IsOfficialEpicPortraitCdn(g.CoverUrl) ? g.CoverUrl : null;
            url ??= EpicCatCacheArt.FindPortraitUrl(g.Title, g.LaunchTarget, EpicArtifactSuffix(g));
            if (!IsOfficialEpicPortraitCdn(url))
                url = await EpicCatalogArt.FindPortraitUrlAsync(
                        http,
                        g.Title,
                        new[] { g.LaunchTarget, EpicArtifactSuffix(g) },
                        cancellationToken)
                    .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (url is null)
            {
                AppLog.Debug($"No Epic portrait art for '{g.Title}'.");
                return false;
            }
            if (!await TryDownloadAnyAsync(http, new[] { url }, dest, cancellationToken).ConfigureAwait(false))
            {
                AppLog.Warn($"Epic portrait download failed for '{g.Title}'.");
                return false;
            }
            if (!IsPortraitCover(dest))
            {
                DiscardIfLandscape(dest);
                AppLog.Debug($"Epic art for '{g.Title}' was landscape; discarded.");
                return false;
            }
            AppLog.Info($"Cover: Epic portrait art for '{g.Title}'.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Epic portrait art failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Official Riot art, resolved through the running client's own metadata:
    /// product-metadata gives a theme manifest URL, and the manifest names the
    /// product card image the client itself displays.
    /// </summary>
    private static async Task<bool> DownloadRiotThemeArtAsync(
        HttpClient http,
        GameEntry g,
        CancellationToken cancellationToken)
    {
        var product = RiotProductId(g);
        if (string.IsNullOrWhiteSpace(product)) return false;
        var dest = Path.Combine(CacheRoot, "riot_" + SanitizeId(product) + "_card.png");
        if (IsValidImageFile(dest) && IsPortraitCover(dest)) return true;
        DiscardIfLandscape(dest);

        try
        {
            using var api = Adapters.Riot.RiotClientApi.TryConnect();
            if (api is null) return false;

            var manifestUrl = await api
                .GetThemeManifestUrlAsync(product, Adapters.Cli.RiotCli.DefaultPatchline, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(manifestUrl)) return false;

            using var resp = await http.GetAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var relative = ReadThemeImage(doc.RootElement);
            if (string.IsNullOrWhiteSpace(relative)) return false;

            var absolute = new Uri(new Uri(manifestUrl), relative).AbsoluteUri;
            if (!await TryDownloadAnyAsync(http, new[] { absolute }, dest, cancellationToken).ConfigureAwait(false))
                return false;
            if (!IsPortraitCover(dest))
            {
                DiscardIfLandscape(dest);
                AppLog.Debug($"Cover: Riot theme art for '{g.Title}' was landscape; discarded.");
                return false;
            }
            AppLog.Info($"Cover: Riot theme portrait art for '{g.Title}'.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Riot theme art failed for '{g.Title}': {ex.Message}");
            return false;
        }
    }

    private static string? ReadThemeImage(JsonElement root)
    {
        if (root.TryGetProperty("game_library", out var lib) &&
            lib.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "product_card_image", "game_card_image" })
            {
                if (lib.TryGetProperty(key, out var el) && el.GetString() is { Length: > 0 } v)
                    return v;
            }
        }
        foreach (var key in new[] { "splash_image", "product_image" })
        {
            if (root.TryGetProperty(key, out var el) && el.GetString() is { Length: > 0 } v)
                return v;
        }
        return null;
    }

    private static string EpicArtFileName(GameEntry g)
    {
        if (g.Store == StoreKind.Riot)
        {
            var product = RiotProductId(g);
            if (!string.IsNullOrWhiteSpace(product))
                return "riot_" + SanitizeId(product) + ".jpg";
        }
        return SanitizeId(g.Id) + ".jpg";
    }

    private static async Task<bool> DownloadSteamPosterAsync(
        HttpClient http,
        string appId,
        GameEntry g,
        CancellationToken cancellationToken)
    {
        var dest2x = Path.Combine(CacheRoot, appId + "_2x.jpg");
        var dest = Path.Combine(CacheRoot, appId + ".jpg");
        PurgeTinyCover(dest2x);
        PurgeTinyCover(dest);
        PurgeTinyCover(Path.Combine(CacheRoot, "steam_" + appId + ".jpg"));
        var gameId = g.Id;

        if (HasPortraitArt(appId))
        {
            SyncSlugPortrait(appId, gameId, dest2x, dest);
            return true;
        }

        // Instant: Steam client already cached the capsule.
        if (TryCopyLocalSteamLibraryCapsule(appId, dest2x, dest) && HasPortraitArt(appId))
        {
            SyncSlugPortrait(appId, gameId, dest2x, dest);
            return true;
        }

        var ok = false;

        // Newer apps: hashed library_capsule first (classic library_600x900 often 404s).
        // Race one classic 1x URL in parallel so older apps stay fast without 2x RAM.
        var classicFirst = string.Format(SteamPosterTemplates[0], appId);
        var classicTask = IsValidImageFile(dest) && IsPortraitCover(dest)
            ? Task.FromResult(true)
            : TryDownloadAnyAsync(http, new[] { classicFirst }, dest, cancellationToken);
        var capsuleTask = DownloadSteamLibraryCapsuleAsync(
            http,
            appId,
            dest2x,
            dest,
            cancellationToken);
        await Task.WhenAll(classicTask, capsuleTask).ConfigureAwait(false);
        ok = await classicTask.ConfigureAwait(false) | await capsuleTask.ConfigureAwait(false);

        DiscardIfLandscape(dest);
        DiscardIfLandscape(dest2x);

        // Remaining classic CDN mirrors if still no portrait.
        if (!HasPortraitArt(appId))
        {
            if (!IsHighResPoster(dest2x))
            {
                ok = await TryDownloadAnyAsync(
                        http,
                        SteamPosterTemplates.Where(t => t.Contains("_2x", StringComparison.Ordinal))
                            .Skip(1)
                            .Select(t => string.Format(t, appId)),
                        dest2x,
                        cancellationToken)
                    .ConfigureAwait(false) || ok;
            }

            if (!IsValidImageFile(dest))
            {
                if (!IsValidImageFile(dest2x))
                {
                    ok = await TryDownloadAnyAsync(
                            http,
                            SteamPosterTemplates
                                .Where(t => !t.Contains("_2x", StringComparison.Ordinal))
                                .Select(t => string.Format(t, appId)),
                            dest,
                            cancellationToken)
                        .ConfigureAwait(false)
                        || ok;
                }
            }
        }
        else ok = true;

        DiscardIfLandscape(dest);
        DiscardIfLandscape(dest2x);

        if (HasPortraitArt(appId))
        {
            SyncSlugPortrait(appId, gameId, dest2x, dest);
            return true;
        }

        var slugDest = Path.Combine(CacheRoot, SanitizeId(gameId) + ".jpg");
        DiscardIfLandscape(slugDest);
        return await TrySteamIconPlateAsync(http, appId, g, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryExtractGameIcon(GameEntry g)
    {
        try
        {
            var dest = Path.Combine(CacheRoot, GameIconArt.CacheFileName(g.Id));
            if (GameIconArt.IsValidPlate(dest)) return true;
            Directory.CreateDirectory(CacheRoot);
            if (!GameIconArt.TryExtract(g, dest)) return false;
            AppLog.Info($"Cover: executable icon for '{g.Title}' ({g.Store}).");
            return GameIconArt.IsValidPlate(dest);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Icon extract failed for '{g.Title}': {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> TrySteamIconPlateAsync(
        HttpClient http,
        string appId,
        GameEntry g,
        CancellationToken cancellationToken)
    {
        var dest = Path.Combine(CacheRoot, GameIconArt.CacheFileName(g.Id));
        if (GameIconArt.IsValidPlate(dest)) return true;
        Directory.CreateDirectory(CacheRoot);

        if (TryImportLocalSteamIcon(appId, dest))
        {
            AppLog.Info($"Cover: Steam cache icon for app {appId}.");
            return true;
        }

        if (!SteamCommunityIcon.TryGetValue(appId, out var hash) || string.IsNullOrWhiteSpace(hash))
            await ResolveSteamLibraryCapsuleUrlsAsync(http, appId, cancellationToken).ConfigureAwait(false);
        if (SteamCommunityIcon.TryGetValue(appId, out hash) && !string.IsNullOrWhiteSpace(hash))
        {
            var url =
                "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/"
                + appId + "/" + hash + ".jpg";
            var tmp = dest + ".src";
            try
            {
                if (await TryDownloadLooseAsync(http, url, tmp, cancellationToken).ConfigureAwait(false) &&
                    GameIconArt.TryWritePlateFromImage(tmp, dest))
                {
                    AppLog.Info($"Cover: Steam community icon for app {appId}.");
                    return true;
                }
            }
            finally
            {
                TryDelete(tmp);
            }
        }

        return TryExtractGameIcon(g);
    }

    private static bool TryImportLocalSteamIcon(string appId, string dest)
    {
        try
        {
            var steamRoot = TryFindSteamInstallPath();
            if (steamRoot is null) return false;
            var root = Path.Combine(steamRoot, "appcache", "librarycache", appId);
            if (!Directory.Exists(root)) return false;
            string? best = null;
            long bestLen = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (!name.Contains("icon", StringComparison.OrdinalIgnoreCase) &&
                    name.Length < 40)
                    continue;
                // Hashed community icons are 40-char sha1 names; tiny files still plate.
                var len = new FileInfo(path).Length;
                if (len < 200 || len > 2 * 1024 * 1024) continue;
                if (len > bestLen) { best = path; bestLen = len; }
            }
            if (best is null) return false;
            return GameIconArt.TryWritePlateFromImage(best, dest);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryDownloadLooseAsync(
        HttpClient http,
        string url,
        string dest,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await DownloadValidatedImageAsync(
                    http,
                    url,
                    dest,
                    minimumBytes: 200,
                    maximumBytes: 2 * 1024 * 1024,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Loose cover fetch fail {ArtworkLogLabel(url)}: {ex.Message}");
            return false;
        }
    }

    private static void SyncSlugPortrait(string appId, string gameId, string dest2x, string dest)
    {
        // Native Steam rows resolve the canonical numeric files directly.
        // Writing steam_<appid>.jpg was a full third copy of the same poster.
        if (gameId.Equals("steam:" + appId, StringComparison.OrdinalIgnoreCase)) return;

        var best = (IsValidImageFile(dest2x) && IsPortraitCover(dest2x)) ? dest2x
            : (IsValidImageFile(dest) && IsPortraitCover(dest)) ? dest
            : null;
        if (best is null) return;
        try
        {
            File.Copy(best, Path.Combine(CacheRoot, SanitizeId(gameId) + ".jpg"), overwrite: true);
        }
        catch { /* */ }
    }

    private static void PurgeTinyCover(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length < MinCoverBytes)
                File.Delete(path);
        }
        catch { /* */ }
    }

    /// <summary>
    /// Resolve hashed library_capsule URLs from Steam's public GetItems API and download.
    /// </summary>
    private static async Task<bool> DownloadSteamLibraryCapsuleAsync(
        HttpClient http,
        string appId,
        string dest2x,
        string dest,
        CancellationToken cancellationToken)
    {
        try
        {
            var urls = await ResolveSteamLibraryCapsuleUrlsAsync(http, appId, cancellationToken).ConfigureAwait(false);
            if (urls.Count == 0) return false;

            // URLs are ordered 2x then 1x across CDNs. Do not filter on the
            // filename — some apps name the asset library_600x900_2x.jpg while
            // the GetItems field is still library_capsule_2x.
            var got2x = IsHighResPoster(dest2x) && IsPortraitCover(dest2x);
            if (!got2x)
            {
                got2x = await TryDownloadAnyAsync(http, urls, dest2x, cancellationToken).ConfigureAwait(false);
                if (got2x && !IsPortraitCover(dest2x))
                {
                    DiscardIfLandscape(dest2x);
                    got2x = false;
                }
            }

            var got1x = IsValidImageFile(dest) && IsPortraitCover(dest);
            if (!got1x && !got2x)
            {
                got1x = await TryDownloadAnyAsync(http, urls, dest, cancellationToken).ConfigureAwait(false);
                if (got1x && !IsPortraitCover(dest))
                {
                    DiscardIfLandscape(dest);
                    got1x = false;
                }
            }

            if (got2x || got1x)
            {
                AppLog.Info($"Cover: Steam library_capsule portrait for app {appId}.");
                return true;
            }
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Steam library_capsule fail for {appId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Public for tests — builds CDN URLs from GetItems asset fields.
    /// </summary>
    public static IReadOnlyList<string> BuildSteamLibraryCapsuleUrls(
        string? assetUrlFormat, string? capsule2x, string? capsule1x)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(assetUrlFormat)) return list;
        foreach (var file in new[] { capsule2x, capsule1x })
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            var relative = assetUrlFormat.Replace("${FILENAME}", file, StringComparison.Ordinal);
            foreach (var prefix in SteamLibraryCapsuleCdnPrefixes)
                list.Add(prefix + relative);
        }
        return list;
    }

    private static async Task<IReadOnlyList<string>> ResolveSteamLibraryCapsuleUrlsAsync(
        HttpClient http,
        string appId,
        CancellationToken cancellationToken = default)
    {
        if (!IsUsableAppId(appId)) return Array.Empty<string>();
        var input = $"{{\"ids\":[{{\"appid\":{appId}}}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\",\"steam_realm\":1}},\"data_request\":{{\"include_assets\":true}}}}";
        var url =
            "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json="
            + Uri.EscapeDataString(input);
        using var resp = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("store_items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("assets", out var assets)) continue;
            var fmt = assets.TryGetProperty("asset_url_format", out var fmtEl) ? fmtEl.GetString() : null;
            var c2 = assets.TryGetProperty("library_capsule_2x", out var c2El) ? c2El.GetString() : null;
            var c1 = assets.TryGetProperty("library_capsule", out var c1El) ? c1El.GetString() : null;
            var built = BuildSteamLibraryCapsuleUrls(fmt, c2, c1);
            if (assets.TryGetProperty("community_icon", out var iconEl))
            {
                var hash = iconEl.GetString();
                if (!string.IsNullOrWhiteSpace(hash))
                    SteamCommunityIcon[appId] = hash;
            }
            if (built.Count > 0)
            {
                var oneX = built.FirstOrDefault(u =>
                    !u.Contains("_2x", StringComparison.OrdinalIgnoreCase)) ?? built[0];
                SteamCapsuleCdn[appId] = oneX;
                return built;
            }
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Copy Steam's already-downloaded portrait (library_600x900 or
    /// library_capsule) into Exo's cover cache. Works offline on any PC that
    /// has opened the title in Steam — no CDN, no Exo disk cache required.
    /// </summary>
    public static bool TryImportSteamLibraryCachePoster(
        string appId, string dest2x, string dest, string? steamRoot = null)
    {
        if (!IsUsableAppId(appId) || string.IsNullOrWhiteSpace(dest)) return false;
        try
        {
            steamRoot = string.IsNullOrWhiteSpace(steamRoot)
                ? TryFindSteamInstallPath()
                : steamRoot;
            if (steamRoot is null) return false;
            var root = Path.Combine(steamRoot, "appcache", "librarycache", appId);
            if (!Directory.Exists(root)) return false;

            DiscardIfLandscape(dest);
            if (!string.IsNullOrWhiteSpace(dest2x))
                DiscardIfLandscape(dest2x);

            string? src1x = null;
            string? src2x = null;
            var src1xRank = 0;
            var src2xRank = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                var rank = SteamLibraryCachePosterRank(name);
                if (rank == 0 || !IsValidImageFile(path) || !IsPortraitCover(path)) continue;
                if (name.Contains("_2x", StringComparison.OrdinalIgnoreCase))
                {
                    if (rank > src2xRank) { src2x = path; src2xRank = rank; }
                }
                else if (rank > src1xRank)
                {
                    src1x = path;
                    src1xRank = rank;
                }
            }

            if (src2x is not null && !string.IsNullOrWhiteSpace(dest2x))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest2x)!);
                File.Copy(src2x, dest2x, overwrite: true);
            }
            if (src1x is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src1x, dest, overwrite: true);
            }
            if (!IsPortraitCover(dest) && !IsPortraitCover(dest2x))
                TryImportHashedSteamPortrait(root, dest);

            DiscardIfLandscape(dest);
            if (!string.IsNullOrWhiteSpace(dest2x))
                DiscardIfLandscape(dest2x);
            var imported = IsPortraitCover(dest) || IsPortraitCover(dest2x);
            if (imported)
                AppLog.Info($"Cover: local Steam library cache for app {appId}.");
            return imported;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Local Steam library cache fail for {appId}: {ex.Message}");
            return false;
        }
    }

    private static int SteamLibraryCachePosterRank(string fileName)
    {
        if (fileName.Equals("library_600x900.jpg", StringComparison.OrdinalIgnoreCase)) return 4;
        if (fileName.Equals("library_600x900_2x.jpg", StringComparison.OrdinalIgnoreCase)) return 3;
        if (fileName.Equals("library_capsule.jpg", StringComparison.OrdinalIgnoreCase)) return 2;
        if (fileName.Equals("library_capsule_2x.jpg", StringComparison.OrdinalIgnoreCase)) return 1;
        if (fileName.Contains("library_hero", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("header", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("logo", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (fileName.Contains("library_600x900", StringComparison.OrdinalIgnoreCase)) return 3;
        if (fileName.Contains("library_capsule", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    /// <summary>
    /// Newer Steam librarycache folders only keep a hashed .jpg plus header.
    /// If that hashed file is already a portrait poster, use it offline.
    /// </summary>
    private static bool TryImportHashedSteamPortrait(string root, string dest)
    {
        try
        {
            string? best = null;
            long bestLen = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (SteamLibraryCachePosterRank(name) != 0) continue;
                if (name.Contains("header", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("library_hero", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("logo", StringComparison.OrdinalIgnoreCase))
                    continue;
                var info = new FileInfo(path);
                if (info.Length < MinCoverBytes || !IsPortraitCover(path)) continue;
                if (info.Length > bestLen)
                {
                    best = path;
                    bestLen = info.Length;
                }
            }
            if (best is null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(best, dest, overwrite: true);
            if (!IsPortraitCover(dest))
            {
                DiscardIfLandscape(dest);
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Steam client already caches official library_capsule art locally — use it
    /// when the network path is blocked and classic CDN 404s.
    /// </summary>
    private static bool TryCopyLocalSteamLibraryCapsule(string appId, string dest2x, string dest) =>
        TryImportSteamLibraryCachePoster(appId, dest2x, dest);

    private static string? TryFindSteamInstallPath()
    {
        try
        {
            using var cu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = cu?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                return path.Replace('/', Path.DirectorySeparatorChar);
        }
        catch { /* */ }
        try
        {
            using var lm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            var path = lm?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                return path;
        }
        catch { /* */ }
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
                 })
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>True when file is a real hi-res poster (2x is typically 100KB+).</summary>
    private static bool IsHighResPoster(string path)
    {
        try
        {
            if (!IsValidImageFile(path)) return false;
            return new FileInfo(path).Length >= 40_000;
        }
        catch { return false; }
    }

    /// <summary>
    /// Resolve Steam app id for cover art: seed map → community SearchApps → storesearch.
    /// </summary>
    private static async Task<string?> ResolveSteamAppIdByTitleAsync(
        HttpClient http,
        GameEntry g,
        CancellationToken cancellationToken)
    {
        EnsureTitleMapLoaded();
        var keys = TitleLookupKeys(g.Title);
        if (keys.Count == 0) return null;
        if (TryGetTitleBoundGameMap(g, keys, out var cached))
            return cached;
        var key = keys[0];
        var scoreKey = keys[^1];
        foreach (var lookup in keys)
        {
            if (TitleSteamMap.TryGetValue(lookup, out var byTitle) && IsUsableAppId(byTitle))
            {
                BindGameTitleMap(g, byTitle);
                return byTitle;
            }
            if (SeedTitleSteamIds.TryGetValue(lookup, out var seed) && IsUsableAppId(seed))
            {
                BindGameTitleMap(g, seed);
                TitleSteamMap[lookup] = seed;
                return seed;
            }
        }
        if (keys.Any(k => TitleSteamMap.ContainsKey("!" + k)))
            return null;

        try
        {
            var searchTitle = CleanSearchTitle(g.Title);
            if (string.IsNullOrWhiteSpace(searchTitle)) return null;
            var acceptScore = AcceptSteamTitleScore(searchTitle);

            string? best = null;
            var bestScore = -1;

            // 1) steamcommunity SearchApps — more reliable than storesearch for many titles
            await CollectSearchAppsAsync(http, searchTitle, scoreKey, (id, score) =>
            {
                if (score > bestScore) { bestScore = score; best = id; }
            }, cancellationToken).ConfigureAwait(false);

            // 2) store API as secondary
            if (bestScore < acceptScore)
            {
                await CollectStoreSearchAsync(http, searchTitle, scoreKey, (id, score) =>
                {
                    if (score > bestScore) { bestScore = score; best = id; }
                }, cancellationToken).ConfigureAwait(false);
            }

            // 3) Shorter query (first 2–3 words) when under accept threshold — require strong score.
            if (bestScore < acceptScore)
            {
                var words = searchTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 2)
                {
                    var shortQ = string.Join(' ', words.Take(Math.Min(3, words.Length)));
                    await CollectSearchAppsAsync(http, shortQ, scoreKey, (id, score) =>
                    {
                        if (score >= acceptScore && score > bestScore) { bestScore = score; best = id; }
                    }, cancellationToken).ConfigureAwait(false);
                }
            }

            // Strong match before committing a Steam map (82 for 2+ token titles, 90 for short).
            if (best is null || bestScore < acceptScore)
            {
                TitleSteamMap["!" + key] = NegativeSteamMapValue();
                return null;
            }

            // Verify poster actually exists on CDN before committing the map
            if (!await SteamPosterExistsAsync(http, best, cancellationToken).ConfigureAwait(false))
            {
                TitleSteamMap["!" + key] = NegativeSteamMapValue();
                return null;
            }

            BindGameTitleMap(g, best);
            foreach (var lookup in keys)
                TitleSteamMap[lookup] = best;
            return best;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam title search fail: " + ex.Message);
            return null;
        }
    }

    internal static string CleanSearchTitle(string title)
    {
        var searchTitle = SplitCamelTitle(title
            .Replace("™", "", StringComparison.Ordinal)
            .Replace("®", "", StringComparison.Ordinal)
            .Replace("©", "", StringComparison.Ordinal));
        foreach (var junk in new[]
                 {
                     " - Deluxe Edition", " Deluxe Edition", " - Ultimate Edition", " Ultimate Edition",
                     " - Gold Edition", " Gold Edition",
                     " - Game of the Year Edition", " Game of the Year Edition", " Game of the Year", " GOTY",
                     " - Standard Edition", " Standard Edition", " (Epic)", " (Steam)", " (GOG)",
                     " - Director's Cut", " Director's Cut", " Complete Edition", " Definitive Edition",
                     " Remastered", " Enhanced Edition", " Anniversary Edition",
                     " - Premium Edition", " Premium Edition",
                     " - Windows Edition", " Windows Edition",
                     " - Legendary Edition", " Legendary Edition",
                 })
        {
            var idx = searchTitle.IndexOf(junk, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) searchTitle = searchTitle[..idx].Trim();
        }
        return string.Join(' ', searchTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Xbox / camel folder names: ForzaHorizon5 → Forza Horizon 5, HaloInfinite → Halo Infinite.
    /// Splits lower→Upper and letter↔digit.
    /// </summary>
    internal static string SplitCamelTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return title;
        var chars = new List<char>(title.Length + 8);
        for (var i = 0; i < title.Length; i++)
        {
            if (i > 0)
            {
                var prev = title[i - 1];
                var cur = title[i];
                if (char.IsUpper(cur) && char.IsLower(prev))
                    chars.Add(' ');
                else if ((char.IsLetter(prev) && char.IsDigit(cur)) ||
                         (char.IsDigit(prev) && char.IsLetter(cur)))
                    chars.Add(' ');
            }
            chars.Add(title[i]);
        }
        return new string(chars.ToArray());
    }

    /// <summary>Steam title map accept score: 82 for 2+ token titles, 90 for short names.</summary>
    internal static int AcceptSteamTitleScore(string searchTitle)
    {
        var tokens = searchTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length >= 2 ? 82 : 90;
    }

    private delegate void ScoreSink(string appId, int score);

    private static async Task CollectSearchAppsAsync(
        HttpClient http,
        string term,
        string titleNorm,
        ScoreSink sink,
        CancellationToken cancellationToken)
    {
        try
        {
            var q = Uri.EscapeDataString(term.Trim());
            var url = $"https://steamcommunity.com/actions/SearchApps/{q}";
            using var resp = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json) || json.TrimStart().StartsWith('<')) return;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.TryGetProperty("appid", out var idEl)
                    ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32().ToString() : idEl.GetString())
                    : null;
                if (!IsUsableAppId(id)) continue;
                var name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var nameNorm = NormalizeTitleKey(name);
                if (IsJunkStoreName(nameNorm)) continue;
                sink(id!, ScoreTitleMatch(titleNorm, nameNorm));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("SearchApps fail: " + ex.Message);
        }
    }

    private static async Task CollectStoreSearchAsync(
        HttpClient http,
        string term,
        string titleNorm,
        ScoreSink sink,
        CancellationToken cancellationToken)
    {
        try
        {
            var q = Uri.EscapeDataString(term.Trim());
            var url = $"https://store.steampowered.com/api/storesearch/?term={q}&l=english&cc=US";
            using var resp = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return;
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var typeEl) &&
                    !string.Equals(typeEl.GetString(), "app", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!item.TryGetProperty("id", out var idEl)) continue;
                var id = idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt32().ToString()
                    : idEl.GetString();
                if (!IsUsableAppId(id)) continue;
                var name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var nameNorm = NormalizeTitleKey(name);
                if (IsJunkStoreName(nameNorm)) continue;
                sink(id!, ScoreTitleMatch(titleNorm, nameNorm));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("storesearch fail: " + ex.Message);
        }
    }

    private static bool IsJunkStoreName(string nameNorm) =>
        nameNorm.Contains("soundtrack", StringComparison.Ordinal) ||
        nameNorm.Contains(" ost", StringComparison.Ordinal) ||
        nameNorm.Contains("dlc", StringComparison.Ordinal) ||
        nameNorm.Contains("server", StringComparison.Ordinal) ||
        nameNorm.Contains("dedicated", StringComparison.Ordinal) ||
        nameNorm.Contains("demo", StringComparison.Ordinal) ||
        nameNorm.Contains("trailer", StringComparison.Ordinal) ||
        nameNorm.Contains("playtest", StringComparison.Ordinal) ||
        nameNorm.Contains("sdk", StringComparison.Ordinal) ||
        nameNorm.Contains("tool", StringComparison.Ordinal);

    private static async Task<bool> SteamPosterExistsAsync(
        HttpClient http,
        string appId,
        CancellationToken cancellationToken)
    {
        // Steam often answers HEAD 200 with a tiny placeholder. Only a real
        // image body counts as a poster.
        try
        {
            var url = string.Format(SteamPosterTemplates[0], appId);
            var destination = Path.Combine(CacheRoot, appId + ".jpg");
            if (await DownloadValidatedImageAsync(
                    http,
                    url,
                    destination,
                    MinCoverBytes,
                    8 * 1024 * 1024,
                    cancellationToken)
                .ConfigureAwait(false))
                return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* fall through to library_capsule */ }

        // Newer apps only publish hashed library_capsule portraits.
        try
        {
            var urls = await ResolveSteamLibraryCapsuleUrlsAsync(http, appId, cancellationToken).ConfigureAwait(false);
            return urls.Count > 0 && await TryDownloadAnyAsync(
                    http,
                    urls,
                    Path.Combine(CacheRoot, appId + "_2x.jpg"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    internal static int ScoreTitleMatch(string want, string got)
    {
        if (string.IsNullOrEmpty(want) || string.IsNullOrEmpty(got)) return 0;
        if (want == got) return 100;
        var compactWant = want.Replace(" ", "", StringComparison.Ordinal);
        var compactGot = got.Replace(" ", "", StringComparison.Ordinal);
        if (compactWant.Length >= 8 && compactWant == compactGot) return 100;
        // Prefix only when both sides are long enough — "war"≠"warhammer".
        var minLen = Math.Min(want.Length, got.Length);
        if (minLen >= 10 &&
            (got.StartsWith(want, StringComparison.Ordinal) || want.StartsWith(got, StringComparison.Ordinal)))
            return 92;
        // Substring-only matches are weak (short titles collide) — never persist these.
        if (got.Contains(want, StringComparison.Ordinal) || want.Contains(got, StringComparison.Ordinal))
            return 50;
        // Token overlap
        var wa = want.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ga = got.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (wa.Length == 0 || ga.Length == 0) return 0;
        var hit = wa.Count(t => ga.Any(x => x == t || (t.Length >= 4 && x.StartsWith(t, StringComparison.Ordinal))));
        return (int)(100.0 * hit / Math.Max(wa.Length, ga.Length));
    }

    private static async Task<bool> TryDownloadAnyAsync(
        HttpClient http,
        IEnumerable<string> urls,
        string dest,
        CancellationToken cancellationToken = default)
    {
        foreach (var url in urls)
        {
            try
            {
                if (await DownloadValidatedImageAsync(
                        http,
                        url,
                        dest,
                        MinCoverBytes,
                        8 * 1024 * 1024,
                        cancellationToken)
                    .ConfigureAwait(false))
                    return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Cover fetch fail {ArtworkLogLabel(url)}: {ex.Message}");
            }
        }
        return false;
    }

    internal static string CollisionSafeCacheId(string id)
    {
        var readable = LegacySanitizeId(id);
        if (readable.Length > 48) readable = readable[..48];
        if (readable.Length == 0) readable = "item";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16]
            .ToLowerInvariant();
        return readable + "_" + hash;
    }

    private static string SanitizeId(string id) => CollisionSafeCacheId(id);

    private static IEnumerable<string> CacheIdentityCandidates(string id)
    {
        var current = CollisionSafeCacheId(id);
        yield return current;
        var legacy = LegacySanitizeId(id);
        if (!legacy.Equals(current, StringComparison.OrdinalIgnoreCase))
            yield return legacy;
    }

    private static string ArtworkLogLabel(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "invalid-uri";
        return uri.IdnHost + uri.AbsolutePath;
    }

    private static string LegacySanitizeId(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private static string? CoverSourceFor(GameEntry g, string? coverUrl = null)
    {
        if (GameIconArt.IsCacheUrl(coverUrl) || GameIconArt.IsCacheFileName(Path.GetFileName(coverUrl)))
            return "icon";
        if (SteamAppId(g) is not null || MappedSteamAppId(g) is not null)
            return "steam";
        return g.Store switch
        {
            StoreKind.Steam => "steam",
            StoreKind.Epic => "epic",
            StoreKind.Gog => "gog",
            StoreKind.Riot => "riot",
            StoreKind.Local => "local",
            _ => g.CoverSource,
        };
    }

    private static GameEntry CloneWithCover(
        GameEntry g,
        string? coverUrl,
        string? coverSource) => new()
    {
        Id = g.Id,
        Title = g.Title,
        Store = g.Store,
        Installed = g.Installed,
        Owned = g.Owned,
        EntitlementState = g.EntitlementState,
        UpdateAvailable = g.UpdateAvailable,
        CanInstall = g.CanInstall,
        Path = g.Path,
        CoverUrl = coverUrl,
        CoverSource = coverSource,
        ArtRevision = g.ArtRevision,
        PlaytimeMinutes = g.PlaytimeMinutes,
        SizeBytes = g.SizeBytes,
        Status = g.Status,
        Deps = g.Deps,
        LaunchNote = g.LaunchNote,
        LaunchTarget = g.LaunchTarget,
        LastPlayedUtc = g.LastPlayedUtc,
        IsFavorite = g.IsFavorite,
        CanonicalTitleKey = g.CanonicalTitleKey,
        SelectedVariantId = g.SelectedVariantId,
        Variants = g.Variants,
    };
}
