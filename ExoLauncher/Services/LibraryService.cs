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
    private readonly bool _includeMicrosoftStore;
    private readonly GameCoverImageStore _coverImages = new();
    private readonly SteamOwnershipCatalog _steamOwnershipCatalog;
    private readonly object _storeMatrixGate = new();
    private static readonly TimeSpan StoreMatrixTtl = TimeSpan.FromSeconds(20);
    private IReadOnlyList<StoreBackendStatus>? _storeMatrix;
    private DateTimeOffset _storeMatrixAtUtc;
    private IReadOnlyList<GameEntry> _cache = Array.Empty<GameEntry>();
    private readonly Lazy<Task> _diskSnapshotTask;
    private bool _hasLibrarySnapshot;
    private readonly Dictionary<string, IReadOnlyList<GameEntry>> _lastGoodByAdapter =
        new(StringComparer.OrdinalIgnoreCase);
    // Opaque account scopes are process-local cache keys. They never cross the
    // bridge and are intentionally not persisted in the library model. Read
    // before the scan gate (HaveAccountScopesChanged) while a scan writes it.
    private readonly ConcurrentDictionary<string, string?> _lastAccountScopeByAdapter =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _scanErrors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _targetedScanRevisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<StoreKind, Lazy<Task<IReadOnlyList<GameEntry>>>> _storeRefreshes = new();
    private readonly ConcurrentDictionary<string, long> _adapterReadRevisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AdapterScanResult> _latestAdapterReads = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private DateTimeOffset _cacheAt = DateTimeOffset.MinValue;
    private long _scanGeneration;
    private long _invalidationRevision;
    private long _completedInvalidationRevision;
    private long _artRevisionSequence;
    private readonly ConcurrentDictionary<string, long> _artRevisions =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);
    private readonly LibraryWatchers _watchers = new();
    private int _watchersStarted;
    private readonly ConcurrentDictionary<StoreKind, byte> _dirtyStores = new();
    private int _dirtyAll;
    private int _backgroundScanScheduled;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _coverWarmGate = new();
    private bool _coverWarmRunning;
    private Dictionary<string, GameEntry>? _queuedCoverWarm;
    private IReadOnlyList<GameEntry> _queuedCoverWarmLibrary = Array.Empty<GameEntry>();

    private sealed record AdapterScanResult(
        string Id,
        IReadOnlyList<GameEntry> Items,
        bool Succeeded,
        string? Error,
        bool ProvesInstalledSteamOwnership,
        bool IsAccountScoped,
        string? AccountScope,
        long ElapsedMilliseconds,
        long ReadRevision = 0);

    /// <summary>Raised when the cached library changes or a cover batch lands.</summary>
    public event Action? LibraryUpdated;

    public LibraryService(IReadOnlyList<IStoreAdapter> adapters, SettingsService settings, bool includeMicrosoftStore = true)
        : this(adapters, settings, new SteamOwnershipCatalog(), includeMicrosoftStore)
    {
    }

    internal LibraryService(
        IReadOnlyList<IStoreAdapter> adapters,
        SettingsService settings,
        SteamOwnershipCatalog steamOwnershipCatalog,
        bool includeMicrosoftStore = true)
    {
        _adapters = adapters;
        _settings = settings;
        _includeMicrosoftStore = includeMicrosoftStore;
        _steamOwnershipCatalog = steamOwnershipCatalog;
        // Re-verify catalog titles through the Steam client even after their
        // manifests/cache files are gone, so they are confirmed, not pruned.
        foreach (var steam in adapters.OfType<SteamAdapter>())
            steam.ExtraOwnershipCandidates ??= steamOwnershipCatalog.KnownAppIds;
        _diskSnapshotTask =new Lazy<Task>(() => Task.Run(TryHydrateDiskSnapshot));
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
        _lifetime.Cancel();
        _watchers.Changed -= OnWatchedStoreChanged;
        _watchers.Dispose();
    }

    private void OnWatchedStoreChanged(StoreKind? store)
    {
        if (store is { } changed) _dirtyStores[changed] = 0;
        else Interlocked.Exchange(ref _dirtyAll, 1);
        Interlocked.Increment(ref _invalidationRevision);
        _cacheAt = DateTimeOffset.MinValue;
        ScheduleBackgroundScan();
    }

    /// <summary>Stores whose watched roots changed since the last scan. Null
    /// means every adapter must re-read (an unknown watcher source, or a full
    /// scan that consumes all pending marks).</summary>
    private HashSet<StoreKind>? TakeDirtyStores()
    {
        if (Interlocked.Exchange(ref _dirtyAll, 0) == 1) return null;
        var dirty = new HashSet<StoreKind>();
        foreach (var key in _dirtyStores.Keys)
            if (_dirtyStores.TryRemove(key, out _)) dirty.Add(key);
        return dirty;
    }

    /// <summary>
    /// A forced refresh re-reads only adapters whose watched roots changed since
    /// the last scan. An adapter with no successful read yet has nothing to
    /// reuse, so it is always included.
    /// </summary>
    internal static List<IStoreAdapter> AdaptersToRescan(
        IReadOnlyList<IStoreAdapter> adapters,
        IReadOnlySet<StoreKind> dirtyStores,
        IEnumerable<string> readAdapterIds) =>
        adapters.Where(adapter =>
            dirtyStores.Contains(adapter.Store) || !readAdapterIds.Contains(adapter.Id)).ToList();

    private void TryHydrateDiskSnapshot()
    {
        try
        {
            var disk = LibraryDiskCache.TryLoad(CurrentAccountScopes());
            if (disk is { Count: > 0 })
            {
                _cache = disk;
                SeedLastGoodFrom(disk);
                Volatile.Write(ref _hasLibrarySnapshot, true);
                try { LibraryUpdated?.Invoke(); } catch { /* first paint */ }
                // Disk rows can predate cover files that finished warming after
                // the last save. Resolving every row's local art costs over a
                // second cold, so the saved rows answer the first paint and the
                // refreshed URLs follow as a library update. RefreshCovers never
                // replaces a newer scan's rows.
                _ = Task.Run(() =>
                {
                    try
                    {
                        RefreshCovers();
                        LibraryUpdated?.Invoke();
                    }
                    catch (Exception ex) { AppLog.Debug("Library cover refresh after hydration failed: " + ex.GetType().Name); }
                });
            }
        }
        catch (Exception ex)
        {
            // A malformed/stale paint cache must never prevent the real scan.
            AppLog.Debug("Library disk hydration failed: " + ex.GetType().Name);
        }
    }

    public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync(bool force = false, bool fullScan = false, CancellationToken ct = default)
    {
        // A cold boot can ask from several rooms at once. Share one disk read
        // off the UI thread, and never reload an old disk snapshot after the
        // in-memory library intentionally becomes empty.
        await _diskSnapshotTask.Value.WaitAsync(ct).ConfigureAwait(false);

        // Scope probes read store account files. Ask once before the gate and
        // once after it (a scan that held the gate may have moved the scopes).
        bool? scopesChanged = null;
        bool ScopesChanged() => scopesChanged ??= HaveAccountScopesChanged();

        // Last-good (memory or disk) is enough to answer the UI. A view switch
        // must not wait for every store adapter. Watchers and force=true still
        // run a real scan; account-scope changes never reuse another user's rows.
        if (!force && Volatile.Read(ref _hasLibrarySnapshot) && !ScopesChanged())
        {
            var stale = _cacheAt == DateTimeOffset.MinValue ||
                        DateTimeOffset.UtcNow - _cacheAt >= Freshness;
            if (stale) ScheduleBackgroundScan();
            return OverlayUserPrefs(_cache);
        }

        var observedGeneration = Interlocked.Read(ref _scanGeneration);
        var observedInvalidation = Interlocked.Read(ref _invalidationRevision);
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            scopesChanged = null;
            // A forced refresh means "newer than what I observed", not "run one
            // more scan even if another caller just produced that result". This
            // coalesces the terminal progress/RPC/library-event burst after an
            // install or sync into one store scan.
            if (Interlocked.Read(ref _scanGeneration) != observedGeneration &&
                Interlocked.Read(ref _completedInvalidationRevision) >= observedInvalidation &&
                !ScopesChanged())
                return OverlayUserPrefs(_cache);
            if (!force && Volatile.Read(ref _hasLibrarySnapshot) && DateTimeOffset.UtcNow - _cacheAt < Freshness &&
                !ScopesChanged())
                return OverlayUserPrefs(_cache);
            var scanRevision = Interlocked.Read(ref _invalidationRevision);
            HashSet<StoreKind>? dirty = null;
            if (force && !fullScan && Volatile.Read(ref _hasLibrarySnapshot) && !ScopesChanged())
            {
                dirty = TakeDirtyStores();
                if (dirty is { Count: 0 } && _adapters.All(adapter => _latestAdapterReads.ContainsKey(adapter.Id)))
                {
                    // No watched store changed and every adapter already has a
                    // read to reuse. The cache is verified as of now without
                    // waking a single store adapter.
                    _cacheAt = DateTimeOffset.UtcNow;
                    Interlocked.Exchange(ref _completedInvalidationRevision, scanRevision);
                    Interlocked.Increment(ref _scanGeneration);
                    return OverlayUserPrefs(_cache);
                }
            }
            else if (force)
            {
                // A full scan satisfies every pending dirty mark.
                Interlocked.Exchange(ref _dirtyAll, 0);
                _dirtyStores.Clear();
            }
            // Only a user-requested full scan refetches GetOwnedGames. Watcher
            // scans fire on every appmanifest write while Steam downloads.
            if (force && fullScan) SteamWebApi.InvalidateOwnedGames();
            var result = await ScanLibraryAsync(force, ct, dirtyStores: dirty).ConfigureAwait(false);
            Interlocked.Exchange(ref _completedInvalidationRevision, scanRevision);
            if (Interlocked.Read(ref _invalidationRevision) != scanRevision)
                _cacheAt = DateTimeOffset.MinValue;
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
            var completed = false;
            try
            {
                await GetLibraryAsync(force: true).ConfigureAwait(false);
                completed = true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Background library scan failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundScanScheduled, 0);
                // A manifest may change after its adapter has already read it.
                // Run once more instead of marking that newer event as scanned.
                if (completed && Interlocked.Read(ref _completedInvalidationRevision) <
                    Interlocked.Read(ref _invalidationRevision))
                    ScheduleBackgroundScan();
            }
        });
    }

    private async Task<AdapterScanResult> ScanAdapterAsync(IStoreAdapter adapter, CancellationToken ct)
    {
        var revision = _adapterReadRevisions.AddOrUpdate(adapter.Id, 1, (_, value) => value + 1);
        var result = (await ReadAdapterAsync(adapter, ct).ConfigureAwait(false)) with { ReadRevision = revision };
        if (result.Succeeded)
            _latestAdapterReads.AddOrUpdate(adapter.Id, result, (_, previous) =>
                previous.ReadRevision > result.ReadRevision ? previous : result);
        return result;
    }

    private Task<AdapterScanResult> ReadAdapterAsync(IStoreAdapter adapter, CancellationToken ct) =>
        Task.Run(async () =>
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
                    // WaitAsync is the hard deadline: adapters that read the
                    // registry or disk before their first await (or never await)
                    // ignore the token, and one stuck read held the whole scan.
                    var token = timeout.Token;
                    var items = await Task.Run(() => adapter.GetLibraryAsync(token), token)
                        .WaitAsync(token).ConfigureAwait(false);
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
            }, ct);

    /// <summary>
    /// One cover warm runs at a time. Targets that arrive meanwhile merge into a
    /// single queued pass (latest row per id wins), so a burst of store refreshes
    /// cannot stack whole-library warms.
    /// </summary>
    private void QueueLibraryCoverWarm(IReadOnlyList<GameEntry> targets, IReadOnlyList<GameEntry> activeLibrary)
    {
        lock (_coverWarmGate)
        {
            _queuedCoverWarm ??= new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets) _queuedCoverWarm[target.Id] = target;
            // Cache maintenance protects the whole library, not just the targets.
            _queuedCoverWarmLibrary = activeLibrary;
            if (_coverWarmRunning) return;
            _coverWarmRunning = true;
        }
        _ = RunLibraryCoverWarmsAsync();
    }

    private async Task RunLibraryCoverWarmsAsync()
    {
        while (true)
        {
            GameEntry[] targets;
            IReadOnlyList<GameEntry> activeLibrary;
            lock (_coverWarmGate)
            {
                if (_queuedCoverWarm is not { Count: > 0 } || _lifetime.IsCancellationRequested)
                {
                    _queuedCoverWarm = null;
                    _coverWarmRunning = false;
                    return;
                }
                targets = _queuedCoverWarm.Values.ToArray();
                activeLibrary = _queuedCoverWarmLibrary;
                _queuedCoverWarm = null;
            }
            try
            {
                await CoverArtService.WarmCacheAsync(targets,
                    () => { if (!_lifetime.IsCancellationRequested) OnCoverWarmBatch(); },
                    requested: false, deferForFirstPaint: true, activeLibrary: activeLibrary,
                    cancellationToken: _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { AppLog.Warn("Cover warm failed: " + ex.Message); }
        }
    }

    private void OnCoverWarmBatch()
    {
        // Re-expand first. WithCover used to clone the grouped card and
        // drop Variants, which hid Epic/GOG hours on a Steam-preferred tile.
        RefreshCovers();
        try { LibraryUpdated?.Invoke(); } catch { /* ignore */ }
    }

    private async Task<IReadOnlyList<GameEntry>> ScanLibraryAsync(bool force, CancellationToken ct,
        IReadOnlyList<AdapterScanResult>? precomputed = null, IReadOnlySet<StoreKind>? dirtyStores = null)
    {
        var scanStopwatch = Stopwatch.StartNew();
        var discovered = new List<GameEntry>();
        // Several adapters still do registry and directory traversal before
        // returning their first incomplete Task. Run each adapter entry point on
        // the pool so a cold scan cannot synchronously stall the WebView thread.
        var tasks = precomputed is null
            ? (dirtyStores is null ? _adapters
                : AdaptersToRescan(_adapters, dirtyStores, _latestAdapterReads.Keys))
                    .Select(adapter => ScanAdapterAsync(adapter, ct)).ToArray()
            : [];

        var completed = precomputed ?? await Task.WhenAll(tasks).ConfigureAwait(false);
        var scanned = completed.Select(result => result.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (precomputed is null && dirtyStores is not null)
        {
            // Adapters left out of this forced scan contribute their most recent
            // successful read, which flows through the same merge and last-good
            // handling as a fresh result.
            completed = completed.Concat(_adapters
                .Where(adapter => !scanned.Contains(adapter.Id))
                .Select(adapter => _latestAdapterReads.TryGetValue(adapter.Id, out var read) ? read : null)
                .OfType<AdapterScanResult>()).ToArray();
        }
        var results = completed.Select(result =>
        {
            if (!_latestAdapterReads.TryGetValue(result.Id, out var newer) || newer.ReadRevision <= result.ReadRevision) return result;
            var adapter = _adapters.First(candidate => candidate.Id == result.Id);
            return adapter is IStoreAccountScope scoped &&
                !string.Equals(newer.AccountScope, scoped.GetActiveAccountScope(), StringComparison.Ordinal) ? result : newer;
        }).ToArray();
        if (precomputed is not null)
        {
            foreach (var other in _adapters.Where(adapter => !scanned.Contains(adapter.Id)))
            {
                var painted = PaintedRowsForAdapter(other.Id).ToArray();
                if (other is IStoreAccountScope scoped &&
                    (!_lastAccountScopeByAdapter.TryGetValue(other.Id, out var previous) ||
                     !string.Equals(previous, scoped.GetActiveAccountScope(), StringComparison.Ordinal)))
                    discovered.AddRange(InstalledWithoutAccountClaims(painted));
                else discovered.AddRange(painted);
            }
        }
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
                // The change this read was for is still unread: the next forced scan reads the store again
                // instead of reusing its older successful read.
                if (_adapters.FirstOrDefault(adapter => adapter.Id == result.Id) is { } failed)
                    _dirtyStores[failed.Store] = 0;
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
        // Current is a detached settings snapshot; capture the flag once rather
        // than cloning all preferences for every discovered game.
        var showLibraryTools = _settings.Current.ShowLibraryTools;
        discovered = discovered
            .Where(g => !IsTestPollution(g))
            .Where(g => !IsNonGameTitle(g))
            .Where(g => CatalogHygiene.KeepLibraryEntry(g, showLibraryTools))
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
            // Catalog rows carry only a title; current appinfo supplies the type
            // and parent that keep non-games off the owned-not-installed shelf.
            var steamAppInfo = _adapters
                .Where(adapter => string.Equals(adapter.Id, steamResult.Id, StringComparison.Ordinal))
                .OfType<SteamAdapter>()
                .Select(adapter => adapter.LastAppInfo)
                .FirstOrDefault();
            var manifestProvenSteamGames = steamResult.Items
                .Where(game => !IsTestPollution(game))
                .Where(game => !IsNonGameTitle(game))
                .Where(game => CatalogHygiene.KeepLibraryEntry(game, showLibraryTools))
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
                        steamResult.AccountScope!, discovered, authoritativeSteamOwned, steamAppInfo)
                    .Where(game => CatalogHygiene.KeepLibraryEntry(game, showLibraryTools)));
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
                        "legacy-unscoped", discovered, authoritativeSteamOwned, steamAppInfo)
                    .Where(game => CatalogHygiene.KeepLibraryEntry(game, showLibraryTools)));
            }
        }

        // Dedupe by id
        var byId = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in discovered)
            byId[g.Id] = g;

        var ordered = byId.Values
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Store playtime for every title (Steam VDF and Web API, GOG, Epic), else Galaxy, else a tracker.
        ordered = PlaytimeService.Enrich(ordered, SteamAccountMinutes()).ToList();
        ordered = EpicPlaytime.Apply(ordered, EpicPlaytime.GetCachedMinutes()).ToList();
        EpicPlaytime.RefreshCachedMinutes();

        // Keep source rows intact through every enrichment step, then project
        // exact-title matches into one card at the boundary consumed by the UI.
        // The projected card still uses one real source as its top-level entry;
        // it is never a synthetic launch target or a sum of store statistics.
        // A scan is the moment an install or sign-in could have changed.
        InvalidateStoreMatrix();
        // A single-store refresh re-covers and re-warms only that store's rows;
        // every other row is the painted card, which already carries its cover.
        var refreshedStores = precomputed is null
            ? null
            : _adapters.Where(adapter => scanned.Contains(adapter.Id)).Select(adapter => adapter.Store).ToHashSet();
        var deduplicated = CatalogHygiene.DeduplicateById(ordered);
        _cache = GroupVariants(refreshedStores is null
            ? CoverArtService.WithCovers(deduplicated)
            : deduplicated.Select(game => refreshedStores.Contains(game.Store) ? CoverArtService.WithCover(game) : game).ToList());
        // Fire once immediately so UI gets CDN URLs, then again as disk art lands.
        try { LibraryUpdated?.Invoke(); } catch { /* ignore */ }
        // Every real library title may fill its portrait cache in the background;
        // installed and pinned titles are also eligible for missing wide art.
        var warmLibrary = OverlayUserPrefs(_cache)
            .Where(CoverArtService.ShouldWarmLibraryCover)
            .ToArray();
        // A broad scan is never a user-requested cover operation. Queue the
        // warm at background priority and let the scan return immediately;
        // search keeps the requested/high-priority path for its visible results.
        QueueLibraryCoverWarm(
            refreshedStores is null
                ? warmLibrary
                : warmLibrary.Where(card => refreshedStores.Contains(card.Store) ||
                    card.Variants.Any(variant => refreshedStores.Contains(variant.Store))).ToArray(),
            warmLibrary);
        if (precomputed is null) _cacheAt = DateTimeOffset.UtcNow;
        Volatile.Write(ref _hasLibrarySnapshot, true);
        scanStopwatch.Stop();
        var adapterTimings = string.Join(' ', results.Select(result => scanned.Contains(result.Id)
            ? $"{result.Id}:{result.ElapsedMilliseconds}ms/{result.Items.Count}/{(result.Succeeded ? "ok" : "failed")}"
            : $"{result.Id}:reused"));
        AppLog.Info(
            $"PERF library-scan totalMs={scanStopwatch.ElapsedMilliseconds} force={force.ToString().ToLowerInvariant()} adapters=\"{adapterTimings}\"");
        try { LibraryDiskCache.Save(_cache, CurrentAccountScopes()); }
        catch { /* disk cache is best-effort */ }
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
        if (hit is not null) return OverlayUserPrefs(new[] { hit }, selectPreferredSource: false)[0];

        // A variant id remains a valid bridge/action id even though it is no
        // longer a separate visible card. Resolve it back to an exact source
        // row; do not route its action through the preferred sibling store.
        var grouped = _cache.FirstOrDefault(card => card.Variants.Any(variant =>
            string.Equals(variant.Id, id, StringComparison.OrdinalIgnoreCase)));
        if (grouped is null) return null;
        var selected = grouped.Variants.First(variant =>
            string.Equals(variant.Id, id, StringComparison.OrdinalIgnoreCase));
        return OverlayUserPrefs(new[] { MaterializeVariant(grouped, selected) }, selectPreferredSource: false)[0];
    }

    /// <summary>
    /// Reads only the action's store before returning an exact current-account
    /// source. Queued mutations use this immediately before adapter work so an
    /// account switch or refund cannot inherit the authority of the old row.
    /// </summary>
    internal Task<IReadOnlyList<GameEntry>> RefreshStoreAsync(StoreKind store, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Lazy<Task<IReadOnlyList<GameEntry>>>? candidate = null;
        candidate = new(() => RefreshStoreCoreAsync(store, candidate!));
        return _storeRefreshes.GetOrAdd(store, candidate).Value.WaitAsync(ct);
    }

    private async Task<IReadOnlyList<GameEntry>> RefreshStoreCoreAsync(StoreKind store,
        Lazy<Task<IReadOnlyList<GameEntry>>> owner)
    {
        var entered = false;
        try
        {
            var ct = _lifetime.Token;
            await _diskSnapshotTask.Value.WaitAsync(ct).ConfigureAwait(false);
            await _scanGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            var adapter = _adapters.FirstOrDefault(candidate => candidate.Store == store);
            if (adapter is null) return PeekCachedLibrary();
            if (store == StoreKind.Steam) SteamWebApi.InvalidateOwnedGames();
            var result = await ScanAdapterAsync(adapter, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return await ScanLibraryAsync(force: false, ct, new[] { result }).ConfigureAwait(false);
        }
        finally
        {
            if (entered) _scanGate.Release();
            _storeRefreshes.TryRemove(new KeyValuePair<StoreKind, Lazy<Task<IReadOnlyList<GameEntry>>>>(store, owner));
        }
    }

    internal async Task<GameEntry?> RevalidateActionGameAsync(
        string id,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        await _diskSnapshotTask.Value.WaitAsync(ct).ConfigureAwait(false);
        var expectedStore = Find(id)?.Store;
        var adapter = expectedStore is null
            ? null
            : _adapters.FirstOrDefault(candidate => candidate.Store == expectedStore.Value);
        if (adapter is null) return null;
        var revision = _targetedScanRevisions.AddOrUpdate(adapter.Id, 1, (_, value) => value + 1);
        var fullScan = Interlocked.Read(ref _scanGeneration);
        var invalidation = Interlocked.Read(ref _invalidationRevision);
        if (adapter.Store == StoreKind.Steam) SteamWebApi.InvalidateOwnedGames();
        var result = await ScanAdapterAsync(adapter, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!result.Succeeded || adapter is IStoreAccountScope account &&
            !string.Equals(result.AccountScope, account.GetActiveAccountScope(), StringComparison.Ordinal)) return null;
        // Publication can wait behind an existing broad scan. Authorization
        // uses this fresh source directly, so an unrelated store cannot delay it.
        _ = PublishTargetedScanAsync(adapter, result, revision, fullScan, invalidation);
        var showLibraryTools = _settings.Current.ShowLibraryTools;
        bool Keep(GameEntry row) => !IsTestPollution(row) && !IsNonGameTitle(row) &&
                                    CatalogHygiene.KeepLibraryEntry(row, showLibraryTools);
        bool Match(GameEntry row) => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase);
        var current = result.Items.FirstOrDefault(row => Match(row) && Keep(row));
        if (current is null && result.ProvesInstalledSteamOwnership && !string.IsNullOrWhiteSpace(result.AccountScope))
        {
            // Owned-not-installed Steam rows usually come from the verified
            // ownership catalog, not the adapter (see ScanLibraryAsync). Rebuild
            // them the same way, against this fresh ownership snapshot, or every
            // queued Steam install looks unowned and is cancelled.
            var owned = (adapter as IAuthoritativeOwnershipSource)?.LastAuthoritativeOwnedAppIds;
            var appInfo = (adapter as SteamAdapter)?.LastAppInfo;
            current = _steamOwnershipCatalog
                .RestoreMissing(result.AccountScope!, result.Items.Where(Keep).ToList(), owned, appInfo)
                .FirstOrDefault(row => Match(row) && Keep(row));
        }
        if (current is null) return null;
        return OverlayUserPrefs([current], selectPreferredSource: false)[0];
    }

    private async Task PublishTargetedScanAsync(IStoreAdapter adapter, AdapterScanResult result,
        long revision, long fullScan, long invalidation)
    {
        await _scanGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_targetedScanRevisions.GetValueOrDefault(adapter.Id) != revision ||
                Interlocked.Read(ref _scanGeneration) != fullScan ||
                Interlocked.Read(ref _invalidationRevision) != invalidation ||
                adapter is IStoreAccountScope account &&
                !string.Equals(result.AccountScope, account.GetActiveAccountScope(), StringComparison.Ordinal)) return;
            await ScanLibraryAsync(force: false, CancellationToken.None, new[] { result }).ConfigureAwait(false);
        }
        catch (Exception ex) { AppLog.Debug("Targeted library publication failed: " + ex.Message); }
        finally { _scanGate.Release(); }
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
        return card is null ? null : OverlayUserPrefs(new[] { card })[0];
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
            {
                // A user cover operation touches one visual card. Keep every
                // unrelated row and its computed art without another disk pass.
                var refreshed = GroupVariants(CoverArtService.WithCovers(ExpandVariants(new[] { baseCard })));
                _cache = _cache.SelectMany(card => ReferenceEquals(card, baseCard)
                    ? refreshed : new[] { card }).ToArray();
            }

            var sourceIds = SourceIdsFor(baseCard);
            var revision = Interlocked.Increment(ref _artRevisionSequence);
            foreach (var sourceId in sourceIds) _artRevisions[sourceId] = revision;

            var changedCard = _cache.FirstOrDefault(candidate =>
                sourceIds.Contains(candidate.Id, StringComparer.OrdinalIgnoreCase) ||
                candidate.Variants.Any(variant =>
                    sourceIds.Contains(variant.Id, StringComparer.OrdinalIgnoreCase)));
            result = changedCard is null ? null : OverlayUserPrefs(new[] { changedCard })[0];
        }
        finally
        {
            _scanGate.Release();
        }

        try { LibraryUpdated?.Invoke(); } catch { /* UI refresh is best-effort */ }
        return result;
    }

    /// <summary>Something outside the watchers changed the library (install,
    /// uninstall, account work). The next forced scan re-reads every adapter.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _invalidationRevision);
        Interlocked.Exchange(ref _dirtyAll, 1);
        _cacheAt = DateTimeOffset.MinValue;
    }

    /// <summary>Re-apply cover URLs after cache warm without full rediscovery.</summary>
    public IReadOnlyList<GameEntry> RefreshCovers()
    {
        var snapshot = _cache;
        if (snapshot.Count == 0) return snapshot;
        var refreshed = GroupVariants(CoverArtService.WithCovers(ExpandVariants(snapshot)));
        // Warming runs outside the scan gate (and may be called while it is
        // held). Never replace a newer scan or artwork publication with this
        // older snapshot; its next warm callback will apply the cached cover.
        Interlocked.CompareExchange(ref _cache, refreshed, snapshot);
        return OverlayUserPrefs(_cache);
    }

    /// <summary>Steam Web API playtime_forever from the last Steam scan; null when unavailable.</summary>
    private IReadOnlyDictionary<string, int>? SteamAccountMinutes() => _adapters
        .OfType<IOwnedPlaytimeSource>()
        .Select(adapter => adapter.LastOwnedPlaytimeMinutes)
        .FirstOrDefault(minutes => minutes is { Count: > 0 });

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
                    PlaytimeService.Enrich(ExpandVariants(_cache), SteamAccountMinutes()),
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
                var webApiKeyPresent = a.Id.ToLowerInvariant() switch
                {
                    "steam" => SteamWebApiKeyStore.HasKey(),
                    "itch" => ItchApiKeyStore.HasKey(),
                    _ => false,
                };
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
                    "Check unavailable",
                    "probe_failed",
                    checkedAtUtc);
            }
        })
        // Adapter fixtures can opt out of this additional machine-level probe.
        .Concat(_includeMicrosoftStore ? [MicrosoftStoreStatus(checkedAtUtc)] : [])
        .Where(status => includeAbsent || IsVisibleStoreStatus(status))
        .ToList();
    }

    private static StoreBackendStatus MicrosoftStoreStatus(DateTimeOffset checkedAtUtc)
    {
        var present = false;
        try
        {
            using var packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            present = packages?.GetSubKeyNames().Any(name => name.StartsWith("Microsoft.WindowsStore_", StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch { }
        return new StoreBackendStatus("microsoft-store", "Microsoft Store", present, present, false,
            false, false, false, present ? "Client present" : "Not installed", "checked", checkedAtUtc);
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
            "riot" => adapter is RiotAdapter && LocalStoreSessions.HasSession("riot"),
            "itch" => adapter is ItchAdapter && ItchApiKeyStore.HasKey(),
            "battlenet" or "ea" or "ubisoft" or "xbox" or "minecraft"
                or "paradox" or "rockstar" or "roblox" or "wargaming" =>
                adapter is AgentPresentAdapterBase && LocalStoreSessions.HasSession(adapter.Id),
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

    // The same sign-in GOG uses everywhere, so a sign-out in Exo is not undone by Heroic's.
    private static bool IsGogSignedIn() => GogAuthService.FindExistingAuthConfigPath() is not null;

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

    private IReadOnlyList<GameEntry> OverlayUserPrefs(IReadOnlyList<GameEntry> games, bool selectPreferredSource = true)
    {
        var preferences = _settings.GetLibraryPreferences();
        List<GameEntry>? updated = null;
        for (var index = 0; index < games.Count; index++)
        {
            var next = Apply(games[index]);
            if (ReferenceEquals(next, games[index])) continue;
            updated ??= new List<GameEntry>(games);
            updated[index] = next;
        }
        return updated ?? games;

        GameEntry Apply(GameEntry g)
        {
            if (selectPreferredSource) g = SelectPreferredSource(g, preferences.PreferredGameSources);
            string? customFile = null;
            var artRevision = g.ArtRevision;
            var last = g.LastPlayedUtc;
            void ReadSource(string id)
            {
                if (customFile is null && preferences.CustomCovers.TryGetValue(id, out var name)) customFile = name;
                if (_artRevisions.TryGetValue(id, out var revision)) artRevision = Math.Max(artRevision, revision);
                if (preferences.LastPlayed.TryGetValue(id, out var played) && (last is null || played > last)) last = played;
            }
            if (g.Variants.Count == 0) ReadSource(g.Id);
            // Exo stamps a launch against the exact source it launched, so the
            // stamp lands on that variant before the card takes the newest of
            // them. Without this pass a just-launched grouped card kept showing
            // whatever reading it had before the launch, and switching sources in
            // the details overlay showed that stale reading too.
            List<GameVariant>? variants = null;
            for (var index = 0; index < g.Variants.Count; index++)
            {
                var variant = g.Variants[index];
                ReadSource(variant.Id);
                if (variant.LastPlayedUtc is { } variantLast && (last is null || variantLast > last)) last = variantLast;
                if (preferences.LastPlayed.TryGetValue(variant.Id, out var recorded) &&
                    (variant.LastPlayedUtc is null || recorded > variant.LastPlayedUtc))
                {
                    variants ??= new List<GameVariant>(g.Variants);
                    variants[index] = variant with { LastPlayedUtc = recorded };
                }
            }
            var customUrl = _coverImages.ResolveUrl(customFile);
            if (last == g.LastPlayedUtc && variants is null &&
                (customUrl is null || customUrl == g.CoverUrl && g.CoverSource == "custom") && artRevision == g.ArtRevision)
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
                FamilyShared = g.FamilyShared,
                PlaytimeMinutes = g.PlaytimeMinutes,
                SizeBytes = g.SizeBytes,
                Status = g.Status,
                Deps = g.Deps,
                LaunchNote = g.LaunchNote,
                LaunchTarget = g.LaunchTarget,
                LastPlayedUtc = last,
                CanonicalTitleKey = g.CanonicalTitleKey,
                SelectedVariantId = g.SelectedVariantId,
                Variants = variants ?? g.Variants,
            };
        }
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
                        variants)
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
            var owned = entry.FamilyShared ||
                        (verified && !string.IsNullOrWhiteSpace(appId) &&
                         authoritativeOwnedAppIds!.Contains(appId));
            if (!entry.Installed && !owned)
                continue;

            reconciled.Add(new GameEntry
            {
                Id = entry.Id,
                Title = entry.Title,
                Store = entry.Store,
                Installed = entry.Installed,
                Owned = owned,
                FamilyShared = entry.FamilyShared,
                EntitlementState = !verified && !entry.FamilyShared
                    ? EntitlementState.Unverified
                    : owned ? EntitlementState.Owned : EntitlementState.NotOwned,
                // Unverified (Steam closed, no Web API key) is not "not owned": the update stays.
                UpdateAvailable = (owned || !verified) && entry.UpdateAvailable,
                // A finished preload installs itself on release day; Steam refuses an install before.
                CanInstall = !entry.Installed && owned && entry.Status != "Preloaded",
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
                LaunchNote = entry.FamilyShared
                    ? entry.LaunchNote
                    : owned
                        ? "Ownership verified for this Steam account. Launches through Steam."
                        : verified
                            ? "Installed files found, but this Steam account does not currently own the game. Buy it again through Steam."
                            : "Installed files found. Ownership is unverified for the active Steam account.",
                LaunchTarget = entry.LaunchTarget,
                // Hygiene reads both: without the type an owned or family
                // title that is not installed (or still downloading) drops.
                ParentAppId = entry.ParentAppId,
                SteamAppType = entry.SteamAppType,
                LastPlayedUtc = entry.LastPlayedUtc,
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
        var groups = variants.GroupBy(variant => variant.Store).ToList();
        // Riot rows carry tracker.gg hours: the player's account-wide total, not one
        // store's ledger. A card that also has Steam/Epic/etc. must not add them on.
        var official = groups.Where(store => store.Key != StoreKind.Riot).ToList();
        var chosen = official.Count > 0 ? official : groups;
        var total = 0L;
        foreach (var store in chosen)
        {
            var best = store.Select(variant => variant.PlaytimeMinutes)
                .Where(minutes => minutes is > 0)
                .Max();
            if (best is not > 0) continue;
            // Tracker-only: every store row carries the same account-wide total, so it counts once.
            total = official.Count > 0 ? total + best.Value : Math.Max(total, best.Value);
        }

        return total > 0 ? (int)Math.Min(total, int.MaxValue) : null;
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
        IReadOnlyList<GameVariant> variants) => new()
    {
        Id = source.Id,
        Title = source.Title,
        Store = source.Store,
        Installed = source.Installed,
        Owned = source.Owned,
        EntitlementState = source.EntitlementState,
        // Store actions must reflect this source. Sibling update indicators stay
        // on their variants and must never turn this source's Play into Update.
        UpdateAvailable = source.UpdateAvailable,
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
        CanonicalTitleKey = canonicalTitleKey,
        SelectedVariantId = source.Id,
        Variants = variants,
    };

    internal static GameEntry SelectPreferredSource(GameEntry card, IReadOnlyDictionary<string, string> preferences)
    {
        if (card.Variants.Count < 2 || string.IsNullOrEmpty(card.CanonicalTitleKey) ||
            !preferences.TryGetValue(card.CanonicalTitleKey, out var id)) return card;
        var selected = card.Variants.FirstOrDefault(variant =>
            variant.Installed && string.Equals(variant.Id, id, StringComparison.OrdinalIgnoreCase));
        // A remembered launcher only selects an installed copy. If it was
        // uninstalled, retain the current library projection's installed source.
        if (selected is null || string.Equals(selected.Id, card.Id, StringComparison.OrdinalIgnoreCase)) return card;
        return CopyWithVariants(MaterializeVariant(card, selected), card.CanonicalTitleKey,
            card.Variants);
    }

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
