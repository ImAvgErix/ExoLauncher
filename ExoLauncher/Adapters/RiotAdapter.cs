using System.Diagnostics;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// Riot fixed catalog — orchestration of official RiotClientServices, not a custom CDN client.
/// Vanguard remains required for online titles; Exo never touches vgk/vgc.
/// </summary>
public sealed class RiotAdapter : IStoreAdapter
{
    private readonly Dictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);

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
        var root = ResolveRiotRoot();
        var rcs = ResolveRiotClientServices();

        foreach (var (productId, title) in RiotCli.FixedCatalog)
        {
            ct.ThrowIfCancellationRequested();
            var installedPath = FindProductPath(root, productId, title);
            var installed = installedPath is not null;

            games.Add(new GameEntry
            {
                Id = "riot:" + productId,
                Title = title,
                Store = StoreKind.Riot,
                Installed = installed,
                Owned = true, // fixed catalog tiles — install path is official client
                CanInstall = rcs is not null || ResolveBootstrapInstaller() is not null,
                Path = installedPath,
                LaunchTarget = productId,
                Status = installed ? "Ready" : (rcs is not null ? "Not installed" : "Client missing"),
                Deps = productId == "valorant"
                    ? new[] { "Riot Client", "Vanguard" }
                    : new[] { "Riot Client" },
                LaunchNote = productId == "valorant"
                    ? "Official RiotClientServices launch. Vanguard must stay installed for online play — Exo does not bypass it."
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

        Report(game.Id, progress, InstallPhase.Preparing, 5, "Starting official Riot install (UI hidden)…");

        try
        {
            Process? proc;
            if (rcs is not null)
            {
                // Launch/update path also drives install when product is missing.
                proc = StartHidden(rcs, RiotCli.LaunchArgs(productId));
            }
            else
            {
                proc = StartHidden(bootstrap!, RiotCli.BootstrapInstallArgs());
            }

            if (proc is null)
            {
                Report(game.Id, progress, InstallPhase.Failed, null, "Could not start Riot installer.");
                return new InstallResult { Ok = false, Message = "Could not start Riot installer." };
            }

            // Hide main windows aggressively for a few seconds.
            for (var i = 0; i < 10; i++)
            {
                ct.ThrowIfCancellationRequested();
                ProcessHelper.MinimizeProcessWindows(proc.Id);
                HideRiotUiWindows();
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            // Watch install dirs + process; Exo owns the progress UI.
            var start = DateTimeOffset.UtcNow;
            var lastSize = 0L;
            while (!ct.IsCancellationRequested)
            {
                HideRiotUiWindows();

                var path = FindProductPath(ResolveRiotRoot(), productId, game.Title);
                long size = 0;
                if (path is not null)
                {
                    try
                    {
                        size = DirSizeBounded(path, maxFiles: 5000);
                    }
                    catch { }
                }

                // Heuristic percent: grows with install dir; caps at 95 until process exits.
                var elapsed = (DateTimeOffset.UtcNow - start).TotalSeconds;
                double pct;
                if (path is not null && size > 50 * 1024 * 1024)
                    pct = Math.Min(95, 20 + Math.Log10(size) * 8);
                else
                    pct = Math.Min(40, 5 + elapsed / 3);

                var bps = size > lastSize && elapsed > 1
                    ? (size - lastSize) / Math.Max(1, 2)
                    : (double?)null;
                lastSize = size;

                var stillRunning = !proc.HasExited || ProcessHelper.IsProcessRunning("RiotClientServices")
                    || ProcessHelper.IsProcessRunning("RiotClientUx");

                Report(game.Id, progress, InstallPhase.Installing, pct,
                    path is not null
                        ? $"Installing {game.Title}… ({FormatBytes(size)})"
                        : $"Waiting for Riot to place {game.Title}…");

                // Completion: product folder exists with substantial content and Riot UX has settled.
                if (path is not null && size > 100 * 1024 * 1024 && !stillRunning && elapsed > 15)
                    break;

                // Also complete if product folder is clearly present and launchable after long settle.
                if (path is not null && size > 500 * 1024 * 1024 && elapsed > 30)
                    break;

                // Timeout soft-complete so we never spin forever.
                if (elapsed > 45 * 60)
                {
                    Report(game.Id, progress, InstallPhase.Failed, null, "Install watch timed out. Check Riot Client manually.");
                    return new InstallResult { Ok = false, Message = "Install watch timed out." };
                }

                await Task.Delay(2000, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            // Force-close Riot UI chrome only — leave Vanguard alone.
            SoftCloseRiotUi();

            var finalPath = FindProductPath(ResolveRiotRoot(), productId, game.Title);
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
        catch (OperationCanceledException)
        {
            SoftCloseRiotUi();
            Report(game.Id, progress, InstallPhase.Cancelled, null, "Cancelled.");
            return new InstallResult { Ok = false, Message = "Cancelled." };
        }
        catch (Exception ex)
        {
            Report(game.Id, progress, InstallPhase.Failed, null, ex.Message);
            return new InstallResult { Ok = false, Message = ex.Message };
        }
    }

    public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default)
        // Same official path as install — Riot updates on launch-product.
        => InstallAsync(game, game.Path, progress, ct);

    public async Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        var rcs = ResolveRiotClientServices();
        if (rcs is null)
            return new LaunchResult { Ok = false, Message = "RiotClientServices.exe not found." };

        var productId = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(productId))
            return new LaunchResult { Ok = false, Message = "Missing Riot product id." };

        try
        {
            var p = StartHidden(rcs, RiotCli.LaunchArgs(productId));
            if (p is null)
                return new LaunchResult { Ok = false, Message = "Riot Client did not start." };

            await Task.Delay(800, ct).ConfigureAwait(false);
            if (options.MinimizeStoreUi)
            {
                ProcessHelper.MinimizeProcessWindows(p.Id);
                HideRiotUiWindows();
            }

            return new LaunchResult
            {
                Ok = true,
                Message = "Riot launch started.",
                ProcessId = p.Id,
                BackendStarted = "riot",
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message };
        }
    }

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
            var p = StartHidden(rcs, RiotCli.UninstallArgs(productId));
            if (p is null)
                return new InstallResult { Ok = false, Message = "Uninstall did not start." };

            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            SoftCloseRiotUi();
            return new InstallResult { Ok = true, Message = "Uninstall requested via RiotClientServices." };
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
            SoftCloseRiotUi();
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

    private static Process? StartHidden(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized,
        };
        return Process.Start(psi);
    }

    private static void SoftCloseRiotUi()
    {
        foreach (var name in RiotCli.UiProcessNames)
        {
            if (RiotCli.IsProtectedProcess(name)) continue;
            ProcessHelper.TryCloseProcesses(name);
        }
    }

    private static void HideRiotUiWindows()
    {
        foreach (var name in RiotCli.UiProcessNames)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { ProcessHelper.MinimizeProcessWindows(p.Id); }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }
    }

    private static string? FindProductPath(string? root, string productId, string title)
    {
        var candidates = new List<string>();
        if (root is not null)
        {
            candidates.Add(Path.Combine(root, title));
            candidates.Add(Path.Combine(root, productId));
            if (productId == "valorant") candidates.Add(Path.Combine(root, "VALORANT"));
            if (productId == "league_of_legends") candidates.Add(Path.Combine(root, "League of Legends"));
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games", title));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games", "VALORANT"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games", "League of Legends"));

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? ResolveRiotRoot()
    {
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

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? ResolveRiotClientServices()
    {
        var root = ResolveRiotRoot();
        if (root is not null)
        {
            var direct = Path.Combine(root, "Riot Client", "RiotClientServices.exe");
            if (File.Exists(direct)) return direct;
        }

        return Cli.CliRunner.FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Riot Games", "Riot Client", "RiotClientServices.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Riot Games", "Riot Client", "RiotClientServices.exe"));
    }

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
