using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed record GogOwnedLibrarySyncResult(
    bool Ok,
    bool Updated,
    int GameCount,
    bool Unauthorized,
    string Message);

/// <summary>
/// Synchronizes the authenticated GOG Galaxy library into Exo's local cache.
/// Normal library scans only read that cache; network work is explicit or
/// scheduled in the background by <see cref="Adapters.GogAdapter"/>.
/// </summary>
public sealed class GogOwnedLibraryService : IDisposable
{
    private const int MaxPages = 200;
    private const int MaxJsonBytes = 8 * 1024 * 1024;
    private const int MetadataConcurrency = 6;
    private static readonly TimeSpan DefaultCacheMaxAge = TimeSpan.FromHours(6);
    private static readonly TimeSpan DefaultMetadataMaxAge = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _cacheMaxAge;
    private readonly TimeSpan _metadataMaxAge;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string[] _lastVisibleProductIds = [];
    private bool _disposed;

    public GogOwnedLibraryService(
        HttpClient? http = null,
        string? cachePath = null,
        TimeSpan? cacheMaxAge = null,
        TimeSpan? metadataMaxAge = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttp = http is null;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _cacheMaxAge = cacheMaxAge ?? DefaultCacheMaxAge;
        _metadataMaxAge = metadataMaxAge ?? DefaultMetadataMaxAge;
        CachePath = Path.GetFullPath(cachePath ?? Path.Combine(PathHelper.AppDataDir, "gog-owned.json"));
    }

    public string CachePath { get; }
    public IReadOnlyList<string> LastVisibleProductIds => Volatile.Read(ref _lastVisibleProductIds);

    /// <summary>Raised only after the complete replacement cache is durable.</summary>
    public event Action? CacheUpdated;

    public bool IsCacheFresh(string userId)
    {
        try
        {
            if (!TryAccountKey(userId, out var accountKey) ||
                !File.Exists(CachePath) ||
                !CacheBelongsTo(accountKey))
                return false;
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(CachePath), TimeSpan.Zero);
            return _utcNow() - modified <= _cacheMaxAge;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns only the cache owned by the supplied authenticated GOG
    /// account. The raw account id is neither persisted nor returned.</summary>
    public IReadOnlyList<GogdlCli.OwnedGame> LoadCachedOwnedGames(string userId)
    {
        if (!TryAccountKey(userId, out var accountKey)) return [];
        return LoadCachedGames(accountKey)
            .Where(game => game.Visible)
            .Select(game => new GogdlCli.OwnedGame(game.Id, game.Title, null, false, game.CoverUrl))
            .ToArray();
    }

    public async Task<GogOwnedLibrarySyncResult> RefreshAsync(
        GogdlCli.AuthCredentials credentials,
        bool force = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CredentialsAreSafe(credentials))
            return Failure("GOG credentials are incomplete.");
        var accountKey = AccountKeyForUser(credentials.UserId);

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && IsCacheFresh(credentials.UserId))
            {
                var cachedCount = LoadCachedGames(accountKey).Count(game => game.Visible);
                return new GogOwnedLibrarySyncResult(
                    true, false, cachedCount, false, "GOG library cache is current.");
            }

            var previous = LoadCachedGames(accountKey)
                .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var releases = await FetchOwnedReleasesAsync(credentials, ct).ConfigureAwait(false);
            var resolved = await ResolveMetadataAsync(releases, previous, credentials, ct).ConfigureAwait(false);
            await WriteCacheAtomicallyAsync(accountKey, resolved, ct).ConfigureAwait(false);
            var visibleProductIds = resolved.Where(game => game.Visible).Select(game => game.Id).ToArray();
            Volatile.Write(ref _lastVisibleProductIds, visibleProductIds);
            var visibleCount = visibleProductIds.Length;

            try { CacheUpdated?.Invoke(); }
            catch { /* a consumer refresh must not turn a durable sync into failure */ }

