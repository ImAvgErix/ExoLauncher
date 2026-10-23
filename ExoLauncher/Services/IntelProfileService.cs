using System.Text.Json;
using ExoLauncher.Helpers;
using Keys = ExoLauncher.Helpers.IntelIgcl.Keys;

namespace ExoLauncher.Services;

/// <summary>
/// System > Graphics > Driver and profile on an Intel GPU: Exo's profile (<see cref="GpuProfile.IntelWrites"/>) written
/// through IGCL, with the same backup and Undo as the NVIDIA and Radeon profiles. Use saves every value it replaces to
/// intel-profile.json first (a re-apply keeps the first backup), then writes; a setting the driver refuses counts as one
/// it doesn't have and drops out of the profile. XeSS frame generation is the player's, per game (intel-games.json):
/// Exo knows a game by its library entry, not by its executable's name, so it sets the driver's global override just
/// before a game it launches starts and puts back what was there once no game runs. The value to put back is saved
/// before the switch, so a crash puts it back at the next start.
/// </summary>
internal sealed class IntelProfileService(IGpuDriver driver, string folder, Func<bool> isIntel, Func<bool> anyGameRunning)
{
    /// <summary>The profile for this PC. Null from Read, Apply and Undo means IGCL did not answer, and the page falls back to what to set in Intel Graphics Software.</summary>
    internal sealed record State(bool Ok, string? Message, string? Gpu, string Status, string LowLatency, bool CanUndo, bool DriverUpdated, string Summary);

    private sealed record Backup(string? Driver, string LowLatency, Dictionary<string, int?> Before);

