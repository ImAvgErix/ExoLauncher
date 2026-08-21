using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// In-place upscaler replace. Dest names only, and only where ScanGame
/// already saw that exact file. Never add a missing dest. Never FSR 4
/// loader rename or companion inject. Official vendor packs only — no
/// wrapper, no injector. FSR 4 dests need an RDNA 4 GPU. Anti-cheat titles
/// are never written to. NVIDIA / AMD / Intel catalogs stay per dest.
/// The shipped file is captured once as <c>.dlsss</c> and never rewritten,
/// so Restore always hands back what the game came with.
/// </summary>
public sealed class DlssSwapService
{
    internal const string BackupSuffix = ".exo-bak";
    internal const string SwapperBackupSuffix = ".dlsss";
    internal const string SidecarName = ".exo-upscale.json";
    internal const string WrittenSuffix = ".exo-written";
    internal const string StaleSuffix = ".exo-stale";
    internal const string NewSuffix = ".exo-new";
    internal const string PinnedHashSuffix = ".sha256";
    internal const string StoreLockedMessage = "This install is locked by the store.";
    internal const string GameRunningMessage = "Close the game first.";
    internal const string AntiCheatMessage = "This title uses anti-cheat.";
    internal const string NoPackFileMessage = "Exo has no newer file for this.";
    internal const string NotAnUpscalerMessage = "Not an upscaler file.";
    internal const string AlreadyNewestMessage = "Already newest.";
    internal const string KeptNewerMessage = "Kept your newer file.";
    internal const string NoOriginalMessage = "Exo never captured the shipped file.";
    internal const string StaleRestoreMessage = "Restore is unavailable because another app changed this file.";
    private static readonly string[] OriginalBackupSuffixes = [SwapperBackupSuffix, BackupSuffix];
    private const ushort PeMachineAmd64 = 0x8664;
    private const long MaximumZipBytes = 260L * 1024 * 1024;
    private const long MaximumManifestBytes = 8L * 1024 * 1024;
    private const string ManifestUrl = "https://beeradmoore.github.io/dlss-swapper/manifest.json";
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static readonly HttpClient Http = CreateHttp();
    private static readonly ConcurrentDictionary<string, MutationGate> MutationGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ManifestCatalogCache> ManifestCatalogs =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DetectionStatusCache> DetectionCaches =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task> DetectionRefreshes =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AntiCheatPathMarkers =
    [
        "easyanticheat", "battleye", "vanguard", "riot games", "vgk.sys",
        "beservice", "easyanticheat_eos",
    ];

    private static readonly string[] AntiCheatTitleMarkers =
    [
        "fortnite", "valorant", "league of legends", "teamfight tactics",
        "legends of runeterra", "2xko",
    ];

    private static readonly string[] SkipScanFolders =
    [
        "easyanticheat", "battleye", "vanguard", "_commonredist", "redist",
        "__installer", "node_modules", ".git",
        "paks", "movies", "videos", "cinematics", "shadercache", "saved",
        "intermediate", "logs", "crashdumps", ".egstore",
    ];

    private static readonly HashSet<string> SkipScanFolderNames = new(SkipScanFolders, StringComparer.OrdinalIgnoreCase);

    private static readonly PackSpec[] PackSpecs =
    [
        new("dlss", "nvngx_dlss.dll", "DLSS", ManifestKey: "dlss"),
        new("dlss_g", "nvngx_dlssg.dll", "Frame Generation", ManifestKey: "dlss_g"),
        new("dlss_d", "nvngx_dlssd.dll", "Ray Reconstruction", ManifestKey: "dlss_d"),
        new("fsr_31_dx12", "amd_fidelityfx_dx12.dll", "FSR", ManifestKey: "fsr_31_dx12"),
        new("fsr_31_vk", "amd_fidelityfx_vk.dll", "FSR", ManifestKey: "fsr_31_vk"),
        new("fsr_loader", "amd_fidelityfx_loader_dx12.dll", "FSR 4", ManifestKey: null),
        new("fsr_upscaler", "amd_fidelityfx_upscaler_dx12.dll", "FSR 4", ManifestKey: null),
        new("fsr_fg", "amd_fidelityfx_framegeneration_dx12.dll", "FSR FG", ManifestKey: null),
        new("fsr_denoiser", "amd_fidelityfx_denoiser_dx12.dll", "FSR RR", ManifestKey: null),
        new("fsr_radiance", "amd_fidelityfx_radiancecache_dx12.dll", "FSR RC", ManifestKey: null),
        new("xess", "libxess.dll", "XeSS", ManifestKey: "xess"),
        new("xess_dx11", "libxess_dx11.dll", "XeSS", ManifestKey: "xess_dx11"),
        new("xess_fg", "libxess_fg.dll", "XeSS", ManifestKey: "xess_fg"),
        new("xell", "libxell.dll", "XeSS", ManifestKey: "xell"),
    ];

    private const string NoSwappableMessage = "This game has no swappable upscaling files.";
    private const string Fsr31Dx12Name = "amd_fidelityfx_dx12.dll";
    private const string Fsr4LoaderName = "amd_fidelityfx_loader_dx12.dll";
    private static readonly string[] Fsr4RequiredNames =
    [
        Fsr4LoaderName,
        "amd_fidelityfx_upscaler_dx12.dll",
    ];

    public sealed record DetectedDll(
        string Path,
        string FileName,
        string Kind,
        string? GameId,
        string? GameTitle,
        string? CurrentVersion,
        bool Eligible,
        string? SkipReason,
        string? LatestVersion = null,
        bool Present = true,
        bool CanRestore = false,
        string? UnsupportedReason = null,
        string? CurrentDisplayVersion = null,
        string? LatestDisplayVersion = null);

    public sealed record SdkStatus(
        bool Ok,
        string? CachedVersion,
        string? LatestVersion,
        bool AlreadyBest,
        string Message);

    public sealed record StatusResult(
        bool Ok,
        string? LatestVersion,
        IReadOnlyList<DetectedDll> Items,
        string? Message,
        bool AntiCheatWarning = false,
        bool AlreadyBest = false,
        string? LatestDisplayVersion = null);

    public sealed record UpdateResult(
        bool Ok,
        int Updated,
        int Skipped,
        int Failed,
        string? LatestVersion,
        string Message,
        IReadOnlyList<FileOutcome>? Files = null,
        string? LatestDisplayVersion = null);

    /// <summary>
    /// What happened at one destination. One Newest / Restore press covers
    /// every dest, so each row still gets its own honest line.
    /// </summary>
    public sealed record FileOutcome(string FileName, string State, string? Version, string Message, string? DisplayVersion = null);

    private sealed record PackSpec(string Id, string FileName, string Kind, string? ManifestKey);

    private sealed record ManifestRecord(
        string Version,
        ulong VersionNumber,
        string DownloadUrl,
        string? ZipMd5,
        bool IsDevFile,
        string? InternalName);

    private sealed record DlssPack(string Version, IReadOnlyDictionary<string, string> Files);

    private sealed record DownloadedDll(string Name, string Path, string Tag);

    private sealed class UpscaleSidecar
    {
        public int Version { get; set; } = 1;
        public List<string> Injected { get; set; } = [];
    }

    private sealed class MutationGate
    {
        public object Sync { get; } = new();
        public object Lifecycle { get; } = new();
        public int Users { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class MutationLease : IDisposable
    {
        private readonly string _key;
        private MutationGate? _gate;

        public MutationLease(string key, MutationGate gate)
        {
            _key = key;
            _gate = gate;
            Sync = gate.Sync;
        }

        public object Sync { get; }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _gate, null);
            if (current is null) return;
            lock (current.Lifecycle)
            {
                current.Users--;
                if (current.Users != 0) return;
                current.Retired = true;
                ((ICollection<KeyValuePair<string, MutationGate>>)MutationGates)
                    .Remove(new KeyValuePair<string, MutationGate>(_key, current));
            }
        }
    }

    internal sealed class ManifestCatalogCache
    {
        private const int SchemaVersion = 1;
        private const long MaximumCatalogBytes = 8L * 1024 * 1024;
        private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);

        private readonly string _path;
        private readonly Func<CancellationToken, Task<JsonElement>> _fetch;
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _freshness;
        private readonly TimeSpan _refreshTimeout;
        private readonly Action<string>? _log;
        private readonly object _gate = new();
        private CatalogSnapshot? _snapshot;
        private bool _diskRead;
        private Task<CatalogSnapshot?>? _refreshTask;

        private sealed record CatalogSnapshot(DateTime FetchedUtc, JsonElement Manifest);

        internal ManifestCatalogCache(
            string path,
            Func<CancellationToken, Task<JsonElement>> fetch,
            Func<DateTime>? utcNow = null,
            TimeSpan? freshness = null,
            TimeSpan? refreshTimeout = null,
            Action<string>? log = null)
        {
            _path = Path.GetFullPath(path);
            _fetch = fetch;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _freshness = freshness ?? TimeSpan.FromHours(6);
            _refreshTimeout = refreshTimeout ?? TimeSpan.FromSeconds(45);
            _log = log;
        }

        internal async Task<JsonElement?> GetAsync(bool forceRefresh, CancellationToken ct)
        {
            var cached = LoadDiskOnce();
            if (!forceRefresh && cached is not null)
            {
                if (IsFresh(cached))
                    return cached.Manifest.Clone();
                _ = StartRefresh();
                return cached.Manifest.Clone();
            }

            var refreshed = await StartRefresh().WaitAsync(ct).ConfigureAwait(false);
            if (refreshed is not null)
                return refreshed.Manifest.Clone();
            return cached?.Manifest.Clone();
        }

        internal Task WaitForBackgroundRefreshAsync()
        {
            lock (_gate)
                return _refreshTask ?? Task.CompletedTask;
        }

        private bool IsFresh(CatalogSnapshot snapshot)
        {
            var age = _utcNow() - snapshot.FetchedUtc;
            return age >= -MaximumFutureSkew && age <= _freshness;
        }

        private CatalogSnapshot? LoadDiskOnce()
        {
            lock (_gate)
            {
                if (_diskRead) return _snapshot;
                _diskRead = true;
                _snapshot = ReadDiskSnapshot();
                return _snapshot;
            }
        }

