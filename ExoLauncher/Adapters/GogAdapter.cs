using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// GOG via heroic-gogdl when present; offline registry titles launch as local exes.
/// Galaxy is not required for the happy path.
/// https://github.com/Heroic-Games-Launcher/heroic-gogdl
/// </summary>
public sealed class GogAdapter : IStoreAdapter
{
    private readonly Dictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);

    public StoreKind Store => StoreKind.Gog;
    public string Id => "gog";
    public string DisplayName => "GOG";

    public bool IsAgentPresent() => ResolveGogdl() is not null || ResolveGalaxy() is not null;

    public async Task<AuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        var gogdl = ResolveGogdl();
        if (gogdl is null)
        {
            return new AuthResult
            {
                Ok = false,
                RequiresUserAction = true,
                Message = "gogdl not found. Install heroic-gogdl or place gogdl.exe on PATH / tools/.",
            };
        }

        try
        {
            var (code, _, err) = await CliRunner.RunAsync(gogdl, GogdlCli.AuthArgs(), null, null, ct)
                .ConfigureAwait(false);
            return new AuthResult
            {
                Ok = code == 0,
                RequiresUserAction = true,
                Message = code == 0 ? "gogdl auth finished." : (err.Trim().Length > 0 ? err.Trim() : "Auth failed."),
            };
        }
        catch (Exception ex)
        {
            return new AuthResult { Ok = false, Message = ex.Message, RequiresUserAction = true };
        }
    }

    public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games")
                ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (key is not null)
            {
                foreach (var subName in key.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    using var sub = key.OpenSubKey(subName);
                    if (sub is null) continue;
                    var name = sub.GetValue("gameName") as string ?? sub.GetValue("GAMENAME") as string;
                    var path = sub.GetValue("path") as string ?? sub.GetValue("PATH") as string;
                    var exe = sub.GetValue("exe") as string ?? sub.GetValue("EXE") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    games.Add(new GameEntry
                    {
                        Id = "gog:" + subName,
                        Title = name,
                        Store = StoreKind.Gog,
                        Installed = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path),
                        Owned = true,
                        CanInstall = ResolveGogdl() is not null,
                        Path = path,
                        LaunchTarget = exe is not null && path is not null ? Path.Combine(path, exe) : exe,
                        Status = "Ready",
                        Deps = ResolveGogdl() is not null
                            ? new[] { "gogdl (preferred)" }
                            : new[] { "GOG Galaxy (optional offline)" },
                        LaunchNote = "Offline GOG builds launch as local exes. gogdl handles install without Galaxy.",
                    });
                }
            }
        }
        catch { }

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var gogdl = ResolveGogdl();
        if (gogdl is null)
        {
            return new InstallResult
            {
                Ok = false,
                Message = "gogdl required for GOG install without Galaxy. Place gogdl.exe on PATH or tools/.",
            };
        }

        var gameId = ExtractGogId(game);
        if (string.IsNullOrWhiteSpace(gameId))
            return new InstallResult { Ok = false, Message = "Missing GOG product id." };

        var path = installPath ?? Path.Combine(PathHelper.AppDataDir, "GOG", gameId);
        Directory.CreateDirectory(path);

        Report(game.Id, progress, InstallPhase.Preparing, 0, "Starting gogdl download…");
        try
        {
            var (code, _, err) = await CliRunner.RunAsync(
                gogdl,
                GogdlCli.DownloadArgs(gameId, path),
                null,
                line =>
                {
                    var p = GogdlCli.ToProgress(game.Id, line);
                    _progress[game.Id] = p;
                    progress?.Report(p);
                },
                ct).ConfigureAwait(false);

            if (code != 0)
            {
                Report(game.Id, progress, InstallPhase.Failed, null, err.Trim());
                return new InstallResult { Ok = false, Message = err.Trim().Length > 0 ? err.Trim() : $"gogdl exited {code}." };
            }

            Report(game.Id, progress, InstallPhase.Completed, 100, "Install complete.");
            return new InstallResult { Ok = true, Message = "Installed via gogdl.", Path = path };
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
        var gogdl = ResolveGogdl();
        if (gogdl is null)
            return new InstallResult { Ok = false, Message = "gogdl required for update." };

        var gameId = ExtractGogId(game);
        var path = game.Path ?? Path.Combine(PathHelper.AppDataDir, "GOG", gameId ?? "unknown");
        if (string.IsNullOrWhiteSpace(gameId))
            return new InstallResult { Ok = false, Message = "Missing GOG product id." };

        Report(game.Id, progress, InstallPhase.Preparing, 0, "Repairing / updating via gogdl…");
        try
        {
            var (code, _, err) = await CliRunner.RunAsync(
                gogdl,
                GogdlCli.RepairArgs(gameId, path),
                null,
                line =>
                {
                    var p = GogdlCli.ToProgress(game.Id, line);
                    _progress[game.Id] = p;
                    progress?.Report(p);
                },
                ct).ConfigureAwait(false);

            if (code != 0)
            {
                Report(game.Id, progress, InstallPhase.Failed, null, err.Trim());
                return new InstallResult { Ok = false, Message = err.Trim().Length > 0 ? err.Trim() : $"gogdl exited {code}." };
            }

            Report(game.Id, progress, InstallPhase.Completed, 100, "Up to date.");
            return new InstallResult { Ok = true, Message = "Updated via gogdl." };
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

    public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget) && File.Exists(game.LaunchTarget))
        {
            try
            {
                var p = ProcessHelper.StartMinimized(game.LaunchTarget);
                // StartMinimized still runs the game; for exe launch use normal start.
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = game.LaunchTarget,
                    WorkingDirectory = Path.GetDirectoryName(game.LaunchTarget) ?? string.Empty,
                    UseShellExecute = true,
                };
                var proc = System.Diagnostics.Process.Start(psi);
                return Task.FromResult(new LaunchResult
                {
                    Ok = proc is not null,
                    Message = proc is not null ? "Started GOG title." : "Failed to start.",
                    ProcessId = proc?.Id,
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new LaunchResult { Ok = false, Message = ex.Message });
            }
        }

        // Prefer gogdl launch when path is known.
        var gogdl = ResolveGogdl();
        var gameId = ExtractGogId(game);
        if (gogdl is not null && gameId is not null && !string.IsNullOrWhiteSpace(game.Path))
        {
            try
            {
                var p = ProcessHelper.StartMinimized(gogdl, string.Join(' ', GogdlCli.LaunchArgs(gameId, game.Path)));
                return Task.FromResult(new LaunchResult
                {
                    Ok = p is not null,
                    Message = p is not null ? "gogdl launch started." : "gogdl did not start.",
                    ProcessId = p?.Id,
                    BackendStarted = "gogdl",
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new LaunchResult { Ok = false, Message = ex.Message });
            }
        }

        var galaxy = ResolveGalaxy();
        if (galaxy is not null && game.Id.StartsWith("gog:", StringComparison.Ordinal))
        {
            var id = game.Id["gog:".Length..];
            try
            {
                ProcessHelper.StartProtocol($"goggalaxy://openGameView/{id}");
                return Task.FromResult(new LaunchResult
                {
                    Ok = true,
                    Message = "GOG Galaxy open requested (gogdl path unavailable).",
                    BackendStarted = "gog",
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new LaunchResult { Ok = false, Message = ex.Message });
            }
        }

        return Task.FromResult(new LaunchResult { Ok = false, Message = "No launch path for this GOG title." });
    }

    public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(game.Path) || !Directory.Exists(game.Path))
            return Task.FromResult(new InstallResult { Ok = false, Message = "Install path not found." });

        var libraryRoot = Path.GetFullPath(Path.Combine(PathHelper.AppDataDir, "GOG"));
        var full = Path.GetFullPath(game.Path);
        if (!full.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new InstallResult
            {
                Ok = false,
                Message = "Refusing to delete a folder outside the Exo GOG library. Use GOG/gogdl uninstall if needed.",
            });
        }

        try
        {
            Directory.Delete(full, recursive: true);
            return Task.FromResult(new InstallResult { Ok = true, Message = "Removed from Exo GOG library." });
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
        if (options.CloseStoreUiAfterExit)
            ProcessHelper.TryCloseProcesses("GalaxyClient");
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

    private static string? ExtractGogId(GameEntry game)
    {
        if (game.Id.StartsWith("gog:", StringComparison.OrdinalIgnoreCase))
            return game.Id["gog:".Length..];
        return game.LaunchTarget;
    }

    internal static string? ResolveGogdl()
    {
        var fromPath = CliRunner.ResolveOnPath("gogdl.exe") ?? CliRunner.ResolveOnPath("gogdl");
        if (fromPath is not null) return fromPath;
        return CliRunner.FirstExisting(
            Path.Combine(PathHelper.AppDataDir, "tools", "gogdl.exe"),
            Path.Combine(PathHelper.AppDirectory, "tools", "gogdl.exe"));
    }

    private static string? ResolveGalaxy() =>
        CliRunner.FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "GOG Galaxy", "GalaxyClient.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "GOG Galaxy", "GalaxyClient.exe"));
}
