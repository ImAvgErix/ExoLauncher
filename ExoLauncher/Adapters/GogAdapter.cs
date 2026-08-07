using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>GOG Galaxy or offline GOG builds. Offline builds can behave like Local.</summary>
public sealed class GogAdapter : IStoreAdapter
{
    public StoreKind Store => StoreKind.Gog;
    public string DisplayName => "GOG";

    public bool IsAgentPresent() => ResolveGalaxy() is not null;

    public Task<IReadOnlyList<GameEntry>> DiscoverAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games")
                ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (key is null)
                return Task.FromResult<IReadOnlyList<GameEntry>>(games);

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
                    Path = path,
                    LaunchTarget = exe is not null && path is not null ? Path.Combine(path, exe) : exe,
                    Status = "Ready",
                    Deps = new[] { "GOG Galaxy (optional for offline builds)" },
                    LaunchNote = "Offline GOG builds launch as local exes. Galaxy used when present for online titles.",
                });
            }
        }
        catch { }

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
    }

    public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget) && File.Exists(game.LaunchTarget))
        {
            try
            {
                var p = ProcessHelper.StartMinimized(game.LaunchTarget);
                return Task.FromResult(new LaunchResult
                {
                    Ok = p is not null,
                    Message = p is not null ? "Started GOG title." : "Failed to start.",
                    ProcessId = p?.Id,
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
                    Message = "GOG Galaxy open requested.",
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

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        if (options.CloseStoreUiAfterExit)
            ProcessHelper.TryCloseProcesses("GalaxyClient");
        return Task.CompletedTask;
    }

    private static string? ResolveGalaxy()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "GOG Galaxy", "GalaxyClient.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "GOG Galaxy", "GalaxyClient.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
