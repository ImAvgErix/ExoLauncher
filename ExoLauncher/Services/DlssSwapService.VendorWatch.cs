using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// New upscaler SDKs reach people without an Exo update. When Exo opens (at most hourly) it asks each vendor's
/// repository on GitHub for its latest release: NVIDIA's DLSS, AMD's FSR SDK (FSR 4, which DLSS Swapper's manifest
/// does not carry) and Intel's XeSS. A release newer than every one Exo knows has its DLLs fetched from the release's
/// commit, only those a scanned game ships. Each must match the size and git blob hash GitHub lists for it and pass
/// the checks every upscaler file passes (the vendor's Authenticode signature, identity, exports, x64); its version
/// is then read from the file and the release is kept in vendor-releases.json with the SHA-256 it was verified at,
/// used from then on like a pin.
/// </summary>
public sealed partial class DlssSwapService
{
    /// <summary>A vendor's SDK repository: where its DLLs sit in a release, and how Exo names the release.</summary>
    internal sealed record VendorSdk(string Repo, string Folder, string[] Files, string ReleasePrefix, string Name, int MinMajor = 0);

    internal static readonly VendorSdk[] VendorSdks =
    [
        new(DlssRepo, "lib/Windows_x86_64/rel", ["nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll"], "dlss-", "DLSS"),
        // FSR SDK 2 and later; FSR 3.1's own builds come from DLSS Swapper's manifest.
        new(FfxRepo, "Kits/FidelityFX/signedbin", ["amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll",
            "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_denoiser_dx12.dll", "amd_fidelityfx_radiancecache_dx12.dll"],
            "fsr-", "FSR SDK", MinMajor: 2),
        new(XessRepo, "bin", ["libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "libxell.dll"], "xess-", "XeSS SDK"),
    ];

    private static readonly TimeSpan VendorWatchMinGap = TimeSpan.FromHours(1);
    private static IReadOnlyList<PinnedFile>? _watched;
    private static Task? _vendorWatch;

    private static string WatchedPath => Path.Combine(CacheRoot, "vendor-releases.json");
    private static string WatchedCheckedPath => Path.Combine(CacheRoot, "vendor-checked");

    private static IReadOnlyList<PinnedFile> LoadWatched()
    {
        try
        {
            if (!File.Exists(WatchedPath)) return [];
            var files = JsonSerializer.Deserialize<List<PinnedFile>>(File.ReadAllText(WatchedPath)) ?? [];
            // Only what the watch writes: a vendor's repository at a commit, with the SHA-256 it was verified at.
            return files.Where(file => file.Sha256.Length == 64 && file.Zip is null && IsVendorSdkUrl(file.Url)).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Debug("Upscaler releases from GitHub unreadable: " + ex.Message);
            return [];
        }
    }

    /// <summary>One of a vendor's SDK DLLs at a commit: the only place the watch fetches from.</summary>
    internal static bool IsVendorSdkUrl(string url)
    {
        foreach (var sdk in VendorSdks)
        {
            var prefix = $"https://raw.githubusercontent.com/{sdk.Repo}/";
            if (!url.StartsWith(prefix, StringComparison.Ordinal) || url.Length < prefix.Length + 41) continue;
            var commit = url.AsSpan(prefix.Length, 40);
            if (url[prefix.Length + 40] != '/' || !commit.ToString().All(char.IsAsciiHexDigitLower)) continue;
            var rest = url[(prefix.Length + 41)..];
            if (sdk.Files.Any(name => rest == sdk.Folder + "/" + name)) return true;
        }
        return false;
    }

    /// <summary>The newest release of a vendor's SDK Exo has, pinned or found: what a GitHub release must beat.</summary>
    private static Version NewestKnown(VendorSdk sdk) =>
        PinnedFiles.Concat(Watched())
            .Where(file => file.Repo == sdk.Repo && file.Release.StartsWith(sdk.ReleasePrefix, StringComparison.Ordinal))
            .Select(file => TryParseVersion(file.Release[sdk.ReleasePrefix.Length..]) ?? new Version(0, 0))
            .Where(version => version.Major >= sdk.MinMajor)
            .DefaultIfEmpty(new Version(0, 0))
            .Max()!;

    private static IReadOnlyList<PinnedFile> Watched()
    {
        lock (CatalogGate)
            return _watched ??= LoadWatched();
    }

    /// <summary>
    /// On opening Exo (App.OnLaunched), and again for a status read an hour later: DLSS Swapper's list, then
    /// each vendor's latest SDK, in the background.
    /// </summary>
    internal static void WatchUpscalerReleases()
    {
        RefreshCatalogInBackground();
        lock (CatalogGate)
        {
            if (_vendorWatch is { IsCompleted: false }) return;
            try
            {
                var info = new FileInfo(WatchedCheckedPath);
                if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < VendorWatchMinGap) return;
            }
            catch { return; }
            _vendorWatch = Task.Run(WatchVendorsAsync);
        }
    }

    private static async Task WatchVendorsAsync()
    {
        try
        {
            var used = CurrentDetectionCache().FileNames();
            Directory.CreateDirectory(CacheRoot);
            File.WriteAllText(WatchedCheckedPath, "");
            foreach (var sdk in VendorSdks)
            {
                if (!sdk.Files.Any(used.Contains)) continue;
                try { await WatchVendorAsync(sdk, used).ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Info($"{sdk.Name} releases not checked (the ones Exo has still apply): {ex.Message}"); }
            }
        }
        catch (Exception ex) { AppLog.Info("Upscaler releases not checked: " + ex.Message); }
    }

    private static async Task WatchVendorAsync(VendorSdk sdk, IReadOnlySet<string> used)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        var api = "https://api.github.com/repos/" + sdk.Repo;
        var tag = (await GetGitHubJsonAsync(api + "/releases/latest", ct).ConfigureAwait(false))?["tag_name"]?.GetValue<string>();
        if (tag is null || TryParseVersion(tag.TrimStart('v', 'V')) is not { } version || version.Major < sdk.MinMajor || version <= NewestKnown(sdk)) return;
        var commit = (await GetGitHubJsonAsync(api + "/commits/" + Uri.EscapeDataString(tag), ct).ConfigureAwait(false))?["sha"]?.GetValue<string>();
        if (commit is not { Length: 40 } || !commit.All(char.IsAsciiHexDigit)) return;
        commit = commit.ToLowerInvariant();
        var wanted = SdkListing(await GetGitHubJsonAsync($"{api}/contents/{sdk.Folder}?ref={commit}", ct).ConfigureAwait(false), sdk, used);
        if (wanted.Count == 0) return;

        var release = TrimVersion(tag.TrimStart('v', 'V'))!;
        var incoming = Path.Combine(CacheRoot, "incoming-" + Guid.NewGuid().ToString("N"));
        var found = new List<PinnedFile>();
        try
        {
            Directory.CreateDirectory(incoming);
            foreach (var (name, size, blob) in wanted)
            {
                var path = Path.Combine(incoming, name);
                AppLog.Info($"{sdk.Name} {release} on GitHub: fetching {name} ({size} bytes).");
                await DownloadToFileAsync($"https://raw.githubusercontent.com/{sdk.Repo}/{commit}/{sdk.Folder}/{name}", path, size, ct).ConfigureAwait(false);
                if (!blob.Equals(GitBlobSha1(path), StringComparison.OrdinalIgnoreCase) || !IsTrustedReplacementDll(path, name) ||
                    TryFileVersion(path) is not { } fileVersion || FileSha256(path) is not { } sha)
                    throw new InvalidDataException($"{name} from {sdk.Name} {release} did not match GitHub's listing or the vendor's signature.");
                var file = new PinnedFile(name, sdk.ReleasePrefix + release, sdk.Name + " " + release, fileVersion, TrimVersion(fileVersion)!,
                    sdk.Repo, commit, sdk.Folder + "/" + name, size, sha.ToLowerInvariant());
                var dest = PinnedCachePath(file);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(path, dest, overwrite: true);
                found.Add(file);
            }
        }
        finally
        {
            try { Directory.Delete(incoming, recursive: true); } catch { }
        }

        lock (CatalogGate)
        {
            var kept = found.Concat(_watched ?? LoadWatched()).ToList();
            var temp = WatchedPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(kept));
            File.Move(temp, WatchedPath, overwrite: true);
            _watched = kept;
            _allReleases = null;
        }
        AppLog.Info($"{sdk.Name} {release} found on GitHub: {string.Join(", ", found.Select(file => file.FileName + " " + file.Display))}.");
    }

    /// <summary>
    /// The files to fetch from a release folder's listing (GitHub's contents API): the SDK's DLLs a scanned game
    /// ships, each with its size and git blob hash.
    /// </summary>
    internal static List<(string Name, long Size, string Blob)> SdkListing(JsonNode? listing, VendorSdk sdk, IReadOnlySet<string> used)
    {
        var files = new List<(string, long, string)>();
        if (listing is not JsonArray items) return files;
        foreach (var node in items)
        {
            if (node is not JsonObject item || item["type"]?.GetValue<string>() != "file") continue;
            var name = item["name"]?.GetValue<string>()?.ToLowerInvariant();
            var size = item["size"]?.GetValue<long>() ?? 0;
            var blob = item["sha"]?.GetValue<string>();
            if (name is null || !sdk.Files.Contains(name) || !used.Contains(name)) continue;
            if (size <= 0 || size > 256L << 20 || blob is not { Length: 40 } || !blob.All(char.IsAsciiHexDigit)) continue;
            files.Add((name, size, blob));
        }
        return files;
    }

    private static async Task<JsonNode?> GetGitHubJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return text.Length > ManifestMaxBytes ? null : JsonNode.Parse(text);
    }

    /// <summary>Git's blob hash of a file: SHA-1 over "blob {size}\0" and its bytes, as GitHub lists it.</summary>
    internal static string? GitBlobSha1(string path)
    {
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var fs = File.OpenRead(path);
            sha.AppendData(Encoding.ASCII.GetBytes($"blob {fs.Length}\0"));
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = fs.Read(buffer)) > 0) sha.AppendData(buffer, 0, read);
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch { return null; }
    }
}
