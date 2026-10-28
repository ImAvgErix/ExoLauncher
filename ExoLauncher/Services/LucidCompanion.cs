using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Lucid (a separate EQ app on Equalizer APO: headphone EQ, auto device and per-game EQ switching), started with Exo
/// when the user turns it on. Exo starts the user's own copy exactly as Lucid's Windows sign-in entry does, quietly in
/// its tray (<c>/startup</c>), and opens its window with a plain second launch (Lucid's single instance restores it).
/// Lucid reads its settings once at start and saves its whole copy on every change (no watcher, no message), so Exo
/// changes them only with Lucid closed: Windows' Restart Manager asks it to close as at sign-out (no kill), Exo writes
/// the file the way Lucid does (<see cref="LucidSettings"/>), then starts it again in its tray. Lucid Voice is kept off
/// (<see cref="KeepVoiceOff"/>): Exo Voice handles the mic in Exo's calls, and a second chain before it adds delay.
/// </summary>
internal static class LucidCompanion
{
    private const string Instance = @"Global\LucidToggle.SingleInstance";

    /// <summary>Lucid's official home, the repository its own updater downloads from.</summary>
    internal const string DownloadPage = "https://github.com/tekkusai/Lucid";

    internal sealed record State(string? Path, bool Running, bool VoiceOn);

    internal static State Read(string? remembered)
    {
        var path = Locate(remembered);
        return new State(path, Running(), VoiceOn());
    }

    /// <summary>Lucid's exe: where it runs now, its Windows sign-in entry, else where Exo last found it.</summary>
    internal static string? Locate(string? remembered)
    {
        var running = RunningLucid();
        try { if (running.Count > 0) return running[0].File; }
        finally { foreach (var (process, _) in running) process.Dispose(); }
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            if (run?.GetValue("Lucid") is string command && PathHelper.CommandExe(command) is { } fromRun && IsLucid(fromRun)) return fromRun;
        return remembered is not null && IsLucid(remembered) ? remembered : null;
    }

