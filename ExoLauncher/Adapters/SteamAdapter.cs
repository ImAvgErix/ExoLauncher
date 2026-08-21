using System.Collections.Concurrent;
using System.Diagnostics;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam library via appmanifest + minimized install/launch.
/// Steam runtime usually remains installed; user should not need to open Steam day-to-day.
/// Anonymous SteamCMD is NOT used for owned paid games.
/// </summary>
public sealed class SteamAdapter : IStoreAdapter, IInstalledSteamManifestSource, IStoreAccountScope, IAuthoritativeOwnershipSource, IStoreRepair
{
    private readonly ConcurrentDictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private static int _staleCleanupScheduled;

    public IReadOnlySet<string>? LastAuthoritativeOwnedAppIds { get; private set; }

    public StoreKind Store => StoreKind.Steam;
    public string Id => "steam";
    public string DisplayName => "Steam";

    public string? GetActiveAccountScope()
    {
        var root = ResolveSteamRoot();
        return root is null ? null : SteamPlaytime.GetActiveAccountScope(root);
    }

    public bool IsAgentPresent() => ResolveSteamExe() is not null;

    public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return Task.FromResult(new AuthResult
            {
                Ok = false,
                RequiresUserAction = true,
                Message = "Install Steam first.",
            });

        // Official Steam login window — Exo never collects a Steam password.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new AuthResult
            {
                Ok = false,
                RequiresUserAction = true,
                Message = "Could not open Steam. " + ex.Message,
            });
        }
        return Task.FromResult(new AuthResult
        {
            Ok = true,
            RequiresUserAction = true,
            Message = "Steam opened. Sign in there if asked, then come back.",
        });
    }

    public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry>();
        var steamRoot = ResolveSteamRoot();
        if (steamRoot is null)
        {
            LastAuthoritativeOwnedAppIds = null;
            return games;
        }
        // Leftover download cleanup walks large trees. Never block the library
        // scan — a cancelled 50 GB leftover used to trip the 25s adapter timeout.
        ScheduleStaleDownloadCleanup(steamRoot);
        var activeAccount = SteamPlaytime.LoadActiveAccount(steamRoot);
        IReadOnlySet<string>? authoritativeOwnedAppIds = null;
        var apiKey = SteamWebApiKeyStore.TryRead();
        var steamId64 = SteamFriends.LoadSelfSteamId64(steamRoot);
        if (apiKey is not null && steamId64 is not null)
        {
            var ownedResult = await SteamWebApi.LoadOwnedGamesAsync(apiKey, steamId64, ct).ConfigureAwait(false);
            if (ownedResult.Authoritative)
                authoritativeOwnedAppIds = ownedResult.AppIds;
        }
        LastAuthoritativeOwnedAppIds = authoritativeOwnedAppIds;
        var presentAppIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var lib in CollectLibraryFolders(steamRoot))
        {
            ct.ThrowIfCancellationRequested();
            var steamApps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var acf in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(acf);
                    if (!SteamProtocol.TryParseAppManifest(text, out var appId, out var name, out var installDir, out var size)
                        || appId is null || name is null)
                        continue;

                    var path = string.IsNullOrWhiteSpace(installDir)
                        ? null
                        : Path.Combine(steamApps, "common", installDir);
                    var pathExists = path is not null && Directory.Exists(path);

                    // Hide tools / redistributables / non-games (Steamworks Common, SDKs, …).
                    if (IsNonGameSteamEntry(appId, name, installDir))
                        continue;

                    // StateFlags is a bitfield — do not compare to the string "4".
                    var stateFlags = SteamProtocol.MatchAcfField(text, "StateFlags");
                    var installed = SteamStateFlags.IsInstalledPresence(pathExists, stateFlags);
                    long? bytesToDownload = null;
                    long? bytesDownloaded = null;
                    if (long.TryParse(SteamProtocol.MatchAcfField(text, "BytesToDownload"), out var btd) && btd > 0)
                        bytesToDownload = btd;
                    if (long.TryParse(SteamProtocol.MatchAcfField(text, "BytesDownloaded"), out var bd) && bd >= 0)
                        bytesDownloaded = bd;
                    var buildId = SteamProtocol.MatchAcfField(text, "buildid");
                    var targetBuildId = SteamProtocol.MatchAcfField(text, "TargetBuildID");
                    var updateAvailable = SteamStateFlags.IsUpdateAvailable(
                        stateFlags, installed, bytesToDownload, bytesDownloaded) ||
                        SteamStateFlags.HasPendingTargetBuild(buildId, targetBuildId);
                    var play = activeAccount is not null &&
                               activeAccount.Entries.TryGetValue(appId, out var accountPlay)
                        ? accountPlay
                        : (SteamPlaytime.Entry?)null;
                    // App manifests, localconfig entries, app tickets, and
                    // librarycache files can all outlive a refund. They prove
                    // installation/history, not the current account's license.
                    var ownershipVerified = authoritativeOwnedAppIds is not null;
                    var ownedByActiveAccount = authoritativeOwnedAppIds?.Contains(appId) == true;
                    var entitlementStatus = !ownershipVerified
                        ? "Ownership unverified"
                        : ownedByActiveAccount
                            ? (installed ? (updateAvailable ? "Update" : "Ready") : "Not installed")
                            : "Buy again";

                    presentAppIds.Add(appId);
                    games.Add(new GameEntry
                    {
                        Id = "steam:" + appId,
                        Title = name,
                        Store = StoreKind.Steam,
                        Installed = installed,
                        Owned = ownedByActiveAccount,
                        EntitlementState = !ownershipVerified
                            ? EntitlementState.Unverified
                            : ownedByActiveAccount
                                ? EntitlementState.Owned
                                : EntitlementState.NotOwned,
                        CanInstall = !installed && ownedByActiveAccount,
                        UpdateAvailable = ownedByActiveAccount && updateAvailable,
                        Path = path,
                        LaunchTarget = appId,
                        // Native CoverArtService resolves official Steam art into its cache.
                        CoverUrl = null,
                        SizeBytes = size,
                        PlaytimeMinutes = play is { Minutes: > 0 } ? play.Value.Minutes : null,
                        LastPlayedUtc = play?.LastPlayedUtc,
                        Status = entitlementStatus,
                        Deps = new[] { "Steam client" },
                        LaunchNote = !ownershipVerified
                            ? "Installed files found. Ownership is unverified for the active Steam account."
                            : ownedByActiveAccount
                                ? "Ownership verified for this Steam account. Launches through Steam."
                                : "Installed files found, but this Steam account does not currently own the game. Buy it again through Steam.",
                    });
                }
                catch { /* skip corrupt manifests */ }
            }
        }

        // appinfo.vdf is only needed for owned-not-installed titles. Installed
        // rows already have names from appmanifest. Steam rewrites appinfo often.
        // Use account-local cache only as a name index for ids already present
        // in the authoritative current-account snapshot. It is never the source
        // of the ownership claim itself.
        var ownedIds = authoritativeOwnedAppIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : authoritativeOwnedAppIds.ToHashSet(StringComparer.Ordinal);
        var appNames = ownedIds.Any(id => !presentAppIds.Contains(id))
            ? SteamAppInfoNames.Load(Path.Combine(steamRoot, "appcache", "appinfo.vdf"))
            : new Dictionary<string, SteamAppInfoNames.Entry>(StringComparer.Ordinal);
        foreach (var extra in SteamAccountLibrary.UninstalledOwnedGames(
                     ownedIds, presentAppIds, appNames, authoritativeOwnedAppIds))
        {
            if (IsNonGameSteamEntry(extra.LaunchTarget ?? "", extra.Title, null))
                continue;
            games.Add(extra);
        }

        return games;
    }

    private static void ScheduleStaleDownloadCleanup(string steamRoot)
    {
        if (Interlocked.CompareExchange(ref _staleCleanupScheduled, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { Services.SteamLeftoverCleanup.CleanStale(steamRoot); }
            catch { /* leftover folders are best-effort */ }
            finally { Interlocked.Exchange(ref _staleCleanupScheduled, 0); }
        });
    }

    /// <summary>
    /// Steam library includes tools and redistributables that are not playable titles.
    /// </summary>
    internal static bool IsNonGameSteamEntry(string appId, string name, string? installDir)
    {
        // Known tool / redistributable app ids
        if (appId is "228980" // Steamworks Common Redistributables
            or "1070560" // Steam Linux Runtime
            or "1391110" or "1493710" or "1628350" // Proton / Steam Linux runtimes
            or "2180100" or "2805730")
            return true;

        var n = (name ?? "").Trim();
        if (n.Length == 0) return true;
        var lower = n.ToLowerInvariant();
        string[] junk =
        [
            "steamworks common redistributables",
            "steamworks redistributable",
            "redistributable",
            "directx redistributable",
            "visual c++",
            "microsoft visual c",
            "proton ",
            "steam linux runtime",
            "steamworks sdk",
            " dedicated server",
            " server dedicated",
            "sdk",
            "benchmark",
            "playtest",
            "soundtrack",
            " - tools",
            "content creator",
            "software development kit",
        ];
        foreach (var j in junk)
        {
            if (lower.Contains(j, StringComparison.Ordinal)) return true;
        }

        var dir = (installDir ?? "").ToLowerInvariant();
        if (dir.Contains("steamworks shared", StringComparison.Ordinal) ||
            dir.Contains("steamworks", StringComparison.Ordinal) && dir.Contains("redist", StringComparison.Ordinal))
            return true;

        return false;
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        // installPath is ignored for Steam — library folders are managed by the Steam client.
        _ = installPath;
        var appId = game.LaunchTarget;
        if (!SteamProtocol.IsValidAppId(appId))
            return new InstallResult { Ok = false, Message = "Missing or invalid Steam app id." };

        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return new InstallResult { Ok = false, Message = "Steam is not installed." };

        Report(game.Id, progress, InstallPhase.Preparing, null, "Starting Steam…");

        // Cancel may only undo what this request created. A manifest that already
        // exists is the user's install (a paused or partially downloaded game
        // still has one), and uninstalling it on cancel deleted real files.
        var appManifestExistedBeforeRequest = FindAppManifestPath(appId) is not null;

        using var hider = StoreWindowHider.ForSteam();

        try
        {
            hider.Start(TimeSpan.FromMinutes(90), restoreOnStop: false);
            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ProcessHelper.StartHidden(
                    steamExe,
                    SteamUpdateCommandPlan.HiddenClientStartArguments());
                await WaitForSteamCommandListenerAsync(ct).ConfigureAwait(false);
            }

            Report(game.Id, progress, InstallPhase.Downloading, null,
                "Starting Steam install…");
            if (await CommandSteamIpcAsync("install", appId, ct).ConfigureAwait(false) != SteamIpcStatus.Ok)
                NudgeSteamUpdate(steamExe, game, appId);

            var start = DateTimeOffset.UtcNow;
            var sawManifest = false;
            var nextIpc = start.AddSeconds(20);
            long lastSize = 0;
            var stableTicks = 0;
            var steamRoot = ResolveSteamRoot();
            var baseline = ReadAppManifestSnapshot(appId);
            var sampler = new SteamWatchSampler();

            while (!ct.IsCancellationRequested)
            {
                var elapsed = (DateTimeOffset.UtcNow - start).TotalSeconds;
                var hit = FindInstalled(appId);
                var snap = ReadAppManifestSnapshot(appId);
                var transfer = sampler.ReadTransfer(appId, steamRoot, snap, baseline);
                if (hit is not null || snap.StateFlags is not null || transfer.ToDownload is not null)
                    sawManifest = true;

                var liveBytes = transfer.ToDownload is > 0 &&
                                transfer.Downloaded is not null &&
                                transfer.Percent is > 0;
                var size = hit is not null ? sampler.ReadInstalledSize(hit.Value.Path) : 0L;
                var status = liveBytes
                    ? $"Downloading {game.Title}… ({FormatBytes(transfer.Downloaded ?? 0)} / {FormatBytes(transfer.ToDownload!.Value)})"
                    : transfer.ToDownload is > 0 || snap.StateFlags is not null
                        ? $"Downloading {game.Title}…"
                        : size > 0
                            ? $"Downloading {game.Title}… ({FormatBytes(size)})"
                            : sawManifest
                                ? $"Installing {game.Title}…"
                                : "Starting Steam install…";
                Report(game.Id, progress, InstallPhase.Installing, transfer.Percent, status,
                    transfer.Downloaded is > 0 ? transfer.Downloaded : null,
                    transfer.ToDownload is > 0 ? transfer.ToDownload : null);

                if (hit is not null)
                {
                    var ready = SteamStateFlags.IsFullyInstalled(snap.StateFlags) &&
                                !SteamStateFlags.IsBusy(snap.StateFlags, snap.BytesToDownload, snap.BytesDownloaded);
                    if (ready)
                    {
                        if (size >= lastSize) stableTicks++;
                        else stableTicks = 0;
                        lastSize = size;
                        if (stableTicks >= 2)
                        {
                            Report(game.Id, progress, InstallPhase.Completed, 100, "Installed.");
                            return new InstallResult
                            {
                                Ok = true,
                                Message = "Installed via Steam.",
                                Path = hit.Value.Path,
                            };
                        }
                    }
                    else
                    {
                        stableTicks = 0;
                        lastSize = size;
                    }
                }

                if (!sawManifest && DateTimeOffset.UtcNow >= nextIpc)
                {
                    if (await CommandSteamIpcAsync("install", appId, ct).ConfigureAwait(false) != SteamIpcStatus.Ok)
                        NudgeSteamUpdate(steamExe, game, appId);
                    nextIpc = DateTimeOffset.UtcNow.AddSeconds(20);
                }

                if (elapsed > 120 * 60)
                {
                    Report(game.Id, progress, InstallPhase.Failed, null, "Install watch timed out.");
                    return new InstallResult { Ok = false, Message = "Steam install timed out." };
                }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }

            StopFreshSteamInstall(appId, appManifestExistedBeforeRequest);
            const string cancelled = "Cancelled.";
            Report(game.Id, progress, InstallPhase.Cancelled, null, cancelled);
            return new InstallResult { Ok = false, Message = cancelled };
        }
        catch (OperationCanceledException)
        {
            StopFreshSteamInstall(appId, appManifestExistedBeforeRequest);
            const string cancelled = "Cancelled.";
            Report(game.Id, progress, InstallPhase.Cancelled, null, cancelled);
            return new InstallResult { Ok = false, Message = cancelled };
        }
        catch (Exception ex)
        {
            Report(game.Id, progress, InstallPhase.Failed, null, ex.Message);
            return new InstallResult { Ok = false, Message = ex.Message };
        }
    }

    private static void StopFreshSteamInstall(string appId, bool appManifestExistedBeforeRequest)
    {
        if (!SteamStateFlags.CanRollBackCancelledInstall(appManifestExistedBeforeRequest))
        {
            AppLog.Info(
                $"Steam install cancel: appId={appId}; left the pre-existing install in place.");
            return;
        }

        var status = SteamClientIpc.Command("uninstall", appId);
        AppLog.Info($"Steam install cancel: appId={appId}; ipc={status}.");
    }

    /// <summary>
    /// Re-send the exact selected app's install/update request as a fallback
    /// if Steam IPC is missing or this client interface is too new.
    /// </summary>
    private static void NudgeSteamUpdate(string steamExe, GameEntry game, string appId)
    {
        foreach (var command in SteamUpdateCommandPlan.BuildNudge(appId))
        {
            try
            {
                AppLog.Info(
                    $"Steam update request: gameId={game.Id}; appId={appId}; purpose={command.Purpose}.");
                ProcessHelper.StartHidden(steamExe, command.Arguments);
                StoreWindowHider.HideOnce(StoreWindowHider.SteamProcessNames);
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Steam update request failed for gameId={game.Id}; appId={appId}: {ex.Message}");
            }
        }
    }

    private static async Task<SteamIpcStatus> CommandSteamIpcAsync(
        string action,
        string appId,
        CancellationToken ct,
        bool retryCommandFailure = true)
    {
        var last = SteamIpcStatus.Unavailable;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            last = SteamClientIpc.Command(action, appId);
            if (last == SteamIpcStatus.Ok)
                return last;
            // A missing helper is not a transient failure. Four more spawns and
            // 4.5s of delay only postponed the protocol fallback the caller
            // already has.
            if (last == SteamIpcStatus.HostMissing)
                return last;
            if (last == SteamIpcStatus.CommandFailed && !retryCommandFailure)
                return last;
            await Task.Delay(1500, ct).ConfigureAwait(false);
        }

        return last;
    }

    private static async Task WaitForSteamCommandListenerAsync(CancellationToken ct)
    {
        // steam.exe appears before its single-instance command listener is ready.
        // Waiting for the UI helper (with a bounded fallback) prevents the first
        // exact app request from being lost during a cold client start.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline
               && (!ProcessHelper.IsProcessRunning("steam")
                   || !ProcessHelper.IsProcessRunning("steamwebhelper")))
        {
            await Task.Delay(350, ct).ConfigureAwait(false);
        }

        await Task.Delay(750, ct).ConfigureAwait(false);
    }

    public async Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        // Real Steam game update: request install/update URI, then poll appmanifest until ready.
        var appId = game.LaunchTarget;
        if (!SteamProtocol.IsValidAppId(appId))
            return new InstallResult { Ok = false, Message = "Missing or invalid Steam app id." };

        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return new InstallResult { Ok = false, Message = "Steam is not installed." };

        using var hider = StoreWindowHider.ForSteam();

        try
        {
            // Arm suppression before starting or messaging Steam so protocol
            // handling cannot flash or foreground the store window.
            hider.Start(TimeSpan.FromMinutes(90));
            Report(game.Id, progress, InstallPhase.Preparing, null, "Starting Steam…");
            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ProcessHelper.StartHidden(
                    steamExe,
                    SteamUpdateCommandPlan.HiddenClientStartArguments());
                await WaitForSteamCommandListenerAsync(ct).ConfigureAwait(false);
            }

            var initial = ReadAppManifestSnapshot(appId);
            if (string.IsNullOrWhiteSpace(initial.Name) ||
                !string.Equals(initial.Name.Trim(), game.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                const string identityError =
                    "Steam update was refused because the selected game did not match its app manifest.";
                Report(game.Id, progress, InstallPhase.Failed, null, identityError);
                return new InstallResult { Ok = false, Message = identityError };
            }

            var queuedAtStart = initial.BytesToDownload is > 0
                && (initial.BytesDownloaded is null or 0);
            var neededAtStart = SnapshotNeedsUpdate(initial) || queuedAtStart;

            Report(game.Id, progress, InstallPhase.Downloading, null, "Starting Steam update…");

            if (queuedAtStart)
                ReleaseScheduledSteamUpdate(appId, game.Title);

            if (await CommandSteamIpcAsync("update", appId, ct).ConfigureAwait(false) != SteamIpcStatus.Ok)
                NudgeSteamUpdate(steamExe, game, appId);

            var steamRoot = ResolveSteamRoot();
            var start = DateTimeOffset.UtcNow;
            var nextNudge = start.AddSeconds(8);
            var nextIpc = start.AddSeconds(20);
            var queuedStallDeadline = start.AddSeconds(120);
            var sawDownloadProgress = false;
            var sawBusy = false;
            var sawTargetManifestChange = false;
            double? lastPct = null;
            var readyStreak = 0;
            long lastDownloaded = -1;
            var sampler = new SteamWatchSampler();

            while (!ct.IsCancellationRequested)
            {
                var snap = ReadAppManifestSnapshot(appId);
                var flags = snap.StateFlags ?? "";
                var busy = SteamStateFlags.IsBusy(flags, snap.BytesToDownload, snap.BytesDownloaded);
                var updateNeeded = SnapshotNeedsUpdate(snap);
                var queuedNoProgress = snap.BytesToDownload is > 0
                    && (snap.BytesDownloaded is null or 0);

                if (snap != initial)
                    sawTargetManifestChange = true;

                if (busy) sawBusy = true;

                var transfer = sampler.ReadTransfer(appId, steamRoot, snap, initial);
                var transferPct = transfer.Percent;

                if (transferPct is > 0 and < 100 &&
                    transfer.Downloaded is long d &&
                    d > lastDownloaded)
                {
                    sawDownloadProgress = true;
                    lastDownloaded = d;
                }

                // Steam retains the final byte totals after returning to StateFlags=4.
                // Only render byte progress while the manifest is still busy; otherwise
                // this branch would mask the ready state forever after a successful update.
                if (busy && (snap.BytesToDownload is > 0 || transferPct is not null))
                {
                    if (transferPct is double pct)
                        lastPct = pct;
                    var to = transfer.ToDownload ?? snap.BytesToDownload;
                    var done = transfer.Downloaded ?? snap.BytesDownloaded ?? 0;
                    var liveBytes = transferPct is not null && to is > 0 && done <= to.Value;
                    Report(game.Id, progress, InstallPhase.Downloading, transferPct ?? lastPct,
                        liveBytes
                            ? $"Updating… {FormatBytes(done)} / {FormatBytes(to!.Value)}"
                            : "Steam is applying the update…",
                        liveBytes && done > 0 ? done : null,
                        liveBytes && to is > 0 ? to : null);
                    readyStreak = 0;
                }
                else if (queuedNoProgress || (updateNeeded && !sawDownloadProgress))
                {
                    readyStreak = 0;
                    if (transferPct is double pct)
                        lastPct = pct;
                    Report(game.Id, progress, InstallPhase.Downloading, transferPct,
                        "Requesting this game's queued Steam update…");

                    if (!sawDownloadProgress && !sawBusy && DateTimeOffset.UtcNow >= queuedStallDeadline)
                    {
                        const string stalled =
                            "Steam did not start this game's update.";
                        Report(game.Id, progress, InstallPhase.Failed, lastPct, stalled);
                        return new InstallResult { Ok = false, Message = stalled };
                    }

                    if (DateTimeOffset.UtcNow >= nextIpc &&
                        IsQueuedForTargetedPromotion(snap))
                    {
                        _ = await CommandSteamIpcAsync("update", appId, ct).ConfigureAwait(false);
                        nextIpc = DateTimeOffset.UtcNow.AddSeconds(20);
                    }

                    if (DateTimeOffset.UtcNow >= nextNudge)
                    {
                        NudgeSteamUpdate(steamExe, game, appId);
                        nextNudge = DateTimeOffset.UtcNow.AddSeconds(12);
                    }
                }
                else if (busy)
                {
                    readyStreak = 0;
                    Report(game.Id, progress, InstallPhase.Downloading, lastPct,
                        "Steam is applying the update…");
                }
                else if (SteamStateFlags.IsFullyInstalled(flags) && !updateNeeded)
                {
                    readyStreak++;
                    // Never fake "up to date" after a short wait when we still needed an update
                    // and never saw download progress / busy bits.
                    var mayComplete = sawDownloadProgress || sawBusy
                        || sawTargetManifestChange || !neededAtStart;
                    if (mayComplete && readyStreak >= 2)
                    {
                        Report(game.Id, progress, InstallPhase.Completed, 100, "Up to date.");
                        return new InstallResult { Ok = true, Message = "Game is up to date." };
                    }

                    if (!mayComplete && DateTimeOffset.UtcNow >= nextNudge)
                    {
                        NudgeSteamUpdate(steamExe, game, appId);
                        nextNudge = DateTimeOffset.UtcNow.AddSeconds(20);
                        Report(game.Id, progress, InstallPhase.Downloading, lastPct,
                            "Re-requesting Steam update…");
                    }
                }
                else
                {
                    readyStreak = 0;
                    Report(game.Id, progress, InstallPhase.Downloading, lastPct,
                        "Waiting for Steam to start this game's update…");
                }

                if ((DateTimeOffset.UtcNow - start).TotalMinutes > 90)
                {
                    Report(game.Id, progress, InstallPhase.Failed, lastPct,
                        "Update watch timed out.");
                    return new InstallResult
                    {
                        Ok = false,
                        Message = "Steam update timed out.",
                    };
                }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }

            const string cancelled = "Cancelled.";
            Report(game.Id, progress, InstallPhase.Cancelled, null, cancelled);
            return new InstallResult { Ok = false, Message = cancelled };
        }
        catch (OperationCanceledException)
        {
            const string cancelled = "Cancelled.";
            Report(game.Id, progress, InstallPhase.Cancelled, null, cancelled);
            return new InstallResult { Ok = false, Message = cancelled };
        }
        catch (Exception ex)
        {
            Report(game.Id, progress, InstallPhase.Failed, null, ex.Message);
            return new InstallResult { Ok = false, Message = ex.Message };
        }
    }

    private sealed record AppManifestSnapshot(
        string? Name,
        string? StateFlags,
        long? BytesToDownload,
        long? BytesDownloaded,
        long? BytesToStage,
        long? BytesStaged,
        string? BuildId,
        string? TargetBuildId);

    private static void ReleaseScheduledSteamUpdate(string appId, string exactTitle)
    {
        var path = FindAppManifestPath(appId);
        if (path is null || !File.Exists(path))
            return;

        try
        {
            var text = File.ReadAllText(path);
            if (!SteamAppManifestSchedule.TryClearScheduledAutoUpdate(text, appId, exactTitle, out var updated))
                return;

            File.WriteAllText(path, updated);
            AppLog.Info($"Steam scheduled update released: appId={appId}.");
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Steam schedule release skipped for appId={appId}: {ex.Message}");
        }
    }

    private static bool SnapshotNeedsUpdate(AppManifestSnapshot snapshot)
    {
        var flags = snapshot.StateFlags ?? "";
        return SteamStateFlags.IsUpdateAvailable(flags, installed: true, snapshot.BytesToDownload, snapshot.BytesDownloaded) ||
               SteamStateFlags.HasPendingTargetBuild(snapshot.BuildId, snapshot.TargetBuildId);
    }

    private static bool IsQueuedForTargetedPromotion(AppManifestSnapshot snapshot) =>
        SteamStateFlags.IsQueuedForTargetedPromotion(
            snapshot.StateFlags,
            snapshot.BytesToDownload,
            snapshot.BytesDownloaded,
            snapshot.BuildId,
            snapshot.TargetBuildId);

    private static AppManifestSnapshot ReadAppManifestSnapshot(string appId)
    {
        var path = FindAppManifestPath(appId);
        if (path is null || !File.Exists(path))
            return new AppManifestSnapshot(null, null, null, null, null, null, null, null);
        try
        {
            var text = File.ReadAllText(path);
            var name = SteamProtocol.MatchAcfField(text, "name");
            var flags = SteamProtocol.MatchAcfField(text, "StateFlags");
            long? to = null, done = null, toStage = null, staged = null;
            if (long.TryParse(SteamProtocol.MatchAcfField(text, "BytesToDownload"), out var btd))
                to = btd;
            if (long.TryParse(SteamProtocol.MatchAcfField(text, "BytesDownloaded"), out var bd))
                done = bd;
            if (long.TryParse(SteamProtocol.MatchAcfField(text, "BytesToStage"), out var bts))
                toStage = bts;
            if (long.TryParse(SteamProtocol.MatchAcfField(text, "BytesStaged"), out var bs))
                staged = bs;
            var buildId = SteamProtocol.MatchAcfField(text, "buildid");
            var targetBuildId = SteamProtocol.MatchAcfField(text, "TargetBuildID");
            return new AppManifestSnapshot(name, flags, to, done, toStage, staged, buildId, targetBuildId);
        }
        catch
        {
            return new AppManifestSnapshot(null, null, null, null, null, null, null, null);
        }
    }

    /// <summary>
    /// The content log tail and the downloading/install directory walks are the
    /// expensive part of an install watch. Re-reading them on every 400ms tick
    /// spent hundreds of milliseconds of disk I/O per second against the same
    /// drive Steam is writing the download to. The ACF counters stay live; only
    /// these two samples are held for a beat.
    /// </summary>
    private sealed class SteamWatchSampler
    {
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

        private DateTimeOffset _contentAtUtc = DateTimeOffset.MinValue;
        private SteamContentLogProgress.Job? _job;
        private long? _downloadingBytes;
        private DateTimeOffset _sizeAtUtc = DateTimeOffset.MinValue;
        private long _installedBytes;

        public SteamTransferProgress.Sample ReadTransfer(
            string appId,
            string? steamRoot,
            AppManifestSnapshot snap,
            AppManifestSnapshot baseline)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _contentAtUtc >= SampleInterval)
            {
                _job = SteamContentLogProgress.TryReadLatest(steamRoot, appId);
                _downloadingBytes = SteamContentLogProgress.TryReadDownloadingBytes(steamRoot, appId);
                _contentAtUtc = now;
            }

            var busy = SteamStateFlags.IsBusy(
                snap.StateFlags, snap.BytesToDownload, snap.BytesDownloaded);
            return SteamTransferProgress.Resolve(
                snap.BytesDownloaded,
                snap.BytesToDownload,
                snap.BytesStaged,
                snap.BytesToStage,
                busy,
                baseline.BytesDownloaded,
                baseline.BytesToDownload,
                _job,
                _downloadingBytes);
        }

        public long ReadInstalledSize(string? path)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _sizeAtUtc < SampleInterval)
                return _installedBytes;
            _installedBytes = TryDirSize(path);
            _sizeAtUtc = now;
            return _installedBytes;
        }
    }

    private static string? FindAppManifestPath(string appId)
    {
        var root = ResolveSteamRoot();
        if (root is null) return null;
        foreach (var lib in CollectLibraryFolders(root))
        {
            var p = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public bool CanRepair(GameEntry game) =>
        game.Installed && SteamProtocol.IsValidAppId(game.LaunchTarget) && ResolveSteamExe() is not null;

    public async Task<InstallResult> RepairAsync(
        GameEntry game,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var appId = game.LaunchTarget;
        if (!SteamProtocol.IsValidAppId(appId))
            return new InstallResult { Ok = false, Message = "Missing or invalid Steam app id." };
        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return new InstallResult { Ok = false, Message = "Steam is not installed." };

        Report(game.Id, progress, InstallPhase.Preparing, null, "Asking Steam to verify files…");
        try
        {
            using var hider = StoreWindowHider.ForSteam();
            hider.Start(TimeSpan.FromMinutes(35), restoreOnStop: false);
            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ProcessHelper.StartHidden(steamExe, SteamUpdateCommandPlan.HiddenClientStartArguments());
                await WaitForSteamCommandListenerAsync(ct).ConfigureAwait(false);
            }

            ProcessHelper.StartHidden(
                steamExe,
                [.. SteamUpdateCommandPlan.HiddenClientStartArguments(), SteamProtocol.ValidateUri(appId)]);

            var start = DateTimeOffset.UtcNow;
            var sawBusy = false;
            while (!ct.IsCancellationRequested)
            {
                var snap = ReadAppManifestSnapshot(appId);
                var busy = SteamStateFlags.IsBusy(snap.StateFlags, snap.BytesToDownload, snap.BytesDownloaded);
                if (busy) sawBusy = true;
                var ready = SteamStateFlags.IsFullyInstalled(snap.StateFlags) && !busy;
                if (sawBusy && ready)
                {
                    Report(game.Id, progress, InstallPhase.Completed, 100, "Files verified.");
                    return new InstallResult { Ok = true, Message = "Steam verified the files.", Path = game.Path };
                }

                var elapsed = DateTimeOffset.UtcNow - start;
                if (!sawBusy && ready && elapsed > TimeSpan.FromSeconds(20))
                {
                    Report(game.Id, progress, InstallPhase.Completed, 100, "Files verified.");
                    return new InstallResult { Ok = true, Message = "Steam verified the files.", Path = game.Path };
                }

                if (elapsed > TimeSpan.FromMinutes(30))
                {
                    Report(game.Id, progress, InstallPhase.Failed, null, "Steam verify timed out.");
                    return new InstallResult { Ok = false, Message = "Steam did not finish verifying files." };
                }

                Report(game.Id, progress, InstallPhase.Installing, null, "Steam is verifying files…");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            Report(game.Id, progress, InstallPhase.Cancelled, null, "Cancelled.");
            return new InstallResult { Ok = false, Message = "Cancelled." };
        }
        catch (OperationCanceledException)
        {
            Report(game.Id, progress, InstallPhase.Cancelled, null, "Cancelled.");
            return new InstallResult { Ok = false, Message = "Cancelled." };
        }
        catch (Exception ex)
        {
            Report(game.Id, progress, InstallPhase.Failed, null, ex.Message);
            return new InstallResult { Ok = false, Message = ex.Message };
        }
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        var appId = game.LaunchTarget;
        if (!SteamProtocol.IsValidAppId(appId))
            return new LaunchResult { Ok = false, Message = "Missing or invalid Steam app id." };

        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return new LaunchResult { Ok = false, Message = "Steam is not installed." };

        ct.ThrowIfCancellationRequested();

        // Suppress Steam chrome for the whole handoff. restoreOnStop:false — the old
        // 5s hide + Restore on dispose was exactly what flashed Steam on Play.
        using var hider = StoreWindowHider.ForSteam();
        if (options.MinimizeStoreUi)
            hider.Start(SteamProcessPath.LaunchChromeSuppressionTimeout, restoreOnStop: false);

        try
        {
            var alreadyRunningPid = FindEligibleRunningPid(game.Path);
            if (alreadyRunningPid is int pid)
            {
                MinimizeSteamUi();
                return new LaunchResult
                {
                    Ok = true,
                    Message = "Already running",
                    ProcessId = pid,
                    BackendStarted = "steam",
                };
            }

            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ct.ThrowIfCancellationRequested();
                ProcessHelper.StartHidden(steamExe, "-silent -nofriendsui -nochatui -noreactlogin");
                MinimizeSteamUi();
            }

            // Protocol handoff — avoids a second steam.exe process flash when already running.
            var processIdsBeforeLaunch = SnapshotRunningPids(game.Path);
            try
            {
                ct.ThrowIfCancellationRequested();
                ProcessHelper.StartProtocol(SteamProtocol.RunGameUri(appId, options.ExtraArgs));
            }
            catch
            {
                ct.ThrowIfCancellationRequested();
                ProcessHelper.StartHidden(steamExe, $"-silent -nofriendsui -nochatui -applaunch {appId}");
            }

            MinimizeSteamUi();

            // Wait for a real game process — keep poking hide while we wait.
            var gamePid = await WaitForGameProcessAsync(
                    game.Path,
                    SteamProcessPath.LaunchHandoffTimeout,
                    ct,
                    processIdsBeforeLaunch,
                    onPoll: MinimizeSteamUi)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            MinimizeSteamUi();

            if (gamePid is null)
            {
                StoreWindowHider.CollapseOrphanSurfaces(StoreWindowHider.SteamProcessNames);
                return new LaunchResult
                {
                    Ok = false,
                    Message = "Steam did not start the game. Sign in if prompted, then try Play again.",
                    BackendStarted = "steam",
                };
            }

            StoreWindowHider.CollapseOrphanSurfaces(StoreWindowHider.SteamProcessNames);
            return new LaunchResult
            {
                Ok = true,
                Message = "Running",
                ProcessId = gamePid,
                BackendStarted = "steam",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message, BackendStarted = "steam" };
        }
        finally
        {
            MinimizeSteamUi();
            StoreWindowHider.CollapseOrphanSurfaces(StoreWindowHider.SteamProcessNames);
        }
    }

    /// <summary>
    /// PID of a process whose executable is actually below the install folder.
    /// </summary>
    private static async Task<int?> WaitForGameProcessAsync(
        string? installPath,
        TimeSpan timeout,
        CancellationToken ct,
        ISet<int> processIdsBeforeLaunch,
        Action? onPoll = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try { onPoll?.Invoke(); } catch { /* */ }

            var hit = FindRunningPid(installPath, processIdsBeforeLaunch);
            if (hit is int candidatePid)
            {
                // A launcher/bootstrap can briefly exist below the install folder
                // even when Steam did not start a real game. Do not credit the
                // handoff until the fresh candidate survives a short grace.
                await Task.Delay(SteamProcessPath.LaunchProcessConfirmationWindow, ct)
                    .ConfigureAwait(false);
                var confirmed = SteamProcessPath.ConfirmFreshGameProcess(
                    candidatePid,
                    IsProcessStillAlive);
                if (confirmed is not null) return confirmed;
            }

            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return null;
    }

    private static bool IsProcessStillAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private static HashSet<int> SnapshotRunningPids(string? installPath)
    {
        var processIds = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return processIds;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.HasExited) continue;
                    var path = ProcessHelper.TryGetExecutablePath(process);
                    if (!string.IsNullOrWhiteSpace(path) && SteamProcessPath.IsWithinInstall(installPath, path))
                        processIds.Add(process.Id);
                }
                catch { /* access denied / process exit */ }
                finally { process.Dispose(); }
            }
        }
        catch { /* enumeration race */ }

        return processIds;
    }

    private static int? FindEligibleRunningPid(string? installPath) =>
        FindRunningPid(installPath, processIdsBeforeLaunch: null);

    private static int? FindRunningPid(string? installPath, ISet<int>? processIdsBeforeLaunch)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return null;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var path = ProcessHelper.TryGetExecutablePath(p);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var eligible = processIdsBeforeLaunch is null
                        ? SteamProcessPath.IsEligibleGameProcess(p.Id, p.ProcessName, path, installPath)
                        : SteamProcessPath.IsEligibleNewGameProcess(
                            p.Id, p.ProcessName, path, installPath, processIdsBeforeLaunch);
                    if (!p.HasExited && eligible)
                        return p.Id;
                }
                catch { /* access denied */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* */ }
        return null;
    }

    public async Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
    {
        var appId = game.LaunchTarget;
        if (!SteamProtocol.IsValidAppId(appId))
            return new InstallResult { Ok = false, Message = "Missing or invalid Steam app id." };

        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return new InstallResult { Ok = false, Message = "Steam is not installed." };

        try
        {
            if (FindAppManifestPath(appId) is null)
                return new InstallResult { Ok = true, Message = "Already removed from Steam." };

            using var hider = StoreWindowHider.ForSteam();
            hider.Start(TimeSpan.FromMinutes(60), restoreOnStop: false);
            StoreUninstallPromptAutomator.Arm(
                game.Title,
                TimeSpan.FromSeconds(120),
                StoreWindowHider.SteamMainProcessNames);
            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ProcessHelper.StartHidden(
                    steamExe,
                    SteamUpdateCommandPlan.HiddenClientStartArguments());
                await WaitForSteamCommandListenerAsync(ct).ConfigureAwait(false);
            }

            AppLog.Info($"Steam uninstall request: gameId={game.Id}; appId={appId}.");
            var ipc = await CommandSteamIpcAsync(
                    "uninstall",
                    appId,
                    ct,
                    retryCommandFailure: false)
                .ConfigureAwait(false);
            // UninstallApp shows Steam's confirm. IPC is the command; the
            // automator confirms the hidden dialog. The URI covers a helper
            // that never reached the client or a refused IPC call.
            if (ipc != SteamIpcStatus.Ok)
            {
                ProcessHelper.StartHidden(
                    steamExe,
                    [.. SteamUpdateCommandPlan.HiddenClientStartArguments(), SteamProtocol.UninstallUri(appId)]);
            }

            var uninstallingSeen = false;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct).ConfigureAwait(false);
                if (FindAppManifestPath(appId) is null)
                {
                    try { Services.SteamLeftoverCleanup.CleanAfterUninstall(ResolveSteamRoot(), appId); }
                    catch { /* leftover folders are best-effort */ }
                    return new InstallResult { Ok = true, Message = "Removed from Steam." };
                }

                var snap = ReadAppManifestSnapshot(appId);
                if (!uninstallingSeen &&
                    SteamStateFlags.TryParse(snap.StateFlags, out var flags) &&
                    (flags & SteamStateFlags.Uninstalling) != 0)
                {
                    uninstallingSeen = true;
                    deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
                }
            }

            return new InstallResult
            {
                Ok = false,
                Message = uninstallingSeen
                    ? "Steam is still removing this game. Try again in a minute."
                    : "Steam did not start removing this game.",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new InstallResult { Ok = false, Message = ex.Message };
        }
    }

    public InstallProgress GetDownloadProgress(string gameId) =>
        _progress.TryGetValue(gameId, out var p) ? p : new InstallProgress { GameId = gameId, Phase = InstallPhase.Idle };

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        // Leave Steam alone after launch — closing steamwebhelper made chrome
        // flash back and also fought the user opening Steam manually.
        _ = game;
        _ = options;
        _ = ct;
        return Task.CompletedTask;
    }

    private void Report(
        string gameId,
        IProgress<InstallProgress>? progress,
        InstallPhase phase,
        double? pct,
        string status,
        long? downloaded = null,
        long? toDownload = null)
    {
        var p = new InstallProgress
        {
            GameId = gameId,
            Phase = phase,
            Percent = pct,
            BytesDownloaded = downloaded,
            BytesToDownload = toDownload,
            Status = status,
            CanCancel = phase is InstallPhase.Preparing or InstallPhase.Downloading or InstallPhase.Installing,
        };
        _progress[gameId] = p;
        progress?.Report(p);
    }

    private static void MinimizeSteamUi() =>
        StoreWindowHider.HideOnce(StoreWindowHider.SteamProcessNames);

    private static (string Path, bool Installed)? FindInstalled(string appId)
    {
        var root = ResolveSteamRoot();
        if (root is null) return null;
        foreach (var lib in CollectLibraryFolders(root))
        {
            var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(acf)) continue;
            try
            {
                var text = File.ReadAllText(acf);
                if (!SteamProtocol.TryParseAppManifest(text, out _, out _, out var installDir, out _))
                    continue;
                if (string.IsNullOrWhiteSpace(installDir)) continue;
                var path = Path.Combine(lib, "steamapps", "common", installDir);
                return (path, Directory.Exists(path));
            }
            catch { }
        }
        return null;
    }

    // Install/update/uninstall watches resolve the root and its library folders
    // on every poll tick. The registry read plus libraryfolders.vdf parse cost
    // more than the manifest read they exist to find; a few seconds of staleness
    // is invisible because a new library folder only matters on the next scan.
    private static readonly TimeSpan SteamPathTtl = TimeSpan.FromSeconds(5);
    private static readonly object SteamPathGate = new();
    private static string? _steamRoot;
    private static DateTimeOffset _steamRootAtUtc = DateTimeOffset.MinValue;
    private static string? _libraryFoldersRoot;
    private static List<string>? _libraryFolders;
    private static DateTimeOffset _libraryFoldersAtUtc = DateTimeOffset.MinValue;

    private static string? ResolveSteamRoot()
    {
        lock (SteamPathGate)
        {
            if (DateTimeOffset.UtcNow - _steamRootAtUtc < SteamPathTtl)
                return _steamRoot;
        }

        var resolved = ReadSteamRoot();
        lock (SteamPathGate)
        {
            _steamRoot = resolved;
            _steamRootAtUtc = DateTimeOffset.UtcNow;
        }
        return resolved;
    }

    private static string? ReadSteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                return path.Replace('/', Path.DirectorySeparatorChar);
        }
        catch { }

        return new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        }.FirstOrDefault(Directory.Exists);
    }

    /// <summary>Public for Settings → Open Steam.</summary>
    public static string? TryResolveSteamExePublic() => ResolveSteamExe();

    public static string? TryResolveSteamRootPublic() => ResolveSteamRoot();

    private static string? ResolveSteamExe()
    {
        var root = ResolveSteamRoot();
        if (root is null) return null;
        var exe = Path.Combine(root, "steam.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static IReadOnlyList<string> CollectLibraryFolders(string steamRoot)
    {
        lock (SteamPathGate)
        {
            if (_libraryFolders is not null &&
                string.Equals(_libraryFoldersRoot, steamRoot, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.UtcNow - _libraryFoldersAtUtc < SteamPathTtl)
                return _libraryFolders;
        }

        var folders = ReadLibraryFolders(steamRoot);
        lock (SteamPathGate)
        {
            _libraryFoldersRoot = steamRoot;
            _libraryFolders = folders;
            _libraryFoldersAtUtc = DateTimeOffset.UtcNow;
        }
        return folders;
    }

    private static List<string> ReadLibraryFolders(string steamRoot)
    {
        var list = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return list;
        try
        {
            var text = File.ReadAllText(vdf);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var p = m.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase))
                    list.Add(p);
            }
        }
        catch { }
        return list;
    }

    private static long TryDirSize(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
        try
        {
            long total = 0;
            var n = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
                if (++n > 8000) break;
            }
            return total;
        }
        catch { return 0; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }
}
