using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Exo Capture (instant replay, recording, snips): its own program, GPL-3.0-or-later,
/// source in <c>capture/</c>. Exo's core never captures the screen (BannedSymbols.txt);
/// all of that lives in this add-on, shipped under <c>addons\capture</c> in the app folder
/// or installed beside Exo's data under <c>%LOCALAPPDATA%\ExoLauncher\addons\capture</c>.
/// Exo starts it hidden and speaks newline-delimited JSON over its stdin and stdout:
/// anonymous pipes only the two processes hold, so no other program can reach it
/// (no port, no named pipe); Go Live's frames come back on one more such pipe
/// (<see cref="ShareHandle"/>). Exo closes it on exit, which also closes a recording.
/// </summary>
internal sealed class CaptureAddon : IDisposable
{
    /// <summary>A copy installed beside Exo's data (a newer or development build) wins over the one in the app folder.</summary>
    internal static string Root => File.Exists(Path.Combine(LocalRoot, "ExoCapture.exe")) ? LocalRoot : BundledRoot;

    private static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "addons", "capture");

    private static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "addons", "capture");
    internal static string ExePath => Path.Combine(Root, "ExoCapture.exe");
    internal static bool Present => File.Exists(ExePath);

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private Process? _process;
    private long _exitedAt;
    private Task? _reading; // _process's stdout loop
    private long _nextId;
    private bool _disposed;
    private int _restarts;

    /// <summary>An add-on event (<c>ready</c>, <c>state</c>, <c>saved</c>, <c>action</c>, <c>failed</c>), or <c>exited</c>.</summary>
    internal event Action<string, JsonNode?>? Event;

    internal void Start()
    {
        lock (_gate)
        {
            // After a second crash it rests: nothing starts it again for 30 seconds (no crash loop), then what needs it
            // (Go Live, a recording, a page) may, so one bad moment doesn't leave it down until Exo restarts.
            if (_disposed || !Present || _process is { HasExited: false }) return;
            if (Volatile.Read(ref _restarts) > 1 && Environment.TickCount64 - Volatile.Read(ref _exitedAt) < 30_000) return;
            try
            {
                var info = new ProcessStartInfo(ExePath)
                {
                    // Not the app folder, so a running add-on never holds it during an update.
                    WorkingDirectory = Path.GetTempPath(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardInputEncoding = new UTF8Encoding(false),
                };
                var process = Process.Start(info) ?? throw new InvalidOperationException("Exo Capture did not start.");
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => OnExited(process);
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppLog.Info("Capture: " + Trim(e.Data, 300)); };
                process.BeginErrorReadLine();
                _process = process;
                _reading = Task.Run(() => ReadLoop(process));
                AppLog.Info("Capture add-on started: pid " + process.Id + ".");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Capture add-on failed to start: " + ex.Message);
            }
        }
    }

    private Process Running()
    {
        Process? process;
        lock (_gate) process = _process is { HasExited: false } live ? live : null;
        if (process is not null) return process;
        Start();
        lock (_gate) process = _process is { HasExited: false } live ? live : null;
        return process ?? throw new InvalidOperationException("Exo Capture is not running.");
    }

    /// <summary>
    /// One of Exo's handles duplicated into the add-on (Go Live's pipe), and its value there; the add-on
    /// owns and closes that copy. Still only the two processes hold it.
    /// </summary>
    internal nint ShareHandle(SafeHandle handle)
    {
        var process = Running();
        if (!DuplicateHandle(GetCurrentProcess(), handle, process.Handle, out var copy, 0, false, 2 /* DUPLICATE_SAME_ACCESS */))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return copy;
    }

    /// <summary>
    /// Lets the add-on bring its own window to the front (the snip overlay, which needs the keyboard for Esc and Enter).
    /// Windows gives the foreground only to the process the user is in, so Exo, in front when its Snip is pressed, passes it on.
    /// </summary>
    internal void AllowForeground() => AllowSetForegroundWindow((uint)Running().Id);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(nint sourceProcess, SafeHandle source, nint targetProcess, out nint target, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    internal async Task<JsonNode?> CallAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var process = Running();
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            var line = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters?.DeepClone() ?? new JsonObject() }.ToJsonString();
            lock (_gate)
            {
                process.StandardInput.WriteLine(line);
                process.StandardInput.Flush();
            }
            using var reg = ct.Register(() => reply.TrySetCanceled(ct));
            return await reply.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoop(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); } catch { continue; }
                if (node is not JsonObject message) continue;
                if (message["id"] is JsonValue idValue && idValue.TryGetValue<double>(out var rawId))
                {
                    if (!_pending.TryGetValue((long)rawId, out var reply)) continue;
                    if (message["error"] is { } error) reply.TrySetException(new InvalidOperationException(Trim(error.GetValue<string>(), 300)));
                    else reply.TrySetResult(message["result"]?.DeepClone());
                }
                else if (message["event"]?.GetValue<string>() is { } name)
                {
                    try { Event?.Invoke(name, message["data"]?.DeepClone()); }
                    catch (Exception ex) { AppLog.Warn("Capture event " + name + ": " + ex.Message); }
                }
            }
        }
        catch (Exception ex) { AppLog.Debug("Capture add-on read ended: " + ex.Message); }
    }

    private void OnExited(Process process)
    {
        int code;
        try { code = process.ExitCode; } catch { code = -1; }
        Task? reading = null;
        lock (_gate)
        {
            if (ReferenceEquals(_process, process) && !_disposed)
            {
                _process = null;
                reading = _reading;
            }
        }
        // Released once its last output line is read; Dispose() owns it after Exo closes.
        reading?.ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        foreach (var pending in _pending.Values)
            pending.TrySetException(new InvalidOperationException("Exo Capture stopped."));
        if (_disposed) return;
        AppLog.Warn("Capture add-on exited: code " + code + ".");
        Volatile.Write(ref _exitedAt, Environment.TickCount64);
        Event?.Invoke("exited", null);
        // One quiet restart after a crash; a second one stays down until Exo restarts.
        if (code != 0 && Interlocked.Increment(ref _restarts) == 1)
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => Start(), TaskScheduler.Default);
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>Asks the add-on to finish a recording and exit; waits up to 10 seconds.</summary>
    public void Dispose()
    {
        Process? process;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            process = _process;
        }
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("{\"id\":0,\"method\":\"shutdown\"}");
                process.StandardInput.Close();
                if (!process.WaitForExit(10000))
                    AppLog.Warn("Capture add-on still finishing after Exo closed; it exits on its own.");
            }
        }
        catch (Exception ex) { AppLog.Debug("Capture add-on shutdown: " + ex.Message); }
        finally { process.Dispose(); }
    }
}
