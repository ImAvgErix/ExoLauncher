using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The newest version of something, looked up the one way every check shares (graphics and chipset drivers, .NET, Lucid,
/// controller firmware; versions compared by <see cref="VersionNumber"/>):
/// - one lookup at a time per key: everyone asking meanwhile shares it, and one caller leaving doesn't stop it;
/// - an answer is kept for its period, on disk too when the check names a file, so a restart doesn't ask again;
/// - an answer older than its period shows at once while it is asked again (<c>fresh</c>, an update, waits for it);
/// - a lookup that fails keeps the last good answer and tries again soon: after 5 minutes, then twice as long each time,
///   up to the period (before, some checks asked again on every showing and others waited a whole day);
/// - a source that says to slow down (a 429 or 503: <see cref="Backoff"/>) is left alone for its Retry-After, an hour
///   when it gives none, a day at most.
/// </summary>
internal sealed class LatestCache<T> where T : class
{
    /// <summary>The first wait after a failed lookup; it doubles with each failure in a row, up to the period.</summary>
    internal static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(5);

    private sealed record Entry(DateTime At, T? Value, DateTime RetryAt, int Failures);
    private sealed record Saved(DateTime At, T? Value);

    private readonly string _name;
    private readonly TimeSpan _period;
    private readonly string? _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Dictionary<string, Task<T?>> _fetching = [];
    private bool _loaded;
    private DateTime _backoffUntil;

    /// <param name="name">What it looks up, for the log ("NVIDIA's driver list").</param>
    /// <param name="file">A file (in Exo's data folder unless a full path) that keeps the answers across restarts, or null.</param>
    internal LatestCache(string name, TimeSpan period, string? file = null)
    {
        _name = name;
        _period = period;
        _path = file is null ? null : Path.IsPathRooted(file) ? file : Path.Combine(PathHelper.AppDataDir, file);
    }

    /// <summary>The newest for <paramref name="key"/>: an answer kept, else the lookup's (null: there is none, or none known yet).</summary>
    internal async Task<T?> GetAsync(string key, Func<CancellationToken, Task<T?>> lookup, CancellationToken ct, bool fresh = false)
    {
        Task<T?>? task;
        Entry? entry;
        lock (_gate)
        {
            Load();
            var now = DateTime.UtcNow;
            var known = _entries.TryGetValue(key, out entry);
            if (now < _backoffUntil) return entry?.Value;
            if (known && (now - entry!.At < _period || now < entry.RetryAt)) return entry.Value;
            if (!_fetching.TryGetValue(key, out task)) _fetching[key] = task = Task.Run(() => RunAsync(key, lookup));
            if (known && entry!.At != DateTime.MinValue && !fresh) return entry.Value;
        }
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The answer kept for <paramref name="key"/>, without asking (null: none yet).</summary>
    internal T? Kept(string key)
    {
        lock (_gate)
        {
            Load();
            return _entries.GetValueOrDefault(key)?.Value;
        }
    }

    /// <summary>Forgets every answer (an update just installed: the next showing asks again).</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            Load();
            _entries.Clear();
            Save();
        }
    }

    /// <summary>The source said to slow down: no lookups until its Retry-After (an hour without one, a day at most).</summary>
    internal void Backoff(TimeSpan? retryAfter)
    {
        var wait = retryAfter is { } after && after > TimeSpan.Zero ? (after < TimeSpan.FromDays(1) ? after : TimeSpan.FromDays(1)) : TimeSpan.FromHours(1);
        lock (_gate) if (DateTime.UtcNow + wait > _backoffUntil) _backoffUntil = DateTime.UtcNow + wait;
        AppLog.Info($"{_name}: the source asked Exo to wait; next look in {wait.TotalMinutes:0} min.");
    }

    /// <summary>A 429 or 503 slows lookups down (<see cref="Backoff"/>); true when it did.</summary>
    internal bool SlowDown(HttpResponseMessage response)
    {
        if (response.StatusCode is not (System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable)) return false;
        Backoff(response.Headers.RetryAfter is { } retry ? retry.Delta ?? retry.Date - DateTimeOffset.UtcNow : null);
        return true;
    }

    private async Task<T?> RunAsync(string key, Func<CancellationToken, Task<T?>> lookup)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var value = await lookup(timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _fetching.Remove(key);
                _entries[key] = new Entry(DateTime.UtcNow, value, default, 0);
                Save();
            }
            return value;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or System.Xml.XmlException
            or IOException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            lock (_gate)
            {
                _fetching.Remove(key);
                var before = _entries.GetValueOrDefault(key);
                var failures = (before?.Failures ?? 0) + 1;
                var wait = TimeSpan.FromTicks(Math.Min(_period.Ticks, FirstRetry.Ticks << Math.Min(failures - 1, 20)));
                _entries[key] = new Entry(before?.At ?? DateTime.MinValue, before?.Value, DateTime.UtcNow + wait, failures);
                AppLog.Info($"{_name} could not be read ({ex.Message}); trying again in {wait.TotalMinutes:0} min.");
                return before?.Value;
            }
        }
    }

    private void Load()
    {
        if (_loaded || _path is null) return;
        _loaded = true;
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(_path)) is { } saved)
                foreach (var (key, kept) in saved) _entries[key] = new Entry(kept.At, kept.Value, default, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            AppLog.Debug($"{_name}: the kept answers are unreadable: {ex.Message}");
        }
    }

    private void Save()
    {
        if (_path is null) return;
        var temp = _path + ".tmp";
        try
        {
            var kept = _entries.Where(e => e.Value.At != DateTime.MinValue).ToDictionary(e => e.Key, e => new Saved(e.Value.At, e.Value.Value));
            File.WriteAllText(temp, JsonSerializer.Serialize(kept));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug($"{_name}: the answers weren't kept: {ex.Message}"); }
    }
}
