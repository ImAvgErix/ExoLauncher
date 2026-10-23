using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Debounced file watchers on store install manifests so the library does not
/// wait for the 30-second freshness window.
/// </summary>
internal sealed class LibraryWatchers : IDisposable
{
    private readonly List<(FileSystemWatcher Watcher, StoreKind? Store)> _watchers = [];
    private readonly object _gate = new();
    private CancellationTokenSource? _debounce;
    private readonly HashSet<StoreKind> _pendingStores = [];
    private bool _pendingUnknown;
    // Files rewritten inside the window, and whether anything else (a file added, removed or renamed, a folder) changed.
    private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _pendingOther;
    private readonly Dictionary<string, byte[]> _contents = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private Timer? _rootRefresh;
    private readonly Func<IEnumerable<(string Path, StoreKind? Store)>> _candidateRoots;

    internal LibraryWatchers(Func<IEnumerable<(string Path, StoreKind? Store)>>? candidateRoots = null) =>
        _candidateRoots = candidateRoots ?? CandidateRoots;

    /// <summary>The store whose watched root changed; null when the source is
    /// unknown (watcher error) and every store is dirty. Raised once per store
    /// that changed inside one debounce window.</summary>
    public event Action<StoreKind?>? Changed;

    public void Start()
    {
        RefreshRoots();
        // Steam can add another library folder while Exo is already open.
        _rootRefresh ??= new Timer(_ => RefreshRoots(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    internal void RefreshRoots()
    {
        lock (_gate)
        {
            if (_disposed) return;
            for (var index = _watchers.Count - 1; index >= 0; index--)
            {
                var (watcher, _) = _watchers[index];
                if (Directory.Exists(watcher.Path) && watcher.EnableRaisingEvents) continue;
                _watchers.RemoveAt(index);
                watcher.Dispose();
            }
            foreach (var root in _candidateRoots().Distinct())
                if (!_watchers.Any(w => string.Equals(w.Watcher.Path, root.Path, StringComparison.OrdinalIgnoreCase)))
                    TryWatch(root.Path, root.Store);
        }
    }

    internal static IEnumerable<(string Path, StoreKind? Store)> CandidateRoots()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var steam = TrySteamRoot();
        if (!string.IsNullOrWhiteSpace(steam))
        {
            yield return (Path.Combine(steam, "config"), StoreKind.Steam);
            yield return (Path.Combine(steam, "steamapps"), StoreKind.Steam);
            foreach (var library in ExtraSteamLibraries(steam))
                yield return (Path.Combine(library, "steamapps"), StoreKind.Steam);
        }

        yield return (Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests"), StoreKind.Epic);
        // The new Epic launcher's install list (installations.jsonl).
        if (Adapters.EpicLauncherV2.DataDir() is { } eglv2) yield return (eglv2, StoreKind.Epic);
        yield return (Path.Combine(programData, "GOG.com", "Galaxy", "Applications"), StoreKind.Gog);
        yield return (Path.Combine(local, "GOG.com", "Galaxy", "Applications"), StoreKind.Gog);
        yield return (Path.Combine(programData, "Riot Games"), StoreKind.Riot);
        yield return (Path.Combine(local, "Riot Games"), StoreKind.Riot);
        yield return (Path.Combine(programData, "EA Desktop"), StoreKind.Ea);
        yield return (Path.Combine(programData, "Electronic Arts", "EA Desktop"), StoreKind.Ea);
        yield return (PathHelper.GamesRoot, StoreKind.Local);
        foreach (var root in AmazonFuelWatchRoots(local, programData))
            yield return (root, StoreKind.Amazon);
        foreach (var xbox in Adapters.OfficialInstalledLibraries.XboxGamesRoots())
            yield return (xbox, StoreKind.Xbox);
    }

    private void TryWatch(string path, StoreKind? store)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            if (!Directory.Exists(path)) return;
            watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = false,
            };
            // The new Epic launcher's data folder churns caches, telemetry and logs: only its install list counts.
            if (Adapters.EpicLauncherV2.DataDir() is { } eglv2 && string.Equals(Path.GetFullPath(path), Path.GetFullPath(eglv2), StringComparison.OrdinalIgnoreCase))
            {
                watcher.Filter = "installations.jsonl";
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
            }
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watchers.Add((watcher, store));
        }
        catch (Exception ex)
        {
            watcher?.Dispose();
            AppLog.Debug("Library watcher skipped " + path + ": " + ex.Message);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e) => OnChanged(sender, e);

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (sender is FileSystemWatcher watcher)
        {
            // A deleted folder or disconnected drive invalidates the native
            // watch handle. Let the root refresh attach a new watcher when the
            // location returns instead of retaining a dead watcher forever.
            lock (_gate)
            {
                if (_disposed) return;
                _watchers.RemoveAll(entry => ReferenceEquals(entry.Watcher, watcher));
            }
            try { watcher.Dispose(); } catch { /* already gone */ }
        }

        // A dead watcher may have missed events: report an unknown source so
        // the next forced scan covers every store.
        OnChanged(this, new FileSystemEventArgs(WatcherChangeTypes.Changed, "", ""));
    }

