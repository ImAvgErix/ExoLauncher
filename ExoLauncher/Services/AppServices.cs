using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed class AppServices
{
    public SettingsService Settings { get; } = new();
    public LibraryService Library { get; private set; } = null!;
    public LaunchOrchestrator Launcher { get; private set; } = null!;
    public DependencyService Dependencies { get; } = new();
    public AppUpdateService Updater { get; } = new();
    public StoreSearchService StoreSearch { get; } = new();
    public HiddenStoreRuntime HiddenStores { get; } = new();
    public AchievementService Achievements { get; } = new();
    private readonly AchievementIconCache _achievementIconCache = new();
    public TrophyNotificationService TrophyNotifications { get; private set; } = null!;
    public GogAuthService GogAuth { get; } = new();
    public GogOwnedLibraryService GogOwnedLibrary { get; } = new();
    public IReadOnlyList<IStoreAdapter> Adapters { get; private set; } = Array.Empty<IStoreAdapter>();

    public void Initialize()
    {
        LegacyProfileDataCleanup.Run();
        Settings.Load();
        TrophyNotifications = new TrophyNotificationService(Settings);
        Achievements.NotificationDeliveryRequested += OnAchievementNotificationDeliveryRequested;
        // Always overwrite settings.json version with the running build (not a stale 1.0.0).
        Settings.SyncAppVersion(AppVersion);
        // Local is a first-class DRM-free backend. Phase-2 agent adapters stay unwired
        // until they can report owned titles and launch/install outcomes honestly.
        Adapters =
        [
            new SteamAdapter(),
            new EpicAdapter(),
            new GogAdapter(GogAuth, GogOwnedLibrary),
            new RiotAdapter(),
            new LocalAdapter(Settings),
        ];
        Library = new LibraryService(Adapters, Settings);
        GogOwnedLibrary.CacheUpdated += OnGogOwnedLibraryUpdated;
        EpicPlaytime.CachedMinutesUpdated += OnEpicPlaytimeUpdated;
        Launcher = new LaunchOrchestrator(Adapters, Settings, Dependencies, Achievements);
        HiddenStores.Start();
        AppLog.Info($"Exo Launcher {AppVersion} services ready · {Adapters.Count} adapters");
    }

    public void Shutdown()
    {
        PlaytimeService.FlushActiveSessions();
        Achievements.NotificationDeliveryRequested -= OnAchievementNotificationDeliveryRequested;
        Achievements.Dispose();
        HiddenStores.Dispose();
        GogAuth.Dispose();
        EpicPlaytime.CachedMinutesUpdated -= OnEpicPlaytimeUpdated;
        GogOwnedLibrary.CacheUpdated -= OnGogOwnedLibraryUpdated;
        GogOwnedLibrary.Dispose();
    }

    /// <summary>
    /// Replays a crash-surviving notification only after refreshing the title
    /// and proving that its provider/source/account still match the outbox
    /// record. It intentionally never sends a raw persisted payload to the UI.
    /// </summary>
    public async Task ReplayPendingAchievementNotificationsAsync()
    {
        var pending = Achievements.GetPendingNotificationDeliveries();
        if (pending.Count == 0) return;

        try
        {
            var library = await Library.GetLibraryAsync().ConfigureAwait(false);
            foreach (var gameId in pending.Select(delivery => delivery.Unlock.GameId)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var game = library.FirstOrDefault(entry =>
                    string.Equals(entry.Id, gameId, StringComparison.OrdinalIgnoreCase));
                if (game is null) continue;
                try { _ = await Achievements.RefreshAsync(game).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    AppLog.Debug("Pending achievement notification replay failed: " + ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Pending achievement notification replay could not start: " + ex.GetType().Name);
        }
    }

    private void OnAchievementNotificationDeliveryRequested(AchievementNotificationDelivery delivery)
    {
        _ = PresentAchievementNotificationAsync(delivery);
    }

    private async Task PresentAchievementNotificationAsync(AchievementNotificationDelivery delivery)
    {
        var unlock = delivery.Unlock;
        var gameTitle = Library?.Find(unlock.GameId)?.Title ?? "Game trophy";
        var definition = unlock.Entry.Definition;
        var iconUrl = definition.IconUnlockedUrl;
        try
        {
            // A toast waits briefly for a bounded, validated local copy. This
            // keeps the real provider art visible even if its CDN is busy when
            // the notification reaches the screen.
            iconUrl = await _achievementIconCache.CacheAsync(iconUrl).ConfigureAwait(false) ?? iconUrl;
        }
        catch (Exception ex)
        {
            // Network/cache failure must never suppress the durable toast.
            // The presenter can still use the source URL or its local mark.
            AppLog.Debug("Achievement icon preparation failed: " + ex.GetType().Name);
        }

        TrophyNotifications.Notify(new TrophyNotificationPayload(
            GameTitle: gameTitle,
            AchievementName: definition.Name,
            Detail: string.IsNullOrWhiteSpace(definition.Description)
                ? gameTitle
                : definition.Description,
            IsRare: definition.GlobalUnlockPercent is <= 10,
            IsPerfect: unlock.IsPerfected,
            IconUrl: iconUrl,
            Rarity: TrophyRarityResolver.Resolve(definition, unlock.IsPerfected),
            RarityPercent: definition.GlobalUnlockPercent),
            onPresented: () =>
            {
                try
                {
                    _ = Achievements.AcknowledgeNotificationDelivery(delivery.DeliveryId);
                }
                catch (Exception ex)
                {
                    // Keep the outbox item durable when an acknowledgement
                    // write fails; the next verified account refresh retries it.
                    AppLog.Debug("Achievement delivery acknowledgement failed: " + ex.GetType().Name);
                }
            });
    }

    private void OnGogOwnedLibraryUpdated()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Join any scan that caused the background refresh, then force
                // one cache-only GOG pass so the UI sees the durable result.
                var current = await Library.GetLibraryAsync().ConfigureAwait(false);
                var currentGogIds = current
                    .Where(game => game.Store == StoreKind.Gog)
                    .Select(game => game.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var expectedGogIds = current
                    .Where(game => game.Store == StoreKind.Gog && game.Installed)
                    .Select(game => game.Id)
                    .Concat(GogOwnedLibrary.LastVisibleProductIds.Select(id => "gog:" + id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (currentGogIds.SetEquals(expectedGogIds))
                    return;
                Library.Invalidate();
                _ = await Library.GetLibraryAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Debug("GOG library cache reload failed: " + ex.Message);
            }
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

    public string AppVersion
    {
        get
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

                var v = typeof(AppServices).Assembly.GetName().Version;
                return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { return "1.0.0"; }
        }
    }

    public IStoreAdapter? FindAdapter(StoreKind store) =>
        Adapters.FirstOrDefault(a => a.Store == store);

    public IStoreAdapter? FindAdapterById(string id) =>
        Adapters.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}
