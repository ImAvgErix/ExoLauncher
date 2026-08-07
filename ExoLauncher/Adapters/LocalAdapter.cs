using System.Diagnostics;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>DRM-free / direct exe. Full install + launch with zero other client.</summary>
public sealed class LocalAdapter : IStoreAdapter
{
    /// <summary>
    /// Always-visible product entry: Install opens a folder picker and registers a portable game.
    /// Not a mock id — LaunchOrchestrator and InstallAsync treat this as a real install path.
    /// </summary>
    public const string AddPortableId = "local:add";

    private readonly Dictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);

    public StoreKind Store => StoreKind.Local;
    public string Id => "local";
    public string DisplayName => "Local";

    public bool IsAgentPresent() => true;

    public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthResult { Ok = true, Message = "Local store needs no account." });

    public static GameEntry CreateAddPortableEntry() => new()
    {
        Id = AddPortableId,
        Title = "Add portable game",
        Store = StoreKind.Local,
        Installed = false,
        Owned = true,
        CanInstall = true,
        Status = "Ready",
        Deps = Array.Empty<string>(),
        LaunchNote = "Pick a folder that contains the game executable. No store client — DRM-free / portable only.",
    };

    public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry> { CreateAddPortableEntry() };
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games"),
            Path.Combine(PathHelper.AppDataDir, "Games"),
            Path.Combine(PathHelper.AppDataDir, "library"),
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var exe = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(f => !IsInstallerLike(f));
                    if (exe is null) continue;
                    var title = Path.GetFileName(dir);
                    var id = "local:" + title.ToLowerInvariant().Replace(' ', '-');
                    if (string.Equals(id, AddPortableId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    games.Add(new GameEntry
                    {
                        Id = id,
                        Title = title,
                        Store = StoreKind.Local,
                        Installed = true,
                        Owned = true,
                        CanInstall = false,
                        Path = dir,
                        LaunchTarget = exe,
                        Status = "Ready",
                        SizeBytes = TryDirSize(dir),
                        Deps = Array.Empty<string>(),
                        LaunchNote = "Launches the executable directly. No store client.",
                    });
                }
            }
            catch { /* skip unreadable roots */ }
        }

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        // Local "install" = register a folder that already contains an exe (portable drop).
        var path = installPath ?? game.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return new InstallResult
            {
                Ok = false,
                Message = "Choose an existing folder that contains the game executable (portable / DRM-free).",
            };
        }

        Report(game.Id, progress, InstallPhase.Preparing, 10, "Scanning folder…");
        await Task.Delay(50, ct).ConfigureAwait(false);

        var exe = Directory.EnumerateFiles(path, "*.exe", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => !IsInstallerLike(f));
        if (exe is null)
        {
            Report(game.Id, progress, InstallPhase.Failed, null, "No playable exe found.");
            return new InstallResult { Ok = false, Message = "No playable .exe found in that folder." };
        }

        // Optionally copy into Exo library root for a stable path.
        var libraryRoot = Path.Combine(PathHelper.AppDataDir, "Games");
        Directory.CreateDirectory(libraryRoot);
        var dest = Path.Combine(libraryRoot, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            Report(game.Id, progress, InstallPhase.Installing, 40, "Copying into Exo library…");
            try
            {
                CopyDirectory(path, dest, ct, (pct, msg) =>
                    Report(game.Id, progress, InstallPhase.Installing, 40 + pct * 0.5, msg));
                path = dest;
                exe = Path.Combine(dest, Path.GetFileName(exe));
            }
            catch (OperationCanceledException)
            {
                Report(game.Id, progress, InstallPhase.Cancelled, null, "Cancelled.");
                return new InstallResult { Ok = false, Message = "Cancelled." };
            }
            catch (Exception ex)
            {
                // Fall back to original path without copy.
                Report(game.Id, progress, InstallPhase.Installing, 80, "Using original folder (copy skipped: " + ex.Message + ")");
            }
        }

        Report(game.Id, progress, InstallPhase.Completed, 100, "Ready.");
        return new InstallResult { Ok = true, Message = "Registered local game.", Path = path };
    }

    public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
        Task.FromResult(new InstallResult
        {
            Ok = false,
            Message = "Local/DRM-free titles update by replacing files in the install folder — no store updater.",
        });

    public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        var target = game.LaunchTarget ?? game.Path;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            return Task.FromResult(new LaunchResult { Ok = false, Message = "Executable not found." });

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = target,
                WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty,
                UseShellExecute = true,
            };
            var proc = Process.Start(psi);
            return Task.FromResult(new LaunchResult
            {
                Ok = proc is not null,
                Message = proc is not null ? "Started." : "Process did not start.",
                ProcessId = proc?.Id,
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new LaunchResult { Ok = false, Message = ex.Message });
        }
    }

    public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(game.Path) || !Directory.Exists(game.Path))
            return Task.FromResult(new InstallResult { Ok = false, Message = "Install path not found." });

        // Only delete if under Exo library root — never wipe arbitrary folders.
        var libraryRoot = Path.GetFullPath(Path.Combine(PathHelper.AppDataDir, "Games"));
        var full = Path.GetFullPath(game.Path);
        if (!full.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new InstallResult
            {
                Ok = false,
                Message = "Refusing to delete a folder outside the Exo library. Remove it manually if needed.",
            });
        }

        try
        {
            Directory.Delete(full, recursive: true);
            return Task.FromResult(new InstallResult { Ok = true, Message = "Removed from Exo library." });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new InstallResult { Ok = false, Message = ex.Message });
        }
    }

    public InstallProgress GetDownloadProgress(string gameId) =>
        _progress.TryGetValue(gameId, out var p) ? p : new InstallProgress { GameId = gameId, Phase = InstallPhase.Idle };

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
        Task.CompletedTask;

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

    private static bool IsInstallerLike(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name.Contains("uninstall") || name.Contains("setup") || name.Contains("install")
            || name.Contains("redist") || name.Contains("vcredist") || name.Contains("dxsetup");
    }

    private static long? TryDirSize(string dir)
    {
        try
        {
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }
        catch { return null; }
    }

    private static void CopyDirectory(string source, string dest, CancellationToken ct, Action<double, string> onProgress)
    {
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        Directory.CreateDirectory(dest);
        for (var i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(source, files[i]);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(files[i], target, overwrite: true);
            onProgress((i + 1) * 100.0 / Math.Max(1, files.Length), $"Copying {rel}");
        }
    }
}
