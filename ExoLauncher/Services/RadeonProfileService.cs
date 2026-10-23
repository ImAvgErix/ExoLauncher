using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;
using Keys = ExoLauncher.Helpers.AmdAdlx.Keys;

namespace ExoLauncher.Services;

/// <summary>
/// System > Graphics > Driver and profile on a Radeon: Exo's profile (<see cref="GpuProfile.Radeon"/>)
/// written through ADLX's official Set calls, with the same backup and Undo as the NVIDIA profile.
/// Use saves every value it replaces to amd-profile.json first (a re-apply keeps the first backup),
/// then writes; ADLX applies each Set at once, so a write that fails part way keeps the backup and
/// Undo puts back what changed. Two settings follow games Exo launches (amd-games.json): Anti-Lag in
/// its "games" choice, and Fluid Motion Frames for each game the player turned it on for. Exo turns
/// them on before the game starts and back to what they were once no game runs, and the values to
/// put back are saved before the switch, so a crash puts them back at the next start.
/// </summary>
internal sealed class RadeonProfileService(IGpuDriver driver, string folder, Func<bool> isAmd, Func<bool> anyGameRunning, Func<bool> amdSoftwareRunning)
{
    /// <summary>
    /// The profile for this PC. Null from Read, Apply and Undo means ADLX did not answer, and the page
    /// falls back to what to set in AMD Software.
    /// </summary>
    internal sealed record State(bool Ok, string? Message, string? Gpu, string Status, string AntiLag, bool CanUndo, bool DriverUpdated,
        bool FreeSync, string Summary);

    private sealed record Backup(string? Software, string AntiLag, Dictionary<string, int?> Before);

