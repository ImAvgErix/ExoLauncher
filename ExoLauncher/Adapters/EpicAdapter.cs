using System.Text.Json;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Epic Games. Prefers Legendary (CLI) when present; falls back to Epic launcher
/// protocol. Epic GUI is optional — Legendary can be first-class for acquisition.
/// </summary>
public sealed class EpicAdapter : IStoreAdapter
{
    public StoreKind Store => StoreKind.Epic;
    public string DisplayName => "Epic";

    public bool IsAgentPresent() => ResolveLegendary() is not null || ResolveEpicLauncher() is not null;

    public Task<IReadOnlyList<GameEntry>> DiscoverAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry>();

        // Legendary binary path present → titles may still need `legendary list-installed`.
        // Phase 1: parse Epic manifest folder when available; stub empty otherwise.
        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (Directory.Exists(manifestDir))
        {
            foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    var name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                    var appName = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                    var install = root.TryGetProperty("InstallLocation", out var i) ? i.GetString() : null;
                    var catalogId = root.TryGetProperty("CatalogItemId", out var c) ? c.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    long? size = null;
                    if (root.TryGetProperty("InstallSize", out var sz) && sz.TryGetInt64(out var s))
                        size = s;

                    games.Add(new GameEntry
                    {
                        Id = "epic:" + (appName ?? catalogId ?? name!.ToLowerInvariant()),
                        Title = name!,
                        Store = StoreKind.Epic,
                        Installed = !string.IsNullOrWhiteSpace(install) && Directory.Exists(install),
                        Path = install,
                        LaunchTarget = appName ?? catalogId,
                        SizeBytes = size,
                        Status = "Ready",
                        Deps = ResolveLegendary() is not null
                            ? new[] { "Legendary (preferred)" }
                            : new[] { "Epic Games Launcher" },
                        LaunchNote = ResolveLegendary() is not null
                            ? "Launches via Legendary when available; Epic GUI stays optional."
                            : "Uses Epic launcher as backend. Prefer Legendary for a quieter path.",
                    });
                }
                catch { /* skip bad manifests */ }
            }
        }

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        var legendary = ResolveLegendary();
        if (legendary is not null && !string.IsNullOrWhiteSpace(game.LaunchTarget))
        {
            try
            {
                var p = ProcessHelper.StartMinimized(legendary, $"launch \"{game.LaunchTarget}\"");
                return new LaunchResult
                {
                    Ok = p is not null,
                    Message = p is not null ? "Legendary launch started." : "Legendary did not start.",
                    ProcessId = p?.Id,
                    BackendStarted = "legendary",
                };
            }
            catch (Exception ex)
            {
                return new LaunchResult { Ok = false, Message = ex.Message };
            }
        }

        var epic = ResolveEpicLauncher();
        if (epic is not null)
        {
            if (!ProcessHelper.IsProcessRunning("EpicGamesLauncher"))
            {
                ProcessHelper.StartMinimized(epic);
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(game.LaunchTarget))
            {
                try
                {
                    ProcessHelper.StartProtocol($"com.epicgames.launcher://apps/{game.LaunchTarget}?action=launch&silent=true");
                    return new LaunchResult
                    {
                        Ok = true,
                        Message = "Epic launch requested.",
                        BackendStarted = "epic",
                    };
                }
                catch (Exception ex)
                {
                    return new LaunchResult { Ok = false, Message = ex.Message };
                }
            }
        }

        return new LaunchResult
        {
            Ok = false,
            Message = "Neither Legendary nor Epic Games Launcher was found.",
        };
    }

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        if (options.CloseStoreUiAfterExit)
            ProcessHelper.TryCloseProcesses("EpicGamesLauncher");
        return Task.CompletedTask;
    }

    private static string? ResolveLegendary()
    {
        var fromPath = ProcessHelper.FindOnPath("legendary.exe");
        if (fromPath is not null) return fromPath;

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "legendary", "legendary.exe");
        return File.Exists(local) ? local : null;
    }

    private static string? ResolveEpicLauncher()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Epic Games", "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
