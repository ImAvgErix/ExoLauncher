using System.Collections.Concurrent;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Cover art: download to disk; prefer virtual-host URLs for the UI (lightweight, many tiles).
/// Never force raw CDN URLs into the UI (broken-image glyphs). Uncached → null → monogram.
/// </summary>
public static class CoverArtService
{
    // Keep first paint clear of speculative cover downloads. Search explicitly
    // opts out because those results were requested by the user just now.
    internal static readonly TimeSpan FirstPaintCoverWarmDelay = TimeSpan.FromMilliseconds(750);
    internal const int BackgroundWarmConcurrency = 4;
    internal const int RequestedWarmConcurrency = 12;

    private static readonly HttpClient CoverHttp = CreateCoverHttpClient();

    public static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExoLauncher", "covers");

    /// <summary>Mapped in WebView2 as https://covers.exo-launcher.local/ — must stay in ui/index.html CSP img-src.</summary>
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
    /// Vertical Steam library posters only (2:3). Prefer 2x, then 1x, multi-CDN.
    /// Never header/capsule — those are landscape and look bad on portrait cards.
    /// </summary>
    private static readonly string[] SteamPosterTemplates =
    [
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900_2x.jpg",
        "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900_2x.jpg",
        "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900_2x.jpg",
        "https://steamcdn-a.akamaihd.net/steam/apps/{0}/library_600x900_2x.jpg",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900.jpg",
        "https://cdn.akamai.steamstatic.com/steam/apps/{0}/library_600x900.jpg",
        "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900.jpg",
        "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900.jpg",
        "https://steamcdn-a.akamaihd.net/steam/apps/{0}/library_600x900.jpg",
    ];