    /// <summary>Lucid's running processes and their exe; the caller disposes them.</summary>
    private static List<(Process Process, string File)> RunningLucid()
    {
        var lucid = new List<(Process, string)>();
        foreach (var process in Process.GetProcessesByName("Lucid"))
        {
            try
            {
                if (process.MainModule?.FileName is { } file && IsLucid(file)) { lucid.Add((process, file)); continue; }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
        return lucid;
    }

    private static bool IsLucid(string file) =>
        string.Equals(System.IO.Path.GetFileName(file), "Lucid.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(file);

    internal static bool Running()
    {
        try
        {
            if (!Mutex.TryOpenExisting(Instance, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>Lucid Voice's live switch, which its Voice Core reads as it runs (Lucid's LucidVoiceAudio.DspSettingsPath).</summary>
    internal static string VoiceDspPath { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LucidEQ", "lucid_voice_dsp.json");

    /// <summary>Lucid Voice's live switch turned off, the rest kept; null when it is already off or isn't Lucid's.</summary>
    internal static string? VoiceDspOff(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["enabled"]?.GetValueKind() != JsonValueKind.True) return null;
            root["enabled"] = false;
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return null; }
    }

    private static readonly List<FileSystemWatcher> VoiceWatchers = [];
    private static Timer? _voiceRecheck, _voiceLater;

    /// <summary>
    /// Lucid Voice stays off, however it was turned on (Lucid's window, a Repair, an update): its live switch off at once,
    /// as Lucid's own DisableLucidVoiceAsync does (no restart), and its setting off so Lucid doesn't turn it on again when
    /// it starts. Checked now and whenever either file changes while Exo runs.
    /// </summary>
    internal static void KeepVoiceOff(SettingsService settings)
    {
        Check();
        Watch();
        // Lucid installed while Exo runs (from Exo's Settings or on its own): its folders appear later, so until both are
        // watched, a slow look every half minute starts watching them and checks once.
        if (!AllWatched()) _voiceLater = new Timer(_ =>
        {
            Watch();
            Check();
            if (AllWatched()) _voiceLater?.Dispose();
        }, null, 30_000, 30_000);

        bool AllWatched() { lock (VoiceWatchers) return VoiceWatchers.Count == 2; }

        void Watch()
        {
            foreach (var file in new[] { VoiceDspPath, LucidSettings.FilePath })
            {
                try
                {
                    var folder = System.IO.Path.GetDirectoryName(file)!;
                    if (!Directory.Exists(folder)) continue;
                    lock (VoiceWatchers) if (VoiceWatchers.Any(w => string.Equals(w.Path, folder, StringComparison.OrdinalIgnoreCase) && w.Filter == System.IO.Path.GetFileName(file))) continue;
                    var watcher = new FileSystemWatcher(folder, System.IO.Path.GetFileName(file)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
                    // Lucid writes through a temporary file and a replace: one check a moment after the last event.
                    FileSystemEventHandler later = (_, _) => (_voiceRecheck ??= new Timer(_ => Check())).Change(800, Timeout.Infinite);
                    watcher.Changed += later;
                    watcher.Created += later;
                    watcher.Renamed += (_, _) => later(null!, null!);
                    watcher.EnableRaisingEvents = true;
                    lock (VoiceWatchers) VoiceWatchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { AppLog.Warn("Lucid: can't watch " + System.IO.Path.GetFileName(file) + ": " + ex.Message); }
            }
        }

        void Check()
        {
            try
            {
                if (File.Exists(VoiceDspPath) && VoiceDspOff(File.ReadAllText(VoiceDspPath)) is { } off)
                {
                    // Lucid's own way: UTF-8 without a mark (its Voice Core rejects a file that starts with one).
                    // Beside it and moved over, as Lucid itself writes: Lucid never reads half a file.
                    var staged = VoiceDspPath + ".exo-tmp";
                    try
                    {
                        File.WriteAllText(staged, off, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        File.Move(staged, VoiceDspPath, overwrite: true);
                    }
                    finally { try { File.Delete(staged); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
                    AppLog.Info("Lucid: Lucid Voice turned off; Exo Voice handles the mic.");
                    // A Voice Tune made through Lucid Voice set its gate for Lucid's louder sound: without it the gate would hold
                    // back a normal voice, so that mic's tuning is let go of (Exo Voice's general settings until it is tuned again).
                    if (LucidSettings.Load(LucidSettings.FilePath)?["LucidVoiceMicEndpointGuid"] is JsonValue mic && mic.TryGetValue<string>(out var guid) && guid.Length > 0)
                        settings.UpdateProfile(s =>
                        {
                            foreach (var key in s.DiscordVoiceTunes.Keys.Where(key => key.EndsWith(guid, StringComparison.OrdinalIgnoreCase)).ToList())
                                s.DiscordVoiceTunes.Remove(key);
                        });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Lucid: Lucid Voice's switch not changed: " + ex.Message); }
            if (VoiceOn() && Locate(settings.Current.LucidPath) is { } lucid) _ = ChangeAsync(lucid, new LucidSettings.Change { VoiceOff = true }, now: true);
        }
    }

    /// <summary>Lucid Voice as Lucid's own settings have it; off when they can't be read.</summary>
    internal static bool VoiceOn() =>
        LucidSettings.Load(LucidSettings.FilePath)?["LucidVoiceEnabled"]?.GetValueKind() == JsonValueKind.True;

    /// <summary>Starts Lucid: in its tray (as at Windows sign-in), or showing its window (a running Lucid comes forward).</summary>
    internal static bool Start(string path, bool window)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, window ? "" : "/startup")
            {
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? "",
            });
            AppLog.Info($"Lucid: started {(window ? "with its window" : "in its tray")}.");
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn("Lucid: could not start: " + ex.Message);
            return false;
        }
    }

    /// <summary>On Exo's start: Lucid out of sight when the user turned Start with Exo on, started if it isn't running.</summary>
    internal static void StartWithExo(SettingsService settings)
    {
        var current = settings.Current;
        if (!current.StartLucidWithExo) return;
        HideTray(true);
        if (Running()) return;
        if (Locate(current.LucidPath) is not { } path) { AppLog.Info("Lucid: Start with Exo is on, but Lucid wasn't found."); return; }
        if (!string.Equals(path, current.LucidPath, StringComparison.OrdinalIgnoreCase)) settings.UpdateProfile(s => s.LucidPath = path);
        Start(path, window: false);
    }

    /// <summary>Equalizer APO's device picker, which Lucid opens elevated from APO's folder to choose its devices.</summary>
    internal static string? DeviceSelector()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
            var dir = key?.GetValue("InstallPath") as string;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
            var picker = System.IO.Path.Combine(dir, "DeviceSelector.exe");
            return File.Exists(picker) ? picker : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>Opens the device picker as Lucid's DeviceSelectorLauncher does (runas, in APO's folder); null when it opened.</summary>
    internal static string? OpenDeviceSelector()
    {
        if (DeviceSelector() is not { } picker) return "Equalizer APO isn't installed. Open Lucid to set it up.";
        try
        {
            // Elevated, it opens behind Exo: Exo (in front when it was pressed) lets it take the front, then puts it there.
            AllowSetForegroundWindow(uint.MaxValue);
            using var process = Process.Start(new ProcessStartInfo(picker)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = System.IO.Path.GetDirectoryName(picker) ?? "",
            });
            if (process is null) return "The device picker didn't open.";
            var id = process.Id;
            _ = Task.Run(async () =>
            {
                for (var tries = 0; tries < 75; tries++)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    if (WindowOf(id) is not 0 and var window) { ShowWindow(window, 9); SetForegroundWindow(window); return; } // SW_RESTORE
                }
            });
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Windows asked for permission and it was declined."; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn("Lucid: device picker didn't open: " + ex.Message);
            return "The device picker didn't open.";
        }
    }

    private static readonly SemaphoreSlim ChangeGate = new(1, 1);
    private static readonly Lock PendingGate = new();
    private static LucidSettings.Change? _pending;
    private static Task<string?>? _pendingRun;

    /// <summary>
    /// Changes Lucid's settings. Changes made close together are written in one go after a short pause, so a few
    /// switches cost Lucid one restart. Null when they took, else why not.
    /// </summary>
    /// <summary>
    /// Whether a game is playing (Exo's host says). A change waits for the last game to end: Lucid starts again to take
    /// one, and the EQ drops out for about 3 seconds while it does. Lucid Voice going off for Exo Voice doesn't wait.
    /// </summary>
    internal static Func<bool> Playing { get; set; } = () => false;

    private static LucidSettings.Change? _held;

    /// <summary>The change waiting for the game to end, if one is.</summary>
    internal static LucidSettings.Change? Held { get { lock (PendingGate) return _held; } }

    internal static Task<string?> ChangeAsync(string lucid, LucidSettings.Change change, bool now = false)
    {
        if (!now && Playing())
        {
            lock (PendingGate) _held = _held is null ? change : _held.Then(change);
            AppLog.Info("Lucid: a change waits for the game to end.");
            return Task.FromResult<string?>(null);
        }
        lock (PendingGate)
        {
            _pending = _pending is null ? change : _pending.Then(change);
            return _pendingRun ??= Task.Run(async () =>
            {
                await Task.Delay(700).ConfigureAwait(false);
                LucidSettings.Change next;
                lock (PendingGate) { next = _pending!; _pending = null; _pendingRun = null; }
                await ChangeGate.WaitAsync().ConfigureAwait(false);
                try { return await WriteAsync(lucid, next).ConfigureAwait(false); }
                finally { ChangeGate.Release(); }
            });
        }
    }

    /// <summary>The last game ended: the change that waited for it goes in now.</summary>
    internal static void ReleaseHeld(string? lucidPath)
    {
        LucidSettings.Change? held;
        lock (PendingGate) { held = _held; _held = null; }
        if (held is null || Locate(lucidPath) is not { } lucid) return;
        AppLog.Info("Lucid: the game ended; the change that waited goes in.");
        _ = ChangeAsync(lucid, held);
    }

    private static async Task<string?> WriteAsync(string lucid, LucidSettings.Change change)
    {
        var wasRunning = Running();
        if (wasRunning && !await CloseAsync().ConfigureAwait(false))
            return "Lucid didn't close to take the change. Quit it from its tray, then try again.";
        string? error;
        try
        {
            // Read after Lucid closed: it saves its own copy as it goes.
            if (LucidSettings.Load(LucidSettings.FilePath) is not { } root) error = "Open Lucid once to finish its setup, then try again.";
            else if ((error = LucidSettings.Apply(root, change, id => HeadphoneName(lucid, id), LucidSettings.WindowsNightLight)) is null)
            {
                LucidSettings.Save(LucidSettings.FilePath, root);
                AppLog.Info("Lucid: settings changed.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warn("Lucid: settings not saved: " + ex.Message);
            error = "Lucid's settings couldn't be saved. Try again.";
        }
        if (wasRunning && !Start(lucid, window: false)) error ??= "Lucid didn't start again. Open it yourself.";
        return error;
    }

    internal static string? HeadphoneName(string lucid, string id) =>
        LucidCatalog.Read(lucid)?.FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase)) is { } headphone &&
        headphone.Id != LucidSettings.LucidIems ? headphone.Name : null;

    /// <summary>
    /// Asks Lucid to close through the Restart Manager: Windows sends it the sign-out close (WM_QUERYENDSESSION with
    /// ENDSESSION_CLOSEAPP), which WPF answers by shutting down normally; its window's close button would only hide it
    /// to the tray. Without RmForceShutdown nothing is ever killed: a Lucid that refuses keeps running.
    /// </summary>
    private static async Task<bool> CloseAsync()
    {
        var processes = RunningLucid();
        try
        {
            var lucid = processes.Select(p => p.Process).ToArray();
            if (lucid.Length == 0) return !Running();
            var rc = RmShutdown(lucid);
            if (rc != 0) AppLog.Warn($"Lucid: Restart Manager didn't close it ({rc}).");
            for (var i = 0; i < 50 && Running(); i++) await Task.Delay(100).ConfigureAwait(false);
            var closed = !Running();
            if (closed) AppLog.Info("Lucid: closed for a settings change.");
            return closed;
        }
        finally { foreach (var (process, _) in processes) process.Dispose(); }
    }

    private static int RmShutdown(Process[] processes)
    {
        var rc = RmStartSession(out var session, 0, new StringBuilder(64));
        if (rc != 0) return rc;
        try
        {
            var unique = processes.Select(p =>
            {
                var time = p.StartTime.ToFileTime();
                return new RmUniqueProcess { ProcessId = p.Id, StartTime = new FILETIME { dwLowDateTime = (int)(time & 0xffffffff), dwHighDateTime = (int)(time >> 32) } };
            }).ToArray();
            rc = RmRegisterResources(session, 0, null, (uint)unique.Length, unique, 0, null);
            return rc != 0 ? rc : RmShutdownSession(session, 0, IntPtr.Zero);
        }
        finally { _ = RmEndSession(session); }
    }

    private static readonly Lock TrayGate = new();
    private static Timer? _trayWatch;
    private static bool _trayLogged;

    /// <summary>
    /// While Start with Exo is on, Lucid runs out of sight like the apps Exo hosts: its tray icon is removed with the
    /// remover Exo uses for Spotify's (<see cref="TrayIconRemover"/>), again every 30 s because Lucid adds a new one
    /// when it starts again. Off (or Exo closing) gives the icon back: Lucid's tray library (Hardcodet's TaskbarIcon)
    /// adds it again when its message window gets Windows' TaskbarCreated, the message Explorer sends after a restart.
    /// Started with <c>/startup</c>, Lucid shows no window of its own: only the ones asked for from Exo, and its close
    /// button hides them again (MainWindow.OnClosing: MinimizeToTray).
    /// </summary>
    internal static void HideTray(bool on)
    {
        lock (TrayGate)
        {
            if (on) { _trayWatch ??= new Timer(_ => RemoveTrayIcon(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30)); return; }
            if (_trayWatch is null) return;
            _trayWatch.Dispose();
            _trayWatch = null;
            _trayLogged = false;
        }
        ShowTrayIcon();
    }

    private static void RemoveTrayIcon()
    {
        var running = RunningLucid();
        try
        {
            if (running.Count == 0) return;
            var removed = TrayIconRemover.Remove(TrayIcons.Read(running[0].File), running.Select(p => p.Process.Id).ToArray());
            if (removed > 0 && !_trayLogged) { _trayLogged = true; AppLog.Info("Lucid tray icon removed."); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { foreach (var (process, _) in running) process.Dispose(); }
    }

    private static void ShowTrayIcon()
    {
        var running = RunningLucid();
        var pids = running.Select(p => (uint)p.Process.Id).ToHashSet();
        foreach (var (process, _) in running) process.Dispose();
        if (pids.Count == 0) return;
        var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid)) PostMessage(hwnd, taskbarCreated, 0, 0);
            return true;
        }, 0);
        AppLog.Info("Lucid tray icon given back.");
    }

    // ── Updates: Lucid updates itself, from its own window ──

    /// <summary>The version feed Lucid's own updater reads (Updater.VersionUrl).</summary>
    internal const string UpdateFeed = "https://raw.githubusercontent.com/tekkusai/Lucid/main/app.latest";
    private static readonly HttpClient UpdateHttp = CreateUpdateHttp();
    /// <summary>The feed's text, read at most once a day and retried as every version check is (<see cref="LatestCache{T}"/>).</summary>
    private static readonly LatestCache<string> Feed = new("Lucid's version feed", TimeSpan.FromDays(1));

    private static HttpClient CreateUpdateHttp()
    {
        var http = ExoHttp.Create(TimeSpan.FromSeconds(20));
        http.MaxResponseContentBufferSize = 4096;
        return http;
    }

    /// <summary>
    /// The newer Lucid its feed offers over the installed one, read at most once a day; null when there is none, or
    /// offline, or the feed doesn't parse. Exo only tells: Lucid's own window downloads, verifies and replaces itself.
    /// </summary>
    internal static async Task<Version?> AvailableUpdateAsync(string lucid)
    {
        if (FileVersion(lucid) is not { } current) return null;
        var feed = await Feed.GetAsync("lucid", async ct => await UpdateHttp.GetStringAsync(UpdateFeed, ct).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
        // Compared by Lucid's own rule (Loose), so Exo offers what Lucid's updater would.
        return feed is not null && Loose(feed) is { } latest && latest > current ? latest : null;
    }

    /// <summary>Lucid's Updater.FileVersion: the exe's file version, null when it has none.</summary>
    internal static Version? FileVersion(string exe)
    {
        try { return Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var version) ? version : null; }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>
    /// Lucid's Updater.TryLoose, which its OfferedUpdate compares with the installed version: trimmed, a byte-order
    /// mark and a leading v dropped, missing parts filled with zeros up to four.
    /// </summary>
    internal static Version? Loose(string raw)
    {
        raw = raw.Trim().TrimStart('\uFEFF');
        if (raw.StartsWith('v') || raw.StartsWith('V')) raw = raw[1..];
        if (Version.TryParse(raw, out var version)) return Canonical(version);
        while (raw.Split('.').Length < 4)
        {
            raw += ".0";
            if (Version.TryParse(raw, out version)) return Canonical(version);
        }
        return null;

        static Version Canonical(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    /// <summary>"6.0.3" for 6.0.3.0, "7.0" for 7.0.0.0.</summary>
    internal static string Shown(Version v) => v.ToString(v.Revision > 0 ? 4 : v.Build > 0 ? 3 : 2);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public FILETIME StartTime;
    }

    // The process's first visible top-level window, or 0.
    private static nint WindowOf(int processId)
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out var owner) == 0 || owner != (uint)processId) return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[]? fileNames, uint applications, RmUniqueProcess[] processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll", EntryPoint = "RmShutdown")]
    private static extern int RmShutdownSession(uint session, uint flags, IntPtr progress);

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
