using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Fast search: instant local/owned hits, cached Epic/GOG catalogs, short Steam timeout.
/// Never shells Legendary per keystroke. Ranks exact title matches first.
/// </summary>
public sealed class StoreSearchService
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly TimeSpan OwnedCacheTtl = TimeSpan.FromMinutes(3);

    private readonly object _cacheLock = new();
    private List<StoreSearchHit>? _epicOwnedCache;
    private List<StoreSearchHit>? _gogOwnedCache;
    private DateTimeOffset _epicCacheAt = DateTimeOffset.MinValue;
    private DateTimeOffset _gogCacheAt = DateTimeOffset.MinValue;
    private Task? _epicWarm;
    private Task? _gogWarm;
    private readonly Func<CancellationToken, Task<List<StoreSearchHit>>> _epicOwnedLoader;
    private readonly Func<string, IReadOnlyList<GameEntry>, CancellationToken, Task<IReadOnlyList<StoreSearchHit>>> _steamSearch;

    public StoreSearchService()
        : this(LoadLegendaryOwnedAsync, SearchSteamIfClientPresent)
    {
    }

    /// <summary>
    /// Public Steam catalog search needs the official client. Without it, Exo
    /// would paint a store shelf on a PC that cannot command Steam.
    /// </summary>
    internal static bool CanSearchPublicSteamCatalog(string? steamExe) =>
        !string.IsNullOrWhiteSpace(steamExe);

    /// <summary>
    /// Live search is library + account. Unowned Steam/Epic shelf hits stay off
    /// the wire. Minecraft and Roblox are official clients Exo can hand off to
    /// even when they are not in the local library yet.
    /// </summary>
    internal static bool IsLiveSearchHit(StoreSearchHit hit) =>
        hit.Owned || hit.Installed || IsOfficialClientCatalogInstall(hit);

    internal static bool IsOfficialClientCatalogInstall(StoreSearchHit hit) =>
        IsOfficialClientCatalogInstall(hit.Store, hit.Id, hit.CanInstall);

    internal static bool IsOfficialClientCatalogInstall(StoreKind store, string? id, bool canInstall) =>
        canInstall &&
        store is StoreKind.Minecraft or StoreKind.Roblox &&
        id is "minecraft:java" or "minecraft:bedrock" or "roblox:player";

    internal static bool IsOfficialClientCatalogInstall(GameEntry game) =>
        IsOfficialClientCatalogInstall(game.Store, game.Id, game.CanInstall) &&
        !game.Installed &&
        game.EntitlementState is not (EntitlementState.NotOwned or EntitlementState.Unverified);

    /// <summary>
    /// Minecraft and Roblox are not Steam/Epic catalog rows. A typed search
    /// still has to produce an installable card so Get/Download is possible.
    /// </summary>
    internal static List<StoreSearchHit> WellKnownCatalogHits(string query)
    {
        var hits = new List<StoreSearchHit>();
        if (TitleMatchesQuery("Minecraft", query) || TitleMatchesQuery("Minecraft Bedrock", query))
        {
            hits.Add(new StoreSearchHit
            {
                Id = "minecraft:java",
                Title = "Minecraft",
                Store = StoreKind.Minecraft,
                LaunchTarget = "minecraft:java",
                CanInstall = true,
                Source = "minecraft",
            });
            hits.Add(new StoreSearchHit
            {
                Id = "minecraft:bedrock",
                Title = "Minecraft Bedrock",
                Store = StoreKind.Minecraft,
                LaunchTarget = "minecraft:bedrock",
                CanInstall = true,
                Source = "minecraft",
            });
        }
        if (TitleMatchesQuery("Roblox", query))
        {
            hits.Add(new StoreSearchHit
            {
                Id = "roblox:player",
                Title = "Roblox",
                Store = StoreKind.Roblox,
                LaunchTarget = "9PMF91N3LZ3M",
                CanInstall = true,
                Source = "roblox",
            });
        }
        return hits;
    }

    internal static GameEntry? TrySynthesizeOfficialClientInstall(string gameId, string? title)
    {
        var id = (gameId ?? "").Trim();
        if (id.Equals("minecraft:java", StringComparison.OrdinalIgnoreCase))
        {
            return new GameEntry
            {
                Id = "minecraft:java",
                Title = string.IsNullOrWhiteSpace(title) || title == id ? "Minecraft" : title.Trim(),
                Store = StoreKind.Minecraft,
                Installed = false,
                Owned = false,
                CanInstall = true,
                LaunchTarget = "minecraft:java",
                Status = "Catalog",
                LaunchNote = "Opens the official Minecraft download.",
            };
        }
        if (id.Equals("minecraft:bedrock", StringComparison.OrdinalIgnoreCase))
        {
            return new GameEntry
            {
                Id = "minecraft:bedrock",
                Title = string.IsNullOrWhiteSpace(title) || title == id ? "Minecraft Bedrock" : title.Trim(),
                Store = StoreKind.Minecraft,
                Installed = false,
                Owned = false,
                CanInstall = true,
                LaunchTarget = "minecraft:bedrock",
                Status = "Catalog",
                LaunchNote = "Opens Minecraft Bedrock in the Microsoft Store.",
            };
        }
        if (id.Equals("roblox:player", StringComparison.OrdinalIgnoreCase))
        {
            return new GameEntry
            {
                Id = "roblox:player",
                Title = string.IsNullOrWhiteSpace(title) || title == id ? "Roblox" : title.Trim(),
                Store = StoreKind.Roblox,
                Installed = false,
                Owned = false,
                CanInstall = true,
                LaunchTarget = "9PMF91N3LZ3M",
                Status = "Catalog",
                LaunchNote = "Opens Roblox in the Microsoft Store.",
            };
        }
        return null;
    }

    private static Task<IReadOnlyList<StoreSearchHit>> SearchSteamIfClientPresent(
        string q, IReadOnlyList<GameEntry> ownedLibrary, CancellationToken ct)
    {
        if (!CanSearchPublicSteamCatalog(SteamAdapter.TryResolveSteamExePublic()))
            return Task.FromResult<IReadOnlyList<StoreSearchHit>>(Array.Empty<StoreSearchHit>());
        return SearchSteamAsync(q, ownedLibrary, ct);
    }

    internal StoreSearchService(
        Func<CancellationToken, Task<List<StoreSearchHit>>> epicOwnedLoader,
        Func<string, IReadOnlyList<GameEntry>, CancellationToken, Task<IReadOnlyList<StoreSearchHit>>> steamSearch)
    {
        _epicOwnedLoader = epicOwnedLoader ?? throw new ArgumentNullException(nameof(epicOwnedLoader));
        _steamSearch = steamSearch ?? throw new ArgumentNullException(nameof(steamSearch));
    }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ExoLauncher/1.0");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json,*/*");
        return c;
    }

    public void InvalidateOwnedCaches()
    {
        lock (_cacheLock)
        {
            _epicOwnedCache = null;
            _gogOwnedCache = null;
            _epicCacheAt = DateTimeOffset.MinValue;
            _gogCacheAt = DateTimeOffset.MinValue;
        }
    }

    public async Task<IReadOnlyList<StoreSearchHit>> SearchAsync(
        string query,
        IReadOnlyList<GameEntry> ownedLibrary,
        CancellationToken ct = default,
        Action<IReadOnlyList<StoreSearchHit>>? onPartialResults = null)
    {
        var q = (query ?? "").Trim();
        if (q.Length < 2) return Array.Empty<StoreSearchHit>();

        var local = SearchOwnedLibrary(q, ownedLibrary);
        local.AddRange(WellKnownCatalogHits(q));
        onPartialResults?.Invoke(RankAndDedup(local, q, 40));

        var epicWarm = EnsureEpicCacheWarm();
        var gogWarm = EnsureGogCacheWarm(ownedLibrary);
        var ownedWarm = Task.WhenAll(epicWarm, gogWarm);

        var (epic, gog) = FilterOwnedCaches(q);
        var early = RankAndDedup(local.Concat(epic).Concat(gog), q, 40);
        if (early.Count > 0)
            onPartialResults?.Invoke(early);

        if (onPartialResults is not null && !ownedWarm.IsCompleted)
            _ = PublishOwnedWhenWarmAsync(ownedWarm, q, local, onPartialResults, ct);

        IReadOnlyList<StoreSearchHit> steam = Array.Empty<StoreSearchHit>();
        try
        {
            steam = await _steamSearch(q, ownedLibrary, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Debug("Steam search fail: " + ex.Message);
        }

        // Steam catalog lookup is independent of the slower owned-library
        // providers. Paint any useful Steam result immediately while those
        // providers finish, so a cold Legendary/GOG check never leaves a
        // correct game search looking empty for its whole timeout budget.
        var beforeOwnedSettles = RankAndDedup(local.Concat(epic).Concat(gog).Concat(steam), q, 40);
        if (beforeOwnedSettles.Count > 0)
            onPartialResults?.Invoke(beforeOwnedSettles);

        // An empty final response must mean every enabled owned provider has
        // settled. The background partial remains useful for a fast local hit,
        // but returning before Legendary/GOG finishes made the UI show a false
        // "No matches" state and then replace it later.
        await ownedWarm.WaitAsync(ct).ConfigureAwait(false);

        (epic, gog) = FilterOwnedCaches(q);

        return RankAndDedup(local.Concat(epic).Concat(gog).Concat(steam), q, 40);
    }

    private async Task PublishOwnedWhenWarmAsync(
        Task ownedWarm,
        string query,
        IReadOnlyList<StoreSearchHit> local,
        Action<IReadOnlyList<StoreSearchHit>> publish,
        CancellationToken ct)
    {
        try
        {
            await ownedWarm.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var (epic, gog) = FilterOwnedCaches(query);
            var owned = RankAndDedup(local.Concat(epic).Concat(gog), query, 40);
            if (owned.Count > 0) publish(owned);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer query owns the UI now.
        }
        catch (Exception ex)
        {
            AppLog.Debug("Owned search partial publish failed: " + ex.Message);
        }
    }

    private static List<StoreSearchHit> FilterCached(List<StoreSearchHit>? cache, string q)
    {
        if (cache is null || cache.Count == 0) return new List<StoreSearchHit>();
        return cache
            .Where(h => h.Store != StoreKind.Epic || IsSearchableEpicTitle(h.Title))
            .Select(h => new { Hit = h, Score = CachedMatchScore(h, q) })
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Hit.Title, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(x => x.Hit)
            .ToList();
    }

    private static int CachedMatchScore(StoreSearchHit hit, string query)
    {
        var titleScore = TitleMatchScore(hit.Title, query);
        if (titleScore >= 0) return titleScore;
        return TitleMatchScore(hit.LaunchTarget, query);
    }

    private (List<StoreSearchHit> Epic, List<StoreSearchHit> Gog) FilterOwnedCaches(string q)
    {
        lock (_cacheLock)
            return (FilterCached(_epicOwnedCache, q), FilterCached(_gogOwnedCache, q));
    }

    private Task EnsureEpicCacheWarm()
    {
        lock (_cacheLock)
        {
            if (_epicOwnedCache is not null && DateTimeOffset.UtcNow - _epicCacheAt < OwnedCacheTtl)
                return Task.CompletedTask;
            if (_epicWarm is { IsCompleted: false }) return _epicWarm;
            _epicWarm = Task.Run(async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var list = await _epicOwnedLoader(timeout.Token).ConfigureAwait(false);
                    lock (_cacheLock)
                    {
                        _epicOwnedCache = list;
                        _epicCacheAt = DateTimeOffset.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug("Epic owned cache warm fail: " + ex.Message);
                }
            }, CancellationToken.None);
            return _epicWarm;
        }
    }

    private Task EnsureGogCacheWarm(IReadOnlyList<GameEntry> ownedLibrary)
    {
        lock (_cacheLock)
        {
            if (_gogOwnedCache is not null && DateTimeOffset.UtcNow - _gogCacheAt < OwnedCacheTtl)
                return Task.CompletedTask;
            if (_gogWarm is { IsCompleted: false }) return _gogWarm;
            _gogWarm = Task.Run(() =>
            {
                try
                {
                    var list = ownedLibrary
                        .Where(g => g.Store == StoreKind.Gog)
                        .Select(g => new StoreSearchHit
                        {
                            Id = g.Id,
                            Title = g.Title,
                            Store = StoreKind.Gog,
                            LaunchTarget = g.LaunchTarget,
                            CoverUrl = g.CoverUrl,
                            Owned = true,
                            Installed = g.Installed,
                            CanInstall = !g.Installed && g.Owned && g.CanInstall,
                            Source = "gog",
                        })
                        .ToList();
                    lock (_cacheLock)
                    {
                        _gogOwnedCache = list;
                        _gogCacheAt = DateTimeOffset.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug("GOG owned cache warm fail: " + ex.Message);
                }
            }, CancellationToken.None);
            return _gogWarm;
        }
    }

    private static List<StoreSearchHit> SearchOwnedLibrary(string q, IReadOnlyList<GameEntry> ownedLibrary)
    {
        return ownedLibrary
            .Where(IsSearchableLibraryGame)
            .Where(g => TitleMatchesQuery(g.Title, q)
                        || TitleMatchesQuery(g.LaunchTarget, q))
            .Select(g => new StoreSearchHit
            {
                Id = g.Id,
                Title = g.Title,
                Store = g.Store,
                LaunchTarget = g.LaunchTarget,
                CoverUrl = g.CoverUrl ?? SteamCover(g.LaunchTarget),
                Owned = g.Owned,
                Installed = g.Installed,
                CanInstall = !g.Installed && g.Owned && g.CanInstall,
                Source = "library",
            })
            .ToList();
    }

    private static bool IsSearchableLibraryGame(GameEntry game) =>
        !string.Equals(game.Id, LocalAdapter.AddPortableId, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(game.Title, "Add portable game", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSearchableEpicTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || CoverArtService.LooksLikeEngineAsset(title)) return false;
        var normalized = Normalize(title);
        return !normalized.Contains("metahuman", StringComparison.Ordinal) &&
               !normalized.Contains("wait for players", StringComparison.Ordinal) &&
               !normalized.Contains("ai for npc", StringComparison.Ordinal);
    }

    /// <summary>
    /// Legendary categories are authoritative when present. Older responses
    /// omit categories; those unknown rows retain the title safeguard so a
    /// legitimate game is not hidden merely due to missing metadata.
    /// </summary>
    internal static bool IsSearchableEpicRow(LegendaryCli.GameRow row)
    {
        if (!IsSearchableEpicTitle(row.Title)) return false;
        if (row.Categories.Count == 0) return true; // explicit unknown policy
        return row.Categories.Any(category =>
            string.Equals(category, "games", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(category, "game", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<StoreSearchHit>> LoadLegendaryOwnedAsync(CancellationToken ct)
    {
        var list = new List<StoreSearchHit>();
        var legendary = EpicAdapter.ResolveLegendary();
        if (legendary is null) return list;

        var (code, stdout, _) = await CliRunner.RunAsync(
            legendary, LegendaryCli.ListOwnedArgs(), null, null, ct).ConfigureAwait(false);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout)) return list;

        var rows = LegendaryCli.ParseLibraryJson(stdout, forceInstalled: false);
        foreach (var row in rows)
        {
            if (!IsSearchableEpicRow(row)) continue;
            list.Add(new StoreSearchHit
            {
                Id = "epic:" + row.AppName,
                Title = row.Title,
                Store = StoreKind.Epic,
                LaunchTarget = row.AppName,
                CoverUrl = row.CoverUrl,
                CoverSource = row.CoverUrl is null ? null : "epic-catalog",
                Owned = true,
                Installed = row.Installed,
                CanInstall = !row.Installed,
                Source = "epic",
            });
        }
        return list;
    }

    private static async Task<IReadOnlyList<StoreSearchHit>> SearchSteamAsync(
        string q, IReadOnlyList<GameEntry> ownedLibrary, CancellationToken ct)
    {
        // A catalog hit cannot prove an entitlement. Only account-scoped local
        // evidence may turn a public store result into an Install action.
        var directId = TryParseSteamAppId(q);
        if (directId is not null)
            return new[] { BuildSteamCatalogHit(directId, "Steam app " + directId, ownedLibrary) };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(5));
        var token = linked.Token;

        // Prefer SearchApps (better relevance) — run in parallel with store JSON.
        var appsTask = SearchSteamAppsAsync(q, ownedLibrary, token);
        var storeTask = SearchSteamStoreJsonAsync(q, ownedLibrary, token);

        var results = new ConcurrentBag<StoreSearchHit>();
        try
        {
            await Task.WhenAll(
                Absorb(appsTask, results),
                Absorb(storeTask, results)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Soft timeout — return whatever arrived.
        }

        // Steam gets the precise text first. A stray sequel/year suffix is a common
        // human query mistake ("Mortal Shell 2"), so only when that has no results
        // do one narrower retry without that final numeric token. Never fan a typo
        // out into a broad catalog scan.
        var relaxed = GetSafeRelaxedSteamQuery(q);
        if (results.IsEmpty && relaxed is not null && !ct.IsCancellationRequested)
        {
            try
            {
                using var relaxedTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                relaxedTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                var relaxedResults = new ConcurrentBag<StoreSearchHit>();
                await Task.WhenAll(
                    Absorb(SearchSteamAppsAsync(relaxed, ownedLibrary, relaxedTimeout.Token), relaxedResults),
                    Absorb(SearchSteamStoreJsonAsync(relaxed, ownedLibrary, relaxedTimeout.Token), relaxedResults))
                    .ConfigureAwait(false);
                foreach (var h in relaxedResults) results.Add(h);
            }
            catch { /* */ }
        }

        return results.ToList();
    }

    private static async Task Absorb(Task<IReadOnlyList<StoreSearchHit>> task, ConcurrentBag<StoreSearchHit> bag)
    {
        try
        {
            foreach (var h in await task.ConfigureAwait(false))
                bag.Add(h);
        }
        catch (OperationCanceledException) { /* */ }
        catch (Exception ex)
        {
            AppLog.Debug("Steam search part fail: " + ex.Message);
        }
    }

    private static async Task<IReadOnlyList<StoreSearchHit>> SearchSteamAppsAsync(
        string q, IReadOnlyList<GameEntry> ownedLibrary, CancellationToken ct)
    {
        var list = new List<StoreSearchHit>();
        var enc = Uri.EscapeDataString(q);
        var url = $"https://steamcommunity.com/actions/SearchApps/{enc}";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return list;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json) || json.TrimStart().StartsWith('<')) return list;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = item.TryGetProperty("appid", out var idEl)
                ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32().ToString() : idEl.GetString())
                : null;
            var name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            if (!TitleMatchesQuery(name, q)) continue;
            if (SteamAdapter.IsNonGameSteamEntry(id, name, null)) continue;
            list.Add(BuildSteamCatalogHit(id!, name!, ownedLibrary));
            if (list.Count >= 20) break;
        }
        return list;
    }

    private static async Task<IReadOnlyList<StoreSearchHit>> SearchSteamStoreJsonAsync(
        string q, IReadOnlyList<GameEntry> ownedLibrary, CancellationToken ct)
    {
        var list = new List<StoreSearchHit>();
        var enc = Uri.EscapeDataString(q);
        var url = $"https://store.steampowered.com/search/results/?term={enc}&category1=998&json=1";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return list;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return list;
        foreach (var item in items.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var logo = item.TryGetProperty("logo", out var l) ? l.GetString() : null;
            string? id = null;
            if (!string.IsNullOrWhiteSpace(logo))
            {
                var m = System.Text.RegularExpressions.Regex.Match(logo, @"/apps/(\d+)/");
                if (m.Success) id = m.Groups[1].Value;
            }
            if (id is null || string.IsNullOrWhiteSpace(name)) continue;
            // Store JSON pads with unrelated recommendations — require title match.
            if (!TitleMatchesQuery(name, q)) continue;
            if (SteamAdapter.IsNonGameSteamEntry(id, name, null)) continue;
            list.Add(BuildSteamCatalogHit(id, name, ownedLibrary));
            if (list.Count >= 20) break;
        }
        return list;
    }

    private static bool IsSteamAppRef(string id, StoreKind store, string? launchTarget, string appId) =>
        string.Equals(id, "steam:" + appId, StringComparison.OrdinalIgnoreCase) ||
        (store == StoreKind.Steam && string.Equals(launchTarget, appId, StringComparison.Ordinal));

    private static bool SteamSourceIsOwned(GameEntry game, string appId)
    {
        if (IsSteamAppRef(game.Id, game.Store, game.LaunchTarget, appId) && game.Owned)
            return true;
        return game.Variants.Any(variant =>
            IsSteamAppRef(variant.Id, variant.Store, variant.LaunchTarget, appId) &&
            variant.Owned);
    }

    private static bool SteamSourceIsInstalled(GameEntry game, string appId)
    {
        if (IsSteamAppRef(game.Id, game.Store, game.LaunchTarget, appId) && game.Installed)
            return true;
        return game.Variants.Any(variant =>
            IsSteamAppRef(variant.Id, variant.Store, variant.LaunchTarget, appId) && variant.Installed);
    }

    /// <summary>
    /// Builds a Steam catalog result without treating catalog metadata or an
    /// installed Steam client as an ownership assertion.
    /// </summary>
    internal static StoreSearchHit BuildSteamCatalogHit(
        string id,
        string name,
        IReadOnlyList<GameEntry> ownedLibrary)
    {
        var owned = ownedLibrary.Any(g => SteamSourceIsOwned(g, id));
        var installed = ownedLibrary.Any(g => SteamSourceIsInstalled(g, id));
        return new StoreSearchHit
        {
            Id = "steam:" + id,
            Title = name,
            Store = StoreKind.Steam,
            LaunchTarget = id,
            CoverUrl = SteamCover(id),
            Owned = owned,
            Installed = installed,
            CanInstall = owned,
            Source = "steam",
        };
    }

    private static string? TryParseSteamAppId(string q)
    {
        var t = q.Trim();
        if (t.All(char.IsDigit) && t.Length is >= 1 and <= 10) return t;
        var m = System.Text.RegularExpressions.Regex.Match(t, @"/app/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? GetSafeRelaxedSteamQuery(string query)
    {
        var parts = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[^1].All(char.IsDigit)) return null;
        return string.Join(' ', parts[..^1]);
    }

    /// <summary>
    /// Bounded title match that accepts ordinary punctuation/diacritic differences,
    /// small typos, and a trailing numeric mistake without admitting Steam's
    /// recommendation filler. Exact and prefix matches score highest.
    /// </summary>
    internal static bool TitleMatchesQuery(string? title, string query)
    {
        return TitleMatchScore(title, query) >= 0;
    }

    internal static int TitleMatchScore(string? title, string? query)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(query)) return -1;
        var t = Normalize(title);
        var q = Normalize(query);
        if (t.Length == 0 || q.Length == 0) return -1;

        // Cheap exact routes preserve the familiar ordering for normal searches.
        if (t == q) return 1200;
        if (t.StartsWith(q, StringComparison.Ordinal)) return 1050;
        if (ContainsWholePhrase(t, q)) return 900;

        var titleTokens = ExpandAdjacentTokens(Tokens(t));
        var queryTokens = Tokens(q);
        if (titleTokens.Length == 0 || queryTokens.Length == 0) return -1;

        var usedTitleTokens = new bool[titleTokens.Length];
        var matched = 0;
        var exact = 0;
        var prefixes = 0;
        var fuzzy = 0;
        var inOrder = true;
        var lastTitleIndex = -1;
        var unmatchedAreOnlyNumbers = true;

        foreach (var token in queryTokens)
        {
            var bestIndex = -1;
            var bestQuality = 0;
            for (var i = 0; i < titleTokens.Length; i++)
            {
                if (usedTitleTokens[i]) continue;
                var quality = TokenMatchQuality(titleTokens[i], token);
                if (quality <= bestQuality) continue;
                bestQuality = quality;
                bestIndex = i;
            }

            if (bestIndex < 0)
            {
                // Permit one accidental sequel marker ("2"), not a year/code
                // that would make a broad title search look precise.
                unmatchedAreOnlyNumbers &= token.Length == 1 && token.All(char.IsDigit);
                continue;
            }

            usedTitleTokens[bestIndex] = true;
            matched++;
            if (bestIndex < lastTitleIndex) inOrder = false;
            lastTitleIndex = bestIndex;
            switch (bestQuality)
            {
                case 3: exact++; break;
                case 2: prefixes++; break;
                default: fuzzy++; break;
            }
        }

        var nonNumericQueryCount = queryTokens.Count(token => !token.All(char.IsDigit));
        var allMatched = matched == queryTokens.Length;
        var strongPartial = unmatchedAreOnlyNumbers && matched >= 2 && nonNumericQueryCount >= 2;
        var singleStrongToken = queryTokens.Length == 1 && matched == 1 &&
                                (exact == 1 || (prefixes == 1 && queryTokens[0].Length >= 3));
        if (!allMatched && !strongPartial && !singleStrongToken) return -1;

        // A single fuzzy short word is too noisy (for example, "war" should not
        // flood results with unrelated three-letter titles).
        if (queryTokens.Length == 1 && fuzzy == 1 && queryTokens[0].Length < 5) return -1;

        var score = 620 + exact * 95 + prefixes * 55 + fuzzy * 24;
        score += Math.Min(80, matched * 18);
        if (inOrder) score += 30;
        if (strongPartial) score -= 85;
        score -= fuzzy * 12;
        return score;
    }

    private static bool ContainsWholePhrase(string title, string query)
    {
        return title.Contains(" " + query + " ", StringComparison.Ordinal) ||
               title.StartsWith(query + " ", StringComparison.Ordinal) ||
               title.EndsWith(" " + query, StringComparison.Ordinal);
    }

    private static string[] Tokens(string normalized)
    {
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    // Hyphenated and spaced compound names are often typed without the
    // separator ("spiderman"). Add only adjacent joins, preserving the
    // normal bounded token matcher and avoiding broad substring matching.
    private static string[] ExpandAdjacentTokens(string[] tokens)
    {
        if (tokens.Length < 2) return tokens;
        var expanded = new List<string>(tokens.Length * 2 - 1);
        for (var i = 0; i < tokens.Length; i++)
        {
            expanded.Add(tokens[i]);
            if (i + 1 < tokens.Length) expanded.Add(tokens[i] + tokens[i + 1]);
        }
        return expanded.ToArray();
    }

    // 3 exact, 2 prefix, 1 bounded Damerau-Levenshtein typo, 0 no match.
    private static int TokenMatchQuality(string titleToken, string queryToken)
    {
        if (titleToken == queryToken) return 3;
        if (queryToken.Length >= 3 && titleToken.Length >= 3 &&
            (titleToken.StartsWith(queryToken, StringComparison.Ordinal) ||
             queryToken.StartsWith(titleToken, StringComparison.Ordinal))) return 2;
        if (queryToken.Length < 4 || titleToken.Length < 4) return 0;
        if (titleToken[0] != queryToken[0]) return 0;
        var max = AllowedEditDistance(Math.Max(titleToken.Length, queryToken.Length));
        return BoundedDamerauLevenshtein(titleToken, queryToken, max) <= max ? 1 : 0;
    }

    private static int AllowedEditDistance(int length) => length switch
    {
        <= 4 => 1,
        <= 7 => 1,
        _ => 2,
    };

    private static int BoundedDamerauLevenshtein(string left, string right, int max)
    {
        if (Math.Abs(left.Length - right.Length) > max) return max + 1;
        var previousPrevious = new int[right.Length + 1];
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                var value = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);
                if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1])
                    value = Math.Min(value, previousPrevious[j - 2] + 1);
                current[j] = value;
                rowMin = Math.Min(rowMin, value);
            }
            if (rowMin > max) return max + 1;
            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }
        return previous[right.Length];
    }

    internal static string Normalize(string s)
    {
        var chars = new List<char>(s.Length);
        var lastWasSpace = true;
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                chars.Add(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                chars.Add(' ');
                lastWasSpace = true;
            }
        }
        if (chars.Count > 0 && chars[^1] == ' ') chars.RemoveAt(chars.Count - 1);
        return new string(chars.ToArray());
    }

    private static int RelevanceScore(StoreSearchHit h, string query)
    {
        var t = Normalize(h.Title);
        var score = TitleMatchScore(h.Title, query);
        if (score < 0) score = TitleMatchScore(h.LaunchTarget, query);
        if (score < 0) return score;
        if (h.Installed) score += 50;
        if (h.Owned) score += 25;
        if (IsOfficialClientCatalogInstall(h)) score += 40;
        if (string.Equals(h.Source, "steam", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(h.Source, "library", StringComparison.OrdinalIgnoreCase))
            score += 10;
        // Prefer shorter titles (Palworld over Palworld: Something Long)
        score += Math.Max(0, 40 - Math.Min(40, t.Length));
        return score;
    }

    private static List<StoreSearchHit> RankAndDedup(IEnumerable<StoreSearchHit> hits, string query, int cap)
    {
        // A public catalog row and an account-proven library row can share a
        // title across stores (Epic/Xbox/GOG/Riot/… vs Steam). Keep the
        // higher-ranked owned/installed entry so Search never offers Buy for
        // a game already in the library.
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<StoreSearchHit>();
        foreach (var h in hits.OrderByDescending(h => RelevanceScore(h, query))
                              .ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase))
        {
            var titleKey = TitleIdentity(h.Title);
            if (seenIds.Contains(h.Id)) continue;
            if (titleKey.Length > 0 && seenTitles.Contains(titleKey)) continue;
            seenIds.Add(h.Id);
            if (titleKey.Length > 0) seenTitles.Add(titleKey);
            ordered.Add(h);
            if (ordered.Count >= cap) break;
        }
        return ordered;
    }

    /// <summary>Letter/digit title fold used to collapse the same game across stores.</summary>
    internal static string TitleIdentity(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        return Normalize(title).Replace(" ", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Official Steam library poster CDN for instant search paint. Disk cache /
    /// virtual host still replace this once warm finishes.
    /// </summary>
    private static string? SteamCover(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsDigit)) return null;
        return CoverArtService.SteamPortraitCdnUrl(appId);
    }
}
