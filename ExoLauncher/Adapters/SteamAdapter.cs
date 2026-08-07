using System.Text.RegularExpressions;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam library via protocol + silent client. Steam may remain installed —
/// Exo is the UI; steam.exe is the invisible backend for DRM titles.
/// </summary>
public sealed class SteamAdapter : IStoreAdapter
{
    public StoreKind Store => StoreKind.Steam;
    public string DisplayName => "Steam";

    public bool IsAgentPresent() => ResolveSteamExe() is not null;

    public Task<IReadOnlyList<GameEntry>> DiscoverAsync(CancellationToken ct = default)
    {
        var games = new List<GameEntry>();
        var steamRoot = ResolveSteamRoot();
        if (steamRoot is null)
            return Task.FromResult<IReadOnlyList<GameEntry>>(games);

        var libraryFolders = CollectLibraryFolders(steamRoot);
        foreach (var lib in libraryFolders)
        {
            ct.ThrowIfCancellationRequested();
            var steamApps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var acf in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(acf);
                    var appId = MatchField(text, "appid");
                    var name = MatchField(text, "name");
                    var installDir = MatchField(text, "installdir");
                    var sizeOnDisk = MatchField(text, "SizeOnDisk");
                    if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name))
                        continue;

                    var path = string.IsNullOrWhiteSpace(installDir)
                        ? null
                        : Path.Combine(steamApps, "common", installDir);

                    long? size = null;
                    if (long.TryParse(sizeOnDisk, out var s)) size = s;

                    games.Add(new GameEntry
                    {
                        Id = "steam:" + appId,
                        Title = name,
                        Store = StoreKind.Steam,
                        Installed = path is not null && Directory.Exists(path),
                        Path = path,
                        LaunchTarget = appId,
                        SizeBytes = size,
                        Status = path is not null && Directory.Exists(path) ? "Ready" : "Not installed",
                        Deps = new[] { "Steam client" },
                        LaunchNote = "Uses steam://run. Steam stays as the DRM backend; UI can be minimized.",
                    });
                }
                catch { /* skip corrupt manifests */ }
            }
        }

        return Task.FromResult<IReadOnlyList<GameEntry>>(games);
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
            // -silent: no main window flash when possible
            var p = ProcessHelper.StartMinimized(steamExe, "-silent");
            backend = "steam";
            if (p is not null)
            {
                // Brief settle so protocol handoff has a running client.
                await Task.Delay(1500, ct).ConfigureAwait(false);
                if (options.MinimizeStoreUi)
                    ProcessHelper.MinimizeProcessWindows(p.Id);
            }
        }

        try
        {
            ProcessHelper.StartProtocol($"steam://rungameid/{appId}");
            return new LaunchResult
            {
                Ok = true,
                Message = "Steam launch requested.",
                BackendStarted = backend,
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = ex.Message, BackendStarted = backend };
        }
    }

    public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default)
    {
        // Do not force-kill Steam by default — downloads and friends still use it.
        // Soft-close only the main window if the user opted in.
        if (options.CloseStoreUiAfterExit)
            ProcessHelper.TryCloseProcesses("steamwebhelper");
        return Task.CompletedTask;
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

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
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
            foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
            {
                var p = m.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase))
                    list.Add(p);
            }
        }
        catch { }

        return list;
    }

    private static string? MatchField(string acf, string field)
    {
        var m = Regex.Match(acf, $"\"{Regex.Escape(field)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
