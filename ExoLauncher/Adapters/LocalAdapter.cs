using System.Diagnostics;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>DRM-free / direct exe games. First-class; no backend agent required.</summary>
public sealed class LocalAdapter : IStoreAdapter
{
    public StoreKind Store => StoreKind.Local;
    public string DisplayName => "Local";

    public bool IsAgentPresent() => true;

    public Task<IReadOnlyList<GameEntry>> DiscoverAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry>();

        // Common portable / GOG offline / user drop folders.
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "Games"),
        };

        foreach (var root in roots)
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
                    games.Add(new GameEntry
                    {
                        Id = "local:" + title.ToLowerInvariant().Replace(' ', '-'),
                        Title = title,
                        Store = StoreKind.Local,
                        Installed = true,
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

    public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        var target = game.LaunchTarget ?? game.Path;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            return Task.FromResult(new LaunchResult
            {
                Ok = false,
                Message = "Executable not found.",
            });
        }

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

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
        => Task.CompletedTask;

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
}
