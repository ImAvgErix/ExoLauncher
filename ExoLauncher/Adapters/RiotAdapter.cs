using System.Collections.Concurrent;
using System.Diagnostics;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Adapters.Riot;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// Riot fixed catalog — orchestration of official RiotClientServices, not a custom CDN client.
/// Vanguard remains required for online titles; Exo never touches vgk/vgc.
/// </summary>
public sealed class RiotAdapter : IStoreAdapter
{
    private readonly ConcurrentDictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);

    public StoreKind Store => StoreKind.Riot;
    public string Id => "riot";
    public string DisplayName => "Riot";

    public bool IsAgentPresent() => ResolveRiotClientServices() is not null;

    public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthResult
        {
            Ok = IsAgentPresent(),
            RequiresUserAction = true,
            Message = IsAgentPresent()
                ? "Riot Client handles its own sign-in. Exo only orchestrates product flags."
                : "Install Riot Client first (official installer).",
        });

    public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry>();
        var rcs = ResolveRiotClientServices();

        foreach (var (productId, title) in RiotCli.FixedCatalog)
        {
            ct.ThrowIfCancellationRequested();
            // Probe all known Riot roots (C:\Riot Games, Program Files, LocalAppData, …).
            var installedPath = RiotInstallProbe.FindInstalledProduct(productId);
            var installed = installedPath is not null;

            games.Add(new GameEntry
            {
                Id = "riot:" + productId,
                Title = title,
                Store = StoreKind.Riot,
                Installed = installed,
                // A Riot Client installation proves only that the launcher is
                // present. Each free-to-play title remains unowned until its
                // own product files are observed.
                Owned = installed,
                // Only offer Install when not already present and a client/bootstrap exists.
                CanInstall = !installed && (rcs is not null || ResolveBootstrapInstaller() is not null),
                Path = installedPath,
                LaunchTarget = productId,
                Status = installed ? "Ready" : (rcs is not null ? "Not installed" : "Client missing"),
                // Uninstall EstimatedSize is instant and excludes Vanguard. A
                // directory walk is deferred and only used when that key is missing.
                SizeBytes = installed
                    ? RiotInstallProbe.TryReadInstallSizeBytes(productId)
                      ?? InstalledSizeCache.Get(installedPath)
                    : null,
                Deps = productId == "valorant"
                    ? new[] { "Riot Client", "Vanguard" }
                    : new[] { "Riot Client" },
                LaunchNote = productId == "valorant"
                    ? "Official RiotClientServices launch. Vanguard must stay installed for online play. Exo does not bypass it."
                    : "RiotClientServices --launch-product. Optional force-close of Riot UI after exit.",
            });
        }

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var productId = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(productId) || !RiotCli.IsKnownProduct(productId))
            return new InstallResult { Ok = false, Message = "Unknown Riot product." };

        var rcs = ResolveRiotClientServices();
        var bootstrap = ResolveBootstrapInstaller();

        if (rcs is null && bootstrap is null)
        {
            return new InstallResult
            {
                Ok = false,
                Message = "Riot Client not found. Download the official installer from Riot; Exo does not ship a CDN scraper.",
            };
        }

        Report(game.Id, progress, InstallPhase.Preparing, 5, "Starting official Riot install…");

        try
        {
            // Prefer the local REST patch API — real percent/speed, no folder heuristics.
            if (rcs is not null)
            {
                using var hider = StoreWindowHider.ForRiot();
                hider.Start(TimeSpan.FromMinutes(45), restoreOnStop: false);
                var api = await RiotClientApi.ConnectAsync(rcs, TimeSpan.FromSeconds(45), ct)
                    .ConfigureAwait(false);
                if (api is not null)
                {
                    try
                    {
                        return await InstallViaPatchApiAsync(api, game, productId, progress, ct)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        api.Dispose();
                    }
                }
            }

            return await InstallViaLaunchProductFallbackAsync(game, productId, rcs, bootstrap, progress, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                using var api = RiotClientApi.TryConnect();
                if (api is not null)
                    await api.CancelPatchAsync(productId, RiotCli.DefaultPatchline, CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch { /* */ }
            SoftCloseRiotUi();
            Report(game.Id, progress, InstallPhase.Cancelled, null, "Cancelled.");
            return new InstallResult { Ok = false, Message = "Cancelled." };
        }
        catch (Exception ex)
        {
            SoftCloseRiotUi();
            Report(game.Id, progress, InstallPhase.Failed, null, ex.Message);
            return new InstallResult { Ok = false, Message = ex.Message };
        }
    }

    private async Task<InstallResult> InstallViaPatchApiAsync(
        RiotClientApi api,
        GameEntry game,
        string productId,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var patchline = RiotCli.DefaultPatchline;
        Report(game.Id, progress, InstallPhase.Installing, 8, "Requesting Riot patch…");
        var requested = await api.RequestPatchAsync(productId, patchline, ct).ConfigureAwait(false);
        if (!requested)
        {
            Report(game.Id, progress, InstallPhase.Failed, null, "Riot rejected the patch request.");
            return new InstallResult { Ok = false, Message = "Riot rejected the patch request." };
        }

        var start = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            HideRiotUiWindows();
            var state = await api.GetPatchStateAsync(productId, patchline, ct).ConfigureAwait(false);
            if (state is not null)
            {
                if (state.IsUpToDate || (state.Launchable && !state.IsPatching))
                {
                    SoftCloseRiotUi(includeServices: false);
                    var path = RiotInstallProbe.FindInstalledProduct(productId)
                               ?? FindProductPath(ResolveRiotRoot(), productId, game.Title);
                    InstalledSizeCache.Invalidate(path);
                    Report(game.Id, progress, InstallPhase.Completed, 100, "Install finished.");
                    return new InstallResult
                    {
                        Ok = path is not null || state.Launchable,
                        Message = "Installed via Riot patch API.",
                        Path = path,
                    };
                }

                if (string.Equals(state.State, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    Report(game.Id, progress, InstallPhase.Failed, null, "Riot patch reported an error.");
                    return new InstallResult { Ok = false, Message = "Riot patch reported an error." };
                }

                var pct = state.Percent > 0 ? Math.Min(99, state.Percent) : 10;
                var bps = state.SpeedMbps > 0 ? state.SpeedMbps * 1024.0 * 1024.0 / 8.0 : (double?)null;
                var status = string.IsNullOrWhiteSpace(state.Phase)
                    ? $"Installing {game.Title}…"
                    : $"{state.Phase} · {game.Title}";
                Report(game.Id, progress, InstallPhase.Installing, pct, status, bps);
            }
            else
            {
                Report(game.Id, progress, InstallPhase.Installing, 12, $"Waiting for Riot patch state…");
            }

            if ((DateTimeOffset.UtcNow - start).TotalMinutes > 45)
            {
                Report(game.Id, progress, InstallPhase.Failed, null, "Install timed out.");
                return new InstallResult { Ok = false, Message = "Install timed out." };
            }

            await Task.Delay(1500, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return new InstallResult { Ok = false, Message = "Cancelled." };
    }

    private async Task<InstallResult> InstallViaLaunchProductFallbackAsync(
        GameEntry game,
        string productId,
        string? rcs,
        string? bootstrap,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        Report(game.Id, progress, InstallPhase.Preparing, 5, "Starting official Riot install (UI hidden)…");

        Process? proc;
        if (rcs is not null)
            proc = StartHidden(rcs, RiotCli.LaunchArgs(productId));
        else
            proc = StartHidden(bootstrap!, RiotCli.BootstrapInstallArgs());

        if (proc is null)
        {
            Report(game.Id, progress, InstallPhase.Failed, null, "Could not start Riot installer.");
            return new InstallResult { Ok = false, Message = "Could not start Riot installer." };
        }

        using (var hider = StoreWindowHider.ForRiot())
        {
            hider.Start(TimeSpan.FromSeconds(8));
            for (var i = 0; i < 10; i++)
            {
                ct.ThrowIfCancellationRequested();
                ProcessHelper.HideProcessWindows(proc.Id);
                HideRiotUiWindows();
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        var start = DateTimeOffset.UtcNow;
        var lastSize = 0L;
        var lastPath = string.Empty;
        var stableTicks = 0;
        while (!ct.IsCancellationRequested)
        {
            HideRiotUiWindows();

            var path = FindProductPath(ResolveRiotRoot(), productId, game.Title);
            long size = 0;
            if (path is not null)
            {
                try { size = DirSizeBounded(path, maxFiles: 5000); }
                catch { /* */ }
            }

            var elapsed = (DateTimeOffset.UtcNow - start).TotalSeconds;
            double pct = path is not null && size > 50 * 1024 * 1024
                ? Math.Min(95, 20 + Math.Log10(size) * 8)
                : Math.Min(40, 5 + elapsed / 3);

            var previousSize = lastSize;
            var bps = size > previousSize && elapsed > 1
                ? (size - previousSize) / Math.Max(1, 2)
                : (double?)null;
            lastSize = size;
            if (path is not null &&
                string.Equals(path, lastPath, StringComparison.OrdinalIgnoreCase) &&
                size >= 1 * 1024 * 1024 &&
                size == previousSize)
                stableTicks++;
            else
                stableTicks = 0;
            lastPath = path ?? string.Empty;

            var stillRunning = !proc.HasExited || ProcessHelper.IsProcessRunning("RiotClientServices")
                || ProcessHelper.IsProcessRunning("RiotClientUx");
            var status = path is not null
                ? $"Installing {game.Title}… ({FormatBytes(size)})"
                : stillRunning
                    ? $"Waiting for Riot to place {game.Title}…"
                    : $"Waiting for {game.Title} install markers…";
            Report(game.Id, progress, InstallPhase.Installing, pct, status, bps);

            if (path is not null && stableTicks >= 2)
                break;

            if (elapsed > 45 * 60)
            {
                SoftCloseRiotUi();
                Report(game.Id, progress, InstallPhase.Failed, null, "Install watch timed out. Check Riot Client manually.");
                return new InstallResult { Ok = false, Message = "Install watch timed out." };
            }

            await Task.Delay(2000, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        SoftCloseRiotUi(includeServices: false);

        var finalPath = FindProductPath(ResolveRiotRoot(), productId, game.Title);
        InstalledSizeCache.Invalidate(finalPath);
        var ok = finalPath is not null;
        Report(game.Id, progress,
            ok ? InstallPhase.Completed : InstallPhase.Failed,
            ok ? 100 : null,
            ok ? "Install finished. Riot UI closed." : "Riot finished but product folder not detected.");

        return new InstallResult
        {
            Ok = ok,
            Message = ok
                ? "Installed via official Riot client (UI hidden)."
                : "Riot process ended without a detectable product folder.",
            Path = finalPath,
        };
    }

    public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default)
        // Same official path as install — Riot updates on launch-product.
        => InstallAsync(game, game.Path, progress, ct);

    public async Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return new LaunchResult { Ok = false, Message = "Cancelled.", BackendStarted = "riot" };

        var rcs = ResolveRiotClientServices();
        if (rcs is null)
            return new LaunchResult { Ok = false, Message = "RiotClientServices.exe not found." };

        var productId = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(productId))
            return new LaunchResult { Ok = false, Message = "Missing Riot product id." };

        var patchline = RiotCli.DefaultPatchline;
        try
        {
            var readyNames = LaunchReadyProcessNames(productId);
            var existingPid = FindAnyProcessId(readyNames);
            if (existingPid is not null)
            {
                if (options.MinimizeStoreUi) HideRiotUiWindows();
                return new LaunchResult
                {
                    Ok = true,
                    Message = "Already running",
                    ProcessId = existingPid,
                    BackendStarted = "riot",
                };
            }

            using var api = await Riot.RiotClientApi
                .ConnectAsync(rcs, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            if (api is null)
            {
                return new LaunchResult
                {
                    Ok = false,
                    Message = "Riot Client did not start. Open it once, then try Play again.",
                    BackendStarted = "riot",
                };
            }

            // region-locale becomes ready before Riot's product registry is
            // necessarily hydrated on a cold start. A verified local install
            // gives the registry a brief bounded window to catch up, then lets
            // the launch endpoint be authoritative instead of showing a false
            // "not installed" error on the first click.
            var verifiedLocalInstall = HasVerifiedLocalInstall(game, productId);
            var installState = await ReadInstallStateAfterWarmupAsync(
                    token => api.GetInstallStateAsync(productId, patchline, token),
                    verifiedLocalInstall,
                    maxAttempts: 17,
                    retryDelay: TimeSpan.FromMilliseconds(500),
                    ct)
                .ConfigureAwait(false);
            if (!IsInstalledState(installState) && !verifiedLocalInstall)
            {
                var reportedState = string.IsNullOrWhiteSpace(installState) ? "unknown" : installState;
                return new LaunchResult
                {
                    Ok = false,
                    Message = $"{game.Title} is not installed in Riot ({reportedState}).",
                    BackendStarted = "riot",
                };
            }
            if (!IsInstalledState(installState))
            {
                AppLog.Warn(
                    $"Riot product registry remained '{installState ?? "unknown"}' for {game.Title}; " +
                    "continuing because its local install was verified.");
            }

            // Eligibility can briefly be false while a warm League session is
            // completing its handoff. Re-check the concrete process state before
            // presenting that transient API response as an error.
            existingPid = FindAnyProcessId(readyNames);
            if (existingPid is not null)
            {
                if (options.MinimizeStoreUi) HideRiotUiWindows();
                return new LaunchResult
                {
                    Ok = true,
                    Message = "Already running",
                    ProcessId = existingPid,
                    BackendStarted = "riot",
                };
            }

            var eligible = await ReadEligibilityAfterWarmupAsync(
                    token => api.IsEligibleAsync(productId, patchline, token),
                    verifiedLocalInstall,
                    maxAttempts: 5,
                    retryDelay: TimeSpan.FromMilliseconds(500),
                    ct)
                .ConfigureAwait(false);
            if (eligible is false)
            {
                var patch = await api.GetPatchStateAsync(productId, patchline, ct).ConfigureAwait(false);
                var patching = patch is not null && patch.IsPatching;
                if (!CanLetLaunchEndpointDecide(eligible, verifiedLocalInstall, patching))
                {
                    var why = patching
                        ? $"Riot is updating {game.Title} ({patch!.Percent:0}%)."
                        : "Riot reports this game cannot launch right now.";
                    return new LaunchResult { Ok = false, Message = why, BackendStarted = "riot" };
                }

                // The user explicitly requested Play and the installation was
                // verified on disk. With no active patch, POSTing the launch is
                // both safe and more authoritative than a cold eligibility read;
                // Riot still rejects it normally for auth or account restrictions.
                AppLog.Warn($"Riot launch eligibility remained false for {game.Title}; asking the launch endpoint because its local install is verified.");
            }
            if (eligible is null)
            {
                // A non-2xx eligibility read during client warm-up is unknown,
                // not a definitive denial. The launch POST returns the actual
                // decision and is safe to attempt for a verified installation.
                AppLog.Warn($"Riot launch eligibility was temporarily unavailable for {game.Title}; retrying through the launch endpoint.");
            }

            var launch = await api.LaunchAsync(productId, patchline, ct).ConfigureAwait(false);
            if (!launch.Accepted)
            {
                return new LaunchResult
                {
                    Ok = false,
                    Message = string.IsNullOrWhiteSpace(launch.Error)
                        ? "Riot Client refused the launch request."
                        : $"Riot Client refused the launch request: {launch.Error}",
                    BackendStarted = "riot",
                };
            }

            // League opens its persistent client first; the actual match process
            // may not appear until the player joins a game. Observe the handoff
            // here, then let the session watcher wait for the real executable.
            var gamePid = await WaitForAnyProcessIdAsync(readyNames, TimeSpan.FromSeconds(15), ct)
                .ConfigureAwait(false);

            if (options.MinimizeStoreUi)
            {
                using var hider = StoreWindowHider.ForRiot();
                hider.Start(TimeSpan.FromSeconds(6), restoreOnStop: false);
                HideRiotUiWindows();
            }

            return new LaunchResult
            {
                Ok = true,
                // The API is authoritative for acceptance. League can spend
                // longer than the foreground wait moving from Riot Client to
                // LeagueClient; the orchestrator's session watcher continues
                // observing that handoff without showing a false failure.
                Message = gamePid is not null || launch.AlreadyRunning ? "Running" : "Starting…",
                ProcessId = gamePid,
                BackendStarted = "riot",
            };
        }
        catch (OperationCanceledException)
        {
            return new LaunchResult { Ok = false, Message = "Cancelled.", BackendStarted = "riot" };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message, BackendStarted = "riot" };
        }
    }

    internal static bool IsInstalledState(string? state) =>
        string.Equals(state, "installed", StringComparison.OrdinalIgnoreCase);

    internal static async Task<string?> ReadInstallStateAfterWarmupAsync(
        Func<CancellationToken, Task<string?>> readState,
        bool verifiedLocalInstall,
        int maxAttempts,
        TimeSpan retryDelay,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readState);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));

        string? state = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            state = await readState(ct).ConfigureAwait(false);
            if (IsInstalledState(state) || !verifiedLocalInstall || attempt == maxAttempts - 1)
                return state;
            await Task.Delay(retryDelay, ct).ConfigureAwait(false);
        }
        return state;
    }

    internal static async Task<bool?> ReadEligibilityAfterWarmupAsync(
        Func<CancellationToken, Task<bool?>> readEligibility,
        bool verifiedLocalInstall,
        int maxAttempts,
        TimeSpan retryDelay,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readEligibility);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));

        bool? eligible = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            eligible = await readEligibility(ct).ConfigureAwait(false);
            if (eligible is not false || !verifiedLocalInstall || attempt == maxAttempts - 1)
                return eligible;
            await Task.Delay(retryDelay, ct).ConfigureAwait(false);
        }
        return eligible;
    }

    internal static bool CanLetLaunchEndpointDecide(
        bool? eligible,
        bool verifiedLocalInstall,
        bool patching) =>
        eligible is not false || (verifiedLocalInstall && !patching);

    private static bool HasVerifiedLocalInstall(GameEntry game, string productId)
    {
        if (!string.IsNullOrWhiteSpace(game.Path) &&
            RiotInstallProbe.LooksInstalled(productId, game.Path))
            return true;
        return RiotInstallProbe.FindInstalledProduct(productId) is not null;
    }

    private static async Task WaitRiotUiGoneAsync(TimeSpan timeout, CancellationToken ct)
    {
        // RiotClientServices is deliberately excluded: it carries the pending
        // launch request, and closing it here cancels the very thing we retried.
        var uiOnly = RiotCli.UiProcessNames
            .Where(n => !string.Equals(n, "RiotClientServices", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!uiOnly.Any(ProcessHelper.IsProcessRunning)) return;
            SoftCloseRiotUi(includeServices: false);
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
    }

    private static async Task<int?> WaitForAnyProcessIdAsync(
        IReadOnlyList<string> names, TimeSpan timeout, CancellationToken ct)
    {
        if (names.Count == 0) return null;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var id = FindAnyProcessId(names);
            if (id is not null) return id;
            await Task.Delay(350, ct).ConfigureAwait(false);
        }
        return FindAnyProcessId(names);
    }

    private static int? FindAnyProcessId(IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            try
            {
                var process = Process.GetProcessesByName(name).FirstOrDefault();
                if (process is not null)
                {
                    var id = process.Id;
                    process.Dispose();
                    return id;
                }
            }
            catch { /* */ }
        }
        return null;
    }

    /// <summary>Actual in-game processes used to credit playtime.</summary>
    internal static string[] GameProcessNames(string productId) =>
        productId.Trim().ToLowerInvariant() switch
        {
            "league_of_legends" =>
                ["League of Legends"],
            "lion" =>
                ["2XKO", "Lion"],
            "valorant" =>
                ["VALORANT-Win64-Shipping", "VALORANT"],
            "bacon" =>
                ["LoR", "LegendsofRuneterra"],
            _ => [],
        };

    /// <summary>Processes that prove Riot accepted the launch handoff.</summary>
    internal static string[] LaunchReadyProcessNames(string productId) =>
        productId.Trim().ToLowerInvariant() switch
        {
            "league_of_legends" =>
                ["LeagueClient", "LeagueClientUx", "League of Legends"],
            "lion" =>
                ["2XKO", "Lion"],
            _ => GameProcessNames(productId),
        };

    public async Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
    {
        var rcs = ResolveRiotClientServices();
        if (rcs is null)
            return new InstallResult { Ok = false, Message = "RiotClientServices.exe not found." };

        var productId = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(productId))
            return new InstallResult { Ok = false, Message = "Missing Riot product id." };

        try
        {
            var installedPath = !string.IsNullOrWhiteSpace(game.Path) &&
                                RiotInstallProbe.LooksInstalled(productId, game.Path)
                ? game.Path
                : RiotInstallProbe.FindInstalledProduct(productId);
            if (installedPath is null)
                return new InstallResult { Ok = true, Message = "Already removed from Riot." };

            using var hider = StoreWindowHider.ForRiot();
            hider.Start(TimeSpan.FromSeconds(125), restoreOnStop: false);
            StoreUninstallPromptAutomator.Arm(
                game.Title,
                TimeSpan.FromSeconds(120),
                StoreWindowHider.RiotUiProcessNames);
            using var p = StartHidden(rcs, RiotCli.UninstallArgs(productId));
            if (p is null)
                return new InstallResult { Ok = false, Message = "Uninstall did not start." };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (!RiotInstallProbe.LooksInstalled(productId, installedPath))
                {
                    InstalledSizeCache.Invalidate(installedPath);
                    SoftCloseRiotUi();
                    return new InstallResult { Ok = true, Message = "Removed from Riot." };
                }
                await Task.Delay(650, ct).ConfigureAwait(false);
            }

            SoftCloseRiotUi();
            return new InstallResult
            {
                Ok = false,
                Message = "Riot did not confirm removal. Try again after the client finishes its current task.",
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            SoftCloseRiotUi();
            return new InstallResult { Ok = false, Message = "Riot uninstall timed out." };
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
        if (options.CloseStoreUiAfterExit)
            HideRiotUiWindows();
        return Task.CompletedTask;
    }

    private void Report(
        string gameId,
        IProgress<InstallProgress>? progress,
        InstallPhase phase,
        double? pct,
        string status,
        double? bytesPerSecond = null)
    {
        var p = new InstallProgress
        {
            GameId = gameId,
            Phase = phase,
            Percent = pct,
            BytesPerSecond = bytesPerSecond,
            Status = status,
            CanCancel = phase is InstallPhase.Preparing or InstallPhase.Downloading or InstallPhase.Installing,
        };
        _progress[gameId] = p;
        progress?.Report(p);
    }

    private static Process? StartHidden(string fileName, string arguments) =>
        ProcessHelper.StartHidden(fileName, arguments);

    private static void SoftCloseRiotUi(bool allowKill = false, bool includeServices = true)
    {
        foreach (var name in RiotCli.UiProcessNames)
        {
            if (!includeServices &&
                string.Equals(name, "RiotClientServices", StringComparison.OrdinalIgnoreCase))
                continue;
            if (RiotCli.IsProtectedProcess(name)) continue;
            ProcessHelper.TryCloseProcesses(name);
            if (!allowKill) continue;
            // Relaunch path only: warm Riot Client ignores --launch-product until
            // its UX hosts restart. Never kill RiotClientServices or an entire tree.
            if (string.Equals(name, "RiotClientServices", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (!p.HasExited)
                            p.Kill(entireProcessTree: false);
                    }
                    catch { /* */ }
                    finally { p.Dispose(); }
                }
            }
            catch { /* */ }
        }
    }

    private static void HideRiotUiWindows() =>
        StoreWindowHider.HideOnce(StoreWindowHider.RiotUiProcessNames);

    private static string? FindProductPath(string? root, string productId, string title)
    {
        // Prefer multi-root probe (includes C:\Riot Games). Keep root as first candidate.
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            var hit = RiotInstallProbe.FindInstalledProduct(productId, new[] { root! });
            if (hit is not null) return hit;
        }
        return RiotInstallProbe.FindInstalledProduct(productId);
    }

    private static string? ResolveRiotRoot()
    {
        // Prefer root that already contains products or the client.
        foreach (var root in RiotInstallProbe.DefaultRootCandidates)
        {
            if (!Directory.Exists(root)) continue;
            if (File.Exists(Path.Combine(root, "Riot Client", "RiotClientServices.exe")))
                return root;
            if (Directory.Exists(Path.Combine(root, "VALORANT")) ||
                Directory.Exists(Path.Combine(root, "League of Legends")))
                return root;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Riot Game valorant.live");
            var loc = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(loc))
            {
                var parent = Directory.GetParent(loc.TrimEnd('\\'))?.FullName;
                if (parent is not null && Directory.Exists(parent)) return parent;
            }
        }
        catch { }

        return RiotInstallProbe.DefaultRootCandidates.FirstOrDefault(Directory.Exists);
    }

    public static string? TryResolveRiotClientServicesPublic() => ResolveRiotClientServices();

    private static string? ResolveRiotClientServices() =>
        RiotInstallProbe.FindRiotClientServices();

    private static string? ResolveBootstrapInstaller()
    {
        // User-downloaded official installer in common locations — not shipped by Exo.
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads)) return null;
        try
        {
            return Directory.EnumerateFiles(downloads, "RiotClientInstall*.exe")
                .OrderByDescending(File.GetCreationTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static long DirSizeBounded(string dir, int maxFiles)
    {
        long total = 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; } catch { }
            if (++n >= maxFiles) break;
        }
        return total;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }
}
