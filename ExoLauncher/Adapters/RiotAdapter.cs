using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// Riot Client path. Starts RiotClientServices minimized, launches product,
/// optionally force-closes Riot UI after exit. Vanguard is required for VALORANT
/// and cannot be replaced — that is an honest hard limit, not a bug.
/// </summary>
public sealed class RiotAdapter : IStoreAdapter
{
    public StoreKind Store => StoreKind.Riot;
    public string DisplayName => "Riot";

    public bool IsAgentPresent() => ResolveRiotClientServices() is not null;

    public Task<IReadOnlyList<GameEntry>> DiscoverAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry>();
        var root = ResolveRiotRoot();
        if (root is null)
            return Task.FromResult<IReadOnlyList<GameEntry>>(games);

        // Known Riot product folders under the install root.
        var products = new (string Folder, string Title, string ProductId)[]
        {
            ("VALORANT", "VALORANT", "valorant"),
            ("League of Legends", "League of Legends", "league_of_legends"),
            ("Legends of Runeterra", "Legends of Runeterra", "bacon"),
            ("Teamfight Tactics", "Teamfight Tactics", "tft"),
        };

        foreach (var (folder, title, productId) in products)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(root, folder);
            // Also check ProgramData metadata / common install locations.
            var alt = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Riot Games", folder);
            var installedPath = Directory.Exists(path) ? path
                : Directory.Exists(alt) ? alt
                : null;

            if (installedPath is null && productId != "valorant") continue;
            // Surface VALORANT even when only Riot Client exists (common).
            if (installedPath is null && ResolveRiotClientServices() is null) continue;

            games.Add(new GameEntry
            {
                Id = "riot:" + productId,
                Title = title,
                Store = StoreKind.Riot,
                Installed = installedPath is not null,
                Path = installedPath,
                LaunchTarget = productId,
                Status = installedPath is not null ? "Ready" : "Client present",
                Deps = title == "VALORANT"
                    ? new[] { "Riot Client", "Vanguard" }
                    : new[] { "Riot Client" },
                LaunchNote = title == "VALORANT"
                    ? "Riot Client starts minimized. Vanguard must stay installed and running — no full replace."
                    : "RiotClientServices minimized → product. Optional close of Riot UI after exit.",
            });
        }

        // If client exists but no product folder found, still list a generic entry.
        if (games.Count == 0 && ResolveRiotClientServices() is not null)
        {
            games.Add(new GameEntry
            {
                Id = "riot:client",
                Title = "Riot Client",
                Store = StoreKind.Riot,
                Installed = true,
                Path = root,
                LaunchTarget = null,
                Status = "Client only",
                Deps = new[] { "Riot Client" },
                LaunchNote = "Opens Riot Client minimized. Product detection needs a installed title folder.",
            });
        }

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        var rcs = ResolveRiotClientServices();
        if (rcs is null)
            return new LaunchResult { Ok = false, Message = "RiotClientServices.exe not found." };

        try
        {
            var args = string.IsNullOrWhiteSpace(game.LaunchTarget)
                ? string.Empty
                : $"--launch-product={game.LaunchTarget} --launch-patchline=live";

            var p = ProcessHelper.StartMinimized(rcs, args);
            if (p is null)
                return new LaunchResult { Ok = false, Message = "Riot Client did not start." };

            await Task.Delay(800, ct).ConfigureAwait(false);
            if (options.MinimizeStoreUi)
                ProcessHelper.MinimizeProcessWindows(p.Id);

            return new LaunchResult
            {
                Ok = true,
                Message = "Riot Client launch started.",
                ProcessId = p.Id,
                BackendStarted = "riot",
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message };
        }
    }

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        // Close Riot UI chrome only — never touch vgk / vgc (Vanguard).
        if (options.CloseStoreUiAfterExit)
            ProcessHelper.TryCloseProcesses("Riot Client", "RiotClientServices", "RiotClientUx");
        return Task.CompletedTask;
    }

    private static string? ResolveRiotRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Riot Game valorant.live");
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

        var fallbacks = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Riot Games", "Riot Client", "RiotClientServices.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Riot Games", "Riot Client", "RiotClientServices.exe"),
        };
        return fallbacks.FirstOrDefault(File.Exists);
    }
}
