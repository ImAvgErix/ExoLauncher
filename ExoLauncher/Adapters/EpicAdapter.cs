using System.Text.Json;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Epic via Legendary CLI — true no-Epic-GUI path when Legendary is present.
/// https://github.com/derrod/legendary
/// </summary>
public sealed class EpicAdapter : IStoreAdapter
{
    private readonly Dictionary<string, InstallProgress> _progress = new(StringComparer.OrdinalIgnoreCase);

    public StoreKind Store => StoreKind.Epic;
    public string Id => "epic";
    public string DisplayName => "Epic";

    public bool IsAgentPresent() => ResolveLegendary() is not null || ResolveEpicLauncher() is not null;

    public async Task<AuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        var legendary = ResolveLegendary();
        if (legendary is null)
        {
            return new AuthResult
            {
                Ok = false,
                RequiresUserAction = true,
                Message = "Legendary not found. Install Legendary (https://github.com/derrod/legendary) or place legendary.exe on PATH.",
            };
        }

        try
        {
            // legendary auth opens browser / device code — user action required.
            var (code, _, err) = await CliRunner.RunAsync(legendary, LegendaryCli.AuthArgs(), null, null, ct)
                .ConfigureAwait(false);
            return new AuthResult
            {
                Ok = code == 0,
                RequiresUserAction = true,
                Message = code == 0 ? "Legendary auth finished." : (err.Trim().Length > 0 ? err.Trim() : "Auth failed."),
            };
        }
        catch (Exception ex)
        {
            return new AuthResult { Ok = false, Message = ex.Message, RequiresUserAction = true };
        }
    }

    public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry>();
        var legendary = ResolveLegendary();

        if (legendary is not null)
        {
            try
            {
                var (code, stdout, _) = await CliRunner.RunAsync(
                    legendary, LegendaryCli.ListInstalledArgs(), null, null, ct).ConfigureAwait(false);
                if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
                    games.AddRange(ParseLegendaryInstalledJson(stdout));
            }
            catch { /* fall through to manifests */ }
        }

        // Also merge Epic manifest folder (works even without Legendary).
        foreach (var g in ReadEpicManifests())
        {
            if (!games.Any(x => string.Equals(x.LaunchTarget, g.LaunchTarget, StringComparison.OrdinalIgnoreCase)))
                games.Add(g);
        }

        return games;
    }

    public async Task<InstallResult> InstallAsync(
        GameEntry game,
        string? installPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var legendary = ResolveLegendary();
        if (legendary is null)
        {
            return new InstallResult
            {
                Ok = false,
                Message = "Legendary required for install without Epic GUI. Install Legendary and sign in (legendary auth).",
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

            Report(game.Id, progress, InstallPhase.Completed, 100, "Install complete.");
            return new InstallResult { Ok = true, Message = "Installed via Legendary.", Path = basePath };
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
        var legendary = ResolveLegendary();
        if (legendary is null)
            return new InstallResult { Ok = false, Message = "Legendary required for update." };

        var appName = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(appName))
            return new InstallResult { Ok = false, Message = "Missing Epic app name." };

        Report(game.Id, progress, InstallPhase.Preparing, 0, "Checking Legendary update…");
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
        var legendary = ResolveLegendary();
        if (legendary is not null && !string.IsNullOrWhiteSpace(game.LaunchTarget))
        {
            try
            {
                // Launch detached so Exo does not block on the game process.
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
                    ProcessHelper.StartProtocol(
                        $"com.epicgames.launcher://apps/{game.LaunchTarget}?action=launch&silent=true");
                    return new LaunchResult
                    {
                        Ok = true,
                        Message = "Epic launch requested (Legendary not found — GUI backend).",
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
            ProcessHelper.TryCloseProcesses("EpicGamesLauncher");
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

    private static IEnumerable<GameEntry> ParseLegendaryInstalledJson(string json)
    {
        // Legendary list-installed --json is typically an array or object map.
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var g = MapLegendaryItem(el);
                if (g is not null) yield return g;
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var g = MapLegendaryItem(prop.Value, prop.Name);
                if (g is not null) yield return g;
            }
        }
    }

    private static GameEntry? MapLegendaryItem(JsonElement el, string? key = null)
    {
        try
        {
            var appName = el.TryGetProperty("app_name", out var a) ? a.GetString()
                : el.TryGetProperty("appName", out var a2) ? a2.GetString()
                : key;
            var title = el.TryGetProperty("title", out var t) ? t.GetString()
                : el.TryGetProperty("app_title", out var t2) ? t2.GetString()
                : appName;
            var installPath = el.TryGetProperty("install_path", out var p) ? p.GetString()
                : el.TryGetProperty("installPath", out var p2) ? p2.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(title))
                return null;

            long? size = null;
            if (el.TryGetProperty("install_size", out var s) && s.TryGetInt64(out var sv)) size = sv;

            return new GameEntry
            {
                Id = "epic:" + appName,
                Title = title!,
                Store = StoreKind.Epic,
                Installed = true,
                Owned = true,
                CanInstall = false,
                Path = installPath,
                LaunchTarget = appName,
                SizeBytes = size,
                Status = "Ready",
                Deps = new[] { "Legendary" },
                LaunchNote = "Launches via Legendary. Epic GUI is optional.",
            };
        }
        catch { return null; }
    }

    private static IEnumerable<GameEntry> ReadEpicManifests()
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
                if (string.IsNullOrWhiteSpace(name)) continue;
                long? size = null;
                if (root.TryGetProperty("InstallSize", out var sz) && sz.TryGetInt64(out var s)) size = s;

                entry = new GameEntry
                {
                    Id = "epic:" + (appName ?? name!.ToLowerInvariant()),
                    Title = name!,
                    Store = StoreKind.Epic,
                    Installed = !string.IsNullOrWhiteSpace(install) && Directory.Exists(install),
                    Owned = true,
                    CanInstall = false,
                    Path = install,
                    LaunchTarget = appName,
                    SizeBytes = size,
                    Status = "Ready",
                    Deps = ResolveLegendary() is not null
                        ? new[] { "Legendary (preferred)" }
                        : new[] { "Epic Games Launcher" },
                    LaunchNote = ResolveLegendary() is not null
                        ? "Launches via Legendary when available; Epic GUI stays optional."
                        : "Uses Epic launcher as backend. Prefer Legendary for a quieter path.",
                };
            }
            catch { /* skip */ }

            if (entry is not null) yield return entry;
        }
    }

    internal static string? ResolveLegendary()
    {
        var fromPath = CliRunner.ResolveOnPath("legendary.exe") ?? CliRunner.ResolveOnPath("legendary");
        if (fromPath is not null) return fromPath;

        return CliRunner.FirstExisting(
            Path.Combine(PathHelper.AppDataDir, "tools", "legendary.exe"),
            Path.Combine(PathHelper.AppDirectory, "tools", "legendary.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "legendary", "legendary.exe"));
    }

    private static string? ResolveEpicLauncher() =>
        CliRunner.FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Epic Games", "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe"));
}
