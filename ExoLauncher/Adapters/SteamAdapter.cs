using System.Collections.Concurrent;
using System.Diagnostics;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam library via appmanifest + minimized install/launch.
/// Steam runtime usually remains installed; user should not need to open Steam day-to-day.
/// Anonymous SteamCMD is NOT used for owned paid games.
/// </summary>
public sealed class SteamAdapter : IStoreAdapter, IInstalledSteamManifestSource, IStoreAccountScope
{
    private readonly ConcurrentDictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);

    public StoreKind Store => StoreKind.Steam;
    public string Id => "steam";
    public string DisplayName => "Steam";

    public string? GetActiveAccountScope()
    {
        var root = ResolveSteamRoot();
        return root is null ? null : SteamPlaytime.GetActiveAccountScope(root);
    }

    public bool IsAgentPresent() => ResolveSteamExe() is not null;

    public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthResult
        {
            Ok = IsAgentPresent(),
            RequiresUserAction = true,
            Message = IsAgentPresent()
                ? "Steam uses its own login session. Exo does not store Steam passwords."
                : "Install Steam first.",
        });

    public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry>();
        var steamRoot = ResolveSteamRoot();
        if (steamRoot is null)
            return Task.FromResult<IReadOnlyList<GameEntry>>(games);
        var activeAccount = SteamPlaytime.LoadActiveAccount(steamRoot);

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
                    var installed = path is not null && Directory.Exists(path);

                    // Hide tools / redistributables / non-games (Steamworks Common, SDKs, …).
                    if (IsNonGameSteamEntry(appId, name, installDir))
                        continue;

                    // StateFlags is a bitfield — do not compare to the string "4".
                    var stateFlags = SteamProtocol.MatchAcfField(text, "StateFlags");
                    var updateAvailable = SteamStateFlags.IsUpdateAvailable(stateFlags, installed);
                    var play = activeAccount is not null &&
                               activeAccount.Entries.TryGetValue(appId, out var accountPlay)
                        ? accountPlay
                        : (SteamPlaytime.Entry?)null;
                    // An appmanifest proves that the title is installed on this
                    // machine, not that the currently active Steam account owns
                    // it. A current app ticket is positive account evidence;
                    // missing tickets remain unknown rather than false.
                    var ownedByActiveAccount = activeAccount?.AppTicketIds.Contains(appId) == true;

                    games.Add(new GameEntry
                    {
                        Id = "steam:" + appId,
                        Title = name,
                        Store = StoreKind.Steam,
                        Installed = installed,
                        Owned = ownedByActiveAccount,
                        CanInstall = true,
                        UpdateAvailable = updateAvailable,
                        Path = path,
                        LaunchTarget = appId,
                        // Native CoverArtService resolves official Steam art into its cache.
                        CoverUrl = null,
                        SizeBytes = size,
                        PlaytimeMinutes = play is { Minutes: > 0 } ? play.Value.Minutes : null,
                        LastPlayedUtc = play?.LastPlayedUtc,
                        Status = installed ? (updateAvailable ? "Update" : "Ready") : "Not installed",
                        Deps = new[] { "Steam client" },
                        LaunchNote = "Launches through Steam quietly — Steam stays a backend, not a window you use.",
                    });
                }
                catch { /* skip corrupt manifests */ }
            }
        }

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
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

        Report(game.Id, progress, InstallPhase.Preparing, 5, "Starting Steam…");

        // Exo Install click = consent. Auto-accept Steam's confirm dialog, then hide chrome.
        using var dialogBot = new SteamInstallDialogAutomator();
        StoreWindowHider? hider = null;

        try
        {
            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ProcessHelper.StartHidden(steamExe, "-silent -nofriendsui -nochatui");
                await Task.Delay(2800, ct).ConfigureAwait(false);
            }

            // Keep Steam briefly visible so the install dialog can appear, then auto-accept it.
            dialogBot.Start(TimeSpan.FromSeconds(45));
            RequestSteamInstall(steamExe, appId);
            Report(game.Id, progress, InstallPhase.Downloading, 12,
                "Starting Steam install…");

            var start = DateTimeOffset.UtcNow;
            var sawManifest = false;
            var chromeHidden = false;
            var manualNoted = false;
            long lastSize = 0;
            var stableTicks = 0;

            while (!ct.IsCancellationRequested)
            {
                var elapsed = (DateTimeOffset.UtcNow - start).TotalSeconds;

                if (dialogBot.NeedsManualAction && !manualNoted)
                {
                    manualNoted = true;
                    Report(game.Id, progress, InstallPhase.Downloading,
                        Math.Min(30, 8 + elapsed / 8),
                        dialogBot.ManualReason ?? "Steam needs a one-time choice…");
                }

                var hit = FindInstalled(appId);
                var snap = ReadAppManifestSnapshot(appId);
                if (hit is not null || snap.StateFlags is not null)
                    sawManifest = true;

                // Hide only once Steam is actually downloading — early hide stalls installs.
                var bytesMoving = snap.BytesToDownload is > 0
                    && snap.BytesDownloaded is long bd && bd > 0;
                if (!chromeHidden && (bytesMoving || (dialogBot.ClickedInstall && elapsed > 45)))
                {
                    chromeHidden = true;
                    try
                    {
                        dialogBot.Stop();
                        hider = StoreWindowHider.ForSteam();
                        hider.Start(TimeSpan.FromSeconds(45));
                    }
                    catch { /* */ }
                }

                if (hit is not null)
                {
                    var size = TryDirSize(hit.Value.Path);
                    double pct;
                    if (snap.BytesToDownload is > 0 && snap.BytesDownloaded is long done)
                        pct = Math.Clamp(15 + done * 80.0 / snap.BytesToDownload.Value, 15, 98);
                    else
                        pct = size > 0 ? Math.Min(95, 20 + Math.Log10(size + 1) * 8) : 25;

                    Report(game.Id, progress, InstallPhase.Installing, pct,
                        size > 0
                            ? $"Downloading {game.Title}… ({FormatBytes(size)})"
                            : $"Installing {game.Title}…");

                    var ready = SteamStateFlags.IsFullyInstalled(snap.StateFlags) &&
                                !SteamStateFlags.IsBusy(snap.StateFlags, snap.BytesToDownload, snap.BytesDownloaded);
                    if (ready && size > 5 * 1024 * 1024)
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
                else
                {
                    var waitMsg = dialogBot.NeedsManualAction
                        ? (dialogBot.ManualReason ?? "Steam needs a one-time choice…")
                        : sawManifest
                            ? "Steam is preparing the download…"
                            : dialogBot.ClickedInstall
                                ? "Steam accepted — preparing download…"
                                : "Starting Steam install…";
                    Report(game.Id, progress, InstallPhase.Downloading,
                        Math.Min(30, 8 + elapsed / 8), waitMsg);
                }

                // Re-nudge once if nothing appeared after 20s
                if (!sawManifest && elapsed is > 20 and < 23)
                    RequestSteamInstall(steamExe, appId);

                if (elapsed > 120 * 60)
                {
                    Report(game.Id, progress, InstallPhase.Failed, null, "Install watch timed out.");
                    return new InstallResult { Ok = false, Message = "Steam install timed out." };
                }

                await Task.Delay(2000, ct).ConfigureAwait(false);
            }

            const string cancelled = "Exo stopped watching. Steam may continue downloading.";
            Report(game.Id, progress, InstallPhase.Cancelled, null, cancelled);
            return new InstallResult { Ok = false, Message = cancelled };
        }
        catch (OperationCanceledException)
        {
            const string cancelled = "Exo stopped watching. Steam may continue downloading.";
            Report(game.Id, progress, InstallPhase.Cancelled, null, cancelled);
            return new InstallResult { Ok = false, Message = cancelled };
        }
        catch (Exception ex)
        {
            Report(game.Id, progress, InstallPhase.Failed, null, ex.Message);
            return new InstallResult { Ok = false, Message = ex.Message };
        }
        finally
        {
            try { hider?.Dispose(); } catch { /* */ }
        }
    }

    /// <summary>Fire one Steam install protocol request.</summary>
    private static void RequestSteamInstall(string steamExe, string appId)
    {
        try
        {
            // Starting both a protocol and steam.exe created duplicate clients
            // and duplicate install dialogs.
            ProcessHelper.StartProtocol(SteamProtocol.InstallUri(appId));
        }
        catch
        {
            try
            {
                ProcessHelper.StartHidden(
                    steamExe,
                    ["-silent", "-nofriendsui", "-nochatui", SteamProtocol.InstallUri(appId)]);
            }
            catch { /* */ }
        }
    }

    /// <summary>
    /// Re-send the exact selected app's install/update request. A scheduled
    /// installed title may still need the separately target-verified Downloads
    /// row promotion below.
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
                // Keep polling; a later exact request may reach a client that is still starting.
            }
        }
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
            Report(game.Id, progress, InstallPhase.Preparing, 3, "Starting Steam…");
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
            var neededAtStart = SteamStateFlags.IsUpdateAvailable(initial.StateFlags, installed: true)
                || queuedAtStart;

            Report(game.Id, progress, InstallPhase.Downloading, 8, "Starting Steam update…");

            NudgeSteamUpdate(steamExe, game, appId);

            // steam://install/<appid> reaches Steam but does not promote an
            // already-installed scheduled update. If this exact app remains
            // queued at zero bytes, open Downloads only while hidden and click
            // only the OCR-verified exact-title row. No first/global-button fallback.
            if (queuedAtStart)
            {
                await Task.Delay(900, ct).ConfigureAwait(false);
                if (IsQueuedForTargetedPromotion(ReadAppManifestSnapshot(appId)))
                {
                    Report(game.Id, progress, InstallPhase.Downloading, 9,
                        $"Starting {game.Title}'s scheduled Steam update…");
                    var promotion = await SteamTargetedQueuePromotionAutomator.PromoteAsync(
                        steamExe,
                        appId,
                        initial.Name,
                        () => IsQueuedForTargetedPromotion(ReadAppManifestSnapshot(appId)),
                        TimeSpan.FromSeconds(15),
                        ct).ConfigureAwait(false);
                    if (!promotion.Clicked)
                    {
                        var message =
                            $"Steam kept {game.Title} scheduled because Exo could not safely verify its exact " +
                            "Download Manager row. Open Steam Downloads and start that game once.";
                        Report(game.Id, progress, InstallPhase.Failed, null, message);
                        return new InstallResult { Ok = false, Message = message };
                    }
                }
            }

            var start = DateTimeOffset.UtcNow;
            var nextNudge = start.AddSeconds(8);
            var sawDownloadProgress = false;
            var sawBusy = false;
            var sawTargetManifestChange = false;
            var lastPct = 8.0;
            var readyStreak = 0;
            long lastDownloaded = -1;

            while (!ct.IsCancellationRequested)
            {
                var snap = ReadAppManifestSnapshot(appId);
                var flags = snap.StateFlags ?? "";
                var busy = SteamStateFlags.IsBusy(flags, snap.BytesToDownload, snap.BytesDownloaded);
                var updateNeeded = SteamStateFlags.IsUpdateAvailable(flags, installed: true);
                var queuedNoProgress = snap.BytesToDownload is > 0
                    && (snap.BytesDownloaded is null or 0);

                if (snap != initial)
                    sawTargetManifestChange = true;

                if (busy) sawBusy = true;

                if (snap.BytesDownloaded is long d && d > 0 && d > lastDownloaded)
                {
                    sawDownloadProgress = true;
                    lastDownloaded = d;
                }

                // Steam retains the final byte totals after returning to StateFlags=4.
                // Only render byte progress while the manifest is still busy; otherwise
                // this branch would mask the ready state forever after a successful update.
                if (busy && snap.BytesToDownload is > 0 && snap.BytesDownloaded is long done && done > 0)
                {
                    var pct = Math.Clamp(10 + (done * 85.0 / snap.BytesToDownload.Value), 10, 95);
                    lastPct = pct;
                    var mb = done / (1024.0 * 1024.0);
                    var totalMb = snap.BytesToDownload.Value / (1024.0 * 1024.0);
                    Report(game.Id, progress, InstallPhase.Downloading, pct,
                        $"Updating… {mb:0.0} / {totalMb:0.0} MB");
                    readyStreak = 0;
                }
                else if (queuedNoProgress || (updateNeeded && !sawDownloadProgress))
                {
                    readyStreak = 0;
                    lastPct = Math.Min(18, lastPct + 0.15);
                    Report(game.Id, progress, InstallPhase.Downloading, lastPct,
                        "Requesting this game's queued Steam update…");

                    if (DateTimeOffset.UtcNow >= nextNudge)
                    {
                        // Keep re-requesting only the selected app until its own
                        // manifest changes or bytes move.
                        NudgeSteamUpdate(steamExe, game, appId);
                        nextNudge = DateTimeOffset.UtcNow.AddSeconds(12);
                    }
                }
                else if (busy)
                {
                    readyStreak = 0;
                    lastPct = Math.Min(90, lastPct + 0.4);
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
                    lastPct = Math.Min(40, lastPct + 0.2);
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

                await Task.Delay(2000, ct).ConfigureAwait(false);
            }

            const string cancelled = "Exo stopped watching. Steam may continue downloading.";
            Report(game.Id, progress, InstallPhase.Cancelled, null, cancelled);
            return new InstallResult { Ok = false, Message = cancelled };
        }
        catch (OperationCanceledException)
        {
            const string cancelled = "Exo stopped watching. Steam may continue downloading.";
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
        long? BytesDownloaded);

    private static bool IsQueuedForTargetedPromotion(AppManifestSnapshot snapshot)
    {
        var flags = snapshot.StateFlags ?? "";
        return snapshot.BytesToDownload is > 0 &&
               snapshot.BytesDownloaded is null or 0 &&
               SteamStateFlags.IsUpdateAvailable(flags, installed: true) &&
               !SteamStateFlags.IsBusy(flags, snapshot.BytesToDownload, snapshot.BytesDownloaded);
    }

    private static AppManifestSnapshot ReadAppManifestSnapshot(string appId)
    {
        var path = FindAppManifestPath(appId);
        if (path is null || !File.Exists(path))
            return new AppManifestSnapshot(null, null, null, null);
        try
        {
            var text = File.ReadAllText(path);
            var name = SteamProtocol.MatchAcfField(text, "name");
            var flags = SteamProtocol.MatchAcfField(text, "StateFlags");
            long? to = null, done = null;
            if (long.TryParse(SteamProtocol.MatchAcfField(text, "BytesToDownload"), out var btd))
                to = btd;
            if (long.TryParse(SteamProtocol.MatchAcfField(text, "BytesDownloaded"), out var bd))
                done = bd;
            // Also common during staged updates
            if (to is null or 0 && long.TryParse(SteamProtocol.MatchAcfField(text, "BytesToStage"), out var bts) && bts > 0)
                to = bts;
            if (done is null && long.TryParse(SteamProtocol.MatchAcfField(text, "BytesStaged"), out var bs))
                done = bs;
            return new AppManifestSnapshot(name, flags, to, done);
        }
        catch
        {
            return new AppManifestSnapshot(null, null, null, null);
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
                ProcessHelper.StartProtocol(SteamProtocol.RunGameUri(appId));
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
                    var path = process.MainModule?.FileName;
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
                    var path = p.MainModule?.FileName;
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

        try
        {
            var current = FindInstalled(appId);
            if (current is null || !current.Value.Installed)
                return new InstallResult { Ok = true, Message = "Already removed from Steam." };

            using var hider = StoreWindowHider.ForSteam();
            hider.Start(TimeSpan.FromSeconds(95), restoreOnStop: false);
            StoreUninstallPromptAutomator.Arm(
                game.Title,
                TimeSpan.FromSeconds(90),
                StoreWindowHider.SteamProcessNames);
            EnsureSteamSilent();
            ProcessHelper.StartProtocol($"steam://uninstall/{appId}");

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct).ConfigureAwait(false);
                var remaining = FindInstalled(appId);
                if (remaining is null || !remaining.Value.Installed)
                    return new InstallResult { Ok = true, Message = "Removed from Steam." };
            }

            return new InstallResult
            {
                Ok = false,
                Message = "Steam did not confirm removal. Try again after Steam finishes its current task.",
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

    private void Report(string gameId, IProgress<InstallProgress>? progress, InstallPhase phase, double? pct, string status)
    {
        var p = new InstallProgress
        {
            GameId = gameId,
            Phase = phase,
            Percent = pct,
            Status = status,
            CanCancel = phase is InstallPhase.Preparing or InstallPhase.Downloading or InstallPhase.Installing,
        };
        _progress[gameId] = p;
        progress?.Report(p);
    }

    private static void EnsureSteamSilent()
    {
        var steamExe = ResolveSteamExe();
        if (steamExe is not null && !ProcessHelper.IsProcessRunning("steam"))
            ProcessHelper.StartHidden(steamExe, "-silent");
        MinimizeSteamUi();
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

    private static string? ResolveSteamRoot()
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

    private static string? ResolveSteamExe()
    {
        var root = ResolveSteamRoot();
        if (root is null) return null;
        var exe = Path.Combine(root, "steam.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static List<string> CollectLibraryFolders(string steamRoot)
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
