using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class GogOwnedLibraryService
{
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(20);
    private sealed record OwnedCheckpoint(int Version, string AccountKey, DateTimeOffset FetchedAt, string[] ProductIds);
    private sealed record RetryCheckpoint(int Version, DateTimeOffset RetryAt);
    private enum GogFailureKind { Unauthorized, RateLimited, Transient, Permanent }
    private sealed class GogLibraryHttpException(GogFailureKind kind, DateTimeOffset? retryAt = null) : Exception
    {
        public GogFailureKind Kind { get; } = kind;
        public bool Unauthorized => Kind == GogFailureKind.Unauthorized;
        public DateTimeOffset? RetryAt { get; } = retryAt;
    }

    private string CheckpointDirectory(string accountKey) => Path.Combine(CachePath + ".work", accountKey);
    private string OwnershipCheckpointPath(string accountKey) => Path.Combine(CheckpointDirectory(accountKey), "ownership.json");
    private string MetadataCheckpointPath(string accountKey, string id) => Path.Combine(CheckpointDirectory(accountKey), "metadata", id + ".json");

    private IReadOnlyList<GalaxyRelease>? ReadOwnershipCheckpoint(string accountKey)
    {
        var saved = ReadCheckpoint<OwnedCheckpoint>(OwnershipCheckpointPath(accountKey), MaxJsonBytes);
        if (saved is not { Version: 1, ProductIds: not null } || saved.AccountKey != accountKey ||
            saved.FetchedAt > _utcNow() || _utcNow() - saved.FetchedAt > _cacheMaxAge ||
            saved.ProductIds.Length > MaxOwnedProducts || saved.ProductIds.Any(id => !IsSafeProductId(id)) ||
            saved.ProductIds.Distinct(StringComparer.Ordinal).Count() != saved.ProductIds.Length) return null;
        // Only proven owned IDs are durable. Per-request library certificates
        // and access tokens are never placed in a metadata checkpoint.
        return saved.ProductIds.Select(id => new GalaxyRelease(id, null)).ToArray();
    }

    private Task WriteOwnershipCheckpointAsync(string accountKey, IReadOnlyList<GalaxyRelease> releases, CancellationToken ct) =>
        WriteCheckpointAsync(OwnershipCheckpointPath(accountKey),
            new OwnedCheckpoint(1, accountKey, _utcNow(), releases.Select(release => release.ExternalId).ToArray()), MaxJsonBytes, ct);

    private async Task MergeMetadataCheckpointsAsync(string accountKey, IReadOnlyList<GalaxyRelease> releases,
        Dictionary<string, CachedGame> previous, CancellationToken ct)
    {
        // Enumerate only current proven IDs; an old or foreign checkpoint can
        // provide neither membership nor entitlement.
        var recovered = new System.Collections.Concurrent.ConcurrentDictionary<string, CachedGame>();
        await Parallel.ForEachAsync(releases, new ParallelOptions { MaxDegreeOfParallelism = MetadataConcurrency, CancellationToken = ct },
            (release, token) =>
            {
                token.ThrowIfCancellationRequested();
                var row = ReadCheckpoint<CachedGame>(MetadataCheckpointPath(accountKey, release.ExternalId), 64 * 1024);
                if (row is not null && row.Id == release.ExternalId && !string.IsNullOrWhiteSpace(row.Title) &&
                    row.MetadataUpdatedUtc is { } at && at <= _utcNow() && _utcNow() - at <= _metadataMaxAge)
                    recovered[row.Id] = row;
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        foreach (var row in recovered.Values)
            if (!previous.TryGetValue(row.Id, out var old) || old.MetadataUpdatedUtc is null || row.MetadataUpdatedUtc > old.MetadataUpdatedUtc)
                previous[row.Id] = row;
    }

    private Task WriteMetadataCheckpointAsync(string accountKey, CachedGame game, CancellationToken ct) =>
        WriteCheckpointAsync(MetadataCheckpointPath(accountKey, game.Id), game, 64 * 1024, ct);

    private void CompleteCheckpoint(string accountKey, IReadOnlyList<GalaxyRelease> releases)
    {
        try { File.Delete(OwnershipCheckpointPath(accountKey)); } catch { }
        try { File.Delete(Path.Combine(CheckpointDirectory(accountKey), "retry.json")); } catch { }
        try
        {
            var owned = releases.Select(release => release.ExternalId).ToHashSet(StringComparer.Ordinal);
            var directory = Path.Combine(CheckpointDirectory(accountKey), "metadata");
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(MaxOwnedProducts * 2))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (IsSafeProductId(id) && !owned.Contains(id)) try { File.Delete(path); } catch { }
            }
        }
        catch { /* Maintenance never invalidates a complete visible snapshot. */ }
    }

    private static T? ReadCheckpoint<T>(string path, int maximum) where T : class
    {
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length is <= 0 || input.Length > maximum) return null;
            using var bounded = new ByteBudgetStream(input, maximum, leaveOpen: true);
            return JsonSerializer.Deserialize<T>(bounded, CacheJsonOptions);
        }
        catch { return null; }
    }

    private static async Task WriteCheckpointAsync<T>(string path, T payload, int maximum, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                using var bounded = new ByteBudgetStream(output, maximum, leaveOpen: true);
                await JsonSerializer.SerializeAsync(bounded, payload, CacheJsonOptions, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private DateTimeOffset? ReadRetryNotBefore(string accountKey) =>
        ReadCheckpoint<RetryCheckpoint>(Path.Combine(CheckpointDirectory(accountKey), "retry.json"), 4096) is { Version: 1 } retry
            ? retry.RetryAt : null;

    private async Task SaveRetryNotBeforeAsync(string accountKey, DateTimeOffset retryAt)
    {
        try
        {
            // Tiny bounded record; authentication material is excluded.
            await WriteCheckpointAsync(Path.Combine(CheckpointDirectory(accountKey), "retry.json"),
                new RetryCheckpoint(1, retryAt), 4096, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* Rate-limit persistence failure must not hide the original failure. */ }
    }

    private async Task<JsonDocument?> ReadRequestJsonAsync(Func<HttpRequestMessage> createRequest,
        CancellationToken ct, bool allowMissing = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RequestBudget);
        var elapsed = Stopwatch.StartNew();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var kind = GogFailureKind.Transient;
            DateTimeOffset? serverRetryAt = null;
            try
            {
                using var request = createRequest();
                if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Only safe GOG reads may be retried.");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return await ReadJsonAsync(response.Content, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new GogLibraryHttpException(GogFailureKind.Unauthorized);
                if (allowMissing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
                kind = response.StatusCode == HttpStatusCode.TooManyRequests ? GogFailureKind.RateLimited
                    : (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout
                        ? GogFailureKind.Transient : GogFailureKind.Permanent;
                if (kind == GogFailureKind.Permanent) throw new GogLibraryHttpException(kind);
                serverRetryAt = response.Headers.RetryAfter?.Date ??
                    (response.Headers.RetryAfter?.Delta is { } delta ? _utcNow() + delta : null);
            }
            catch (HttpRequestException) { }
            catch (IOException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new GogLibraryHttpException(GogFailureKind.Transient); }

            var backoff = TimeSpan.FromMilliseconds((250 << attempt) + Random.Shared.Next(0, 151));
            var wait = serverRetryAt is { } stamp && stamp - _utcNow() > backoff ? stamp - _utcNow() : backoff;
            if (attempt == 2 || wait >= RequestBudget - elapsed.Elapsed)
                throw new GogLibraryHttpException(kind, serverRetryAt ?? (kind == GogFailureKind.RateLimited ? _utcNow() + wait : null));
            try { await _retryDelay(wait, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new GogLibraryHttpException(kind, serverRetryAt); }
        }
        throw new GogLibraryHttpException(GogFailureKind.Transient);
    }
}
