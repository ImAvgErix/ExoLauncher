using System.Diagnostics;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed class AppServices
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _deferredServicesStarted;
    public SettingsService Settings { get; } = new();
    public DependencyService Dependencies { get; } = new();
    public AppUpdateService Updater { get; } = new();
    public HiddenStoreRuntime HiddenStores { get; } = new();
    public AchievementService Achievements { get; } = new();
    public GogAuthService GogAuth { get; } = new();
    public EpicAuthService EpicAuth { get; } = new();
    public GogOwnedLibraryService GogOwnedLibrary { get; } = new();
    /// <summary>The game sheet's Shaders line, read from the Exo Shaders engine.</summary>
    internal ScsKillerStatus Shaders { get; } = new();
    /// <summary>The Store room: deals, prices, free games and the wishlist.</summary>
    internal StoreService Store => _store.Value;
    /// <summary>Settings > Downloads applied: holds while playing and outside the download hours, idle auto-updates.</summary>
    internal DownloadPolicy Downloads => _downloads ??= new DownloadPolicy(this);
    private DownloadPolicy? _downloads;
    private readonly Lazy<StoreService> _store = new(() => new StoreService(RunningVersion));
    /// <summary>HID devices for the Devices room's mouse module.</summary>
    internal Hid.HidService Hid => _hid.Value;
    private readonly Lazy<Hid.HidService> _hid = new(() => new Hid.HidService());

    private readonly Lazy<IReadOnlyList<IStoreAdapter>> _adapters;
    private readonly Lazy<LibraryService> _library;
    private readonly Lazy<LaunchOrchestrator> _launcher;
    private readonly Lazy<GameArtworkService> _artwork;

    public IReadOnlyList<IStoreAdapter> Adapters => _adapters.Value;
    public LibraryService Library => _library.Value;
    public LaunchOrchestrator Launcher => _launcher.Value;
    public GameArtworkService Artwork => _artwork.Value;
    public AppServices()
    {
        _adapters = new Lazy<IReadOnlyList<IStoreAdapter>>(BuildAdapters);
        _library = new Lazy<LibraryService>(BuildLibrary);
        _artwork = new Lazy<GameArtworkService>(() => new GameArtworkService(Library, Settings));
        _launcher = new Lazy<LaunchOrchestrator>(BuildLauncher);
    }

    public void Initialize()
    {
        Settings.Load();
        // Always overwrite settings.json version with the running build (not a stale 1.0.0).
        Settings.SyncAppVersion(AppVersion);

        // Adapters, Library, and Launcher are created on first access so the
        // shell can paint before any official-client probing runs.
        AppLog.Info($"Exo Launcher {AppVersion} services initialized; adapters will load on first use.");
    }

    private IReadOnlyList<IStoreAdapter> BuildAdapters()
    {
        if (PackagedSmoke.Enabled) return [new LocalAdapter(Settings)];
        var sw = Stopwatch.StartNew();
        var factories = new (string Name, Func<IStoreAdapter> Factory)[]
        {
            ("steam", () => new SteamAdapter()),
            ("epic", () => new EpicAdapter(EpicAuth)),
            ("gog", () => new GogAdapter(GogAuth, GogOwnedLibrary)),
            ("riot", () => new RiotAdapter()),
            ("xbox", () => new XboxAdapter()),
            ("ea", () => new EaAdapter()),
            ("ubisoft", () => new UbisoftAdapter()),
            ("battlenet", () => new BattleNetAdapter()),
            ("amazon", () => new AmazonAdapter()),
            ("rockstar", () => new RockstarAdapter()),
            ("itch", () => new ItchAdapter()),
            ("minecraft", () => new MinecraftAdapter()),
            ("roblox", () => new RobloxAdapter()),
            ("paradox", () => new ParadoxAdapter()),
            ("wargaming", () => new WargamingAdapter()),
            ("local", () => new LocalAdapter(Settings)),
        };

        var built = new List<IStoreAdapter>(factories.Length);
        var perAdapter = new List<(string Name, double Ms)>();
        foreach (var (name, factory) in factories)
        {
            var t = Stopwatch.StartNew();
            try
            {
                built.Add(factory());
                perAdapter.Add((name, t.Elapsed.TotalMilliseconds));
            }
            catch (Exception ex)
            {
                // A failing adapter constructor must not take down the rest of
                // the store list. Log it and continue with a null-safe gap.
                AppLog.Warn($"Adapter '{name}' constructor failed: {ex.GetType().Name}: {ex.Message}");
                perAdapter.Add((name, t.Elapsed.TotalMilliseconds));
            }
        }

        sw.Stop();
        // Report the slowest three and the total so startup audit is concrete.
        var topSlow = perAdapter.OrderByDescending(x => x.Ms).Take(3)
            .Select(x => $"{x.Name}={x.Ms:F1}ms")
            .ToList();
        AppLog.Info($"Built {built.Count} store adapters in {sw.Elapsed.TotalMilliseconds:F1}ms; slowest: {string.Join(", ", topSlow)}");

        // Wire cross-adapter cache events now that the instances exist.
        GogOwnedLibrary.CacheUpdated += OnGogOwnedLibraryUpdated;
        EpicAdapter.OwnedLibraryCacheUpdated += OnEpicOwnedLibraryUpdated;
        EpicPlaytime.CachedMinutesUpdated += OnEpicPlaytimeUpdated;
        SteamClientReady.ClientReady += OnSteamClientReady;

        return built;
    }

    private LibraryService BuildLibrary()
    {
        var lib = new LibraryService(Adapters, Settings);
        return lib;
    }

    private LaunchOrchestrator BuildLauncher()
    {
        var launcher = new LaunchOrchestrator(
            Adapters,
            Settings,
            Dependencies,
            Achievements,
            revalidateQueuedGame: Library.RevalidateActionGameAsync);
        return launcher;
    }

    /// <summary>
    /// Starts background-only process and filesystem observation after the main
    /// shell has painted. Neither service is required to construct the first
    /// library response, and both remain in-process for the rest of the run.
    /// </summary>
    public void StartDeferredServices()
    {
        if (PackagedSmoke.Enabled) return;
        if (Interlocked.Exchange(ref _deferredServicesStarted, 1) != 0) return;
        Shaders.Start();
        _ = Task.Run(() =>
        {
            try { LucidCompanion.KeepVoiceOff(Settings); }
            catch (Exception ex) { Helpers.AppLog.Warn("Lucid: keeping Lucid Voice off failed: " + ex.GetType().Name); }
            try { LucidCompanion.StartWithExo(Settings); }
            catch (Exception ex) { Helpers.AppLog.Warn("Lucid: start with Exo failed: " + ex.GetType().Name); }
        });
        Store.Start();
        Downloads.Start();
        // Exo's power plan: switched to by itself on Windows' own plans, refreshed while it's on (PowerPlan.Auto).
        _ = Task.Run(() =>
        {
            try { if (PowerPlan.Shared.Auto(PcHealth.ThisPcProfile()) is { } why) AppLog.Info("Power plan: " + why); }
            catch (Exception ex) { AppLog.Warn("Power plan: " + ex.Message); }
        });
        RadeonProfileService.Shared.Start(Launcher, () => GpuProfileService.MainScreen("amd"));
        IntelProfileService.Shared.Start(Launcher, () => GpuProfileService.MainScreen("intel"));
        _ = Task.Run(async () =>
        {
            // Saved downloads resume once the library can answer for them: a
            // resume checks the game against the current library first.
            try
            {
                for (var waited = 0; waited < 60 && Library.PeekCachedLibrary().Count == 0; waited++)
                    await Task.Delay(1000, _lifetime.Token).ConfigureAwait(false);
                await Task.Delay(2000, _lifetime.Token).ConfigureAwait(false);
                Launcher.ResumeAfterRestart();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Warn("Resume after restart failed: " + ex.Message); }
        }, _lifetime.Token);
        _ = Task.Run(async () =>
        {
            // Each migration runs on its own; one failing must not skip the rest.
            try
            {
                await Achievements.InitializeAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception ex) { AppLog.Debug("Deferred achievements init failed: " + ex.GetType().Name); }
            if (_lifetime.IsCancellationRequested) return;
            try { LegacyProfileDataCleanup.Run(); }
            catch (Exception ex) { AppLog.Debug("Legacy data cleanup failed: " + ex.GetType().Name); }
            try { MouseBridgeRetirement.Run(Settings); }
            catch (Exception ex) { AppLog.Warn("OpenMouse bridge retirement failed: " + ex.GetType().Name); }
            try { Settings.HandStartupToExo(); }
            catch (Exception ex) { AppLog.Warn("Startup hand-off failed: " + ex.GetType().Name); }
            try
            {
                await ExoScheduledTaskCleanup.RemoveLeftoversAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { AppLog.Debug("Deferred state migration failed: " + ex.GetType().Name); }
        }, _lifetime.Token);
        _ = Task.Run(() =>
        {
            try
            {
                if (_lifetime.IsCancellationRequested) return;
                Library.StartWatchers();
                if (_lifetime.IsCancellationRequested) return;
                HiddenStores.Start();
                AppLog.Info("Deferred store observers ready.");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Deferred store observers failed: " + ex.GetType().Name);
            }
        }, _lifetime.Token);
    }

    public void Shutdown()
    {
        try { _lifetime.Cancel(); } catch { }
        PlaytimeService.FlushActiveSessions();
        LucidCompanion.HideTray(false);
        Shaders.Dispose();
        if (_store.IsValueCreated) _store.Value.Dispose();
        if (_hid.IsValueCreated) _hid.Value.Dispose();
        _downloads?.Dispose();
        Achievements.Dispose();
        HiddenStores.Dispose();
        GogAuth.Dispose();
        EpicAuth.Dispose();
        EpicPlaytime.CachedMinutesUpdated -= OnEpicPlaytimeUpdated;
        EpicAdapter.OwnedLibraryCacheUpdated -= OnEpicOwnedLibraryUpdated;
        SteamClientReady.ClientReady -= OnSteamClientReady;
        GogOwnedLibrary.CacheUpdated -= OnGogOwnedLibraryUpdated;
        GogOwnedLibrary.Dispose();
        if (_library.IsValueCreated)
            Library.DisposeWatchers();
    }

    private void OnGogOwnedLibraryUpdated() => RefreshOwnedStore(Models.StoreKind.Gog);

    private void RefreshOwnedStore(Models.StoreKind store)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Library.RefreshStoreAsync(store, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { AppLog.Debug($"{store} owned-library cache refresh failed: {ex.Message}"); }
        });
    }

    private void OnEpicPlaytimeUpdated()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Playtime is a derived overlay. Repaint it without re-running
                // any store scan or blocking the startup library response.
                await Library.RefreshDerivedStateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Epic playtime derived refresh failed: " + ex.Message);
            }
        });
    }

    private void OnEpicOwnedLibraryUpdated() => RefreshOwnedStore(Models.StoreKind.Epic);

    /// <summary>A Steam client that was still signing in now answers ownership.</summary>
    private void OnSteamClientReady()
    {
        foreach (var steam in Adapters.OfType<SteamAdapter>())
            steam.InvalidateClientOwnership();
        RefreshOwnedStore(Models.StoreKind.Steam);
    }

    private static readonly Lazy<string> s_appVersion = new(ReadAppVersion);

    /// <summary>The running build's version, read once per process.</summary>
    internal static string RunningVersion => s_appVersion.Value;

    public string AppVersion => s_appVersion.Value;

    private static string ReadAppVersion()
    {
        try
        {
            // Prefer VERSION file next to exe / project
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "VERSION"),
                Path.Combine(AppContext.BaseDirectory, "..", "VERSION"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "VERSION"),
            };
            foreach (var c in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(c);
                    if (File.Exists(full))
                    {
                        var t = File.ReadAllText(full).Trim();
                        if (!string.IsNullOrWhiteSpace(t)) return t;
                    }
                }
                catch { /* */ }
            }

            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    var info = FileVersionInfo.GetVersionInfo(exe);
                    var product = info.ProductVersion?.Trim();
                    if (!string.IsNullOrWhiteSpace(product))
                        return product.Split('+', 2)[0].Split('-', 2)[0].Trim();
                }
            }
            catch { /* fall through */ }

            var v = typeof(AppServices).Assembly.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return "1.0.0"; }
    }

    public IStoreAdapter? FindAdapter(StoreKind store) =>
        Adapters.FirstOrDefault(a => a.Store == store);

    public IStoreAdapter? FindAdapterById(string id) =>
        Adapters.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}