    /// <summary>
    /// Only install-manifest changes are worth a store rescan. Steam rewrites
    /// config.vdf and its cache folders every minute or two while it runs; without
    /// this filter every one of those writes forced a full library scan.
    /// </summary>
    internal static bool IsLibraryManifestChange(string name)
    {
        if (name.Length == 0) return true;
        var extension = Path.GetExtension(name);
        if (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".lock", StringComparison.OrdinalIgnoreCase))
            return false;
        switch (extension.ToLowerInvariant())
        {
            case ".acf": case ".item": case ".json": case ".info": case ".yaml": case ".mfst": case ".xml": case ".manifest":
                return true;
            case ".jsonl":
                return name.Equals("installations.jsonl", StringComparison.OrdinalIgnoreCase);
            case ".vdf":
                return name.Equals("libraryfolders.vdf", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("loginusers.vdf", StringComparison.OrdinalIgnoreCase);
            case "":
                // Directory entries: install folders matter, Steam's cache and staging folders do not.
                return !(name.Contains("cache", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("downloading", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("temp", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("workshop", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("sourcemods", StringComparison.OrdinalIgnoreCase));
            default:
                return false;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var name = e.Name ?? "";
        if (!IsLibraryManifestChange(name)) return;

        CancellationTokenSource cts;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed) return;
            StoreKind? store = null;
            if (sender is FileSystemWatcher source)
                store = _watchers.FirstOrDefault(entry => ReferenceEquals(entry.Watcher, source)).Store;
            // Every store touched inside one debounce window is reported, not
            // just the last one.
            if (store is { } known) _pendingStores.Add(known);
            else _pendingUnknown = true;
            if (e.ChangeType == WatcherChangeTypes.Changed && e.FullPath.Length > 0) _pendingFiles.Add(e.FullPath);
            else _pendingOther = true;
            try { _debounce?.Cancel(); } catch { }
            previous = _debounce;
            cts = new CancellationTokenSource();
            _debounce = cts;
        }

        _ = DebounceAsync(cts, previous, name);
    }

    private async Task DebounceAsync(CancellationTokenSource cts, CancellationTokenSource? previous,
        string name)
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
            StoreKind[] stores;
            bool unknown;
            string[] files;
            bool other;
            lock (_gate)
            {
                // A newer event restarted the window and will report these.
                if (_disposed || !ReferenceEquals(_debounce, cts)) return;
                stores = _pendingStores.ToArray();
                unknown = _pendingUnknown;
                files = _pendingFiles.ToArray();
                other = _pendingOther;
                _pendingStores.Clear();
                _pendingUnknown = false;
                _pendingFiles.Clear();
                _pendingOther = false;
            }
            // The Riot Client rewrites RiotClientInstalls.json with the same bytes every half minute or so while it
            // runs, and each write was a full library scan (100-400 ms, a cache save and a push to the page), mid-game too.
            var rewritten = 0;
            foreach (var file in files) if (SameAsBefore(file)) rewritten++;
            if (!other && rewritten == files.Length) return;
            AppLog.Info(string.IsNullOrWhiteSpace(name)
                ? "Library watch fired."
                : "Library watch fired: " + name);
            RefreshRoots();
            if (unknown) Changed?.Invoke(null);
            else foreach (var store in stores) Changed?.Invoke(store);
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

    /// <summary>True when the file holds the bytes it held when last seen here; remembered either way. A file first
    /// seen, too big to keep (256 KB) or unreadable counts as changed.</summary>
    internal bool SameAsBefore(string path)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 256 * 1024) return false;
            bytes = File.ReadAllBytes(path);
        }
        catch
        {
            return false;
        }
        lock (_gate)
        {
            var same = _contents.TryGetValue(path, out var last) && last.AsSpan().SequenceEqual(bytes);
            _contents[path] = bytes;
            return same;
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
            _rootRefresh?.Dispose();
            try { _debounce?.Cancel(); } catch { }
            // DebounceAsync disposes the CTS after it observes cancel.
        }

        foreach (var (watcher, _) in _watchers)
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
