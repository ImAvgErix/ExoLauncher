using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// The game sheet's Shaders line, read from the Exo Shaders engine's records
/// (its data folder keeps SCSKiller's layout): the game list (games.json,
/// written when the engine scans) and each game's record (games\&lt;id&gt;\state.json, written when a
/// compile ends), and says when either changed.
/// </summary>
internal sealed partial class ScsKillerStatus : IDisposable
{
    /// <param name="EngineId">The engine's own id for the game (it can differ from Exo's), for game.compile.</param>
    internal sealed record Status(string State, string? Reason, DateTimeOffset? WarmedAt, string? Driver, string? EngineId = null);

    /// <param name="Recorder">the recorder is in its folder: a session's recording waits to be imported when it exits</param>
    /// <param name="Kept">an AMD cache kept aside (games.json's Kept): it goes back into the driver's cache as the game starts</param>
    private sealed record Listed(string Id, string Name, string InstallDir, string Status, string Reason, DateTimeOffset? WarmedAt, string? Driver,
        string Exe = "", bool Recorder = false, bool Kept = false);

    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private (DateTime Written, IReadOnlyList<Listed> Games)? _list;

    /// <summary>The app's list or a game's record changed (debounced): game sheets read their line again.</summary>
    internal event Action? Changed;

    /// <summary>The app's data folder, its AppStore.DefaultDir.</summary>
    internal static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller");

