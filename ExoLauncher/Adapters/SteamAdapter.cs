using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam library via appmanifest + minimized install/launch.
/// Steam runtime usually remains installed; user should not need to open Steam day-to-day.
/// Anonymous SteamCMD is NOT used for owned paid games.
/// </summary>
public sealed class SteamAdapter : IStoreAdapter
{
    private readonly Dictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);

    public StoreKind Store => StoreKind.Steam;
    public string Id => "steam";
    public string DisplayName => "Steam";

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

                    // StateFlags in acf can indicate update needed — best-effort.
                    var stateFlags = SteamProtocol.MatchAcfField(text, "StateFlags");
                    var updateAvailable = stateFlags is not null && stateFlags != "4";

                    games.Add(new GameEntry
                    {
                        Id = "steam:" + appId,
                        Title = name,
                        Store = StoreKind.Steam,
                        Installed = installed,
                        Owned = true,
                        CanInstall = true,
                        UpdateAvailable = updateAvailable && installed,
                        Path = path,
                        LaunchTarget = appId,
                        SizeBytes = size,
                        Status = installed ? (updateAvailable ? "Update" : "Ready") : "Not installed",
                        Deps = new[] { "Steam client" },
                        LaunchNote = "steam://rungameid. Steam stays as the DRM backend; UI can be minimized.",
                    });
                }
                catch { /* skip corrupt manifests */ }
            }
        }

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var appId = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(appId))
            return new InstallResult { Ok = false, Message = "Missing Steam app id." };

        var steamExe = ResolveSteamExe();
        if (steamExe is null)
            return new InstallResult { Ok = false, Message = "Steam is not installed." };

        Report(game.Id, progress, InstallPhase.Preparing, 5, "Starting Steam minimized…");

        try
        {
            if (!ProcessHelper.IsProcessRunning("steam"))
            {
                ProcessHelper.StartMinimized(steamExe, "-silent");
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }

            ProcessHelper.StartProtocol(SteamProtocol.InstallUri(appId));
            Report(game.Id, progress, InstallPhase.Downloading, 10, "Steam install requested (UI minimized)…");

            // Poll library folder for the appmanifest + growing common dir.
            var start = DateTimeOffset.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                MinimizeSteamUi();

                var hit = FindInstalled(appId);
                if (hit is not null)
                {
                    var size = TryDirSize(hit.Value.Path);
                    var pct = size > 0 ? Math.Min(99, 15 + Math.Log10(size + 1) * 10) : 30;
                    Report(game.Id, progress, InstallPhase.Installing, pct,
                        $"Downloading {game.Title}… ({FormatBytes(size)})");

                    // Consider installed when manifest StateFlags is fully installed (4) or folder is large and stable.
                    if (hit.Value.Installed && size > 10 * 1024 * 1024)
                    {
                        // Brief settle
                        await Task.Delay(3000, ct).ConfigureAwait(false);
                        var size2 = TryDirSize(hit.Value.Path);
                        if (size2 >= size)
                        {
                            Report(game.Id, progress, InstallPhase.Completed, 100, "Installed (Steam backend).");
                            return new InstallResult { Ok = true, Message = "Installed via minimized Steam.", Path = hit.Value.Path };
                        }
                    }
                }
                else
                {
                    var elapsed = (DateTimeOffset.UtcNow - start).TotalSeconds;
                    Report(game.Id, progress, InstallPhase.Downloading,
                        Math.Min(25, 5 + elapsed / 10),
                        "Waiting for Steam to create the install…");
                }

                if ((DateTimeOffset.UtcNow - start).TotalMinutes > 120)
                {
                    Report(game.Id, progress, InstallPhase.Failed, null, "Install watch timed out.");
                    return new InstallResult { Ok = false, Message = "Steam install watch timed out." };
                }

                await Task.Delay(2500, ct).ConfigureAwait(false);
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

    public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        // steam://install also updates; validate is alternative.
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget))
        {
            try
            {
                EnsureSteamSilent();
                ProcessHelper.StartProtocol(SteamProtocol.InstallUri(game.LaunchTarget));
                Report(game.Id, progress, InstallPhase.Downloading, 10, "Steam update requested…");
                return Task.FromResult(new InstallResult
                {
                    Ok = true,
                    Message = "Steam update requested (minimized). Progress continues in Steam downloads.",
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new InstallResult { Ok = false, Message = ex.Message });
            }
        }
        return Task.FromResult(new InstallResult { Ok = false, Message = "Missing app id." });
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        var appId = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(appId))
            return new LaunchResult { Ok = false, Message = "Missing Steam app id." };

        var steamExe = ResolveSteamExe();
        string? backend = null;

        if (steamExe is not null && !ProcessHelper.IsProcessRunning("steam"))
        {
            var p = ProcessHelper.StartMinimized(steamExe, "-silent");
            backend = "steam";
            if (p is not null)
            {
                await Task.Delay(1500, ct).ConfigureAwait(false);
                if (options.MinimizeStoreUi)
                    ProcessHelper.MinimizeProcessWindows(p.Id);
            }
        }

        try
        {
            ProcessHelper.StartProtocol(SteamProtocol.RunGameUri(appId));
            if (options.MinimizeStoreUi) MinimizeSteamUi();
            return new LaunchResult
            {
                Ok = true,
                Message = "Steam launch requested.",
                BackendStarted = backend ?? "steam",
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message, BackendStarted = backend };
        }
    }

    public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
    {
        // Steam has no clean public silent uninstall URI we trust; open uninstall via install UI.
        if (string.IsNullOrWhiteSpace(game.LaunchTarget))
            return Task.FromResult(new InstallResult { Ok = false, Message = "Missing app id." });

        try
        {
            EnsureSteamSilent();
            ProcessHelper.StartProtocol($"steam://uninstall/{game.LaunchTarget}");
            return Task.FromResult(new InstallResult
            {
                Ok = true,
                Message = "Steam uninstall requested (minimized). Confirm in Steam if prompted.",
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new InstallResult { Ok = false, Message = ex.Message });
        }
    }

    public InstallProgress GetDownloadProgress(string gameId) =>
        _progress.TryGetValue(gameId, out var p) ? p : new InstallProgress { GameId = gameId, Phase = InstallPhase.Idle };

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        // Soft-close browser helpers only — do not kill Steam entirely (downloads/friends).
        if (options.CloseStoreUiAfterExit)
            ProcessHelper.TryCloseProcesses("steamwebhelper");
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
            ProcessHelper.StartMinimized(steamExe, "-silent");
        MinimizeSteamUi();
    }

    private static void MinimizeSteamUi()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("steam"))
            {
                try { ProcessHelper.MinimizeProcessWindows(p.Id); }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

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
