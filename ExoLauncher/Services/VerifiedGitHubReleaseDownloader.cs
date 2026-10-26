using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The immutable trust contract for one executable published by an official
/// GitHub repository. Asset names are intentionally case-sensitive.
/// </summary>
internal sealed record GitHubReleaseAsset(
    string Owner,
    string Repository,
    string Tag,
    string AssetName,
    long ExpectedSize,
    string ExpectedSha256,
    IReadOnlyList<string>? OwnerAliases = null,
    // Published before GitHub recorded asset digests (2025): the API has none to compare, so only the
    // downloaded bytes are checked against ExpectedSha256, as they are for every asset.
    bool PredatesGitHubDigests = false);

/// <summary>
/// Resolves an exact pinned GitHub release asset, requires its API metadata to
/// match the locally pinned size and SHA-256, and promotes the download only
/// after all integrity and executable validation succeeds.
/// </summary>
internal sealed class VerifiedGitHubReleaseDownloader : IDisposable
{
    private const int MaximumRedirects = 5;
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int BufferSize = 128 * 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DestinationGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (GitHubReleaseAsset Asset, Func<string, bool> Validate)> ExecutionContracts =
        new(StringComparer.OrdinalIgnoreCase);

    internal static IDisposable? AcquireExecutionLease(string path)
    {
        if (!ExecutionContracts.TryGetValue(Path.GetFullPath(path), out var contract)) return null;
        var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // The lease blocks writers for the whole run. Under it, an unchanged
            // stamp read from its own handle (length, last-write and change time,
            // file id) reuses the earlier verification instead of
            // hashing a 12-18 MB binary on every CLI call.
            if (!Adapters.PinnedToolCache.IsPinnedAsset(contract.Asset, path, contract.Validate, lease.SafeFileHandle))
                throw new SecurityException("The store tool no longer matches its pinned release. Reinstall the tool from Settings.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static VerifiedGitHubReleaseDownloader Shared { get; } = new(CreateProductionHandler());

    /// <summary>For a download too big for <see cref="Shared"/>'s five minutes: dictation's 465 MB speech model on a slow line.</summary>
    internal static VerifiedGitHubReleaseDownloader Large { get; } = new(CreateProductionHandler(), TimeSpan.FromHours(3));

    private readonly HttpClient _http;

    internal VerifiedGitHubReleaseDownloader(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _http = ExoHttp.Create(timeout ?? TimeSpan.FromMinutes(5), handler, disposeHandler: true);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ExoLauncher", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    public void Dispose() => _http.Dispose();

    internal async Task<string> DownloadPinnedAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        Func<string, bool> validateExecutable,
        CancellationToken ct,
        Action<long>? progress = null)
    {
        ValidateAssetContract(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(validateExecutable);

        using var deadline = ExoHttpTransport.Deadline(_http, ct);
        ct = deadline.Token;

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new ArgumentException("Destination must have a parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);

        var gate = DestinationGates.GetOrAdd(destination, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A PE-shaped file is not a trusted cache entry. A second caller can
            // skip the network only when the existing managed file still matches
            // the production-pinned size and SHA-256 byte for byte.
            if (IsPinnedAssetFile(asset, destination, validateExecutable)) return destination;

            var release = await ResolvePinnedAssetAsync(asset, ct).ConfigureAwait(false);
            var temporaryPath = CreateUniqueTemporaryPath(destination);
            try
            {
                await DownloadAndVerifyAsync(asset, release, temporaryPath, ct, progress).ConfigureAwait(false);
                if (!TryValidate(validateExecutable, temporaryPath))
                    throw new InvalidDataException("The verified release asset failed executable validation.");

                // The unique temporary file is created beside the destination,
                // so this same-volume move is the only promotion step observed by
                // other callers. Existing files remain intact until now.
                File.Move(temporaryPath, destination, overwrite: true);
                ExecutionContracts[destination] = (asset, validateExecutable);
                return destination;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal static byte[]? ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrEmpty(digest) ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return null;

        var hex = digest["sha256:".Length..];
        if (hex.Length != 64 || hex.Any(static c => !Uri.IsHexDigit(c))) return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static bool IsPinnedAssetFile(
        GitHubReleaseAsset asset,
        string path,
        Func<string, bool> validateExecutable)
    {
        try
        {
            ValidateAssetContract(asset);
            if (string.IsNullOrWhiteSpace(path) ||
                !File.Exists(path) ||
                new FileInfo(path).Length != asset.ExpectedSize ||
                !validateExecutable(path))
                return false;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan);
            var actual = SHA256.HashData(stream);
            var verified = CryptographicOperations.FixedTimeEquals(actual, ExpectedDigest(asset));
            if (verified) ExecutionContracts[Path.GetFullPath(path)] = (asset, validateExecutable);
            return verified;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsExpectedAssetDownloadUri(
        Uri uri,
        GitHubReleaseAsset asset)
    {
        if (!HasPinnedHttpsOrigin(uri, "github.com") ||
            !string.IsNullOrEmpty(uri.Query))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 6) return false;

        try
        {
            var decoded = segments.Select(Uri.UnescapeDataString).ToArray();
            if (decoded.Any(static segment =>
                    segment.Length == 0 || segment.Contains('/') || segment.Contains('\\')))
                return false;

            return IsExpectedOwner(decoded[0], asset)
                   && decoded[1].Equals(asset.Repository, StringComparison.OrdinalIgnoreCase)
                   && decoded[2].Equals("releases", StringComparison.Ordinal)
                   && decoded[3].Equals("download", StringComparison.Ordinal)
                   && decoded[4].Equals(asset.Tag, StringComparison.Ordinal)
                   && decoded[5].Equals(asset.AssetName, StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    internal static bool IsAllowedRedirectUri(Uri uri) =>
        HasPinnedHttpsOrigin(uri, "release-assets.githubusercontent.com")
        || HasPinnedHttpsOrigin(uri, "objects.githubusercontent.com");

    internal static Uri PinnedDownloadUri(GitHubReleaseAsset asset)
    {
        ValidateAssetContract(asset);
        var uri = new Uri(
            $"https://github.com/{asset.Owner}/{asset.Repository}/releases/download/{asset.Tag}/{asset.AssetName}",
            UriKind.Absolute);
        if (!IsExpectedAssetDownloadUri(uri, asset))
            throw new SecurityException("Pinned GitHub download URL is not the official repository asset.");
        return uri;
    }

    internal static string CreateUniqueTemporaryPath(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new ArgumentException("Destination must have a parent directory.", nameof(destinationPath));
        var fileName = Path.GetFileName(destination);
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentException("Destination must include a file name.", nameof(destinationPath));
        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.download");
    }

    private async Task<ResolvedReleaseAsset> ResolvePinnedAssetAsync(
        GitHubReleaseAsset asset,
        CancellationToken ct)
    {
        var metadataUri = new Uri(
            $"https://api.github.com/repos/{asset.Owner}/{asset.Repository}/releases/tags/{Uri.EscapeDataString(asset.Tag)}",
            UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUri);
        using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct)
            .ConfigureAwait(false);

        if (IsRedirect(response.StatusCode))
        {
            AppLog.Debug("GitHub release metadata redirected; using the pinned download URL.");
            return new ResolvedReleaseAsset(PinnedDownloadUri(asset));
        }
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            if (code is 403 or 429 or 502 or 503)
            {
                AppLog.Warn($"GitHub release metadata HTTP {code}; using pinned download URL.");
                return new ResolvedReleaseAsset(PinnedDownloadUri(asset));
            }
            throw new HttpRequestException(
                $"GitHub release metadata request failed with HTTP {code}.",
                null,
                response.StatusCode);
        }

        var metadata = await ReadBoundedAsync(response.Content, MaximumMetadataBytes, ct)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tag_name", out var tagElement) ||
            tagElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tagElement.GetString()))
            throw new InvalidDataException("GitHub release metadata did not include a release tag.");

        var tag = tagElement.GetString()!;
        if (!tag.Equals(asset.Tag, StringComparison.Ordinal))
            throw new InvalidDataException("GitHub release metadata did not match the pinned release tag.");
        if (!root.TryGetProperty("assets", out var assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub release metadata did not include assets.");

        JsonElement? match = null;
        foreach (var candidate in assetsElement.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object ||
                !candidate.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                !string.Equals(nameElement.GetString(), asset.AssetName, StringComparison.Ordinal))
                continue;

            if (match is not null)
                throw new InvalidDataException("GitHub release metadata contained duplicate expected assets.");
            match = candidate;
        }

        if (match is null)
            throw new InvalidDataException(
                $"GitHub release did not contain the exact expected asset '{asset.AssetName}'.");

        var selected = match.Value;
        if (!selected.TryGetProperty("state", out var stateElement) ||
            stateElement.ValueKind != JsonValueKind.String ||
            !string.Equals(stateElement.GetString(), "uploaded", StringComparison.Ordinal))
            throw new InvalidDataException("The expected GitHub release asset is not fully uploaded.");

        if (!selected.TryGetProperty("size", out var sizeElement) ||
            !sizeElement.TryGetInt64(out var size) ||
            size != asset.ExpectedSize)
            throw new InvalidDataException("The GitHub release asset size did not match the pinned size.");

        var digest = selected.TryGetProperty("digest", out var digestElement) &&
                     digestElement.ValueKind == JsonValueKind.String
            ? ParseSha256Digest(digestElement.GetString())
            : null;
        if (digest is null && !asset.PredatesGitHubDigests)
            throw new InvalidDataException(
                "The expected GitHub release asset did not provide a valid SHA-256 digest.");
        if (digest is not null && !CryptographicOperations.FixedTimeEquals(digest, ExpectedDigest(asset)))
            throw new InvalidDataException(
                "The GitHub release asset digest did not match the pinned SHA-256.");

        if (!selected.TryGetProperty("browser_download_url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var downloadUri) ||
            !IsExpectedAssetDownloadUri(downloadUri, asset))
            throw new SecurityException(
                "The expected GitHub release asset URL did not match the official repository and tag.");

        return new ResolvedReleaseAsset(downloadUri);
    }

    private async Task DownloadAndVerifyAsync(
        GitHubReleaseAsset asset,
        ResolvedReleaseAsset release,
        string temporaryPath,
        CancellationToken ct,
        Action<long>? progress)
    {
        using var response = await SendFollowingAllowedRedirectsAsync(
                release.DownloadUri,
                asset,
                ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub release asset request failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);

        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != asset.ExpectedSize)
            throw new InvalidDataException(
                "GitHub release asset Content-Length did not match release metadata.");

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(
            temporaryPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (written > asset.ExpectedSize - read)
                throw new InvalidDataException("GitHub release asset exceeded its declared size.");
            hasher.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            progress?.Invoke(written);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(flushToDisk: true);

        if (written != asset.ExpectedSize)
            throw new InvalidDataException("GitHub release asset did not match its declared size.");
        var actualDigest = hasher.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, ExpectedDigest(asset)))
            throw new InvalidDataException("GitHub release asset SHA-256 did not match the pinned digest.");
    }

    private async Task<HttpResponseMessage> SendFollowingAllowedRedirectsAsync(
        Uri initialUri,
        GitHubReleaseAsset asset,
        CancellationToken ct)
    {
        var current = initialUri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            var allowed = current.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? IsExpectedAssetDownloadUri(current, asset)
                : IsAllowedRedirectUri(current);
            if (!allowed)
                throw new SecurityException("GitHub release asset redirected outside the allowlist.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct)
                .ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode)) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new SecurityException("GitHub release asset redirect had no destination.");
            if (redirect == MaximumRedirects)
                throw new SecurityException("GitHub release asset redirected too many times.");

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            var nextAllowed = current.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? IsExpectedAssetDownloadUri(current, asset)
                : IsAllowedRedirectUri(current);
            if (!nextAllowed)
                throw new SecurityException("GitHub release asset redirected outside the allowlist.");
        }

        throw new SecurityException("GitHub release asset redirected too many times.");
    }

    private static bool HasPinnedHttpsOrigin(Uri uri, string expectedHost) =>
        uri.IsAbsoluteUri
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and < 400;

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("GitHub release metadata exceeded the size limit.");

        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length > maximumBytes - read)
                throw new InvalidDataException("GitHub release metadata exceeded the size limit.");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static bool TryValidate(Func<string, bool> validateExecutable, string path)
    {
        try
        {
            return File.Exists(path) && validateExecutable(path);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort. A GUID-scoped file cannot collide with another download.
        }
    }

    private static void ValidateAssetContract(GitHubReleaseAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!IsSafeRepositoryComponent(asset.Owner) ||
            !IsSafeRepositoryComponent(asset.Repository))
            throw new ArgumentException("GitHub owner and repository must be simple path components.", nameof(asset));
        if (asset.OwnerAliases is { Count: > 0 })
        {
            if (asset.OwnerAliases.Count > 8)
                throw new ArgumentException("GitHub owner aliases are limited to eight names.", nameof(asset));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { asset.Owner };
            foreach (var alias in asset.OwnerAliases)
            {
                if (!IsSafeRepositoryComponent(alias) || !seen.Add(alias))
                    throw new ArgumentException(
                        "GitHub owner aliases must be unique simple path components.",
                        nameof(asset));
            }
        }
        if (string.IsNullOrWhiteSpace(asset.Tag) ||
            asset.Tag.Contains('/') ||
            asset.Tag.Contains('\\'))
            throw new ArgumentException("GitHub release tag must be one exact path component.", nameof(asset));
        if (string.IsNullOrWhiteSpace(asset.AssetName) ||
            !string.Equals(Path.GetFileName(asset.AssetName), asset.AssetName, StringComparison.Ordinal) ||
            asset.AssetName.Contains('/') ||
            asset.AssetName.Contains('\\'))
            throw new ArgumentException("GitHub asset name must be one exact file name.", nameof(asset));
        if (asset.ExpectedSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(asset), "Expected asset size must be positive.");
        if (string.IsNullOrWhiteSpace(asset.ExpectedSha256) ||
            asset.ExpectedSha256.Length != 64 ||
            asset.ExpectedSha256.Any(static c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Pinned SHA-256 must contain exactly 64 hexadecimal characters.", nameof(asset));
    }

    private static byte[] ExpectedDigest(GitHubReleaseAsset asset) =>
        Convert.FromHexString(asset.ExpectedSha256);

    private static bool IsExpectedOwner(string owner, GitHubReleaseAsset asset)
    {
        if (owner.Equals(asset.Owner, StringComparison.OrdinalIgnoreCase)) return true;
        if (asset.OwnerAliases is not { Count: > 0 }) return false;
        foreach (var alias in asset.OwnerAliases)
        {
            if (owner.Equals(alias, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsSafeRepositoryComponent(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    };

    private sealed record ResolvedReleaseAsset(Uri DownloadUri);
}
