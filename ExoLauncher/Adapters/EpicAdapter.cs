using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// Epic via Legendary CLI — true no-Epic-GUI path when Legendary is present.
/// https://github.com/derrod/legendary
/// </summary>
public sealed class EpicAdapter : IStoreAdapter, IStoreClientPresence, IStoreAccountScope
{
    private readonly ConcurrentDictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LegendaryAuthTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LegendarySessionProbeTimeout = TimeSpan.FromSeconds(45);
    private static readonly string[] EpicBootstrapProcessNames =
    [
        "EpicGamesLauncher", "EpicWebHelper", "CrashReportClient",
        "Launcher", "EasyAntiCheat", "EasyAntiCheat_EOS",
        "EpicOnlineServices", "EOSOverlayRenderer-Win64-Shipping",
    ];
    private static readonly TimeSpan NewGameProcessConfirmationDelay = TimeSpan.FromMilliseconds(750);

    public StoreKind Store => StoreKind.Epic;
    public string Id => "epic";
    public string DisplayName => "Epic";

    public string? GetActiveAccountScope() => EpicPlaytime.GetActiveAccountScope();

    // Legendary is an intentionally headless backend. Do not let it make
    // Settings claim that the separately installed Epic Games Launcher exists.
    public bool IsAgentPresent() => ResolveLegendary() is not null || ResolveEpicLauncher() is not null;
    public bool IsClientPresent() => ResolveEpicLauncher() is not null;

