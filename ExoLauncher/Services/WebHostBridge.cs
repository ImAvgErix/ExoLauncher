using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

/// <summary>
/// JSON-RPC bridge between the React UI (WebView2) and native services.
/// UI owns pixels; host owns discovery, install/progress, launch, and deps.
/// </summary>
public sealed class WebHostBridge
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _queue;
    private CoreWebView2? _web;
    private CancellationTokenSource? _searchCts;
    private readonly object _searchCoverWarmGate = new();
    private readonly HashSet<string> _searchCoverWarmKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly DlssSwapService _dlss = new();
    private readonly StoreMetadataService _metadata = new();
    private readonly SocialService _social;
    private readonly ExoSessionStore _sessionStore;
    private readonly ExoProfileMediaCache _onlineMediaCache;
    private readonly ExoIdentityLifecycle _identityLifecycle;
    private readonly ExoAccountService _account;
    private readonly ExoOnlineClient _online;
    private readonly ExoPresenceClient? _presence;
    private readonly ConcurrentDictionary<string, ExoProfileMediaMetadata> _onlineMedia = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _accountCts = new();
    private readonly object _presenceActivityGate = new();
    private string? _presenceActivityKey;
    private int _presenceStarting;
    private bool _detached;
    private int _libraryPushScheduled;
    private int _libraryPushQueued;
    private long _libraryPushedAtMs;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public WebHostBridge(AppServices services, DispatcherQueue queue)
    {
        _services = services;
        _queue = queue;
        _social = new SocialService(services.Library, services.Settings);
        _sessionStore = new ExoSessionStore();
        _onlineMediaCache = new ExoProfileMediaCache();
        var onlineCache = new ExoOnlineCache();
        _identityLifecycle = new ExoIdentityLifecycle(
            _sessionStore,
            onlineCache,
            _onlineMediaCache);
        _account = new ExoAccountService(
            _sessionStore,
            CreateIdentityHandler(),
            ExoAccountService.OpenSystemBrowser,
            ExoLoopbackListener.Start,
            origin: null,
            clearOnlineState: () =>
            {
                onlineCache.Clear();
                _onlineMediaCache.Clear();
            },
            lifecycle: _identityLifecycle);
        _online = new ExoOnlineClient(
            _sessionStore,
            CreateIdentityHandler(),
            onlineCache,
            origin: null,
            storeTokens: new NativeStoreTokenSource(),
            mediaCache: _onlineMediaCache,
            lifecycle: _identityLifecycle);
        Uri? presenceUri = null;
        try { presenceUri = ExoIdContract.ResolvePresenceSocketUri(); }
        catch { /* Invalid online configuration must not block launcher startup. */ }
        if (presenceUri is not null)
        {
            _presence = new ExoPresenceClient(presenceUri);
            _presence.MessageReceived += OnPresenceMessage;
        }
        var bridgeSession = new ExoBridgeSessionCoordinator(
            clearMappedMedia: _onlineMedia.Clear,
            stopPresenceAsync: StopPresenceAsync,
            signedOutAccount: () => _account.SignedOutState,
            profileSnapshot: () => MapProfile(_social.Profile(RunningLibraryGame())),
            publishEvent: PostEvent);
        _identityLifecycle.SetSignedOutObserver(bridgeSession.CompleteSignedOutAsync);
        _services.Launcher.ProgressChanged += OnProgress;
        _services.Launcher.GameSessionCompleted += OnGameSessionCompleted;
        _services.Library.LibraryUpdated += OnLibraryUpdated;
        _services.Achievements.SnapshotUpdated += OnAchievementSnapshotUpdated;
    }

    public void Attach(CoreWebView2 web)
    {
        _services.GogAuth.AttachDispatcher(_queue);
        try
        {
            Directory.CreateDirectory(Path.Combine(PathHelper.AppDataDir, ExoProfileMediaCache.DirectoryName));
        }
        catch { /* Online media remains unavailable; the launcher still starts. */ }
        _web = web;
        web.WebMessageReceived += OnMessage;
        _ = StartPresenceIfSignedInAsync();
    }

    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        if (_web is not null)
        {
            try { _web.WebMessageReceived -= OnMessage; } catch { }
        }
        _web = null;
        _services.Launcher.ProgressChanged -= OnProgress;
        _services.Launcher.GameSessionCompleted -= OnGameSessionCompleted;
        _services.Library.LibraryUpdated -= OnLibraryUpdated;
        _services.Achievements.SnapshotUpdated -= OnAchievementSnapshotUpdated;
        var search = Interlocked.Exchange(ref _searchCts, null);
        try { search?.Cancel(); } catch { }
        search?.Dispose();
        try { _accountCts.Cancel(); } catch { }
        if (_presence is not null)
            _presence.MessageReceived -= OnPresenceMessage;
        _onlineMedia.Clear();
        _ = DisposePresenceAsync();
        try { _online.Dispose(); } catch { }
        try { _account.Dispose(); } catch { }
        try { _accountCts.Dispose(); } catch { }
    }

    private void OnProgress(InstallProgress p) =>
        PostEvent("install.progress", MapProgress(p));

    private void OnGameSessionCompleted(GameEntry game)
    {
        PostEvent("launch.status", new
        {
            gameId = game.Id,
            ok = true,
            message = "Game closed.",
            phase = "stopped",
        });
        QueuePresenceFromLibrary();
    }

    private void OnAchievementSnapshotUpdated(AchievementSnapshot snapshot) =>
        PostEvent("achievements.updated", MapAchievementSnapshot(snapshot, includeEntries: true));

    private void OnLibraryUpdated()
    {
        QueuePresenceFromLibrary();
        // Cover warm used to fire this once per downloaded poster, each time
        // mapping and serialising the whole library on the UI thread. Throttle
        // to one push per 80 ms; a trailing pass still lands the last cover.
        Interlocked.Exchange(ref _libraryPushQueued, 1);
        if (Interlocked.CompareExchange(ref _libraryPushScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _libraryPushQueued, 0);
                    var wait = 80 - (Environment.TickCount64 - Interlocked.Read(ref _libraryPushedAtMs));
                    if (wait > 0 && wait < 1_000)
                        await Task.Delay((int)wait).ConfigureAwait(false);
                    if (_detached) return;
                    Interlocked.Exchange(ref _libraryPushedAtMs, Environment.TickCount64);
                    try
                    {
                        // LibraryService.OnWarmBatch already reapplies cache URLs
                        // before it raises LibraryUpdated. Publish that snapshot;
                        // do not regroup the entire library a second time here.
                        var games = _services.Library.PeekCachedLibrary();
                        PostEvent("library.updated", new
                        {
                            games = games.Select(game => MapGame(game)).ToList(),
                            count = games.Count,
                        });
                    }
                    catch (Exception ex)
                    {
                        AppLog.Debug("library.updated failed: " + ex.Message);
                    }
                }
                while (!_detached && Interlocked.CompareExchange(ref _libraryPushQueued, 0, 1) == 1);
            }
            finally
            {
                Interlocked.Exchange(ref _libraryPushScheduled, 0);
                if (!_detached && Volatile.Read(ref _libraryPushQueued) != 0)
                    OnLibraryUpdated();
            }
        });
    }

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!WebViewTrustPolicy.IsTrustedAppUri(e.Source))
        {
            AppLog.Warn("Blocked a privileged WebView message from an untrusted origin.");
            return;
        }

        string? raw = null;
        try { raw = e.TryGetWebMessageAsString(); } catch { }
        if (string.IsNullOrWhiteSpace(raw))
        {
            try { raw = e.WebMessageAsJson; } catch { return; }
        }
        if (string.IsNullOrWhiteSpace(raw)) return;
        _ = HandleAsync(raw);
    }

    private async Task HandleAsync(string raw)
    {
        string? id = null;
        string? method = null;
        try
        {
            if (!ExoBridgeProtocol.TryParseRequest(raw, out var request))
                return;
            id = request.Id;
            method = request.Method;
            var paramsEl = request.Params;
            var hasParams = request.HasParams;

            object? result = method switch
            {
                "library.get" => await LibraryGetAsync(paramsEl, hasParams).ConfigureAwait(false),
                "library.refresh" => await LibraryGetAsync(paramsEl, hasParams: true, force: true).ConfigureAwait(false),
                "game.get" => await GameGetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.launch" => await GameLaunchAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.stop" => await GameStopAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.install" => await GameInstallAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.update" => await GameUpdateAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.uninstall" => await GameUninstallAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.repair" => await GameRepairAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.extras" => GameExtras(paramsEl, hasParams),
                "game.metadata" => await GameMetadataAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.openFolder" => GameOpenFolder(paramsEl, hasParams),
                "game.toggleFavorite" => GameToggleFavorite(paramsEl, hasParams),
                "art.replace" => await ArtworkReplaceAsync(paramsEl, hasParams).ConfigureAwait(true),
                "art.reset" => await ArtworkResetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "art.refetch" => await ArtworkRefetchAsync(paramsEl, hasParams).ConfigureAwait(true),
                "art.report" => await ArtworkReportAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.cancelInstall" => _services.Launcher.Cancel(),
                "game.progress" => GameProgress(paramsEl, hasParams),
                "achievements.get" => AchievementGet(paramsEl, hasParams),
                "achievements.refresh" => await AchievementRefreshAsync(paramsEl, hasParams).ConfigureAwait(true),
                "stores.auth" => await StoresAuthAsync(paramsEl, hasParams).ConfigureAwait(true),
                "friends.list" => await FriendsListAsync(paramsEl, hasParams).ConfigureAwait(true),
                "friends.roster" => FriendsRoster(),
                "friends.add" => FriendsAdd(paramsEl, hasParams),
                "friends.remove" => FriendsRemove(paramsEl, hasParams),
                "friends.setNote" => FriendsSetNote(paramsEl, hasParams),
                "friends.link" => FriendsLink(paramsEl, hasParams),
                "friends.unlink" => FriendsUnlink(paramsEl, hasParams),
                "friends.steamLibrary" => await FriendsSteamLibraryAsync(paramsEl, hasParams).ConfigureAwait(true),
                "profile.get" => ProfileGet(),
                "profile.set" => ProfileSet(paramsEl, hasParams),
                "profile.setLook" => ProfileSetLook(paramsEl, hasParams),
                "profile.setShowcase" => ProfileSetShowcase(paramsEl, hasParams),
                "profile.pickImage" => await ProfilePickImageAsync(paramsEl, hasParams).ConfigureAwait(true),
                "profile.clearImage" => ProfileClearImage(paramsEl, hasParams),
                "account.get" => await AccountGetAsync().ConfigureAwait(true),
                "account.signIn" => await AccountSignInAsync(paramsEl, hasParams).ConfigureAwait(true),
                "account.createPassword" => await AccountCreatePasswordAsync(paramsEl, hasParams).ConfigureAwait(true),
                "account.signInPassword" => await AccountPasswordSignInAsync(paramsEl, hasParams).ConfigureAwait(true),
                "account.signOut" => await AccountSignOutAsync().ConfigureAwait(true),
                "account.reserveHandle" => await AccountReserveHandleAsync(paramsEl, hasParams).ConfigureAwait(true),
                "account.getProfile" => await _account.GetProfileAsync(_services.Settings, _accountCts.Token).ConfigureAwait(true),
                "account.setProfile" => await AccountSetProfileAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.health" => await _online.GetHealthAsync(_accountCts.Token).ConfigureAwait(true),
                "online.profiles.get" => await OnlineProfileGetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.profiles.search" => await OnlineProfilesSearchAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.profiles.share" => OnlineProfileShare(paramsEl, hasParams),
                "online.badges.get" => await _online.GetManagedBadgesAsync(
                    ReadString(paramsEl, hasParams, "handle"), _accountCts.Token).ConfigureAwait(true),
                "online.badges.grant" => await _online.GrantManagedBadgeAsync(
                    ReadString(paramsEl, hasParams, "handle"),
                    ReadString(paramsEl, hasParams, "badge"),
                    _accountCts.Token).ConfigureAwait(true),
                "online.badges.revoke" => await _online.RevokeManagedBadgeAsync(
                    ReadString(paramsEl, hasParams, "handle"),
                    ReadString(paramsEl, hasParams, "badge"),
                    _accountCts.Token).ConfigureAwait(true),
                "online.privacy.get" => await _online.GetPrivacyAsync(_accountCts.Token).ConfigureAwait(true),
                "online.privacy.set" => await OnlinePrivacySetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.friends.list" => await OnlineFriendsListAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.friends.requests" => await OnlineFriendRequestsAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.friends.request" => await _online.SendFriendRequestAsync(
                    ReadString(paramsEl, hasParams, "handle"), _accountCts.Token).ConfigureAwait(true),
                "online.friends.accept" => await _online.AcceptFriendRequestAsync(
                    ReadString(paramsEl, hasParams, "requestId"), _accountCts.Token).ConfigureAwait(true),
                "online.friends.decline" => await _online.DeclineFriendRequestAsync(
                    ReadString(paramsEl, hasParams, "requestId"), _accountCts.Token).ConfigureAwait(true),
                "online.friends.remove" => await _online.RemoveFriendAsync(
                    ReadString(paramsEl, hasParams, "userId"), _accountCts.Token).ConfigureAwait(true),
                "online.blocks.list" => await OnlineBlocksListAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.blocks.block" => await _online.BlockAsync(
                    ReadString(paramsEl, hasParams, "userId"), _accountCts.Token).ConfigureAwait(true),
                "online.blocks.unblock" => await _online.UnblockAsync(
                    ReadString(paramsEl, hasParams, "userId"), _accountCts.Token).ConfigureAwait(true),
                "online.links.get" => await OnlineLinksGetAsync().ConfigureAwait(true),
                "online.links.discovery" => await _online.SetDiscoveryAsync(
                    ReadBool(paramsEl, hasParams, "enabled") == true, _accountCts.Token).ConfigureAwait(true),
                "online.links.link" => await OnlineLinkStoreAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.links.unlink" => await OnlineUnlinkStoreAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.links.match" => await OnlineMatchStoreAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.sessions.list" => await _online.GetSessionsAsync(_accountCts.Token).ConfigureAwait(true),
                "online.sessions.revoke" => await OnlineRevokeSessionAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.sessions.revokeAll" => await OnlineRevokeAllSessionsAsync().ConfigureAwait(true),
                "online.account.export" => await OnlineExportAccountAsync().ConfigureAwait(true),
                "online.account.delete" => await OnlineDeleteAccountAsync().ConfigureAwait(true),
                "online.media.upload" => await OnlineUploadMediaAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.media.delete" => await _online.DeleteProfileMediaAsync(
                    ReadString(paramsEl, hasParams, "kind"), _accountCts.Token).ConfigureAwait(true),
                "online.media.download" => await OnlineDownloadMediaAsync(paramsEl, hasParams).ConfigureAwait(true),
                "online.presence.get" => await _online.GetPresenceAsync(
                    ReadInt(paramsEl, hasParams, "limit") ?? 50, _accountCts.Token).ConfigureAwait(true),
                "stores.search" => await StoresSearchAsync(paramsEl, hasParams).ConfigureAwait(true),
                "deps.list" => DepsList(),
                "deps.offerInstall" => DepsOfferInstall(paramsEl, hasParams),
                "stores.check" => await StoresCheckAsync().ConfigureAwait(false),
                "stores.matrix" => StoreMatrixWithLayers(),
                "settings.get" => BuildSettings(),
                "settings.set" => SetSettings(paramsEl, hasParams),
                "trophies.preview" => PreviewTrophyNotification(),
                "shell.minimize" => HideToNotificationArea(),
                "shell.maximize" => ToggleMaximize(),
                "shell.windowState" => WindowState(),
                "shell.close" => CloseWindow(),
                "shell.openUrl" => OpenUrl(paramsEl, hasParams),
                "shell.openPath" => OpenPath(paramsEl, hasParams),
                "shell.showStore" => await ShowStoreAsync(paramsEl, hasParams).ConfigureAwait(true),
                "shell.pickFolder" => await PickFolderAsync(paramsEl, hasParams).ConfigureAwait(true),
                "app.version" => new { version = _services.AppVersion },
                "app.checkUpdate" => await CheckUpdateAsync().ConfigureAwait(true),
                "app.installUpdate" => await InstallUpdateAsync().ConfigureAwait(true),
                "dlss.status" => await DlssStatusAsync(paramsEl, hasParams).ConfigureAwait(true),
                "dlss.updateAll" => await DlssUpdateAllAsync(paramsEl, hasParams).ConfigureAwait(true),
                "dlss.restore" => await DlssRestoreAsync(paramsEl, hasParams).ConfigureAwait(true),
                _ => throw new InvalidOperationException($"Unknown method: {method}")
            };

            PostResponse(id!, ok: true, result: result);
        }
        catch (Exception ex)
        {
            var passwordRequest = method is "account.createPassword" or "account.signInPassword";
            AppLog.Warn(passwordRequest ? $"RPC {id}: password account request failed." : $"RPC {id}: {ex.Message}");
            if (id is not null)
                PostResponse(id, ok: false, error: passwordRequest ? "The account request did not complete." : ex.Message);
        }
    }

    private async Task<object> LibraryGetAsync(JsonElement p, bool hasParams, bool force = false)
    {
        if (hasParams && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True)
            force = true;

        var games = await _services.Library.GetLibraryAsync(force).ConfigureAwait(false);
        var settings = _services.Settings.Current;
        var mapped = await Task.Run(() => games.Select(game => MapGame(game)).ToList()).ConfigureAwait(false);
        return new
        {
            games = mapped,
            count = games.Count,
            stores = MapStoreMatrix(
                _services.Library.PeekStoreMatrix() ?? Array.Empty<LibraryService.StoreBackendStatus>()),
            progress = MapProgress(_services.Launcher.CurrentProgress),
            queuedGameIds = _services.Launcher.QueuedGameIds,
            sortMode = settings.SortMode,
            favorites = settings.Favorites,
            recent = settings.Recent,
        };
    }

    private async Task<object> GameGetAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
        }
        if (game is null)
            return new { ok = false, message = "Game not found." };

        return new
        {
            ok = true,
            game = MapGame(game, discoverExternalRunningGame: true),
            metadata = MapMetadata(_metadata.Peek(game)),
        };
    }

    /// <summary>
    /// Catalog text for the opened card. Kept off <c>library.get</c> on purpose:
    /// the grid must never fan out one store request per tile.
    /// </summary>
    private async Task<object> GameMetadataAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        var metadata = game is null
            ? await _metadata.GetAsync(gameId!).ConfigureAwait(true)
            : await _metadata.GetAsync(game).ConfigureAwait(true);
        return new { ok = metadata is not null, metadata = MapMetadata(metadata) };
    }

    private static object? MapMetadata(StoreMetadataService.StoreMetadata? metadata)
    {
        if (metadata is null) return null;
        return new
        {
            genre = metadata.Genre,
            year = metadata.Year,
            description = metadata.Description,
        };
    }

    private async Task<object> GameLaunchAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
        }
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library." };

        PostEvent("launch.status", new
        {
            gameId = game.Id,
            ok = true,
            message = "Preparing launch…",
            phase = "preparing",
        });

        var skipDeps = ReadBool(p, hasParams, "skipDeps") == true;
        var launchTask = _services.Launcher.LaunchAsync(game, skipDeps);
        var first = await Task.WhenAny(launchTask, Task.Delay(450)).ConfigureAwait(true);
        var hiddenForLaunch = first != launchTask;
        if (hiddenForLaunch)
        {
            try { App.MainAppWindow?.HideForGameplay(); } catch { }
        }
        var result = await launchTask.ConfigureAwait(true);

        PostEvent("launch.status", new
        {
            gameId = game.Id,
            ok = result.Ok,
            message = result.Ok
                ? (string.IsNullOrWhiteSpace(result.Message) ? "Running" : result.Message)
                : result.Message,
            processId = result.ProcessId,
            backendStarted = result.BackendStarted,
            handoffOnly = result.HandoffOnly,
            needsDependencies = result.NeedsDependencies,
            phase = result.Ok ? "running" : (result.NeedsDependencies ? "needsDeps" : (result.HandoffOnly ? "handoff" : "failed")),
        });

        // Keep Exo out of the way while playing, but leave an explicit restore
        // affordance in the Windows notification area. Slow store handoffs are
        // hidden above while they continue; a failed handoff restores the UI.
        if (result.Ok)
        {
            if (!hiddenForLaunch)
                try { App.MainAppWindow?.HideForGameplay(); } catch { }
            QueuePresenceFromLibrary();
        }
        else if (hiddenForLaunch)
        {
            try { App.MainAppWindow?.RestoreAndActivate(); } catch { }
        }

        return MapDepAwareResult(result);
    }

    private async Task<object> GameStopAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
        }
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library." };

        var result = await _services.Launcher.StopGameAsync(game).ConfigureAwait(true);
        QueuePresenceFromLibrary();
        PostEvent("launch.status", new
        {
            gameId = game.Id,
            ok = result.Ok,
            message = result.Message,
            phase = result.Ok ? "stopped" : "stopFailed",
        });
        return new { ok = result.Ok, message = result.Message };
    }

    private async Task<object> GameInstallAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        // Auto root: Settings override → %LOCALAPPDATA%\ExoLauncher\Games
        var path = ReadString(p, hasParams, "path");
        if (string.IsNullOrWhiteSpace(path))
            path = _services.Settings.Current.DefaultInstallRoot;
        if (string.IsNullOrWhiteSpace(path))
            path = Helpers.PathHelper.GamesRoot;

        var skipDeps = ReadBool(p, hasParams, "skipDeps") == true;

        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        var game = _services.Library.Find(gameId!)
                   ?? TrySynthesizeFromId(gameId!, p, hasParams);
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library or pick a store result." };
        if (!game.Owned && !StoreSearchService.IsOfficialClientCatalogInstall(game))
            return new { ok = false, message = "This title is not owned by the active store account. Buy it from the store first." };

        var result = await _services.Launcher.InstallAsync(game, path, skipDeps).ConfigureAwait(true);
        if (result.Ok)
            _services.Library.Invalidate();

        return new
        {
            ok = result.Ok,
            message = result.Message,
            path = result.Path,
            handoffOnly = result.HandoffOnly,
            needsDependencies = result.NeedsDependencies,
            missingDependencies = MapMissingDeps(result.MissingDependencies),
            queued = result.Queued,
            queuedGameIds = _services.Launcher.QueuedGameIds,
            progress = MapProgress(_services.Launcher.CurrentProgress),
        };
    }

    /// <summary>Install from Store search when the title is not in the library yet.</summary>
    private GameEntry? TrySynthesizeFromId(string gameId, JsonElement p, bool hasParams)
    {
        var title = ReadString(p, hasParams, "title") ?? gameId;
        var fromLibrary = TryLibraryOwnedSource(gameId);
        if (fromLibrary is not null)
        {
            return new GameEntry
            {
                Id = fromLibrary.Id,
                Title = string.IsNullOrWhiteSpace(title) || title == gameId ? fromLibrary.Title : title,
                Store = fromLibrary.Store,
                Installed = fromLibrary.Installed,
                Owned = fromLibrary.Owned,
                EntitlementState = fromLibrary.EntitlementState,
                CanInstall = !fromLibrary.Installed && fromLibrary.Owned && fromLibrary.CanInstall,
                Path = fromLibrary.Path,
                LaunchTarget = fromLibrary.LaunchTarget,
                CoverUrl = fromLibrary.CoverUrl,
                Status = fromLibrary.Installed ? fromLibrary.Status : "Not installed",
                Deps = fromLibrary.Deps,
                LaunchNote = fromLibrary.LaunchNote,
            };
        }

        var officialClient = StoreSearchService.TrySynthesizeOfficialClientInstall(gameId, title);
        if (officialClient is not null) return officialClient;

        if (gameId.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
        {
            var appId = gameId["steam:".Length..];
            if (!appId.All(char.IsDigit)) return null;
            // Re-check library + active-account ticket evidence. Never let a
            // crafted bridge request turn an unknown catalog app into an install
            // handoff merely because Steam is installed.
            var proven = StoreSearchService.BuildSteamCatalogHit(
                appId, title, _services.Library.PeekCachedLibrary());
            if (!proven.Owned) return null;
            return new GameEntry
            {
                Id = gameId,
                Title = title,
                Store = StoreKind.Steam,
                Installed = false,
                Owned = true,
                EntitlementState = EntitlementState.Owned,
                CanInstall = true,
                LaunchTarget = appId,
                CoverUrl = null,
                Status = "Not installed",
                Deps = Array.Empty<string>(),
                LaunchNote = "",
            };
        }

        return null;
    }

    private GameEntry? TryLibraryOwnedSource(string gameId)
    {
        foreach (var game in _services.Library.PeekCachedLibrary())
        {
            if (string.Equals(game.Id, gameId, StringComparison.OrdinalIgnoreCase) &&
                (game.Owned || game.Installed))
                return game;
            var variant = game.Variants.FirstOrDefault(item =>
                string.Equals(item.Id, gameId, StringComparison.OrdinalIgnoreCase));
            if (variant is not null && (variant.Owned || variant.Installed))
                return variant.ToGameEntry(game);
        }
        return null;
    }

    private async Task<object> StoresSearchAsync(JsonElement p, bool hasParams)
    {
        var q = ReadString(p, hasParams, "query") ?? ReadString(p, hasParams, "q") ?? "";
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return new { ok = true, query = q, results = Array.Empty<object>(), count = 0 };

        var query = q.Trim();
        try { _searchCts?.Cancel(); } catch { /* */ }
        try { _searchCts?.Dispose(); } catch { /* */ }
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        void PublishPartial(IReadOnlyList<StoreSearchHit> hits)
        {
            try
            {
                WarmSearchCovers(query, hits, ct);
                PostEvent("stores.search.partial", new
                {
                    query,
                    results = hits.Select(MapSearchHit).ToList(),
                });
            }
            catch (Exception ex)
            {
                AppLog.Debug("stores.search.partial failed: " + ex.Message);
            }
        }

        try
        {
            // Never block search on a full library rescan — use cache, warm if empty once.
            var lib = _services.Library.PeekCachedLibrary();
            if (lib.Count == 0)
                lib = await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            var hits = await _services.StoreSearch
                .SearchAsync(query, lib, ct, PublishPartial)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return new { ok = true, query, results = Array.Empty<object>(), count = 0, cancelled = true };

            // Pull the official art for these titles now and push it back when it
            // lands, so results fill in instead of staying as monograms.
            WarmSearchCovers(query, hits, ct);

            return new
            {
                ok = true,
                query,
                count = hits.Count,
                results = hits.Select(MapSearchHit).ToList(),
            };
        }
        catch (OperationCanceledException)
        {
            // Do not wipe UI results — client ignores cancelled:true and keeps prior hits.
            return new { ok = true, query, results = Array.Empty<object>(), count = 0, cancelled = true };
        }
    }

    private void WarmSearchCovers(string query, IReadOnlyList<StoreSearchHit> hits, CancellationToken ct)
    {
        var candidates = hits
            .Where(h => !CoverArtService.IsUiLoadableCoverUrl(h.CoverUrl))
            .Select(SearchHitEntry)
            .Where(g => CoverArtService.ResolvePreferredUrl(g) is null)
            .ToList();
        var needsArt = new List<GameEntry>(candidates.Count);
        var warmKeys = new List<string>(candidates.Count);
        lock (_searchCoverWarmGate)
        {
            foreach (var candidate in candidates)
            {
                var key = SearchCoverWarmKey(candidate);
                if (!_searchCoverWarmKeys.Add(key)) continue;
                warmKeys.Add(key);
                needsArt.Add(candidate);
            }
        }
        if (needsArt.Count == 0) return;

        var warm = CoverArtService.WarmSearchPortraitCacheAsync(needsArt, ct, onBatchDone: () =>
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                PostEvent("stores.search.partial", new
                {
                    query,
                    results = hits.Select(MapSearchHit).ToList(),
                });
            }
            catch (Exception ex)
            {
                AppLog.Debug("search cover push failed: " + ex.Message);
            }
        });
        _ = ReleaseSearchCoverWarmKeysAsync(warm, warmKeys);
    }

    private async Task ReleaseSearchCoverWarmKeysAsync(Task warm, IReadOnlyList<string> keys)
    {
        try { await warm.ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Debug("search cover warm failed: " + ex.Message); }
        finally
        {
            lock (_searchCoverWarmGate)
            {
                foreach (var key in keys) _searchCoverWarmKeys.Remove(key);
            }
        }
    }

    private static string SearchCoverWarmKey(GameEntry game) =>
        game.Store + ":" + (game.LaunchTarget ?? game.Id) + ":" + game.Title.Trim();

    /// <summary>Search hits carry no art of their own; resolve through the same
    /// official-cover cache the library uses so results are not bare monograms.</summary>
    private static GameEntry SearchHitEntry(StoreSearchHit h) => new()
    {
        Id = h.Id,
        Title = h.Title,
        Store = h.Store,
        LaunchTarget = h.LaunchTarget,
        CoverUrl = h.CoverUrl,
        CoverSource = h.CoverSource,
        Installed = h.Installed,
        Owned = h.Owned,
        CanInstall = h.CanInstall,
    };

    private static object MapSearchHit(StoreSearchHit h) => new
    {
        id = h.Id,
        title = h.Title,
        store = h.Store.ToString().ToLowerInvariant(),
        launchTarget = h.LaunchTarget,
        coverUrl = CoverArtService.IsUiLoadableCoverUrl(h.CoverUrl)
            ? h.CoverUrl
            : CoverArtService.ResolvePreferredUrl(SearchHitEntry(h))
              ?? CoverArtService.ProvisionalSteamPosterUrl(SearchHitEntry(h)),
        coverSource = h.CoverSource,
        owned = h.Owned,
        installed = h.Installed,
        canInstall = h.CanInstall,
        source = h.Source,
        buyUrl = UiFormat.BuyUrl(SearchHitEntry(h)),
    };

    private async Task<object> GameUpdateAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        try
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            var game = _services.Library.Find(gameId!);
            if (game is null)
                return new { ok = false, message = "Game not found. Refresh the library." };

            var skipDeps = ReadBool(p, hasParams, "skipDeps") == true;
            var result = await _services.Launcher.UpdateAsync(game, skipDeps).ConfigureAwait(true);
            if (result.Ok)
                _services.Library.Invalidate();

            return new
            {
                ok = result.Ok,
                message = result.Message,
                path = result.Path,
                handoffOnly = result.HandoffOnly,
                needsDependencies = result.NeedsDependencies,
                missingDependencies = MapMissingDeps(result.MissingDependencies),
                queued = result.Queued,
                queuedGameIds = _services.Launcher.QueuedGameIds,
                progress = MapProgress(_services.Launcher.CurrentProgress),
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"game.update failed: {ex}");
            return new
            {
                ok = false,
                message = "Update failed: " + ex.Message,
                progress = MapProgress(_services.Launcher.CurrentProgress),
            };
        }
    }

    private async Task<object> GameUninstallAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        var result = await _services.Launcher.UninstallAsync(game).ConfigureAwait(true);
        if (result.Ok && !result.Queued)
            _services.Library.Invalidate();
        return new
        {
            ok = result.Ok,
            queued = result.Queued,
            queuedGameIds = _services.Launcher.QueuedGameIds,
            message = result.Message,
        };
    }

    private async Task<object> GameRepairAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        var result = await _services.Launcher.RepairAsync(game).ConfigureAwait(true);
        return new
        {
            ok = result.Ok,
            queued = result.Queued,
            queuedGameIds = _services.Launcher.QueuedGameIds,
            message = result.Message,
            handoffOnly = result.HandoffOnly,
            progress = MapProgress(_services.Launcher.CurrentProgress),
        };
    }

    private object GameExtras(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };
        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        var adapter = _services.FindAdapter(game.Store);
        var canRepair = adapter is IStoreRepair repair && repair.CanRepair(game);
        return new
        {
            ok = true,
            canRepair,
            repairLabel = game.Store switch
            {
                StoreKind.Steam => "Verify files",
                StoreKind.Epic => "Repair",
                StoreKind.Gog => "Repair",
                _ => "Verify",
            },
        };
    }

    private object GameOpenFolder(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh first." };

        var path = game.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            if (!string.IsNullOrWhiteSpace(game.LaunchTarget) && File.Exists(game.LaunchTarget))
                path = Path.GetDirectoryName(game.LaunchTarget);
        }

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new { ok = false, message = "Install folder not found." };

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
            return new { ok = true, path };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    private object GameToggleFavorite(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };

        var card = _services.Library.PeekCachedLibrary().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, gameId, StringComparison.OrdinalIgnoreCase) ||
            candidate.Variants.Any(variant =>
                string.Equals(variant.Id, gameId, StringComparison.OrdinalIgnoreCase)));
        var sourceIds = card is { Variants.Count: > 0 }
            ? card.Variants.Select(variant => variant.Id).ToArray()
            : new[] { gameId! };
        var wasFavorite = sourceIds.Any(_services.Settings.IsFavorite);
        if (wasFavorite)
        {
            // A grouped card is one visible pin. Clearing it must not leave a
            // hidden alternate-store favorite that reappears on refresh.
            _services.Settings.SetFavoriteState(sourceIds, isFavorite: false);
        }
        else
        {
            // Persist the exact source the user acted on. OverlayUserPrefs
            // projects that pin back onto the canonical card on every scan.
            _services.Settings.SetFavoriteState([gameId!], isFavorite: true);
        }
        var settings = _services.Settings.Current;
        return new
        {
            ok = true,
            isFavorite = !wasFavorite,
            favorites = settings.Favorites,
        };
    }

    private async Task<object> ArtworkReplaceAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, cancelled = false, message = "Missing game id." };
        var card = _services.Library.FindVisualCard(gameId);
        if (card is null)
            return new { ok = false, cancelled = false, message = "That title is not in your library." };
        if (string.Equals(card.Id, "local:add", StringComparison.OrdinalIgnoreCase) ||
            (!card.Owned && !card.Installed))
            return new { ok = false, cancelled = false, message = "Artwork controls are available for library titles." };

        // The RPC has no path parameter. Only this native picker can produce the
        // path passed to the image store.
        var picked = await PickImageFileAsync().ConfigureAwait(true);
        if (picked.Cancelled)
            return new { ok = false, cancelled = true, message = "No changes made." };
        if (picked.Path is null)
            return new { ok = false, cancelled = false, message = "Cover picker failed." };

        var result = await _services.Artwork.ReplaceAsync(gameId, picked.Path).ConfigureAwait(true);
        return MapArtworkMutation(result);
    }

    private async Task<object> ArtworkResetAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, cancelled = false, message = "Missing game id." };
        return MapArtworkMutation(await _services.Artwork.ResetAsync(gameId).ConfigureAwait(true));
    }

    private async Task<object> ArtworkRefetchAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, cancelled = false, message = "Missing game id." };
        return MapArtworkMutation(await _services.Artwork.RefetchAsync(gameId).ConfigureAwait(true));
    }

    private async Task<object> ArtworkReportAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, copied = false, issueOpened = false, message = "Missing game id." };
        var report = _services.Artwork.BuildReport(gameId);
        if (!report.Ok || report.Diagnostics is null)
            return new { ok = false, copied = false, issueOpened = false, message = report.Message };

        var copied = false;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(report.Diagnostics);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            copied = true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Artwork report copy failed: " + ex.GetType().Name);
        }

        var issueOpened = false;
        if (ReadBool(p, hasParams, "openIssue") == true)
        {
            try
            {
                // Fixed destination only. Diagnostics never enter the URI and
                // Exo never submits an issue on the user's behalf.
                issueOpened = await Windows.System.Launcher.LaunchUriAsync(
                    new Uri(GameArtworkService.IssueUrl));
            }
            catch (Exception ex)
            {
                AppLog.Debug("Artwork issue page failed: " + ex.GetType().Name);
            }
        }

        return new
        {
            ok = copied,
            copied,
            issueOpened,
            message = copied
                ? issueOpened ? "Artwork details copied. Issue page opened." : "Artwork details copied."
                : "Artwork details could not be copied.",
        };
    }

    private object MapArtworkMutation(GameArtworkService.MutationResult result) => new
    {
        result.Ok,
        result.Cancelled,
        result.Message,
        game = result.Game is null ? null : MapGame(result.Game),
        result.ArtRevision,
    };

    private async Task<object> StoresAuthAsync(JsonElement p, bool hasParams)
    {
        var storeId = ReadString(p, hasParams, "store");
        if (string.IsNullOrWhiteSpace(storeId))
            return new { ok = false, message = "Missing store id." };

        var adapter = _services.FindAdapterById(storeId!);
        if (adapter is null)
            return new { ok = false, message = "Unknown store." };

        var result = await adapter.AuthenticateAsync().ConfigureAwait(true);
        if (result.Ok)
            _services.Library.InvalidateStoreMatrix();
        if (result.Ok &&
            (string.Equals(storeId, "epic", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(storeId, "gog", StringComparison.OrdinalIgnoreCase)))
        {
            try { _services.StoreSearch.InvalidateOwnedCaches(); }
            catch { /* */ }
        }

        return new
        {
            ok = result.Ok,
            message = result.Message,
            requiresUserAction = result.RequiresUserAction,
        };
    }

    /// <summary>
    /// Every store Exo can read, in one payload. The non-live request returns
    /// the disk snapshot immediately. A live refresh gives Epic and Steam a
    /// bounded chance to answer without holding the room shut indefinitely.
    /// </summary>
    private async Task<object> FriendsListAsync(JsonElement p, bool hasParams)
    {
        var live = hasParams &&
                   p.ValueKind == JsonValueKind.Object &&
                   p.TryGetProperty("live", out var liveEl) &&
                   liveEl.ValueKind == JsonValueKind.True;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var snapshot = live
            ? await _social.StoreFriendsAsync(deadline.Token, live: true).ConfigureAwait(true)
            : _social.StoreFriends();
        if (live)
            PostEvent("friends.updated", new { activeCount = snapshot.ActiveCount, count = snapshot.Count });
        return new
        {
            ok = true,
            source = snapshot.Source,
            live = snapshot.Live,
            note = snapshot.Note,
            count = snapshot.Count,
            // The nav badge counts people who are around, so a cached name and
            // an offline row both contribute nothing.
            activeCount = snapshot.ActiveCount,
            sources = snapshot.Sources.Select(source => new
            {
                store = source.Store,
                live = source.Live,
                count = source.Count,
                note = source.Note,
            }).ToList(),
            friends = snapshot.Friends.Select(friend => new
            {
                id = friend.Id,
                name = friend.Name,
                avatarUrl = friend.AvatarUrl,
                source = friend.Source,
                status = friend.Status,
                statusText = friend.StatusText,
                playingId = friend.PlayingId,
                playingTitle = friend.PlayingTitle,
                lastSeenUtc = friend.LastSeenUtc,
                live = friend.Live,
                presenceFrom = friend.PresenceFrom,
            }).ToList(),
        };
    }

    private object FriendsRoster() => MapRoster(_social.Roster());

    private object FriendsAdd(JsonElement p, bool hasParams) => MapRosterResult(_social.AddPerson(
        ReadString(p, hasParams, "handle"),
        ReadString(p, hasParams, "name"),
        ReadString(p, hasParams, "note")));

    private object FriendsRemove(JsonElement p, bool hasParams) =>
        MapRosterResult(_social.RemovePerson(ReadString(p, hasParams, "id")));

    private object FriendsSetNote(JsonElement p, bool hasParams) => MapRosterResult(_social.SetNote(
        ReadString(p, hasParams, "id"),
        ReadString(p, hasParams, "note")));

    /// <summary>
    /// The user saying a store account is the same human as someone on their
    /// Exo list. Exo never works this out on its own.
    /// </summary>
    private object FriendsLink(JsonElement p, bool hasParams) => MapRosterResult(_social.LinkPerson(
        ReadString(p, hasParams, "id"),
        ReadString(p, hasParams, "friendId")));

    private object FriendsUnlink(JsonElement p, bool hasParams) => MapRosterResult(_social.UnlinkPerson(
        ReadString(p, hasParams, "id"),
        ReadString(p, hasParams, "friendId")));

    private async Task<object> FriendsSteamLibraryAsync(JsonElement p, bool hasParams)
    {
        var snapshot = await _social.LinkedSteamLibraryAsync(
            ReadString(p, hasParams, "id"),
            CancellationToken.None).ConfigureAwait(false);
        return new
        {
            ok = snapshot.Ok,
            note = snapshot.Note,
            games = snapshot.Games.Select(game => new
            {
                id = game.Id,
                title = game.Title,
                appId = game.AppId,
                playtimeMinutes = game.PlaytimeMinutes,
            }).ToList(),
        };
    }

    private static object MapRoster(SocialService.RosterSnapshot roster) => new
    {
        ok = true,
        live = roster.Live,
        note = roster.Note,
        people = MapPeople(roster),
    };

    private static object MapRosterResult(SocialService.RosterResult result) => new
    {
        ok = result.Ok,
        message = result.Message,
        live = result.Roster.Live,
        note = result.Roster.Note,
        people = MapPeople(result.Roster),
    };

    /// <summary>
    /// Roster rows are whatever the user typed — no presence, no avatar, no
    /// server. The links are their own claims about which store accounts belong
    /// to the same person.
    /// </summary>
    private static List<object> MapPeople(SocialService.RosterSnapshot roster) => roster.People
        .Select(person => (object)new
        {
            id = person.Id,
            handle = person.Handle,
            name = person.Name,
            note = person.Note,
            addedUtc = person.AddedUtc,
            links = person.Links.Select(link => new
            {
                id = link.Id,
                store = link.Store,
                name = link.Name,
            }).ToList(),
        })
        .ToList();

    private object ProfileGet() => MapProfile(_social.Profile(RunningLibraryGame()));

    /// <summary>
    /// Saves the authored Exo profile. Absent fields are left alone; an empty
    /// string clears one. SocialService caps and validates every value.
    /// </summary>
    private object ProfileSet(JsonElement p, bool hasParams)
    {
        var session = _sessionStore.TryLoad();
        var signedIn = session is not null && session.ExpiresUtc > DateTimeOffset.UtcNow;
        return ProfileSaved(_social.SetProfile(
            ReadString(p, hasParams, "name"),
            signedIn ? null : ReadString(p, hasParams, "handle"),
            ReadString(p, hasParams, "pronouns"),
            ReadString(p, hasParams, "statusText"),
            ReadString(p, hasParams, "bio"),
            ReadString(p, hasParams, "accent"),
            ReadString(p, hasParams, "avatarGameId"),
            ReadString(p, hasParams, "bannerGameId"),
            RunningLibraryGame()));
    }

    /// <summary>Saves section order and visibility, alignment, banner size, and showcase style.</summary>
    private object ProfileSetLook(JsonElement p, bool hasParams) => ProfileSaved(_social.SetLook(
        new SocialService.ProfileLook(
            ReadString(p, hasParams, "layout"),
            ReadString(p, hasParams, "bannerHeight"),
            ReadString(p, hasParams, "showcaseStyle"),
            ReadBool(p, hasParams, "showHandle"),
            ReadStringList(p, hasParams, "sections"),
            ReadStringList(p, hasParams, "hiddenSections")),
        RunningLibraryGame()));

    /// <summary>
    /// Opens the host's file picker and stores the chosen PNG or JPEG as the
    /// avatar or banner. The UI only names the slot: a path it sent would never
    /// be accepted, and the picture is copied into Exo rather than referenced.
    /// </summary>
    private async Task<object> ProfilePickImageAsync(JsonElement p, bool hasParams)
    {
        var kind = ReadString(p, hasParams, "kind");
        if (ProfileImageStore.NormalizeSlot(kind) is null)
            return new { ok = false, cancelled = false, message = "Unknown image slot." };

        var picked = await PickImageFileAsync().ConfigureAwait(true);
        if (picked.Path is null)
            return new { ok = false, cancelled = picked.Cancelled, message = picked.Message };

        var result = _social.SetImage(kind, picked.Path, RunningLibraryGame());
        return new
        {
            ok = result.Ok,
            cancelled = false,
            message = result.Message,
            profile = result.Ok ? ProfileSaved(result.Profile) : MapProfile(result.Profile),
        };
    }

    private object ProfileClearImage(JsonElement p, bool hasParams)
    {
        var result = _social.ClearImage(ReadString(p, hasParams, "kind"), RunningLibraryGame());
        return new
        {
            ok = result.Ok,
            cancelled = false,
            message = result.Message,
            profile = result.Ok ? ProfileSaved(result.Profile) : MapProfile(result.Profile),
        };
    }

    private async Task<object> AccountGetAsync()
    {
        var result = await _account.GetAccountAsync(_accountCts.Token).ConfigureAwait(true);
        if (result.SignedIn)
        {
            await StartPresenceIfSignedInAsync().ConfigureAwait(true);
            PostEvent("profile.updated", MapProfile(_social.Profile(RunningLibraryGame())));
        }
        return result;
    }

    private async Task<object> AccountSignInAsync(JsonElement p, bool hasParams)
    {
        var result = await _account.SignInAsync(
                ReadString(p, hasParams, "provider"),
                _services.Settings,
                _accountCts.Token,
                ReadString(p, hasParams, "email"))
            .ConfigureAwait(true);
        if (_sessionStore.TryLoad() is not null)
        {
            await StartPresenceIfSignedInAsync().ConfigureAwait(true);
            PostEvent("profile.updated", MapProfile(_social.Profile(RunningLibraryGame())));
        }
        return result;
    }

    private async Task<object> AccountCreatePasswordAsync(JsonElement p, bool hasParams)
    {
        var result = await _account.CreatePasswordAccountAsync(
                ReadString(p, hasParams, "name"),
                ReadString(p, hasParams, "email"),
                ReadString(p, hasParams, "password"),
                _services.Settings,
                _accountCts.Token)
            .ConfigureAwait(true);
        if (_sessionStore.TryLoad() is not null)
        {
            await StartPresenceIfSignedInAsync().ConfigureAwait(true);
            PostEvent("profile.updated", MapProfile(_social.Profile(RunningLibraryGame())));
        }
        return result;
    }

    private async Task<object> AccountPasswordSignInAsync(JsonElement p, bool hasParams)
    {
        var result = await _account.SignInWithPasswordAsync(
                ReadString(p, hasParams, "email"),
                ReadString(p, hasParams, "password"),
                _services.Settings,
                _accountCts.Token)
            .ConfigureAwait(true);
        if (_sessionStore.TryLoad() is not null)
        {
            await StartPresenceIfSignedInAsync().ConfigureAwait(true);
            PostEvent("profile.updated", MapProfile(_social.Profile(RunningLibraryGame())));
        }
        return result;
    }

    private async Task<object> AccountSignOutAsync()
    {
        var result = await _account.SignOutAsync(_accountCts.Token).ConfigureAwait(true);
        return result;
    }

    private async Task<object> AccountReserveHandleAsync(JsonElement p, bool hasParams)
    {
        var result = await _account.ReserveHandleAsync(
                ReadString(p, hasParams, "handle"), _services.Settings, _accountCts.Token)
            .ConfigureAwait(true);
        if (_sessionStore.TryLoad() is not null)
        {
            await StartPresenceIfSignedInAsync().ConfigureAwait(true);
            PostEvent("profile.updated", MapProfile(_social.Profile(RunningLibraryGame())));
        }
        return result;
    }

    private Task<object> AccountSetProfileAsync(JsonElement p, bool hasParams) =>
        _account.SetProfileAsync(p, hasParams, _services.Settings, _accountCts.Token);

    private async Task<object> OnlineProfileGetAsync(JsonElement p, bool hasParams)
    {
        var result = await _online.GetPublicProfileAsync(
                ReadString(p, hasParams, "handle"),
                ReadString(p, hasParams, "userId"),
                _accountCts.Token)
            .ConfigureAwait(true);
        if (!result.Ok || result.Value is null)
            return result;

        var profile = result.Value;
        var media = new Dictionary<string, object?>(StringComparer.Ordinal);
        AppLog.Info(
            "Public profile media: " +
            string.Join(
                ", ",
                ProfileImageStore.Slots.Select(kind =>
                {
                    profile.Media.TryGetValue(kind, out var slot);
                    return slot is null
                        ? kind + "=none"
                        : kind + "=" + slot.ContentType + " " + slot.Width + "x" + slot.Height;
                })));
        foreach (var kind in ProfileImageStore.Slots)
        {
            profile.Media.TryGetValue(kind, out var metadata);
            if (metadata is null)
            {
                media[kind] = null;
                continue;
            }

            _onlineMedia[OnlineMediaKey(profile.UserId, kind)] = metadata;
            var downloaded = await _online.DownloadProfileMediaAsync(
                    profile.UserId, metadata, _accountCts.Token)
                .ConfigureAwait(true);
            if (downloaded.Value is null)
            {
                AppLog.Info(
                    $"Public profile {kind} download failed: {downloaded.Diagnostics.Error?.Code ?? downloaded.Diagnostics.Source} ({metadata.ContentType}).");
            }
            media[kind] = downloaded.Value is null
                ? new
                {
                    available = false,
                    source = downloaded.Diagnostics.Source,
                    updatedAt = metadata.UpdatedAt,
                }
                : new
                {
                    available = true,
                    url = downloaded.Value.Url,
                    contentType = downloaded.Value.ContentType,
                    size = downloaded.Value.Size,
                    source = downloaded.Diagnostics.Source,
                    updatedAt = metadata.UpdatedAt,
                };
        }

        return new
        {
            result.Ok,
            value = new
            {
                profile.UserId,
                profile.Handle,
                profile.Profile,
                profile.Badges,
                media,
            },
            result.Diagnostics,
            result.Queued,
        };
    }

    private async Task<object> OnlineProfilesSearchAsync(
        JsonElement p,
        bool hasParams)
    {
        var result = await _online.SearchProfilesAsync(
                ReadString(p, hasParams, "query"),
                ReadInt(p, hasParams, "limit") ?? 20,
                ReadString(p, hasParams, "cursor"),
                _accountCts.Token)
            .ConfigureAwait(true);
        return new
        {
            result.Ok,
            value = result.Value is null ? null : new
            {
                profiles = result.Value.Profiles.Select(profile => new
                {
                    profile.UserId,
                    profile.Handle,
                    profile.Profile,
                }).ToList(),
                result.Value.NextCursor,
            },
            result.Diagnostics,
            result.Queued,
        };
    }

    private object OnlineProfileShare(JsonElement p, bool hasParams)
    {
        var handle = (ReadString(p, hasParams, "handle") ?? string.Empty).Trim();
        var action = (ReadString(p, hasParams, "action") ?? "copy").Trim().ToLowerInvariant();
        if (!ExoHandle.TryValidate(handle, out var clean, out var problem))
            return new { ok = false, message = problem };
        if (action is not ("copy" or "open"))
            return new { ok = false, message = "Share action must be copy or open." };

        var origin = ExoIdContract.ResolveOrigin();
        if (string.IsNullOrEmpty(origin))
            return new { ok = false, message = "Online profile sharing is not configured." };
        var url = ExoIdContract.Combine(
            origin,
            ExoIdContract.PublicProfileSharePrefix + "/" + Uri.EscapeDataString(clean));
        if (action == "open")
        {
            var opened = ExoAccountService.OpenSystemBrowser(url);
            return new
            {
                ok = opened,
                message = opened ? "Public profile opened." : "The public profile could not be opened.",
            };
        }

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(url);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            return new { ok = true, message = "Public profile link copied." };
        }
        catch
        {
            return new { ok = false, message = "The public profile link could not be copied." };
        }
    }

    private Task<ExoOnlineResult<ExoProfilePrivacy>> OnlinePrivacySetAsync(
        JsonElement p,
        bool hasParams)
    {
        var searchable = ReadBool(p, hasParams, "searchable");
        if (searchable is null)
        {
            return Task.FromResult(OnlineFailure<ExoProfilePrivacy>(
                "INVALID_REQUEST", "Search visibility must be on or off."));
        }
        return _online.SetPrivacyAsync(
            new ExoProfilePrivacy
            {
                ProfileVisibility = ReadString(p, hasParams, "profileVisibility") ?? string.Empty,
                Searchable = searchable.Value,
                RequestPolicy = ReadString(p, hasParams, "requestPolicy") ?? string.Empty,
                ActivityVisibility = ReadString(p, hasParams, "activityVisibility") ?? string.Empty,
            },
            _accountCts.Token);
    }

    private async Task<object> OnlineFriendsListAsync(
        JsonElement p,
        bool hasParams)
    {
        var result = await _online.GetFriendsAsync(
                ReadInt(p, hasParams, "limit") ?? 50,
                ReadString(p, hasParams, "cursor"),
                _accountCts.Token)
            .ConfigureAwait(true);
        if (!result.Ok || result.Value is null) return result;

        using var gate = new SemaphoreSlim(6);
        var friends = await Task.WhenAll(result.Value.Friends.Select(async friend =>
        {
            string? avatarUrl = null;
            if (friend.Avatar is not null)
            {
                _onlineMedia[OnlineMediaKey(friend.UserId, "avatar")] = friend.Avatar;
                await gate.WaitAsync(_accountCts.Token).ConfigureAwait(true);
                try
                {
                    var downloaded = await _online.DownloadProfileMediaAsync(
                            friend.UserId, friend.Avatar, _accountCts.Token)
                        .ConfigureAwait(true);
                    avatarUrl = downloaded.Value?.Url;
                }
                finally
                {
                    gate.Release();
                }
            }
            return new
            {
                friend.UserId,
                friend.Handle,
                friend.Sources,
                friend.ConnectedAt,
                avatarUrl,
            };
        })).ConfigureAwait(true);

        return new
        {
            result.Ok,
            value = new { friends, result.Value.NextCursor },
            result.Diagnostics,
            result.Queued,
        };
    }

    private Task<ExoOnlineResult<ExoFriendRequestPage>> OnlineFriendRequestsAsync(
        JsonElement p,
        bool hasParams) =>
        _online.GetFriendRequestsAsync(
            ReadInt(p, hasParams, "limit") ?? 20,
            ReadString(p, hasParams, "incomingCursor"),
            ReadString(p, hasParams, "outgoingCursor"),
            _accountCts.Token);

    private Task<ExoOnlineResult<ExoBlockPage>> OnlineBlocksListAsync(
        JsonElement p,
        bool hasParams) =>
        _online.GetBlocksAsync(
            ReadInt(p, hasParams, "limit") ?? 20,
            ReadString(p, hasParams, "cursor"),
            _accountCts.Token);

    private async Task<object> OnlineLinksGetAsync()
    {
        var result = await _online.GetLinksAsync(_accountCts.Token).ConfigureAwait(true);
        return MapSafeLinks(result);
    }

    private async Task<object> OnlineLinkStoreAsync(JsonElement p, bool hasParams)
    {
        if (!TryReadLinkedStore(p, hasParams, out var store))
            return OnlineFailure<ExoVerifiedStoreLink>("INVALID_REQUEST", "Store must be Steam, Epic, or GOG.");
        if (store == ExoLinkedStore.Steam)
        {
            var result = await _online.LinkSteamAsync(_accountCts.Token).ConfigureAwait(true);
            return MapSafeLinks(result);
        }
        var linked = await _online.LinkStoreAsync(store, _accountCts.Token).ConfigureAwait(true);
        return new
        {
            linked.Ok,
            value = linked.Value is null ? null : new
            {
                store = linked.Value.Store,
                linked.Value.Verified,
                linked.Value.VerifiedAt,
            },
            linked.Diagnostics,
            linked.Queued,
        };
    }

    private Task<ExoOnlineResult<ExoMutationAck>> OnlineUnlinkStoreAsync(
        JsonElement p,
        bool hasParams) =>
        TryReadLinkedStore(p, hasParams, out var store)
            ? _online.UnlinkStoreAsync(store, _accountCts.Token)
            : Task.FromResult(OnlineFailure<ExoMutationAck>(
                "INVALID_REQUEST", "Store must be Steam, Epic, or GOG."));

    private async Task<ExoOnlineResult<ExoMatchEnvelope>> OnlineMatchStoreAsync(
        JsonElement p,
        bool hasParams)
    {
        if (!TryReadLinkedStore(p, hasParams, out var store))
            return OnlineFailure<ExoMatchEnvelope>("INVALID_REQUEST", "Store must be Steam, Epic, or GOG.");
        if (store == ExoLinkedStore.Gog)
        {
            return OnlineFailure<ExoMatchEnvelope>(
                "MATCH_SOURCE_UNAVAILABLE",
                "GOG does not expose a verified mutual friend list to Exo on this build.");
        }

        string[] ids;
        if (store == ExoLinkedStore.Steam)
        {
            var root = SteamAdapter.TryResolveSteamRootPublic();
            ids = root is null
                ? []
                : SteamFriends.LoadActiveAccount(root)
                    .Select(friend => friend.SteamId64)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Take(200)
                    .ToArray();
        }
        else
        {
            var snapshot = await EpicFriends.LoadAsync(_accountCts.Token).ConfigureAwait(true);
            ids = (snapshot.MutualExternalIds ?? [])
                .Distinct(StringComparer.Ordinal)
                .Take(200)
                .ToArray();
        }
        if (ids.Length == 0)
        {
            return OnlineFailure<ExoMatchEnvelope>(
                "MATCH_SOURCE_UNAVAILABLE",
                store == ExoLinkedStore.Steam
                    ? "Steam has not written a usable mutual friends list for the active account on this PC."
                    : "Epic has not returned a usable mutual friends list for the active account on this PC.");
        }
        return await _online.MatchStoreFriendsAsync(
                store, ExoStoreRelationship.Mutual, ids, _accountCts.Token)
            .ConfigureAwait(true);
    }

    private async Task<object> OnlineRevokeAllSessionsAsync()
    {
        return await _online.RevokeAllSessionsAsync(_accountCts.Token).ConfigureAwait(true);
    }

    private async Task<object> OnlineRevokeSessionAsync(JsonElement p, bool hasParams)
    {
        var result = await _online.RevokeSessionAsync(
                ReadString(p, hasParams, "sessionId"), _accountCts.Token)
            .ConfigureAwait(true);
        return result;
    }

    private async Task<object> OnlineDeleteAccountAsync()
    {
        return await _online.DeleteAccountAsync(_accountCts.Token).ConfigureAwait(true);
    }

    private async Task<object> OnlineUploadMediaAsync(JsonElement p, bool hasParams)
    {
        var kind = (ReadString(p, hasParams, "kind") ?? string.Empty).Trim().ToLowerInvariant();
        var currentSettings = _services.Settings.Current;
        var fileName = kind switch
        {
            "avatar" => currentSettings.ProfileAvatarImage,
            "banner" => currentSettings.ProfileBannerImage,
            _ when kind.StartsWith("gallery", StringComparison.Ordinal) &&
                   currentSettings.ProfileGalleryImages.TryGetValue(kind, out var galleryFile) => galleryFile,
            _ => null,
        };
        fileName = ProfileImageStore.FileName(fileName);
        if (fileName is null)
        {
            return OnlineFailure<ExoProfileMediaMetadata>(
                "MEDIA_LOCAL_UNAVAILABLE",
                ProfileImageStore.NormalizeSlot(kind) is not null
                    ? "Pick a local image for that profile slot first."
                    : "Media kind must be avatar, banner, or gallery slot.");
        }

        var path = Path.Combine(CoverArtService.CacheRoot, fileName);
        var result = await _online.UploadProfileMediaFileAsync(kind, path, _accountCts.Token)
            .ConfigureAwait(true);
        return new
        {
            result.Ok,
            value = result.Value is null ? null : new
            {
                result.Value.Kind,
                result.Value.UpdatedAt,
            },
            result.Diagnostics,
            result.Queued,
        };
    }

    private async Task<object> OnlineDownloadMediaAsync(JsonElement p, bool hasParams)
    {
        var userId = ReadString(p, hasParams, "userId") ?? string.Empty;
        var kind = (ReadString(p, hasParams, "kind") ?? string.Empty).Trim().ToLowerInvariant();
        if (!_onlineMedia.TryGetValue(OnlineMediaKey(userId, kind), out var metadata))
        {
            return OnlineFailure<ExoProfileMediaLocalRef>(
                "MEDIA_REFERENCE_UNAVAILABLE",
                "Refresh that profile before downloading its media.");
        }
        var result = await _online.DownloadProfileMediaAsync(userId, metadata, _accountCts.Token)
            .ConfigureAwait(true);
        return new
        {
            result.Ok,
            value = result.Value is null ? null : new
            {
                result.Value.Url,
                result.Value.ContentType,
                result.Value.Size,
                result.Value.Sha256,
            },
            result.Diagnostics,
            result.Queued,
        };
    }

    private async Task<object> OnlineExportAccountAsync()
    {
        var result = await _online.ExportAccountAsync(_accountCts.Token).ConfigureAwait(true);
        if (!result.Ok || result.Value is null)
            return result;

        string json;
        try
        {
            json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonOpts)
            {
                WriteIndented = true,
            });
        }
        catch
        {
            return new
            {
                ok = false,
                cancelled = false,
                message = "The account export could not be prepared.",
                result.Diagnostics,
            };
        }

        var saved = await SaveAccountExportAsync(json).ConfigureAwait(true);
        return new
        {
            ok = saved.Ok,
            cancelled = saved.Cancelled,
            saved.Message,
            result.Diagnostics,
        };
    }

    private sealed record SavedExport(bool Ok, bool Cancelled, string? Message);

    private async Task<SavedExport> SaveAccountExportAsync(string json)
    {
        var completion = new TaskCompletionSource<SavedExport>();
        void Run() => _ = RunSaveAccountExportAsync(json, completion);
        if (!_queue.HasThreadAccess)
        {
            if (!_queue.TryEnqueue(Run))
                return new SavedExport(false, false, "The account export picker could not open.");
        }
        else
        {
            Run();
        }
        return await completion.Task.ConfigureAwait(true);
    }

    private static async Task RunSaveAccountExportAsync(
        string json,
        TaskCompletionSource<SavedExport> completion)
    {
        try
        {
            var window = App.MainAppWindow;
            if (window is null)
            {
                completion.TrySetResult(new SavedExport(false, false, "No window for the account export picker."));
                return;
            }
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = "exo-account-export",
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeChoices.Add("JSON", [".json"]);
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(window));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                completion.TrySetResult(new SavedExport(false, true, null));
                return;
            }
            await Windows.Storage.FileIO.WriteTextAsync(file, json);
            completion.TrySetResult(new SavedExport(true, false, "Account export saved."));
        }
        catch
        {
            completion.TrySetResult(new SavedExport(false, false, "The account export could not be saved."));
        }
    }

    private static object MapSafeLinks(ExoOnlineResult<ExoLinkState> result) => new
    {
        result.Ok,
        value = result.Value is null ? null : new
        {
            result.Value.Discovery,
            links = result.Value.Links.Select(link => new
            {
                link.Store,
                link.Verified,
                link.VerifiedAt,
            }).ToList(),
            connections = result.Value.Connections.Select(connection => new
            {
                connection.UserId,
                connection.Handle,
                connection.Store,
                connection.CreatedAt,
            }).ToList(),
        },
        result.Diagnostics,
        result.Queued,
    };

    private static bool TryReadLinkedStore(
        JsonElement p,
        bool hasParams,
        out ExoLinkedStore store)
    {
        store = default;
        return (ReadString(p, hasParams, "store") ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "steam" => Set(ExoLinkedStore.Steam, out store),
            "epic" => Set(ExoLinkedStore.Epic, out store),
            "gog" => Set(ExoLinkedStore.Gog, out store),
            _ => false,
        };

        static bool Set(ExoLinkedStore value, out ExoLinkedStore output)
        {
            output = value;
            return true;
        }
    }

    private ExoOnlineResult<T> OnlineFailure<T>(string code, string message) where T : class => new(
        false,
        null,
        new ExoOnlineDiagnostics(
            Configured: OnlineConfigured(),
            SignedIn: _sessionStore.TryLoad() is null ? false : true,
            Source: ExoOnlineSources.Unavailable,
            LastSuccessfulSync: null,
            Retryable: false,
            Error: new ExoOnlineError(code, message)));

    private static string OnlineMediaKey(string userId, string kind) => userId + "\n" + kind;

    private static bool OnlineConfigured()
    {
        try { return ExoIdContract.ResolveOrigin() is not null; }
        catch { return false; }
    }

    /// <summary>
    /// Every profile write ends here. The titlebar reads the profile once, so a
    /// save that does not announce itself leaves a stale avatar beside the gear.
    /// </summary>
    private object ProfileSaved(SocialService.ExoProfile profile)
    {
        var mapped = MapProfile(profile);
        PostEvent("profile.updated", mapped);
        return mapped;
    }

    private object MapProfile(SocialService.ExoProfile profile)
    {
        var session = _sessionStore.TryLoad();
        var signedIn = session is not null && session.ExpiresUtc > DateTimeOffset.UtcNow;
        return new
        {
        ok = true,
        name = profile.Name,
        handle = signedIn ? session!.Handle : profile.Handle,
        handleSource = signedIn ? "server" : "local",
        pronouns = profile.Pronouns,
        statusText = profile.StatusText,
        bio = profile.Bio,
        accent = profile.Accent,
        // KnownId drops the saved pick when PeekCachedLibrary is still empty.
        // Keep the settings id so the titlebar can resolve the cover once games land.
        avatarGameId = TitlebarIdentity.CoalesceSavedAvatarGameId(
            profile.AvatarGameId,
            _services.Settings.Current.ProfileAvatarGameId),
        bannerGameId = profile.BannerGameId,
        // Uploaded pictures are served from Exo's cover cache, not from the
        // folder the user picked them out of.
        avatarImageUrl = profile.AvatarImageUrl,
        bannerImageUrl = profile.BannerImageUrl,
        galleryImages = profile.GalleryImages.Select(image => new { slot = image.Slot, url = image.Url }),
        layout = profile.Layout,
        bannerHeight = profile.BannerHeight,
        showcaseStyle = profile.ShowcaseStyle,
        showHandle = profile.ShowHandle,
        sections = profile.Sections,
        hiddenSections = profile.HiddenSections,
        playingId = profile.PlayingId,
        playingTitle = profile.PlayingTitle,
        gameCount = profile.GameCount,
        installedCount = profile.InstalledCount,
        playtimeMinutes = profile.PlaytimeMinutes,
        unlockedCount = profile.UnlockedCount,
        storesConnected = profile.StoresConnected,
        rosterCount = profile.RosterCount,
        showcase = profile.Showcase,
        showcaseEntries = ShowcaseEntries(profile.Showcase),
        // Labelled as store sessions, never as the Exo identity.
        storeAccounts = profile.StoreAccounts.Select(account => new
        {
            store = account.Store,
            displayName = account.DisplayName,
            accountName = account.AccountName,
        }).ToList(),
        stores = StoreMatrixWithLayers(),
        };
    }

    /// <summary>
    /// What Exo actually recorded for each pinned game: hours, last played, and
    /// the unlock count from the last successful provider read. A number Exo
    /// never observed is absent, and the room prints a dash for it.
    /// </summary>
    private List<object> ShowcaseEntries(IReadOnlyList<string> ids)
    {
        var rows = new List<object>();
        foreach (var id in ids)
        {
            var game = _services.Library.Find(id);
            if (game is null) continue;

            int? unlocked = null;
            int? total = null;
            var snapshot = _services.Achievements.GetLatestSnapshot(game);
            if (snapshot is not null &&
                snapshot.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete)
            {
                var seen = snapshot.ReportedUnlocked ?? snapshot.Entries.Count(entry => entry.State.Unlocked);
                var catalog = snapshot.ReportedTotal ?? snapshot.Entries.Count;
                if (catalog > 0)
                {
                    unlocked = seen;
                    total = catalog;
                }
            }

            rows.Add(new
            {
                id = game.Id,
                title = game.Title,
                store = game.Store.ToString().ToLowerInvariant(),
                installed = game.Installed,
                playtimeMinutes = game.PlaytimeMinutes,
                lastPlayedUtc = game.LastPlayedUtc?.ToString("O"),
                achievementsUnlocked = unlocked,
                achievementsTotal = total,
            });
        }

        return rows;
    }

    private object ProfileSetShowcase(JsonElement p, bool hasParams)
    {
        _social.SetShowcase(ReadStringList(p, hasParams, "ids") ?? new List<string>());
        return ProfileSaved(_social.Profile(RunningLibraryGame()));
    }

    private static List<string>? ReadStringList(JsonElement p, bool hasParams, string name)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return null;

        var values = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                values.Add(item.GetString() ?? string.Empty);
        }

        return values;
    }

    /// <summary>The picked file's path, or why there is none. Only the host may name a file.</summary>
    private sealed record PickedFile(string? Path, bool Cancelled, string? Message);

    private async Task<PickedFile> PickImageFileAsync()
    {
        var tcs = new TaskCompletionSource<PickedFile>();
        void Run() => _ = RunPickImageAsync(tcs);

        if (!_queue.HasThreadAccess)
            _queue.TryEnqueue(Run);
        else
            Run();

        return await tcs.Task.ConfigureAwait(true);
    }

    private static async Task RunPickImageAsync(TaskCompletionSource<PickedFile> tcs)
    {
        try
        {
            var window = App.MainAppWindow;
            if (window is null)
            {
                tcs.TrySetResult(new PickedFile(null, true, "No window for the file picker."));
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" })
                picker.FileTypeFilter.Add(extension);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                tcs.TrySetResult(new PickedFile(null, true, null));
                return;
            }

            tcs.TrySetResult(new PickedFile(file.Path, false, null));
        }
        catch (Exception ex)
        {
            tcs.TrySetResult(new PickedFile(null, false, ex.Message));
        }
    }

    /// <summary>The one library entry the orchestrator currently reports as live.</summary>
    private Models.GameEntry? RunningLibraryGame()
    {
        foreach (var game in _services.Library.PeekCachedLibrary())
        {
            if (!game.Installed) continue;
            var state = _services.Launcher.GetGameRunState(game, false);
            if (state.IsRunning || state.CanStop) return game;
        }

        return null;
    }

    private async Task StartPresenceIfSignedInAsync()
    {
        if (_detached || _presence is null || _presence.IsRunning ||
            Interlocked.CompareExchange(ref _presenceStarting, 1, 0) != 0)
            return;
        try
        {
            if (_detached || _presence.IsRunning)
                return;
            await _presence.StartAsync(_sessionStore, _accountCts.Token).ConfigureAwait(false);
            QueuePresenceFromLibrary();
        }
        catch (OperationCanceledException) when (_accountCts.IsCancellationRequested) { }
        catch (InvalidOperationException) { /* Signed out, expired, disposed, or already running. */ }
        catch
        {
            PostEvent("online.presence", new
            {
                kind = "transportError",
                presence = new
                {
                    status = ExoPresenceStatus.Unknown,
                    available = false,
                },
                error = new
                {
                    code = "TRANSPORT_UNAVAILABLE",
                    message = "Presence connection is unavailable.",
                },
            });
        }
        finally
        {
            Interlocked.Exchange(ref _presenceStarting, 0);
        }
    }

    private async Task StopPresenceAsync()
    {
        lock (_presenceActivityGate)
            _presenceActivityKey = null;
        if (_presence is null || !_presence.IsRunning)
            return;
        try { await _presence.StopAsync().ConfigureAwait(false); }
        catch { /* Presence cannot block sign-out or shutdown. */ }
    }

    private async Task DisposePresenceAsync()
    {
        if (_presence is null)
            return;
        try { await _presence.DisposeAsync().ConfigureAwait(false); }
        catch { /* Optional online cleanup cannot block launcher shutdown. */ }
    }

    private void QueuePresenceFromLibrary()
    {
        var presence = _presence;
        if (_detached || presence is null || !presence.IsRunning)
            return;
        var game = RunningLibraryGame();
        var key = game is null ? "online" : "game:" + game.Id;
        lock (_presenceActivityGate)
        {
            if (string.Equals(_presenceActivityKey, key, StringComparison.Ordinal))
                return;
            _presenceActivityKey = key;
        }
        _ = PublishPresenceActivityAsync(presence, game, key);
    }

    private async Task PublishPresenceActivityAsync(
        ExoPresenceClient presence,
        Models.GameEntry? game,
        string key)
    {
        try
        {
            await presence.SetStatusAsync(
                    game is null ? ExoPresenceActivity.Online : ExoPresenceActivity.InGame,
                    game?.Id,
                    game?.Title,
                    _accountCts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_presenceActivityGate)
            {
                if (string.Equals(_presenceActivityKey, key, StringComparison.Ordinal))
                    _presenceActivityKey = null;
            }
        }
    }

    private void OnPresenceMessage(ExoPresenceMessage message)
    {
        var presence = message.Presence;
        PostEvent("online.presence", new
        {
            kind = message.Kind switch
            {
                ExoPresenceMessageKind.Ready => "ready",
                ExoPresenceMessageKind.Ack => "ack",
                ExoPresenceMessageKind.Presence => "presence",
                ExoPresenceMessageKind.Error => "error",
                _ => "transportError",
            },
            scope = message.Kind == ExoPresenceMessageKind.TransportError
                ? "roster"
                : presence is null ? null : "user",
            presence = presence is null ? null : new
            {
                presence.UserId,
                status = presence.Available ? presence.Status : ExoPresenceStatus.Unknown,
                gameId = presence.Available ? presence.GameId : null,
                gameTitle = presence.Available ? presence.GameTitle : null,
                presence.LastSeen,
                presence.Available,
            },
            error = message.ErrorCode is null ? null : new
            {
                code = message.ErrorCode,
                message = message.ErrorMessage,
            },
            message.ReceivedAt,
        });
    }

    private static SocketsHttpHandler CreateIdentityHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
    };

    private sealed class NativeStoreTokenSource : IExoStoreTokenSource
    {
        public async ValueTask<string?> GetAccessTokenAsync(
            ExoLinkedStore store,
            CancellationToken cancellationToken)
        {
            switch (store)
            {
                case ExoLinkedStore.Epic:
                    return (await EpicPlaytime.ResolveSessionAsync(cancellationToken).ConfigureAwait(false))
                        ?.AccessToken;
                case ExoLinkedStore.Gog:
                {
                    var path = GogAuthService.FindExistingAuthConfigPath();
                    if (path is null)
                        return null;
                    try
                    {
                        await using var stream = new FileStream(
                            path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            16 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        using var reader = new StreamReader(stream);
                        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                        return GogdlCli.TryReadCredentials(json, out var credentials) &&
                               !credentials.IsExpired(DateTimeOffset.UtcNow)
                            ? credentials.AccessToken
                            : null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        return null;
                    }
                }
                default:
                    return null;
            }
        }
    }

    private async Task<object> StoresCheckAsync()
    {
        var check = await Task.Run(_services.Library.CheckStoresLocal).ConfigureAwait(false);
        return new
        {
            check.state,
            checkedAtUtc = check.checkedAtUtc.ToString("O"),
            check.code,
            stores = check.stores.Select(store => new
            {
                store.store,
                store.client,
                store.backend,
                store.session,
                store.cache,
                store.readiness,
                store.code,
            }).ToList(),
        };
    }

    /// <summary>
    /// Store rows carry which of the five backends Exo actually speaks for that
    /// store, so the UI never implies a layer that was never wired. Every caller
    /// uses this mapper; library.get cannot race out a status-only row.
    /// </summary>
    private object StoreMatrixWithLayers() =>
        MapStoreMatrix(_services.Library.StoreMatrix());

    private static object MapStoreMatrix(IReadOnlyList<LibraryService.StoreBackendStatus> statuses) =>
        statuses.Select(status =>
        {
            var layers = StoreLayerMatrix.For(status.store, new StoreLayerMatrix.Context(
                status.clientPresent,
                status.backendPresent,
                status.signedIn,
                status.webApiKeyPresent,
                status.localDatabasePresent));
            return new
            {
                status.store,
                status.displayName,
                status.agentPresent,
                status.clientPresent,
                status.backendPresent,
                status.signedIn,
                status.cachePresent,
                status.detail,
                status.checkCode,
                checkedAtUtc = status.checkedAtUtc.ToString("O"),
                layers = new
                {
                    login = layers.Login,
                    owned = layers.Owned,
                    covers = layers.Covers,
                    downloads = layers.Downloads,
                    social = layers.Social,
                    note = layers.Note,
                },
            };
        }).ToList();

    private object GameProgress(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        return MapProgress(_services.Launcher.GetProgress(gameId));
    }

    private object DepsList() => new
    {
        items = _services.Dependencies.DetectAll().Select(d => new
        {
            id = d.Id,
            name = d.Name,
            status = d.Status,
            detail = d.Detail,
            canOfferInstall = d.CanOfferInstall,
            officialUrl = d.OfficialUrl,
        }).ToList(),
    };

    private object DepsOfferInstall(JsonElement p, bool hasParams)
    {
        var depId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(depId))
            return new { ok = false, message = "Missing dependency id." };
        return _services.Dependencies.OfferInstall(depId!);
    }

    private object BuildSettings()
    {
        var s = _services.Settings.Current;
        // Locked product defaults always reflected to the UI.
        return new
        {
            appVersion = _services.AppVersion,
            closeStoreClientsAfterLaunch = true,
            autoInstallRedistributables = true,
            minimizeWhilePlaying = true,
            antiCheatSafeMode = true,
            theme = "amoled",
            copyPortableIntoLibrary = false,
            allowResize = true,
            checkForUpdates = true,
            sortMode = s.SortMode,
            defaultInstallRoot = s.DefaultInstallRoot,
            favorites = s.Favorites,
            recent = s.Recent,
            onboardingComplete = s.OnboardingComplete,
            accountSetupComplete = s.AccountSetupComplete,
            trophyNotificationsEnabled = s.TrophyNotificationsEnabled,
            trophyNotificationPreset = s.TrophyNotificationPreset,
            trophyNotificationPosition = s.TrophyNotificationPosition,
            trophyNotificationPositionX = s.TrophyNotificationPositionX,
            trophyNotificationPositionY = s.TrophyNotificationPositionY,
            trophyNotificationDurationSeconds = s.TrophyNotificationDurationSeconds,
            trophyNotificationSound = s.TrophyNotificationSound,
            trophyNotificationSoundCue = s.TrophyNotificationSoundCue,
            steamWebApiKeySet = SteamWebApiKeyStore.HasKey(),
        };
    }

    private object SetSettings(JsonElement p, bool hasParams)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object)
            return BuildSettings();

        bool? close = null, auto = null, min = null, copy = null, resize = null, updates = null, onboard = null, accountSetup = null;
        bool? trophies = null, trophySound = null;
        int? trophyDuration = null;
        double? trophyPositionX = null, trophyPositionY = null;
        string? sort = null, root = null, trophyPreset = null, trophyPosition = null;
        string? trophySoundCue = null;

        if (p.TryGetProperty("closeStoreClientsAfterLaunch", out var c) &&
            (c.ValueKind is JsonValueKind.True or JsonValueKind.False))
            close = c.GetBoolean();
        if (p.TryGetProperty("autoInstallRedistributables", out var a) &&
            (a.ValueKind is JsonValueKind.True or JsonValueKind.False))
            auto = a.GetBoolean();
        if (p.TryGetProperty("minimizeWhilePlaying", out var m) &&
            (m.ValueKind is JsonValueKind.True or JsonValueKind.False))
            min = m.GetBoolean();
        if (p.TryGetProperty("copyPortableIntoLibrary", out var cp) &&
            (cp.ValueKind is JsonValueKind.True or JsonValueKind.False))
            copy = cp.GetBoolean();
        if (p.TryGetProperty("allowResize", out var ar) &&
            (ar.ValueKind is JsonValueKind.True or JsonValueKind.False))
            resize = ar.GetBoolean();
        if (p.TryGetProperty("checkForUpdates", out var cu) &&
            (cu.ValueKind is JsonValueKind.True or JsonValueKind.False))
            updates = cu.GetBoolean();
        if (p.TryGetProperty("onboardingComplete", out var ob) &&
            (ob.ValueKind is JsonValueKind.True or JsonValueKind.False))
            onboard = ob.GetBoolean();
        if (p.TryGetProperty("accountSetupComplete", out var acs) &&
            (acs.ValueKind is JsonValueKind.True or JsonValueKind.False))
            accountSetup = acs.GetBoolean();
        if (p.TryGetProperty("sortMode", out var sm) && sm.ValueKind == JsonValueKind.String)
            sort = sm.GetString();
        if (p.TryGetProperty("defaultInstallRoot", out var dr))
            root = dr.ValueKind == JsonValueKind.String ? dr.GetString() : (dr.ValueKind == JsonValueKind.Null ? "" : null);
        if (p.TryGetProperty("trophyNotificationsEnabled", out var tn) &&
            (tn.ValueKind is JsonValueKind.True or JsonValueKind.False))
            trophies = tn.GetBoolean();
        if (p.TryGetProperty("trophyNotificationSound", out var ts) &&
            (ts.ValueKind is JsonValueKind.True or JsonValueKind.False))
            trophySound = ts.GetBoolean();
        if (p.TryGetProperty("trophyNotificationDurationSeconds", out var td) && td.TryGetInt32(out var seconds))
            trophyDuration = seconds;
        if (p.TryGetProperty("trophyNotificationPreset", out var tp) && tp.ValueKind == JsonValueKind.String)
            trophyPreset = tp.GetString();
        if (p.TryGetProperty("trophyNotificationPosition", out var tpos) && tpos.ValueKind == JsonValueKind.String)
            trophyPosition = tpos.GetString();
        if (p.TryGetProperty("trophyNotificationPositionX", out var tposX) && tposX.TryGetDouble(out var positionX))
            trophyPositionX = positionX;
        if (p.TryGetProperty("trophyNotificationPositionY", out var tposY) && tposY.TryGetDouble(out var positionY))
            trophyPositionY = positionY;
        if (p.TryGetProperty("trophyNotificationSoundCue", out var tsc) && tsc.ValueKind == JsonValueKind.String)
            trophySoundCue = tsc.GetString();
        if (p.TryGetProperty("steamWebApiKey", out var steamKey) &&
            steamKey.ValueKind == JsonValueKind.String &&
            !SteamWebApiKeyStore.Save(steamKey.GetString()))
        {
            throw new InvalidOperationException("Steam Web API key was not saved.");
        }
        _services.Settings.ApplyPatch(
            closeStore: close,
            autoRedist: auto,
            minimizeWhilePlaying: min,
            copyPortable: copy,
            allowResize: resize,
            checkUpdates: updates,
            sortMode: sort,
            defaultInstallRoot: root,
            onboardingComplete: onboard,
            accountSetupComplete: accountSetup,
            trophyNotificationsEnabled: trophies,
            trophyNotificationPreset: trophyPreset,
            trophyNotificationPosition: trophyPosition,
            trophyNotificationPositionX: trophyPositionX,
            trophyNotificationPositionY: trophyPositionY,
            trophyNotificationDurationSeconds: trophyDuration,
            trophyNotificationSound: trophySound,
            trophyNotificationSoundCue: trophySoundCue);
        return BuildSettings();
    }

    private object AchievementGet(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };
        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        // Prefer last successful provider snapshot for immediate detail paint.
        // Detail still calls achievements.refresh for a live account-scoped read;
        // returning empty forever made the row flash "Unavailable" on every open
        // when refresh was slow or flaky.
        var latest = _services.Achievements.GetLatestSnapshot(game);
        if (latest is not null &&
            latest.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete)
        {
            return MapAchievementSnapshot(latest, includeEntries: true);
        }

        var coverage = _services.Achievements.GetCoverage(game);
        return new
        {
            ok = true,
            gameId = game.Id,
            provider = coverage.ProviderId,
            coverage = coverage.Status,
            capabilities = coverage.Capabilities,
            summary = (object?)null,
            achievements = Array.Empty<object>(),
            message = coverage.Message,
        };
    }

    private async Task<object> AchievementRefreshAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Missing game id." };
        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        // Retry — Legendary/Steam cache often lands a beat late. Prefer any
        // usable Partial/Complete over returning blank after a single miss.
        var snapshot = await _services.Achievements.RefreshAsync(game).ConfigureAwait(true);
        if (snapshot.Coverage is not (AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete))
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await Task.Delay(attempt == 0 ? 450 : 900).ConfigureAwait(true);
                    var retry = await _services.Achievements.RefreshAsync(game).ConfigureAwait(true);
                    if (retry.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete)
                    {
                        snapshot = retry;
                        break;
                    }
                }
                catch { /* keep best so far */ }
            }
        }

        // If live refresh still failed, serve the last durable snapshot so the
        // detail rail keeps numbers the user already earned.
        if (snapshot.Coverage is not (AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete))
        {
            var latest = _services.Achievements.GetLatestSnapshot(game);
            if (latest is not null &&
                latest.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete)
                return MapAchievementSnapshot(latest, includeEntries: true);
        }
        return MapAchievementSnapshot(snapshot, includeEntries: true);
    }

    private object PreviewTrophyNotification()
    {
        void Show() => _services.TrophyNotifications.Preview();
        if (!_queue.HasThreadAccess) _queue.TryEnqueue(Show); else Show();
        return new { ok = true };
    }

    public void NotifyWindowState(bool maximized) =>
        PostEvent("shell.window", new { maximized });

    private object ToggleMaximize()
    {
        var maximized = false;
        try { maximized = App.MainAppWindow?.ToggleMaximize() ?? false; } catch { }
        return new { ok = true, maximized };
    }

    private object WindowState()
    {
        var maximized = false;
        try { maximized = App.MainAppWindow?.IsMaximized ?? false; } catch { }
        return new { ok = true, maximized };
    }

    private object HideToNotificationArea()
    {
        void Go()
        {
            try { App.MainAppWindow?.HideForGameplay(); } catch { }
        }
        if (!_queue.HasThreadAccess) _queue.TryEnqueue(Go); else Go();
        return new { ok = true };
    }

    private object CloseWindow()
    {
        void Go()
        {
            try { App.MainAppWindow?.Close(); } catch { }
            // WinUI's dispatcher can outlive its final unpackaged window. End
            // the application loop explicitly after Closed has flushed state;
            // otherwise the user sees no window but Exo keeps its files locked.
            try { Microsoft.UI.Xaml.Application.Current?.Exit(); } catch { }
        }
        if (!_queue.HasThreadAccess) _queue.TryEnqueue(Go); else Go();
        return new { ok = true };
    }

    private static DateTimeOffset _lastSteamProtocolUtc = DateTimeOffset.MinValue;
    private static string? _lastSteamProtocolUri;

    private object OpenUrl(JsonElement p, bool hasParams)
    {
        var url = ReadString(p, hasParams, "url");
        if (string.IsNullOrWhiteSpace(url)) return new { ok = false };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new { ok = false };
        // Browser storefronts, the validated Steam client handoff, and the
        // documented Microsoft Store PDP/search contract only. Never accept an
        // arbitrary custom scheme from the WebView.
        var microsoftStore = uri.Scheme.Equals("ms-windows-store", StringComparison.OrdinalIgnoreCase) &&
                             uri.Host is "pdp" or "search";
        if (uri.Scheme is not ("https" or "http" or "steam") && !microsoftStore)
            return new { ok = false };
        try
        {
            if (uri.Scheme.Equals("steam", StringComparison.OrdinalIgnoreCase))
                return OpenSteamProtocol(uri.AbsoluteUri);

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    /// <summary>
    /// One protocol handoff + reveal main Steam chrome only. Do not restore
    /// steamwebhelper windows — that spawned multiple Steam taskbar entries.
    /// </summary>
    private static object OpenSteamProtocol(string absoluteUri)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(absoluteUri, _lastSteamProtocolUri, StringComparison.OrdinalIgnoreCase) &&
            now - _lastSteamProtocolUtc < TimeSpan.FromSeconds(2))
            return new { ok = true, message = "Already opening Steam." };

        _lastSteamProtocolUri = absoluteUri;
        _lastSteamProtocolUtc = now;

        HiddenStoreRuntime.SuspendFor(StoreKind.Steam, TimeSpan.FromMinutes(30));

        // Process snapshots and window walks used to run here, on the RPC thread,
        // so Buy sat still until they finished. Queue all of it and answer now,
        // the same way ShowStoreAsync does.
        _ = Task.Run(async () =>
        {
            StoreClientCleanup.HideUnused(StoreKind.Steam);
            _ = StoreClientCleanup.ExitUnusedAsync(StoreKind.Steam);
            ProcessHelper.StartProtocol(absoluteUri);

            // Main steam.exe window only — helpers stay off the taskbar.
            var chrome = StoreWindowHider.SteamMainProcessNames;
            StoreWindowHider.RestoreStoreWindows(chrome);
            // Brief settle while Steam creates the store HWND; two nudges max.
            for (var i = 0; i < 2; i++)
            {
                await Task.Delay(600).ConfigureAwait(false);
                StoreWindowHider.RestoreStoreWindows(chrome);
            }
        });
        return new { ok = true };
    }

    private object OpenPath(JsonElement p, bool hasParams)
    {
        var path = ReadString(p, hasParams, "path");
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) && !File.Exists(path))
            return new { ok = false, message = "Path not found." };
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"",
                UseShellExecute = true,
            });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    /// <summary>
    /// Settings → Open a verified installed store client. This is deliberately
    /// an Open action only; it does not imply library or game control support.
    /// </summary>
    private async Task<object> ShowStoreAsync(JsonElement p, bool hasParams)
    {
        // Yield the WebView RPC turn so Settings can paint "Opening…" first.
        await Task.Yield();
        var store = (ReadString(p, hasParams, "store") ?? "steam").Trim().ToLowerInvariant();
        try
        {
            return store switch
            {
                "steam" => OpenVendorClient(
                    StoreKind.Steam,
                    StoreWindowHider.SteamMainProcessNames,
                    ExecutableCommand(SteamAdapter.TryResolveSteamExePublic()),
                    missing: "Steam not found."),
                "epic" => OpenVendorClient(
                    StoreKind.Epic,
                    StoreWindowHider.EpicProcessNames,
                    ExecutableCommand(ResolveEpicLauncherExe()),
                    missing: "Epic Games Launcher not found."),
                "gog" => OpenVendorClient(
                    StoreKind.Gog,
                    StoreWindowHider.GalaxyProcessNames,
                    ExecutableCommand(ResolveGalaxyExe()),
                    missing: "GOG Galaxy not found."),
                "riot" => OpenVendorClient(
                    StoreKind.Riot,
                    StoreWindowHider.RiotUiProcessNames,
                    ExecutableCommand(ResolveRiotClientExe()),
                    missing: "Riot Client not found."),
                "xbox" => OpenOfficialClient("xbox", StoreKind.Xbox, "Xbox app is not installed."),
                "ea" => OpenOfficialClient("ea", StoreKind.Ea, "EA app is not installed."),
                "ubisoft" => OpenOfficialClient("ubisoft", StoreKind.Ubisoft, "Ubisoft Connect is not installed."),
                "battlenet" => OpenOfficialClient("battlenet", StoreKind.BattleNet, "Battle.net is not installed."),
                "amazon" => OpenOfficialClient("amazon", StoreKind.Amazon, "Amazon Games is not installed."),
                "rockstar" => OpenOfficialClient("rockstar", StoreKind.Rockstar, "Rockstar Games Launcher is not installed."),
                "itch" => OpenOfficialClient("itch", StoreKind.Itch, "itch is not installed."),
                "minecraft" => OpenOfficialClient("minecraft", StoreKind.Minecraft, "Minecraft Launcher is not installed."),
                "roblox" => OpenOfficialClient("roblox", StoreKind.Roblox, "Roblox is not installed."),
                "paradox" => OpenOfficialClient("paradox", StoreKind.Paradox, "Paradox Launcher is not installed."),
                "wargaming" => OpenOfficialClient("wargaming", StoreKind.Wargaming, "Wargaming Game Center is not installed."),
                _ => new { ok = false, message = "Unknown store." },
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    private static object OpenVendorClient(
        StoreKind kind,
        string[] processNames,
        StoreClientLaunchCommand? command,
        string missing)
    {
        if (command is null ||
            (!command.IsAppx && (string.IsNullOrWhiteSpace(command.FileName) || !File.Exists(command.FileName))))
            return new { ok = false, message = missing };

        // Do not fight the user for a while after they asked to open the client.
        HiddenStoreRuntime.SuspendFor(kind, TimeSpan.FromMinutes(30));
        try
        {
            // Process.Start itself is a short OS dispatch. Do it before
            // returning so an invalid command cannot become a false success.
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments,
                UseShellExecute = true,
                WorkingDirectory = command.IsAppx ? "" : Path.GetDirectoryName(command.FileName) ?? "",
            });
            if (started is null && !command.IsAppx)
                return new { ok = false, message = $"{kind} did not accept the open request." };
            if (kind == StoreKind.Steam)
                ProcessHelper.StartProtocol(SteamProtocol.OpenMainUri());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Open {kind}: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
            return new { ok = false, message = $"{kind} could not be opened." };
        }

        // Cold clients can spend several seconds creating their main HWND.
        // Cleanup and repeated reveal stay off the WebView RPC thread.
        _ = Task.Run(async () =>
        {
            try
            {
                StoreClientCleanup.HideUnused(kind);
                _ = StoreClientCleanup.ExitUnusedAsync(kind);

                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
                var delayMs = 120;
                do
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    StoreWindowHider.RestoreStoreWindows(processNames);
                    delayMs = Math.Min(900, delayMs + 140);
                }
                while (DateTimeOffset.UtcNow < deadline);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Open {kind}: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
            }
        });
        return new { ok = true, message = $"Opening {kind}…" };
    }

    private object OpenOfficialClient(string adapterId, StoreKind kind, string missing)
    {
        var adapter = _services.FindAdapterById(adapterId) as IOfficialStoreClient;
        return adapter is null
            ? new { ok = false, message = "Official client backend is unavailable." }
            : OpenVendorClient(kind, OfficialClientUiProcessNames(kind), adapter.GetClientLaunchCommand(), missing);
    }

    /// <summary>
    /// Settings → Open is allowed to reveal only named launcher chrome. Keep
    /// this independent from an adapter's wider process observation list:
    /// helpers and services can be necessary for a game and must never be
    /// foregrounded merely because the user opened the launcher.
    /// </summary>
    private static string[] OfficialClientUiProcessNames(StoreKind kind) => kind switch
    {
        StoreKind.Xbox => StoreWindowHider.XboxClientProcessNames,
        StoreKind.Ea => StoreWindowHider.EaClientProcessNames,
        StoreKind.Ubisoft => StoreWindowHider.UbisoftClientProcessNames,
        StoreKind.BattleNet => StoreWindowHider.BattleNetClientProcessNames,
        StoreKind.Amazon => StoreWindowHider.AmazonClientProcessNames,
        StoreKind.Rockstar => StoreWindowHider.RockstarClientProcessNames,
        StoreKind.Itch => StoreWindowHider.ItchClientProcessNames,
        StoreKind.Minecraft => StoreWindowHider.MinecraftClientProcessNames,
        StoreKind.Roblox => StoreWindowHider.RobloxClientProcessNames,
        StoreKind.Paradox => StoreWindowHider.ParadoxClientProcessNames,
        StoreKind.Wargaming => StoreWindowHider.WargamingClientProcessNames,
        _ => [],
    };

    private static StoreClientLaunchCommand? ExecutableCommand(string? executable) =>
        string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)
            ? null
            : new StoreClientLaunchCommand(executable);

    private static string? ResolveEpicLauncherExe() =>
        CliRunner.FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Epic Games", "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe"));

    private static string? ResolveGalaxyExe() =>
        CliRunner.FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "GOG Galaxy", "GalaxyClient.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "GOG Galaxy", "GalaxyClient.exe"));

    private static string? ResolveRiotClientExe() =>
        RiotAdapter.TryResolveRiotClientServicesPublic();

    private async Task<object> PickFolderAsync(JsonElement p, bool hasParams)
    {
        var title = ReadString(p, hasParams, "title") ?? "Choose game folder";

        var tcs = new TaskCompletionSource<object>();
        void Run() => _ = RunPickFolderAsync(title, tcs);

        if (!_queue.HasThreadAccess)
            _queue.TryEnqueue(Run);
        else
            Run();

        return await tcs.Task.ConfigureAwait(true);
    }

    private static async Task RunPickFolderAsync(string title, TaskCompletionSource<object> tcs)
    {
        try
        {
            var window = App.MainAppWindow;
            if (window is null)
            {
                tcs.TrySetResult(new { ok = false, cancelled = true, message = "No window for folder picker." });
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            _ = title;

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                tcs.TrySetResult(new { ok = false, cancelled = true, message = "Cancelled." });
                return;
            }

            tcs.TrySetResult(new { ok = true, cancelled = false, path = folder.Path });
        }
        catch (Exception ex)
        {
            tcs.TrySetResult(new { ok = false, cancelled = false, message = ex.Message });
        }
    }

    private async Task<object> CheckUpdateAsync()
    {
        // Update checks are always on.
        var check = await _services.Updater.CheckAsync(_services.AppVersion).ConfigureAwait(false);
        return new
        {
            ok = true,
            updateAvailable = check.UpdateAvailable,
            latest = check.RemoteVersion,
            current = check.LocalVersion,
            message = check.Message,
            // In-app only — no browser URL for install.
            inApp = true,
        };
    }

    private async Task<object> InstallUpdateAsync()
    {
        void Push(string status, double percent) =>
            PostEvent("app.updateProgress", new { status, percent });

        var progress = new Progress<(string status, double percent)>(p => Push(p.status, p.percent));
        var result = await _services.Updater
            .InstallAsync(_services.AppVersion, progress)
            .ConfigureAwait(true);

        if (result.ShouldExit)
        {
            Push(result.Message, 100);
            // Let the normal Window.Closed path flush active playtime,
            // settings, sync state, tray resources, and bridge services before
            // the installer swaps the application directory.
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(350).ConfigureAwait(false); } catch { /* */ }
                _queue.TryEnqueue(() =>
                {
                    try { App.MainAppWindow?.Close(); } catch { /* installer will time out safely */ }
                    try { Microsoft.UI.Xaml.Application.Current?.Exit(); } catch { /* installer will time out safely */ }
                });
            });
        }

        return new
        {
            ok = result.Installed || result.AlreadyLatest || !result.UpdateAvailable,
            updateAvailable = result.UpdateAvailable,
            alreadyLatest = result.AlreadyLatest,
            installed = result.Installed,
            shouldExit = result.ShouldExit,
            latest = result.RemoteVersion,
            current = result.LocalVersion,
            message = result.Message,
        };
    }

    private async Task<object> DlssStatusAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Pick a game first.", items = Array.Empty<object>() };

        var game = _services.Library.Find(gameId!);
        if (game is null)
            return new { ok = false, message = "Game not found.", items = Array.Empty<object>() };

        var status = await _dlss.GetStatusAsync([game], CancellationToken.None).ConfigureAwait(false);
        return MapDlssStatus(status);
    }

    private async Task<object> DlssUpdateAllAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Pick a game first." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
        }
        if (game is null)
            return new { ok = false, message = "Game not found." };

        try
        {
            var result = await _dlss.UpdateGameAsync(game, CancellationToken.None).ConfigureAwait(true);
            return MapDlssRun(result);
        }
        catch (Exception ex)
        {
            AppLog.Warn("dlss.updateAll failed: " + ex.Message);
            return new { ok = false, updated = 0, skipped = 0, failed = 0, message = "Could not update." };
        }
    }

    private async Task<object> DlssRestoreAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(gameId))
            return new { ok = false, message = "Pick a game first." };

        var game = _services.Library.Find(gameId!);
        if (game is null)
        {
            await _services.Library.GetLibraryAsync().ConfigureAwait(true);
            game = _services.Library.Find(gameId!);
        }
        if (game is null)
            return new { ok = false, message = "Game not found." };

        var result = _dlss.RestoreGame(game);
        return MapDlssRun(result);
    }

    /// <summary>One Newest / Restore press, reported per destination.</summary>
    private static object MapDlssRun(DlssSwapService.UpdateResult result) => new
    {
        ok = result.Ok,
        updated = result.Updated,
        skipped = result.Skipped,
            failed = result.Failed,
            latestVersion = result.LatestVersion,
            latestDisplayVersion = result.LatestDisplayVersion,
            message = result.Message,
        files = (result.Files ?? []).Select(file => new
        {
            fileName = file.FileName,
            state = file.State,
            version = file.Version,
            displayVersion = file.DisplayVersion,
            message = file.Message,
        }).ToList(),
    };

    private static object MapDlssStatus(DlssSwapService.StatusResult status) => new
    {
        ok = status.Ok,
        latestVersion = status.LatestVersion,
        latestDisplayVersion = status.LatestDisplayVersion,
        alreadyBest = status.AlreadyBest,
        message = status.Message,
        antiCheatWarning = status.AntiCheatWarning,
        items = status.Items.Select(item => new
        {
            path = item.Path,
            fileName = item.FileName,
            kind = item.Kind,
            gameId = item.GameId,
            gameTitle = item.GameTitle,
            currentVersion = item.CurrentVersion,
            currentDisplayVersion = item.CurrentDisplayVersion,
            fileVersion = item.CurrentVersion,
            latestVersion = item.LatestVersion,
            packVersion = item.LatestVersion,
            packDisplayVersion = item.LatestDisplayVersion,
            displayName = DlssSwapService.DisplayName(item.Kind),
            eligible = item.Eligible,
            present = item.Present,
            canRestore = item.CanRestore,
            unsupportedReason = item.UnsupportedReason,
            skipReason = item.SkipReason,
        }).ToList(),
    };

    private object MapGame(GameEntry g, bool discoverExternalRunningGame = false)
    {
        var runState = _services.Launcher.GetGameRunState(g, discoverExternalRunningGame);
        return new
        {
        id = g.Id,
        title = g.Title,
        store = g.Store.ToString().ToLowerInvariant(),
        // `store` remains the deterministic selected source. `stores` and
        // `variants` let a card show alternate active sources without exposing
        // account identity or replacing any exact action ids.
        stores = (g.Variants.Count == 0 ? new[] { g.Store } : g.Variants.Select(v => v.Store))
            .Distinct()
            .Select(store => store.ToString().ToLowerInvariant())
            .ToArray(),
        canonicalTitleKey = g.CanonicalTitleKey,
        selectedVariantId = g.SelectedVariantId ?? g.Id,
        variants = g.Variants.Select(variant =>
        {
            // game.get is the deliberate, selected-card reconciliation point.
            // Scan every exact source on that card there, otherwise a running
            // alternate-store copy stays invisible until the user happens to
            // switch sources and triggers another round trip. library.get keeps
            // this false so the grid never enumerates every installed process.
            var variantRunState = _services.Launcher.GetGameRunState(
                variant.ToGameEntry(g), discoverExternalRunningGame);
            return new
            {
                id = variant.Id,
                store = variant.Store.ToString().ToLowerInvariant(),
                installed = variant.Installed,
                owned = variant.Owned,
                entitlementState = variant.EntitlementState,
                updateAvailable = variant.UpdateAvailable,
                canInstall = variant.CanInstall,
                primaryAction = variant.PrimaryAction,
                path = variant.Path,
                launchTarget = variant.LaunchTarget,
                playtimeMinutes = variant.PlaytimeMinutes,
                lastPlayedUtc = variant.LastPlayedUtc?.ToString("O"),
                status = variant.Status,
                isRunning = variantRunState.IsRunning,
                canStop = variantRunState.CanStop,
            };
        }).ToArray(),
        installed = g.Installed,
        owned = g.Owned,
        entitlementState = g.EntitlementState,
        updateAvailable = g.UpdateAvailable,
        canInstall = g.CanInstall,
        primaryAction = g.PrimaryAction,
        path = g.Path,
        coverUrl = g.CoverUrl,
        coverSource = g.CoverSource,
        artRevision = g.ArtRevision,
        playtimeMinutes = g.PlaytimeMinutes,
        sizeBytes = g.SizeBytes,
        status = g.Status,
        deps = g.Deps,
        launchNote = g.LaunchNote,
        launchTarget = g.LaunchTarget,
        lastPlayedUtc = g.LastPlayedUtc?.ToString("O"),
        isFavorite = g.IsFavorite,
        isAddPortable = string.Equals(g.Id, "local:add", StringComparison.OrdinalIgnoreCase),
        isRunning = runState.IsRunning,
        canStop = runState.CanStop,
        buyUrl = UiFormat.BuyUrl(g),
        };
    }

    private static object MapAchievementSnapshot(AchievementSnapshot snapshot, bool includeEntries)
    {
        var unlocked = snapshot.ReportedUnlocked ?? snapshot.Entries.Count(entry => entry.State.Unlocked);
        var total = snapshot.ReportedTotal ?? snapshot.Entries.Count;
        var complete = snapshot.Coverage is AchievementCoverageStatus.Partial or AchievementCoverageStatus.Complete;
        // Complete 0/0 is a confirmed empty catalog (UI shows None). Partial 0/0
        // is not progress and must not paint a summary.
        var hasProgress = unlocked > 0 || total > 0 || snapshot.Entries.Count > 0;
        var confirmedEmpty = snapshot.Coverage == AchievementCoverageStatus.Complete &&
                             unlocked == 0 && total == 0 && snapshot.Entries.Count == 0;
        var summary = (complete && hasProgress) || confirmedEmpty
            ? new
            {
                unlocked,
                total,
                completionPercent = total > 0 ? Math.Round(unlocked * 100d / total, 1) : (double?)null,
                perfected = total > 0 && unlocked >= total && snapshot.Coverage == AchievementCoverageStatus.Complete,
                observedAt = snapshot.ObservedAtUtc,
            }
            : null;
        var entries = includeEntries
            ? snapshot.Entries
                .OrderByDescending(entry => entry.State.Unlocked)
                .ThenByDescending(entry => entry.State.UnlockedAtUtc)
                .ThenBy(entry => entry.Definition.Name, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .Select(entry =>
                {
                    var redact = entry.Definition.Hidden && !entry.State.Unlocked;
                    return new
                    {
                        id = entry.Definition.ExternalId,
                        name = redact ? "Hidden achievement" : entry.Definition.Name,
                        description = redact ? string.Empty : entry.Definition.Description,
                        hidden = entry.Definition.Hidden,
                        iconUrl = redact ? null : AchievementIconCache.SanitizeProviderImageUrl(
                            entry.Definition.IconUnlockedUrl),
                        rarityPercent = redact ? null : entry.Definition.GlobalUnlockPercent,
                        points = redact ? null : entry.Definition.Points,
                        tier = redact ? null : entry.Definition.Tier,
                        unlocked = entry.State.Unlocked,
                        unlockedAt = entry.State.UnlockedAtUtc,
                        progressCurrent = redact ? null : entry.State.ProgressCurrent,
                        progressTarget = redact ? null : entry.State.ProgressTarget,
                    };
                })
                .Cast<object>()
                .ToArray()
            : Array.Empty<object>();

        return new
        {
            ok = true,
            gameId = snapshot.GameId,
            provider = snapshot.ProviderId,
            sourceGameId = snapshot.SourceGameId,
            coverage = snapshot.Coverage,
            capabilities = new
            {
                progress = snapshot.Capabilities.HasFlag(AchievementProviderCapabilities.Progress),
                rarity = snapshot.Capabilities.HasFlag(AchievementProviderCapabilities.Rarity),
                completeCatalog = snapshot.Capabilities.HasFlag(AchievementProviderCapabilities.CompleteCatalog),
            },
            summary,
            achievements = entries,
            message = snapshot.Message,
        };
    }

    private static object MapProgress(InstallProgress p) => new
    {
        gameId = p.GameId,
        phase = p.Phase.ToString().ToLowerInvariant(),
        percent = p.Percent,
        bytesDownloaded = p.BytesDownloaded,
        bytesToDownload = p.BytesToDownload,
        bytesPerSecond = p.BytesPerSecond,
        status = p.Status,
        canCancel = p.CanCancel,
        isActive = p.IsActive,
    };

    private static string? ReadString(JsonElement p, bool hasParams, string name)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }

    private static bool? ReadBool(JsonElement p, bool hasParams, string name)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
        return null;
    }

    private static int? ReadInt(JsonElement p, bool hasParams, string name)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        return p.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static object MapDepAwareResult(LaunchResult result) => new
    {
        ok = result.Ok,
        message = result.Message,
        processId = result.ProcessId,
        backendStarted = result.BackendStarted,
        handoffOnly = result.HandoffOnly,
        needsDependencies = result.NeedsDependencies,
        missingDependencies = MapMissingDeps(result.MissingDependencies),
    };

    private static object MapMissingDeps(IReadOnlyList<DependencyInfo> deps) =>
        deps.Select(d => new
        {
            id = d.Id,
            name = d.Name,
            status = d.Status,
            canOfferInstall = d.CanOfferInstall,
            officialUrl = d.OfficialUrl,
        }).ToArray();

    private void PostResponse(string id, bool ok, object? result = null, string? error = null)
        => PostSerialized(ExoBridgeProtocol.SerializeResponse(id, ok, result, error));

    private void PostEvent(string name, object? data)
        => PostSerialized(ExoBridgeProtocol.SerializeEvent(name, data));

    private void PostSerialized(string json)
    {
        try
        {
            var web = _web;
            if (web is null) return;

            void Send()
            {
                try { web.PostWebMessageAsJson(json); } catch { }
            }

            if (!_queue.HasThreadAccess)
                _queue.TryEnqueue(Send);
            else
                Send();
        }
        catch { }
    }
}
