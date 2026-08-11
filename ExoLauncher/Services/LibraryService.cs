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
        if (!force && _cache.Count > 0 && DateTimeOffset.UtcNow - _cacheAt < Freshness)
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
            if (!force && _cache.Count > 0 && DateTimeOffset.UtcNow - _cacheAt < Freshness)
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
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(25));
                    var items = await adapter.GetLibraryAsync(timeout.Token).ConfigureAwait(false);
                    return new AdapterScanResult(
                        adapter.Id,
                        items,
                        true,
                        null,
                        adapter.Store == StoreKind.Steam && adapter is IInstalledSteamManifestSource,
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
                        adapterStopwatch.ElapsedMilliseconds);
                }
            }, ct))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var result in results)
        {
            if (result.Succeeded)
            {
                _scanErrors.TryRemove(result.Id, out _);
                _lastGoodByAdapter[result.Id] = result.Items;
                discovered.AddRange(result.Items);
            }
            else
            {
                _scanErrors[result.Id] = result.Error ?? "Unknown scan error";
                AppLog.Warn($"Library scan failed for {result.Id}: {result.Error}");
                if (_lastGoodByAdapter.TryGetValue(result.Id, out var lastGood))
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
        var manifestProvenSteamGames = results
            .Where(result => result.Succeeded && result.ProvesInstalledSteamOwnership)
            .SelectMany(result => result.Items)
            .Where(game => !IsTestPollution(game))
            .Where(game => !IsNonGameTitle(game))
            .ToList();
        _steamOwnershipCatalog.RememberInstalled(manifestProvenSteamGames);
        discovered.AddRange(_steamOwnershipCatalog.RestoreMissing(discovered));

        // Dedupe by id
        var byId = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in discovered)
            byId[g.Id] = g;

        var ordered = byId.Values
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Store + Exo-tracked playtime for every title (Steam VDF, GOG JSON, sessions).
        ordered = PlaytimeService.Enrich(ordered).ToList();

        _cache = CoverArtService.WithCovers(ordered);
        // Fire once immediately so UI gets CDN URLs, then again as disk art lands.
        try { LibraryUpdated?.Invoke(); } catch { /* ignore */ }
        // Warm what can actually appear in the installed/favorites library.
        // Search requests warm their own result on demand; prefetching every
        // uninstalled Epic entitlement caused a large startup burst.
        var warmTargets = OverlayUserPrefs(_cache)
            .Where(game => game.Installed || game.IsFavorite)
            .ToArray();
        _ = CoverArtService.WarmCacheAsync(
            warmTargets,
            () =>
            {
                _cache = CoverArtService.WithCovers(_cache);
                try { LibraryUpdated?.Invoke(); } catch { /* ignore */ }
            },
            deferForFirstPaint: true);
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
        if (hit is null) return null;
        return OverlayUserPrefs(new[] { hit })[0];
    }

    public void Invalidate() => _cacheAt = DateTimeOffset.MinValue;

    /// <summary>Re-apply cover URLs after cache warm without full rediscovery.</summary>
    public IReadOnlyList<GameEntry> RefreshCovers()
    {
        if (_cache.Count == 0) return _cache;
        _cache = CoverArtService.WithCovers(_cache);
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
            _cache = CoverArtService.WithCovers(EpicPlaytime.Apply(
                PlaytimeService.Enrich(_cache),
                EpicPlaytime.GetCachedMinutes()));
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
            var signedIn = IsStoreSignedIn(a.Id, agentPresent);
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
        if (!present) return "Not installed";
        if (signedIn) return "Connected";
        // Steam / Riot are ready when the client is present; Epic / GOG need Connect.
        if (storeId is "steam" or "riot") return "Client present";
        return "Found";
    }

    private IReadOnlyList<GameEntry> OverlayUserPrefs(IReadOnlyList<GameEntry> games)
    {
        return games.Select(g =>
        {
            var fav = _settings.IsFavorite(g.Id);
            var last = _settings.GetLastPlayed(g.Id) ?? g.LastPlayedUtc;
            if (!fav && last is null && !g.IsFavorite && g.LastPlayedUtc is null)
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
            };
        }).ToList();
    }

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