    public async Task<AuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        try
        {
            // Best-effort: fetch legendary.exe into Exo tools if missing (needed for quiet Epic auth/install).
            var legendary = ResolveLegendary() ?? await EnsureLegendaryAsync(ct).ConfigureAwait(false);

            if (legendary is not null)
            {
                if (await HasValidLegendarySessionAsync(legendary, ct).ConfigureAwait(false))
                {
                    return new AuthResult
                    {
                        Ok = true,
                        RequiresUserAction = false,
                        Message = "Epic account is already connected through Legendary.",
                    };
                }

                // Do not use `auth --import`: Legendary documents that it logs the
                // Epic Games Launcher out. Its normal auth flow owns the browser UI.
                using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                authTimeout.CancelAfter(LegendaryAuthTimeout);

                (int ExitCode, string StdOut, string StdErr) auth;
                try
                {
                    auth = await CliRunner.RunAsync(
                            legendary, LegendaryCli.AuthArgs(), null, null, authTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new AuthResult
                    {
                        Ok = false,
                        RequiresUserAction = true,
                        Message = "Epic sign-in timed out before it completed. Try again.",
                    };
                }

                if (auth.ExitCode != 0)
                {
                    AppLog.Debug($"legendary auth exited {auth.ExitCode}.");
                    return new AuthResult
                    {
                        Ok = false,
                        RequiresUserAction = true,
                        Message = $"Epic sign-in did not complete (Legendary exited {auth.ExitCode}).",
                    };
                }

                if (!await HasValidLegendarySessionAsync(legendary, ct).ConfigureAwait(false))
                {
                    return new AuthResult
                    {
                        Ok = false,
                        RequiresUserAction = true,
                        Message = "Legendary sign-in finished, but the Epic session could not be verified.",
                    };
                }

                return new AuthResult
                {
                    Ok = true,
                    RequiresUserAction = false,
                    Message = "Epic account connected through Legendary.",
                };
            }

            // No Legendary — do not open Epic Games Launcher. Exo keeps store clients
            // invisible; the user can install Legendary from the official source.
            var epic = ResolveEpicLauncher();
            if (epic is not null)
            {
                return new AuthResult
                {
                    Ok = false,
                    RequiresUserAction = true,
                    Message = "Legendary is required for hidden Epic actions. Install it from the official source, then Refresh.",
                };
            }

            return new AuthResult
            {
                Ok = false,
                RequiresUserAction = true,
                Message = "Epic/Legendary not found. Install Epic Games Launcher or Legendary, then try Sign in again.",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AuthResult
            {
                Ok = false,
                Message = "Epic sign-in was cancelled.",
                RequiresUserAction = true,
            };
        }
        catch (Exception ex)
        {
            return new AuthResult { Ok = false, Message = ex.Message, RequiresUserAction = true };
        }
    }

    private static async Task<bool> HasValidLegendarySessionAsync(string legendary, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LegendarySessionProbeTimeout);

        try
        {
            var (exitCode, stdout, _) = await CliRunner.RunAsync(
                    legendary, LegendaryCli.ListOwnedArgs(), null, null, timeout.Token)
                .ConfigureAwait(false);
            return LegendaryCli.IsAuthenticatedLibraryResponse(exitCode, stdout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Debug("Legendary session verification timed out.");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Legendary session verification failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>AMD64 only. Reject ARM64 binaries (Windows shows "Machine Type Mismatch").</summary>
    private const ushort PeMachineAmd64 = 0x8664;
    private static readonly GitHubReleaseAsset LegendaryReleaseAsset = new(
        "derrod",
        "legendary",
        "0.21.0",
        "legendary_windows_x64.exe",
        ExpectedSize: 17_610_944,
        ExpectedSha256: "4c01a14c0acb0c46069b197ae7212ea4ea6b861661126ca0593cdac31658fb01");

    /// <summary>Download legendary_windows_x64.exe into Exo tools if absent / wrong arch.</summary>
    private static async Task<string?> EnsureLegendaryAsync(CancellationToken ct)
    {
        try
        {
            var tools = Path.Combine(PathHelper.AppDataDir, "tools");
            Directory.CreateDirectory(tools);
            var dest = Path.Combine(tools, "legendary.exe");
            return await VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(
                    LegendaryReleaseAsset,
                    dest,
                    IsValidAmd64Pe,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("EnsureLegendary failed: " + ex.Message);
            return null;
        }
    }

    private static bool IsValidAmd64Pe(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var len = new FileInfo(path).Length;
            if (len < 1_000_000) return false;
            using var fs = File.OpenRead(path);
            Span<byte> dos = stackalloc byte[64];
            if (fs.Read(dos) < 64) return false;
            if (dos[0] != (byte)'M' || dos[1] != (byte)'Z') return false;
            var peOff = BitConverter.ToInt32(dos.Slice(0x3C, 4));
            if (peOff < 0 || peOff > len - 6) return false;
            fs.Position = peOff;
            Span<byte> pe = stackalloc byte[6];
            if (fs.Read(pe) < 6) return false;
            // PE\0\0
            if (pe[0] != (byte)'P' || pe[1] != (byte)'E') return false;
            var machine = BitConverter.ToUInt16(pe.Slice(4, 2));
            return machine == PeMachineAmd64;
        }
        catch
        {
            return false;
        }
    }

    private static int _eglSyncScheduled;

    public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var installed = new List<LegendaryCli.GameRow>();
        var legendary = ResolveLegendary();
        var playtimes = EpicPlaytime.GetCachedMinutes();

        if (legendary is not null)
        {
            // Owned-title discovery has its own background cache in StoreSearchService.
            // Keep startup installed-only and query native Epic playtime alongside
            // Legendary so neither account entitlement refresh nor EGL import gates
            // the first library paint.
            // Never hold the first library response behind a remote playtime
            // request. The last-good snapshot is applied now; a changed remote
            // snapshot raises a derived-library refresh after it arrives.
            EpicPlaytime.RefreshCachedMinutes();

            try
            {
                var (code, stdout, _) = await CliRunner.RunAsync(
                    legendary, LegendaryCli.ListInstalledArgs(), null, null, ct).ConfigureAwait(false);
                if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
                    installed.AddRange(LegendaryCli.ParseLibraryJson(stdout, forceInstalled: true));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* fall through to manifests */ }

            // Importing EGL state may mutate Legendary's local manifests. Start it
            // only after list-installed releases Legendary's files, and never await
            // it on the startup library path. Native EGL manifests below already
            // preserve installed titles until a later scan sees the import.
            ct.ThrowIfCancellationRequested();
            ScheduleEglSyncImport(legendary);
        }

        var merged = LegendaryCli.MergeOwnedAndInstalled(
            Array.Empty<LegendaryCli.GameRow>(),
            installed);
        var hasLegendary = legendary is not null;
        var games = merged.Select(row => MapInstalledRow(row, hasLegendary)).ToList();

        // EGL manifests + LauncherInstalled.dat win when Legendary still says not installed.
        var egl = ReadEpicManifests(hasLegendary).Concat(ReadLauncherInstalled(games)).ToList();
        var withEgl = EpicEglMerge.ApplyInstalledOverlays(games, egl);
        return EpicPlaytime.Apply(withEgl, playtimes);
    }

    private static void ScheduleEglSyncImport(string legendary)
    {
        if (Interlocked.CompareExchange(ref _eglSyncScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await TryEglSyncImportOnceAsync(legendary, timeout.Token).ConfigureAwait(false);
        });
    }

    private static async Task TryEglSyncImportOnceAsync(string legendary, CancellationToken ct)
    {
        try
        {
            var installedJson = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "legendary", "installed.json");
            // Still sync when file missing or empty — EGL may have installs Legendary doesn't know.
            if (File.Exists(installedJson))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(installedJson, ct).ConfigureAwait(false);
                    if (text.Contains("\"app_name\"", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("\"appName\"", StringComparison.OrdinalIgnoreCase))
                        return;
                }
                catch { /* sync anyway */ }
            }

            await CliRunner.RunAsync(
                legendary,
                ["egl-sync", "--one-shot", "--import-only"],
                null, null, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    internal static GameEntry MapInstalledRow(LegendaryCli.GameRow row, bool hasLegendary)
    {
        return new GameEntry
        {
            Id = "epic:" + row.AppName,
            Title = row.Title,
            Store = StoreKind.Epic,
            Installed = row.Installed,
            // `legendary list-installed` is machine-local install evidence. It
            // does not prove that the active Epic account owns this title.
            Owned = false,
            CanInstall = !row.Installed && hasLegendary,
            Path = row.InstallPath,
            CoverUrl = row.CoverUrl,
            CoverSource = row.CoverUrl is null ? null : "epic-catalog",
            LaunchTarget = row.AppName,
            SizeBytes = row.SizeBytes,
            Status = row.Installed ? "Ready" : "Not installed",
            Deps = new[] { "Legendary" },
            LaunchNote = row.Installed
                ? "Launches via Legendary when available."
                : "Owned on Epic. Install via Legendary.",
        };
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        Report(game.Id, progress, InstallPhase.Preparing, 0, "Preparing Legendary…");
        var legendary = ResolveLegendary() ?? await EnsureLegendaryAsync(ct).ConfigureAwait(false);
        if (legendary is null)
        {
            return new InstallResult
            {
                Ok = false,
                Message = "Legendary required for install. Could not download legendary_windows_x64 — check network, or place legendary.exe in tools/.",
            };
        }

        var appName = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(appName))
            return new InstallResult { Ok = false, Message = "Missing Epic app name." };

        var basePath = installPath ?? Path.Combine(PathHelper.AppDataDir, "Epic");
        Directory.CreateDirectory(basePath);

        Report(game.Id, progress, InstallPhase.Preparing, 0, "Starting Legendary install…");

        try
        {
            var args = LegendaryCli.InstallArgs(appName, basePath);
            var (code, _, err) = await CliRunner.RunAsync(
                legendary,
                args,
                null,
                line =>
                {
                    var p = LegendaryCli.ToProgress(game.Id, line);
                    _progress[game.Id] = p;
                    progress?.Report(p);
                },
                ct).ConfigureAwait(false);

            if (code != 0)
            {
                Report(game.Id, progress, InstallPhase.Failed, null, err.Trim().Length > 0 ? err.Trim() : "Legendary install failed.");
                return new InstallResult { Ok = false, Message = err.Trim().Length > 0 ? err.Trim() : $"Legendary exited {code}." };
            }

            // Legendary installs under --base-path/<AppName>, not the parent alone.
            var titleDir = Path.Combine(basePath, appName);
            var installDir = Directory.Exists(titleDir)
                ? titleDir
                : Directory.GetDirectories(basePath)
                    .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                    .FirstOrDefault()
                  ?? basePath;

            Report(game.Id, progress, InstallPhase.Completed, 100, "Install complete.");
            return new InstallResult { Ok = true, Message = "Installed via Legendary.", Path = installDir };
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

    public async Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        var appName = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(appName))
            return new InstallResult { Ok = false, Message = "Missing Epic app name." };

        Report(game.Id, progress, InstallPhase.Preparing, 0, "Preparing Epic update…");
        var legendary = ResolveLegendary() ?? await EnsureLegendaryAsync(ct).ConfigureAwait(false);
        if (legendary is null)
        {
            // Fall back to Epic URI reinstall/update nudge.
            var meta = FindManifestMeta(appName, game.Path);
            var epic = ResolveEpicLauncher();
            if (epic is not null)
            {
                using var hider = StoreWindowHider.ForEpic();
                hider.Start(TimeSpan.FromSeconds(8));
                if (!ProcessHelper.IsProcessRunning("EpicGamesLauncher"))
                {
                    ProcessHelper.StartHidden(epic, "-silent");
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
                StoreWindowHider.HideOnce(StoreWindowHider.EpicProcessNames);
                foreach (var uri in BuildEpicLaunchUris(appName, meta))
                {
                    try
                    {
                        // Launch URI often triggers updates before start.
                        ProcessHelper.StartProtocol(uri.Replace("action=launch", "action=install")
                            .Replace("action=install&silent", "action=install&silent"));
                    }
                    catch { /* try next */ }
                }
                // Standard install action
                try
                {
                    if (meta?.CatalogNamespace is not null && meta.CatalogItemId is not null)
                    {
                        var triple = Uri.EscapeDataString($"{meta.CatalogNamespace}:{meta.CatalogItemId}:{appName}");
                        ProcessHelper.StartProtocol($"com.epicgames.launcher://apps/{triple}?action=install&silent=true");
                    }
                    else
                    {
                        ProcessHelper.StartProtocol($"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=install&silent=true");
                    }
                }
                catch { /* */ }
                Report(game.Id, progress, InstallPhase.Failed, null,
                    "Epic update handed off — install Legendary for in-app progress.");
                return new InstallResult
                {
                    Ok = false,
                    Message = "Epic update opened in Epic Games Launcher. Install Legendary for progress in Exo.",
                    HandoffOnly = true,
                };
            }
            return new InstallResult
            {
                Ok = false,
                Message = "Legendary / Epic not available for updates.",
            };
        }

        Report(game.Id, progress, InstallPhase.Downloading, 5, "Updating via Legendary…");
        try
        {
            var (code, _, err) = await CliRunner.RunAsync(
                legendary,
                LegendaryCli.UpdateArgs(appName),
                null,
                line =>
                {
                    var p = LegendaryCli.ToProgress(game.Id, line, InstallPhase.Downloading);
                    _progress[game.Id] = p;
                    progress?.Report(p);
                },
                ct).ConfigureAwait(false);

            if (code != 0)
            {
                Report(game.Id, progress, InstallPhase.Failed, null, err.Trim());
                return new InstallResult { Ok = false, Message = err.Trim().Length > 0 ? err.Trim() : $"Legendary exited {code}." };
            }

            Report(game.Id, progress, InstallPhase.Completed, 100, "Up to date.");
            return new InstallResult { Ok = true, Message = "Updated via Legendary." };
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
        // Cancellation must win before probing routes or starting any store/game process.
        ct.ThrowIfCancellationRequested();
        var appName = game.LaunchTarget;
        var meta = FindManifestMeta(appName, game.Path);
        var epic = ResolveEpicLauncher();
        var fallbackRoute = SelectEpicFallbackRoute(
            launcherAvailable: epic is not null,
            launchTargetAvailable: !string.IsNullOrWhiteSpace(appName));
        var existingGamePids = ProcessHelper.SnapshotLiveProcessIdsUnderPath(
            game.Path,
            EpicBootstrapProcessNames);
        if (existingGamePids.FirstOrDefault() is var existingPid && existingPid > 0)
        {
            return new LaunchResult
            {
                Ok = true,
                Message = "Already running",
                ProcessId = existingPid,
                BackendStarted = "epic",
            };
        }

        // 1) Legendary — preferred quiet path.
        ct.ThrowIfCancellationRequested();
        var legendary = ResolveLegendary();
        if (legendary is not null && !string.IsNullOrWhiteSpace(appName))
        {
            try
            {
                using var helper = ProcessHelper.StartHidden(legendary, LegendaryCli.LaunchArgs(appName))
                    ?? throw new InvalidOperationException("Legendary did not start.");
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var processWait = ProcessHelper.WaitForProcessUnderPathAsync(
                    game.Path,
                    TimeSpan.FromSeconds(30),
                    waitCts.Token,
                    EpicBootstrapProcessNames,
                    existingGamePids,
                    NewGameProcessConfirmationDelay);
                var helperExit = helper.WaitForExitAsync(ct);
                var completed = await Task.WhenAny(processWait, helperExit).ConfigureAwait(false);
                int? gamePid;
                if (completed == processWait)
                {
                    gamePid = await processWait.ConfigureAwait(false);
                }
                else
                {
                    await helperExit.ConfigureAwait(false);
                    waitCts.Cancel();
                    try { _ = await processWait.ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* replaced by short handoff grace */ }

                    // Legendary commonly exits immediately after a successful
                    // handoff. Allow a short spawn grace, but never burn the full
                    // 30-second process scan after a definite helper failure.
                    gamePid = await ProcessHelper.WaitForProcessUnderPathAsync(
                            game.Path,
                            TimeSpan.FromSeconds(helper.ExitCode == 0 ? 3 : 1),
                            ct,
                            EpicBootstrapProcessNames,
                            existingGamePids,
                            NewGameProcessConfirmationDelay)
                        .ConfigureAwait(false);
                    if (gamePid is null)
                        AppLog.Info(
                            $"Legendary launch for '{appName}' exited {helper.ExitCode} before a game process appeared; trying the next Epic route.");
                }
                ct.ThrowIfCancellationRequested();
                if (gamePid is int pid)
                {
                    return new LaunchResult
                    {
                        Ok = true,
                        Message = "Legendary launch started.",
                        ProcessId = pid,
                        BackendStarted = "legendary",
                    };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Info($"Legendary launch for '{appName}' failed; trying the next Epic route: {ex.Message}");
            }
        }

        ct.ThrowIfCancellationRequested();

        // 2) Direct install launch is only a fallback when the official Epic
        // launcher route is unavailable. Epic-managed games can require
        // exchange-code/EOS/EAC arguments; starting their final executable first
        // produces a short-lived process and a false-positive success even when a
        // local manifest has temporarily gone stale or missing.
        if (fallbackRoute == EpicFallbackRoute.DirectExecutable)
        {
            ct.ThrowIfCancellationRequested();
            using var direct = TryStartInstalledGame(game.Path, meta?.LaunchExecutable, appName);
            if (direct is not null)
            {
                var pid = await ProcessHelper.ConfirmDirectLaunchAsync(
                        direct,
                        game.Path,
                        existingGamePids,
                        ct,
                        EpicBootstrapProcessNames,
                        NewGameProcessConfirmationDelay)
                    .ConfigureAwait(false);
                if (pid is not null)
                {
                    return new LaunchResult
                    {
                        Ok = true,
                        Message = "Launched installed Epic title.",
                        ProcessId = pid,
                        BackendStarted = "epic-direct",
                    };
                }

                AppLog.Info(
                    $"Direct Epic launch for '{appName}' did not produce a stable game process; trying any remaining Epic route.");
            }
        }
        else
        {
            AppLog.Info(
                $"Epic manifest-backed launch for '{appName}' is using the authenticated launcher handoff before any direct executable.");
        }

        // 3) Epic Games Launcher protocol — full CatalogNamespace:CatalogItemId:AppName form.
        ct.ThrowIfCancellationRequested();
        if (epic is not null && !string.IsNullOrWhiteSpace(appName))
        {
            try
            {
                using var hider = StoreWindowHider.ForEpic();
                hider.Start(TimeSpan.FromSeconds(55));
                if (!ProcessHelper.IsProcessRunning("EpicGamesLauncher"))
                {
                    ct.ThrowIfCancellationRequested();
                    ProcessHelper.StartHidden(epic, "-silent");
                    // A protocol request issued while a cold client is still
                    // constructing its command listener can be accepted by
                    // Windows yet silently discarded by Epic. Wait for the
                    // launcher handoff surface before submitting this exact
                    // title's URI. This is intentionally bounded; the normal
                    // URI retry path below still owns a slow or unhealthy
                    // client.
                    await WaitForEpicCommandListenerAsync(ct).ConfigureAwait(false);
                }
                StoreWindowHider.HideOnce(StoreWindowHider.EpicProcessNames);

                var requested = false;
                var gamePid = await TryEpicLaunchUrisAsync(
                    BuildEpicLaunchUris(appName!, meta).ToArray(),
                    async (uri, attempt, token) =>
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            ProcessHelper.StartProtocol(uri);
                            StoreWindowHider.HideOnce(StoreWindowHider.EpicProcessNames);
                            requested = true;

                            // A cold Epic client can accept a protocol URI before it is
                            // ready to act on it. Give each supported URI a bounded turn,
                            // then try the non-silent/bare fallback rather than waiting
                            // the entire launch budget on the first shell invocation.
                            var pid = await ProcessHelper.WaitForProcessUnderPathAsync(
                                    game.Path,
                                    TimeSpan.FromSeconds(12),
                                    token,
                                    EpicBootstrapProcessNames,
                                    existingGamePids,
                                    NewGameProcessConfirmationDelay)
                                .ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (pid is not null)
                                AppLog.Info($"Epic launcher handoff for '{appName}' started game process {pid} on attempt {attempt}.");
                            else
                                AppLog.Info(
                                    $"Epic launcher handoff for '{appName}' produced no game process on attempt {attempt}; trying the next supported URI.");
                            return pid;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Info($"Epic launcher handoff for '{appName}' failed on attempt {attempt}: {ex.Message}");
                            return null;
                        }
                    },
                    ct).ConfigureAwait(false);
                if (gamePid is int pid)
                {
                    return new LaunchResult
                    {
                        Ok = true,
                        Message = "Epic game started.",
                        ProcessId = pid,
                        BackendStarted = "epic",
                    };
                }
                if (requested)
                {
                    AppLog.Warn(
                        $"Epic launcher accepted launch requests for '{appName}', but no game process appeared.");
                    return new LaunchResult
                    {
                        Ok = false,
                        HandoffOnly = true,
                        Message = "Epic launch was requested, but no game process appeared. Exo left the client hidden.",
                        BackendStarted = "epic",
                    };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new LaunchResult { Ok = false, Message = ex.Message };
            }
        }

        return new LaunchResult
        {
            Ok = false,
            Message = legendary is null && epic is null
                ? "Neither Legendary nor Epic Games Launcher was found."
                : $"Could not launch {(string.IsNullOrWhiteSpace(game.Title) ? appName : game.Title)}. Check the install path or refresh Legendary auth in Settings.",
        };
    }

    internal enum EpicFallbackRoute
    {
        DirectExecutable,
        LauncherHandoff,
    }

    internal static EpicFallbackRoute SelectEpicFallbackRoute(
        bool launcherAvailable,
        bool launchTargetAvailable) =>
        launcherAvailable && launchTargetAvailable
            ? EpicFallbackRoute.LauncherHandoff
            : EpicFallbackRoute.DirectExecutable;

    internal static async Task<int?> TryEpicLaunchUrisAsync(
        IReadOnlyList<string> uris,
        Func<string, int, CancellationToken, Task<int?>> tryUri,
        CancellationToken ct)
    {
        for (var index = 0; index < uris.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var pid = await tryUri(uris[index], index + 1, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (pid is not null) return pid;
        }

        return null;
    }

    /// <summary>
    /// Gives a cold Epic client a short, bounded chance to create the command
    /// listener which receives <c>com.epicgames.launcher://</c> launch URIs.
    /// A launcher process alone is not enough on a cold start: it can exist
    /// before its web helper is ready to dispatch title-specific requests.
    /// </summary>
    private static Task<bool> WaitForEpicCommandListenerAsync(CancellationToken ct) =>
        WaitForEpicCommandListenerAsync(
            () => ProcessHelper.IsProcessRunning("EpicGamesLauncher"),
            () => ProcessHelper.IsProcessRunning("EpicWebHelper"),
            static (delay, token) => Task.Delay(delay, token),
            ct);

    internal static async Task<bool> WaitForEpicCommandListenerAsync(
        Func<bool> launcherRunning,
        Func<bool> webHelperRunning,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken ct,
        int maxPolls = 20)
    {
        ArgumentNullException.ThrowIfNull(launcherRunning);
        ArgumentNullException.ThrowIfNull(webHelperRunning);
        ArgumentNullException.ThrowIfNull(delayAsync);
        if (maxPolls <= 0) throw new ArgumentOutOfRangeException(nameof(maxPolls));

        var launcherSeen = false;
        for (var attempt = 0; attempt < maxPolls; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var launcherReady = launcherRunning();
            var helperReady = webHelperRunning();

            // Some healthy Epic builds do not keep a separately observable
            // web helper. Once the launcher has survived one probe, accept it
            // as the bounded fallback instead of holding the user's launch for
            // the full timeout. A helper, when present, is the stronger signal.
            if (launcherReady && (helperReady || launcherSeen))
            {
                await delayAsync(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
                return true;
            }

            launcherSeen |= launcherReady;
            await delayAsync(TimeSpan.FromMilliseconds(350), ct).ConfigureAwait(false);
        }

        return false;
    }

    private sealed record EpicManifestMeta(
        string AppName,
        string? CatalogNamespace,
        string? CatalogItemId,
        string? LaunchExecutable,
        string? InstallLocation);

    private static EpicManifestMeta? FindManifestMeta(string? appName, string? installPath)
    {
        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) return null;

        foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var name = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                var loc = root.TryGetProperty("InstallLocation", out var i) ? i.GetString() : null;
                var matchApp = !string.IsNullOrWhiteSpace(appName) &&
                               string.Equals(name, appName, StringComparison.OrdinalIgnoreCase);
                var matchPath = !string.IsNullOrWhiteSpace(installPath) &&
                                !string.IsNullOrWhiteSpace(loc) &&
                                string.Equals(
                                    Path.GetFullPath(loc.TrimEnd('\\', '/')),
                                    Path.GetFullPath(installPath.TrimEnd('\\', '/')),
                                    StringComparison.OrdinalIgnoreCase);
                if (!matchApp && !matchPath) continue;

                return new EpicManifestMeta(
                    name ?? appName ?? "",
                    root.TryGetProperty("CatalogNamespace", out var ns) ? ns.GetString() : null,
                    root.TryGetProperty("CatalogItemId", out var ci) ? ci.GetString() : null,
                    root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null,
                    loc);
            }
            catch { /* skip */ }
        }
        return null;
    }

    private static IEnumerable<string> BuildEpicLaunchUris(string appName, EpicManifestMeta? meta) =>
        BuildEpicLaunchUris(appName, meta?.CatalogNamespace, meta?.CatalogItemId);

    internal static IEnumerable<string> BuildEpicLaunchUris(
        string appName,
        string? catalogNamespace,
        string? catalogItemId)
    {
        // Preferred: Namespace:CatalogItemId:AppName (URL-encoded) — required for many titles.
        if (!string.IsNullOrWhiteSpace(catalogNamespace) &&
            !string.IsNullOrWhiteSpace(catalogItemId))
        {
            var triple = Uri.EscapeDataString($"{catalogNamespace}:{catalogItemId}:{appName}");
            yield return $"com.epicgames.launcher://apps/{triple}?action=launch&silent=true";
            yield return $"com.epicgames.launcher://apps/{triple}?action=launch";
        }

        // Fallback: bare AppName (works for some older titles).
        yield return $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch&silent=true";
        yield return $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch";
    }

    private static Process? TryStartInstalledGame(string? installPath, string? launchExecutable, string? appName)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return null;

        var candidates = new List<string>();
        // Prefer the real game binary over helper launchers (Rocket League: RocketLeague.exe > Launcher.exe).
        candidates.Add(Path.Combine(installPath, "Binaries", "Win64", "RocketLeague.exe"));
        if (!string.IsNullOrWhiteSpace(appName) &&
            string.Equals(appName, "Sugar", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(installPath, "Binaries", "Win64", "RocketLeague.exe"));

        if (!string.IsNullOrWhiteSpace(launchExecutable))
        {
            var rel = launchExecutable.Replace('/', Path.DirectorySeparatorChar);
            // Skip generic Launcher.exe until after known game exes.
            if (!rel.EndsWith("Launcher.exe", StringComparison.OrdinalIgnoreCase))
                candidates.Insert(0, Path.Combine(installPath, rel));
            else
                candidates.Add(Path.Combine(installPath, rel));
        }

        candidates.Add(Path.Combine(installPath, "Binaries", "Win64", "Launcher.exe"));
        if (!string.IsNullOrWhiteSpace(appName))
            candidates.Add(Path.Combine(installPath, "Binaries", "Win64", appName + ".exe"));

        foreach (var exe in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(exe)) continue;
            try
            {
                // Normal window — game should appear on the taskbar, not minimized.
                return ProcessHelper.StartGame(exe, "", Path.GetDirectoryName(exe));
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Direct Epic launch fail {exe}: {ex.Message}");
            }
        }
        return null;
    }

    public async Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
    {
        var legendary = ResolveLegendary();
        if (legendary is null || string.IsNullOrWhiteSpace(game.LaunchTarget))
            return new InstallResult { Ok = false, Message = "Legendary required to uninstall Epic titles cleanly." };

        try
        {
            var (code, _, err) = await CliRunner.RunAsync(
                legendary, LegendaryCli.UninstallArgs(game.LaunchTarget), null, null, ct).ConfigureAwait(false);
            return new InstallResult
            {
                Ok = code == 0,
                Message = code == 0 ? "Uninstalled via Legendary." : (err.Trim().Length > 0 ? err.Trim() : $"Exit {code}"),
            };
        }
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
            StoreWindowHider.CollapseOrphanSurfaces(StoreWindowHider.EpicProcessNames);
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
            CanCancel = phase is not (InstallPhase.Completed or InstallPhase.Failed or InstallPhase.Cancelled or InstallPhase.Idle),
        };
        _progress[gameId] = p;
        progress?.Report(p);
    }

    private static IEnumerable<GameEntry> ReadEpicManifests(bool hasLegendary)
    {
        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) yield break;

        foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            GameEntry? entry = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                var appName = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                var install = root.TryGetProperty("InstallLocation", out var i) ? i.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(appName)) continue;
                long? size = null;
                if (root.TryGetProperty("InstallSize", out var sz) && sz.TryGetInt64(out var s)) size = s;

