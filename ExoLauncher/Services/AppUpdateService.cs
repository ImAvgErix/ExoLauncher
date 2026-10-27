using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// In-app GitHub update: check + download + quiet SFX install. Never opens a browser.
/// </summary>
public sealed class AppUpdateService
{
    private const int MaxRetainedInstallers = 2;
    private static readonly TimeSpan InstallerRetentionAge = TimeSpan.FromDays(14);
    private static readonly HttpClient SharedHttp = CreateClient();

    private readonly HttpClient _http;
    private readonly string _updatesDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly bool _allowCurlFallback;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private static readonly TimeSpan ReleaseCacheAge = TimeSpan.FromMinutes(10);
    private sealed record CachedRelease(ParsedRelease Release, DateTimeOffset FetchedAt);
    private volatile CachedRelease? _cachedRelease;
    private sealed record CachedNotes(string Tag, ExoWhatsNewRelease Notes);
    private volatile CachedNotes? _cachedNotes;

    public AppUpdateService()
        : this(SharedHttp, GetDefaultUpdatesDirectory(), TimeProvider.System, allowCurlFallback: true)
    {
    }

    internal AppUpdateService(
        HttpClient http,
        string updatesDirectory,
        TimeProvider timeProvider,
        bool allowCurlFallback = false)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(updatesDirectory))
            throw new ArgumentException("An updater working directory is required.", nameof(updatesDirectory));
        _updatesDirectory = Path.GetFullPath(updatesDirectory);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _allowCurlFallback = allowCurlFallback;

        CleanupUpdateArtifacts();
    }

    private const string LatestApiUrl = "https://api.github.com/repos/UhhErix/ExoLauncher/releases/latest";
    private const string LatestPageUrl = "https://github.com/UhhErix/ExoLauncher/releases/latest";
    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            EnableMultipleHttp2Connections = false,
        };
        var c = ExoHttp.Create(TimeSpan.FromMinutes(30), handler);
        c.DefaultRequestVersion = HttpVersion.Version11;
        c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ExoLauncher", "1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    public sealed class CheckResult
    {
        public bool UpdateAvailable { get; init; }
        public bool AlreadyLatest { get; init; }
        public string LocalVersion { get; init; } = "";
        public string RemoteVersion { get; init; } = "";
        public string Message { get; init; } = "";
        public string? DownloadUrl { get; init; }
        public long? DownloadSize { get; init; }
        public string? Sha256 { get; init; }
        public bool ShouldExit { get; init; }
        public bool Installed { get; init; }
        /// <summary>The offered version's What's New; null when its release has none or it failed validation.</summary>
        internal ExoWhatsNewRelease? Upcoming { get; init; }
    }

    /// <param name="fresh">A check the user asked for skips the release cache.</param>
    public async Task<CheckResult> CheckAsync(string localVersion, CancellationToken ct = default, bool fresh = false)
    {
        var local = Normalize(localVersion);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var release = await LoadLatestReleaseAsync(timeout.Token, fresh).ConfigureAwait(false);
            if (release is null)
            {
                return new CheckResult
                {
                    LocalVersion = local,
                    RemoteVersion = "?",
                    Message = "Could not check for updates. Try again in a minute.",
                };
            }

            var remote = Normalize(release.Tag);
            if (!Version.TryParse(local, out var lv) || !Version.TryParse(remote, out var rv))
            {
                return new CheckResult
                {
                    LocalVersion = local,
                    RemoteVersion = remote,
                    Message = $"Could not compare versions. The latest is v{remote}; you have v{local}.",
                };
            }

            if (lv > rv)
            {
                return new CheckResult
                {
                    AlreadyLatest = true,
                    LocalVersion = local,
                    RemoteVersion = remote,
                    Message = $"You have v{local}, which is newer than the latest release (v{remote}).",
                };
            }

            if (lv == rv)
            {
                return new CheckResult
                {
                    AlreadyLatest = true,
                    LocalVersion = local,
                    RemoteVersion = remote,
                    Message = $"You're up to date (v{local}).",
                };
            }

            var available = HasVerifiedInstallerMetadata(release);
            return new CheckResult
            {
                UpdateAvailable = available,
                LocalVersion = local,
                RemoteVersion = remote,
                DownloadUrl = available ? release.DownloadUrl : null,
                DownloadSize = release.Size,
                Sha256 = release.Sha256,
                Upcoming = available ? await LoadWhatsNewAsync(release, ct).ConfigureAwait(false) : null,
                Message = available
                    ? $"Version {remote} is available (you have v{local})."
                    : $"Version {remote} is out, but it cannot be verified yet. Check again later.",
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CheckResult
            {
                LocalVersion = local,
                Message = "The update check timed out. Try again in a minute.",
            };
        }
        catch (Exception ex)
        {
            return new CheckResult
            {
                LocalVersion = local,
                Message = $"Could not check for updates: {ex.Message}",
            };
        }
    }

    public async Task<CheckResult> InstallAsync(
        string localVersion,
        IProgress<(string status, double? percent)>? progress = null,
        CancellationToken ct = default)
    {
        var entered = false;
        try
        {
            await _installGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            return await InstallSerializedAsync(localVersion, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CheckResult
            {
                UpdateAvailable = true,
                LocalVersion = Normalize(localVersion),
                Message = "Update canceled. Nothing was installed.",
            };
        }
        finally
        {
            if (entered)
                _installGate.Release();
        }
    }

    private async Task<CheckResult> InstallSerializedAsync(
        string localVersion,
        IProgress<(string status, double? percent)>? progress,
        CancellationToken ct)
    {
        // CheckAsync bounds the metadata requests itself. The installer body has
        // no whole-transfer deadline here; ResumableDownload's stall timeout
        // ends a dead connection, so a slow one can still finish.
        var check = await CheckAsync(localVersion, ct).ConfigureAwait(false);
        if (!check.UpdateAvailable || string.IsNullOrWhiteSpace(check.DownloadUrl))
            return check;

        if (!Uri.TryCreate(check.DownloadUrl, UriKind.Absolute, out var uri) ||
            !IsAllowedDownloadUri(uri))
        {
            return new CheckResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = "The update is not from Exo's official releases. Nothing was installed.",
            };
        }

        if (string.IsNullOrWhiteSpace(check.Sha256))
        {
            return new CheckResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = "The update could not be verified. Nothing was installed.",
            };
        }

        progress?.Report(("Downloading…", 0));
        string? artifactPath = null;
        var installerStarted = false;
        try
        {
            Directory.CreateDirectory(_updatesDirectory);
            var artifactId = Guid.NewGuid().ToString("N");
            var partialPath = Path.Combine(_updatesDirectory, $"ExoLauncher-Setup-{artifactId}.partial");
            var setupPath = Path.Combine(_updatesDirectory, $"ExoLauncher-Setup-{artifactId}.exe");
            artifactPath = partialPath;
            await ResumableDownload.DownloadAsync(uri, partialPath, _updatesDirectory,
                (request, token) => SendFollowingAllowedRedirectsAsync(request, token),
                check.DownloadSize, check.Sha256, (written, total) =>
                {
                    if (total is > 0) progress?.Report(("Downloading…", Math.Min(100, written * 100d / total.Value)));
                    else progress?.Report(($"Downloading… {written / 1048576d:0.0} MB", null));
                }, ct).ConfigureAwait(false);

            if (check.DownloadSize is > 0 && new FileInfo(partialPath).Length != check.DownloadSize.Value)
            {
                return new CheckResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "The update download was the wrong size. Nothing was installed.",
                };
            }

            progress?.Report(("Verifying…", null));
            var hash = await Sha256FileAsync(partialPath, ct).ConfigureAwait(false);
            if (!TimingSafeHashEquals(hash, check.Sha256))
            {
                return new CheckResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "The update download did not match the release. Nothing was installed.",
                };
            }

            if (!IsExpectedInstaller(partialPath))
            {
                return new CheckResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "The update file is not an Exo installer. Nothing was installed.",
                };
            }

            // The executable name only appears after every pre-launch check has
            // passed. If anything below fails before Process.Start succeeds,
            // the finally block removes it immediately.
            File.Move(partialPath, setupPath);
            artifactPath = setupPath;

            progress?.Report(("Installing…", null));
            var processInfo = new ProcessStartInfo
            {
                FileName = setupPath,
                // NSIS' documented silent switch. The installer owns the
                // atomic app.incoming -> app swap; the updater must never copy
                // the installer itself over ExoLauncher.exe.
                Arguments = "/S",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _updatesDirectory,
                ErrorDialog = false,
            };
            processInfo.Environment["EXO_SILENT_INSTALL"] = "1";
            using var installer = Process.Start(processInfo);
            if (installer is null)
                throw new InvalidOperationException("Windows did not start the verified installer.");
            installerStarted = true;

            try { await Task.Delay(400, ct).ConfigureAwait(false); } catch { /* */ }
            progress?.Report(("Restarting…", null));
            return new CheckResult
            {
                UpdateAvailable = true,
                Installed = true,
                ShouldExit = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = $"Applying v{check.RemoteVersion}… Exo Launcher will close and reopen.",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // A stalled or timed-out transfer is not a user cancel.
            // ResumableDownload keeps its SHA-keyed partial (strong ETag only),
            // so a retry resumes it.
            return new CheckResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = "The update download timed out. Try again.",
            };
        }
        catch (Exception ex)
        {
            return new CheckResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = $"Update failed: {ex.Message}",
            };
        }
        finally
        {
            // A successfully launched installer may still be executing and
            // Windows will not let it delete itself. Keep that one for bounded
            // age/count cleanup on the next AppUpdateService startup.
            if (!installerStarted && artifactPath is not null)
                TryDeleteOwnedArtifact(artifactPath);
        }
    }

    /// <summary>The release asset tools/Build-Release.ps1 writes beside the installer: that version's What's New.</summary>
    internal const string WhatsNewAssetName = "whats-new.json";
    internal const int MaxWhatsNewBytes = 64 * 1024;
    internal const int MaxWhatsNewItems = 30;

    internal static bool IsUpdateAssetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Equals("ExoLauncher.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ExoLauncher-Setup.exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAllowedDownloadUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendFollowingAllowedRedirectsAsync(
        HttpRequestMessage initialRequest,
        CancellationToken ct)
    {
        var current = initialRequest.RequestUri ?? throw new InvalidOperationException("Missing download URL.");
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (!IsAllowedDownloadUri(current))
                throw new InvalidOperationException("Update redirected outside GitHub's release-asset hosts.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            foreach (var header in initialRequest.Headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400)
            {
                if (response.RequestMessage?.RequestUri is { } final && !IsAllowedDownloadUri(final))
                {
                    response.Dispose();
                    throw new InvalidOperationException("Update redirected outside GitHub's release-asset hosts.");
                }
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new InvalidOperationException("Update redirect did not include a destination.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new InvalidOperationException("Update redirected too many times.");
    }

    private static string GetDefaultUpdatesDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExoLauncher",
            "updates");

    private void CleanupUpdateArtifacts()
    {
        try
        {
            if (!Directory.Exists(_updatesDirectory))
                return;

            // A cancelled or stalled update leaves its partial download (up to 512 MB); it is cleaned here, not only by the next one.
            ResumableDownload.CleanupAbandoned(_updatesDirectory);
            var installers = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(_updatesDirectory, "ExoLauncher-Setup-*", SearchOption.TopDirectoryOnly))
            {
                if (!TryClassifyArtifact(path, out var partial))
                    continue;

                if (partial)
                {
                    // An active download is opened FileShare.None and cannot be
                    // acquired here. Any unlocked partial belongs to an
                    // interrupted updater and is safe to remove on startup.
                    TryDeleteOwnedArtifact(path);
                    continue;
                }

                try { installers.Add(new FileInfo(path)); }
                catch { /* cleanup is best effort */ }
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var ordered = installers
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var age = now - ordered[index].LastWriteTimeUtc;
                if (age >= InstallerRetentionAge || index >= MaxRetainedInstallers)
                    TryDeleteOwnedArtifact(ordered[index].FullName);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Updater artifact cleanup skipped: " + ex.Message);
        }
    }

    private bool TryDeleteOwnedArtifact(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(
                    Path.GetDirectoryName(fullPath),
                    _updatesDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                !TryClassifyArtifact(fullPath, out _))
            {
                return false;
            }

            if (!File.Exists(fullPath))
                return true;
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                return false;

            // DeleteOnClose makes the exclusivity check and deletion one file
            // operation. Locked downloads and running installers are skipped.
            using var owned = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryClassifyArtifact(string path, out bool partial)
    {
        partial = false;
        var fileName = Path.GetFileName(path);
        const string prefix = "ExoLauncher-Setup-";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string id;
        if (fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
        {
            partial = true;
            id = fileName[prefix.Length..^".partial".Length];
        }
        else if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            id = fileName[prefix.Length..^".exe".Length];
        }
        else
        {
            return false;
        }

        return Guid.TryParseExact(id, "N", out _);
    }

    internal static bool IsExpectedInstaller(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(info.ProductName, "Exo Launcher", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(info.FileDescription, "Exo Launcher Setup", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TimingSafeHashEquals(string? computed, string? expected)
    {
        if (computed is null || expected is null || computed.Length != expected.Length) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(computed),
                Convert.FromHexString(expected));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal sealed record ParsedRelease(
        string Tag,
        string? DownloadUrl,
        string? AssetName,
        long? Size,
        string? Sha256,
        string? WhatsNewUrl = null);

    private static bool HasVerifiedInstallerMetadata(ParsedRelease release) =>
        release.Size is > 0 and <= ResumableDownload.MaxInstallerBytes && NormalizeSha(release.Sha256) is not null &&
        IsReleaseAssetUri(release.DownloadUrl, release.Tag, release.AssetName);

    internal static bool IsReleaseAssetUri(string? url, string tag, string? name) =>
        name is not null && IsUpdateAssetName(name) && IsAssetOfRelease(url, tag, name);

    /// <summary>github.com/UhhErix/ExoLauncher/releases/download/&lt;tag&gt;/&lt;name&gt; over plain HTTPS, nothing else.</summary>
    internal static bool IsAssetOfRelease(string? url, string tag, string name) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals("/UhhErix/ExoLauncher/releases/download/" + Uri.EscapeDataString(tag) + "/" + name, StringComparison.Ordinal);

    private async Task<ParsedRelease?> LoadLatestReleaseAsync(CancellationToken ct, bool fresh = false)
    {
        // The startup check and Install ask for the same release, and GitHub's
        // unauthenticated API budget is small. A user-initiated check always
        // asks GitHub, and refreshes the cache Install then reads.
        if (!fresh && _cachedRelease is { } cached && _timeProvider.GetUtcNow() - cached.FetchedAt < ReleaseCacheAge)
            return cached.Release;
        var release = await FetchLatestReleaseAsync(ct).ConfigureAwait(false);
        if (release is not null && HasVerifiedInstallerMetadata(release))
            _cachedRelease = new CachedRelease(release, _timeProvider.GetUtcNow());
        return release;
    }

    private async Task<ParsedRelease?> FetchLatestReleaseAsync(CancellationToken ct)
    {
        ParsedRelease? discovered = null;
        var (json, responded) = await GetApiJsonAsync(LatestApiUrl, ct).ConfigureAwait(false);
        if (json is not null && TryParseReleaseJson(json, out var parsed))
        {
            discovered = await CompleteDigestAsync(parsed, ct).ConfigureAwait(false);
            if (HasVerifiedInstallerMetadata(discovered)) return discovered;
        }
        var tag = discovered?.Tag ?? await TryLatestTagFromRedirectAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            responded = true;
            var tagApi = "https://api.github.com/repos/UhhErix/ExoLauncher/releases/tags/" + Uri.EscapeDataString(tag);
            (json, _) = await GetApiJsonAsync(tagApi, ct).ConfigureAwait(false);
            if (json is not null && TryParseReleaseJson(json, out parsed) && parsed.Tag == tag)
            {
                discovered = await CompleteDigestAsync(parsed, ct).ConfigureAwait(false);
                if (HasVerifiedInstallerMetadata(discovered)) return discovered;
            }
            discovered ??= new ParsedRelease(tag, null, null, null, null);
        }
        // curl only helps when .NET could not reach GitHub at all. Any HTTP
        // answer (a rate limit, a 404) would be the same through curl.
        if (_allowCurlFallback && !responded)
        {
            json = await CurlGetJsonAsync(LatestApiUrl, ct).ConfigureAwait(false);
            if (json is not null && TryParseReleaseJson(json, out parsed))
            {
                var fallback = await CompleteDigestAsync(parsed, ct).ConfigureAwait(false);
                if (HasVerifiedInstallerMetadata(fallback)) return fallback;
                discovered ??= fallback;
            }
        }
        return discovered;
    }

    private async Task<ParsedRelease> CompleteDigestAsync(ParsedRelease release, CancellationToken ct)
    {
        if (release.Sha256 is not null || !IsReleaseAssetUri(release.DownloadUrl, release.Tag, release.AssetName) ||
            release.Size is not (> 0 and <= ResumableDownload.MaxInstallerBytes)) return release;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl + ".sha256");
            using var response = await SendFollowingAllowedRedirectsAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return release;
            var text = await ReadBoundedTextAsync(response.Content, 4096, ct).ConfigureAwait(false);
            return ParseSha256File(text, release.AssetName) is { } sha ? release with { Sha256 = sha } : release;
        }
        catch (OperationCanceledException) { throw; }
        catch { return release; }
    }

    /// <summary>
    /// The offered release's whats-new.json, through the installer's own host and redirect rules, at most 64 KB.
    /// Anything wrong (no asset, an error, a timeout, a file that fails ParseWhatsNew) is no notes, never a failed check.
    /// </summary>
    private async Task<ExoWhatsNewRelease?> LoadWhatsNewAsync(ParsedRelease release, CancellationToken ct)
    {
        if (release.WhatsNewUrl is null) return null;
        if (_cachedNotes is { } cached && cached.Tag == release.Tag) return cached.Notes;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, release.WhatsNewUrl);
            using var response = await SendFollowingAllowedRedirectsAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var text = await ReadBoundedTextAsync(response.Content, MaxWhatsNewBytes, timeout.Token).ConfigureAwait(false);
            var notes = ParseWhatsNew(text, release.Tag);
            if (notes is not null) _cachedNotes = new CachedNotes(release.Tag, notes);
            return notes;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AppLog.Debug("What's New for the update unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// A whats-new.json: { "version": "X.Y.Z" (the release's tag), "items": [1 to 30 of { "icon": a short Latin name,
    /// "title": up to 80 characters, "detail": up to 240 }] }. Null for anything else: the UI shows only what passed.
    /// </summary>
    internal static ExoWhatsNewRelease? ParseWhatsNew(string json, string tag)
    {
        try
        {
            if (json.Length > MaxWhatsNewBytes) return null;
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;
            var version = v.GetString()!;
            if (version.Length > 20 || !version.All(c => char.IsAsciiDigit(c) || c == '.') ||
                !Version.TryParse(version, out _) || version != Normalize(tag))
                return null;
            var count = list.GetArrayLength();
            if (count is < 1 or > MaxWhatsNewItems) return null;
            var items = new ExoWhatsNewItem[count];
            var index = 0;
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) return null;
                var icon = Text(entry, "icon", 32);
                var title = Text(entry, "title", 80);
                var detail = Text(entry, "detail", 240);
                if (icon is null || title is null || detail is null || !icon.All(char.IsAsciiLetter)) return null;
                items[index++] = new ExoWhatsNewItem { Icon = icon, Title = title, Detail = detail };
            }
            return new ExoWhatsNewRelease { Version = version, Items = items };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }

        static string? Text(JsonElement entry, string name, int max) =>
            entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text && text.Length <= max && !string.IsNullOrWhiteSpace(text) &&
            !text.Any(char.IsControl) ? text : null;
    }

    /// <summary>Json is null on any failure; Responded is true once GitHub sent an HTTP response.</summary>
    private async Task<(string? Json, bool Responded)> GetApiJsonAsync(string url, CancellationToken ct)
    {
        var responded = false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            responded = true;
            if (!resp.IsSuccessStatusCode) return (null, true);
            var body = await ReadBoundedTextAsync(resp.Content, 2 * 1024 * 1024, ct).ConfigureAwait(false);
            return (LooksLikeJson(body) ? body : null, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Debug("GitHub releases API unavailable: " + ex.GetType().Name);
            return (null, responded);
        }
    }

    private static async Task<string> ReadBoundedTextAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("Release metadata is too large.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > maximum) throw new InvalidDataException("Release metadata is too large.");
            bytes.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private async Task<string?> TryLatestTagFromRedirectAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestPageUrl)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            req.Headers.Accept.Clear();
            req.Headers.TryAddWithoutValidation("Accept", "text/html");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var location = resp.Headers.Location;
            if (location is null) return null;
            var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri(LatestPageUrl), location);
            return TagFromReleasesUrl(absolute);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("GitHub latest redirect unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    internal static string? TagFromReleasesUrl(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || !parts[3].Equals("tag", StringComparison.OrdinalIgnoreCase)) return null;
        if (!parts[0].Equals("UhhErix", StringComparison.OrdinalIgnoreCase) ||
            !parts[1].Equals("ExoLauncher", StringComparison.OrdinalIgnoreCase) ||
            !parts[2].Equals("releases", StringComparison.OrdinalIgnoreCase))
            return null;
        var tag = parts[3].Equals("tag", StringComparison.OrdinalIgnoreCase) && parts.Length >= 5
            ? parts[4]
            : parts[3];
        return string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
    }

    internal static bool TryParseReleaseJson(string json, out ParsedRelease parsed)
    {
        parsed = new ParsedRelease("", null, null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(tag)) return false;

            string? downloadUrl = null;
            string? assetName = null;
            long? downloadSize = null;
            string? sha256 = null;
            string? whatsNewUrl = null;
            var verified = false;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    if (name == WhatsNewAssetName)
                    {
                        if (IsAssetOfRelease(url, tag, name)) whatsNewUrl = url;
                        continue;
                    }
                    if (verified || !IsUpdateAssetName(name)) continue;
                    downloadUrl = url;
                    assetName = name;
                    downloadSize = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : null;
                    sha256 = a.TryGetProperty("digest", out var d) ? NormalizeSha(d.GetString()) : null;
                    verified = sha256 is not null && downloadSize is > 0 && IsReleaseAssetUri(url, tag, name);
                }
            }

            parsed = new ParsedRelease(tag, downloadUrl, assetName, downloadSize, sha256, whatsNewUrl);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static async Task<string?> CurlGetJsonAsync(string url, CancellationToken ct)
    {
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (!File.Exists(curl)) return null;
        using var process = new Process();
        try
        {
            process.StartInfo.FileName = curl;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.ArgumentList.Add("-sS");
            process.StartInfo.ArgumentList.Add("--compressed");
            process.StartInfo.ArgumentList.Add("--max-time");
            process.StartInfo.ArgumentList.Add("20");
            process.StartInfo.ArgumentList.Add("--max-filesize");
            process.StartInfo.ArgumentList.Add("2097152");
            process.StartInfo.ArgumentList.Add("-H");
            process.StartInfo.ArgumentList.Add("Accept: application/vnd.github+json");
            process.StartInfo.ArgumentList.Add("-H");
            process.StartInfo.ArgumentList.Add("User-Agent: ExoLauncher/1.0");
            process.StartInfo.ArgumentList.Add("-H");
            process.StartInfo.ArgumentList.Add("X-GitHub-Api-Version: 2022-11-28");
            process.StartInfo.ArgumentList.Add(url);
            process.Start();
            var stdoutTask = ReadBoundedReaderAsync(process.StandardOutput, 2 * 1024 * 1024, ct);
            var stderrTask = ReadBoundedReaderAsync(process.StandardError, 65536, ct);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            return process.ExitCode == 0 && LooksLikeJson(stdout) ? stdout : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("GitHub releases curl fallback unavailable: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            try { if (!process.HasExited) ProcessTermination.Kill(process, entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task<string> ReadBoundedReaderAsync(StreamReader reader, int maximum, CancellationToken ct)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > maximum) throw new InvalidDataException("Release metadata is too large.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static bool LooksLikeJson(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var trimmed = body.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    internal static string Normalize(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "0.0.0";
        var core = v.Trim();
        if (core.StartsWith("v", StringComparison.OrdinalIgnoreCase) ||
            core.StartsWith("V", StringComparison.Ordinal))
            core = core[1..];
        var cut = core.IndexOfAny(['+', '-']);
        if (cut >= 0) core = core[..cut];
        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        while (parts.Length < 3) parts = [.. parts, "0"];
        return string.Join('.', parts.Take(3));
    }

    /// <summary>A "<hex> [*name]" .sha256 file: the hash when it is well formed and names this asset, else null.</summary>
    internal static string? ParseSha256File(string text, string? assetName)
    {
        var tokens = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > 2 || tokens.Length == 2 && tokens[1].TrimStart('*') != assetName)
            return null;
        return NormalizeSha(tokens[0]);
    }

    internal static string? NormalizeSha(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var s = digest.Trim();
        if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            s = s["sha256:".Length..];
        s = s.ToLowerInvariant();
        return s.Length == 64 && s.All(Uri.IsHexDigit) ? s : null;
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
