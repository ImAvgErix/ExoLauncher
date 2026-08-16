using System.Collections.Concurrent;
using System.Diagnostics;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
// AppLog + CoverArtService live in Helpers / Services (same assembly)

namespace ExoLauncher.Services;

public sealed class LibraryService
{
    public sealed record StoreBackendStatus(
        string store,
        string displayName,
        bool agentPresent,
        bool clientPresent,
        bool signedIn,
        string detail);

    private readonly IReadOnlyList<IStoreAdapter> _adapters;
    private readonly SettingsService _settings;
    private readonly SteamOwnershipCatalog _steamOwnershipCatalog;
    private IReadOnlyList<GameEntry> _cache = Array.Empty<GameEntry>();
    private readonly Dictionary<string, IReadOnlyList<GameEntry>> _lastGoodByAdapter =
        new(StringComparer.OrdinalIgnoreCase);
    // Opaque account scopes are process-local cache keys. They never cross the
    // bridge and are intentionally not persisted in the library model.
    private readonly Dictionary<string, string?> _lastAccountScopeByAdapter =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _scanErrors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private DateTimeOffset _cacheAt = DateTimeOffset.MinValue;
    private long _scanGeneration;
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);

    private sealed record AdapterScanResult(
        string Id,
        IReadOnlyList<GameEntry> Items,
        bool Succeeded,
        string? Error,
        bool ProvesInstalledSteamOwnership,
        bool IsAccountScoped,
        string? AccountScope,
        long ElapsedMilliseconds);

    /// <summary>Raised when cover cache finishes a batch so UI can refresh art.</summary>
    public event Action? LibraryUpdated;

    /// <summary>Raised once after a complete store scan and playtime enrichment.</summary>
    public event Action? LibraryScanCompleted;

    public LibraryService(IReadOnlyList<IStoreAdapter> adapters, SettingsService settings)
        : this(adapters, settings, new SteamOwnershipCatalog())
    {
    }

    internal LibraryService(
        IReadOnlyList<IStoreAdapter> adapters,
        SettingsService settings,
        SteamOwnershipCatalog steamOwnershipCatalog)
    {
        _adapters = adapters;
        _settings = settings;
        _steamOwnershipCatalog = steamOwnershipCatalog;
    }

    /// <summary>Cached library for fast search — never triggers a store rescan.</summary>
    public IReadOnlyList<GameEntry> PeekCachedLibrary() =>
        _cache.Count == 0 ? Array.Empty<GameEntry>() : OverlayUserPrefs(_cache);

    public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cache.Count > 0 && DateTimeOffset.UtcNow - _cacheAt < Freshness &&
            !HaveAccountScopesChanged())
            return OverlayUserPrefs(_cache);

        var observedGeneration = Interlocked.Read(ref _scanGeneration);
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A forced refresh means "newer than what I observed", not "run one
            // more scan even if another caller just produced that result". This
            // coalesces the terminal progress/RPC/library-event burst after an
            // install or sync into one store scan.
            if (Interlocked.Read(ref _scanGeneration) != observedGeneration)
                return OverlayUserPrefs(_cache);
            if (!force && _cache.Count > 0 && DateTimeOffset.UtcNow - _cacheAt < Freshness &&
                !HaveAccountScopesChanged())
                return OverlayUserPrefs(_cache);
            var result = await ScanLibraryAsync(force, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _scanGeneration);
            return result;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<IReadOnlyList<GameEntry>> ScanLibraryAsync(bool force, CancellationToken ct)
    {
        var scanStopwatch = Stopwatch.StartNew();
        var discovered = new List<GameEntry>();
        // Several adapters still do registry and directory traversal before
        // returning their first incomplete Task. Run each adapter entry point on
        // the pool so a cold scan cannot synchronously stall the WebView thread.
        var tasks = _adapters.Select(adapter => Task.Run(async () =>
            {
                var adapterStopwatch = Stopwatch.StartNew();
                var accountScoped = adapter is IStoreAccountScope;
                var scopeBefore = accountScoped
                    ? ((IStoreAccountScope)adapter).GetActiveAccountScope()
                    : null;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(25));
                    var items = await adapter.GetLibraryAsync(timeout.Token).ConfigureAwait(false);
                    var scopeAfter = accountScoped
                        ? ((IStoreAccountScope)adapter).GetActiveAccountScope()
                        : null;
                    if (accountScoped && !string.Equals(scopeBefore, scopeAfter, StringComparison.Ordinal))
                    {
                        return new AdapterScanResult(
                            adapter.Id, Array.Empty<GameEntry>(), false,
                            "Store account changed during scan; retrying with the active account.", false,
                            true, scopeAfter, adapterStopwatch.ElapsedMilliseconds);
                    }
                    if (accountScoped && string.IsNullOrWhiteSpace(scopeBefore))
                    {
                        // A logged-out/unknown account must not inherit owned
                        // titles, playtime, or last-played data. Machine install
                        // manifests remain useful and safe: keep only verified
                        // installed paths and strip every account-only claim.
                        items = InstalledWithoutAccountClaims(items);
                    }
                    return new AdapterScanResult(
                        adapter.Id,
                        items,
                        true,
                        null,
                        adapter.Store == StoreKind.Steam && adapter is IInstalledSteamManifestSource,
                        accountScoped,
                        scopeBefore,
                        adapterStopwatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    return new AdapterScanResult(
                        adapter.Id,
                        Array.Empty<GameEntry>(),
                        false,
                        ex.Message,
                        false,
                        accountScoped,
                        scopeBefore,
                        adapterStopwatch.ElapsedMilliseconds);
                }
            }, ct))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var result in results)
        {
            if (result.IsAccountScoped)
                _lastAccountScopeByAdapter[result.Id] = result.AccountScope;
            if (result.Succeeded)
            {
                _scanErrors.TryRemove(result.Id, out _);
                if (!result.IsAccountScoped || result.AccountScope is not null)
                    _lastGoodByAdapter[LastGoodKey(result.Id, result.AccountScope, result.IsAccountScoped)] = result.Items;
                discovered.AddRange(result.Items);
            }
            else
            {
                _scanErrors[result.Id] = result.Error ?? "Unknown scan error";
                AppLog.Warn($"Library scan failed for {result.Id}: {result.Error}");
                var lastGoodKey = LastGoodKey(result.Id, result.AccountScope, result.IsAccountScoped);
                if ((!result.IsAccountScoped || result.AccountScope is not null) &&
                    _lastGoodByAdapter.TryGetValue(lastGoodKey, out var lastGood))
                {
                    // A timeout must not make an otherwise healthy store disappear from Exo.
                    discovered.AddRange(lastGood);
                }
            }
        }

        // Drop test fixtures and non-games junk. Local is a real wired backend.
        discovered = discovered
            .Where(g => !IsTestPollution(g))
            .Where(g => !IsNonGameTitle(g))
            .ToList();

        // Steam removes appmanifest_<id>.acf during uninstall. Persist only
        // entries previously proven by that installed-manifest scan, then
        // rehydrate them as owned/not-installed so Search can offer Install.
        // A Steam appmanifest belongs to a machine, not conclusively to every
        // user who can sign in there. Persist/re-hydrate that uninstall proof
        // only inside the exact active account scope that produced it.
        foreach (var steamResult in results.Where(result =>
                     result.Succeeded && result.ProvesInstalledSteamOwnership))
        {
            var manifestProvenSteamGames = steamResult.Items
                .Where(game => !IsTestPollution(game))
                .Where(game => !IsNonGameTitle(game))
                .ToList();
            if (!string.IsNullOrWhiteSpace(steamResult.AccountScope))
            {
                _steamOwnershipCatalog.RememberInstalled(steamResult.AccountScope!, manifestProvenSteamGames);
                discovered.AddRange(_steamOwnershipCatalog.RestoreMissing(steamResult.AccountScope!, discovered));
            }
            else if (!steamResult.IsAccountScoped)
            {
                // Lightweight fixture/third-party adapter compatibility. The
                // real Steam adapter implements IStoreAccountScope and never
                // enters this legacy branch.
                _steamOwnershipCatalog.RememberInstalled(manifestProvenSteamGames);
                discovered.AddRange(_steamOwnershipCatalog.RestoreMissing(discovered));
            }
        }

        // Dedupe by id
        var byId = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in discovered)
            byId[g.Id] = g;

        var ordered = byId.Values
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Store + Exo-tracked playtime for every title (Steam VDF, GOG JSON, sessions).
        ordered = PlaytimeService.Enrich(ordered).ToList();

        // Keep source rows intact through every enrichment step, then project
        // exact-title matches into one card at the boundary consumed by the UI.
        // The projected card still uses one real source as its top-level entry;
        // it is never a synthetic launch target or a sum of store statistics.
        var coldStart = _cacheAt == DateTimeOffset.MinValue;
        _cache = GroupVariants(CoverArtService.WithCovers(ordered));
        // Fire once immediately so UI gets CDN URLs, then again as disk art lands.
        try { LibraryUpdated?.Invoke(); } catch { /* ignore */ }
        // Warm installed + pinned titles before first paint on cold start.
        // Owned-not-installed posters keep filling after the splash.
        var warmTargets = OverlayUserPrefs(_cache)
            .Where(CoverArtService.ShouldWarmLibraryCover)
            .ToArray();
        void OnWarmBatch()
        {
            _cache = CoverArtService.WithCovers(_cache);
            try { LibraryUpdated?.Invoke(); } catch { /* ignore */ }
        }
        // requested=true: high concurrency + notify every poster so tiles fill ASAP.
        if (coldStart)
        {
            // Splash waits for the home grid (installed + pins) and playtime.
            // Owned-not-installed posters keep warming after the app opens.
            var visible = warmTargets.Where(g => g.Installed || g.IsFavorite).ToArray();
            var rest = warmTargets.Where(g => !g.Installed && !g.IsFavorite).ToArray();
            var visibleWarm = CoverArtService.WarmCacheAsync(
                visible, OnWarmBatch, requested: true, deferForFirstPaint: false);
            _ = await Task.WhenAny(visibleWarm, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false);
            OnWarmBatch();
            if (rest.Length > 0)
                _ = CoverArtService.WarmCacheAsync(rest, OnWarmBatch, requested: true, deferForFirstPaint: false);
        }
        else
        {
            _ = CoverArtService.WarmCacheAsync(
                warmTargets, OnWarmBatch, requested: true, deferForFirstPaint: false);
        }
        _cacheAt = DateTimeOffset.UtcNow;
        scanStopwatch.Stop();
        var adapterTimings = string.Join(' ', results.Select(result =>
            $"{result.Id}:{result.ElapsedMilliseconds}ms/{result.Items.Count}/{(result.Succeeded ? "ok" : "failed")}"));
        AppLog.Info(
            $"PERF library-scan totalMs={scanStopwatch.ElapsedMilliseconds} force={force.ToString().ToLowerInvariant()} adapters=\"{adapterTimings}\"");
        try { LibraryScanCompleted?.Invoke(); } catch { /* background consumers are best-effort */ }
        return OverlayUserPrefs(_cache);
    }

    public GameEntry? Find(string id)
    {
        var hit = _cache.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return OverlayUserPrefs(new[] { hit })[0];

        // A variant id remains a valid bridge/action id even though it is no
        // longer a separate visible card. Resolve it back to an exact source
        // row; do not route its action through the preferred sibling store.
        var grouped = _cache.FirstOrDefault(card => card.Variants.Any(variant =>
            string.Equals(variant.Id, id, StringComparison.OrdinalIgnoreCase)));
        if (grouped is null) return null;
        var selected = grouped.Variants.First(variant =>
            string.Equals(variant.Id, id, StringComparison.OrdinalIgnoreCase));
        return OverlayUserPrefs(new[] { MaterializeVariant(grouped, selected) })[0];
    }

    public void Invalidate() => _cacheAt = DateTimeOffset.MinValue;

    /// <summary>Re-apply cover URLs after cache warm without full rediscovery.</summary>
    public IReadOnlyList<GameEntry> RefreshCovers()
    {
        if (_cache.Count == 0) return _cache;
        _cache = GroupVariants(CoverArtService.WithCovers(ExpandVariants(_cache)));
        return OverlayUserPrefs(_cache);
    }

    /// <summary>
    /// Recompute settings overlays and local/cloud playtime against the current
    /// library without asking every store client to rediscover its catalog.
    /// </summary>
    public async Task<IReadOnlyList<GameEntry>> RefreshDerivedStateAsync(CancellationToken ct = default)
    {
        IReadOnlyList<GameEntry> result;
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.Count == 0) return _cache;
            _cache = GroupVariants(CoverArtService.WithCovers(EpicPlaytime.Apply(
                PlaytimeService.Enrich(ExpandVariants(_cache)),
                EpicPlaytime.GetCachedMinutes())));
            result = OverlayUserPrefs(_cache);
        }
        finally
        {
            _scanGate.Release();
        }

        try { LibraryUpdated?.Invoke(); } catch { /* background consumers are best-effort */ }
        return result;
    }

    public IReadOnlyList<StoreBackendStatus> StoreMatrix()
    {
        return _adapters.Select(a =>
        {
            var agentPresent = a.IsAgentPresent();
            // Legacy adapters only have one presence concept, which preserves
            // existing Steam/Epic/Riot behavior. GOG reports Galaxy separately
            // because Exo may bundle gogdl without Galaxy being installed.
            var clientPresent = a is IStoreClientPresence client
                ? client.IsClientPresent()
                : agentPresent;
            // A visible official client is only a presence signal. It must not
            // claim a connected account until its own authenticated adapter can
            // prove one.
            var signedIn = a is IOfficialStoreClient ? false : IsStoreSignedIn(a.Id, agentPresent);
            var detail = StoreDetail(a.Id, clientPresent, signedIn);
            return new StoreBackendStatus(
                a.Id,
                a.DisplayName,
                agentPresent,
                clientPresent,
                signedIn,
                _scanErrors.TryGetValue(a.Id, out _)
                    ? $"{detail} · Scan unavailable"
                    : detail);
        }).ToList();
    }

    private static bool IsStoreSignedIn(string storeId, bool agentPresent)
    {
        if (!agentPresent) return false;
        return storeId.ToLowerInvariant() switch
        {
            // Client presence is not proof of account/session state. Exo reports
            // the backend as available and lets the launch/install result say if
            // vendor sign-in is required.
            "steam" => false,
            "riot" => false,
            "epic" => File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "legendary", "user.json")),
            "gog" => IsGogSignedIn(),
            _ => agentPresent,
        };
    }

    private static bool IsGogSignedIn()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(PathHelper.AppDataDir, "gogdl", "credentials.json"),
            Path.Combine(roaming, "heroic", "gog_store", "auth.json"),
            Path.Combine(user, ".config", "heroic", "gog_store", "auth.json"),
            Path.Combine(user, ".config", "heroic", "gog_store", "credentials.json"),
            Path.Combine(user, ".config", "gogdl", "credentials.json"),
        };
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(candidate) &&
                    GogdlCli.HasAuthenticatedCredentials(File.ReadAllText(candidate)))
                    return true;
            }
            catch { /* try the next known location */ }
        }
        return false;
    }

    private static string StoreDetail(string storeId, bool present, bool signedIn)
    {
        // "Missing" was ambiguous and made an unavailable store look like a
        // transient scan state. This is installation state, not connectivity.
        if (signedIn) return "Signed in";
        if (!present) return "Not installed";
        // Steam / Riot are ready when the client is present; Epic / GOG say Found until signed in.
        if (storeId is "steam" or "riot") return "Client present";
        return "Found";
    }

    private IReadOnlyList<GameEntry> OverlayUserPrefs(IReadOnlyList<GameEntry> games)
    {
        return games.Select(g =>
        {
            var sourceIds = g.Variants.Count == 0
                ? new[] { g.Id }
                : g.Variants.Select(variant => variant.Id).ToArray();
            var fav = sourceIds.Any(_settings.IsFavorite);
            var last = sourceIds
                .Select(_settings.GetLastPlayed)
                .Concat(g.Variants.Select(variant => variant.LastPlayedUtc))
                .Append(g.LastPlayedUtc)
                .Where(value => value.HasValue)
                .OrderByDescending(value => value)
                .FirstOrDefault();
            if (!fav && last is null && !g.IsFavorite)
                return g;
            return new GameEntry
            {
                Id = g.Id,
                Title = g.Title,
                Store = g.Store,
                Installed = g.Installed,
                Owned = g.Owned,
                UpdateAvailable = g.UpdateAvailable,
                CanInstall = g.CanInstall,
                Path = g.Path,
                CoverUrl = g.CoverUrl,
                CoverSource = g.CoverSource,
                PlaytimeMinutes = g.PlaytimeMinutes,
                SizeBytes = g.SizeBytes,
                Status = g.Status,
                Deps = g.Deps,
                LaunchNote = g.LaunchNote,
                LaunchTarget = g.LaunchTarget,
                LastPlayedUtc = last,
                IsFavorite = fav || g.IsFavorite,
                CanonicalTitleKey = g.CanonicalTitleKey,
                SelectedVariantId = g.SelectedVariantId,
                Variants = g.Variants,
            };
        }).ToList();
    }

    /// <summary>
    /// Projects exact same-title entries from two or more stores into one card.
    /// Title matching is deliberately conservative: after Unicode/case/punctuation
    /// normalization it must be exact. Editions, DLC, and same-store duplicates
    /// therefore stay distinct until an authoritative product mapping exists.
    /// </summary>
    internal static IReadOnlyList<GameEntry> GroupVariants(IReadOnlyList<GameEntry> entries)
    {
        if (entries.Count < 2) return entries;

        return entries
            .GroupBy(CanonicalTitleKeyFor, StringComparer.Ordinal)
            .SelectMany(group =>
            {
                var rows = group.ToArray();
                if (rows.Length < 2 || rows.Select(row => row.Store).Distinct().Count() < 2)
                    return rows;

                var selected = rows
                    .OrderByDescending(row => row.Installed)
                    // Never hide a real pending update behind an equally
                    // installed sibling whose action is only Play.
                    .ThenByDescending(row => row.UpdateAvailable)
                    .ThenByDescending(row => row.PrimaryAction == "play")
                    .ThenByDescending(row => row.Owned)
                    .ThenBy(row => StorePreference(row.Store))
                    .ThenBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
                    .First();
                var variants = rows
                    .OrderByDescending(row => string.Equals(row.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(row => row.Installed)
                    .ThenBy(row => StorePreference(row.Store))
                    .ThenBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(GameVariant.FromGame)
                    .ToArray();
                return new[]
                {
                    CopyWithVariants(
                        selected,
                        group.Key,
                        variants,
                        rows.Any(row => row.IsFavorite))
                };
            })
            .OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string CanonicalTitleKeyFor(GameEntry game)
    {
        var title = (game.Title ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(title.Length);
        foreach (var character in title)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) ==
                System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        // An empty/unusable display name must never make unrelated broken rows
        // share a card.
        return builder.Length == 0 ? "id:" + game.Id.ToLowerInvariant() : builder.ToString();
    }

    private static int StorePreference(StoreKind store) => store switch
    {
        StoreKind.Steam => 0,
        StoreKind.Epic => 1,
        StoreKind.Gog => 2,
        StoreKind.Riot => 3,
        StoreKind.Local => 4,
        _ => 10,
    };

    private static IReadOnlyList<GameEntry> ExpandVariants(IReadOnlyList<GameEntry> entries) =>
        entries.SelectMany(entry => entry.Variants.Count == 0
            ? new[] { entry }
            : entry.Variants.Select(variant => MaterializeVariant(entry, variant)))
            .ToArray();

    private static GameEntry MaterializeVariant(GameEntry card, GameVariant variant) => variant.ToGameEntry(card);

    private static IReadOnlyList<GameEntry> InstalledWithoutAccountClaims(IReadOnlyList<GameEntry> entries) =>
        entries
            .Where(entry => entry.Installed &&
                            !string.IsNullOrWhiteSpace(entry.Path) &&
                            (Directory.Exists(entry.Path) || File.Exists(entry.Path)))
            .Select(entry => new GameEntry
            {
                Id = entry.Id,
                Title = entry.Title,
                Store = entry.Store,
                Installed = true,
                Owned = false,
                UpdateAvailable = entry.UpdateAvailable,
                CanInstall = false,
                Path = entry.Path,
                CoverUrl = entry.CoverUrl,
                CoverSource = entry.CoverSource,
                PlaytimeMinutes = null,
                SizeBytes = entry.SizeBytes,
                Status = entry.Status,
                Deps = entry.Deps,
                LaunchNote = entry.LaunchNote,
                LaunchTarget = entry.LaunchTarget,
                LastPlayedUtc = null,
                IsFavorite = entry.IsFavorite,
            })
            .ToArray();

    private static GameEntry CopyWithVariants(
        GameEntry source,
        string canonicalTitleKey,
        IReadOnlyList<GameVariant> variants,
        bool isFavorite) => new()
    {
        Id = source.Id,
        Title = source.Title,
        Store = source.Store,
        Installed = source.Installed,
        Owned = source.Owned,
        UpdateAvailable = source.UpdateAvailable || variants.Any(variant => variant.UpdateAvailable),
        CanInstall = source.CanInstall,
        Path = source.Path,
        CoverUrl = source.CoverUrl,
        CoverSource = source.CoverSource,
        PlaytimeMinutes = source.PlaytimeMinutes,
        SizeBytes = source.SizeBytes,
        Status = source.Status,
        Deps = source.Deps,
        LaunchNote = source.LaunchNote,
        LaunchTarget = source.LaunchTarget,
        LastPlayedUtc = source.LastPlayedUtc,
        // A canonical card stays pinned when any of its exact store sources is
        // pinned, even if another installed source becomes the selected row.
        IsFavorite = isFavorite,
        CanonicalTitleKey = canonicalTitleKey,
        SelectedVariantId = source.Id,
        Variants = variants,
    };

    private bool HaveAccountScopesChanged()
    {
        foreach (var adapter in _adapters.OfType<IStoreAccountScope>())
        {
            // Adapter identity is stable and is the only bridge-visible store
            // identifier. The opaque scope itself stays inside this service.
            var storeId = ((IStoreAdapter)adapter).Id;
            if (!_lastAccountScopeByAdapter.TryGetValue(storeId, out var previous))
                continue;
            var current = adapter.GetActiveAccountScope();
            if (!string.Equals(previous, current, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string LastGoodKey(string adapterId, string? accountScope, bool accountScoped) =>
        accountScoped ? adapterId + "\0" + (accountScope ?? "unverified") : adapterId;

    private static bool IsTestPollution(GameEntry g)
    {
        if (g.Id.Contains("exo-launcher-test", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(g.Path) &&
            g.Path.Contains("exo-launcher-test", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(g.Title) &&
            g.Title.StartsWith("exo-launcher-test", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool IsNonGameTitle(GameEntry g)
    {
        if (g.Store == StoreKind.Steam && !string.IsNullOrWhiteSpace(g.LaunchTarget))
            return SteamAdapter.IsNonGameSteamEntry(g.LaunchTarget, g.Title, Path.GetFileName(g.Path));
        var t = (g.Title ?? "").ToLowerInvariant();
        return t.Contains("steamworks", StringComparison.Ordinal)
               || t.Contains("redistributable", StringComparison.Ordinal)
               || t.Contains("dedicated server", StringComparison.Ordinal);
    }
}

internal static class MockCatalog
{
    public static IReadOnlyList<GameEntry> Create() =>
    [
        new GameEntry
        {
            Id = "mock:valorant",
            Title = "VALORANT",
            Store = StoreKind.Riot,
            Installed = false,
            Owned = true,
            CanInstall = true,
            Status = "Demo",
            PlaytimeMinutes = 0,
            SizeBytes = 30L * 1024 * 1024 * 1024,
            Deps = ["Riot Client", "Vanguard"],
            LaunchNote = "Demo tile. Real install uses official RiotClientServices; Vanguard required for online play.",
            LaunchTarget = "valorant",
        },
        new GameEntry
        {
            Id = "mock:hades",
            Title = "Hades",
            Store = StoreKind.Steam,
            Installed = false,
            Owned = true,
            CanInstall = true,
            Status = "Demo",
            PlaytimeMinutes = 1240,
            SizeBytes = 15L * 1024 * 1024 * 1024,
            Deps = ["Steam client"],
            LaunchNote = "Demo tile. Real Steam titles install/launch via minimized Steam.",
            LaunchTarget = "1145360",
        },
        new GameEntry
        {
            Id = "mock:control",
            Title = "Control",
            Store = StoreKind.Epic,
            Installed = false,
            Owned = true,
            CanInstall = true,
            Status = "Demo",
            PlaytimeMinutes = 720,
            SizeBytes = 42L * 1024 * 1024 * 1024,
            Deps = ["Legendary"],
            LaunchNote = "Demo tile. Epic installs via Legendary when present — Epic GUI optional.",
            LaunchTarget = "Control",
        },
        new GameEntry
        {
            Id = "mock:disco",
            Title = "Disco Elysium",
            Store = StoreKind.Gog,
            Installed = false,
            Owned = true,
            CanInstall = true,
            Status = "Demo",
            PlaytimeMinutes = 2100,
            SizeBytes = 20L * 1024 * 1024 * 1024,
            Deps = ["gogdl"],
            LaunchNote = "Demo tile. GOG installs via gogdl.",
        },
    ];
}