                var title = EpicEglMerge.NormalizeEpicTitle(name, appName);
                if (string.IsNullOrWhiteSpace(title))
                    title = appName!;

                var installed = !string.IsNullOrWhiteSpace(install) && Directory.Exists(install);
                entry = new GameEntry
                {
                    Id = "epic:" + (appName ?? name!.ToLowerInvariant()),
                    Title = title,
                    Store = StoreKind.Epic,
                    Installed = installed,
                    // EGL manifests are machine-install evidence, not proof
                    // that the currently active Epic account owns the title.
                    Owned = false,
                    CanInstall = !installed && hasLegendary,
                    Path = install,
                    LaunchTarget = appName,
                    SizeBytes = size,
                    Status = installed ? "Ready" : "Not installed",
                    Deps = hasLegendary
                        ? new[] { "Legendary (preferred)" }
                        : new[] { "Epic Games Launcher" },
                    LaunchNote = hasLegendary
                        ? "Launches via Legendary when available."
                        : "Launches the installed game directly when possible.",
                };
            }
            catch { /* skip */ }

            if (entry is not null) yield return entry;
        }
    }

    /// <summary>Parse ProgramData LauncherInstalled.dat (EGL install registry).</summary>
    private static IEnumerable<GameEntry> ReadLauncherInstalled(IReadOnlyList<GameEntry> ownedForTitles)
    {
        var roots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "LauncherInstalled.dat"),
        };

        var titleByApp = ownedForTitles
            .Where(g => !string.IsNullOrWhiteSpace(g.LaunchTarget))
            .GroupBy(g => g.LaunchTarget!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);

        foreach (var path in roots)
        {
            if (!File.Exists(path)) continue;
            IEnumerable<GameEntry>? batch = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("InstallationList", out var list)
                    || list.ValueKind != JsonValueKind.Array)
                    continue;

                var acc = new List<GameEntry>();
                foreach (var el in list.EnumerateArray())
                {
                    var appName = el.TryGetProperty("AppName", out var a) ? a.GetString()
                        : el.TryGetProperty("ArtifactId", out var art) ? art.GetString()
                        : null;
                    var install = el.TryGetProperty("InstallLocation", out var i) ? i.GetString() : null;
                    if (string.IsNullOrWhiteSpace(appName)) continue;
                    if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install)) continue;

                    titleByApp.TryGetValue(appName, out var title);
                    title = EpicEglMerge.NormalizeEpicTitle(title, appName);
                    if (string.IsNullOrWhiteSpace(title)) title = appName;

                    acc.Add(new GameEntry
                    {
                        Id = "epic:" + appName,
                        Title = title,
                        Store = StoreKind.Epic,
                        Installed = true,
                        // LauncherInstalled.dat is shared machine state. Keep
                        // the playable install without leaking another user's
                        // entitlement into the active account.
                        Owned = false,
                        CanInstall = false,
                        Path = install,
                        LaunchTarget = appName,
                        Status = "Ready",
                        Deps = new[] { "Epic Games Launcher" },
                        LaunchNote = "Installed via Epic Games Launcher.",
                    });
                }
                batch = acc;
            }
            catch { /* skip broken dat */ }

            if (batch is null) continue;
            foreach (var g in batch) yield return g;
        }
    }

    internal static string? ResolveLegendary()
    {
        var managedCache = Path.Combine(PathHelper.AppDataDir, "tools", "legendary.exe");
        var packagedTool = Path.Combine(PathHelper.AppDirectory, "tools", "legendary.exe");

        // PATH entries are explicitly user-installed and remain user-trusted,
        // unless PATH resolves back into an Exo-managed tools location.
        foreach (var candidate in new[]
                 {
                     CliRunner.ResolveOnPath("legendary.exe"),
                     CliRunner.ResolveOnPath("legendary"),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (IsSamePath(candidate, managedCache) || IsSamePath(candidate, packagedTool))
            {
                if (VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(
                        LegendaryReleaseAsset,
                        candidate,
                        IsValidAmd64Pe))
                    return candidate;
                continue;
            }
            if (IsValidAmd64Pe(candidate)) return candidate;
        }

        foreach (var managed in new[] { managedCache, packagedTool })
            if (VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(
                    LegendaryReleaseAsset,
                    managed,
                    IsValidAmd64Pe))
                return managed;

        var externalInstall = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "legendary",
            "legendary.exe");
        if (IsValidAmd64Pe(externalInstall)) return externalInstall;
        return null;
    }

    private static bool IsSamePath(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveEpicLauncher() =>
        CliRunner.FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Epic Games", "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe"));
}
