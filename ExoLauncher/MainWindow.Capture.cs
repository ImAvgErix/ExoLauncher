using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher;

/// <summary>
/// Capture: Exo Capture (<see cref="Services.CaptureAddon"/>) records instant replays,
/// recordings and snips in its own process; Exo keeps its settings, tells it which
/// game is running, lists the captures folder for the Captures sheet and the game
/// page, and passes trims, exports and edits to it. Captures stay on this PC.
/// Settings and the list of favorites live in <c>capture\settings.json</c>,
/// what each saved file belongs to in <c>capture\index.json</c>, thumbnails in
/// <c>capture\thumbs</c>, all under Exo's data folder.
/// </summary>
public sealed partial class MainWindow
{
    internal const string CaptureMediaHost = "captures.exo-launcher.local";
    internal const string CaptureThumbHost = "capture-thumbs.exo-launcher.local";

    private static string CaptureData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "capture");
    private static string CaptureThumbs => Path.Combine(CaptureData, "thumbs");
    private static string CaptureSettingsPath => Path.Combine(CaptureData, "settings.json");
    private static string CaptureIndexPath => Path.Combine(CaptureData, "index.json");
    private static string DefaultCaptureFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Exo Captures");

    private static readonly JsonSerializerOptions CaptureJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _captureGate = new();
    private Services.CaptureAddon? _captureAddon;
    private CaptureSettings? _captureSettings;
    private Dictionary<string, CaptureMeta>? _captureIndex;
    private JsonNode? _captureState;
    private string? _captureGameId;
    private CoreWebView2? _captureCore;
    private string? _captureMapped;
    // Set until an add-on is configured: the next one to say ready has no settings, hotkeys or game yet
    // (also the first, whether the boot prewarm or a page's capture.status started it).
    private int _captureReconfigure = 1;

    internal sealed class CaptureSettings
    {
        public int Quality { get; set; } = 50;
        public int ReplaySeconds { get; set; } = 60;
        /// <summary>60, or 120 (up to the screen's refresh rate).</summary>
        public int MaxFps { get; set; } = 60;
        /// <summary>games (while a game runs), always, or off.</summary>
        public string ReplayMode { get; set; } = "games";
        public bool Cursor { get; set; } = true;
        public string SaveReplayKeys { get; set; } = "Ctrl+Alt+F10";
        public string RecordKeys { get; set; } = "Ctrl+Alt+F9";
        public string SnipKeys { get; set; } = "Ctrl+Alt+F8";
        public bool PrintScreen { get; set; }
        /// <summary>Null: Videos\Exo Captures.</summary>
        public string? Folder { get; set; }
        /// <summary>0: no limit.</summary>
        public int KeepGb { get; set; }
        public int KeepDays { get; set; }
        /// <summary>Paths relative to the captures folder; never deleted by the limits.</summary>
        public List<string> Favourites { get; set; } = new();
    }

    internal sealed class CaptureMeta
    {
        public string? GameId { get; set; }
        public double DurationMs { get; set; }
    }

    private CaptureSettings CaptureConfig
    {
        get
        {
            lock (_captureGate)
            {
                if (_captureSettings is not null) return _captureSettings;
                try
                {
                    if (File.Exists(CaptureSettingsPath))
                        _captureSettings = JsonSerializer.Deserialize<CaptureSettings>(File.ReadAllText(CaptureSettingsPath), CaptureJson);
                }
                catch (Exception ex) { AppLog.Warn("Capture settings unreadable, using defaults: " + ex.Message); }
                return _captureSettings ??= new CaptureSettings();
            }
        }
    }

    private Dictionary<string, CaptureMeta> CaptureIndex
    {
        get
        {
            lock (_captureGate)
            {
                if (_captureIndex is not null) return _captureIndex;
                try
                {
                    if (File.Exists(CaptureIndexPath))
                        _captureIndex = JsonSerializer.Deserialize<Dictionary<string, CaptureMeta>>(File.ReadAllText(CaptureIndexPath), CaptureJson) is { } read
                            ? new Dictionary<string, CaptureMeta>(read, StringComparer.OrdinalIgnoreCase) : null;
                }
                catch (Exception ex) { AppLog.Warn("Capture index unreadable: " + ex.Message); }
                return _captureIndex ??= new Dictionary<string, CaptureMeta>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, CaptureJson));
        File.Move(temp, path, overwrite: true);
    }

    private void SaveCaptureSettings() { lock (_captureGate) WriteJsonAtomic(CaptureSettingsPath, CaptureConfig); }
    private void SaveCaptureIndex() { lock (_captureGate) WriteJsonAtomic(CaptureIndexPath, CaptureIndex); }

    /// <summary>The captures folder, full and without a trailing separator.</summary>
    private string CaptureFolder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(CaptureConfig.Folder ?? DefaultCaptureFolder));

    // ------------------------------------------------------------------ add-on

    private readonly Lock _captureSyncGate = new();

    private Services.CaptureAddon? CaptureAddon
    {
        get
        {
            if (_captureAddon is not null || !Services.CaptureAddon.Present) return _captureAddon;
            using var scope = _addonGate.EnterScope();
            if (_captureAddon is not null) return _captureAddon;
            var addon = new Services.CaptureAddon();
            addon.Event += OnCaptureEvent;
            // A game starting or closing, however it was started: the bridge's run-state poll
            // reports games started from a store, a shortcut or before Exo as well.
            App.Services.Launcher.GameRunStateChanged += (_, _) => Task.Run(SyncCaptureGame);
            return _captureAddon = addon;
        }
    }

    /// <summary>Exo Capture for Discord's Go Live (pictures of windows and screens, and the stream itself); null when it isn't installed.</summary>
    internal Services.CaptureAddon? StreamCapture => CaptureAddon;

    /// With the connected rooms: Exo Capture, so its hotkeys work from the start.
    private Task PrewarmCapture() => CaptureAddon is { } addon ? Task.Run(() =>
    {
        addon.Start();
        _ = Task.Run(EnforceCaptureLimits);
    }) : Task.CompletedTask;

    private async Task ConfigureCaptureAsync()
    {
        if (CaptureAddon is not { } addon) return;
        var s = CaptureConfig;
        var folder = CaptureFolder;
        try { Directory.CreateDirectory(folder); } catch (Exception ex) { AppLog.Warn("Captures folder: " + ex.Message); }
        var parameters = new JsonObject
        {
            ["quality"] = s.Quality,
            ["replaySeconds"] = s.ReplaySeconds,
            ["maxFps"] = s.MaxFps,
            ["replayMode"] = s.ReplayMode,
            ["cursor"] = s.Cursor,
            ["folder"] = folder,
            ["thumbs"] = CaptureThumbs,
            ["printScreen"] = s.PrintScreen,
            ["font"] = CaptureFont(),
            ["hotkeys"] = new JsonObject { ["saveReplay"] = s.SaveReplayKeys, ["record"] = s.RecordKeys, ["snip"] = s.SnipKeys },
        };
        try
        {
            var state = await addon.CallAsync("configure", parameters, CancellationToken.None).ConfigureAwait(false);
            OnCaptureEvent("state", state);
            SyncCaptureGame();
        }
        catch (Exception ex) { AppLog.Warn("Capture configure: " + ex.Message); }
    }

    /// <summary>
    /// Tells the add-on which installed game is running now, however it was started: the one
    /// it already follows while that still runs, else the first running one; none ends it.
    /// </summary>
    private void SyncCaptureGame()
    {
        if (_captureAddon is null) return;
        // Waits rather than skips: a start reported while a look runs must still be seen.
        using var _ = _captureSyncGate.EnterScope();
        try
        {
            var running = RunningLibraryGames();
            var current = _captureGameId;
            if (current is not null && running.ContainsKey(current)) return;
            if (running.Count > 0)
            {
                var (id, state) = running.First();
                OnCaptureRunState(id, state);
            }
            else if (current is not null)
            {
                OnCaptureRunState(current, default);
            }
        }
        catch (Exception ex) { AppLog.Debug("Capture: running game check failed: " + ex.GetType().Name); }
    }

    /// <summary>Installed library games running now, however they were started.</summary>
    internal static Dictionary<string, Services.GameRunState> RunningLibraryGames()
    {
        var installed = App.Services.Library.PeekCachedLibrary()
            .Where(g => g.Installed && !string.IsNullOrWhiteSpace(g.Path))
            .ToArray();
        return installed.Length == 0
            ? new Dictionary<string, Services.GameRunState>()
            : App.Services.Launcher.GetGameRunStates(installed, underRootOnly: true)
                .Where(row => row.Value.IsRunning)
                .ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Exo's own Inter, so the add-on's toast and windows read like Exo.</summary>
    private static string CaptureFont()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Inter-Variable.ttf");
        return File.Exists(path) ? path : "";
    }

    private void OnCaptureRunState(string gameId, Services.GameRunState state)
    {
        if (CaptureAddon is not { } addon) return;
        if (state.IsRunning)
        {
            var game = App.Services.Library.Find(gameId);
            if (game is null || string.IsNullOrWhiteSpace(game.Path)) { AppLog.Info("Capture: running game " + gameId + " has no install folder in the library."); return; }
            _captureGameId = gameId;
            AppLog.Info("Capture: following " + game.Title + ".");
            _ = addon.CallAsync("game", new JsonObject { ["id"] = gameId, ["title"] = game.Title, ["folder"] = game.Path }, CancellationToken.None)
                .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        else if (string.Equals(_captureGameId, gameId, StringComparison.OrdinalIgnoreCase))
        {
            _captureGameId = null;
            _ = addon.CallAsync("game", new JsonObject(), CancellationToken.None)
                .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private void OnCaptureEvent(string name, JsonNode? data)
    {
        switch (name)
        {
            case "ready":
            case "state":
                _captureState = data;
                // Recording or keeping a replay: its microphone track takes Exo's cleaned voice.
                if (CaptureAddon is { } voiceAddon)
                    Services.DiscordClient.Voice.RecordedVoice.Set(voiceAddon, data?["buffering"]?.GetValue<bool>() == true || data?["recording"]?.GetValue<bool>() == true);
                _bridge?.PostCaptureEvent("capture.state", CaptureStateDto());
                if (name == "ready" && Interlocked.Exchange(ref _captureReconfigure, 0) == 1) _ = ConfigureCaptureAsync();
                break;
            case "exited":
                _captureGameId = null;
                if (CaptureAddon is { } goneAddon) Services.DiscordClient.Voice.RecordedVoice.Set(goneAddon, false);
                Interlocked.Exchange(ref _captureReconfigure, 1);
                _captureState = null;
                _bridge?.PostCaptureEvent("capture.state", CaptureStateDto());
                break;
            case "failed":
                _captureState = null;
                _bridge?.PostCaptureEvent("capture.state", CaptureStateDto());
                break;
            case "saved":
                if (data?["path"]?.GetValue<string>() is not { } path) return;
                var relative = RelativeCapture(path);
                if (relative is null) return;
                lock (_captureGate)
                {
                    var game = data["game"]?.GetValue<string>();
                    // A trim or export belongs to the game of the file it came from.
                    CaptureIndex[relative] = new CaptureMeta
                    {
                        GameId = string.IsNullOrEmpty(game) ? GameOfFolder(relative) : game,
                        DurationMs = data["durationMs"]?.GetValue<double>() ?? 0,
                    };
                }
                try { SaveCaptureIndex(); } catch (Exception ex) { AppLog.Warn("Capture index: " + ex.Message); }
                _ = Task.Run(EnforceCaptureLimits);
                var kind = data["kind"]?.GetValue<string>();
                if (ItemFor(path) is { } item) _bridge?.PostCaptureEvent("capture.saved", new { item, kind });
                // The page opens a snip in its editor on capture.saved; Exo comes to the front for it.
                if (kind == "snip") DispatcherQueue.TryEnqueue(RestoreAndActivate);
                break;
        }
    }

    private object CaptureStateDto()
    {
        var state = _captureState;
        return new
        {
            present = Services.CaptureAddon.Present,
            running = state is not null,
            buffering = state?["buffering"]?.GetValue<bool>() ?? false,
            recording = state?["recording"]?.GetValue<bool>() ?? false,
            recordingMs = state?["recordingMs"]?.GetValue<double>() ?? 0,
            codec = state?["codec"]?.GetValue<string>(),
            encoder = state?["encoder"]?.GetValue<string>(),
            width = state?["width"]?.GetValue<double>(),
            height = state?["height"]?.GetValue<double>(),
            fps = state?["fps"]?.GetValue<double>(),
            hdr = state?["hdr"]?.GetValue<bool>() ?? false,
            error = state?["error"]?.GetValue<string>(),
            hotkeys = new
            {
                saveReplay = state?["hotkeys"]?["saveReplay"]?["ok"]?.GetValue<bool>() ?? true,
                record = state?["hotkeys"]?["record"]?["ok"]?.GetValue<bool>() ?? true,
                snip = state?["hotkeys"]?["snip"]?["ok"]?.GetValue<bool>() ?? true,
                printScreen = state?["printScreenOk"]?.GetValue<bool>() ?? true,
            },
        };
    }

    private object CaptureSettingsDto()
    {
        var s = CaptureConfig;
        return new
        {
            s.Quality,
            s.ReplaySeconds,
            s.MaxFps,
            s.ReplayMode,
            s.Cursor,
            s.SaveReplayKeys,
            s.RecordKeys,
            s.SnipKeys,
            s.PrintScreen,
            folder = CaptureFolder,
            s.KeepGb,
            s.KeepDays,
        };
    }

    // ------------------------------------------------------------------ bridge

    /// <summary>capture.status: whether the add-on is here, what it is doing, and the settings.</summary>
    internal object CaptureStatus()
    {
        if (CaptureAddon is { } addon) addon.Start();
        return new { ok = true, state = CaptureStateDto(), settings = CaptureSettingsDto() };
    }

    /// <summary>capture.settings: a patch of settings; saved, then passed to the add-on.</summary>
    internal async Task<object> CaptureSetSettingsAsync(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return new { ok = false, message = "Nothing to change." };
        var s = CaptureConfig;
        string? error = null;
        lock (_captureGate)
        {
            foreach (var prop in p.EnumerateObject())
            {
                var v = prop.Value;
                int? n = v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
                switch (prop.Name)
                {
                    case "quality" when n is { } quality: s.Quality = Math.Clamp(quality, 0, 100); break;
                    case "replaySeconds" when n is { } seconds: s.ReplaySeconds = Math.Clamp(seconds, 30, 300); break;
                    case "maxFps" when n is { } fps: s.MaxFps = fps >= 120 ? 120 : 60; break;
                    case "replayMode" when v.ValueKind == JsonValueKind.String && v.GetString() is "games" or "always" or "off": s.ReplayMode = v.GetString()!; break;
                    case "cursor" when v.ValueKind is JsonValueKind.True or JsonValueKind.False: s.Cursor = v.GetBoolean(); break;
                    case "printScreen" when v.ValueKind is JsonValueKind.True or JsonValueKind.False: s.PrintScreen = v.GetBoolean(); break;
                    case "saveReplayKeys" or "recordKeys" or "snipKeys" when v.ValueKind == JsonValueKind.String:
                        var keys = v.GetString()!.Trim();
                        if (CaptureHotkeyProblem(keys) is { } problem) { error = problem; break; }
                        // Exo's own other action would hold them, and the add-on would report "another app".
                        var holder = new[] { ("saveReplayKeys", "Save replay", s.SaveReplayKeys), ("recordKeys", "Record", s.RecordKeys), ("snipKeys", "Snip", s.SnipKeys) }
                            .FirstOrDefault(other => other.Item1 != prop.Name && string.Equals(other.Item3, keys, StringComparison.OrdinalIgnoreCase));
                        if (holder.Item2 is { } taken) { error = "Already used for " + taken + "."; break; }
                        if (prop.Name == "saveReplayKeys") s.SaveReplayKeys = keys;
                        else if (prop.Name == "recordKeys") s.RecordKeys = keys;
                        else s.SnipKeys = keys;
                        break;
                    case "keepGb" when n is { } gb: s.KeepGb = Math.Clamp(gb, 0, 10_000); break;
                    case "keepDays" when n is { } days: s.KeepDays = Math.Clamp(days, 0, 3650); break;
                    case "folder" when v.ValueKind == JsonValueKind.String:
                        var folder = v.GetString()!;
                        if (CaptureFolderProblem(folder) is { } folderProblem) error = folderProblem;
                        else s.Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                        break;
                    case "folder" when v.ValueKind == JsonValueKind.Null: s.Folder = null; break;
                }
            }
        }
        try { SaveCaptureSettings(); }
        catch (Exception ex) { AppLog.Warn("Capture settings: " + ex.Message); return new { ok = false, message = "Exo could not save the capture settings." }; }
        DispatcherQueue.TryEnqueue(MapCaptureHosts);
        await ConfigureCaptureAsync().ConfigureAwait(true);
        _ = Task.Run(EnforceCaptureLimits);
        return new { ok = error is null, message = error, state = CaptureStateDto(), settings = CaptureSettingsDto() };
    }

    /// <summary>Keys Exo will not take: ones ShadowPlay or Game Bar use, or a bare key that would stop typing it.</summary>
    private static string? CaptureHotkeyProblem(string keys)
    {
        if (keys.Length is 0 or > 40) return "Pick a key combination.";
        var parts = keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var key = parts.LastOrDefault() ?? "";
        bool ctrl = parts.Contains("Ctrl"), alt = parts.Contains("Alt"), shift = parts.Contains("Shift"), win = parts.Contains("Win");
        if (win) return "Windows keeps Win combinations for itself.";
        if (!ctrl && !alt && !(key.Length > 1 && key[0] == 'F')) return "Use Ctrl or Alt with a letter or number, or an F key.";
        // NVIDIA's overlay (ShadowPlay) and Steam's own keys.
        var nvidia = new[] { "Alt+F10", "Alt+F9", "Alt+F1", "Alt+Z", "Alt+Shift+F10", "Alt+F2", "Alt+F3" };
        var normalized = string.Join('+', new[] { ctrl ? "Ctrl" : null, alt ? "Alt" : null, shift ? "Shift" : null, key }.Where(x => x is not null));
        if (nvidia.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return normalized + " is NVIDIA's overlay key.";
        if (normalized is "F12") return "F12 is Steam's screenshot key.";
        return null;
    }

    private static string? CaptureFolderProblem(string folder)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!Path.IsPathFullyQualified(folder) || Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
                return "Pick a folder, not a whole drive.";
            var guarded = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            };
            if (guarded.Any(g => !string.IsNullOrEmpty(g) && (string.Equals(full, g, StringComparison.OrdinalIgnoreCase) ||
                g != Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) && full.StartsWith(g + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                return "Pick a folder of your own for captures.";
            Directory.CreateDirectory(full);
            return null;
        }
        catch { return "That folder can't be used."; }
    }

    /// <summary>capture.act: saveReplay, record, snip, openFolder.</summary>
    internal async Task<object> CaptureActAsync(string? action)
    {
        if (action == "openFolder")
        {
            try
            {
                Directory.CreateDirectory(CaptureFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + CaptureFolder + "\"") { UseShellExecute = true });
                return new { ok = true };
            }
            catch (Exception ex) { return CaptureFailure("Captures folder", ex); }
        }
        if (CaptureAddon is not { } addon) return new { ok = false, message = "Exo Capture is not installed." };
        var method = action switch { "saveReplay" => "replay.save", "record" => "record.toggle", "snip" => "snip", _ => null };
        if (method is null) return new { ok = false, message = "Unknown action." };
        try
        {
            // The snip overlay freezes the screen as it is: Exo's own menu must be gone first.
            if (method == "snip") { await Task.Delay(180).ConfigureAwait(true); addon.AllowForeground(); }
            var state = await addon.CallAsync(method, new JsonObject(), CancellationToken.None).ConfigureAwait(true);
            if (state is JsonObject) _captureState = state;
            return new { ok = true, state = CaptureStateDto() };
        }
        catch (Exception ex) { return CaptureFailure("Capture " + method, ex); }
    }

    /// <summary>
    /// The add-on's own failures (InvalidOperationException) are lines meant for the user; file and
    /// Windows errors carry paths, which stay in native code: those go to the log.
    /// </summary>
    private static object CaptureFailure(string what, Exception ex)
    {
        if (ex is InvalidOperationException) return new { ok = false, message = ex.Message };
        AppLog.Warn(what + ": " + ex.GetType().Name + ": " + ex.Message);
        return new { ok = false, message = "Exo could not finish that. The details are in the log." };
    }

    // ------------------------------------------------------------------ the captures folder

    private static readonly string[] CaptureExtensions = [".mp4", ".png"];

    /// <summary>FNV-1a of the full path's UTF-8: the item id and the thumbnail's name (the add-on works it out the same way).</summary>
    private static string CaptureId(string fullPath)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(fullPath)) { hash ^= b; hash *= 1099511628211UL; }
        return hash.ToString("x16");
    }

    private string? RelativeCapture(string path)
    {
        var full = Path.GetFullPath(path);
        var root = CaptureFolder + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : null;
    }

    private IEnumerable<string> CaptureFiles()
    {
        var root = CaptureFolder;
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> folders;
        try { folders = Directory.EnumerateDirectories(root).ToList(); } catch { yield break; }
        foreach (var folder in folders)
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(folder).Where(f => CaptureExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList(); }
            catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    private string? GameOfFolder(string relative)
    {
        var folder = relative.Split(Path.DirectorySeparatorChar)[0];
        foreach (var game in App.Services.Library.PeekCachedLibrary())
            if (string.Equals(SafeCaptureName(game.Title), folder, StringComparison.OrdinalIgnoreCase)) return game.Id;
        return null;
    }

    /// <summary>The folder name the add-on gives a game (its SafeName).</summary>
    private static string SafeCaptureName(string title)
    {
        var chars = title.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? ' ' : c).ToArray();
        var name = new string(chars).Trim().TrimEnd('.').Trim();
        if (name.Length > 80) name = name[..80].TrimEnd(' ', '.');
        return name.Length == 0 ? "Capture" : name;
    }

    internal sealed record CaptureItem(string Id, string Kind, string Game, string? GameId, string Name, string Created,
        long Size, double? DurationMs, bool Favourite, string Url, string? Thumb);

    private CaptureItem? ItemFor(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var relative = RelativeCapture(info.FullName);
            if (relative is null) return null;
            var id = CaptureId(info.FullName);
            CaptureMeta? meta;
            lock (_captureGate) CaptureIndex.TryGetValue(relative, out meta);
            var folder = relative.Split(Path.DirectorySeparatorChar)[0];
            var image = info.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
            var url = "https://" + CaptureMediaHost + "/" + string.Join('/', relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
            var thumb = File.Exists(Path.Combine(CaptureThumbs, id + ".jpg"))
                ? "https://" + CaptureThumbHost + "/" + id + ".jpg?v=" + info.LastWriteTimeUtc.Ticks.ToString("x")
                : image ? url : null;
            bool favourite;
            lock (_captureGate) favourite = CaptureConfig.Favourites.Contains(relative, StringComparer.OrdinalIgnoreCase);
            return new CaptureItem(id, image ? "image" : "video", folder, meta?.GameId ?? GameOfFolder(relative),
                Path.GetFileNameWithoutExtension(info.Name), info.CreationTimeUtc.ToString("o"), info.Length,
                meta?.DurationMs > 0 ? meta.DurationMs : null, favourite, url + "?v=" + info.LastWriteTimeUtc.Ticks.ToString("x"), thumb);
        }
        catch { return null; }
    }

    private string? PathOfCapture(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 16) return null;
        return CaptureFiles().FirstOrDefault(f => CaptureId(Path.GetFullPath(f)) == id);
    }

    /// <summary>capture.list: every capture, newest first; with a game id, only that game's.</summary>
    internal object CaptureList(string? gameId, int? limit)
    {
        // Lazy past the sort: a game page's strip of six builds six items, not one per file.
        var items = CaptureFiles()
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => ItemFor(f.FullName))
            .Where(i => i is not null);
        if (!string.IsNullOrEmpty(gameId))
        {
            var title = App.Services.Library.Find(gameId)?.Title;
            var folder = title is null ? null : SafeCaptureName(title);
            items = items.Where(i => string.Equals(i!.GameId, gameId, StringComparison.OrdinalIgnoreCase) ||
                folder is not null && string.Equals(i.Game, folder, StringComparison.OrdinalIgnoreCase));
        }
        if (limit is > 0) items = items.Take(limit.Value);
        return new { ok = true, items = items.ToList() };
    }

    /// <summary>capture.item: favourite, unfavourite, delete (to the Recycle Bin), reveal, copy, discord.</summary>
    internal async Task<object> CaptureItemAsync(string? id, string? action)
    {
        var path = PathOfCapture(id);
        if (path is null) return new { ok = false, message = "That capture is gone." };
        var relative = RelativeCapture(path)!;
        switch (action)
        {
            case "favourite":
            case "unfavourite":
                lock (_captureGate)
                {
                    CaptureConfig.Favourites.RemoveAll(f => string.Equals(f, relative, StringComparison.OrdinalIgnoreCase));
                    if (action == "favourite") CaptureConfig.Favourites.Add(relative);
                }
                SaveCaptureSettings();
                return new { ok = true, item = ItemFor(path) };
            case "delete":
                if (!RecycleFile(path)) return new { ok = false, message = "Windows could not move it to the Recycle Bin." };
                ForgetCapture(path, relative);
                return new { ok = true };
            case "reveal":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                return new { ok = true };
            case "copy":
            case "discord":
                if (CaptureAddon is not { } addon) return new { ok = false, message = "Exo Capture is not installed." };
                var image = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                try
                {
                    // A snip copies as a picture; a video (and anything for Discord) as a file to paste.
                    await addon.CallAsync(image && action == "copy" ? "copyImage" : "copy", new JsonObject { ["path"] = path }, CancellationToken.None).ConfigureAwait(true);
                    return new { ok = true };
                }
                catch (Exception ex) { return CaptureFailure("Capture copy", ex); }
        }
        return new { ok = false, message = "Unknown action." };
    }

    /// <summary>capture.edit: trim (lossless) or an export preset (discord, youtube, vertical) between two points.</summary>
    internal async Task<object> CaptureEditAsync(string? id, string? preset, double? startMs, double? endMs)
    {
        if (CaptureAddon is not { } addon) return new { ok = false, message = "Exo Capture is not installed." };
        var path = PathOfCapture(id);
        if (path is null || !path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return new { ok = false, message = "That clip is gone." };
        var parameters = new JsonObject { ["path"] = path, ["startMs"] = Math.Max(0, startMs ?? 0), ["endMs"] = Math.Max(0, endMs ?? 0) };
        try
        {
            JsonNode? result;
            if (preset is null or "trim") result = await addon.CallAsync("trim", parameters, CancellationToken.None).ConfigureAwait(true);
            else if (preset is "discord" or "youtube" or "vertical")
            {
                parameters["preset"] = preset;
                result = await addon.CallAsync("export", parameters, CancellationToken.None).ConfigureAwait(true);
            }
            else return new { ok = false, message = "Unknown export." };
            var made = result?["path"]?.GetValue<string>();
            return new { ok = true, item = made is null ? null : ItemFor(made) };
        }
        catch (Exception ex) { return CaptureFailure("Capture edit", ex); }
    }

    /// <summary>capture.saveImage: the snip editor's result over the snip (PNG, at most 64 MB), then copied.</summary>
    internal async Task<object> CaptureSaveImageAsync(string? id, string? base64)
    {
        var path = PathOfCapture(id);
        if (path is null || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return new { ok = false, message = "That snip is gone." };
        if (string.IsNullOrEmpty(base64) || base64.Length > 90_000_000) return new { ok = false, message = "Nothing to save." };
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); } catch { return new { ok = false, message = "Nothing to save." }; }
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != (byte)'P') return new { ok = false, message = "Nothing to save." };
        try
        {
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(true);
            File.Move(temp, path, overwrite: true);
            try { File.Delete(Path.Combine(CaptureThumbs, CaptureId(path) + ".jpg")); } catch { }
            if (CaptureAddon is { } addon)
                await addon.CallAsync("copyImage", new JsonObject { ["path"] = path }, CancellationToken.None).ConfigureAwait(true);
            return new { ok = true, item = ItemFor(path) };
        }
        catch (Exception ex) { return CaptureFailure("Snip save", ex); }
    }

    private void ForgetCapture(string path, string relative)
    {
        lock (_captureGate)
        {
            CaptureIndex.Remove(relative);
            CaptureConfig.Favourites.RemoveAll(f => string.Equals(f, relative, StringComparison.OrdinalIgnoreCase));
        }
        try { File.Delete(Path.Combine(CaptureThumbs, CaptureId(path) + ".jpg")); } catch { }
        try { SaveCaptureIndex(); SaveCaptureSettings(); } catch { }
    }

    /// <summary>
    /// Settings > Capture's storage limit: past N days, then the oldest until under N GB, to the Recycle Bin.
    /// Only captures Exo Capture saved (in the index) count or go: the folder may be Videos or OneDrive,
    /// full of the user's own media. Favorites are never removed.
    /// </summary>
    private void EnforceCaptureLimits()
    {
        var s = CaptureConfig;
        if (s.KeepDays <= 0 && s.KeepGb <= 0) return;
        try
        {
            var files = new List<(string Relative, DateTime CreatedUtc, long Length)>();
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in CaptureFiles())
            {
                var info = new FileInfo(path);
                if (!info.Exists || RelativeCapture(info.FullName) is not { } relative) continue;
                files.Add((relative, info.CreationTimeUtc, info.Length));
                paths[relative] = info.FullName;
            }
            HashSet<string> indexed, favourites;
            lock (_captureGate)
            {
                indexed = new HashSet<string>(CaptureIndex.Keys, StringComparer.OrdinalIgnoreCase);
                favourites = new HashSet<string>(s.Favourites, StringComparer.OrdinalIgnoreCase);
            }
            int removed = 0;
            foreach (var relative in CapturesPastLimit(files, indexed, favourites, s.KeepDays, s.KeepGb, DateTime.UtcNow))
            {
                if (!RecycleFile(paths[relative])) { AppLog.Debug("Capture limit: could not recycle a capture."); continue; }
                ForgetCapture(paths[relative], relative);
                removed++;
            }
            if (removed > 0) AppLog.Info($"Capture storage limit moved {removed} old capture(s) to the Recycle Bin.");
        }
        catch (Exception ex) { AppLog.Warn("Capture storage limit: " + ex.Message); }
    }

    /// <summary>The captures the limit removes, oldest first. Only indexed ones count toward the size or go.</summary>
    internal static List<string> CapturesPastLimit(IEnumerable<(string Relative, DateTime CreatedUtc, long Length)> files,
        IReadOnlySet<string> indexed, IReadOnlySet<string> favourites, int keepDays, int keepGb, DateTime nowUtc)
    {
        var ours = files.Where(f => indexed.Contains(f.Relative)).OrderBy(f => f.CreatedUtc).ToList();
        long total = ours.Sum(f => f.Length);
        var cutoff = nowUtc.AddDays(-keepDays);
        long limit = (long)keepGb * 1024 * 1024 * 1024;
        var remove = new List<string>();
        foreach (var f in ours)
        {
            if (favourites.Contains(f.Relative)) continue;
            bool old = keepDays > 0 && f.CreatedUtc < cutoff;
            bool over = keepGb > 0 && total > limit;
            if (!old && !over) continue;
            remove.Add(f.Relative);
            total -= f.Length;
        }
        return remove;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public int AnyAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOp op);

    private static bool RecycleFile(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_NOERRORUI = 0x400, FOF_SILENT = 0x4;
        var op = new ShFileOp { Func = FO_DELETE, From = path + "\0\0", Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT };
        return SHFileOperationW(ref op) == 0 && op.AnyAborted == 0 && !File.Exists(path);
    }

    // ------------------------------------------------------------------ the shell page

    /// <summary>
    /// The captures folder and the thumbnails, on their own hosts for the shell page
    /// (img and media in its CSP). CORS is allowed so the snip editor can read a snip
    /// into its canvas. Called when the shell's WebView starts and when the folder changes.
    /// </summary>
    internal void MapCaptureHosts(CoreWebView2 core)
    {
        _captureCore = core;
        MapCaptureHosts();
    }

    private void MapCaptureHosts()
    {
        if (_captureCore is not { } core) return;
        try
        {
            var folder = CaptureFolder;
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(CaptureThumbs);
            if (_captureMapped == folder) return;
            if (_captureMapped is not null) core.ClearVirtualHostNameToFolderMapping(CaptureMediaHost);
            else core.SetVirtualHostNameToFolderMapping(CaptureThumbHost, CaptureThumbs, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping(CaptureMediaHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            _captureMapped = folder;
        }
        catch (Exception ex) { AppLog.Warn("Captures folder could not be mapped: " + ex.Message); }
    }
}
