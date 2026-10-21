using System.Security.Cryptography;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class AchievementService
{
    internal const int MaxInlineStateBytes = 32 * 1024 * 1024;
    internal const int MaxStatePartBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan TimestampCheckpointInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DeferredSaveInterval = TimeSpan.FromSeconds(60);
    private readonly HashSet<string> _dirtyGameKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _timestampDirtyGameKeys = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _persistenceClock;
    private DateTimeOffset _lastTimestampCheckpoint;
    private DateTimeOffset _lastStateSave;
    private bool _stateDirty;
    private bool _partitioned;
    private string? _stateReadFailure;
    private PartitionManifest? _partitionManifest;
    private string PartsDirectory => _statePath + ".parts";

    private PersistentState LoadState(string path)
    {
        var oversized = false;
        try
        {
            if (!File.Exists(path)) return new PersistentState();
            using var stream = File.OpenRead(path);
            if (stream.Length == 0) return new PersistentState();
            oversized = stream.Length > MaxInlineStateBytes;
            // Reading the envelope skips legacy game payloads without making a
            // second in-memory JSON copy. Oversized valid v1/v2 files are read
            // through a stream, then migrated on the next successful save.
            var manifest = JsonSerializer.Deserialize<PartitionManifest>(stream, JsonOptions);
            PersistentState state;
            if (manifest?.PartitionFormat is not null)
            {
                _partitioned = true;
                if (manifest.PartitionFormat != PartitionManifest.Format || manifest.Version != SchemaVersion ||
                    manifest.GamePartitions is null)
                    throw new InvalidDataException("Achievement partition manifest is unsupported or incomplete.");
                _partitionManifest = manifest;
                state = new PersistentState();
                foreach (var pair in manifest.GamePartitions)
                {
                    try { state.Games[pair.Key] = ReadParts<PersistedGameState>(pair.Value); }
                    catch (Exception ex)
                    {
                        // Keep readable account/game state available. Do not
                        // overwrite a missing/corrupt partition.
                        _stateReadFailure = "Saved achievement partitions require repair before new state can be saved.";
                        AppLog.Debug("Achievement partition load failed: " + ex.GetType().Name);
                    }
                }
            }
            else
            {
                stream.Position = 0;
                state = JsonSerializer.Deserialize<PersistentState>(stream, JsonOptions) ?? new();
                if (oversized)
                {
                    _partitioned = _stateDirty = true;
                    _dirtyGameKeys.UnionWith(state.Games.Keys);
                }
            }
            if (state.Version is < 1 or > SchemaVersion) return new PersistentState();
            state.Version = SchemaVersion;
            state.EnsureComparers();
            foreach (var pair in state.Games.ToArray())
            {
                try
                {
                    var row = pair.Value;
                    if (string.IsNullOrWhiteSpace(row.GameId) || row.LastObservedAtUtc == default)
                        throw new InvalidDataException("Persisted achievement identity is incomplete.");
                    ValidateSnapshot(ToSnapshot(row));
                    if (SnapshotKey(ToSnapshot(row)) != pair.Key)
                        throw new InvalidDataException("Persisted achievement account key does not match its state.");
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                {
                    state.Games.Remove(pair.Key);
                    AppLog.Debug("Discarded invalid achievement state: " + ex.Message);
                }
            }
            return state;
        }
        catch (Exception ex)
        {
            if (_partitioned || oversized)
                _stateReadFailure = "Saved achievement state requires repair before new state can be saved.";
            AppLog.Debug("Achievement state load failed: " + ex.GetType().Name);
            return new PersistentState();
        }
    }

    private void SaveStateAtomic()
    {
        if (_stateReadFailure is not null) throw new InvalidDataException(_stateReadFailure);
        var directory = Path.GetDirectoryName(_statePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Achievement state path has no directory.");
        Directory.CreateDirectory(directory);
        var checkpointDue = _persistenceClock() - _lastTimestampCheckpoint >= TimestampCheckpointInterval;
        if (checkpointDue) _dirtyGameKeys.UnionWith(_timestampDirtyGameKeys);
        if (!_partitioned)
        {
            var temp = WriteJsonTemp(_state);
            try
            {
                if (new FileInfo(temp).Length <= MaxInlineStateBytes)
                {
                    File.Move(temp, _statePath, overwrite: true);
                    _timestampDirtyGameKeys.Clear();
                }
                else
                {
                    _partitioned = true;
                    SavePartitioned();
                }
            }
            finally { TryDelete(temp); }
        }
        else SavePartitioned();
        _timestampDirtyGameKeys.ExceptWith(_dirtyGameKeys);
        _dirtyGameKeys.Clear();
        _stateDirty = false;
        _lastStateSave = _persistenceClock();
        if (checkpointDue) _lastTimestampCheckpoint = _persistenceClock();
    }

    private void SavePartitioned()
    {
        Directory.CreateDirectory(PartsDirectory);
        var previous = _partitionManifest;
        var next = new PartitionManifest
        {
            PartitionFormat = PartitionManifest.Format,
            Version = SchemaVersion,
            GamePartitions = previous?.GamePartitions is { } existing
                ? new(existing, StringComparer.Ordinal) : new(StringComparer.Ordinal),
        };
        foreach (var removed in next.GamePartitions.Keys.Where(key => !_state.Games.ContainsKey(key)).ToArray())
            next.GamePartitions.Remove(removed);
        foreach (var key in _state.Games.Keys.Where(key => !next.GamePartitions.ContainsKey(key) || _dirtyGameKeys.Contains(key)))
            next.GamePartitions[key] = WriteParts(_state.Games[key]);
        var temp = WriteJsonTemp(next);
        try
        {
            if (new FileInfo(temp).Length > MaxInlineStateBytes)
                throw new InvalidDataException("Achievement partition index exceeds the supported bound.");
            // Every game part is flushed before this one commit point.
            // Readers see the old manifest or the new one.
            File.Move(temp, _statePath, overwrite: true);
        }
        finally { TryDelete(temp); }
        _partitionManifest = next;
        // Keep one prior committed generation for overlapping readers. A
        // process crash leaves only unreferenced parts; the next save reclaims
        // them while preserving both current and previous committed parts.
        var keep = ReferencedParts(next).Concat(ReferencedParts(previous)).ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var file in Directory.EnumerateFiles(PartsDirectory, "*.part"))
                if (!keep.Contains(Path.GetFileName(file))) TryDelete(file);
        }
        catch { /* Cleanup never invalidates an already durable commit. */ }
    }

    private string WriteJsonTemp<T>(T value)
    {
        var temp = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.WriteThrough);
            JsonSerializer.Serialize(stream, value, JsonOptions);
            stream.Flush(flushToDisk: true);
            return temp;
        }
        catch { TryDelete(temp); throw; }
    }

    private string[] WriteParts<T>(T value)
    {
        using var writer = new StatePartWriter(PartsDirectory);
        JsonSerializer.Serialize(writer, value, JsonOptions);
        return writer.Complete();
    }

    private T ReadParts<T>(string[] parts)
    {
        using var stream = new StatePartReader(PartsDirectory, parts);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidDataException("Achievement partition is empty.");
    }

    private static IEnumerable<string> ReferencedParts(PartitionManifest? manifest) =>
        manifest?.GamePartitions?.Values.SelectMany(parts => parts) ?? [];

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private sealed class PartitionManifest
    {
        internal const string Format = "game-shards-v1";
        public string? PartitionFormat { get; set; }
        public int Version { get; set; }
        public Dictionary<string, string[]>? GamePartitions { get; set; }
    }

    private sealed class StatePartWriter(string directory) : Stream
    {
        private readonly byte[] _buffer = new byte[MaxStatePartBytes];
        private readonly List<string> _parts = [];
        private int _length;
        private bool _completed;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_completed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> source)
        {
            if (_completed) throw new InvalidOperationException("Achievement partition stream is complete.");
            while (!source.IsEmpty)
            {
                var count = Math.Min(source.Length, _buffer.Length - _length);
                source[..count].CopyTo(_buffer.AsSpan(_length));
                _length += count;
                source = source[count..];
                if (_length == _buffer.Length) FlushPart();
            }
        }
        internal string[] Complete()
        {
            if (!_completed) { FlushPart(); _completed = true; }
            return _parts.ToArray();
        }
        private void FlushPart()
        {
            if (_length == 0) return;
            var hash = SHA256.HashData(_buffer.AsSpan(0, _length));
            var name = Convert.ToHexString(hash).ToLowerInvariant() + ".part";
            var path = Path.Combine(directory, name);
            var exists = false;
            if (File.Exists(path))
            {
                using var prior = File.OpenRead(path);
                exists = prior.Length == _length && SHA256.HashData(prior).AsSpan().SequenceEqual(hash);
            }
            if (!exists)
            {
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        64 * 1024, FileOptions.WriteThrough))
                    {
                        stream.Write(_buffer, 0, _length);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temp, path, overwrite: true);
                }
                finally { TryDelete(temp); }
            }
            _parts.Add(name);
            _length = 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class StatePartReader(string directory, string[] parts) : Stream
    {
        private byte[] _part = [];
        private int _offset;
        private int _index;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> destination)
        {
            if (destination.IsEmpty) return 0;
            while (_offset >= _part.Length)
            {
                if (_index >= parts.Length) return 0;
                var name = parts[_index++];
                if (name.Length != 69 || !name.EndsWith(".part", StringComparison.Ordinal) ||
                    name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef".AsSpan()) >= 0)
                    throw new InvalidDataException("Achievement partition path is invalid.");
                var path = Path.Combine(directory, name);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length is <= 0 or > MaxStatePartBytes)
                    throw new InvalidDataException("Achievement partition size is invalid.");
                _part = File.ReadAllBytes(path);
                if (Convert.ToHexString(SHA256.HashData(_part)).ToLowerInvariant() + ".part" != name)
                    throw new InvalidDataException("Achievement partition checksum does not match.");
                _offset = 0;
            }
            var count = Math.Min(destination.Length, _part.Length - _offset);
            _part.AsSpan(_offset, count).CopyTo(destination);
            _offset += count;
            return count;
        }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