    // No landscape fallbacks. Library tiles are portrait-only; wide heroes are never shown.

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
    };

    private static readonly ConcurrentDictionary<string, byte> WarmInFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (long Len, long Mtime, int W, int H)> ImageSizeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> TitleSteamMap = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Steam app ids whose CDN posters 404'd — do not keep dead URLs on tiles.</summary>
    private static readonly ConcurrentDictionary<string, byte> DeadSteamCdn = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string TitleMapPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExoLauncher", "covers", "title-steam-map.json");

    public static IReadOnlyList<GameEntry> WithCovers(IEnumerable<GameEntry> games) =>
        games.Select(WithCover).ToList();

    public static GameEntry WithCover(GameEntry g)
    {
        var preferred = ResolvePreferredUrl(g);

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            if (string.Equals(preferred, g.CoverUrl, StringComparison.Ordinal))
                return g;
            return CloneWithCover(g, preferred, CoverSourceFor(g));
        }

        // Provisional Steam portrait CDN while disk cache warms — official 2:3 only.
        var provisional = ProvisionalSteamPosterUrl(g);
        if (provisional is not null)
            return CloneWithCover(g, provisional, "steam-cdn");

        if (!string.IsNullOrWhiteSpace(g.CoverUrl) && !IsUiLoadableCoverUrl(g.CoverUrl))
            return CloneWithCover(g, coverUrl: null, coverSource: null);

        return g;
    }

    /// <summary>
    /// Immediate official Steam portrait URL (library_600x900_2x) for search / first paint.
    /// Disk cache + virtual host still win via <see cref="ResolvePreferredUrl"/> when present.
    /// </summary>
    public static string? ProvisionalSteamPosterUrl(GameEntry g)
    {
        var appId = SteamAppId(g) ?? MappedSteamAppId(g);
        if (appId is null || DeadSteamCdn.ContainsKey(appId)) return null;
        return SteamPortraitCdnUrl(appId);
    }

    public static string SteamPortraitCdnUrl(string appId) =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900_2x.jpg";

    /// <summary>True for official Steam portrait CDN paths (not heroes / wide capsules).</summary>
    public static bool IsOfficialSteamPortraitCdn(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!url.Contains("steamstatic.com", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase))
            return false;
        if (url.Contains("library_hero", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("header.jpg", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("capsule_231", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("capsule_184", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("capsule_616", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("capsule_sm", StringComparison.OrdinalIgnoreCase))
            return false;
        return url.Contains("library_600x900", StringComparison.OrdinalIgnoreCase)
               || url.Contains("library_capsule", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowlistedCdnCover(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsOfficialSteamPortraitCdn(url) || IsOfficialEpicPortraitCdn(url)) return true;
        return url.Contains("ddragon.leagueoflegends.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("images.gog-statics.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("gog-statics.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Best available art for a game: official native cache → validated local folder art.
    /// </summary>
    public static string? ResolvePreferredUrl(GameEntry g)
    {
        // Gather every art file this game could use, then pick the best shape.
        // Ranking beats short-circuiting here: preferring portrait art but
        // *removing* a game's only cover when none exists just leaves a blank
        // tile, which is worse than the wide art it replaced.
        var candidates = new List<string>();

        // Steam cache (native app id, or mapped Steam id for Epic/etc. covers only).
        var appId = SteamAppId(g) ?? MappedSteamAppId(g);
        if (appId is not null)
        {
            candidates.Add(Path.Combine(CacheRoot, appId + "_2x.jpg"));
            candidates.Add(Path.Combine(CacheRoot, appId + ".jpg"));
        }

        // Riot ships no public cover endpoint; its art arrives from the Epic
        // catalog warm and is cached under the product id.
        if (g.Store == StoreKind.Riot)
        {
            var productId = RiotProductId(g);
            if (!string.IsNullOrWhiteSpace(productId))
            {
                var safe = SanitizeId(productId);
                foreach (var ext in new[] { ".jpg", ".png", ".jpeg", ".webp" })
                    candidates.Add(Path.Combine(CacheRoot, "riot_" + safe + ext));
                candidates.Add(Path.Combine(CacheRoot, "riot_" + safe + "_card.png"));
            }
        }

        // Per-id cache slug — also where portrait art fetched by title lands,
        // so a Steam title with only hero art can still gain a real poster.
        var slug = SanitizeId(g.Id);
        foreach (var ext in new[] { ".jpg", ".png", ".jpeg", ".webp" })
            candidates.Add(Path.Combine(CacheRoot, slug + ext));

        // GOG product art from the official GOG cache.
        if (g.Store == StoreKind.Gog)
        {
            var gogId = GogProductId(g);
            if (gogId is not null)
                candidates.Add(Path.Combine(CacheRoot, "gog_" + gogId + ".jpg"));
        }

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
                         "cover.jpg", "cover.png", "header.jpg", "library.jpg", "poster.png",
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
                    Directory.CreateDirectory(CacheRoot);
                    var safe = "local_" + Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(hit.ToLowerInvariant())))[..16]
                        + (hit.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ? ".png" : Path.GetExtension(hit));
                    var dest = Path.Combine(CacheRoot, safe);
                    if (!File.Exists(dest))
                    {
                        if (hit.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                            continue; // skip raw ico copy — warm path may replace later
                        File.Copy(hit, dest, overwrite: true);
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
        EnsureTitleMapLoaded();
        if (TitleSteamMap.TryGetValue(g.Id, out var byId) && IsUsableAppId(byId))
            return byId;
        var key = NormalizeTitleKey(g.Title);
        if (TitleSteamMap.TryGetValue(key, out var byTitle) && IsUsableAppId(byTitle))
            return byTitle;
        if (SeedTitleSteamIds.TryGetValue(key, out var seed) && IsUsableAppId(seed))
            return seed;
        // Prefix seed match only: "grand theft auto v epic" → "grand theft auto v"
        // Avoid short substring traps (e.g. "control" inside unrelated titles).
        string? partial = null;
        var partialLen = 0;
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
        return partial;
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
                // Negatives: "0:<unix>" active for 7 days; legacy "0" and expired entries are skipped (retry).
                if (p.Name.StartsWith('!') && !IsActiveNegativeCache(v)) continue;
                TitleSteamMap[p.Name] = v;
            }
        }
        catch { /* ignore */ }

        // Seed popular multi-store titles into the live map (covers only).
        foreach (var (k, v) in SeedTitleSteamIds)
        {
            if (IsUsableAppId(v))
                TitleSteamMap.TryAdd(k, v);
        }
    }

    private static string NegativeSteamMapValue() =>
        "0:" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>True when a negative map value should still block Steam title retries.</summary>
    private static bool IsActiveNegativeCache(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        // Legacy bare "0" — do not load (allow retry).
        if (value == "0") return false;
        if (!value.StartsWith("0:", StringComparison.Ordinal)) return false;
        if (!long.TryParse(value.AsSpan(2), out var unix)) return false;
        try
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix);
            return age < TimeSpan.FromDays(7);
        }
        catch
        {
            return false;
        }
    }

    private static void PersistTitleMap()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TitleMapPath)!);
            var obj = new Dictionary<string, string>(TitleSteamMap, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(TitleMapPath, JsonSerializer.Serialize(obj));
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// True when <paramref name="url"/> is allowed by the shipped WebView CSP img-src
    /// (<c>'self' data: https://covers.exo-launcher.local</c> in ui/index.html).
    /// PreferLocalArt must only emit URLs that pass this check.
    /// </summary>
    public static bool IsUiLoadableCoverUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsOfficialSteamPortraitCdn(url)) return true;
        if (url.StartsWith(VirtualHostOrigin + "/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = url[(VirtualHostOrigin.Length + 1)..];
            return rest.Length > 0
                   && !rest.Contains("..", StringComparison.Ordinal)
                   && !rest.Contains('/')
                   && !rest.Contains('\\');
        }
        return false;
    }

    /// <summary>Official Epic portrait CDN hosts used by catalog keyImages.</summary>
    public static bool IsOfficialEpicPortraitCdn(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.Host.Equals("cdn1.epicgames.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("cdn2.unrealengine.com", StringComparison.OrdinalIgnoreCase);
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
    /// Tiles are 2:3. Anything wider than this is hero art, and either cropping
    /// or letterboxing it looks stretched, so it is not used as a cover at all.
    /// </summary>
    public const double MaxCoverAspect = 1.15;

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
    /// Best portrait poster only. Landscape heroes are never returned — tiles
    /// show a monogram until a real 2:3 (or taller) cover is cached.
    /// </summary>
    public static string? PickBestArt(IEnumerable<string> paths)
    {
        var usable = paths.Where(IsValidImageFile).Where(IsPortraitCover).ToList();
        if (usable.Count == 0) return null;
        return usable
            .OrderByDescending(CoverRank)
            .First();
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
        return shape + h + sharp;
    }

    /// <summary>True when the file is a 2:3 (or taller) poster. Unknown size = no.</summary>
    public static bool IsPortraitCover(string path)
    {
        var size = ReadImageSize(path);
        if (size is null) return false;
        return size.Value.Width / (double)size.Value.Height <= MaxCoverAspect;
    }

    /// <summary>Drop cached landscape files so they cannot win over a monogram.</summary>
    public static void DiscardIfLandscape(string path)
    {
        try
        {
            if (!IsValidImageFile(path)) return;
            if (IsPortraitCover(path)) return;
            File.Delete(path);
            AppLog.Debug($"Cover: discarded landscape {Path.GetFileName(path)}.");
        }
        catch { /* */ }
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
            if (header[0] == (byte)'<') return false;
            var isJpeg = header[0] == 0xFF && header[1] == 0xD8;
            var isPng = n >= 4 && header[0] == 0x89 && header[1] == 0x50;
            var isWebp = n >= 12 && header[0] == (byte)'R' && header[8] == (byte)'W';
            return isJpeg || isPng || isWebp;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when URL is unknown CDN/http (strip from UI).</summary>
    public static bool IsUnreliableCoverUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Contains(VirtualHost, StringComparison.OrdinalIgnoreCase)) return false;
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
        if (string.IsNullOrWhiteSpace(url)) return null;
        // …/steam/apps/123456/library_600x900…
        const string marker = "/apps/";
        var i = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var start = i + marker.Length;
        var end = start;
        while (end < url.Length && char.IsDigit(url[end])) end++;
        if (end <= start) return null;
        return url[start..end];
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

    public static Task WarmCacheAsync(
        IEnumerable<GameEntry> games,
        Action? onBatchDone = null,
        bool requested = false,
        bool deferForFirstPaint = false)
    {
        var list = games
            .Where(g => !string.Equals(g.Id, "local:add", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (list.Count == 0) return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                if (deferForFirstPaint && !requested)
                    await Task.Delay(FirstPaintCoverWarmDelay).ConfigureAwait(false);

                Directory.CreateDirectory(CacheRoot);

                AppLog.Info($"Cover warm started for {list.Count} titles. requested={requested.ToString().ToLowerInvariant()} deferred={deferForFirstPaint.ToString().ToLowerInvariant()}");
                var any = 0;
                var sinceNotify = 0;
                // Search wants every poster ASAP; library can batch a little.
                var notifyEvery = requested ? 1 : 2;
                var gate = new SemaphoreSlim(requested ? RequestedWarmConcurrency : BackgroundWarmConcurrency);
                var tasks = new List<Task>();

                void NotifyMaybe(bool downloaded)
                {
                    if (!downloaded) return;
                    Interlocked.Exchange(ref any, 1);
                    if (Interlocked.Increment(ref sinceNotify) >= notifyEvery)
                    {
                        Interlocked.Exchange(ref sinceNotify, 0);
                        try { onBatchDone?.Invoke(); } catch { /* */ }
                    }
                }

                foreach (var g in list)
                {
                    // Do not cancel in-flight library warm when search starts —
                    // skip titles already warming or already holding a portrait.
                    if (!WarmInFlight.TryAdd(g.Id, 0)) continue;

                    tasks.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            var steamId = SteamAppId(g) ?? MappedSteamAppId(g);
                            if (!requested && steamId is not null && HasPortraitArt(steamId))
                                return;
                            if (!requested && ResolvePreferredUrl(g) is not null && steamId is null)
                            {
                                // Already have local portrait (Epic/Riot slug) — skip network.
                                return;
                            }

                            var downloaded = await WarmOneAsync(CoverHttp, g, requested).ConfigureAwait(false);
                            NotifyMaybe(downloaded);
                        }
                        finally
                        {
                            WarmInFlight.TryRemove(g.Id, out _);
                            gate.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                PersistTitleMap();
                if (any != 0 || sinceNotify > 0)
                    try { onBatchDone?.Invoke(); } catch { /* */ }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Cover warm failed: " + ex.Message);
            }
        });
    }

    private static HttpClient CreateCoverHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ExoLauncher/1.0");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/*,application/json,*/*");
        return http;
    }

    private static async Task<bool> WarmOneAsync(HttpClient http, GameEntry g, bool requested)
    {
        var downloaded = false;

        // Steam native app id
        var appId = SteamAppId(g);
        if (appId is not null)
        {
            var ok = await DownloadSteamPosterAsync(http, appId, g.Id).ConfigureAwait(false);
            if (!ok) DeadSteamCdn[appId] = 0;
            else DeadSteamCdn.TryRemove(appId, out _);
            // Steam had no portrait poster (only 3:1 hero art) — ask Epic's
            // catalog for the publisher's real box art instead.
            if (!ok || !HasPortraitArt(appId))
                ok |= await DownloadEpicPortraitAsync(http, g, requested).ConfigureAwait(false);
            return ok;
        }

        // Mapped Steam CDN art for Epic / Local / multi-store titles (covers only).
        var mapped = MappedSteamAppId(g);
        if (mapped is not null)
        {
            var ok = await DownloadSteamPosterAsync(http, mapped, g.Id).ConfigureAwait(false);
            if (ok)
            {
                TitleSteamMap[g.Id] = mapped;
                DeadSteamCdn.TryRemove(mapped, out _);
                return true;
            }
            DeadSteamCdn[mapped] = 0;
        }

        // Riot: Epic portrait first (fast when catcache hits), then theme card.
        if (g.Store == StoreKind.Riot)
        {
            var epic = await DownloadEpicPortraitAsync(http, g, requested).ConfigureAwait(false);
            if (epic) return true;
            return await DownloadRiotThemeArtAsync(http, g).ConfigureAwait(false);
        }

        // GOG product art
        var gogId = GogProductId(g);
        if (gogId is not null)
        {
            var dest = Path.Combine(CacheRoot, "gog_" + gogId + ".jpg");
            if (IsValidImageFile(dest) && IsPortraitCover(dest))
                downloaded = true;
            else
            {
                DiscardIfLandscape(dest);
                // Prefer tall-ish assets; landscape tiles are discarded after download.
                var urls = new[]
                {
                    $"https://images.gog-statics.com/{gogId}_product_tile_256_2x.jpg",
                    $"https://api.gog.com/products/{gogId}",
                };
                if (await TryDownloadAnyAsync(http, urls.Take(1), dest).ConfigureAwait(false))
                {
                    if (IsPortraitCover(dest)) downloaded = true;
                    else DiscardIfLandscape(dest);
                }
                if (!downloaded)
                {
                    try
                    {
                        using var resp = await http.GetAsync(urls[1]).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("images", out var images))
                            {
                                foreach (var key in new[] { "logo2x", "logo", "icon", "sidebarIcon2x", "sidebarIcon" })
                                {
                                    if (!images.TryGetProperty(key, out var el)) continue;
                                    var path = el.GetString();
                                    if (string.IsNullOrWhiteSpace(path)) continue;
                                    if (path.StartsWith("//", StringComparison.Ordinal))
                                        path = "https:" + path;
                                    if (await TryDownloadAnyAsync(http, new[] { path }, dest).ConfigureAwait(false))
                                    {
                                        if (IsPortraitCover(dest)) { downloaded = true; break; }
                                        DiscardIfLandscape(dest);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Debug("GOG cover API fail: " + ex.Message);
                    }
                }
            }
        }

        // Local folder files
        if (!string.IsNullOrWhiteSpace(g.Path) && Directory.Exists(g.Path))
            _ = WithCover(g);

        if (!downloaded)
            downloaded = await DownloadEpicPortraitAsync(http, g, requested).ConfigureAwait(false);

        return downloaded;
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
        // Installed titles are on the shelf; a search hit was asked for by name.
        return requested || g.Installed || g.Store == StoreKind.Riot;
    }

    /// <summary>True when a real portrait poster is already cached for this app id.</summary>
    private static bool HasPortraitArt(string appId)
    {
        foreach (var name in new[] { appId + "_2x.jpg", appId + ".jpg" })
        {
            var path = Path.Combine(CacheRoot, name);
            if (IsValidImageFile(path) && IsPortraitCover(path)) return true;
        }
        return false;
    }

    /// <summary>
    /// Publisher box art from Epic's public catalog, matched on exact title.
    /// This is what gives Riot titles and Steam-hero-only titles a real poster.
    /// </summary>
    private static async Task<bool> DownloadEpicPortraitAsync(HttpClient http, GameEntry g, bool requested)
    {
        // Only titles the user can actually see. An Epic account carries every
        // Unreal Marketplace entitlement it has ever claimed — asking about all
        // 143 of them in one burst is what got this machine refused by Epic.
        if (!ShouldSeekOnlineArt(g, requested)) return false;
        try
        {
            var dest = Path.Combine(CacheRoot, EpicArtFileName(g));
            if (IsValidImageFile(dest) && IsPortraitCover(dest) && IsSharpEnough(dest))
                return true;

            var url = IsOfficialEpicPortraitCdn(g.CoverUrl)
                ? g.CoverUrl
                : await EpicCatalogArt.FindPortraitUrlAsync(http, g.Title).ConfigureAwait(false);
            if (url is null)
            {
                AppLog.Debug($"No Epic portrait art for '{g.Title}'.");
                return false;
            }
            if (!await TryDownloadAnyAsync(http, new[] { url }, dest).ConfigureAwait(false))
            {
                AppLog.Warn($"Epic portrait download failed for '{g.Title}'.");
                return false;
            }
            if (!IsPortraitCover(dest))
            {
                try { File.Delete(dest); } catch { /* */ }
                AppLog.Debug($"Epic art for '{g.Title}' was not portrait; discarded.");
                return false;
            }
            AppLog.Info($"Cover: Epic portrait art for '{g.Title}'.");
            return true;
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
    private static async Task<bool> DownloadRiotThemeArtAsync(HttpClient http, GameEntry g)
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
                .GetThemeManifestUrlAsync(product, Adapters.Cli.RiotCli.DefaultPatchline, CancellationToken.None)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(manifestUrl)) return false;

            using var resp = await http.GetAsync(manifestUrl).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var relative = ReadThemeImage(doc.RootElement);
            if (string.IsNullOrWhiteSpace(relative)) return false;

            var absolute = new Uri(new Uri(manifestUrl), relative).AbsoluteUri;
            if (!await TryDownloadAnyAsync(http, new[] { absolute }, dest).ConfigureAwait(false))
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

    private static async Task<bool> DownloadSteamPosterAsync(HttpClient http, string appId, string gameId)
    {
        var dest2x = Path.Combine(CacheRoot, appId + "_2x.jpg");
        var dest = Path.Combine(CacheRoot, appId + ".jpg");
        PurgeTinyCover(dest2x);
        PurgeTinyCover(dest);
        PurgeTinyCover(Path.Combine(CacheRoot, "steam_" + appId + ".jpg"));

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
        // Race one classic 2x URL in parallel so older apps stay fast.
        var classicFirst = string.Format(SteamPosterTemplates[0], appId);
        var classicTask = IsHighResPoster(dest2x)
            ? Task.FromResult(true)
            : TryDownloadAnyAsync(http, new[] { classicFirst }, dest2x);
        var capsuleTask = DownloadSteamLibraryCapsuleAsync(http, appId, dest2x, dest);
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
                        dest2x)
                    .ConfigureAwait(false) || ok;
            }

            if (!IsValidImageFile(dest))
            {
                if (IsValidImageFile(dest2x))
                {
                    try { File.Copy(dest2x, dest, overwrite: true); ok = true; } catch { /* */ }
                }
                if (!IsValidImageFile(dest))
                {
                    ok = await TryDownloadAnyAsync(
                            http,
                            SteamPosterTemplates
                                .Where(t => !t.Contains("_2x", StringComparison.Ordinal))
                                .Select(t => string.Format(t, appId)),
                            dest)
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
        return false;
    }

    private static void SyncSlugPortrait(string appId, string gameId, string dest2x, string dest)
    {
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
        HttpClient http, string appId, string dest2x, string dest)
    {
        try
        {
            var urls = await ResolveSteamLibraryCapsuleUrlsAsync(http, appId).ConfigureAwait(false);
            if (urls.Count == 0) return false;

            // URLs are ordered 2x then 1x across CDNs. Do not filter on the
            // filename — some apps name the asset library_600x900_2x.jpg while
            // the GetItems field is still library_capsule_2x.
            var got2x = IsHighResPoster(dest2x) && IsPortraitCover(dest2x);
            if (!got2x)
            {
                got2x = await TryDownloadAnyAsync(http, urls, dest2x).ConfigureAwait(false);
                if (got2x && !IsPortraitCover(dest2x))
                {
                    DiscardIfLandscape(dest2x);
                    got2x = false;
                }
            }

            var got1x = IsValidImageFile(dest) && IsPortraitCover(dest);
            if (!got1x)
            {
                if (got2x)
                {
                    try { File.Copy(dest2x, dest, overwrite: true); got1x = true; } catch { /* */ }
                }
                if (!got1x)
                {
                    got1x = await TryDownloadAnyAsync(http, urls, dest).ConfigureAwait(false);
                    if (got1x && !IsPortraitCover(dest))
                    {
                        DiscardIfLandscape(dest);
                        got1x = false;
                    }
                }
            }

            if (got2x || got1x)
            {
                AppLog.Info($"Cover: Steam library_capsule portrait for app {appId}.");
                return true;
            }
            return false;
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
        HttpClient http, string appId)
    {
        if (!IsUsableAppId(appId)) return Array.Empty<string>();
        var input = $"{{\"ids\":[{{\"appid\":{appId}}}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\",\"steam_realm\":1}},\"data_request\":{{\"include_assets\":true}}}}";
        var url =
            "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json="
            + Uri.EscapeDataString(input);
        using var resp = await http.GetAsync(url).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
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
            if (built.Count > 0) return built;
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Steam client already caches official library_capsule art locally — use it
    /// when the network path is blocked and classic CDN 404s.
    /// </summary>
    private static bool TryCopyLocalSteamLibraryCapsule(string appId, string dest2x, string dest)
    {
        try
        {
            var steamRoot = TryFindSteamInstallPath();
            if (steamRoot is null) return false;
            var root = Path.Combine(steamRoot, "appcache", "librarycache", appId);
            if (!Directory.Exists(root)) return false;

            string? src2x = null;
            string? src1x = null;
            foreach (var path in Directory.EnumerateFiles(root, "library_capsule*.jpg", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (name.Equals("library_capsule_2x.jpg", StringComparison.OrdinalIgnoreCase))
                    src2x = path;
                else if (name.Equals("library_capsule.jpg", StringComparison.OrdinalIgnoreCase))
                    src1x = path;
            }

            var copied = false;
            if (src2x is not null && File.Exists(src2x) && new FileInfo(src2x).Length >= MinCoverBytes)
            {
                Directory.CreateDirectory(CacheRoot);
                File.Copy(src2x, dest2x, overwrite: true);
                if (!IsPortraitCover(dest2x)) { DiscardIfLandscape(dest2x); }
                else copied = true;
            }
            if (src1x is not null && File.Exists(src1x) && new FileInfo(src1x).Length >= MinCoverBytes)
            {
                Directory.CreateDirectory(CacheRoot);
                File.Copy(src1x, dest, overwrite: true);
                if (!IsPortraitCover(dest)) { DiscardIfLandscape(dest); }
                else copied = true;
            }
            if (copied && !IsValidImageFile(dest) && IsValidImageFile(dest2x))
            {
                try { File.Copy(dest2x, dest, overwrite: true); } catch { /* */ }
            }
            if (copied)
                AppLog.Info($"Cover: local Steam library_capsule for app {appId}.");
            return copied && HasPortraitArt(appId);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Local Steam library_capsule fail for {appId}: {ex.Message}");
            return false;
        }
    }

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
    private static async Task<string?> ResolveSteamAppIdByTitleAsync(HttpClient http, GameEntry g)
    {
        EnsureTitleMapLoaded();
        if (TitleSteamMap.TryGetValue(g.Id, out var cached) && IsUsableAppId(cached))
            return cached;
        var key = NormalizeTitleKey(g.Title);
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (TitleSteamMap.TryGetValue(key, out var byTitle) && IsUsableAppId(byTitle))
            return byTitle;
        if (SeedTitleSteamIds.TryGetValue(key, out var seed) && IsUsableAppId(seed))
        {
            TitleSteamMap[g.Id] = seed;
            TitleSteamMap[key] = seed;
            return seed;
        }
        if (TitleSteamMap.TryGetValue("!" + key, out _))
            return null;

        try
        {
            var searchTitle = CleanSearchTitle(g.Title);
            if (string.IsNullOrWhiteSpace(searchTitle)) return null;

            string? best = null;
            var bestScore = -1;

            // 1) steamcommunity SearchApps — more reliable than storesearch for many titles
            await CollectSearchAppsAsync(http, searchTitle, key, (id, score) =>
            {
                if (score > bestScore) { bestScore = score; best = id; }
            }).ConfigureAwait(false);

            // 2) store API as secondary
            if (bestScore < 90)
            {
                await CollectStoreSearchAsync(http, searchTitle, key, (id, score) =>
                {
                    if (score > bestScore) { bestScore = score; best = id; }
                }).ConfigureAwait(false);
            }

            // 3) Shorter query (first 2–3 words) when under accept threshold — require strong score.
            if (bestScore < 90)
            {
                var words = searchTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 2)
                {
                    var shortQ = string.Join(' ', words.Take(Math.Min(3, words.Length)));
                    await CollectSearchAppsAsync(http, shortQ, key, (id, score) =>
                    {
                        if (score >= 95 && score > bestScore) { bestScore = score; best = id; }
                    }).ConfigureAwait(false);
                }
            }

            // Require a strong match before committing a Steam map (avoid wrong / soft covers).
            if (best is null || bestScore < 90)
            {
                TitleSteamMap["!" + key] = NegativeSteamMapValue();
                return null;
            }

            // Verify poster actually exists on CDN before committing the map
            if (!await SteamPosterExistsAsync(http, best).ConfigureAwait(false))
            {
                TitleSteamMap["!" + key] = NegativeSteamMapValue();
                return null;
            }

            TitleSteamMap[g.Id] = best;
            TitleSteamMap[key] = best;
            return best;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam title search fail: " + ex.Message);
            return null;
        }
    }

    private static string CleanSearchTitle(string title)
    {
        var searchTitle = title
            .Replace("™", "", StringComparison.Ordinal)
            .Replace("®", "", StringComparison.Ordinal)
            .Replace("©", "", StringComparison.Ordinal);
        foreach (var junk in new[]
                 {
                     " - Deluxe Edition", " Deluxe Edition", " - Ultimate Edition", " Ultimate Edition",
                     " - Gold Edition", " Gold Edition", " Game of the Year", " GOTY",
                     " - Standard Edition", " Standard Edition", " (Epic)", " (Steam)", " (GOG)",
                     " - Director's Cut", " Director's Cut", " Complete Edition", " Definitive Edition",
                     " Remastered", " Enhanced Edition", " Anniversary Edition",
                 })
        {
            var idx = searchTitle.IndexOf(junk, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) searchTitle = searchTitle[..idx].Trim();
        }
        return searchTitle.Trim();
    }

    private delegate void ScoreSink(string appId, int score);

    private static async Task CollectSearchAppsAsync(HttpClient http, string term, string titleNorm, ScoreSink sink)
    {
        try
        {
            var q = Uri.EscapeDataString(term.Trim());
            var url = $"https://steamcommunity.com/actions/SearchApps/{q}";
            using var resp = await http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
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
        catch (Exception ex)
        {
            AppLog.Debug("SearchApps fail: " + ex.Message);
        }
    }

    private static async Task CollectStoreSearchAsync(HttpClient http, string term, string titleNorm, ScoreSink sink)
    {
        try
        {
            var q = Uri.EscapeDataString(term.Trim());
            var url = $"https://store.steampowered.com/api/storesearch/?term={q}&l=english&cc=US";
            using var resp = await http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
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

    private static async Task<bool> SteamPosterExistsAsync(HttpClient http, string appId)
    {
        foreach (var tmpl in SteamPosterTemplates.Take(4))
        {
            try
            {
                var url = string.Format(tmpl, appId);
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await http.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* try next */ }
        }
        // HEAD may be blocked — try a tiny GET of 1x
        try
        {
            var url = string.Format(SteamPosterTemplates[^1], appId);
            using var resp = await http.GetAsync(url).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length >= MinCoverBytes && bytes[0] == 0xFF && bytes[1] == 0xD8)
                    return true;
            }
        }
        catch { /* fall through to library_capsule */ }

        // Newer apps only publish hashed library_capsule portraits.
        try
        {
            var urls = await ResolveSteamLibraryCapsuleUrlsAsync(http, appId).ConfigureAwait(false);
            return urls.Count > 0;
        }
        catch { return false; }
    }

    private static int ScoreTitleMatch(string want, string got)
    {
        if (string.IsNullOrEmpty(want) || string.IsNullOrEmpty(got)) return 0;
        if (want == got) return 100;
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

    private static async Task<bool> TryDownloadAnyAsync(HttpClient http, IEnumerable<string> urls, string dest)
    {
        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                // Epic / Unreal CDN often 403s bare clients; browser-shaped headers match the store.
                if (url.Contains("epicgames.com", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("unrealengine.com", StringComparison.OrdinalIgnoreCase))
                {
                    req.Headers.TryAddWithoutValidation(
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                        + "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
                    req.Headers.TryAddWithoutValidation("Referer", "https://store.epicgames.com/");
                    req.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");
                }
                using var resp = await http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length < MinCoverBytes || bytes.Length > 8 * 1024 * 1024) continue;
                if (bytes[0] == (byte)'<') continue;
                var okJpeg = bytes[0] == 0xFF && bytes[1] == 0xD8;
                var okPng = bytes[0] == 0x89 && bytes[1] == 0x50;
                if (!okJpeg && !okPng) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await File.WriteAllBytesAsync(dest, bytes).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Cover fetch fail {url}: {ex.Message}");
            }
        }
        return false;
    }

    private static string SanitizeId(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private static string? CoverSourceFor(GameEntry g) =>
        g.Store switch
        {
            StoreKind.Steam => "steam",
            StoreKind.Epic => "epic",
            StoreKind.Gog => "gog",
            StoreKind.Riot => "riot",
            StoreKind.Local => "local",
            _ => null,
        };

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
        UpdateAvailable = g.UpdateAvailable,
        CanInstall = g.CanInstall,
        Path = g.Path,
        CoverUrl = coverUrl,
        CoverSource = coverSource,
        PlaytimeMinutes = g.PlaytimeMinutes,
        SizeBytes = g.SizeBytes,
        Status = g.Status,
        Deps = g.Deps,
        LaunchNote = g.LaunchNote,
        LaunchTarget = g.LaunchTarget,
        LastPlayedUtc = g.LastPlayedUtc,
        IsFavorite = g.IsFavorite,
    };
}
