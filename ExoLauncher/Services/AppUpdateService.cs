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
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public AppUpdateService()
        : this(SharedHttp, GetDefaultUpdatesDirectory(), TimeProvider.System)
    {
    }

    internal AppUpdateService(
        HttpClient http,
        string updatesDirectory,
        TimeProvider timeProvider)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(updatesDirectory))
            throw new ArgumentException("An updater working directory is required.", nameof(updatesDirectory));
        _updatesDirectory = Path.GetFullPath(updatesDirectory);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        CleanupUpdateArtifacts();
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ExoLauncher", "1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
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
        public string? AssetName { get; init; }
        public long? DownloadSize { get; init; }
        public string? Sha256 { get; init; }
        public bool ShouldExit { get; init; }
        public bool Installed { get; init; }
    }

    public async Task<CheckResult> CheckAsync(string localVersion, CancellationToken ct = default)
    {
        var local = Normalize(localVersion);
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.github.com/repos/ImAvgErix/ExoLauncher/releases/latest");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return new CheckResult
                {
                    LocalVersion = local,
                    RemoteVersion = "?",
                    Message = $"Could not check releases (HTTP {(int)resp.StatusCode}).",
                };
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            var remote = Normalize(tag.Trim().TrimStart('v', 'V'));

            string? downloadUrl = null;
            string? assetName = null;
            long? downloadSize = null;
            string? sha256 = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : (long?)null;
                    var digest = a.TryGetProperty("digest", out var d) ? NormalizeSha(d.GetString()) : null;

                    if (IsUpdateAssetName(name!))
                    {
                        downloadUrl = url;
                        assetName = name;
                        downloadSize = size;
                        sha256 = digest;
                        break;
                    }
                }
            }

            if (Version.TryParse(local, out var lv) && Version.TryParse(remote, out var rv) && lv > rv)
            {
                return new CheckResult
                {
                    AlreadyLatest = true,
                    LocalVersion = local,
                    RemoteVersion = remote,
                    Message = $"This build is v{local}, newer than GitHub v{remote}.",
                };
            }

            if (Version.TryParse(local, out lv) && Version.TryParse(remote, out rv) && lv == rv)
            {
                return new CheckResult
                {
                    AlreadyLatest = true,
                    LocalVersion = local,
                    RemoteVersion = remote,
                    Message = $"Exo Launcher is up to date (v{local}).",
                };
            }

            // The release artifact is an NSIS installer. GitHub's asset digest is
            // mandatory: silently executing an unverified executable is never an
            // acceptable update fallback.
            var available = downloadUrl is not null && sha256 is not null;
            return new CheckResult
            {
                UpdateAvailable = available,
                LocalVersion = local,
                RemoteVersion = remote,
                DownloadUrl = downloadUrl,
                AssetName = assetName,
                DownloadSize = downloadSize,
                Sha256 = sha256,
                Message = available
                    ? $"Exo Launcher v{remote} is available (you have v{local})."
                    : downloadUrl is not null
                        ? $"v{remote} exists, but its installer has no SHA-256 digest. Nothing will be installed."
                        : $"v{remote} exists but no supported installer asset was found on the release.",
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
        IProgress<(string status, double percent)>? progress = null,
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
                Message = "Update cancelled. Nothing was installed.",
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
        IProgress<(string status, double percent)>? progress,
        CancellationToken ct)
    {
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
                Message = "Update URL was not an allowlisted GitHub asset.",
            };
        }

        if (string.IsNullOrWhiteSpace(check.Sha256))
        {
            return new CheckResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = "The release did not provide a SHA-256 digest. Nothing was installed.",
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
            using (var response = await SendFollowingAllowedRedirectsAsync(uri, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return new CheckResult
                    {
                        UpdateAvailable = true,
                        LocalVersion = check.LocalVersion,
                        RemoteVersion = check.RemoteVersion,
                        Message = $"Download failed (HTTP {(int)response.StatusCode}).",
                    };
                }

                if (response.RequestMessage?.RequestUri is not { } finalUri ||
                    !IsAllowedDownloadUri(finalUri))
                {
                    return new CheckResult
                    {
                        UpdateAvailable = true,
                        LocalVersion = check.LocalVersion,
                        RemoteVersion = check.RemoteVersion,
                        Message = "The update redirected outside GitHub's release-asset hosts. Nothing was installed.",
                    };
                }

                var total = response.Content.Headers.ContentLength ?? check.DownloadSize ?? -1;
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                var buffer = new byte[128 * 1024];
                long written = 0;
                int read;
                var last = -1;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    if (total > 0)
                    {
                        var pct = (int)Math.Min(90, (written * 90) / total);
                        if (pct != last)
                        {
                            last = pct;
                            progress?.Report(("Downloading…", pct));
                        }
                    }
                }
            }

            if (check.DownloadSize is > 0 && new FileInfo(partialPath).Length != check.DownloadSize.Value)
            {
                return new CheckResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "Update size did not match the release metadata. Nothing was installed.",
                };
            }

            progress?.Report(("Verifying…", 92));
            var hash = await Sha256FileAsync(partialPath, ct).ConfigureAwait(false);
            if (!string.Equals(hash, check.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new CheckResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "Update checksum did not match. Nothing was installed.",
                };
            }

            if (!IsExpectedInstaller(partialPath))
            {
                return new CheckResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "The verified release asset is not an Exo Launcher installer. Nothing was installed.",
                };
            }

            // The executable name only appears after every pre-launch check has
            // passed. If anything below fails before Process.Start succeeds,
            // the finally block removes it immediately.
            File.Move(partialPath, setupPath);
            artifactPath = setupPath;

            progress?.Report(("Installing…", 96));
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
            var installer = Process.Start(processInfo);
            if (installer is null)
                throw new InvalidOperationException("Windows did not start the verified installer.");
            installerStarted = true;

            try { await Task.Delay(400, ct).ConfigureAwait(false); } catch { /* */ }
            progress?.Report(("Restarting…", 100));
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
        Uri initialUri,
        CancellationToken ct)
    {
        var current = initialUri;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (!IsAllowedDownloadUri(current))
                throw new InvalidOperationException("Update redirected outside GitHub's release-asset hosts.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400)
                return response;

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

    private static string Normalize(string v)
    {
        var parts = v.Trim().TrimStart('v', 'V')
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        while (parts.Length < 3) parts = parts.Append("0").ToArray();
        return string.Join('.', parts.Take(3));
    }

    private static string? NormalizeSha(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var s = digest.Trim();
        if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            s = s["sha256:".Length..];
        s = s.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return s.Length == 64 ? s : null;
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
