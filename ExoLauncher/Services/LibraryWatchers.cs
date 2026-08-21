using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Debounced file watchers on store install manifests so the library does not
/// wait for the 30-second freshness window.
/// </summary>
internal sealed class LibraryWatchers : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _gate = new();
    private CancellationTokenSource? _debounce;
    private bool _disposed;

    public event Action? Changed;

    public void Start()
    {
        foreach (var path in CandidateRoots())
            TryWatch(path);
    }

    internal static IEnumerable<string> CandidateRoots()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var steam = TrySteamRoot();
        if (!string.IsNullOrWhiteSpace(steam))
        {
            yield return Path.Combine(steam, "steamapps");
            foreach (var library in ExtraSteamLibraries(steam))
                yield return Path.Combine(library, "steamapps");
        }

        yield return Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        yield return Path.Combine(programData, "GOG.com", "Galaxy", "Applications");
        yield return Path.Combine(local, "GOG.com", "Galaxy", "Applications");
        yield return Path.Combine(programData, "Riot Games");
        yield return Path.Combine(local, "Riot Games");
        yield return Path.Combine(programData, "EA Desktop");
        yield return Path.Combine(programData, "Electronic Arts", "EA Desktop");
        yield return PathHelper.GamesRoot;
        foreach (var root in AmazonFuelWatchRoots(local, programData))
            yield return root;
        foreach (var xbox in XboxGamesRoots())
            yield return xbox;
    }

    private void TryWatch(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Library watcher skipped " + path + ": " + ex.Message);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e) => OnChanged(sender, e);

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (sender is FileSystemWatcher watcher)
        {
            try { watcher.EnableRaisingEvents = true; }
            catch { /* watcher is gone */ }
        }

        OnChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.Changed, "", ""));
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var name = e.Name ?? "";
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return;

        CancellationTokenSource cts;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed) return;
            try { _debounce?.Cancel(); } catch { }
            previous = _debounce;
            cts = new CancellationTokenSource();
            _debounce = cts;
        }

        _ = DebounceAsync(cts, previous, name);
    }

    internal void NotifyChangedForTests() =>
        OnChanged(this, new FileSystemEventArgs(WatcherChangeTypes.Changed, "", "manifest.acf"));

    private async Task DebounceAsync(CancellationTokenSource cts, CancellationTokenSource? previous, string name)
    {
        try
        {
            try
            {
                await Task.Delay(2000, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            AppLog.Info(string.IsNullOrWhiteSpace(name)
                ? "Library watch fired."
                : "Library watch fired: " + name);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Debug("Library watcher debounce failed: " + ex.Message);
        }
        finally
        {
            try { previous?.Dispose(); } catch { /* already disposed */ }
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_debounce, cts))
                {
                    if (ReferenceEquals(_debounce, cts))
                        _debounce = null;
                    try { cts.Dispose(); } catch { /* already disposed */ }
                }
            }
        }
    }

    internal static string? TrySteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var normalized = path.Replace('/', Path.DirectorySeparatorChar);
                if (Directory.Exists(normalized)) return normalized;
            }
        }
        catch
        {
            /* fall through */
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
                 })
        {
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static IEnumerable<string> AmazonFuelWatchRoots(string local, string programData)
    {
        yield return Path.Combine(local, "Amazon Games", "Data", "Games");
        yield return Path.Combine(local, "Amazon Games", "Installed");
        yield return Path.Combine(programData, "Amazon Games", "Installed");
        yield return Path.Combine(programData, "Amazon Games", "Data", "Games");
    }

    private static IEnumerable<string> XboxGamesRoots()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (var drive in drives)
        {
            string? root = null;
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            }
            catch
            {
                continue;
            }

            if (root is not null)
                yield return root;
        }
    }

    private static IEnumerable<string> ExtraSteamLibraries(string steamRoot)
    {
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
        {
            var path = match.Groups[1].Value.Replace("\\\\", "\\");
            if (Directory.Exists(path) &&
                !path.Equals(steamRoot, StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _debounce?.Cancel(); } catch { }
            // DebounceAsync disposes the CTS after it observes cancel.
        }

        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnChanged;
                watcher.Deleted -= OnChanged;
                watcher.Changed -= OnChanged;
                watcher.Renamed -= OnRenamed;
                watcher.Error -= OnError;
                watcher.Dispose();
            }
            catch
            {
                /* ignore */
            }
        }

        _watchers.Clear();
    }
}