        private CatalogSnapshot? ReadDiskSnapshot()
        {
            try
            {
                var file = new FileInfo(_path);
                if (!file.Exists || file.Length is <= 0 or > MaximumCatalogBytes) return null;
                using var stream = File.OpenRead(_path);
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    MaxDepth = 48,
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                });
                var root = document.RootElement;
                if (!root.TryGetProperty("schema", out var schema) ||
                    schema.GetInt32() != SchemaVersion ||
                    !root.TryGetProperty("fetchedUtc", out var fetchedElement) ||
                    fetchedElement.ValueKind != JsonValueKind.String ||
                    !DateTime.TryParse(
                        fetchedElement.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var fetchedUtc) ||
                    fetchedUtc > _utcNow() + MaximumFutureSkew ||
                    !root.TryGetProperty("manifest", out var manifest) ||
                    !IsUsableCatalogManifest(manifest))
                    return null;
                return new CatalogSnapshot(fetchedUtc, manifest.Clone());
            }
            catch (Exception ex)
            {
                _log?.Invoke("Upscaler catalog disk cache ignored: " + ex.Message);
                return null;
            }
        }

        private Task<CatalogSnapshot?> StartRefresh()
        {
            lock (_gate)
            {
                if (_refreshTask is { IsCompleted: false }) return _refreshTask;
                _refreshTask = RefreshCoreAsync();
                return _refreshTask;
            }
        }

        private async Task<CatalogSnapshot?> RefreshCoreAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(_refreshTimeout);
                var manifest = await _fetch(timeout.Token).ConfigureAwait(false);
                if (!IsUsableCatalogManifest(manifest))
                    throw new InvalidDataException("Catalog did not contain a supported trusted record.");
                var snapshot = new CatalogSnapshot(_utcNow(), manifest.Clone());
                WriteDiskSnapshot(snapshot);
                lock (_gate)
                    _snapshot = snapshot;
                return snapshot;
            }
            catch (Exception ex)
            {
                _log?.Invoke("Upscaler catalog refresh failed: " + ex.Message);
                return null;
            }
        }

        private void WriteDiskSnapshot(CatalogSnapshot snapshot)
        {
            var directory = Path.GetDirectoryName(_path)
                            ?? throw new InvalidOperationException("Catalog path has no directory.");
            Directory.CreateDirectory(directory);
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
                    writer.WriteStartObject();
                    writer.WriteNumber("schema", SchemaVersion);
                    writer.WriteString("fetchedUtc", snapshot.FetchedUtc.ToUniversalTime());
                    writer.WritePropertyName("manifest");
                    snapshot.Manifest.WriteTo(writer);
                    writer.WriteEndObject();
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                    if (stream.Length > MaximumCatalogBytes)
                        throw new InvalidDataException("Catalog cache exceeded its size cap.");
                }
                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    internal sealed class DetectionStatusCache
    {
        private const int SchemaVersion = 1;
        private const long MaximumCacheBytes = 4L * 1024 * 1024;
        private const int MaximumFilesPerGame = 32;
        private readonly string _path;
        private readonly string _appVersion;
        private readonly int _maximumEntries;
        private readonly Func<DateTime> _utcNow;
        private readonly object _gate = new();
        private Dictionary<string, CachedGame> _entries = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

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
            public string? CurrentVersion { get; set; }
            public bool Eligible { get; set; }
            public string? SkipReason { get; set; }
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

        internal IReadOnlyList<DetectedDll>? TryGet(GameEntry game)
        {
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
                entry.UpdatedUtcTicks = _utcNow().Ticks;
                return entry.Files.Select(file => new DetectedDll(
                    file.FullPath,
                    file.FileName,
                    file.Kind,
                    game.Id,
                    game.Title,
                    file.CurrentVersion,
                    file.Eligible,
                    file.SkipReason)).ToList();
            }
        }

        internal void Store(GameEntry game, IReadOnlyList<DetectedDll> detected)
        {
            var root = ResolveInstallRoot(game.Path);
            if (root is null || detected.Count > MaximumFilesPerGame) return;
            CachedGame snapshot;
            try
            {
                snapshot = BuildEntry(game, root, detected, _utcNow());
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

        internal int Count
        {
            get
            {
                lock (_gate)
                {
                    EnsureLoaded();
                    return _entries.Count;
                }
            }
        }

        private static string GameCacheKey(GameEntry game) =>
            game.Store.ToString().ToLowerInvariant() + "|" + game.Id.Trim();

        private static CachedGame BuildEntry(
            GameEntry game,
            string root,
            IReadOnlyList<DetectedDll> detected,
            DateTime now)
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var files = new List<CachedDll>(detected.Count);
            foreach (var item in detected)
            {
                var full = Path.GetFullPath(item.Path);
                if (!IsUnderRoot(fullRoot, full) ||
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
                    CurrentVersion = item.CurrentVersion,
                    Eligible = item.Eligible,
                    SkipReason = item.SkipReason,
                });
            }
            return new CachedGame
            {
                Key = GameCacheKey(game),
                Root = fullRoot,
                RootLastWriteUtcTicks = Directory.GetLastWriteTimeUtc(fullRoot).Ticks,
                UpdatedUtcTicks = now.ToUniversalTime().Ticks,
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
                    if (!IsUnderRoot(fullRoot, reconstructed) ||
                        !reconstructed.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Path.GetFileName(reconstructed), file.FileName, StringComparison.OrdinalIgnoreCase))
                        return false;
                    var info = new FileInfo(reconstructed);
                    if (!info.Exists || info.Length != file.Length ||
                        info.LastWriteTimeUtc.Ticks != file.LastWriteUtcTicks)
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
                left.Files.Count != right.Files.Count)
                return false;
            return left.Files.Zip(right.Files).All(pair =>
                pair.First.FullPath.Equals(pair.Second.FullPath, StringComparison.OrdinalIgnoreCase) &&
                pair.First.FileName.Equals(pair.Second.FileName, StringComparison.OrdinalIgnoreCase) &&
                pair.First.Length == pair.Second.Length &&
                pair.First.LastWriteUtcTicks == pair.Second.LastWriteUtcTicks &&
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

    /// <summary>Swapped in tests; production reads the installed display adapters.</summary>
    internal Func<bool> Fsr4Supported { get; init; } = GpuCapability.SupportsFsr4;

    /// <summary>
    /// Swapped in tests; production reads the file's own version resource.
    /// Versions are only ever read off disk, never guessed from a name.
    /// </summary>
    internal Func<string, string?> FileVersion { get; init; } = TryFileVersion;

    private bool SupportsFsr4() => Fsr4Supported();

    public IReadOnlyList<DetectedDll> Detect(IEnumerable<GameEntry> games)
    {
        var found = new Dictionary<string, DetectedDll>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            if (!game.Installed || string.IsNullOrWhiteSpace(game.Path)) continue;
            foreach (var dll in ScanGame(game))
                found[dll.Path] = dll;
        }

        return found.Values
            .OrderBy(item => item.GameTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<StatusResult> GetStatusAsync(
        IEnumerable<GameEntry> games,
        CancellationToken ct,
        bool forceRefresh = false)
    {
        var list = games.ToList();
        var warning = list.Any(game => IsAntiCheatProtected(game, game.Path ?? ""));
        if (warning)
        {
            return new StatusResult(
                true,
                null,
                [],
                AntiCheatMessage,
                AntiCheatWarning: true,
                AlreadyBest: false);
        }

        var items = await Task.Run(
            () => forceRefresh ? DetectFreshForStatus(list).ToList() : DetectForStatus(list).ToList(),
            ct).ConfigureAwait(false);
        var manifest = await GetCachedManifestAsync(ct, refresh: forceRefresh).ConfigureAwait(false);
        var cachedSources = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? CachedSource(string fileName)
        {
            if (cachedSources.TryGetValue(fileName, out var hit)) return hit;
            hit = LatestCachedDll(fileName);
            cachedSources[fileName] = hit;
            return hit;
        }
        var remoteDlss = manifest is { } doc ? PickLatest(doc, "dlss")?.Version : null;
        var cachedDlss = TryFileVersion(CachedSource("nvngx_dlss.dll") ?? string.Empty);
        var sdk = new SdkStatus(true, cachedDlss, remoteDlss, false, "");

        if (list.Count == 1)
            items = WithFullDestCatalog(items, list[0].Id, list[0].Title).ToList();
        items = AttachLatestVersions(
            items,
            ManifestLatestVersions(manifest),
            ManifestLatestDisplayVersions(manifest),
            CachedSource).ToList();
        items = AnnotateRowActions(items, SupportsFsr4()).ToList();
        return new StatusResult(
            true,
            sdk.LatestVersion ?? sdk.CachedVersion,
            items,
            sdk.Message,
            warning,
            IsPackCurrentWithSources(items, CachedSource),
            ManifestLatestDisplayVersions(manifest).Values.FirstOrDefault());
    }

    private IReadOnlyList<DetectedDll> DetectForStatus(IReadOnlyList<GameEntry> games)
    {
        var cache = CurrentDetectionCache();
        var found = new Dictionary<string, DetectedDll>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            if (!game.Installed || string.IsNullOrWhiteSpace(game.Path)) continue;
            var cached = cache.TryGet(game);
            if (cached is not null)
            {
                foreach (var item in cached)
                    found[item.Path] = item;
                QueueDetectionRefresh(cache, game);
                continue;
            }

            var scanned = ScanGame(game).ToList();
            cache.Store(game, scanned);
            foreach (var item in scanned)
                found[item.Path] = item;
        }
        return found.Values
            .OrderBy(item => item.GameTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<DetectedDll> DetectFreshForStatus(IReadOnlyList<GameEntry> games)
    {
        var cache = CurrentDetectionCache();
        var found = new Dictionary<string, DetectedDll>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            if (!game.Installed || string.IsNullOrWhiteSpace(game.Path)) continue;
            var scanned = ScanGame(game).ToList();
            cache.Store(game, scanned);
            foreach (var item in scanned)
                found[item.Path] = item;
        }
        return found.Values
            .OrderBy(item => item.GameTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DetectionStatusCache CurrentDetectionCache()
    {
        var path = Path.GetFullPath(Path.Combine(CacheRoot, "status-v1.json"));
        var appVersion = typeof(DlssSwapService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        return DetectionCaches.GetOrAdd(
            path,
            _ => new DetectionStatusCache(path, appVersion));
    }

    private void QueueDetectionRefresh(DetectionStatusCache cache, GameEntry game)
    {
        var key = Path.GetFullPath(Path.Combine(CacheRoot, "status-v1.json")) + "|" +
                  game.Store.ToString() + "|" + game.Id;
        var task = DetectionRefreshes.GetOrAdd(key, _ => Task.Run(() =>
        {
            try
            {
                var scanned = ScanGame(game).ToList();
                cache.Store(game, scanned);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Upscaler background status refresh failed: " + ex.Message);
            }
        }));
        _ = task.ContinueWith(
            completed => ((ICollection<KeyValuePair<string, Task>>)DetectionRefreshes)
                .Remove(new KeyValuePair<string, Task>(key, completed)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task<SdkStatus> GetSdkStatusAsync(CancellationToken ct)
    {
        var cachedLabel = DescribeCachedPack();
        var cachedDlss = TryCachedDlssVersion();
        var haveDlss = LatestCachedDll("nvngx_dlss.dll") is not null;
        var haveFsr = LatestCachedDll("amd_fidelityfx_dx12.dll") is not null
                      || LatestCachedDll(Fsr4LoaderName) is not null
                      || LatestCachedDll("amd_fidelityfx_upscaler_dx12.dll") is not null;
        var haveXess = LatestCachedDll("libxess.dll") is not null;
        var cachedFsr = TryFileVersion(LatestCachedDll("amd_fidelityfx_upscaler_dx12.dll") ?? "")
                        ?? TryFileVersion(LatestCachedDll(Fsr4LoaderName) ?? "")
                        ?? TryFileVersion(LatestCachedDll("amd_fidelityfx_dx12.dll") ?? "");
        var cachedXess = TryFileVersion(LatestCachedDll("libxess.dll") ?? "");

        string? remoteDlss = null;
        string? remoteFsr = null;
        string? remoteFsrDisplay = null;
        string? remoteXess = null;
        try
        {
            var manifest = await GetCachedManifestAsync(ct).ConfigureAwait(false);
            if (manifest is { } doc)
            {
                remoteDlss = PickLatest(doc, "dlss")?.Version;
                var fsrRecord = PickLatest(doc, "fsr_31_dx12");
                remoteFsr = fsrRecord?.Version;
                remoteFsrDisplay = fsrRecord?.InternalName;
                remoteXess = PickLatest(doc, "xess")?.Version;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Upscaler manifest check failed: " + ex.Message);
        }

        return EvaluateSdkStatus(
            haveDlss,
            haveFsr,
            haveXess,
            cachedDlss,
            cachedFsr,
            cachedXess,
            remoteDlss,
            remoteFsr,
            remoteXess,
            cachedLabel,
            remoteFsrDisplay);
    }

    internal static SdkStatus EvaluateSdkStatus(
        bool haveDlss,
        bool haveFsr,
        bool haveXess,
        string? cachedDlss,
        string? cachedFsr,
        string? cachedXess,
        string? remoteDlss,
        string? remoteFsr,
        string? remoteXess,
        string? cachedLabel,
        string? remoteFsrDisplay = null)
    {
        var haveAny = haveDlss || haveFsr || haveXess;
        var dlssBest = remoteDlss is null
            ? haveDlss
            : VersionsCompatible(cachedDlss, remoteDlss);
        var fsrBest = remoteFsr is null
            ? haveFsr
            : VersionsCompatible(cachedFsr, remoteFsr);
        var xessBest = remoteXess is null
            ? haveXess
            : VersionsCompatible(cachedXess, remoteXess);
        var alreadyBest = haveAny
            && (!haveDlss || dlssBest)
            && (!haveFsr || fsrBest)
            && (!haveXess || xessBest);
        var latestLabel = FormatLatestLabel(
            haveDlss ? remoteDlss : null,
            haveFsr ? remoteFsr : null,
            haveXess ? remoteXess : null,
            cachedLabel,
            remoteFsrDisplay);
        var message = !haveAny
            ? "Download latest."
            : alreadyBest
                ? "Ready."
                : "Refresh for newer.";
        return new SdkStatus(true, cachedDlss, latestLabel, alreadyBest, message);
    }

    internal static string FormatLatestLabel(
        string? remoteDlss,
        string? remoteFsr,
        string? remoteXess,
        string? cachedLabel,
        string? remoteFsrDisplay = null)
    {
        var parts = new List<string>();
        AppendVersionedLabel(parts, "DLSS", remoteDlss);
        AppendVersionedLabel(parts, "FSR", remoteFsrDisplay ?? remoteFsr);
        AppendVersionedLabel(parts, "XeSS", remoteXess);
        if (parts.Count > 0)
            return string.Join(" · ", parts);
        return cachedLabel ?? "";
    }

    private static void AppendVersionedLabel(List<string> parts, string name, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        var trimmed = version.Trim();
        if (trimmed.Equals(name, StringComparison.OrdinalIgnoreCase)) return;
        parts.Add(name + " " + trimmed);
    }

    public Task WarmAsync(CancellationToken ct) => GetCachedManifestAsync(ct);

    public async Task<SdkStatus> EnsureLatestSdkAsync(CancellationToken ct)
    {
        try
        {
            var pack = await EnsureLatestPackAsync(ct, includeFsr4: true, neededFiles: null)
                .ConfigureAwait(false);
            return new SdkStatus(true, pack.Version, pack.Version, true, "Ready.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SdkStatus(false, TryCachedDlssVersion(), null, false, "Could not download upscalers: " + ex.Message);
        }
    }

    public Task<UpdateResult> UpdateGameAsync(GameEntry game, CancellationToken ct) =>
        UpdateAllAsync([game], ct);

    /// <summary>
    /// Newest for every destination the game ships, in one pass: one pack
    /// fetch, one scan, one decision per dest. Destinations already holding
    /// the newest file Exo has are left alone.
    /// </summary>
    public async Task<UpdateResult> UpdateAllAsync(IEnumerable<GameEntry> games, CancellationToken ct)
    {
        var requested = games.ToList();
        // The refusal happens before cancellation, scanning, cache access, or
        // download. A mixed batch is refused as a unit so a protected title
        // can never accidentally share a mutation pass with an allowed game.
        if (requested.Any(game => IsAntiCheatProtected(game, game.Path ?? "")))
            return new UpdateResult(false, 0, 0, 0, null, AntiCheatMessage);

        var targets = requested.Where(HasSwappableFiles).ToList();
        if (targets.Count == 0)
            return new UpdateResult(false, 0, 0, 0, null, NoSwappableMessage);

        var needed = NeededPackFiles(targets.SelectMany(ScanGame));
        var fsr4Only = needed.Count > 0 && needed.All(IsFsr4Dest);
        if (!SupportsFsr4())
            needed = needed.Where(name => !IsFsr4Dest(name)).ToList();
        if (needed.Count == 0)
            return new UpdateResult(
                false, 0, 0, 0, null,
                fsr4Only ? GpuCapability.Fsr4NeedsRdna4 : NoSwappableMessage);

        DlssPack pack;
        try
        {
            pack = await EnsureLatestPackAsync(ct, NeedsFsr4PackFiles(needed), needed).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateResult(false, 0, 0, 0, null, "Could not download upscalers: " + ex.Message);
        }

        var updated = 0;
        var skipped = 0;
        var failed = 0;
        var outcomes = new List<FileOutcome>();
        foreach (var game in targets)
        {
            ct.ThrowIfCancellationRequested();
            var one = ApplyPackToGame(game, pack);
            updated += one.Updated;
            skipped += one.Skipped;
            failed += one.Failed;
            if (one.Files is { } files) outcomes.AddRange(files);
        }

        return new UpdateResult(
            failed == 0,
            updated,
            skipped,
            failed,
            pack.Version,
            SummarizeUpdate(outcomes, updated, failed),
            outcomes);
    }

    private static string SummarizeUpdate(IReadOnlyList<FileOutcome> outcomes, int updated, int failed)
    {
        if (failed > 0) return "Could not update.";
        if (updated > 0) return "Updated.";
        var skips = outcomes.Where(item => item.State == "skipped").ToList();
        if (skips.Count > 0 && skips.All(item => item.Message == AlreadyNewestMessage))
            return "Already newest.";
        return "Nothing to update.";
    }

    /// <summary>
    /// Puts every destination back to the file the game shipped. Refuses a
    /// destination whose shipped copy Exo never captured rather than writing
    /// some other file over it.
    /// </summary>
    public UpdateResult RestoreGame(GameEntry game)
    {
        if (IsStarCitizen(game))
            return new UpdateResult(true, 0, 1, 0, null, "Skipped.");
        if (IsAntiCheatProtected(game, game.Path ?? string.Empty))
            return new UpdateResult(false, 0, 0, 0, null, AntiCheatMessage);

        using var mutation = AcquireMutationGate(game);
        lock (mutation.Sync)
            return RestoreGameLocked(game);
    }

    private UpdateResult RestoreGameLocked(GameEntry game)
    {

        var root = ResolveInstallRoot(game.Path);
        if (root is null)
            return new UpdateResult(false, 0, 0, 0, null, "Install folder not found.");
        if (IsWindowsAppsPath(root))
            return new UpdateResult(false, 0, 0, 1, null, StoreLockedMessage);
        if (IsGameRunning(game, root))
            return new UpdateResult(false, 0, 0, 1, null, GameRunningMessage);

        var restored = 0;
        var removed = 0;
        var failed = 0;
        var restoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<FileOutcome>();
        try
        {
            var backups = ChooseOriginalBackups(root);
            foreach (var dll in ScanGame(game))
            {
                if (backups.ContainsKey(dll.Path) || !File.Exists(dll.Path + WrittenSuffix)) continue;
                failed++;
                outcomes.Add(new FileOutcome(dll.FileName, "failed", dll.CurrentVersion, NoOriginalMessage));
                AppLog.Warn("Upscaler restore refused, no captured original for " + dll.Path);
            }

            foreach (var (dest, backup) in backups)
            {
                var name = Path.GetFileName(dest);
                try
                {
                    InvalidateForeignWrite(dest);
                    if (!File.Exists(backup)) continue;
                    if (!HasValidRestoreClaim(dest))
                    {
                        failed++;
                        outcomes.Add(new FileOutcome(name, "failed", FileVersion(dest), StaleRestoreMessage));
                        AppLog.Warn("Upscaler restore refused stale claim for " + dest);
                        continue;
                    }
                    var factory = PreferFactory(dest);
                    if (factory is null)
                        continue;
                    if (IsWindowsAppsPath(dest) || IsStoreLockedDest(dest))
                    {
                        failed++;
                        outcomes.Add(new FileOutcome(name, "failed", FileVersion(dest), StoreLockedMessage));
                        AppLog.Warn(StoreLockedMessage + " " + dest);
                        continue;
                    }
                    var before = FileVersion(dest);
                    ReplaceExisting(dest, factory);
                    if (!SameFileBytes(dest, factory))
                    {
                        failed++;
                        outcomes.Add(new FileOutcome(name, "failed", before, "Restore did not match the shipped file."));
                        AppLog.Warn("Upscaler restore did not match factory for " + dest);
                        continue;
                    }
                    var shipped = FileVersion(factory);
                    AppLog.Info(
                        $"Upscaler restore: gameId={game.Id}; file={name}; dest={before ?? "—"}; shipped={shipped ?? "—"}.");
                    restoredPaths.Add(dest);
                    TryDelete(dest + WrittenSuffix);
                    restored++;
                    outcomes.Add(new FileOutcome(name, "restored", shipped, "Shipped file put back."));
                }
                catch (Exception ex)
                {
                    failed++;
                    outcomes.Add(new FileOutcome(name, "failed", null, FailMessage(ex)));
                    AppLog.Warn("Upscaler restore failed for " + backup + ": " + FailMessage(ex));
                }
            }

            // Legacy Exo/DLSS-Swapper sidecars can name companions that were
            // added by that same swap. Only clean them after at least one
            // destination had a valid, non-stale restore claim; an orphaned
            // or forged sidecar alone is never authority to delete a DLL.
            if (restoredPaths.Count > 0)
            {
                foreach (var sidecarPath in FindSidecars(root))
                {
                    var sidecar = ReadSidecar(sidecarPath);
                    var folder = Path.GetDirectoryName(sidecarPath) ?? root;
                    if (sidecar is not null)
                    {
                        foreach (var relative in sidecar.Injected)
                        {
                            try
                            {
                                var dest = Path.GetFullPath(Path.Combine(folder, relative));
                                if (!IsUnderRoot(root, dest)) continue;
                                if (restoredPaths.Contains(dest) || HasOriginalBackup(dest)) continue;
                                var name = Path.GetFileName(dest);
                                if (!IsSafeDllName(name) || !File.Exists(dest)) continue;
                                File.Delete(dest);
                                removed++;
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                AppLog.Warn("Upscaler inject remove failed for " + relative + ": " + ex.Message);
                            }
                        }
                    }

                    TryDelete(sidecarPath);
                }
            }

            foreach (var dest in restoredPaths)
                TryDelete(dest + BackupSuffix);
        }
        catch (Exception ex)
        {
            return new UpdateResult(false, restored, 0, failed, null, "Restore failed: " + ex.Message, outcomes);
        }

        var message = failed > 0
            ? outcomes.Any(item => item.Message == NoOriginalMessage)
                ? NoOriginalMessage
                : outcomes.Any(item => item.Message == StaleRestoreMessage)
                    ? StaleRestoreMessage
                : "Could not restore."
            : restored == 0 && removed == 0
                ? "Nothing to restore."
                : "Restored.";
        return new UpdateResult(failed == 0, restored + removed, 0, failed, null, message, outcomes);
    }

    internal static string? LatestVersionFor(string fileName)
    {
        var path = LatestCachedDll(fileName);
        return path is null ? null : TryFileVersion(path);
    }

    internal static string? LatestDisplayVersionFor(string fileName, string? rawVersion = null)
    {
        var path = LatestCachedDll(fileName);
        return DisplayVersionForPath(fileName, path, rawVersion);
    }

    internal static bool IsFsr31File(string fileName) =>
        fileName.Equals(Fsr31Dx12Name, StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("amd_fidelityfx_vk.dll", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDestName(string fileName) =>
        PackSpecs.Any(spec => spec.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<string> DestFileNames { get; } =
        PackSpecs.Select(spec => spec.FileName).ToArray();

    /// <summary>
    /// Overlay catalog: every dest, whether the game ships it or not.
    /// Missing rows stay ineligible so Apply still never injects a dest.
    /// </summary>
    internal static IReadOnlyList<DetectedDll> WithFullDestCatalog(
        IReadOnlyList<DetectedDll> found,
        string? gameId,
        string? gameTitle)
    {
        var present = new Dictionary<string, DetectedDll>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in found)
        {
            if (string.IsNullOrWhiteSpace(item.FileName)) continue;
            present.TryAdd(item.FileName, item);
        }

        return PackSpecs.Select(spec =>
        {
            if (present.TryGetValue(spec.FileName, out var hit))
                return hit;
            return new DetectedDll(
                "",
                spec.FileName,
                ClassifyKind(spec.FileName),
                gameId,
                gameTitle,
                CurrentVersion: null,
                Eligible: false,
                SkipReason: null,
                LatestVersion: null,
                Present: false);
        }).ToList();
    }

    internal static bool IsFsr4Dest(string fileName) => ClassifyKind(fileName) == "FSR 4";

    /// <summary>
    /// Per-row facts the overlay needs: is there a shipped file to put back,
    /// and is this destination unusable on this GPU.
    /// </summary>
    internal static IReadOnlyList<DetectedDll> AnnotateRowActions(
        IReadOnlyList<DetectedDll> items,
        bool fsr4Supported) =>
        items.Select(item => item with
        {
            CanRestore = item.Present && HasValidRestoreClaim(item.Path),
            UnsupportedReason = IsFsr4Dest(item.FileName) && !fsr4Supported
                ? GpuCapability.Fsr4NeedsRdna4
                : null,
        }).ToList();

    internal static bool IsDlssMajor1(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var major = version.Split('.')[0];
        return major == "1";
    }


    internal static bool IsSafeDllName(string fileName) => IsDestName(fileName);

    internal static bool IsValidAmd64Pe(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var len = new FileInfo(path).Length;
            if (len < MinDllBytes(path) || len > 120_000_000) return false;
            using var fs = File.OpenRead(path);
            Span<byte> dos = stackalloc byte[64];
            if (fs.Read(dos) < 64) return false;
            if (dos[0] != (byte)'M' || dos[1] != (byte)'Z') return false;
            var peOff = BitConverter.ToInt32(dos.Slice(0x3C, 4));
            if (peOff < 0 || peOff > len - 6) return false;
            fs.Position = peOff;
            Span<byte> pe = stackalloc byte[6];
            if (fs.Read(pe) < 6) return false;
            if (pe[0] != (byte)'P' || pe[1] != (byte)'E') return false;
            var machine = BitConverter.ToUInt16(pe.Slice(4, 2));
            return machine == PeMachineAmd64;
        }
        catch
        {
            return false;
        }
    }


    internal static bool IsAntiCheatProtected(GameEntry game, string path)
    {
        if (game.Store == StoreKind.Riot) return true;
        var title = game.Title ?? string.Empty;
        if (AntiCheatTitleMarkers.Any(marker =>
                title.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return true;

        var hay = path.Replace('/', '\\');
        if (AntiCheatPathMarkers.Any(marker =>
                hay.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return true;

        try
        {
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (var marker in new[] { "EasyAntiCheat", "BattlEye", "Vanguard" })
            {
                if (Directory.Exists(Path.Combine(dir, marker)))
                    return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    internal static string ClassifyKind(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (name.Equals("nvngx_dlssd.dll", StringComparison.Ordinal)) return "Ray Reconstruction";
        if (name.Equals("nvngx_dlssg.dll", StringComparison.Ordinal)) return "Frame Generation";
        if (name.StartsWith("nvngx_dlss", StringComparison.Ordinal)) return "DLSS";
        if (name.Contains("framegeneration", StringComparison.Ordinal)) return "FSR FG";
        if (name.Contains("denoiser", StringComparison.Ordinal)) return "FSR RR";
        if (name.Contains("radiancecache", StringComparison.Ordinal)) return "FSR RC";
        if (name.Contains("loader", StringComparison.Ordinal) ||
            name.Contains("upscaler", StringComparison.Ordinal))
            return "FSR 4";
        if (name.StartsWith("amd_fidelityfx", StringComparison.Ordinal) ||
            name.StartsWith("ffx_fsr", StringComparison.Ordinal))
            return "FSR";
        if (name.Equals("libxess_fg.dll", StringComparison.Ordinal)) return "XeSS FG";
        if (name.StartsWith("libxell", StringComparison.Ordinal)) return "XeLL";
        if (name.StartsWith("libxess", StringComparison.Ordinal)) return "XeSS";
        return "Other";
    }

    internal static string DisplayName(string kind) => kind switch
    {
        "DLSS" => "DLSS Super Resolution",
        "Frame Generation" => "DLSS Frame Generation",
        "Ray Reconstruction" => "DLSS Ray Reconstruction",
        "FSR" => "FSR 3.1",
        "FSR 4" => "FSR 4",
        "FSR FG" => "FSR Frame Generation",
        "FSR RR" => "FSR Ray Regeneration",
        "FSR RC" => "FSR Radiance Cache",
        "XeSS" => "XeSS",
        "XeSS FG" => "XeSS Frame Generation",
        "XeLL" => "XeLL",
        _ => string.IsNullOrWhiteSpace(kind) ? "Other" : kind,
    };

    internal static IReadOnlyList<DetectedDll> AttachLatestVersions(
        IReadOnlyList<DetectedDll> items,
        IReadOnlyDictionary<string, string>? remoteVersions = null,
        IReadOnlyDictionary<string, string>? remoteDisplayVersions = null,
        Func<string, string?>? cachedSourceForFile = null)
    {
        if (items.Count == 0) return items;
        return items.Select(item =>
        {
            string? remote = null;
            if (remoteVersions is not null &&
                remoteVersions.TryGetValue(item.FileName, out var advertised) &&
                !string.IsNullOrWhiteSpace(advertised))
                remote = advertised;
            // Latest is catalog/cache only. Folding in CurrentVersion made a
            // factory FSR 2.3 look newer than FSR 3.1 1.0.1, so Newest no-op'd.
            var cachedSource = item.Present ? cachedSourceForFile?.Invoke(item.FileName) : null;
            var cachedVersion = !item.Present
                ? null
                : cachedSourceForFile is null
                    ? LatestVersionFor(item.FileName)
                    : TryFileVersion(cachedSource ?? string.Empty);
            var latest = MaxVersionText(remote, cachedVersion);
            var display = remoteDisplayVersions is not null &&
                remoteDisplayVersions.TryGetValue(item.FileName, out var advertisedDisplay) &&
                !string.IsNullOrWhiteSpace(advertisedDisplay)
                ? advertisedDisplay
                : !item.Present
                    ? latest
                    : cachedSourceForFile is null
                    ? LatestDisplayVersionFor(item.FileName, latest)
                    : DisplayVersionForPath(item.FileName, cachedSource, latest);
            var currentDisplay = item.CurrentDisplayVersion;
            if (string.IsNullOrWhiteSpace(currentDisplay) &&
                IsFsr31File(item.FileName) &&
                remote is not null &&
                string.Equals(item.CurrentVersion, remote, StringComparison.OrdinalIgnoreCase))
                currentDisplay = display;
            return item with
            {
                LatestVersion = latest,
                LatestDisplayVersion = display,
                CurrentDisplayVersion = currentDisplay,
            };
        }).ToList();
    }

    internal static bool IsPackCurrent(IEnumerable<DetectedDll> items)
    {
        var present = items
            .Where(item => item.Present && !string.IsNullOrWhiteSpace(item.CurrentVersion))
            .ToList();
        if (present.Count == 0) return false;
        return present.All(item =>
            !string.IsNullOrWhiteSpace(item.LatestVersion) &&
            VersionsCompatible(
                item.CurrentDisplayVersion ?? item.CurrentVersion,
                item.LatestDisplayVersion ?? item.LatestVersion));
    }

    internal static bool IsPackCurrentWithSources(
        IEnumerable<DetectedDll> items,
        Func<string, string?> sourceForFile)
    {
        var present = items
            .Where(item => item.Present && !string.IsNullOrWhiteSpace(item.CurrentVersion))
            .ToList();
        if (present.Count == 0) return false;
        return present.All(item =>
        {
            var current = item.CurrentDisplayVersion ?? item.CurrentVersion;
            var latest = item.LatestDisplayVersion ?? item.LatestVersion;
            var currentVersion = TryParseVersion(current);
            var latestVersion = TryParseVersion(latest);
            if (currentVersion is null || latestVersion is null) return false;
            if (currentVersion > latestVersion) return true;
            if (currentVersion < latestVersion) return false;
            var source = sourceForFile(item.FileName);
            return !string.IsNullOrWhiteSpace(source) && SameFileBytes(item.Path, source);
        });
    }

    internal static bool IsStarCitizen(GameEntry game)
    {
        var title = game.Title ?? "";
        if (title.Contains("star citizen", StringComparison.OrdinalIgnoreCase))
            return true;
        var hay = (game.Path ?? "").Replace('/', '\\');
        return hay.Contains("\\starcitizen\\", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("\\roberts space industries\\", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsWindowsAppsPath(string path)
    {
        var hay = path.Replace('/', '\\');
        return hay.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("\\ModifiableWindowsApps\\", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsXboxGamesPath(string path)
    {
        var hay = path.Replace('/', '\\');
        return hay.Contains("\\XboxGames\\", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsStoreLockedDest(string dest)
    {
        if (IsWindowsAppsPath(dest)) return true;
        if (IsXboxGamesPath(dest) && !ProbeWritable(dest)) return true;
        return false;
    }

    internal static bool IsGameRunning(GameEntry game, string? root = null)
    {
        root ??= ResolveInstallRoot(game.Path);
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var exe = proc.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(exe)) continue;
                    if (Path.GetFullPath(exe).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    /* access denied / exited */
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
            /* process list unavailable */
        }
        return false;
    }

    internal IEnumerable<DetectedDll> ScanGame(GameEntry game)
    {
        if (IsStarCitizen(game))
            yield break;

        var root = ResolveInstallRoot(game.Path);
        if (root is null)
            yield break;

        var files = new List<string>();
        var wanted = new HashSet<string>(PackSpecs.Select(spec => spec.FileName), StringComparer.OrdinalIgnoreCase);
        try
        {
            CollectUpscalerDlls(root, wanted, files, 0);
        }
        catch
        {
            yield break;
        }

        var warned = IsAntiCheatProtected(game, root);
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string full;
            try { full = Path.GetFullPath(file); }
            catch { continue; }

            var name = Path.GetFileName(full);
            if (!IsSafeDllName(name)) continue;
            if (!IsUnderRoot(root, full)) continue;
            // Detection for a protected title is strictly read-only. Even a
            // stale Exo marker is left untouched when anti-cheat is present.
            if (!warned)
                InvalidateForeignWrite(full);

            var kind = ClassifyKind(name);
            var version = FileVersion(full);
            var dlss1 = name.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase) && IsDlssMajor1(version);
            yield return new DetectedDll(
                full,
                name,
                kind,
                game.Id,
                game.Title,
                version,
                Eligible: !dlss1,
                SkipReason: dlss1
                    ? "DLSS 1. Cannot swap."
                    : warned ? "This title uses anti-cheat." : null);
        }
    }

    internal bool HasSwappableFiles(GameEntry game) => !IsStarCitizen(game) && ScanGame(game).Any();

    internal static IReadOnlyCollection<string> NeededPackFiles(IEnumerable<DetectedDll> existing)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in existing)
        {
            if (string.IsNullOrWhiteSpace(dll.FileName) || !IsDestName(dll.FileName))
                continue;
            names.Add(dll.FileName);
        }
        return names;
    }

    internal static bool NeedsFsr4PackFiles(IReadOnlyCollection<string> needed)
    {
        foreach (var name in needed)
        {
            if (name.Equals(Fsr4LoaderName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("amd_fidelityfx_upscaler_dx12.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("amd_fidelityfx_framegeneration_dx12.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("amd_fidelityfx_denoiser_dx12.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("amd_fidelityfx_radiancecache_dx12.dll", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }


    internal static void WriteWithBackup(string destination, string source)
    {
        var dest = Path.GetFullPath(destination);
        if (!File.Exists(dest))
            throw new IOException("Dest was not present.");
        if (IsWindowsAppsPath(dest) || IsStoreLockedDest(dest))
            throw new UnauthorizedAccessException(StoreLockedMessage);
        ClearReadOnly(dest);
        if (!ProbeWritable(dest))
            throw new UnauthorizedAccessException(StoreLockedMessage);
        EnsureFactoryBackup(dest);
        ReplaceExisting(dest, source);
        WriteWrittenHash(dest);
    }

    internal UpdateResult ApplyPackToGame(GameEntry game, IReadOnlyDictionary<string, string> files, string version) =>
        ApplyPackToGame(game, new DlssPack(version, files));

    private UpdateResult ApplyPackToGame(GameEntry game, DlssPack pack)
    {
        if (IsStarCitizen(game))
            return new UpdateResult(true, 0, 1, 0, pack.Version, "Skipped.");

        if (IsAntiCheatProtected(game, game.Path ?? ""))
            return new UpdateResult(false, 0, 0, 0, pack.Version, AntiCheatMessage);

        using var mutation = AcquireMutationGate(game);
        lock (mutation.Sync)
            return ApplyPackToGameLocked(game, pack);
    }

    private UpdateResult ApplyPackToGameLocked(GameEntry game, DlssPack pack)
    {

        var root = ResolveInstallRoot(game.Path);
        if (root is null)
            return new UpdateResult(false, 0, 0, 0, pack.Version, "Install folder not found.");
        if (IsWindowsAppsPath(root))
            return new UpdateResult(false, 0, 0, 1, pack.Version, StoreLockedMessage);
        if (IsGameRunning(game, root))
            return new UpdateResult(false, 0, 0, 1, pack.Version, GameRunningMessage);

        var existing = ScanGame(game).ToList();
        if (existing.Count == 0)
            return new UpdateResult(false, 0, 0, 0, pack.Version, NoSwappableMessage);

        var updated = 0;
        var skipped = 0;
        var failed = 0;
        var outcomes = new List<FileOutcome>();

        void Skip(DetectedDll dll, string reason)
        {
            skipped++;
            outcomes.Add(new FileOutcome(dll.FileName, "skipped", dll.CurrentVersion, reason));
        }

        foreach (var dll in existing)
        {
            if (!dll.Eligible || !IsDestName(dll.FileName))
            {
                Skip(dll, dll.SkipReason ?? NotAnUpscalerMessage);
                continue;
            }

            // FSR 4 carries RDNA 4 shader binaries. Do not put one where it cannot run.
            if (IsFsr4Dest(dll.FileName) && !SupportsFsr4())
            {
                Skip(dll, GpuCapability.Fsr4NeedsRdna4);
                continue;
            }

            if (!TryPackSource(pack, dll.FileName, out var source))
            {
                Skip(dll, NoPackFileMessage);
                continue;
            }

            try
            {
                InvalidateForeignWrite(dll.Path);
                if (IsWindowsAppsPath(dll.Path) || IsStoreLockedDest(dll.Path))
                {
                    failed++;
                    outcomes.Add(new FileOutcome(dll.FileName, "failed", dll.CurrentVersion, StoreLockedMessage));
                    AppLog.Warn(StoreLockedMessage + " " + dll.Path);
                    continue;
                }
                var destVer = FileVersion(dll.Path);
                var srcVer = FileVersion(source);
                var skipReason = SkipApplyReason(
                    dll.FileName,
                    destVer,
                    srcVer,
                    AlreadyCurrent(dll.Path, source));
                if (skipReason is not null)
                {
                    AppLog.Info(
                        $"Upscaler skip: gameId={game.Id}; file={dll.FileName}; dest={destVer ?? "—"}; src={srcVer ?? "—"}; why={skipReason}");
                    skipped++;
                    outcomes.Add(new FileOutcome(dll.FileName, "skipped", destVer, skipReason));
                    continue;
                }

                WriteWithBackup(dll.Path, source);
                if (!AlreadyCurrent(dll.Path, source))
                {
                    RollbackDest(dll.Path);
                    failed++;
                    outcomes.Add(new FileOutcome(dll.FileName, "failed", destVer, "Write did not match the pack file."));
                    AppLog.Warn("Upscaler write did not match pack for " + dll.Path);
                    continue;
                }

                AppLog.Info(
                    $"Upscaler write: gameId={game.Id}; file={dll.FileName}; dest={destVer ?? "—"}; src={srcVer ?? "—"}; sha={ShortSha(source)}.");
                updated++;
                outcomes.Add(new FileOutcome(
                    dll.FileName,
                    "updated",
                    srcVer ?? destVer,
                    "Newest applied.",
                    DisplayVersionForPath(dll.FileName, source, srcVer ?? destVer)));
            }
            catch (Exception ex)
            {
                failed++;
                outcomes.Add(new FileOutcome(dll.FileName, "failed", dll.CurrentVersion, FailMessage(ex)));
                AppLog.Warn("Upscaler write failed for " + dll.Path + ": " + FailMessage(ex));
            }
        }

        var ok = failed == 0 && updated + skipped > 0;
        return new UpdateResult(
            ok,
            updated,
            skipped,
            failed,
            pack.Version,
            SummarizeUpdate(outcomes, updated, failed),
            outcomes);
    }

    private static string MutationKeyFor(GameEntry game)
    {
        var root = ResolveInstallRoot(game.Path);
        return root is null
            ? "id:" + (game.Id ?? string.Empty).Trim()
            : "path:" + Path.TrimEndingDirectorySeparator(root);
    }

    private static MutationLease AcquireMutationGate(GameEntry game)
    {
        var key = MutationKeyFor(game);
        while (true)
        {
            var gate = MutationGates.GetOrAdd(key, static _ => new MutationGate());
            lock (gate.Lifecycle)
            {
                if (gate.Retired) continue;
                gate.Users++;
                return new MutationLease(key, gate);
            }
        }
    }

    internal static bool IsMutationGateRetained(GameEntry game) =>
        MutationGates.ContainsKey(MutationKeyFor(game));

    private static bool TryPackSource(DlssPack pack, string fileName, out string source)
    {
        source = "";
        if (!IsDestName(fileName))
            return false;
        return pack.Files.TryGetValue(fileName, out source!) && File.Exists(source);
    }

    private async Task<DlssPack> EnsureLatestPackAsync(
        CancellationToken ct,
        bool includeFsr4 = false,
        IReadOnlyCollection<string>? neededFiles = null)
    {
        var cacheRoot = CacheRoot;
        Directory.CreateDirectory(cacheRoot);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var needed = neededFiles is null
            ? null
            : new HashSet<string>(neededFiles, StringComparer.OrdinalIgnoreCase);
        JsonElement? manifest = null;
        try
        {
            manifest = await GetCachedManifestAsync(ct, refresh: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("DLSS Swapper manifest failed: " + ex.Message);
        }

        try
        {
            await DownloadOfficialNvidiaAsync(files, needed, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Official NVIDIA DLSS source skipped: " + ex.Message);
        }

        try
        {
            await DownloadOfficialIntelAsync(files, needed, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Official Intel XeSS source skipped: " + ex.Message);
        }

        if (manifest is { } doc)
        {
            foreach (var spec in PackSpecs.Where(item => item.ManifestKey is not null))
            {
                if (needed is not null && !needed.Contains(spec.FileName))
                    continue;
                if (files.ContainsKey(spec.FileName))
                    continue;
                try
                {
                    var record = PickLatest(doc, spec.ManifestKey!);
                    if (record is null) continue;
                    var downloaded = await DownloadManifestDllAsync(spec, record, ct).ConfigureAwait(false);
                    files[downloaded.Name] = downloaded.Path;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Manifest {spec.Id} skipped: " + ex.Message);
                }
            }
        }

        if (includeFsr4)
        {
            try
            {
                await DownloadFsr4Async(files, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("FSR 4 SDK skipped: " + ex.Message);
            }
        }

        FillMissingFromCache(files, needed);
        PreferNewerCached(files);
        if (files.Count == 0)
            throw new InvalidDataException("No upscaler DLLs could be downloaded.");

        var pruneKeep = new List<string>(files.Values);
        if (needed is not null)
        {
            foreach (var spec in PackSpecs)
            {
                var cached = LatestCachedDll(spec.FileName);
                if (cached is not null)
                    pruneKeep.Add(cached);
            }
        }
        PruneOldCache(CacheRoot, pruneKeep);
        var version = DescribePack(files);
        return new DlssPack(version, files);
    }

    private static async Task<JsonElement> FetchManifestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedJsonAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadBoundedJsonAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
            throw new InvalidDataException("Upscaler metadata exceeded its size cap.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(block, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumManifestBytes)
                throw new InvalidDataException("Upscaler metadata exceeded its size cap.");
            await buffer.WriteAsync(block.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        buffer.Position = 0;
        using var doc = await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    private static Task<JsonElement?> GetCachedManifestAsync(CancellationToken ct, bool refresh = false)
    {
        var path = Path.Combine(CacheRoot, "catalog-v1.json");
        var cache = ManifestCatalogs.GetOrAdd(
            Path.GetFullPath(path),
            static catalogPath => new ManifestCatalogCache(
                catalogPath,
                FetchManifestAsync,
                log: AppLog.Debug));
        return cache.GetAsync(refresh, ct);
    }

    internal static bool IsUsableCatalogManifest(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object) return false;
        return PackSpecs
            .Where(spec => spec.ManifestKey is not null)
            .Any(spec => PickLatest(manifest, spec.ManifestKey!) is not null);
    }

    internal static IReadOnlyDictionary<string, string> ManifestLatestVersions(JsonElement? manifest)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (manifest is not { } doc) return map;
        foreach (var spec in PackSpecs.Where(item => item.ManifestKey is not null))
        {
            var record = PickLatest(doc, spec.ManifestKey!);
            if (record is null || string.IsNullOrWhiteSpace(record.Version)) continue;
            map[spec.FileName] = record.Version;
        }
        return map;
    }

    /// <summary>
    /// Human-facing SDK versions are not always the Windows file-resource
    /// version. FSR 3.1 ships as 1.0.x resources but the manifest's internal
    /// names are the meaningful 3.1.x release line.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ManifestLatestDisplayVersions(JsonElement? manifest)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (manifest is not { } doc) return map;
        foreach (var spec in PackSpecs.Where(item => item.ManifestKey is not null))
        {
            var record = PickLatest(doc, spec.ManifestKey!);
            if (record is null || string.IsNullOrWhiteSpace(record.Version)) continue;
            map[spec.FileName] = IsFsr31File(spec.FileName) && !string.IsNullOrWhiteSpace(record.InternalName)
                ? record.InternalName!
                : record.Version;
        }
        return map;
    }

    internal static void PreferNewerCached(IDictionary<string, string> files)
    {
        foreach (var name in files.Keys.ToList())
        {
            var cached = LatestCachedDll(name);
            if (cached is null) continue;
            if (CompareVersions(TryFileVersion(cached), TryFileVersion(files[name])) > 0)
                files[name] = cached;
        }
    }

    internal static void FillMissingFromCache(
        IDictionary<string, string> files,
        IReadOnlyCollection<string>? neededFiles = null)
    {
        var needed = neededFiles is null
            ? null
            : new HashSet<string>(neededFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var spec in PackSpecs)
        {
            if (needed is not null && !needed.Contains(spec.FileName)) continue;
            if (files.ContainsKey(spec.FileName)) continue;
            var cached = LatestCachedDll(spec.FileName);
            if (cached is not null)
                files[spec.FileName] = cached;
        }
    }

    private static ManifestRecord? PickLatest(JsonElement manifest, string key)
    {
        if (!manifest.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        ManifestRecord? best = null;
        foreach (var item in list.EnumerateArray())
        {
            var isDev = item.TryGetProperty("is_dev_file", out var devEl) && devEl.ValueKind == JsonValueKind.True;
            if (isDev) continue;
            var url = item.TryGetProperty("download_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) || !IsAllowedDownloadUrl(url)) continue;
            var version = item.TryGetProperty("version", out var verEl) ? verEl.GetString() ?? "" : "";
            if (version.Length > 32 || TryParseVersion(version) is null) continue;
            ulong number = 0;
            if (item.TryGetProperty("version_number", out var numEl) && numEl.ValueKind == JsonValueKind.Number)
                numEl.TryGetUInt64(out number);
            var md5 = item.TryGetProperty("zip_md5_hash", out var md5El) ? md5El.GetString() : null;
            var internalName = item.TryGetProperty("internal_name", out var inEl) ? inEl.GetString() : null;
            if (internalName is { Length: > 64 }) continue;
            if (key.StartsWith("fsr_31_", StringComparison.OrdinalIgnoreCase))
            {
                var semantic = TryParseVersion(internalName);
                if (semantic is null || semantic.Major != 3 || semantic.Minor != 1)
                    continue;
            }
            var candidate = new ManifestRecord(version, number, url, md5, false, internalName);
            if (best is null || IsBetterRecord(candidate, best, key.StartsWith("fsr_", StringComparison.OrdinalIgnoreCase)))
                best = candidate;
        }

        return best;
    }

    private static async Task<DownloadedDll> DownloadManifestDllAsync(
        PackSpec spec,
        ManifestRecord record,
        CancellationToken ct)
    {
        var tag = string.IsNullOrWhiteSpace(record.InternalName) ? record.Version : record.InternalName;
        var destDir = Path.Combine(CacheRoot, spec.Id, Sanitize(tag));
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, spec.FileName);
        if (IsTrustedReplacementDll(dest, spec.FileName) &&
            CatalogVersionMatchesBinary(TryFileVersion(dest), record.Version))
        {
            AppLog.Info($"Upscaler cache hit: file={spec.FileName}; version={tag}; bytes={new FileInfo(dest).Length}.");
            return new DownloadedDll(spec.FileName, dest, tag);
        }

        AppLog.Info($"Upscaler download: file={spec.FileName}; version={tag}.");
        DeleteCachedReplacement(dest);
        var zipPath = Path.Combine(destDir, "sdk.zip");
        await DownloadToFileAsync(record.DownloadUrl, zipPath, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(record.ZipMd5) && !ZipMd5Matches(zipPath, record.ZipMd5))
        {
            TryDelete(zipPath);
            throw new InvalidDataException(spec.FileName + " zip failed its checksum.");
        }

        ExtractNamedDll(zipPath, spec.FileName, dest);
        TryDelete(zipPath);
        if (!IsTrustedReplacementDll(dest, spec.FileName) ||
            !CatalogVersionMatchesBinary(TryFileVersion(dest), record.Version))
        {
            DeleteCachedReplacement(dest);
            throw new InvalidDataException(spec.FileName + " failed vendor provenance validation.");
        }
        return new DownloadedDll(spec.FileName, dest, tag);
    }

    private static async Task DownloadOfficialNvidiaAsync(
        IDictionary<string, string> files,
        IReadOnlySet<string>? needed,
        CancellationToken ct)
    {
        var wanted = PackSpecs
            .Where(spec => spec.FileName.StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase) &&
                           (needed is null || needed.Contains(spec.FileName)))
            .ToList();
        if (wanted.Count == 0) return;

        using var request = GitHubApiGet("https://api.github.com/repos/NVIDIA/DLSS/commits/main");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var root = await ReadBoundedJsonAsync(response, ct).ConfigureAwait(false);
        var commit = root.TryGetProperty("sha", out var shaElement) ? shaElement.GetString() : null;
        if (commit is not { Length: 40 } || !commit.All(Uri.IsHexDigit))
            throw new InvalidDataException("NVIDIA repository did not return an immutable commit.");

        foreach (var spec in wanted)
        {
            var destDir = Path.Combine(CacheRoot, spec.Id, "official-" + commit[..12]);
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, spec.FileName);
            if (!IsTrustedReplacementDll(dest, spec.FileName))
            {
                DeleteCachedReplacement(dest);
                var url = $"https://raw.githubusercontent.com/NVIDIA/DLSS/{commit}/lib/Windows_x86_64/rel/{spec.FileName}";
                await DownloadToFileAsync(url, dest, ct).ConfigureAwait(false);
            }
            if (!IsTrustedReplacementDll(dest, spec.FileName))
            {
                DeleteCachedReplacement(dest);
                throw new InvalidDataException(spec.FileName + " failed official NVIDIA provenance validation.");
            }
            files[spec.FileName] = dest;
            AppLog.Info($"Upscaler official source: vendor=NVIDIA; file={spec.FileName}; version={TryFileVersion(dest) ?? "—"}.");
        }
    }

    private static async Task DownloadOfficialIntelAsync(
        IDictionary<string, string> files,
        IReadOnlySet<string>? needed,
        CancellationToken ct)
    {
        var wanted = PackSpecs
            .Where(spec => (spec.FileName.StartsWith("libxess", StringComparison.OrdinalIgnoreCase) ||
                            spec.FileName.Equals("libxell.dll", StringComparison.OrdinalIgnoreCase)) &&
                           (needed is null || needed.Contains(spec.FileName)))
            .Select(spec => spec.FileName)
            .ToArray();
        if (wanted.Length == 0) return;

        using var request = GitHubApiGet("https://api.github.com/repos/intel/xess/releases/latest");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var release = await ReadBoundedJsonAsync(response, ct).ConfigureAwait(false);
        var tag = release.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 64)
            throw new InvalidDataException("Intel repository did not return a release tag.");
        var zipUrl = FindPreferredZipUrl(release);
        if (zipUrl is null)
            throw new InvalidDataException("Intel XeSS release has no trusted zip.");

        var destDir = Path.Combine(CacheRoot, "intel-official", Sanitize(tag));
        Directory.CreateDirectory(destDir);
        var allReady = wanted.All(name => IsTrustedReplacementDll(Path.Combine(destDir, name), name));
        if (!allReady)
        {
            foreach (var name in wanted)
                DeleteCachedReplacement(Path.Combine(destDir, name));
            var zipPath = Path.Combine(destDir, "sdk.zip");
            await DownloadToFileAsync(zipUrl, zipPath, ct).ConfigureAwait(false);
            ExtractNamedDlls(zipPath, wanted, destDir);
            TryDelete(zipPath);
        }

        foreach (var name in wanted)
        {
            var dest = Path.Combine(destDir, name);
            if (!IsTrustedReplacementDll(dest, name))
            {
                DeleteCachedReplacement(dest);
                continue;
            }
            files[name] = dest;
            AppLog.Info($"Upscaler official source: vendor=Intel; file={name}; version={TryFileVersion(dest) ?? "—"}.");
        }
        if (!wanted.Any(files.ContainsKey))
            throw new InvalidDataException("Intel XeSS release had no usable DLLs.");
    }

    private static async Task DownloadFsr4Async(Dictionary<string, string> files, CancellationToken ct)
    {
        using var request = GitHubApiGet(
            "https://api.github.com/repos/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/latest");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl)
            ? tagEl.GetString() ?? "latest"
            : "latest";
        var zipUrl = FindPreferredZipUrl(doc.RootElement);
        if (zipUrl is null)
            throw new InvalidDataException("FidelityFX SDK latest release has no zip.");

        var destDir = Path.Combine(CacheRoot, "fsr4", Sanitize(tag));
        Directory.CreateDirectory(destDir);
        var wanted = PackSpecs
            .Where(spec => spec.ManifestKey is null &&
                           spec.FileName.StartsWith("amd_fidelityfx", StringComparison.OrdinalIgnoreCase) &&
                           !spec.FileName.Equals(Fsr31Dx12Name, StringComparison.OrdinalIgnoreCase))
            .Select(spec => spec.FileName)
            .ToArray();
        var requiredReady = Fsr4RequiredNames.All(name =>
            IsTrustedReplacementDll(Path.Combine(destDir, name), name));
        if (requiredReady)
        {
            AppLog.Info($"Upscaler cache hit: family=fsr4; version={tag}.");
            foreach (var name in wanted)
            {
                var dest = Path.Combine(destDir, name);
                if (IsTrustedReplacementDll(dest, name))
                    files[name] = dest;
            }
            return;
        }

        AppLog.Info($"Upscaler download: family=fsr4; version={tag}.");
        foreach (var name in wanted)
            DeleteCachedReplacement(Path.Combine(destDir, name));
        var zipPath = Path.Combine(destDir, "sdk.zip");
        await DownloadToFileAsync(zipUrl, zipPath, ct).ConfigureAwait(false);
        ExtractNamedDlls(zipPath, wanted, destDir);
        TryDelete(zipPath);
        foreach (var name in wanted)
        {
            var dest = Path.Combine(destDir, name);
            if (IsTrustedReplacementDll(dest, name))
                files[name] = dest;
            else
                DeleteCachedReplacement(dest);
        }

        // Present-only: cache whatever same-name dests the zip actually had.
    }

    private static string? FindPreferredZipUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        string? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsAllowedDownloadUrl(url)) continue;
            fallback ??= url;
            if (name.Contains("prebuilt", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("FidelityFX-SDK", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("FidelityFX-Samples", StringComparison.OrdinalIgnoreCase))
                return url;
        }

        return fallback;
    }

    internal static bool IsAllowedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Port is not (443 or -1)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host is "dlss-swapper-downloads.beeradmoore.com" or "beeradmoore.github.io")
            return true;
        if (host is "developer.download.nvidia.com" or "download.nvidia.com")
            return true;
        if (host == "raw.githubusercontent.com")
            return string.IsNullOrEmpty(uri.Query) && IsApprovedOfficialRawPath(uri.AbsolutePath);
        if (host != "github.com") return false;
        return IsApprovedGithubReleasePath(uri.AbsolutePath);
    }

    private static bool IsApprovedOfficialRawPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 7 ||
            !parts[0].Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            !parts[1].Equals("DLSS", StringComparison.OrdinalIgnoreCase) ||
            parts[2].Length != 40 || !parts[2].All(Uri.IsHexDigit) ||
            !parts[3].Equals("lib", StringComparison.OrdinalIgnoreCase) ||
            !parts[4].Equals("Windows_x86_64", StringComparison.OrdinalIgnoreCase) ||
            !parts[5].Equals("rel", StringComparison.OrdinalIgnoreCase))
            return false;
        return parts[6] is "nvngx_dlss.dll" or "nvngx_dlssd.dll" or "nvngx_dlssg.dll";
    }

    internal static bool IsAllowedDownloadRedirect(string initialUrl, string finalUrl)
    {
        if (!Uri.TryCreate(initialUrl, UriKind.Absolute, out var initial) ||
            !Uri.TryCreate(finalUrl, UriKind.Absolute, out var final))
            return false;
        if (!initial.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            initial.Port is not (443 or -1) ||
            !initial.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !IsApprovedGithubReleasePath(initial.AbsolutePath))
            return false;
        if (!final.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            final.Port is not (443 or -1) || !string.IsNullOrEmpty(final.UserInfo))
            return false;
        var assetHost = final.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                        final.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        return assetHost && final.AbsolutePath.StartsWith(
            "/github-production-release-asset",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApprovedGithubReleasePath(string path)
    {
        var approvedRepo = path.StartsWith("/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/", StringComparison.OrdinalIgnoreCase)
                           || path.StartsWith("/NVIDIA/DLSS/", StringComparison.OrdinalIgnoreCase)
                           || path.StartsWith("/NVIDIA-RTX/Streamline/", StringComparison.OrdinalIgnoreCase)
                           || path.StartsWith("/intel/xess/", StringComparison.OrdinalIgnoreCase);
        return approvedRepo && path.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A PE header, HTTPS, and a manifest checksum do not establish who built a
    /// DLL. Every cached/downloaded replacement must carry a trusted Windows
    /// Authenticode signature from the vendor that owns that filename.
    /// </summary>
    internal static bool HasTrustedVendorSignature(string path, string fileName)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        var vendor = ExpectedVendor(fileName);
        if (vendor is null || !WinTrustValid(path)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // WinTrust validates the embedded signature before reading its signer.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var signer = NormalizePublisher(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
            return ExpectedPublisherNames(vendor).Contains(signer, StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? ExpectedVendor(string fileName) => fileName.ToLowerInvariant() switch
    {
        var name when name.StartsWith("nvngx_", StringComparison.Ordinal) => "nvidia",
        var name when name.StartsWith("amd_fidelityfx_", StringComparison.Ordinal) => "amd",
        var name when name.StartsWith("libxess", StringComparison.Ordinal) || name == "libxell.dll" => "intel",
        _ => null,
    };

    private static IReadOnlyList<string> ExpectedPublisherNames(string vendor) => vendor switch
    {
        "nvidia" => ["nvidiacorporation"],
        "amd" => ["advancedmicrodevices"],
        "intel" => ["intelcorporation"],
        _ => [],
    };

    private static string NormalizePublisher(string? value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool HasCompatibleVendorIdentity(string path, string fileName)
    {
        var vendor = ExpectedVendor(fileName);
        if (vendor is null) return false;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return VendorMetadataMatches(fileName, info.OriginalFilename, info.CompanyName);
        }
        catch
        {
            return false;
        }
    }

    internal static bool VendorMetadataMatches(
        string fileName,
        string? originalFilename,
        string? companyName)
    {
        var vendor = ExpectedVendor(fileName);
        if (vendor is null) return false;
        if (!string.IsNullOrWhiteSpace(originalFilename))
        {
            var original = Path.GetFileName(originalFilename);
            var nvidiaBuildNumber = original.StartsWith("CL ", StringComparison.OrdinalIgnoreCase)
                ? original[3..].Trim()
                : string.Empty;
            var nvidiaBuildLabel = vendor == "nvidia" &&
                                   nvidiaBuildNumber.Length > 0 &&
                                   nvidiaBuildNumber.All(char.IsAsciiDigit);
            if (!original.Equals(fileName, StringComparison.OrdinalIgnoreCase) && !nvidiaBuildLabel)
                return false;
        }
        return string.IsNullOrWhiteSpace(companyName) ||
               ExpectedCompanyNames(vendor).Contains(NormalizePublisher(companyName), StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> ExpectedCompanyNames(string vendor) => vendor switch
    {
        "nvidia" => ["nvidia", "nvidiacorporation"],
        "amd" => ["advancedmicrodevices", "advancedmicrodevicesinc"],
        "intel" => ["intelcorporation"],
        _ => [],
    };

    internal static bool ExportsMatchFileName(string fileName, IEnumerable<string> exportNames)
    {
        var exports = new HashSet<string>(exportNames, StringComparer.OrdinalIgnoreCase);
        if (fileName.StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase))
            return exports.Contains("NVSDK_NGX_D3D12_Init") &&
                   exports.Contains("NVSDK_NGX_D3D12_CreateFeature");
        if (fileName.StartsWith("amd_fidelityfx_", StringComparison.OrdinalIgnoreCase))
            return new[] { "ffxCreateContext", "ffxDestroyContext", "ffxConfigure", "ffxQuery", "ffxDispatch" }
                .All(exports.Contains);
        if (fileName.Equals("libxess_fg.dll", StringComparison.OrdinalIgnoreCase))
            return exports.Contains("xefgSwapChainGetVersion") &&
                   exports.Contains("xefgSwapChainD3D12CreateContext");
        if (fileName.Equals("libxell.dll", StringComparison.OrdinalIgnoreCase))
            return exports.Contains("xellGetVersion") && exports.Contains("xellD3D12CreateContext");
        if (fileName.StartsWith("libxess", StringComparison.OrdinalIgnoreCase))
            return exports.Any(name => name.EndsWith("XessGetVersion", StringComparison.OrdinalIgnoreCase)) &&
                   exports.Any(name => name.EndsWith("XessCreateContext", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    internal static bool HasCompatibleExports(string path, string fileName)
    {
        var exports = ReadPeExportNames(path);
        return exports is not null && ExportsMatchFileName(fileName, exports);
    }

    private static IReadOnlyCollection<string>? ReadPeExportNames(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 256) return null;

            stream.Position = 0x3c;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 24) return null;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return null;
            stream.Position = peOffset + 6;
            var sectionCount = reader.ReadUInt16();
            if (sectionCount is 0 or > 96) return null;
            stream.Position = peOffset + 20;
            var optionalSize = reader.ReadUInt16();
            var optionalStart = peOffset + 24L;
            if (optionalSize < 104 || optionalStart + optionalSize > stream.Length) return null;
            stream.Position = optionalStart;
            var magic = reader.ReadUInt16();
            var dataDirectoryOffset = optionalStart + (magic == 0x20b ? 112 : magic == 0x10b ? 96 : -1);
            if (dataDirectoryOffset < optionalStart || dataDirectoryOffset + 8 > optionalStart + optionalSize)
                return null;
            stream.Position = dataDirectoryOffset;
            var exportRva = reader.ReadUInt32();
            if (exportRva == 0) return null;

            var sections = new List<(uint VirtualAddress, uint Span, uint RawPointer)>();
            var sectionStart = optionalStart + optionalSize;
            for (var i = 0; i < sectionCount; i++)
            {
                var header = sectionStart + i * 40L;
                if (header + 40 > stream.Length) return null;
                stream.Position = header + 8;
                var virtualSize = reader.ReadUInt32();
                var virtualAddress = reader.ReadUInt32();
                var rawSize = reader.ReadUInt32();
                var rawPointer = reader.ReadUInt32();
                sections.Add((virtualAddress, Math.Max(virtualSize, rawSize), rawPointer));
            }

            long? OffsetOf(uint rva)
            {
                foreach (var section in sections)
                {
                    var end = (ulong)section.VirtualAddress + section.Span;
                    if (rva < section.VirtualAddress || (ulong)rva >= end) continue;
                    var offset = (ulong)section.RawPointer + (rva - section.VirtualAddress);
                    return offset < (ulong)stream.Length ? (long)offset : null;
                }
                return null;
            }

            var exportOffset = OffsetOf(exportRva);
            if (exportOffset is null || exportOffset.Value + 40 > stream.Length) return null;
            stream.Position = exportOffset.Value + 24;
            var nameCount = reader.ReadUInt32();
            stream.Position = exportOffset.Value + 32;
            var nameTableRva = reader.ReadUInt32();
            if (nameCount is 0 or > 4096) return null;
            var nameTableOffset = OffsetOf(nameTableRva);
            if (nameTableOffset is null || nameTableOffset.Value + nameCount * 4L > stream.Length) return null;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0u; i < nameCount; i++)
            {
                stream.Position = nameTableOffset.Value + i * 4L;
                var nameRva = reader.ReadUInt32();
                var nameOffset = OffsetOf(nameRva);
                if (nameOffset is null) return null;
                stream.Position = nameOffset.Value;
                var bytes = new List<byte>(64);
                while (bytes.Count < 512 && stream.Position < stream.Length)
                {
                    var value = reader.ReadByte();
                    if (value == 0) break;
                    if (value is < 0x20 or > 0x7e) return null;
                    bytes.Add(value);
                }
                if (bytes.Count == 0 || bytes.Count == 512) return null;
                names.Add(System.Text.Encoding.ASCII.GetString(bytes.ToArray()));
            }
            return names;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Authenticode establishes publisher provenance. The exact SHA-256 is
    /// pinned beside the cache entry so a later cache/store write cannot be
    /// mistaken for the same trusted download. Version-resource identity is
    /// also checked when the vendor provides it.
    /// </summary>
    internal static bool IsTrustedReplacementDll(string path, string fileName)
    {
        if (!string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase) ||
            !IsSafeDllName(fileName) ||
            !IsValidAmd64Pe(path) ||
            !HasTrustedVendorSignature(path, fileName) ||
            !HasCompatibleVendorIdentity(path, fileName) ||
            !HasCompatibleExports(path, fileName) ||
            TryParseVersion(TryFileVersion(path)) is null)
            return false;

        var hash = FileSha256(path);
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)) return false;
        var pinPath = path + PinnedHashSuffix;
        var pinned = TryReadText(pinPath)?.Trim();
        if (pinned is not null)
            return IsSha256(pinned) && hash.Equals(pinned, StringComparison.OrdinalIgnoreCase);
        try
        {
            File.WriteAllText(pinPath, hash);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteCachedReplacement(string path)
    {
        TryDelete(path);
        TryDelete(path + PinnedHashSuffix);
    }

    private static bool WinTrustValid(string path)
    {
        var pathPointer = IntPtr.Zero;
        var filePointer = IntPtr.Zero;
        var dataPointer = IntPtr.Zero;
        var actionPointer = IntPtr.Zero;
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUni(path);
            var file = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
            };
            filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = filePointer,
                StateAction = 0, // WTD_STATEACTION_IGNORE
                ProviderFlags = 0x1000, // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            dataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPointer, false);
            actionPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(WinTrustActionGenericVerifyV2, actionPointer, false);
            return WinVerifyTrust(new IntPtr(-1), actionPointer, dataPointer) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (actionPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(actionPointer);
            if (dataPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(dataPointer);
            if (filePointer != IntPtr.Zero) Marshal.FreeCoTaskMem(filePointer);
            if (pathPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WinVerifyTrust(IntPtr window, IntPtr actionId, IntPtr trustData);

    private static bool ZipMd5Matches(string path, string expected)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var hash = Convert.ToHexString(MD5.HashData(fs));
            return hash.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string CacheRoot => Path.Combine(PathHelper.AppDataDir, "dlss");

    internal static void PruneOldCache(string cacheRoot, IEnumerable<string> keepFiles)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot) || !Directory.Exists(cacheRoot)) return;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in keepFiles)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            try { keep.Add(Path.GetFullPath(file)); }
            catch { /* skip bad path */ }
        }
        if (keep.Count == 0) return;

        try
        {
            foreach (var family in Directory.GetDirectories(cacheRoot))
            {
                foreach (var versionDir in Directory.GetDirectories(family))
                {
                    var keepThis = keep.Any(path =>
                        path.StartsWith(versionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || path.Equals(versionDir, StringComparison.OrdinalIgnoreCase));
                    if (keepThis)
                    {
                        foreach (var zip in Directory.EnumerateFiles(versionDir, "*.zip"))
                            TryDelete(zip);
                        continue;
                    }

                    try { Directory.Delete(versionDir, recursive: true); }
                    catch (Exception ex) { AppLog.Debug("Upscaler cache prune skipped " + versionDir + ": " + ex.Message); }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Upscaler cache prune failed: " + ex.Message);
        }
    }

    private static string? LatestCachedDll(string dllName)
    {
        var root = CacheRoot;
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateFiles(root, dllName, SearchOption.AllDirectories)
                .Where(path => dllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? IsTrustedReplacementDll(path, dllName)
                    : File.Exists(path))
                .OrderByDescending(path => CachedSortVersion(path, dllName))
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static Version CachedSortVersion(string path, string dllName)
    {
        if (IsFsr31File(dllName))
        {
            var tag = Path.GetFileName(Path.GetDirectoryName(path));
            if (tag is not null && TryParseVersion(tag) is { } semantic && tag.StartsWith("3.", StringComparison.Ordinal))
                return semantic;
        }
        return SortVersion(TryFileVersion(path));
    }

    private static string? DisplayVersionForPath(string fileName, string? path, string? rawVersion)
    {
        if (path is not null && IsFsr31File(fileName))
        {
            var tag = Path.GetFileName(Path.GetDirectoryName(path));
            if (tag is not null && TryParseVersion(tag) is not null && tag.StartsWith("3.", StringComparison.Ordinal))
                return tag;
        }
        return rawVersion;
    }

    private static string? TryCachedDlssVersion()
    {
        var path = LatestCachedDll("nvngx_dlss.dll");
        return path is null ? null : TryFileVersion(path);
    }

    private static string DescribeCachedPack()
    {
        var parts = new List<string>();
        var dlss = TryFileVersion(LatestCachedDll("nvngx_dlss.dll") ?? "");
        if (!string.IsNullOrWhiteSpace(dlss)) parts.Add("DLSS " + dlss);
        var fsrPath = LatestCachedDll("amd_fidelityfx_upscaler_dx12.dll")
                      ?? LatestCachedDll(Fsr4LoaderName)
                      ?? LatestCachedDll("amd_fidelityfx_dx12.dll");
        var fsr = fsrPath is null ? null : LatestDisplayVersionFor(
            "amd_fidelityfx_dx12.dll", TryFileVersion(fsrPath));
        if (!string.IsNullOrWhiteSpace(fsr)) parts.Add("FSR " + fsr);
        var xess = TryFileVersion(LatestCachedDll("libxess.dll") ?? "");
        if (!string.IsNullOrWhiteSpace(xess)) parts.Add("XeSS " + xess);
        return string.Join(" · ", parts);
    }

    private static string DescribePack(IReadOnlyDictionary<string, string> files)
    {
        var parts = new List<string>();
        if (files.TryGetValue("nvngx_dlss.dll", out var dlss))
        {
            var ver = TryFileVersion(dlss);
            if (!string.IsNullOrWhiteSpace(ver)) parts.Add("DLSS " + ver);
        }
        var fsr4 = files.TryGetValue("amd_fidelityfx_upscaler_dx12.dll", out var upscaler)
            ? upscaler
            : files.TryGetValue(Fsr4LoaderName, out var loader) ? loader : null;
        if (fsr4 is not null)
        {
            var ver = TryFileVersion(fsr4);
            if (!string.IsNullOrWhiteSpace(ver)) parts.Add("FSR " + ver);
        }
        else if (files.TryGetValue("amd_fidelityfx_dx12.dll", out var fsr))
        {
            var ver = TryFileVersion(fsr);
            if (!string.IsNullOrWhiteSpace(ver)) parts.Add("FSR " + ver);
        }
        if (files.TryGetValue("libxess.dll", out var xess))
        {
            var ver = TryFileVersion(xess);
            if (!string.IsNullOrWhiteSpace(ver)) parts.Add("XeSS " + ver);
        }
        return parts.Count == 0 ? "latest" : string.Join(" · ", parts);
    }

    private bool AlreadyCurrent(string dest, string source)
    {
        // Version resources identify a release line, not a byte-for-byte
        // build. Vendor rebuilds can retain the same four-part version.
        return File.Exists(dest) && SameFileBytes(dest, source);
    }

    private static string ShortSha(string path)
    {
        var hash = FileSha256(path);
        return string.IsNullOrWhiteSpace(hash) || hash.Length < 16 ? "—" : hash[..16];
    }

    private static bool SameFileBytes(string left, string right)
    {
        try
        {
            using var a = File.OpenRead(left);
            using var b = File.OpenRead(right);
            if (a.Length != b.Length) return false;
            return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
        }
        catch
        {
            return false;
        }
    }

    private static string? TryFileVersion(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.FileMajorPart != 0 || info.FileMinorPart != 0 || info.FileBuildPart != 0 || info.FilePrivatePart != 0)
                return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            var raw = info.FileVersion?.Replace(',', '.').Replace(" ", "");
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ResolveInstallRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Directory.Exists(path))
                return Path.GetFullPath(path);
            if (File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path);
                return string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(dir);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        try
        {
            var left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            var right = Path.GetFullPath(candidate);
            return right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CollectUpscalerDlls(string dir, HashSet<string> wanted, List<string> files, int depth)
    {
        if (depth > 10) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.dll"))
            {
                if (IsSkippedScanPath(file)) continue;
                var name = Path.GetFileName(file);
                if (name is not null && wanted.Contains(name))
                    files.Add(file);
            }

            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                try
                {
                    var info = new DirectoryInfo(child);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (SkipScanFolderNames.Contains(info.Name)) continue;
                    CollectUpscalerDlls(child, wanted, files, depth + 1);
                }
                catch
                {
                    /* skip one dir */
                }
            }
        }
        catch
        {
            /* skip this dir */
        }
    }

    private static bool IsSkippedScanPath(string path)
    {
        var hay = path.Replace('/', '\\').ToLowerInvariant();
        return SkipScanFolders.Any(folder => hay.Contains("\\" + folder + "\\", StringComparison.Ordinal));
    }

    private static string Sanitize(string tag)
    {
        var safe = string.Concat(tag.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_'));
        return string.IsNullOrWhiteSpace(safe) ? "latest" : safe;
    }

    private static async Task DownloadToFileAsync(string url, string destination, CancellationToken ct)
    {
        if (!IsAllowedDownloadUrl(url))
            throw new InvalidDataException("Upscaler download host is not allowed.");

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var finalUrl = response.RequestMessage?.RequestUri?.ToString();
        if (!string.IsNullOrWhiteSpace(finalUrl) &&
            !IsAllowedDownloadUrl(finalUrl) &&
            !IsAllowedDownloadRedirect(url, finalUrl))
            throw new InvalidDataException("Upscaler download redirected to a host Exo will not use.");
        var length = response.Content.Headers.ContentLength;
        if (length is > MaximumZipBytes)
            throw new InvalidDataException("Upscaler zip is larger than Exo will accept.");

        var temp = destination + ".part";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaximumZipBytes)
                        throw new InvalidDataException("Upscaler zip exceeded the size cap while downloading.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void ExtractNamedDll(string zipPath, string dllName, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries
            .Where(item => item.Name.Equals(dllName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(ScoreZipEntry)
            .FirstOrDefault();
        if (entry is null)
            throw new InvalidDataException($"Zip did not contain {dllName}.");
        if (entry.Length < MinDllBytes(dllName) || entry.Length > 80_000_000)
            throw new InvalidDataException(dllName + " size is outside the accepted range.");
        var folder = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);
        entry.ExtractToFile(destination, overwrite: true);
    }

    private static void ExtractNamedDlls(string zipPath, IReadOnlyList<string> names, string destDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var name in names)
        {
            var entry = zip.Entries
                .Where(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(ScoreZipEntry)
                .FirstOrDefault();
            if (entry is null) continue;
            if (entry.Length < MinDllBytes(name) || entry.Length > 80_000_000) continue;
            var dest = Path.Combine(destDir, name);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static int ScoreZipEntry(ZipArchiveEntry entry)
    {
        var name = entry.FullName.Replace('\\', '/');
        var score = 0;
        if (name.Contains("Windows_x86_64", StringComparison.OrdinalIgnoreCase)) score -= 4;
        if (name.Contains("/rel/", StringComparison.OrdinalIgnoreCase)) score -= 3;
        if (name.Contains("/x64/", StringComparison.OrdinalIgnoreCase)) score -= 2;
        if (name.Contains("/bin/", StringComparison.OrdinalIgnoreCase)) score -= 2;
        if (name.Contains("/release/", StringComparison.OrdinalIgnoreCase)) score -= 2;
        if (name.Contains("/dbg/", StringComparison.OrdinalIgnoreCase)) score += 8;
        if (name.Contains("/debug/", StringComparison.OrdinalIgnoreCase)) score += 8;
        return score;
    }

    private static HttpRequestMessage GitHubApiGet(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ExoLauncher", "1.0"));
        return http;
    }

    private static long MinDllBytes(string pathOrName)
    {
        var name = Path.GetFileName(pathOrName);
        return name.Contains("loader", StringComparison.OrdinalIgnoreCase) ? 4_000 : 12_000;
    }

    private static IEnumerable<string> FindSidecars(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, SidecarName, SearchOption.AllDirectories)
                .Where(path => IsUnderRoot(root, path))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static UpscaleSidecar? ReadSidecar(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<UpscaleSidecar>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The file on disk is at least what the catalog advertises. Text compares
    /// used to answer this and got 310.40 vs 310.4 backwards.
    /// </summary>
    internal static bool VersionsCompatible(string? cached, string? remote)
    {
        if (string.IsNullOrWhiteSpace(cached) || string.IsNullOrWhiteSpace(remote)) return false;
        if (cached.Equals(remote, StringComparison.OrdinalIgnoreCase)) return true;
        var a = TryParseVersion(cached);
        var b = TryParseVersion(remote);
        return a is not null && b is not null && a >= b;
    }

    /// <summary>
    /// True only when both versions read cleanly and the file on disk is the
    /// same or newer. An unreadable version never claims a downgrade.
    /// </summary>
    internal static bool ShouldSkipAsNewerOrEqual(string? destVersion, string? sourceVersion)
    {
        var dest = TryParseVersion(destVersion);
        var source = TryParseVersion(sourceVersion);
        return dest is not null && source is not null && dest >= source;
    }

    /// <summary>
    /// Null when the write may run, otherwise why it must not. Exo does not
    /// rewrite a destination that already holds this version, and does not put
    /// an older vendor file over a newer one — matching the captured shipped
    /// bytes is a restore point, never a licence to downgrade.
    /// </summary>
    internal static string? SkipApplyReason(string? destVersion, string? sourceVersion, bool alreadyCurrent)
    {
        if (alreadyCurrent) return AlreadyNewestMessage;
        var dest = TryParseVersion(destVersion);
        var source = TryParseVersion(sourceVersion);
        return dest is not null && source is not null && dest > source
            ? KeptNewerMessage
            : null;
    }

    /// <summary>
    /// FSR 3.1 keeps a 1.0.x Windows resource version while older game FSR
    /// files commonly report 2.0.x. A raw numeric comparison therefore
    /// incorrectly kept FSR 2.0 over the 3.1 pack. Treat that exact family
    /// transition as an upgrade while retaining raw build comparison within
    /// the same family.
    /// </summary>
    internal static string? SkipApplyReason(
        string fileName,
        string? destVersion,
        string? sourceVersion,
        bool alreadyCurrent)
    {
        if (alreadyCurrent) return AlreadyNewestMessage;
        if (!IsFsr31File(fileName))
            return SkipApplyReason(destVersion, sourceVersion, false);

        var dest = TryParseVersion(destVersion);
        var source = TryParseVersion(sourceVersion);
        if (dest is null || source is null) return null;
        if (dest.Major >= 2 && source.Major == 1) return null;
        if (dest.Major == 1 && source.Major >= 2) return KeptNewerMessage;
        return CompareVersions(destVersion, sourceVersion) > 0
            ? KeptNewerMessage
            : null;
    }

    internal static string? MaxVersionText(params string?[] versions)
    {
        string? best = null;
        foreach (var version in versions)
        {
            if (string.IsNullOrWhiteSpace(version)) continue;
            if (best is null)
            {
                best = version;
                continue;
            }
            var cmp = CompareVersions(version, best);
            if (cmp > 0 || (cmp == 0 && VersionPartCount(version) > VersionPartCount(best)))
                best = version;
        }
        return best;
    }

    private static int VersionPartCount(string value) =>
        value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    internal static int CompareVersions(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right)) return 0;
        if (string.IsNullOrWhiteSpace(left)) return -1;
        if (string.IsNullOrWhiteSpace(right)) return 1;
        return SortVersion(left).CompareTo(SortVersion(right));
    }

    /// <summary>
    /// Four numeric parts, or null when Exo cannot read the text as a version.
    /// Vendor shapes differ hard — DLSS 310.4.0.0, FSR 1.0.1.41314, XeSS
    /// 2.3.0.2740 — so every part is compared as a number. Any non-numeric
    /// part makes the whole thing unreadable rather than silently zero.
    /// </summary>
    internal static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4 || parts.Any(string.IsNullOrEmpty)) return null;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var part))
                return null;
            numbers[i] = part;
        }
        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    internal static bool CatalogVersionMatchesBinary(string? binaryVersion, string? catalogVersion)
    {
        var binary = TryParseVersion(binaryVersion);
        var catalog = TryParseVersion(catalogVersion);
        return binary is not null && catalog is not null && binary == catalog;
    }

    private static Version SortVersion(string? value) => TryParseVersion(value) ?? new Version(0, 0, 0, 0);

    private static IReadOnlyDictionary<string, string> ChooseOriginalBackups(string root)
    {
        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var backup in EnumerateOriginalBackups(root))
        {
            var dest = TryStripBackupSuffix(backup);
            if (dest is null) continue;
            var name = Path.GetFileName(dest);
            if (!IsSafeDllName(name)) continue;
            if (!IsUnderRoot(root, dest)) continue;
            if (!chosen.TryGetValue(dest, out var current) || IsPreferredBackup(backup, current))
                chosen[dest] = backup;
        }
        return chosen;
    }

    private static IEnumerable<string> EnumerateOriginalBackups(string root)
    {
        var found = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var suffix in OriginalBackupSuffixes)
        {
            try
            {
                found.AddRange(Directory.EnumerateFiles(root, "*" + suffix, options));
            }
            catch
            {
                /* skip one suffix if a folder is locked */
            }
        }
        return found;
    }

    private static string? TryStripBackupSuffix(string path)
    {
        foreach (var suffix in OriginalBackupSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return path[..^suffix.Length];
        }
        return null;
    }

    private static bool IsPreferredBackup(string candidate, string existing)
    {
        var candidateSwapper = candidate.EndsWith(SwapperBackupSuffix, StringComparison.OrdinalIgnoreCase);
        var existingSwapper = existing.EndsWith(SwapperBackupSuffix, StringComparison.OrdinalIgnoreCase);
        return candidateSwapper && !existingSwapper;
    }

    private static bool HasOriginalBackup(string dest) =>
        File.Exists(dest + SwapperBackupSuffix) || File.Exists(dest + BackupSuffix);

    /// <summary>
    /// Restore is available for a captured original unless Exo has positively
    /// observed another writer replace the live bytes. Legacy DLSS-Swapper
    /// backups have no Exo hash marker, so absence of a marker alone is not
    /// treated as foreign; the explicit stale marker carries that fact across
    /// refreshes and restarts.
    /// </summary>
    internal static bool HasValidRestoreClaim(string dest)
    {
        if (!File.Exists(dest) || !HasOriginalBackup(dest) || File.Exists(dest + StaleSuffix))
            return false;
        var writtenPath = dest + WrittenSuffix;
        if (!File.Exists(writtenPath))
            return true;
        var live = FileSha256(dest);
        var recorded = TryReadText(writtenPath)?.Trim();
        return IsSha256(recorded) && live is not null && live.Equals(recorded, StringComparison.OrdinalIgnoreCase);
    }


    private static bool IsBetterRecord(ManifestRecord candidate, ManifestRecord best, bool preferInternalName)
    {
        if (preferInternalName)
        {
            var cmp = CompareVersionText(candidate.InternalName, best.InternalName);
            if (cmp != 0) return cmp > 0;
        }
        return candidate.VersionNumber > best.VersionNumber;
    }

    private static int CompareVersionText(string? left, string? right) =>
        SortVersion(left).CompareTo(SortVersion(right));

    /// <summary>
    /// Captures the shipped file before the first swap and never touches that
    /// copy again. Runs before every write, so Restore always has the original.
    /// </summary>
    private static void EnsureFactoryBackup(string dest)
    {
        var factory = dest + SwapperBackupSuffix;
        var backup = dest + BackupSuffix;
        if (!File.Exists(factory))
        {
            // An older Exo build kept the shipped copy in .exo-bak alone. Promote
            // that, never the live file, which may already be an Exo write.
            if (File.Exists(backup))
                File.Copy(backup, factory, overwrite: false);
            else if (File.Exists(dest))
                File.Copy(dest, factory, overwrite: false);
        }
        if (!File.Exists(backup) && File.Exists(factory))
            File.Copy(factory, backup, overwrite: false);
    }

    private static string? PreferFactory(string dest)
    {
        var factory = dest + SwapperBackupSuffix;
        if (File.Exists(factory)) return factory;
        var backup = dest + BackupSuffix;
        return File.Exists(backup) ? backup : null;
    }

    private static void RollbackDest(string dest)
    {
        var factory = PreferFactory(dest);
        if (factory is null) return;
        try { ReplaceExisting(dest, factory); }
        catch (Exception ex) { AppLog.Warn("Upscaler rollback failed for " + dest + ": " + ex.Message); }
    }

    /// <summary>
    /// Something other than Exo wrote this destination, so Exo drops its claim
    /// on the live file. The captured shipped copy is left alone — it is the
    /// only thing Restore can honestly put back.
    /// </summary>
    internal static void InvalidateForeignWrite(string dest)
    {
        var writtenPath = dest + WrittenSuffix;
        if (!File.Exists(dest) || !File.Exists(writtenPath)) return;
        var live = FileSha256(dest);
        var recorded = TryReadText(writtenPath);
        if (string.IsNullOrWhiteSpace(live) || string.IsNullOrWhiteSpace(recorded)) return;
        if (live.Equals(recorded.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            File.WriteAllText(dest + StaleSuffix, live);
            TryDelete(writtenPath);
        }
        catch (Exception ex) { AppLog.Debug("Upscaler stale marker failed for " + dest + ": " + ex.Message); }
    }

    private static void WriteWrittenHash(string dest)
    {
        var hash = FileSha256(dest);
        if (string.IsNullOrWhiteSpace(hash)) return;
        File.WriteAllText(dest + WrittenSuffix, hash);
        TryDelete(dest + StaleSuffix);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string? FileSha256(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
            /* best-effort */
        }
    }

    internal static bool ProbeWritable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows())
            return NativeProbeWritable(path);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void ReplaceExisting(string dest, string source)
    {
        var staged = dest + NewSuffix;
        try
        {
            File.Copy(source, staged, overwrite: true);
            using (var fs = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                fs.Flush(flushToDisk: true);
            ClearReadOnly(dest);
            if (OperatingSystem.IsWindows())
            {
                if (!NativeReplaceFile(dest, staged))
                    File.Replace(staged, dest, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                try
                {
                    File.Replace(staged, dest, destinationBackupFileName: null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(staged, dest, overwrite: true);
                }
            }
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new IOException(GameRunningMessage, ex);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(StoreLockedMessage);
        }
        finally
        {
            TryDelete(staged);
        }
    }

    private static string FailMessage(Exception ex)
    {
        if (IsSharingViolation(ex) || ex.Message.Contains("Close the game", StringComparison.OrdinalIgnoreCase))
            return GameRunningMessage;
        if (ex is UnauthorizedAccessException || IsAccessDenied(ex))
            return StoreLockedMessage;
        return ex.Message;
    }

    private static bool IsSharingViolation(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is IOException io && (io.HResult & 0xFFFF) is 32 or 0x20)
                return true;
            if (cur.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsAccessDenied(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is UnauthorizedAccessException) return true;
            if (cur is IOException io && (io.HResult & 0xFFFF) == 5) return true;
        }
        return false;
    }

    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;

    private static bool NativeProbeWritable(string path)
    {
        var handle = CreateFileW(path, GenericWrite, FileShareRead, 0, OpenExisting, 0, 0);
        if (handle == nint.Zero || handle == new nint(-1))
            return false;
        CloseHandle(handle);
        return true;
    }

    private static bool NativeReplaceFile(string dest, string staged) =>
        ReplaceFileW(dest, staged, null, 0, 0, 0);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReplaceFileW(
        string lpReplacedFileName,
        string lpReplacementFileName,
        string? lpBackupFileName,
        uint dwReplaceFlags,
        nint lpExclude,
        nint lpReserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