            return new GogOwnedLibrarySyncResult(
                true,
                true,
                visibleCount,
                false,
                $"Synced {visibleCount} GOG games.");
        }
        catch (GogLibraryHttpException ex)
        {
            return new GogOwnedLibrarySyncResult(
                false,
                false,
                0,
                ex.Unauthorized,
                ex.Unauthorized
                    ? "GOG session expired. Reconnect GOG and try again."
                    : "GOG library is temporarily unavailable.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure("GOG library sync timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"GOG owned-library sync failed ({ex.GetType().Name}).");
            return Failure("GOG library sync failed. Your previous library was kept.");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<IReadOnlyList<GalaxyRelease>> FetchOwnedReleasesAsync(
        GogdlCli.AuthCredentials credentials,
        CancellationToken ct)
    {
        var releases = new Dictionary<string, GalaxyRelease>(StringComparer.OrdinalIgnoreCase);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        int? declaredTotal = null;
        var sawAnyItem = false;

        for (var page = 0; page < MaxPages; page++)
        {
            var uri = new UriBuilder(
                "https",
                "galaxy-library.gog.com",
                -1,
                $"users/{Uri.EscapeDataString(credentials.UserId)}/releases");
            if (!string.IsNullOrWhiteSpace(pageToken))
                uri.Query = "page_token=" + Uri.EscapeDataString(pageToken);

            using var request = CreateAuthorizedRequest(uri.Uri, credentials.AccessToken);
            using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct)
                .ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response.Content, ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("GOG library response has no items array.");
            if (page == 0 && root.TryGetProperty("total_count", out var total) &&
                total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out var count) && count >= 0)
                declaredTotal = count;

            foreach (var item in items.EnumerateArray())
            {
                sawAnyItem = true;
                if (item.ValueKind != JsonValueKind.Object) continue;
                var platform = ReadString(item, "platform_id", "platformId");
                var externalId = ReadStringOrNumber(item, "external_id", "externalId");
                var owned = !TryReadBoolean(item, "owned", out var isOwned) || isOwned;
                if (!owned ||
                    !string.Equals(platform, "gog", StringComparison.OrdinalIgnoreCase) ||
                    !IsSafeProductId(externalId))
                    continue;

                var certificate = ReadString(item, "certificate");
                if (!IsSafeHeaderValue(certificate, 16_384)) certificate = null;
                releases[externalId!] = new GalaxyRelease(externalId!, certificate);
            }

            pageToken = ReadString(root, "next_page_token", "nextPageToken");
            if (string.IsNullOrWhiteSpace(pageToken))
            {
                if (declaredTotal is > 0 && !sawAnyItem)
                    throw new InvalidDataException("GOG returned an incomplete empty library page.");
                return releases.Values.ToArray();
            }
            if (pageToken.Length > 4096 || !seenTokens.Add(pageToken))
                throw new InvalidDataException("GOG library returned an invalid page token.");
        }

        throw new InvalidDataException("GOG library pagination exceeded its safety limit.");
    }

    private async Task<IReadOnlyList<CachedGame>> ResolveMetadataAsync(
        IReadOnlyList<GalaxyRelease> releases,
        IReadOnlyDictionary<string, CachedGame> previous,
        GogdlCli.AuthCredentials credentials,
        CancellationToken ct)
    {
        var now = _utcNow();
        var resolved = new ConcurrentDictionary<string, CachedGame>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<GalaxyRelease>();

        foreach (var release in releases)
        {
            if (previous.TryGetValue(release.ExternalId, out var cached) &&
                cached.MetadataUpdatedUtc is { } updated &&
                now - updated <= _metadataMaxAge)
                resolved[release.ExternalId] = cached;
            else
                pending.Add(release);
        }

        await Parallel.ForEachAsync(
                pending,
                new ParallelOptions { MaxDegreeOfParallelism = MetadataConcurrency, CancellationToken = ct },
                async (release, token) =>
                {
                    try
                    {
                        var metadata = await FetchMetadataAsync(release, credentials, token).ConfigureAwait(false);
                        if (metadata is not null)
                        {
                            resolved[release.ExternalId] = new CachedGame(
                                release.ExternalId,
                                metadata.Title,
                                metadata.CoverUrl,
                                now,
                                metadata.Visible);
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // A single bad metadata record must not discard the owned library.
                    }

                    resolved[release.ExternalId] = previous.TryGetValue(release.ExternalId, out var old)
                        ? old
                        : new CachedGame(release.ExternalId, $"GOG game {release.ExternalId}", null, null, true);
                })
            .ConfigureAwait(false);

        return releases
            .Select(release => resolved[release.ExternalId])
            .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<GameMetadata?> FetchMetadataAsync(
        GalaxyRelease release,
        GogdlCli.AuthCredentials credentials,
        CancellationToken ct)
    {
        var gamesDbUri = new Uri(
            $"https://gamesdb.gog.com/platforms/gog/external_releases/{Uri.EscapeDataString(release.ExternalId)}");
        using (var request = CreateAuthorizedRequest(gamesDbUri, credentials.AccessToken))
        {
            if (!string.IsNullOrWhiteSpace(release.Certificate))
                request.Headers.TryAddWithoutValidation("X-GOG-Library-Cert", release.Certificate);
            using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var document = await ReadJsonAsync(response.Content, ct).ConfigureAwait(false);
                var metadata = ParseGamesDbMetadata(document.RootElement);
                if (metadata is not null) return metadata;
            }
        }

        // Public product metadata is a useful fallback for older or unusual
        // releases that have no GamesDB record.
        var productUri = new Uri(
            $"https://api.gog.com/products/{Uri.EscapeDataString(release.ExternalId)}?locale=en-US");
        using var productRequest = CreateAuthorizedRequest(productUri, credentials.AccessToken);
        using var productResponse = await _http.SendAsync(
                productRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ct)
            .ConfigureAwait(false);
        if (!productResponse.IsSuccessStatusCode) return null;
        using var productDocument = await ReadJsonAsync(productResponse.Content, ct).ConfigureAwait(false);
        return ParseProductMetadata(productDocument.RootElement);
    }

    private static GameMetadata? ParseGamesDbMetadata(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var type = ReadString(root, "type");
        var visible = string.IsNullOrWhiteSpace(type) ||
                      type.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                      type.Equals("mod", StringComparison.OrdinalIgnoreCase);
        var title = ReadLocalizedString(root, "title");

        if (root.TryGetProperty("game", out var game) && game.ValueKind == JsonValueKind.Object)
        {
            if (TryReadBoolean(game, "visible_in_library", out var shown)) visible &= shown;
            title ??= ReadLocalizedString(game, "title");
            var cover = ReadImageFormat(game, "vertical_cover")
                        ?? ReadImageFormat(game, "cover")
                        ?? ReadImageFormat(game, "logo");
            if (!string.IsNullOrWhiteSpace(title))
                return new GameMetadata(title.Trim(), cover, visible);
        }

        return string.IsNullOrWhiteSpace(title) ? null : new GameMetadata(title.Trim(), null, visible);
    }

    private static GameMetadata? ParseProductMetadata(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var title = ReadString(root, "title", "name");
        if (string.IsNullOrWhiteSpace(title)) return null;

        string? cover = null;
        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object)
        {
            foreach (var preferred in new[] { "productCard", "product_card", "logo2x", "logo" })
            {
                cover = ReadString(images, preferred);
                cover = NormalizeGogImageUrl(cover);
                if (cover is not null) break;
            }
        }
        return new GameMetadata(title.Trim(), cover, true);
    }

    private IReadOnlyList<CachedGame> LoadCachedGames(string expectedAccountKey)
    {
        try
        {
            if (!File.Exists(CachePath) || new FileInfo(CachePath).Length > MaxJsonBytes)
                return Array.Empty<CachedGame>();
            using var document = JsonDocument.Parse(File.ReadAllText(CachePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !OwnerMatches(root, expectedAccountKey) ||
                !root.TryGetProperty("games", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return Array.Empty<CachedGame>();
            if (items.ValueKind != JsonValueKind.Array) return Array.Empty<CachedGame>();

            var result = new List<CachedGame>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = ReadStringOrNumber(item, "id", "external_id", "externalId");
                var title = ReadString(item, "title", "name");
                if (!IsSafeProductId(id) || string.IsNullOrWhiteSpace(title)) continue;
                var cover = NormalizeGogImageUrl(ReadString(item, "coverUrl", "art_square", "art_cover"));
                DateTimeOffset? metadataUpdated = null;
                var updatedText = ReadString(item, "metadataUpdatedUtc");
                if (DateTimeOffset.TryParse(updatedText, out var parsed)) metadataUpdated = parsed.ToUniversalTime();
                var visible = !TryReadBoolean(item, "visible", out var isVisible) || isVisible;
                result.Add(new CachedGame(id!, title.Trim(), cover, metadataUpdated, visible));
            }
            return result;
        }
        catch
        {
            return Array.Empty<CachedGame>();
        }
    }

    private bool CacheBelongsTo(string expectedAccountKey)
    {
        try
        {
            if (!File.Exists(CachePath) || new FileInfo(CachePath).Length > MaxJsonBytes) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(CachePath));
            return OwnerMatches(document.RootElement, expectedAccountKey);
        }
        catch { return false; }
    }

    private static bool OwnerMatches(JsonElement root, string expectedAccountKey)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("accountKey", out var owner) ||
            owner.ValueKind != JsonValueKind.String)
            return false;
        var actual = owner.GetString();
        if (actual is null || actual.Length != expectedAccountKey.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual),
            Encoding.ASCII.GetBytes(expectedAccountKey));
    }

    private async Task WriteCacheAtomicallyAsync(
        string accountKey,
        IReadOnlyList<CachedGame> games,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(CachePath)
                        ?? throw new InvalidOperationException("GOG cache directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = CachePath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var payload = new CacheEnvelope(2, accountKey, _utcNow(), games);
                await JsonSerializer.SerializeAsync(stream, payload, CacheJsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(CachePath))
                File.Replace(temporaryPath, CachePath, null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, CachePath);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { /* best effort */ }
        }
    }

    private static HttpRequestMessage CreateAuthorizedRequest(Uri uri, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("User-Agent", "ExoLauncher/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new GogLibraryHttpException(unauthorized: true);
        if (!response.IsSuccessStatusCode)
            throw new GogLibraryHttpException(unauthorized: false);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxJsonBytes)
            throw new InvalidDataException("GOG response exceeded the size limit.");

        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxJsonBytes)
                throw new InvalidDataException("GOG response exceeded the size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(
                buffer,
                new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow },
                ct)
            .ConfigureAwait(false);
    }

    private static string? ReadLocalizedString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property)) return null;
        if (property.ValueKind == JsonValueKind.String) return property.GetString();
        if (property.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "*", "en-US", "en", "en-GB" })
        {
            if (property.TryGetProperty(key, out var localized) && localized.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(localized.GetString()))
                return localized.GetString();
        }
        return property.EnumerateObject()
            .Where(candidate => candidate.Value.ValueKind == JsonValueKind.String)
            .Select(candidate => candidate.Value.GetString())
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private static string? ReadImageFormat(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var image)) return null;
        var raw = image.ValueKind == JsonValueKind.String
            ? image.GetString()
            : image.ValueKind == JsonValueKind.Object
                ? ReadString(image, "url_format", "url")
                : null;
        return NormalizeGogImageUrl(raw);
    }

    private static string? NormalizeGogImageUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var candidate = raw.Trim()
            .Replace("{formatter}", "_glx_vertical_cover", StringComparison.OrdinalIgnoreCase)
            .Replace("{ext}", "jpg", StringComparison.OrdinalIgnoreCase);
        if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !(uri.Host.Equals("gog-statics.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".gog-statics.com", StringComparison.OrdinalIgnoreCase)))
            return null;
        return uri.AbsoluteUri;
    }

    private static string? ReadString(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String)
                continue;
            var text = property.Value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    private static string? ReadStringOrNumber(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString()?.Trim();
            if (property.Value.ValueKind == JsonValueKind.Number) return property.Value.GetRawText();
        }
        return null;
    }

    private static bool TryReadBoolean(JsonElement value, string name, out bool result)
    {
        result = false;
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.True) { result = true; return true; }
            if (property.Value.ValueKind == JsonValueKind.False) { result = false; return true; }
        }
        return false;
    }

    private static bool CredentialsAreSafe(GogdlCli.AuthCredentials credentials) =>
        IsSafeHeaderValue(credentials.AccessToken, 16_384) &&
        !string.IsNullOrWhiteSpace(credentials.UserId) &&
        credentials.UserId.Length <= 256 &&
        credentials.UserId.All(ch => !char.IsControl(ch));

    private static bool TryAccountKey(string? userId, out string accountKey)
    {
        accountKey = string.Empty;
        if (string.IsNullOrWhiteSpace(userId) ||
            userId.Length > 256 ||
            userId.Any(char.IsControl))
            return false;
        accountKey = AccountKeyForUser(userId);
        return true;
    }

    internal static string AccountKeyForUser(string userId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("gog-account\0" + userId));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static bool IsSafeHeaderValue(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength &&
        value.All(ch => ch is not ('\r' or '\n') && !char.IsControl(ch));

    private static bool IsSafeProductId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 32 && value.All(char.IsDigit);

    private static GogOwnedLibrarySyncResult Failure(string message) =>
        new(false, false, 0, false, message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
    }

    private sealed record GalaxyRelease(string ExternalId, string? Certificate);
    private sealed record GameMetadata(string Title, string? CoverUrl, bool Visible);
    private sealed record CachedGame(
        string Id,
        string Title,
        string? CoverUrl,
        DateTimeOffset? MetadataUpdatedUtc,
        bool Visible);
    private sealed record CacheEnvelope(
        int SchemaVersion,
        string AccountKey,
        DateTimeOffset SyncedAtUtc,
        IReadOnlyList<CachedGame> Games);

    private sealed class GogLibraryHttpException(bool unauthorized) : Exception
    {
        public bool Unauthorized { get; } = unauthorized;
    }
}
