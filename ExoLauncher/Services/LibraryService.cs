using System.Collections.Concurrent;
using System.Diagnostics;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed class LibraryService
{
    public sealed record StoreBackendStatus(
        string store,
        string displayName,
        bool agentPresent,
        bool clientPresent,
        bool backendPresent,
        bool signedIn,
        bool cachePresent,
        bool webApiKeyPresent,
        bool localDatabasePresent,
        string detail,
        string checkCode,
        DateTimeOffset checkedAtUtc);

    public sealed record StoreLocalCheck(
        string state,
        DateTimeOffset checkedAtUtc,
        string code,
        IReadOnlyList<StoreLocalCheckItem> stores);

    public sealed record StoreLocalCheckItem(
        string store,
        string client,
        string backend,
        string session,
        string cache,
        string readiness,
        string code);

    private readonly IReadOnlyList<IStoreAdapter> _adapters;
    private readonly SettingsService _settings;
    private readonly GameCoverImageStore _coverImages = new();
    private readonly SteamOwnershipCatalog _steamOwnershipCatalog;
    private readonly object _storeMatrixGate = new();
    private static readonly TimeSpan StoreMatrixTtl = TimeSpan.FromSeconds(20);
    private IReadOnlyList<StoreBackendStatus>? _storeMatrix;
    private DateTimeOffset _storeMatrixAtUtc;
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
    private long _artRevisionSequence;
    private readonly ConcurrentDictionary<string, long> _artRevisions =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);
    private readonly LibraryWatchers _watchers = new();
    private int _watchersStarted;
    private int _backgroundScanScheduled;

    private sealed record AdapterScanResult(
        string Id,
        IReadOnlyList<GameEntry> Items,
        bool Succeeded,
        string? Error,
        bool ProvesInstalledSteamOwnership,
        bool IsAccountScoped,
        string? AccountScope,
        long ElapsedMilliseconds);

    /// <summary>Raised when the cached library changes or a cover batch lands.</summary>
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

    public void StartWatchers()
    {
        if (Interlocked.Exchange(ref _watchersStarted, 1) != 0) return;
        _watchers.Changed += OnWatchedStoreChanged;
        _watchers.Start();
    }

    public void DisposeWatchers()
    {
        _watchers.Changed -= OnWatchedStoreChanged;
        _watchers.Dispose();
    }

    private void OnWatchedStoreChanged()
    {
        Invalidate();
        _ = Task.Run(async () =>
        {
            try
            {
                AppLog.Info("Library watch refresh.");
                await GetLibraryAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex) { AppLog.Warn("Watched library refresh failed: " + ex.Message); }
        });
    }

    public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync(bool force = false, CancellationToken ct = default)
    {
        if (_cache.Count == 0)
        {
            var disk = LibraryDiskCache.TryLoad(CurrentAccountScopes());
            if (disk is { Count: > 0 })
            {
                _cache = disk;
                // Disk rows can predate cover files that finished warming after
                // the last save. Reapply local URLs before publishing the paint;
                // the bridge intentionally does not perform this work again.
                RefreshCovers();
                SeedLastGoodFrom(disk);
                try { LibraryUpdated?.Invoke(); } catch { /* first paint */ }
            }
        }

        // Last-good (memory or disk) is enough to answer the UI. A view switch
        // must not wait for every store adapter. Watchers and force=true still
        // run a real scan; account-scope changes never reuse another user's rows.
        if (!force && _cache.Count > 0 && !HaveAccountScopesChanged())
        {
            var stale = _cacheAt == DateTimeOffset.MinValue ||
                        DateTimeOffset.UtcNow - _cacheAt >= Freshness;
            if (stale) ScheduleBackgroundScan();
            return OverlayUserPrefs(_cache);
        }

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

    private void ScheduleBackgroundScan()
    {
        if (Interlocked.CompareExchange(ref _backgroundScanScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await GetLibraryAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Background library scan failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundScanScheduled, 0);
            }
        });
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
                    if (adapter.Store == StoreKind.Steam &&
                        adapter is IInstalledSteamManifestSource &&
                        adapter is IAuthoritativeOwnershipSource ownershipSource)
                    {
                        // Defense in depth: an adapter row cannot turn install
                        // history into a current license. A verified exclusion
                        // remains visible only when files are still installed.
                        items = ReconcileSteamOwnership(
                            items,
                            ownershipSource.LastAuthoritativeOwnedAppIds);
                    }
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
                else if (_cacheAt == DateTimeOffset.MinValue)
                {
                    // First process scan: keep the disk-painted rows for this
                    // store when last-good was never seeded (scope-unverified
                    // adapters, or a cache written before this adapter existed).
                    discovered.AddRange(PaintedRowsForAdapter(result.Id));
                }
            }
        }

        // Drop test fixtures and non-games junk. Local is a real wired backend.
        discovered = discovered
            .Where(g => !IsTestPollution(g))
            .Where(g => !IsNonGameTitle(g))
            .ToList();

        // Steam removes appmanifest_<id>.acf during uninstall. Persist only the
        // intersection between an installed row and the authoritative current-
        // account owned-games snapshot, then rehydrate it inside that exact
        // opaque account scope. A manifest by itself is never ownership proof.
        foreach (var steamResult in results.Where(result =>
                     result.Succeeded && result.ProvesInstalledSteamOwnership))
        {
            var authoritativeSteamOwned = _adapters
                .Where(adapter => string.Equals(adapter.Id, steamResult.Id, StringComparison.Ordinal))
                .OfType<IAuthoritativeOwnershipSource>()
                .Select(adapter => adapter.LastAuthoritativeOwnedAppIds)
                .FirstOrDefault();
            var manifestProvenSteamGames = steamResult.Items
                .Where(game => !IsTestPollution(game))
                .Where(game => !IsNonGameTitle(game))
                .ToList();
            if (!string.IsNullOrWhiteSpace(steamResult.AccountScope))
            {
                if (authoritativeSteamOwned is not null)
                {
                    _steamOwnershipCatalog.RememberVerifiedInstalled(
                        steamResult.AccountScope!, manifestProvenSteamGames, authoritativeSteamOwned);
                    _steamOwnershipCatalog.PruneToAuthoritative(steamResult.AccountScope!, authoritativeSteamOwned);
                }
                discovered.AddRange(_steamOwnershipCatalog.RestoreMissing(
                    steamResult.AccountScope!, discovered, authoritativeSteamOwned));
            }
            else if (!steamResult.IsAccountScoped)
            {
                // Lightweight fixture/third-party adapter compatibility. The
                // real Steam adapter implements IStoreAccountScope and never
                // enters this legacy branch.
                if (authoritativeSteamOwned is not null)
                {
                    _steamOwnershipCatalog.RememberVerifiedInstalled(
                        "legacy-unscoped", manifestProvenSteamGames, authoritativeSteamOwned);
                    _steamOwnershipCatalog.PruneToAuthoritative("legacy-unscoped", authoritativeSteamOwned);
                }
                discovered.AddRange(_steamOwnershipCatalog.RestoreMissing(
                    "legacy-unscoped", discovered, authoritativeSteamOwned));
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
        ordered = EpicPlaytime.Apply(ordered, EpicPlaytime.GetCachedMinutes()).ToList();
        EpicPlaytime.RefreshCachedMinutes();

        // Keep source rows intact through every enrichment step, then project
        // exact-title matches into one card at the boundary consumed by the UI.
        // The projected card still uses one real source as its top-level entry;
        // it is never a synthetic launch target or a sum of store statistics.
        // A scan is the moment an install or sign-in could have changed.
        InvalidateStoreMatrix();
        _cache = GroupVariants(CoverArtService.WithCovers(ordered));
        // Fire once immediately so UI gets CDN URLs, then again as disk art lands.
        try { LibraryUpdated?.Invoke(); } catch { /* ignore */ }
        // Every real library title may fill its portrait cache in the background;
        // installed and pinned titles are also eligible for missing wide art.
        var warmTargets = OverlayUserPrefs(_cache)
            .Where(CoverArtService.ShouldWarmLibraryCover)
            .ToArray();
        void OnWarmBatch()
        {
            // Re-expand first. WithCover used to clone the grouped card and
            // drop Variants, which hid Epic/GOG hours on a Steam-preferred tile.
            RefreshCovers();
            try { LibraryUpdated?.Invoke(); } catch { /* ignore */ }
        }
        // A broad scan is never a user-requested cover operation. Queue the
        // whole warm at background priority and let the scan return immediately;
        // search keeps the requested/high-priority path for its visible results.
        _ = CoverArtService.WarmCacheAsync(
            warmTargets, OnWarmBatch, requested: false, deferForFirstPaint: true);
        _cacheAt = DateTimeOffset.UtcNow;
        scanStopwatch.Stop();
        var adapterTimings = string.Join(' ', results.Select(result =>
            $"{result.Id}:{result.ElapsedMilliseconds}ms/{result.Items.Count}/{(result.Succeeded ? "ok" : "failed")}"));
        AppLog.Info(
            $"PERF library-scan totalMs={scanStopwatch.ElapsedMilliseconds} force={force.ToString().ToLowerInvariant()} adapters=\"{adapterTimings}\"");
        try { LibraryDiskCache.Save(_cache, CurrentAccountScopes()); }
        catch { /* disk cache is best-effort */ }
        try { LibraryScanCompleted?.Invoke(); } catch { /* background consumers are best-effort */ }
        return OverlayUserPrefs(_cache);
    }

    private IReadOnlyDictionary<string, string?> CurrentAccountScopes()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in _adapters)
        {
            if (adapter is IStoreAccountScope scoped)
                map[adapter.Id] = scoped.GetActiveAccountScope();
        }

        return map;
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

    /// <summary>
    /// Forces a current-account store scan before returning an exact action
    /// source. Queued mutations use this immediately before adapter work so an
    /// account switch or refund cannot inherit the authority of the old row.
    /// </summary>
    internal async Task<GameEntry?> RevalidateActionGameAsync(
        string id,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var expectedStore = Find(id)?.Store;
        Invalidate();
        _ = await GetLibraryAsync(force: true, ct).ConfigureAwait(false);
        var current = Find(id);
        var store = current?.Store ?? expectedStore;
        var adapter = store is null
            ? null
            : _adapters.FirstOrDefault(candidate => candidate.Store == store.Value);
        // A last-good row is useful for painting, but it is not fresh authority
        // for a delayed mutation when that store's revalidation scan failed.
        if (adapter is not null && _scanErrors.ContainsKey(adapter.Id))
            return null;
        return current;
    }

    /// <summary>
    /// Finds the visual library card that owns an exact source id. Artwork is a
    /// card-level concern, so callers must not accidentally update only the
    /// currently selected store variant.
    /// </summary>
    public GameEntry? FindVisualCard(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var card = _cache.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase) ||
            candidate.Variants.Any(variant =>
                string.Equals(variant.Id, id, StringComparison.OrdinalIgnoreCase)));
        return card is null ? null : OverlayUserPrefs([card])[0];
    }

    /// <summary>Computed source rows behind one visual card, without user art overlays.</summary>
    public IReadOnlyList<GameEntry> FindVisualSources(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return [];
        var card = _cache.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase) ||
            candidate.Variants.Any(variant =>
                string.Equals(variant.Id, id, StringComparison.OrdinalIgnoreCase)));
        if (card is null) return [];
        return card.Variants.Count == 0
            ? [card]
            : card.Variants.Select(variant => MaterializeVariant(card, variant)).ToArray();
    }

    internal IReadOnlyList<GameEntry> AllSourceEntries() => ExpandVariants(_cache);

    /// <summary>
    /// Repaints computed covers when requested, bumps one revision across every
    /// source on the visual card, and publishes the authoritative card. A null
    /// cover is a valid reset result and is never replaced with stale UI state.
    /// </summary>
    public async Task<GameEntry?> PublishArtworkChangeAsync(
        string id,
        bool recomputeComputedCovers,
        CancellationToken ct = default)
    {
        GameEntry? result;
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var baseCard = _cache.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase) ||
                candidate.Variants.Any(variant =>
                    string.Equals(variant.Id, id, StringComparison.OrdinalIgnoreCase)));
            if (baseCard is null) return null;

            if (recomputeComputedCovers)
                _cache = GroupVariants(CoverArtService.WithCovers(ExpandVariants(_cache)));

            var sourceIds = SourceIdsFor(baseCard);
            var revision = Interlocked.Increment(ref _artRevisionSequence);
            foreach (var sourceId in sourceIds) _artRevisions[sourceId] = revision;

            var changedCard = _cache.FirstOrDefault(candidate =>
                sourceIds.Contains(candidate.Id, StringComparer.OrdinalIgnoreCase) ||
                candidate.Variants.Any(variant =>
                    sourceIds.Contains(variant.Id, StringComparer.OrdinalIgnoreCase)));
            result = changedCard is null ? null : OverlayUserPrefs([changedCard])[0];
        }
        finally
        {
            _scanGate.Release();
        }

        try { LibraryUpdated?.Invoke(); } catch { /* UI refresh is best-effort */ }
        return result;
    }

    public void Invalidate() => _cacheAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Drop a title from last-good / in-memory cache so a failed follow-up scan
    /// cannot resurrect it after a successful uninstall.
    /// </summary>
    public void ForgetInstalled(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        foreach (var key in _lastGoodByAdapter.Keys.ToArray())
        {
            _lastGoodByAdapter[key] = _lastGoodByAdapter[key]
                .Where(game => !SameLibraryId(game, gameId))
                .ToList();
        }

        _cache = _cache.Where(game => !SameLibraryId(game, gameId)).ToList();
        _cacheAt = DateTimeOffset.MinValue;
        try { LibraryDiskCache.Save(_cache, CurrentAccountScopes()); }
        catch { /* disk cache is best-effort */ }
    }

    private static bool SameLibraryId(GameEntry game, string gameId) =>
        string.Equals(game.Id, gameId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(game.SelectedVariantId, gameId, StringComparison.OrdinalIgnoreCase) ||
        (game.Variants?.Any(variant =>
            string.Equals(variant.Id, gameId, StringComparison.OrdinalIgnoreCase)) ?? false);

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
            _cache = GroupVariants(CoverArtService.WithCovers(
                EpicPlaytime.Apply(
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

    /// <summary>
    /// Presence and sign-in probes hit the registry, vendor config files, and
    /// Steam's localconfig. library.get, profile.get, and stores.matrix all ask
    /// for this, so a short cache keeps opening a page off the disk. Library
    /// scans and successful store authentication clear it.
    /// </summary>
    public IReadOnlyList<StoreBackendStatus> StoreMatrix()
    {
        lock (_storeMatrixGate)
        {
            if (_storeMatrix is not null &&
                DateTimeOffset.UtcNow - _storeMatrixAtUtc < StoreMatrixTtl)
                return _storeMatrix;
            // Build under the lock so a cold boot's three callers share one
            // registry walk instead of running three in parallel.
            var fresh = BuildStoreMatrix();
            _storeMatrix = fresh;
            _storeMatrixAtUtc = DateTimeOffset.UtcNow;
            return fresh;
        }
    }

    /// <summary>
    /// Refresh only bounded local capability signals. This never authenticates,
    /// starts a client/helper, downloads anything, calls HTTP, or scans a store
    /// library. The bridge runs it on a worker thread.
    /// </summary>
    public StoreLocalCheck CheckStoresLocal()
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        IReadOnlyList<StoreBackendStatus> fresh;
        try
        {
            fresh = BuildStoreMatrix(checkedAtUtc, includeAbsent: true);
            lock (_storeMatrixGate)
            {
                _storeMatrix = fresh.Where(IsVisibleStoreStatus).ToList();
                _storeMatrixAtUtc = checkedAtUtc;
            }
        }
        catch
        {
            return new StoreLocalCheck(
                "failed",
                checkedAtUtc,
                "local_check_failed",
                Array.Empty<StoreLocalCheckItem>());
        }

        var items = fresh.Select(MapLocalCheckItem).ToList();
        var partial = fresh.Any(status => status.checkCode == "probe_failed");
        return new StoreLocalCheck(
            partial ? "partial" : "complete",
            checkedAtUtc,
            partial ? "local_check_partial" : "local_check_complete",
            items);
    }

    /// <summary>
    /// Last built matrix, or null when nothing has been probed yet. Does not
    /// walk the registry. Invalidated by a library scan or successful auth.
    /// </summary>
    public IReadOnlyList<StoreBackendStatus>? PeekStoreMatrix()
    {
        lock (_storeMatrixGate) return _storeMatrix;
    }

    internal void InvalidateStoreMatrix()
    {
        lock (_storeMatrixGate) _storeMatrix = null;
    }

    private IReadOnlyList<StoreBackendStatus> BuildStoreMatrix() =>
        BuildStoreMatrix(DateTimeOffset.UtcNow);

    private IReadOnlyList<StoreBackendStatus> BuildStoreMatrix(
        DateTimeOffset checkedAtUtc,
        bool includeAbsent = false)
    {
        return _adapters.Select(a =>
        {
            try
            {
                var agentPresent = a.IsAgentPresent();
                // The visible vendor client and Exo's headless backend are
                // distinct. Epic/GOG/Amazon adapters intentionally report both
                // through IsAgentPresent, so resolve their actual helper here.
                var clientPresent = a is IStoreClientPresence client
                    ? client.IsClientPresent()
                    : agentPresent;
                var backendPresent = IsStoreBackendPresent(a, clientPresent);
                var signedIn = IsStoreSignedIn(a);
                var cachePresent = HasCachedStore(a.Store);
                var webApiKeyPresent = string.Equals(a.Id, "steam", StringComparison.OrdinalIgnoreCase) &&
                                       SteamWebApiKeyStore.HasKey();
                var localDatabasePresent = string.Equals(a.Id, "gog", StringComparison.OrdinalIgnoreCase) &&
                                           GogGalaxyFriends.DatabasePresent();
                var detail = StoreDetail(a.Id, clientPresent, backendPresent, signedIn, cachePresent);
                return new StoreBackendStatus(
                    a.Id,
                    a.DisplayName,
                    agentPresent,
                    clientPresent,
                    backendPresent,
                    signedIn,
                    cachePresent,
                    webApiKeyPresent,
                    localDatabasePresent,
                    _scanErrors.TryGetValue(a.Id, out _)
                        ? $"{detail} · Scan unavailable"
                        : detail,
                    "checked",
                    checkedAtUtc);
            }
            catch
            {
                return new StoreBackendStatus(
                    a.Id,
                    a.DisplayName,
                    false,
                    false,
                    false,
                    false,
                    HasCachedStore(a.Store),
                    false,
                    false,
                    "Check unavailable",
                    "probe_failed",
                    checkedAtUtc);
            }
        })
        .Where(status => includeAbsent || IsVisibleStoreStatus(status))
        .ToList();
    }

    private static bool IsVisibleStoreStatus(StoreBackendStatus status) =>
        status.agentPresent ||
        status.clientPresent ||
        status.backendPresent ||
        status.signedIn ||
        status.cachePresent ||
        status.checkCode == "probe_failed" ||
        string.Equals(status.store, "local", StringComparison.OrdinalIgnoreCase);

    private static bool IsStoreBackendPresent(IStoreAdapter adapter, bool clientPresent)
    {
        return adapter.Id.ToLowerInvariant() switch
        {
            "epic" => adapter is EpicAdapter && EpicAdapter.ResolveLegendary() is not null,
            "gog" => adapter is GogAdapter && GogAdapter.ResolveGogdl() is not null,
            "amazon" => adapter is AmazonAdapter && AmazonAdapter.IsNilePresent(),
            "steam" or "riot" => clientPresent,
            "local" => true,
            _ => false,
        };
    }

    private static bool IsStoreSignedIn(IStoreAdapter adapter)
    {
        return adapter.Id.ToLowerInvariant() switch
        {
            // Riot keeps its session inside Riot Client and exposes no local
            // account Exo can read, so presence never becomes a session claim.
            "riot" => false,
            // Steam's active local account is the only session signal Exo can
            // read. It proves Exo can act for that account, not that it is online.
            "steam" => adapter.GetType() == typeof(SteamAdapter) && SteamSessionProbe.HasReadableAccount(),
            "epic" => adapter is EpicAdapter && EpicPlaytime.GetActiveAccountScope() is not null,
            "gog" => adapter is GogAdapter && IsGogSignedIn(),
            "amazon" => adapter is AmazonAdapter && NileCli.HasLocalSession(),
            "local" => true,
            _ => false,
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

    private bool HasCachedStore(StoreKind store) => _cache.Any(game =>
        game.Store == store || game.Variants.Any(variant => variant.Store == store));

    private static string StoreDetail(
        string storeId,
        bool clientPresent,
        bool backendPresent,
        bool signedIn,
        bool cachePresent)
    {
        // "Missing" was ambiguous and made an unavailable store look like a
        // transient scan state. This is installation state, not connectivity.
        if (!clientPresent && !backendPresent && !signedIn)
            return cachePresent ? "Cached library" : "Not installed";
        // Steam's resolved local account is a session Exo can read, not an
        // online account, so it never borrows Epic/GOG's "Signed in".
        if (storeId is "steam")
            return signedIn ? "Account ready" : (clientPresent ? "Client present" : "Account found");
        if (signedIn && backendPresent) return "Signed in";
        if (signedIn) return "Session found · Backend not installed";
        if (backendPresent && storeId is "epic" or "gog" or "amazon") return "Sign-in needed";
        // Riot is ready when the client is present; Epic / GOG need Sign in.
        if (storeId is "riot" && clientPresent) return "Client present";
        return "Found";
    }

    private static StoreLocalCheckItem MapLocalCheckItem(StoreBackendStatus status)
    {
        var readiness = StoreReadiness(status);
        return new StoreLocalCheckItem(
            status.store,
            status.clientPresent ? "present" : "missing",
            BackendProbeSupported(status.store)
                ? (status.backendPresent ? "present" : "missing")
                : "unavailable",
            SessionProbeSupported(status.store)
                ? (status.signedIn ? "present" : "missing")
                : "unavailable",
            status.cachePresent ? "present" : "missing",
            readiness,
            status.checkCode == "probe_failed" ? "probe_failed" : ReadinessCode(status, readiness));
    }

    private static bool SessionProbeSupported(string storeId) =>
        storeId is "steam" or "epic" or "gog" or "amazon" or "local";

    private static bool BackendProbeSupported(string storeId) =>
        storeId is "steam" or "epic" or "gog" or "amazon" or "riot" or "local";

    private static string StoreReadiness(StoreBackendStatus status)
    {
        if (status.checkCode == "probe_failed") return "unknown";
        return status.store switch
        {
            "local" => "ready",
            "steam" => status.clientPresent && status.signedIn
                ? "ready"
                : (status.clientPresent || status.cachePresent ? "limited" : "not_detected"),
            "epic" or "gog" or "amazon" => status.backendPresent && status.signedIn
                ? "ready"
                : (status.backendPresent || status.signedIn || status.clientPresent || status.cachePresent
                    ? "limited"
                    : "not_detected"),
            "riot" => status.clientPresent ? "ready" : (status.cachePresent ? "limited" : "not_detected"),
            _ => status.clientPresent || status.cachePresent ? "limited" : "not_detected",
        };
    }

    private static string ReadinessCode(StoreBackendStatus status, string readiness)
    {
        if (readiness == "ready") return "ready";
        if (status.cachePresent && !status.clientPresent && !status.backendPresent) return "cache_only";
        if (status.store is "epic" or "gog" or "amazon")
        {
            if (!status.backendPresent && status.signedIn) return "backend_required";
            if (status.backendPresent && !status.signedIn) return "sign_in_required";
        }
        if (!status.clientPresent && status.store is "steam" or "riot") return "client_required";
        return readiness == "limited" ? "limited" : "not_detected";
    }

    private IReadOnlyList<GameEntry> OverlayUserPrefs(IReadOnlyList<GameEntry> games)
    {
        return games.Select(g =>
        {
            var sourceIds = g.Variants.Count == 0
                ? new[] { g.Id }
                : g.Variants.Select(variant => variant.Id).ToArray();
            var fav = sourceIds.Any(_settings.IsFavorite);
            var customFile = _settings.GetCustomCoverImage(sourceIds);
            var customUrl = _coverImages.ResolveUrl(customFile);
            var artRevision = sourceIds
                .Select(sourceId => _artRevisions.TryGetValue(sourceId, out var revision) ? revision : 0L)
                .Append(g.ArtRevision)
                .Max();
            // Exo stamps a launch against the exact source it launched, so the
            // stamp lands on that variant before the card takes the newest of
            // them. Without this pass a just-launched grouped card kept showing
            // whatever reading it had before the launch, and switching sources in
            // the details overlay showed that stale reading too.
            var variants = new List<GameVariant>(g.Variants.Count);
            var variantsChanged = false;
            foreach (var variant in g.Variants)
            {
                var recorded = _settings.GetLastPlayed(variant.Id);
                if (recorded is not null &&
                    (variant.LastPlayedUtc is null || recorded > variant.LastPlayedUtc))
                {
                    variants.Add(variant with { LastPlayedUtc = recorded });
                    variantsChanged = true;
                }
                else
                {
                    variants.Add(variant);
                }
            }

            var last = sourceIds
                .Select(_settings.GetLastPlayed)
                .Concat(variants.Select(variant => variant.LastPlayedUtc))
                .Append(g.LastPlayedUtc)
                .Where(value => value.HasValue)
                .Max();
            if (!fav && last is null && !g.IsFavorite && !variantsChanged &&
                customUrl is null && artRevision == g.ArtRevision)
                return g;
            return new GameEntry
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
                CoverUrl = customUrl ?? g.CoverUrl,
                CoverSource = customUrl is null ? g.CoverSource : "custom",
                ArtRevision = artRevision,
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
                Variants = variantsChanged ? variants : g.Variants,
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

    internal static IReadOnlyList<string> SourceIdsFor(GameEntry card) =>
        card.Variants.Count == 0
            ? [card.Id]
            : card.Variants.Select(variant => variant.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

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
                EntitlementState = EntitlementState.Unverified,
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

    /// <summary>
    /// Reconciles machine-local Steam rows with the current account's verified
    /// owned-games snapshot. Null is unavailable, not false; an explicit empty
    /// set is a verified exclusion. Uninstalled history is never a library row
    /// unless ownership is currently verified (or restored separately from the
    /// same account's last verified catalog).
    /// </summary>
    internal static IReadOnlyList<GameEntry> ReconcileSteamOwnership(
        IReadOnlyList<GameEntry> entries,
        IReadOnlySet<string>? authoritativeOwnedAppIds)
    {
        var reconciled = new List<GameEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Store != StoreKind.Steam)
            {
                reconciled.Add(entry);
                continue;
            }

            var appId = entry.LaunchTarget;
            if (string.IsNullOrWhiteSpace(appId) &&
                entry.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) &&
                entry.Id.Length > "steam:".Length)
                appId = entry.Id["steam:".Length..];
            var verified = authoritativeOwnedAppIds is not null;
            var owned = verified && !string.IsNullOrWhiteSpace(appId) &&
                        authoritativeOwnedAppIds!.Contains(appId);
            if (!entry.Installed && !owned)
                continue;

            reconciled.Add(new GameEntry
            {
                Id = entry.Id,
                Title = entry.Title,
                Store = entry.Store,
                Installed = entry.Installed,
                Owned = owned,
                EntitlementState = !verified
                    ? EntitlementState.Unverified
                    : owned ? EntitlementState.Owned : EntitlementState.NotOwned,
                UpdateAvailable = owned && entry.UpdateAvailable,
                CanInstall = !entry.Installed && owned,
                Path = entry.Path,
                CoverUrl = entry.CoverUrl,
                CoverSource = entry.CoverSource,
                ArtRevision = entry.ArtRevision,
                PlaytimeMinutes = entry.PlaytimeMinutes,
                SizeBytes = entry.SizeBytes,
                Status = owned
                    ? entry.Status
                    : verified ? "Buy again" : "Ownership unverified",
                Deps = entry.Deps,
                LaunchNote = owned
                    ? "Ownership verified for this Steam account. Launches through Steam."
                    : verified
                        ? "Installed files found, but this Steam account does not currently own the game. Buy it again through Steam."
                        : "Installed files found. Ownership is unverified for the active Steam account.",
                LaunchTarget = entry.LaunchTarget,
                LastPlayedUtc = entry.LastPlayedUtc,
                IsFavorite = entry.IsFavorite,
                CanonicalTitleKey = entry.CanonicalTitleKey,
                SelectedVariantId = entry.SelectedVariantId,
                Variants = entry.Variants,
            });
        }

        return reconciled;
    }

    /// <summary>
    /// Lifetime for a grouped card: each store's own best reading, added up over
    /// the distinct stores behind it. A store counter only ever covers sessions
    /// that store launched, so two different stores never describe the same
    /// hours. Two rows from one store are one history, so the larger reading wins
    /// instead of being added. A store that reports nothing contributes nothing —
    /// never a zero that reads like a fact.
    /// </summary>
    private static int? CombinedPlaytimeMinutes(IReadOnlyList<GameVariant> variants)
    {
        var total = 0L;
        foreach (var store in variants.GroupBy(variant => variant.Store))
        {
            var best = store.Select(variant => variant.PlaytimeMinutes)
                .Where(minutes => minutes is > 0)
                .Max();
            if (best is > 0) total += best.Value;
        }

        return total > 0 ? (int)Math.Min(total, int.MaxValue) : null;
    }

    /// <summary>
    /// Hours for the copy whose source chip is selected. The card total is for
    /// library sort; the overlay names one store and must show that store only.
    /// </summary>
    internal static int? PlaytimeMinutesForSource(GameEntry game, string? sourceId = null)
    {
        if (game.Variants.Count == 0)
            return game.PlaytimeMinutes;

        foreach (var candidate in new[] { sourceId, game.Id, game.SelectedVariantId })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            foreach (var variant in game.Variants)
            {
                if (string.Equals(variant.Id, candidate, StringComparison.OrdinalIgnoreCase))
                    return variant.PlaytimeMinutes;
            }
        }

        return game.Variants[0].PlaytimeMinutes;
    }

    /// <summary>
    /// Last played for a grouped card is the newest session across every store
    /// behind it — the user played this game then, whichever copy started.
    /// </summary>
    private static DateTimeOffset? NewestLastPlayed(IReadOnlyList<GameVariant> variants) =>
        variants.Max(variant => variant.LastPlayedUtc);

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
        EntitlementState = source.EntitlementState,
        UpdateAvailable = source.UpdateAvailable || variants.Any(variant => variant.UpdateAvailable),
        CanInstall = source.CanInstall,
        Path = source.Path,
        CoverUrl = source.CoverUrl,
        CoverSource = source.CoverSource,
        ArtRevision = source.ArtRevision,
        // The card is the game, not the projected source. Showing only the
        // selected store's counter made a two-store card claim one store's hours
        // while its label named both, and hid the sibling that was played last.
        PlaytimeMinutes = CombinedPlaytimeMinutes(variants) ?? source.PlaytimeMinutes,
        SizeBytes = source.SizeBytes,
        Status = source.Status,
        Deps = source.Deps,
        LaunchNote = source.LaunchNote,
        LaunchTarget = source.LaunchTarget,
        LastPlayedUtc = NewestLastPlayed(variants),
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

    private void SeedLastGoodFrom(IReadOnlyList<GameEntry> games)
    {
        foreach (var group in ExpandVariants(games).GroupBy(game => game.Store))
        {
            var adapter = _adapters.FirstOrDefault(candidate => candidate.Store == group.Key);
            if (adapter is null) continue;
            var accountScoped = adapter is IStoreAccountScope;
            var scope = accountScoped ? ((IStoreAccountScope)adapter).GetActiveAccountScope() : null;
            var key = LastGoodKey(adapter.Id, scope, accountScoped);
            if (_lastGoodByAdapter.ContainsKey(key)) continue;
            var rows = accountScoped && string.IsNullOrWhiteSpace(scope)
                ? InstalledWithoutAccountClaims(group.ToList())
                : group.ToList();
            if (rows.Count == 0) continue;
            _lastGoodByAdapter[key] = rows;
        }
    }

    private IEnumerable<GameEntry> PaintedRowsForAdapter(string adapterId)
    {
        var adapter = _adapters.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, adapterId, StringComparison.OrdinalIgnoreCase));
        if (adapter is null || _cache.Count == 0) return Array.Empty<GameEntry>();
        return ExpandVariants(_cache).Where(game => game.Store == adapter.Store);
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
            LaunchNote = "Demo tile. Epic installs via Legendary when present. Epic GUI is optional.",
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