    private sealed class Games
    {
        public List<string> Afmf { get; set; } = [];
        /// <summary>What a launch switched, and the value it had before.</summary>
        public Dictionary<string, int> Restore { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static RadeonProfileService Shared { get; } = new(AmdAdlx.Driver, PathHelper.AppDataDir,
        () => GraphicsService.Vendor() == "amd", () => App.Services.Launcher.AnyGameRunning, AmdSoftwareRunning);

    private readonly Lock _gate = new();
    private Timer? _restoreTick;
    private bool? _afmfSupported;

    private string BackupPath => Path.Combine(folder, "amd-profile.json");
    private string GamesPath => Path.Combine(folder, "amd-games.json");

    /// <summary>AMD Software (Adrenalin) runs: Fluid Motion Frames works only then, as Adrenalin signals the game.</summary>
    internal static bool AmdSoftwareRunning()
    {
        var found = Process.GetProcessesByName("RadeonSoftware");
        foreach (var process in found) process.Dispose();
        return found.Length > 0;
    }

    /// <summary>Follows game launches (<see cref="LaunchOrchestrator.GameLaunching"/>) and ends; puts back what a launch before a crash switched.</summary>
    internal void Start(LaunchOrchestrator launcher, Func<GpuProfile.Screen> mainScreen)
    {
        launcher.GameLaunching += game => GameLaunching(game.Id, game.Title);
        launcher.GameSessionCompleted += _ => GameEnded();
        _ = Task.Run(() =>
        {
            try
            {
                GameEnded();
                // AMD Software updated the driver and moved some of Exo's settings: write them again.
                if (isAmd() && Read(mainScreen()) is { Status: "changed", DriverUpdated: true } state)
                    AppLog.Info($"Radeon profile after a driver update: {Apply(mainScreen(), state.AntiLag)?.Status ?? "no answer"}.");
            }
            catch (Exception ex) { AppLog.Warn("Radeon profile start failed: " + ex.Message); }
        });
    }

    internal State? Read(GpuProfile.Screen screen)
    {
        lock (_gate)
        {
            if (driver.Read() is not { } state) return null;
            return Describe(state, screen, LoadBackup(), null);
        }
    }

    /// <summary>Applies the profile with the Anti-Lag choice, backing up what it replaces first.</summary>
    internal State? Apply(GpuProfile.Screen screen, string antiLag)
    {
        if (!GpuProfile.IsAntiLagChoice(antiLag)) antiLag = GpuProfile.AntiLagGames;
        lock (_gate)
        {
            if (driver.Read() is not { } state) return null;
            var writes = GpuProfile.Radeon(state, screen.Name, screen.RefreshHz, antiLag);
            if (writes.Count == 0) return Describe(state, screen, LoadBackup(), "AMD's driver offers none of the settings Exo sets.");
            // A launch's switch is not what Undo should put back: the value from before it is.
            var games = LoadGames();
            var now = state.Values.ToDictionary(item => item.Key, item => (int?)(games.Restore.TryGetValue(item.Key, out var before) ? before : item.Value));
            var earlier = LoadBackup();
            var backup = new Backup(state.Software, antiLag, GpuProfile.MergeBackup(earlier?.Before, now, writes.Select(write => write.Key)));
            if (!SaveBackup(backup)) return Describe(state, screen, earlier, "Exo could not save a backup, so it changed nothing.");
            // The profile's value wins over a launch's pending put-back.
            if (writes.Any(write => games.Restore.Remove(write.Key))) SaveGames(games);
            var failed = driver.Write(writes);
            AppLog.Info($"Radeon profile applied: settings={writes.Count}; antiLag={antiLag}; refresh={screen.RefreshHz}; {failed ?? "all held"}.");
            return driver.Read() is { } after ? Describe(after, screen, backup, failed) : null;
        }
    }

    /// <summary>Puts back every value the profile replaced and forgets the backup.</summary>
    internal State? Undo(GpuProfile.Screen screen)
    {
        lock (_gate)
        {
            if (driver.Read() is not { } state) return null;
            if (LoadBackup() is not { } backup) return Describe(state, screen, null, "Nothing to undo.");
            var writes = backup.Before.Where(item => item.Value is not null && state.Values.ContainsKey(item.Key))
                .Select(item => new KeyValuePair<string, int>(item.Key, item.Value!.Value)).ToList();
            var games = LoadGames();
            if (writes.Any(write => games.Restore.Remove(write.Key))) SaveGames(games);
            if (driver.Write(writes) is { } failed) return Describe(state, screen, backup, failed);
            DeleteBackup();
            AppLog.Info($"Radeon profile undone: settings={writes.Count}.");
            return driver.Read() is { } after ? Describe(after, screen, null, null) : null;
        }
    }

    /// <summary>The frame rate target follows the main screen: when only it differs from the applied profile, it is written again.</summary>
    internal void FollowRefresh(GpuProfile.Screen screen)
    {
        lock (_gate)
        {
            if (LoadBackup() is not { } backup || driver.Read() is not { } state) return;
            var stale = GpuProfile.Radeon(state, screen.Name, screen.RefreshHz, backup.AntiLag)
                .Where(write => state.Values.GetValueOrDefault(write.Key, -1) != write.Value).ToList();
            if (stale.Count == 0 || stale.Any(write => write.Key is not (Keys.FrtcFps or Keys.Frtc))) return;
            AppLog.Info($"Radeon frame rate target follows the screen: refresh={screen.RefreshHz}; {driver.Write(stale) ?? "held"}.");
        }
    }

    /// <summary>Game sheet: whether this PC can run Fluid Motion Frames, whether it is on for the game, and whether AMD Software runs.</summary>
    internal (bool Supported, bool On, bool AmdSoftware) Afmf(string gameId)
    {
        lock (_gate)
        {
            // Asked once per run: the GPU and its driver don't change under a running Exo (a failed read asks again).
            if (_afmfSupported is null)
            {
                if (!isAmd()) _afmfSupported = false;
                else if (driver.Read() is { } state) _afmfSupported = state.Values.ContainsKey(Keys.Afmf);
            }
            if (_afmfSupported != true) return (false, false, false);
            return (true, LoadGames().Afmf.Contains(gameId, StringComparer.OrdinalIgnoreCase), amdSoftwareRunning());
        }
    }

    /// <summary>Turns Fluid Motion Frames on or off for one game, from its next launch; false when it could not be saved.</summary>
    internal bool SetAfmf(string gameId, bool on)
    {
        lock (_gate)
        {
            var games = LoadGames();
            games.Afmf.RemoveAll(id => string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase));
            if (on) games.Afmf.Add(gameId);
            return SaveGames(games);
        }
    }

