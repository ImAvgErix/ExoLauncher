using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The shader add-on for System > Shaders (Exo Shaders, GPL-3.0-or-later, source
/// at github.com/UhhErix/exo-shaders): its own program, shipped in Exo's app
/// folder under <c>addons\shaders</c> or installed beside Exo's data under
/// <c>%LOCALAPPDATA%\ExoLauncher\addons\shaders</c>, with an
/// engine (<c>engine\ExoShaders.Service.exe</c>) and a page (<c>ui\index.html</c>).
/// Exo runs no part of it in-process. It starts the engine hidden at below-normal
/// priority, speaks newline-delimited JSON over its stdin and stdout, and passes
/// requests and events between the engine and Exo's Shaders page. Without the add-on, Shaders says it is not installed.
/// The engine starts only when you press Compile or Refresh on the page (<see cref="StartMethods"/>): never for the page
/// opening, a driver or game update, a new install, a game exiting, the PC idling or the time of day. It exits by itself
/// <see cref="IdleExitSeconds"/> after its last scan or compile ends, and leaves its last state for the page.
/// </summary>
internal sealed class ShadersAddon : IDisposable
{
    /// <summary>
    /// What the add-on's page (ShadersBoard) may ask its engine: read the board, say it is on screen, look for installed
    /// games again (Refresh), and queue, pause, resume, stop or compile games it lists. The engine can do far more (add a
    /// program or folder by path, install its recorder, delete caches, change its settings, stop itself): those are Exo's to
    /// ask, never the page's. ShadersPageMethodsTests checks every method the page calls is here.
    /// </summary>
    internal static readonly IReadOnlySet<string> PageMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "snapshot", "ui.visible", "scan", "queue.add", "queue.move", "queue.remove", "queue.start", "queue.pause", "queue.resume",
        "queue.stop", "game.compile",
    };

    /// <summary>
    /// The page's methods that start a stopped engine: Refresh (scan) and Compile (game.compile; queue.add then queue.start;
    /// queue.resume). Every other one is answered without it: the board from the last state it left, the rest as done.
    /// </summary>
    internal static readonly IReadOnlySet<string> StartMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "scan", "game.compile", "queue.add", "queue.start", "queue.resume",
    };

    internal const string VirtualHost = "shaders.exo-launcher.local";

    /// <summary>An add-on installed beside Exo's data (a newer or development build) wins over the copy that ships in Exo's app folder.</summary>
    internal static string Root => File.Exists(Path.Combine(LocalRoot, "engine", "ExoShaders.Service.exe")) ? LocalRoot : BundledRoot;

    private static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "addons", "shaders");

    /// <summary>The release installer carries Exo Shaders under <c>addons\shaders</c> in the app folder.</summary>
    private static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "addons", "shaders");
    internal static string EnginePath => Path.Combine(Root, "engine", "ExoShaders.Service.exe");
    internal static string PageFolder => Path.Combine(Root, "ui");

    /// <summary>Both halves are on this PC.</summary>
    internal static bool Present => File.Exists(EnginePath) && File.Exists(Path.Combine(PageFolder, "index.html"));

    /// <summary>The engine exits with this when another engine (SCSKiller's own app) holds the shader data.</summary>
    private const int OtherInstanceExit = 3;

    /// <summary>The engine exits by itself once nothing has needed it for this long (its exitWhenIdle, at least 5).</summary>
    internal const int IdleExitSeconds = 15;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private Process? _engine;
    private long _nextId;
    private bool _disposed;

    /// <summary>notRunning, starting, running, blocked (SCSKiller's app is open), failed.</summary>
    internal string State { get; private set; } = "notRunning";
    internal string? Message { get; private set; }

    /// <summary>The engine runs now.</summary>
    internal bool Running { get { lock (_gate) return _engine is { HasExited: false }; } }

    /// <summary>An engine event line (<c>{"event":..., "data":...}</c>), or a state change of Exo's own (<c>addon</c>).</summary>
    internal event Action<string, JsonNode?>? Event;

    /// <summary>Starts the engine if it isn't running. Safe to call often. <paramref name="reason"/> goes to the log.</summary>
    private void Start(string reason)
    {
        lock (_gate)
        {
            if (_disposed || !Present || _engine is { HasExited: false }) return;
            SetState("starting", null);
            try
            {
                var info = new ProcessStartInfo(EnginePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(EnginePath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardInputEncoding = new UTF8Encoding(false),
                };
                var engine = Process.Start(info) ?? throw new InvalidOperationException("The shader engine did not start.");
                try { engine.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                var reading = Task.Run(() => ReadLoop(engine));
                engine.EnableRaisingEvents = true;
                engine.Exited += (_, _) => OnExited(engine, reading);
                engine.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppLog.Debug("Shaders engine: " + Trim(e.Data, 300)); };
                engine.BeginErrorReadLine();
                _engine = engine;
                AppLog.Info("Shaders add-on engine started for " + reason + ": pid " + engine.Id + ".");
                SetState("running", null);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Shaders add-on engine failed to start: " + ex.Message);
                SetState("failed", "The shader engine could not start.");
            }
        }
    }

    /// <summary>
    /// One request to the engine; the reply's <c>result</c>, or an exception with its <c>error</c>. Only a running engine is
    /// asked, unless <paramref name="startFor"/> (what the user pressed, for the log) starts a stopped one.
    /// </summary>
    internal async Task<JsonNode?> CallAsync(string method, JsonNode? parameters, CancellationToken ct, string? startFor = null)
    {
        Process? engine;
        lock (_gate) engine = _engine is { HasExited: false } live ? live : null;
        if (engine is null)
        {
            if (startFor is null) throw new InvalidOperationException("The shader engine is not running.");
            Start(startFor);
            lock (_gate) engine = _engine is { HasExited: false } live ? live : null;
            if (engine is null) throw new InvalidOperationException(Message ?? "The shader engine is not running.");
        }
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            var line = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters?.DeepClone() ?? new JsonObject() }.ToJsonString();
            lock (_gate)
            {
                engine.StandardInput.WriteLine(line);
                engine.StandardInput.Flush();
            }
            using var reg = ct.Register(() => reply.TrySetCanceled(ct));
            return await reply.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoop(Process engine)
    {
        try
        {
            while (await engine.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); } catch { AppLog.Debug("Shaders engine wrote a line that isn't JSON."); continue; }
                if (node is not JsonObject message) continue;
                if (message["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id))
                {
                    if (!_pending.TryGetValue(id, out var reply)) continue;
                    if (message["error"] is { } error) reply.TrySetException(new InvalidOperationException(Trim(error.ToString(), 400)));
                    else reply.TrySetResult(message["result"]?.DeepClone());
                }
                else if (message["event"]?.GetValue<string>() is { } name)
                    Event?.Invoke(name, message["data"]?.DeepClone());
            }
        }
        catch (Exception ex) { AppLog.Debug("Shaders engine read ended: " + ex.Message); }
    }

    private void OnExited(Process engine, Task reading)
    {
        int code;
        try { code = engine.ExitCode; } catch { code = -1; }
        // Its last lines (replies, its exiting event) are read before the requests still open are failed.
        try { reading.Wait(TimeSpan.FromSeconds(2)); } catch { }
        lock (_gate) if (ReferenceEquals(_engine, engine)) _engine = null;
        // The next engine knows none of Exo's games.
        Volatile.Write(ref _syncedGames, null);
        var blocked = code == OtherInstanceExit ? "SCSKiller is open. Quit it from its tray icon, then try again." : null;
        foreach (var pending in _pending.Values)
            pending.TrySetException(new InvalidOperationException(blocked ?? "The shader engine stopped."));
        if (_disposed) return;
        AppLog.Info("Shaders add-on engine exited: code " + code + ".");
        if (blocked is not null) SetState("blocked", blocked);
        else SetState(code == 0 ? "notRunning" : "failed", code == 0 ? null : "The shader engine stopped.");
    }

    private void SetState(string state, string? message)
    {
        State = state;
        Message = message;
        Event?.Invoke("addon", new JsonObject { ["state"] = state, ["message"] = message });
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

    private static string StoreName(Models.StoreKind store) => store switch
    {
        Models.StoreKind.Epic => "Epic Games",
        Models.StoreKind.Gog => "GOG",
        Models.StoreKind.Xbox => "Xbox",
        Models.StoreKind.Ea => "EA app",
        Models.StoreKind.Ubisoft => "Ubisoft Connect",
        Models.StoreKind.BattleNet => "Battle.net",
        Models.StoreKind.Amazon => "Amazon Games",
        Models.StoreKind.Rockstar => "Rockstar Games",
        Models.StoreKind.Itch => "itch.io",
        Models.StoreKind.Local => "Added in Exo",
        _ => store.ToString(),
    };

    private int _syncPending;
    private int _syncForced;
    private string? _syncedGames;

    /// <summary>
    /// Exo's installed games (every installed store copy), so a running engine can compile
    /// games its own launcher readers miss. Coalesced: library updates come in bursts.
    /// Most library updates (cover warms, size measurements) leave the installed
    /// list as it was; that list is not sent again unless the engine just started
    /// (<paramref name="force"/>, its "ready" event). A stopped engine is never started for
    /// it: the next one is sent the list when ready. An engine without exo.library answers
    /// with an error, which is ignored.
    /// </summary>
    internal void SyncLibrary(Func<IReadOnlyList<Models.GameEntry>> library, bool force = false)
    {
        if (force) Interlocked.Exchange(ref _syncForced, 1);
        if (_disposed || !Running || Interlocked.Exchange(ref _syncPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Interlocked.Exchange(ref _syncPending, 0);
            var forced = Interlocked.Exchange(ref _syncForced, 0) == 1;
            try
            {
                var games = new JsonArray();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in library())
                {
                    var copies = g.Variants.Count > 0
                        ? g.Variants.Select(v => (v.Id, v.Store, v.Installed, v.Path, v.LaunchTarget))
                        : [(g.Id, g.Store, g.Installed, g.Path, g.LaunchTarget)];
                    foreach (var (id, store, installed, path, target) in copies)
                    {
                        if (!installed || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || !seen.Add(path)) continue;
                        var exe = target is { } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(t) ? t : null;
                        games.Add(new JsonObject { ["id"] = id, ["title"] = g.Title, ["store"] = StoreName(store), ["installDir"] = path, ["exe"] = exe });
                    }
                }
                var payload = games.ToJsonString();
                if (!Running || !forced && payload == Volatile.Read(ref _syncedGames)) return;
                try { await CallAsync("exo.library", new JsonObject { ["games"] = games }, CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) when (Running) { }   // an engine without exo.library: it has the list all the same
                Volatile.Write(ref _syncedGames, payload);
            }
            catch (Exception ex)
            {
                // The engine stopped before it took the list: the next one is sent it when ready.
                AppLog.Debug("Shaders add-on library sync: " + ex.Message);
            }
        });
    }

    private volatile bool _uiVisible;

    /// <summary>Whether the add-on's page is on screen, as the page last told the engine.</summary>
    internal bool UiVisible => _uiVisible;

    /// <summary>The page opened or closed.</summary>
    internal void SetUiVisible(bool visible) => _uiVisible = visible;

    /// <summary>The engine's last state, which it leaves when it exits (<c>exo-snapshot.json</c>, rev 0, no queue): the
    /// page shows it until the running engine's own states replace it. Null when there is none.</summary>
    internal static JsonNode? AtRest()
    {
        try
        {
            var path = Path.Combine(ScsKillerStatus.DataDir, "exo-snapshot.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Asks the engine to stop its queue and exit, as SCSKiller's own app does on quit; waits a little.</summary>
    public void Dispose()
    {
        Process? engine;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            engine = _engine;
        }
        if (engine is null) return;
        try
        {
            if (!engine.HasExited)
            {
                engine.StandardInput.WriteLine(JsonSerializer.Serialize(new { id = 0, method = "shutdown", @params = new { } }));
                engine.StandardInput.Close();
                if (!engine.WaitForExit(8000))
                    AppLog.Warn("Shaders add-on engine still finishing after Exo closed; it exits on its own.");
            }
        }
        catch (Exception ex) { AppLog.Debug("Shaders add-on engine shutdown: " + ex.Message); }
        finally { engine.Dispose(); }
    }
}
