using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed partial class DlssSwapService
{
    internal sealed class DetectionStatusCache
    {
        // 5: per-file origin, original and pinned match; per-game anti-cheat.
        private const int SchemaVersion = 5;
        private const long MaximumCacheBytes = 4L * 1024 * 1024;
        private const int MaximumFilesPerGame = 64;
        private readonly string _path;
        private readonly string _appVersion;
        private readonly int _maximumEntries;
        private readonly Func<DateTime> _utcNow;
        private readonly object _gate = new();
        private Dictionary<string, CachedGame> _entries = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;
        internal void Clear()
        {
            lock (_gate)
            {
                File.Delete(_path);
                _entries.Clear();
                _loaded = true;
            }
        }

        private sealed class CacheEnvelope
        {
            public CacheEnvelope() { }
            public int Schema { get; set; }
            public string AppVersion { get; set; } = string.Empty;
            public List<CachedGame> Entries { get; set; } = [];
        }

        private sealed class CachedGame
        {
            public CachedGame() { }
            public string Key { get; set; } = string.Empty;
            public string Root { get; set; } = string.Empty;
            public long RootLastWriteUtcTicks { get; set; }
            public long UpdatedUtcTicks { get; set; }
            public string? AntiCheat { get; set; }
            public List<CachedDll> Files { get; set; } = [];
        }

        private sealed class CachedDll
        {
            public CachedDll() { }
            public string RelativePath { get; set; } = string.Empty;
            public string FullPath { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string Kind { get; set; } = string.Empty;
            public long Length { get; set; }
            public long LastWriteUtcTicks { get; set; }
            // The saved original and Exo's write marker live beside the file;
            // a change to any of them invalidates the entry like the file itself.
            public string Sidecars { get; set; } = string.Empty;
            public string? CurrentVersion { get; set; }
            public string? SkipReason { get; set; }
            public string Origin { get; set; } = "shipped";
            public string? OriginalVersion { get; set; }
            public string? OriginalState { get; set; }
            public string? MatchedRelease { get; set; }
        }

        internal DetectionStatusCache(
            string path,
            string appVersion,
            int maximumEntries = 256,
            Func<DateTime>? utcNow = null)
        {
            _path = Path.GetFullPath(path);
            _appVersion = appVersion;
            _maximumEntries = Math.Clamp(maximumEntries, 1, 1024);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <param name="stale">
        /// The files still match but the scan is over 5 minutes old: a DLL added
        /// deep in the tree changes no fingerprint Exo checks, so the caller
        /// rescans behind this answer. Dropping the entry instead made every
        /// first status read after a start walk the whole install (~450 ms).
        /// </param>
        /// <summary>Every upscaler file name a scanned game ships, lowercase (what a new release would be fetched for).</summary>
        internal HashSet<string> FileNames()
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _entries.Values.SelectMany(entry => entry.Files).Select(file => file.FileName.ToLowerInvariant()).ToHashSet();
            }
        }

        internal ScanResult? TryGet(GameEntry game, out bool stale)
        {
            stale = false;
            var root = ResolveInstallRoot(game.Path);
            if (root is null) return null;
            var key = GameCacheKey(game);
            lock (_gate)
            {
                EnsureLoaded();
                if (!_entries.TryGetValue(key, out var entry)) return null;
                if (!EntryMatches(entry, root))
                {
                    _entries.Remove(key);
                    TryWriteDisk();
                    return null;
                }
                stale = _utcNow().Ticks - entry.UpdatedUtcTicks > TimeSpan.FromMinutes(5).Ticks;
                return new ScanResult(entry.Files.Select(file => new DetectedDll(
                    file.FullPath,
                    file.RelativePath,
                    file.FileName,
                    file.Kind,
                    file.CurrentVersion,
                    file.SkipReason,
                    file.Origin,
                    file.OriginalVersion,
                    file.OriginalState,
                    file.MatchedRelease)).ToList(), entry.AntiCheat);
            }
        }

        internal void Store(GameEntry game, ScanResult scan)
        {
            var root = ResolveInstallRoot(game.Path);
            if (root is null || scan.Files.Count > MaximumFilesPerGame) return;
            CachedGame snapshot;
            try
            {
                snapshot = BuildEntry(game, root, scan, _utcNow());
            }
            catch
            {
                return;
            }

            lock (_gate)
            {
                EnsureLoaded();
                if (_entries.TryGetValue(snapshot.Key, out var existing) && SameFingerprint(existing, snapshot))
                {
                    existing.UpdatedUtcTicks = snapshot.UpdatedUtcTicks;
                    return;
                }
                _entries[snapshot.Key] = snapshot;
                foreach (var old in _entries.Values
                             .OrderByDescending(entry => entry.UpdatedUtcTicks)
                             .Skip(_maximumEntries)
                             .ToList())
                    _entries.Remove(old.Key);
                TryWriteDisk();
            }
        }

        private static string GameCacheKey(GameEntry game) =>
            game.Store.ToString().ToLowerInvariant() + "|" + game.Id.Trim();

        private static string SidecarFingerprint(string full)
        {
            static string One(string path)
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "-";
            }
            return string.Join('|',
                One(full + SwapperBackupSuffix),
                One(full + BackupSuffix),
                One(full + WrittenSuffix),
                One(full + StaleSuffix));
        }

        private static CachedGame BuildEntry(GameEntry game, string root, ScanResult scan, DateTime now)
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var files = new List<CachedDll>(scan.Files.Count);
            foreach (var item in scan.Files)
            {
                var full = Path.GetFullPath(item.Path);
                if (!IsSafeGamePath(fullRoot, full) ||
                    !IsDestName(item.FileName) ||
                    !string.Equals(Path.GetFileName(full), item.FileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Detected DLL escaped its game root.");
                var info = new FileInfo(full);
                if (!info.Exists) throw new FileNotFoundException("Detected DLL disappeared.", full);
                files.Add(new CachedDll
                {
                    RelativePath = Path.GetRelativePath(fullRoot, full),
                    FullPath = full,
                    FileName = item.FileName,
                    Kind = item.Kind,
                    Length = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    Sidecars = SidecarFingerprint(full),
                    CurrentVersion = item.CurrentVersion,
                    SkipReason = item.SkipReason,
                    Origin = item.Origin,
                    OriginalVersion = item.OriginalVersion,
                    OriginalState = item.OriginalState,
                    MatchedRelease = item.MatchedRelease,
                });
            }
            return new CachedGame
            {
                Key = GameCacheKey(game),
                Root = fullRoot,
                RootLastWriteUtcTicks = Directory.GetLastWriteTimeUtc(fullRoot).Ticks,
                UpdatedUtcTicks = now.ToUniversalTime().Ticks,
                AntiCheat = scan.AntiCheat,
                Files = files.OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }

        private bool EntryMatches(CachedGame entry, string root)
        {
            string fullRoot;
            try { fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
            catch { return false; }
            if (!entry.Root.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(fullRoot) ||
                Directory.GetLastWriteTimeUtc(fullRoot).Ticks != entry.RootLastWriteUtcTicks ||
                entry.Files.Count > MaximumFilesPerGame)
                return false;
            foreach (var file in entry.Files)
            {
                try
                {
                    if (!IsDestName(file.FileName)) return false;
                    var reconstructed = Path.GetFullPath(Path.Combine(fullRoot, file.RelativePath));
                    if (!IsSafeGamePath(fullRoot, reconstructed) ||
                        !reconstructed.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Path.GetFileName(reconstructed), file.FileName, StringComparison.OrdinalIgnoreCase))
                        return false;
                    var info = new FileInfo(reconstructed);
                    if (!info.Exists || info.Length != file.Length ||
                        info.LastWriteTimeUtc.Ticks != file.LastWriteUtcTicks ||
                        SidecarFingerprint(reconstructed) != file.Sidecars)
                        return false;
                }
                catch
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SameFingerprint(CachedGame left, CachedGame right)
        {
            if (!left.Root.Equals(right.Root, StringComparison.OrdinalIgnoreCase) ||
                left.RootLastWriteUtcTicks != right.RootLastWriteUtcTicks ||
                !string.Equals(left.AntiCheat, right.AntiCheat, StringComparison.Ordinal) ||
                left.Files.Count != right.Files.Count)
                return false;
            return left.Files.Zip(right.Files).All(pair =>
                pair.First.FullPath.Equals(pair.Second.FullPath, StringComparison.OrdinalIgnoreCase) &&
                pair.First.Length == pair.Second.Length &&
                pair.First.LastWriteUtcTicks == pair.Second.LastWriteUtcTicks &&
                pair.First.Sidecars == pair.Second.Sidecars &&
                string.Equals(pair.First.Origin, pair.Second.Origin, StringComparison.Ordinal) &&
                string.Equals(pair.First.MatchedRelease, pair.Second.MatchedRelease, StringComparison.Ordinal) &&
                string.Equals(pair.First.CurrentVersion, pair.Second.CurrentVersion, StringComparison.Ordinal));
        }

        private void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var file = new FileInfo(_path);
                if (!file.Exists || file.Length is <= 0 or > MaximumCacheBytes) return;
                var json = File.ReadAllText(_path);
                var envelope = JsonSerializer.Deserialize<CacheEnvelope>(json);
                if (envelope is null || envelope.Schema != SchemaVersion ||
                    !envelope.AppVersion.Equals(_appVersion, StringComparison.Ordinal) ||
                    envelope.Entries.Count > _maximumEntries)
                    return;
                _entries = envelope.Entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                    .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(entry => entry.UpdatedUtcTicks).First())
                    .Take(_maximumEntries)
                    .ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                _entries.Clear();
            }
        }

        private void WriteDisk()
        {
            var envelope = new CacheEnvelope
            {
                Schema = SchemaVersion,
                AppVersion = _appVersion,
                Entries = _entries.Values
                    .OrderByDescending(entry => entry.UpdatedUtcTicks)
                    .Take(_maximumEntries)
                    .ToList(),
            };
            var json = JsonSerializer.Serialize(envelope);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumCacheBytes) return;
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private void TryWriteDisk()
        {
            try { WriteDisk(); }
            catch (Exception ex) { AppLog.Debug("Upscaler status cache write skipped: " + ex.Message); }
        }
    }

}