    private sealed class Games
    {
        /// <summary>Each game's frame generation override (1 2x, 2 3x, 3 4x); a game Exo leaves to itself is not listed.</summary>
        public Dictionary<string, int> FrameGen { get; set; } = [];
        /// <summary>What a launch switched, and the value it had before.</summary>
        public Dictionary<string, int> Restore { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static IntelProfileService Shared { get; } = new(IntelIgcl.Driver, PathHelper.AppDataDir,
        () => GraphicsService.Vendor() == "intel", () => App.Services.Launcher.AnyGameRunning);

    private readonly Lock _gate = new();
    private Timer? _restoreTick;
    private bool? _frameGenSupported;

    private string BackupPath => Path.Combine(folder, "intel-profile.json");
    private string GamesPath => Path.Combine(folder, "intel-games.json");

    /// <summary>Follows game launches and ends; puts back what a launch before a crash switched, and re-applies after a driver update moved the profile.</summary>
    internal void Start(LaunchOrchestrator launcher, Func<GpuProfile.Screen> mainScreen)
    {
        launcher.GameLaunching += game => GameLaunching(game.Id, game.Title);
        launcher.GameSessionCompleted += _ => GameEnded();
        _ = Task.Run(() =>
        {
            try
            {
                GameEnded();
                if (isIntel() && Read(mainScreen()) is { Status: "changed", DriverUpdated: true } state)
                    AppLog.Info($"Intel profile after a driver update: {Apply(mainScreen(), state.LowLatency)?.Status ?? "no answer"}.");
            }
            catch (Exception ex) { AppLog.Warn("Intel profile start failed: " + ex.Message); }
        });
    }

    internal State? Read(GpuProfile.Screen screen)
    {
        lock (_gate)
            return driver.Read() is { } state ? Describe(state, screen, LoadBackup(), null) : null;
    }

    /// <summary>Applies the profile with the Low latency choice, backing up what it replaces first.</summary>
    internal State? Apply(GpuProfile.Screen screen, string lowLatency)
    {
        if (!GpuProfile.IsLowLatencyChoice(lowLatency)) lowLatency = GpuProfile.LowLatencyOn;
        lock (_gate)
        {
            if (driver.Read() is not { } state) return null;
            var writes = GpuProfile.IntelWrites(state, screen.Vrr, screen.RefreshHz, lowLatency);
            if (writes.Count == 0) return Describe(state, screen, LoadBackup(), "Intel's driver offers none of the settings Exo sets.");
            // A launch's switch is not what Undo should put back: the value from before it is.
            var games = LoadGames();
            var now = state.Values.ToDictionary(item => item.Key, item => (int?)(games.Restore.TryGetValue(item.Key, out var before) ? before : item.Value));
            var earlier = LoadBackup();
            var backup = new Backup(state.Software, lowLatency, GpuProfile.MergeBackup(earlier?.Before, now, writes.Select(write => write.Key)));
            if (!SaveBackup(backup)) return Describe(state, screen, earlier, "Exo could not save a backup, so it changed nothing.");
            if (writes.Any(write => games.Restore.Remove(write.Key))) SaveGames(games);
            var failed = driver.Write(writes);
            AppLog.Info($"Intel profile applied: settings={writes.Count}; lowLatency={lowLatency}; refresh={screen.RefreshHz}; vrr={screen.Vrr}; {failed ?? "all held"}.");
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
            AppLog.Info($"Intel profile undone: settings={writes.Count}.");
            return driver.Read() is { } after ? Describe(after, screen, null, null) : null;
        }
    }

    /// <summary>The frame limit follows the main screen: when only it differs from the applied profile, it is written again.</summary>
    internal void FollowRefresh(GpuProfile.Screen screen)
    {
        lock (_gate)
        {
            if (LoadBackup() is not { } backup || driver.Read() is not { } state) return;
            var stale = GpuProfile.IntelWrites(state, screen.Vrr, screen.RefreshHz, backup.LowLatency)
                .Where(write => state.Values.GetValueOrDefault(write.Key, -1) != write.Value).ToList();
            if (stale.Count == 0 || stale.Any(write => write.Key != Keys.FrameLimit)) return;
            AppLog.Info($"Intel frame limit follows the screen: refresh={screen.RefreshHz}; {driver.Write(stale) ?? "held"}.");
        }
    }

    /// <summary>Game sheet: whether this PC's driver has XeSS frame generation's override, and the game's choice ("game", "2x", "3x", "4x").</summary>
    internal (bool Supported, string Choice) FrameGen(string gameId)
    {
        lock (_gate)
        {
            // Asked once per run: the GPU and its driver don't change under a running Exo (a failed read asks again).
            if (_frameGenSupported is null)
            {
                if (!isIntel()) _frameGenSupported = false;
                else if (driver.Read() is { } state) _frameGenSupported = state.Values.ContainsKey(Keys.FrameGen);
            }
            return _frameGenSupported == true ? (true, GpuProfile.FrameGenChoice(LoadGames().FrameGen.GetValueOrDefault(gameId))) : (false, GpuProfile.FrameGenChoices[0]);
        }
    }

    /// <summary>Sets a game's XeSS frame generation from its next launch; false for an unknown choice or when it could not be saved.</summary>
    internal bool SetFrameGen(string gameId, string choice)
    {
        if (GpuProfile.FrameGenValue(choice) is not { } value) return false;
        lock (_gate)
        {
            var games = LoadGames();
            if (value == 0) games.FrameGen.Remove(gameId);
            else games.FrameGen[gameId] = value;
            return SaveGames(games);
        }
    }

    /// <summary>Before a game Exo launches starts: its frame generation override, when the player set one.</summary>
    internal void GameLaunching(string gameId, string title)
    {
        lock (_gate)
        {
            var games = LoadGames();
            if (!games.FrameGen.TryGetValue(gameId, out var wanted) || wanted <= 0 || !isIntel()) return;
            if (driver.Read() is not { } state)
            {
                AppLog.Warn($"Intel launch settings for {title}: Intel's driver did not answer, so nothing was switched.");
                return;
            }
            if (!state.Values.TryGetValue(Keys.FrameGen, out var now))
            {
                AppLog.Info($"Intel launch settings for {title}: this GPU or driver has no {IntelIgcl.Label(Keys.FrameGen)}.");
                return;
            }
            if (now == wanted) return;
            games.Restore.TryAdd(Keys.FrameGen, now);
            // Saved before the switch: if Exo stops before the game ends, the next start puts it back.
            if (!SaveGames(games)) return;
            var failed = driver.Write([new(Keys.FrameGen, wanted)]);
            AppLog.Info($"Intel launch settings for {title}: XeSS frame generation {GpuProfile.FrameGenChoice(wanted)}; {failed ?? "held"}.");
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
            if (!isIntel()) return;
            var writes = games.Restore.Select(item => new KeyValuePair<string, int>(item.Key, item.Value)).ToList();
            var failed = driver.Write(writes);
            // Only frame generation is switched at launch.
            AppLog.Info($"Intel launch settings put back: {string.Join(", ", writes.Select(write => $"{IntelIgcl.Label(write.Key)} {GpuProfile.FrameGenChoice(write.Value)}"))}; {failed ?? "held"}.");
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
        var lowLatency = backup?.LowLatency ?? GpuProfile.LowLatencyOn;
        var writes = GpuProfile.IntelWrites(state, screen.Vrr, screen.RefreshHz, lowLatency);
        // A setting a running game switched compares as the value it goes back to.
        var restore = LoadGames().Restore;
        var held = state.Values.ToDictionary(item => item.Key, item => restore.TryGetValue(item.Key, out var before) ? before : item.Value);
        var status = writes.Count == 0 ? "none" : GpuProfile.Status(writes.ToDictionary(), held, backup is not null);
        var driverUpdated = status == "changed" && backup?.Driver != state.Software;
        return new State(message is null, message, state.Gpu, status, lowLatency, backup is not null, driverUpdated, GpuProfile.IntelSummary(writes, lowLatency));
    }

    // intel-profile.json: {"driver":"32.0.101.8864","lowLatency":"on","before":{"endurance":2,"frameLimit":0}}
    private Backup? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(BackupPath));
            var root = json.RootElement;
            var version = root.TryGetProperty("driver", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            var lowLatency = root.TryGetProperty("lowLatency", out var choice) && GpuProfile.IsLowLatencyChoice(choice.GetString()) ? choice.GetString()! : GpuProfile.LowLatencyOn;
            var before = new Dictionary<string, int?>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("before").EnumerateObject())
                before[item.Name] = item.Value.ValueKind == JsonValueKind.Number ? item.Value.GetInt32() : null;
            return new Backup(version, lowLatency, before);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            AppLog.Warn("Intel profile backup unreadable: " + ex.GetType().Name);
            return null;
        }
    }

    private bool SaveBackup(Backup backup) => Save(BackupPath, new Dictionary<string, object?>
    {
        ["driver"] = backup.Driver,
        ["lowLatency"] = backup.LowLatency,
        ["before"] = backup.Before.OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(),
    });

    private void DeleteBackup()
    {
        try { File.Delete(BackupPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Intel profile backup not removed: " + ex.GetType().Name); }
    }

    // intel-games.json: {"frameGen":{"steam:2215430":2},"restore":{"frameGen":0}}
    private Games LoadGames()
    {
        try
        {
            return File.Exists(GamesPath) ? JsonSerializer.Deserialize<Games>(File.ReadAllText(GamesPath), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Intel game settings unreadable: " + ex.GetType().Name);
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
            AppLog.Warn($"Intel settings not saved ({Path.GetFileName(path)}): {ex.GetType().Name}");
            return false;
        }
    }
}
