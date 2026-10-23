using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The shader add-on for System > Shaders (Exo Shaders, GPL-3.0-or-later, source
/// at github.com/ImAvgErix/exo-shaders): its own program, shipped in Exo's app
/// folder under <c>addons\shaders</c> or installed beside Exo's data under
/// <c>%LOCALAPPDATA%\ExoLauncher\addons\shaders</c>, with an
/// engine (<c>engine\ExoShaders.Service.exe</c>) and a page (<c>ui\index.html</c>).
/// Exo runs no part of it in-process. It starts the engine hidden at below-normal
/// priority, speaks newline-delimited JSON over its stdin and stdout, and passes
/// requests and events between the engine and the add-on's page, which runs in a
/// frame on its own host. Without the add-on, Shaders says it is not installed.
/// </summary>
internal sealed class ShadersAddon : IDisposable
{
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

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private Process? _engine;
    private long _nextId;
    private bool _disposed;

    /// <summary>notRunning, starting, running, blocked (SCSKiller's app is open), failed.</summary>
    internal string State { get; private set; } = "notRunning";
    internal string? Message { get; private set; }

    /// <summary>An engine event line (<c>{"event":..., "data":...}</c>), or a state change of Exo's own (<c>addon</c>).</summary>
    internal event Action<string, JsonNode?>? Event;

    /// <summary>Starts the engine if it isn't running. Safe to call often.</summary>
    internal void Start()
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
                engine.EnableRaisingEvents = true;
                engine.Exited += (_, _) => OnExited(engine);
                engine.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppLog.Debug("Shaders engine: " + Trim(e.Data, 300)); };
                engine.BeginErrorReadLine();
                _engine = engine;
                _ = Task.Run(() => ReadLoop(engine));
                AppLog.Info("Shaders add-on engine started: pid " + engine.Id + ".");
                SetState("running", null);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Shaders add-on engine failed to start: " + ex.Message);
                SetState("failed", "The shader engine could not start.");
            }
        }
    }

    /// <summary>One request to the engine; the reply's <c>result</c>, or an exception with its <c>error</c>. <paramref name="start"/> false: only a running engine is asked.</summary>
    internal async Task<JsonNode?> CallAsync(string method, JsonNode? parameters, CancellationToken ct, bool start = true)
    {
        Process? engine;
        lock (_gate) engine = _engine is { HasExited: false } live ? live : null;
        if (engine is null)
        {
            if (!start) throw new InvalidOperationException("The shader engine is not running.");
            Start();
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
                {
                    Event?.Invoke(name, message["data"]?.DeepClone());
                }
            }
        }
        catch (Exception ex) { AppLog.Debug("Shaders engine read ended: " + ex.Message); }
    }

    private void OnExited(Process engine)
    {
        int code;
        try { code = engine.ExitCode; } catch { code = -1; }
        lock (_gate) if (ReferenceEquals(_engine, engine)) _engine = null;
        // The next engine knows none of Exo's games.
        Volatile.Write(ref _syncedGames, null);
        foreach (var pending in _pending.Values)
            pending.TrySetException(new InvalidOperationException("The shader engine stopped."));
        if (_disposed) return;
        AppLog.Info("Shaders add-on engine exited: code " + code + ".");
        if (code == OtherInstanceExit)
            SetState("blocked", "SCSKiller is open. Quit it from its tray icon, then try again.");
        else
            SetState(code == 0 ? "notRunning" : "failed", code == 0 ? null : "The shader engine stopped.");
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
    /// Exo's installed games (every installed store copy), so the engine can compile
    /// games its own launcher readers miss. Coalesced: library updates come in bursts.
    /// Most library updates (cover warms, size measurements) leave the installed
    /// list as it was; that list is not sent again unless the engine just started
    /// (<paramref name="force"/>, its "ready" event).
    /// An engine without exo.library answers with an error, which is ignored.
    /// </summary>
    internal void SyncLibrary(Func<IReadOnlyList<Models.GameEntry>> library, bool force = false)
    {
        if (force) Interlocked.Exchange(ref _syncForced, 1);
        if (_disposed || Interlocked.Exchange(ref _syncPending, 1) == 1) return;
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
                if (!forced && payload == Volatile.Read(ref _syncedGames)) return;
                // Never starts the engine: a new one is sent the list on its ready event.
                await CallAsync("exo.library", new JsonObject { ["games"] = games }, CancellationToken.None, start: false).ConfigureAwait(false);
                Volatile.Write(ref _syncedGames, payload);
            }
            catch (Exception ex) { AppLog.Debug("Shaders add-on library sync: " + ex.Message); }
        });
    }

    private volatile bool _uiVisible;

    /// <summary>Whether the add-on's page is on screen, as the page last told the engine.</summary>
    internal bool UiVisible => _uiVisible;

    /// <summary>The page opened or closed.</summary>
    internal void SetUiVisible(bool visible) => _uiVisible = visible;

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
