using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
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
    private bool _detached;

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
        _services.Launcher.ProgressChanged += OnProgress;
        _services.Launcher.GameSessionCompleted += OnGameSessionCompleted;
        _services.Library.LibraryUpdated += OnLibraryUpdated;
        _services.Achievements.SnapshotUpdated += OnAchievementSnapshotUpdated;
    }

    public void Attach(CoreWebView2 web)
    {
        _services.GogAuth.AttachDispatcher(_queue);
        _web = web;
        web.WebMessageReceived += OnMessage;
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
    }

    private void OnProgress(InstallProgress p) =>
        PostEvent("install.progress", MapProgress(p));

    private void OnGameSessionCompleted(GameEntry game) =>
        PostEvent("launch.status", new
        {
            gameId = game.Id,
            ok = true,
            message = "Game closed.",
            phase = "stopped",
        });

    private void OnAchievementSnapshotUpdated(AchievementSnapshot snapshot) =>
        PostEvent("achievements.updated", MapAchievementSnapshot(snapshot, includeEntries: true));

    private void OnLibraryUpdated()
    {
        try
        {
            var games = _services.Library.RefreshCovers();
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
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;
            var hasParams = root.TryGetProperty("params", out var paramsEl);

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(method))
                return;

            object? result = method switch
            {
                "library.get" => await LibraryGetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "library.refresh" => await LibraryGetAsync(paramsEl, hasParams: true, force: true).ConfigureAwait(true),
                "game.get" => await GameGetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.launch" => await GameLaunchAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.stop" => await GameStopAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.install" => await GameInstallAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.update" => await GameUpdateAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.uninstall" => await GameUninstallAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.openFolder" => GameOpenFolder(paramsEl, hasParams),
                "game.toggleFavorite" => GameToggleFavorite(paramsEl, hasParams),
                "game.cancelInstall" => _services.Launcher.Cancel(),
                "game.progress" => GameProgress(paramsEl, hasParams),
                "achievements.get" => AchievementGet(paramsEl, hasParams),
                "achievements.refresh" => await AchievementRefreshAsync(paramsEl, hasParams).ConfigureAwait(true),
                "stores.auth" => await StoresAuthAsync(paramsEl, hasParams).ConfigureAwait(true),
                "stores.search" => await StoresSearchAsync(paramsEl, hasParams).ConfigureAwait(true),
                "deps.list" => DepsList(),
                "deps.offerInstall" => DepsOfferInstall(paramsEl, hasParams),
                "stores.matrix" => _services.Library.StoreMatrix(),
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
                _ => throw new InvalidOperationException($"Unknown method: {method}")
            };

            PostResponse(id!, ok: true, result: result);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"RPC {id}: {ex.Message}");
            if (id is not null)
                PostResponse(id, ok: false, error: ex.Message);
        }
    }

    private async Task<object> LibraryGetAsync(JsonElement p, bool hasParams, bool force = false)
    {
        if (hasParams && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True)
            force = true;

        var games = await _services.Library.GetLibraryAsync(force).ConfigureAwait(true);
        var settings = _services.Settings.Current;
        return new
        {
            games = games.Select(game => MapGame(game)).ToList(),
            count = games.Count,
            stores = _services.Library.StoreMatrix(),
            progress = MapProgress(_services.Launcher.CurrentProgress),
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

        return new { ok = true, game = MapGame(game, discoverExternalRunningGame: true) };
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
                Owned = true,
                CanInstall = true,
                Path = fromLibrary.Path,
                LaunchTarget = fromLibrary.LaunchTarget,
                CoverUrl = fromLibrary.CoverUrl,
                Status = fromLibrary.Installed ? fromLibrary.Status : "Not installed",
                Deps = fromLibrary.Deps,
                LaunchNote = fromLibrary.LaunchNote,
            };
        }

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
                (game.Owned || game.CanInstall || game.Installed))
                return game;
            var variant = game.Variants.FirstOrDefault(item =>
                string.Equals(item.Id, gameId, StringComparison.OrdinalIgnoreCase));
            if (variant is not null && (variant.Owned || variant.CanInstall || variant.Installed))
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

        var warm = CoverArtService.WarmCacheAsync(needsArt, requested: true, onBatchDone: () =>
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
        if (result.Ok)
            _services.Library.Invalidate();
        return new { ok = result.Ok, message = result.Message };
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

    private async Task<object> StoresAuthAsync(JsonElement p, bool hasParams)
    {
        var storeId = ReadString(p, hasParams, "store");
        if (string.IsNullOrWhiteSpace(storeId))
            return new { ok = false, message = "Missing store id." };

        var adapter = _services.FindAdapterById(storeId!);
        if (adapter is null)
            return new { ok = false, message = "Unknown store." };

        var result = await adapter.AuthenticateAsync().ConfigureAwait(true);
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
            trophyNotificationsEnabled = s.TrophyNotificationsEnabled,
            trophyNotificationPreset = s.TrophyNotificationPreset,
            trophyNotificationPosition = s.TrophyNotificationPosition,
            trophyNotificationPositionX = s.TrophyNotificationPositionX,
            trophyNotificationPositionY = s.TrophyNotificationPositionY,
            trophyNotificationDurationSeconds = s.TrophyNotificationDurationSeconds,
            trophyNotificationSound = s.TrophyNotificationSound,
            trophyNotificationSoundCue = s.TrophyNotificationSoundCue,
        };
    }

    private object SetSettings(JsonElement p, bool hasParams)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object)
            return BuildSettings();

        bool? close = null, auto = null, min = null, copy = null, resize = null, updates = null, onboard = null;
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
        var latest = _services.Achievements.GetLatestSnapshot(game.Id);
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
            var latest = _services.Achievements.GetLatestSnapshot(game.Id);
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
        // http(s) for browser storefronts; steam:// for the Steam desktop client
        // (Buy on Steam must land on the in-app store page, not Chrome).
        if (uri.Scheme is not ("https" or "http" or "steam")) return new { ok = false };
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
        StoreClientCleanup.HideUnused(StoreKind.Steam);
        _ = StoreClientCleanup.ExitUnusedAsync(StoreKind.Steam);
        ProcessHelper.StartProtocol(absoluteUri);

        // Main steam.exe window only — helpers stay off the taskbar.
        var chrome = StoreWindowHider.SteamMainProcessNames;
        StoreWindowHider.RestoreStoreWindows(chrome);
        _ = Task.Run(async () =>
        {
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
        StoreClientCleanup.HideUnused(kind);
        _ = StoreClientCleanup.ExitUnusedAsync(kind);

        // Cold Epic/Riot clients can spend several seconds starting helpers.
        // Queue all shell work so the Settings bridge responds immediately.
        _ = Task.Run(async () =>
        {
            try
            {
                // Re-invoke the main executable even when a helper process is
                // already alive. All supported clients are single-instance;
                // this both cold-starts them and asks an existing instance to
                // surface its main window. An orphan helper must not suppress
                // the explicit Settings -> Open action.
                using var started = Process.Start(new ProcessStartInfo
                {
                    FileName = command.FileName,
                    Arguments = command.Arguments,
                    UseShellExecute = true,
                    WorkingDirectory = command.IsAppx ? "" : Path.GetDirectoryName(command.FileName) ?? "",
                });
                if (kind == StoreKind.Steam)
                    ProcessHelper.StartProtocol(SteamProtocol.OpenMainUri());

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
        updateAvailable = g.UpdateAvailable,
        canInstall = g.CanInstall,
        primaryAction = g.PrimaryAction,
        path = g.Path,
        coverUrl = g.CoverUrl,
        coverSource = g.CoverSource,
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
                .Select(entry => new
                {
                    id = entry.Definition.ExternalId,
                    name = entry.Definition.Name,
                    description = entry.Definition.Description,
                    hidden = entry.Definition.Hidden,
                    iconUrl = entry.Definition.IconUnlockedUrl,
                    rarityPercent = entry.Definition.GlobalUnlockPercent,
                    points = entry.Definition.Points,
                    tier = entry.Definition.Tier,
                    unlocked = entry.State.Unlocked,
                    unlockedAt = entry.State.UnlockedAtUtc,
                    progressCurrent = entry.State.ProgressCurrent,
                    progressTarget = entry.State.ProgressTarget,
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
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["ok"] = ok,
        };
        if (ok) payload["result"] = result;
        else payload["error"] = error ?? "error";
        PostJson(payload);
    }

    private void PostEvent(string name, object? data)
    {
        PostJson(new Dictionary<string, object?>
        {
            ["event"] = name,
            ["data"] = data,
        });
    }

    private void PostJson(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
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