    internal void Start()
    {
        RemoveOldCopy();
        try
        {
            Directory.CreateDirectory(DataDir);
            var watcher = new FileSystemWatcher(DataDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            watcher.Filters.Add("games.json");
            watcher.Filters.Add("state.json");
            watcher.Changed += (_, _) => Poke();
            watcher.Created += (_, _) => Poke();
            watcher.Renamed += (_, _) => Poke();
            watcher.EnableRaisingEvents = true;
            lock (_gate) _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Info("SCSKiller: cannot watch the app's data folder: " + ex.Message);
        }
    }

    private void Poke()
    {
        lock (_gate)
        {
            _debounce ??= new Timer(_ => Changed?.Invoke());
            _debounce.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// "noApp" (Exo Shaders is not installed), "warmed", "stale", "ready", "record",
    /// or "unsupported" (also a game the engine does not list; its next scan, which the watcher sees, adds a new install).
    /// </summary>
    internal Status For(GameEntry game)
    {
        // The shader add-on runs the same engine on the same records.
        if (!ShadersAddon.Present) return new Status("noApp", null, null, null);
        var match = Match(game);
        if (match is null) return new Status("unsupported", "Exo Shaders does not list this game.", null, null);
        match = FromRecord(match);
        return match.Status switch
        {
            "Warmed" => new Status("warmed", match.Driver is { } d ? $"Compiled for driver {d}." : null, match.WarmedAt, match.Driver, match.Id),
            "Stale" => new Status("stale", DriverChanged().IsMatch(match.Reason) ? "Driver changed" : "Game updated", match.WarmedAt, match.Driver, match.Id),
            "Ready" => new Status("ready", Sentence(match.Reason), null, null, match.Id),
            "NeedsRecording" => new Status("record", Sentence(match.Reason), null, null, match.Id),
            _ => new Status("unsupported", Sentence(match.Reason), null, null),
        };
    }

    /// <summary>The engine's entry for an Exo game: the same id, else the same name, in the same install.</summary>
    private Listed? Match(GameEntry game)
    {
        var list = ReadList();
        return list.FirstOrDefault(g => g.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase) && SameInstall(g.InstallDir, game.Path))
               ?? list.FirstOrDefault(g => NameKey(g.Name) == NameKey(game.Title) && SameInstall(g.InstallDir, game.Path));
    }

    /// <summary>An AMD cache kept aside: the engine puts it back into the driver's cache when it sees the game start.</summary>
    internal bool KeptAside(GameEntry game) => Match(game)?.Kept == true;

    /// <summary>
    /// The engine has work once this game exits: import the recording its recorder made, keep what it compiled aside
    /// (AMD), or move a compile's pipelines into the game's cache, which waits for the game to exit or the engine to start.
    /// </summary>
    internal bool WorkAfterExit(GameEntry game) =>
        Match(game) is { Status: not "Unsupported" } m && (m.Recorder || m.Kept || m.Reason.StartsWith(PipelinesWaitReason, StringComparison.Ordinal));

    /// <summary>The engine's reason while a compile's pipelines wait for the game (ScsKiller.D3DSCachePendingReason).</summary>
    private const string PipelinesWaitReason = "the compiled pipelines go into the game's cache";

    /// <summary>
    /// Each listed game's exe as it is on disk (size and write time), as one short hash; null without a list. A store's
    /// update changes it (and an uninstall), so the engine checks its games again (anti-cheat, new shaders) without
    /// running to watch for it.
    /// </summary>
    internal string? ExeStamp()
    {
        var list = ReadList();
        if (list.Count == 0) return null;
        var text = new System.Text.StringBuilder();
        foreach (var g in list.OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            text.Append(g.Id).Append('|');
            try { if (g.Exe.Length > 0 && new FileInfo(g.Exe) is { Exists: true } f) text.Append(f.Length).Append(':').Append(f.LastWriteTimeUtc.Ticks); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            text.Append('\n');
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    private IReadOnlyList<Listed> ReadList()
    {
        var path = Path.Combine(DataDir, "games.json");
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            lock (_gate) if (_list is { } cached && cached.Written == written) return cached.Games;
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var games = new List<Listed>();
            if (doc.RootElement.TryGetProperty("Games", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var state in array.EnumerateArray())
                {
                    if (!state.TryGetProperty("Game", out var game) || Text(game, "Id") is not { } id) continue;
                    games.Add(new Listed(id, Text(game, "Name") ?? id, Text(game, "InstallDir") ?? "",
                        Text(state, "Status") ?? "Unsupported", Text(state, "StatusReason") ?? "",
                        Date(Text(state, "WarmedAt")), Text(state, "WarmedDriverVersion"), Text(game, "ExePath") ?? "",
                        state.TryGetProperty("RecorderInstalled", out var recorder) && recorder.ValueKind == JsonValueKind.True,
                        state.TryGetProperty("Kept", out var kept) && kept.ValueKind == JsonValueKind.Object));
                }
            lock (_gate) _list = (written, games);
            return games;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A compile after the list was written updated the game's record: when it
    /// was for a graphics driver installed now, the game reads as compiled.
    /// </summary>
    private static Listed FromRecord(Listed listed)
    {
        try
        {
            var path = Path.Combine(DataDir, "games", listed.Id.Replace(':', '_'), "state.json");
            if (!File.Exists(path)) return listed;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var record = doc.RootElement;
            if (Date(Text(record, "WarmedAt")) is not { } warmed) return listed;
            if (listed.Status == "Warmed" && listed.WarmedAt is { } at && at >= warmed) return listed;
            // DriverId is vendor:device:subsystem:driver version, the registry's DriverVersion.
            if (Text(record, "WarmedDriverId")?.Split(':')[^1] is not { Length: > 0 } driver ||
                !GpuCapability.DriverFingerprint().Split('|').Contains(driver, StringComparer.OrdinalIgnoreCase)) return listed;
            return listed with { Status = "Warmed", Reason = "", WarmedAt = warmed, Driver = Text(record, "WarmedDriverVersion") ?? listed.Driver };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return listed;
        }
    }

    // ------------------------------------------------------------------ compile time

    /// <summary>
    /// Seconds each listed game takes to compile on this PC, by the engine's id: the engine's estimate
    /// (reading and preparing its files, then its plan at the rate its own cold compiles measured, else a
    /// stand-in). A game without one yet (no plan) is sized from its shader count at the median seconds
    /// per shader of the games that have both, else takes the median game. The last compile's own time
    /// isn't read: a rewarm onto a cache that still held the game took seconds. System > Shaders fills
    /// these in where the engine's snapshot has no estimate, as rough ones.
    /// </summary>
    internal IReadOnlyDictionary<string, double> CompileSeconds()
    {
        var path = Path.Combine(DataDir, "games.json");
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, double>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var games = new List<(string, TimeSpan?, long?)>();
            if (doc.RootElement.TryGetProperty("Games", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var state in array.EnumerateArray())
                    if (state.TryGetProperty("Game", out var game) && Text(game, "Id") is { } id)
                        games.Add((id, Span(Text(state, "EstimatedWarmTime")),
                            state.TryGetProperty("ShaderCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var n) ? n : null));
            return CompileSeconds(games);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, double>();
        }
    }

    internal static Dictionary<string, double> CompileSeconds(IEnumerable<(string Id, TimeSpan? Estimated, long? Shaders)> games)
    {
        var list = games.Select(g => (g.Id, Known: g.Estimated is { TotalSeconds: > 0 } est ? est.TotalSeconds : (double?)null,
            Shaders: g.Shaders is > 0 ? g.Shaders : null)).ToList();
        var perShader = Median(list.Where(g => g.Known is not null && g.Shaders is not null).Select(g => g.Known!.Value / g.Shaders!.Value));
        var typical = Median(list.Where(g => g.Known is not null).Select(g => g.Known!.Value));
        var seconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, known, shaders) in list)
            if ((known ?? (shaders is { } s && perShader is { } rate ? s * rate : typical)) is { } value) seconds[id] = Math.Round(value);
        return seconds;
    }

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return null;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static TimeSpan? Span(string? text) =>
        text is not null && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span) ? span : null;

    // ------------------------------------------------------------------ the old built-in copy

    /// <summary>
    /// Earlier Exo versions downloaded their own copy of SCSKiller into
    /// tools\scskiller. Any recorder that copy put in a game folder is taken out
    /// with its own command line, then the folder goes. Once; never throws.
    /// </summary>
    private static void RemoveOldCopy()
    {
        var root = Path.Combine(PathHelper.AppDataDir, "tools", "scskiller");
        if (!Directory.Exists(root)) return;
        _ = Task.Run(() =>
        {
            try
            {
                var cli = Directory.EnumerateFiles(root, "scskiller.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f)) == "cli");
                foreach (var id in OldRecorders(Path.Combine(root, "data", "games.json")))
                {
                    if (cli is null) { AppLog.Info($"SCSKiller: the old copy's recorder in {id} stays; its command line is gone"); continue; }
                    using var process = Process.Start(new ProcessStartInfo(cli) { ArgumentList = { "record", "uninstall", id }, UseShellExecute = false, CreateNoWindow = true });
                    if (process is not null && !process.WaitForExit(60_000)) ProcessTermination.Kill(process, entireProcessTree: true);
                    AppLog.Info($"SCSKiller: the old copy's recorder taken out of {id} (exit {process?.ExitCode})");
                }
                if (RecursiveDeleteGuard.TryValidateManagedChild(Path.Combine(PathHelper.AppDataDir, "tools"), root, out var safe, out _))
                {
                    Directory.Delete(safe, recursive: true);
                    AppLog.Info("SCSKiller: Exo's old built-in copy removed; the app does everything now");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                AppLog.Info("SCSKiller: the old built-in copy could not be removed yet: " + ex.Message);
            }
        });
    }

    private static IEnumerable<string> OldRecorders(string gamesJson)
    {
        try
        {
            if (!File.Exists(gamesJson)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(gamesJson));
            if (!doc.RootElement.TryGetProperty("Games", out var games) || games.ValueKind != JsonValueKind.Array) return [];
            return games.EnumerateArray()
                .Where(g => g.TryGetProperty("RecorderInstalled", out var r) && r.ValueKind == JsonValueKind.True)
                .Select(g => g.TryGetProperty("Game", out var game) ? Text(game, "Id") : null)
                .OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    // ------------------------------------------------------------------ helpers

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    private static string? Sentence(string reason)
    {
        var text = reason.Trim();
        if (text.Length == 0) return null;
        text = char.ToUpperInvariant(text[0]) + text[1..];
        return text.EndsWith('.') ? text : text + ".";
    }

    /// <summary>The app's install folder and Exo's are the same, or one holds the other.</summary>
    private static bool SameInstall(string scsKiller, string? exo)
    {
        if (string.IsNullOrWhiteSpace(exo) || string.IsNullOrWhiteSpace(scsKiller)) return false;
        try
        {
            var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scsKiller));
            var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(File.Exists(exo) ? Path.GetDirectoryName(exo)! : exo));
            return a.Equals(b, StringComparison.OrdinalIgnoreCase) || PathHelper.IsUnderRoot(b, a) || PathHelper.IsUnderRoot(a, b);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>"STAR WARS Jedi: Fallen Order™ " and "Star Wars Jedi: Fallen Order" are one key.</summary>
    private static string NameKey(string name) =>
        NameNoise().Replace(TradeMarks().Replace(name, "").Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant(), " ").Trim();

    public void Dispose()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    [GeneratedRegex(@"^driver changed", RegexOptions.IgnoreCase)]
    private static partial Regex DriverChanged();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NameNoise();

    [GeneratedRegex("[™®©]")]
    private static partial Regex TradeMarks();
}