    /// <summary>Before a game Exo launches starts: Anti-Lag on in the "games" choice, Fluid Motion Frames on if the game has it.</summary>
    internal void GameLaunching(string gameId, string title)
    {
        lock (_gate)
        {
            var games = LoadGames();
            var wanted = new List<string>();
            if (LoadBackup() is { AntiLag: GpuProfile.AntiLagGames }) wanted.Add(Keys.AntiLag);
            if (games.Afmf.Contains(gameId, StringComparer.OrdinalIgnoreCase)) wanted.Add(Keys.Afmf);
            if (wanted.Count == 0 || !isAmd()) return;
            if (driver.Read() is not { } state)
            {
                AppLog.Warn($"Radeon launch settings for {title}: AMD's driver did not answer, so nothing was switched.");
                return;
            }
            var writes = new List<KeyValuePair<string, int>>();
            foreach (var key in wanted)
            {
                if (!state.Values.TryGetValue(key, out var now))
                {
                    AppLog.Info($"Radeon launch settings for {title}: this GPU or driver has no {AmdAdlx.Label(key)}.");
                    continue;
                }
                if (now == 1) continue;
                games.Restore.TryAdd(key, now);
                writes.Add(new(key, 1));
            }
            if (writes.Count == 0) return;
            // Saved before the switch: if Exo stops before the game ends, the next start puts it back.
            if (!SaveGames(games)) return;
            var failed = driver.Write(writes);
            AppLog.Info($"Radeon launch settings for {title}: {string.Join(", ", writes.Select(write => AmdAdlx.Label(write.Key)))} on; {failed ?? "held"}.");
            if (wanted.Contains(Keys.Afmf) && !amdSoftwareRunning())
                AppLog.Info($"Fluid Motion Frames for {title}: AMD Software isn't running, so the driver won't add frames until it is.");
            _restoreTick ??= new Timer(_ => GameEnded(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>Once no game runs: every setting a launch switched goes back to what it was.</summary>
    internal void GameEnded()
    {
        lock (_gate)
        {
            if (anyGameRunning()) return;
            var games = LoadGames();
            if (games.Restore.Count == 0)
            {
                StopTick();
                return;
            }
            if (!isAmd()) return;
            var writes = games.Restore.Select(item => new KeyValuePair<string, int>(item.Key, item.Value)).ToList();
            var failed = driver.Write(writes);
            AppLog.Info($"Radeon launch settings put back: {string.Join(", ", writes.Select(write => $"{AmdAdlx.Label(write.Key)} {(write.Value != 0 ? "on" : "off")}"))}; {failed ?? "held"}.");
            if (failed is not null) return;
            games.Restore.Clear();
            SaveGames(games);
            StopTick();
        }
    }

    private void StopTick()
    {
        _restoreTick?.Dispose();
        _restoreTick = null;
    }

    private State Describe(GpuDriverState state, GpuProfile.Screen screen, Backup? backup, string? message)
    {
        var antiLag = backup?.AntiLag ?? GpuProfile.AntiLagGames;
        var writes = GpuProfile.Radeon(state, screen.Name, screen.RefreshHz, antiLag);
        // A setting a running game switched compares as the value it goes back to.
        var restore = LoadGames().Restore;
        var held = state.Values.ToDictionary(item => item.Key, item => restore.TryGetValue(item.Key, out var before) ? before : item.Value);
        var status = writes.Count == 0 ? "none" : GpuProfile.Status(writes.ToDictionary(), held, backup is not null);
        var driverUpdated = status == "changed" && backup?.Software != state.Software;
        return new State(message is null, message, Short(state.Gpu), status, antiLag, backup is not null, driverUpdated,
            GpuProfile.MainFreeSync(state.Values, screen.Name) is not null, GpuProfile.RadeonSummary(writes, antiLag));
    }

    private static string? Short(string? gpu) =>
        gpu?.Replace("AMD ", "", StringComparison.OrdinalIgnoreCase).Replace("Radeon(TM) ", "Radeon ", StringComparison.OrdinalIgnoreCase).Trim();

    // amd-profile.json: {"software":"26.10.1","antiLag":"games","before":{"chill":1,"freeSync:LG ULTRAGEAR":0}}
    private Backup? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(BackupPath));
            var root = json.RootElement;
            var software = root.TryGetProperty("software", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
            var antiLag = root.TryGetProperty("antiLag", out var choice) && GpuProfile.IsAntiLagChoice(choice.GetString()) ? choice.GetString()! : GpuProfile.AntiLagGames;
            var before = new Dictionary<string, int?>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("before").EnumerateObject())
                before[item.Name] = item.Value.ValueKind == JsonValueKind.Number ? item.Value.GetInt32() : null;
            return new Backup(software, antiLag, before);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            AppLog.Warn("Radeon profile backup unreadable: " + ex.GetType().Name);
            return null;
        }
    }

    private bool SaveBackup(Backup backup) => Save(BackupPath, new Dictionary<string, object?>
    {
        ["software"] = backup.Software,
        ["antiLag"] = backup.AntiLag,
        ["before"] = backup.Before.OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(),
    });

    private void DeleteBackup()
    {
        try { File.Delete(BackupPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Radeon profile backup not removed: " + ex.GetType().Name); }
    }

    // amd-games.json: {"afmf":["steam:1091500"],"restore":{"antiLag":0}}
    private Games LoadGames()
    {
        try
        {
            return File.Exists(GamesPath) ? JsonSerializer.Deserialize<Games>(File.ReadAllText(GamesPath), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Radeon game settings unreadable: " + ex.GetType().Name);
            return new();
        }
    }

    private bool SaveGames(Games games) => Save(GamesPath, games);

    private bool Save(string path, object body)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(body, Json));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Radeon settings not saved ({Path.GetFileName(path)}): {ex.GetType().Name}");
            return false;
        }
    }
}
