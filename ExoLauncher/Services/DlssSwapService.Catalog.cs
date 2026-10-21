using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Releases beyond the compiled-in pins, from DLSS Swapper's public manifest
/// (beeradmoore.github.io/dlss-swapper/manifest.json, read with REA from DLSS
/// Swapper 1.2.6.1): one list per file kind with each build's four-part
/// version, file and zip size, MD5, signature validity, a dev/debug flag and a
/// download URL on its archive host. Exo reads it at most once a day and keeps
/// the last copy, so a new DLSS, FSR 3.1 or XeSS build reaches people without
/// an Exo update.
///
/// Trust is Exo's, not the manifest's: a catalog file is installable only
/// after its first download passes the same checks as a pinned one (the
/// vendor's Authenticode signature, original name, company, exports, x64 and
/// the exact version) plus the listed size and MD5. Its SHA-256 is then
/// recorded and every later use is checked against it, like a pin. Dev and
/// debug builds (the watermarked ones), builds the manifest marks unsigned,
/// and DLSS 1 are left out.
/// </summary>
public sealed partial class DlssSwapService
{
    private const string ManifestUrl = "https://beeradmoore.github.io/dlss-swapper/manifest.json";
    private const long ManifestMaxBytes = 8 << 20;
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromDays(1);

    /// <summary>Manifest list → file name, vendor and the name people know the build by.</summary>
    private static readonly (string Kind, string FileName, string Vendor, string Product)[] CatalogKinds =
    [
        ("dlss", "nvngx_dlss.dll", "NVIDIA", "DLSS"),
        ("dlss_d", "nvngx_dlssd.dll", "NVIDIA", "DLSS"),
        ("dlss_g", "nvngx_dlssg.dll", "NVIDIA", "DLSS"),
        ("fsr_31_dx12", "amd_fidelityfx_dx12.dll", "AMD", "FidelityFX"),
        ("fsr_31_vk", "amd_fidelityfx_vk.dll", "AMD", "FidelityFX"),
        ("xess", "libxess.dll", "intel", "XeSS"),
        ("xess_dx11", "libxess_dx11.dll", "intel", "XeSS"),
        ("xess_fg", "libxess_fg.dll", "intel", "XeSS"),
        ("xell", "libxell.dll", "intel", "XeLL"),
    ];

    private static readonly Lock CatalogGate = new();
    private static IReadOnlyList<PinnedFile>? _catalog;
    private static IReadOnlyList<PinnedFile>? _allReleases;
    private static Dictionary<string, string>? _trustedSha;
    private static Task? _catalogRefresh;

    private static string CatalogPath => Path.Combine(CacheRoot, "catalog-v2.json");
    private static string TrustedPath => Path.Combine(CacheRoot, "catalog-trusted.json");

    /// <summary>Pinned releases and the vendors' releases found since, then catalog releases they do not already cover; newest first per file.</summary>
    internal static IReadOnlyList<PinnedFile> AllReleases
    {
        get
        {
            lock (CatalogGate)
            {
                if (_allReleases is not null) return _allReleases;
                _catalog ??= LoadCatalogCache();
                _watched ??= LoadWatched();
                // Releases found on GitHub first, so a new SDK that ships a file unchanged is the one it is known by.
                var known = _watched.Concat(PinnedFiles).ToList();
                var pinned = known.Select(file => (file.FileName.ToLowerInvariant(), NormalizeVersion(file.Version)))
                    .ToHashSet();
                return _allReleases = known
                    .Concat(_catalog.Where(file => !pinned.Contains((file.FileName.ToLowerInvariant(), NormalizeVersion(file.Version)))))
                    .GroupBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(group => group.OrderByDescending(ReleaseOrder))
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Newest first. FSR 3.1's loader version does not follow its release
    /// (1.0.2.38022 is FSR 3.1.2, older than 3.1.4 at 1.0.1.41314), so AMD
    /// files sort by the release they are known by; the rest by file version.
    /// </summary>
    private static Version ReleaseOrder(PinnedFile file) =>
        (file.Vendor == "AMD" ? TryParseVersion(file.Display) : null) ?? TryParseVersion(file.Version) ?? new Version(0, 0);

    /// <summary>A catalog older than a day is fetched again in the background; the current one keeps answering.</summary>
    internal static void RefreshCatalogInBackground()
    {
        lock (CatalogGate)
        {
            if (_catalogRefresh is { IsCompleted: false }) return;
            try
            {
                var info = new FileInfo(CatalogPath);
                if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < CatalogMaxAge) return;
            }
            catch { return; }
            _catalogRefresh = Task.Run(RefreshCatalogAsync);
        }
    }

    private static async Task RefreshCatalogAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await Http.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > ManifestMaxBytes) throw new InvalidDataException("The manifest is larger than Exo accepts.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var bounded = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (bounded.Length + read > ManifestMaxBytes) throw new InvalidDataException("The manifest is larger than Exo accepts.");
                bounded.Write(buffer, 0, read);
            }
            var releases = ParseManifest(bounded.ToArray());
            if (releases.Count == 0) throw new InvalidDataException("The manifest listed no usable releases.");
            Directory.CreateDirectory(CacheRoot);
            var temp = CatalogPath + ".tmp";
            await File.WriteAllBytesAsync(temp, bounded.ToArray(), CancellationToken.None).ConfigureAwait(false);
            File.Move(temp, CatalogPath, overwrite: true);
            lock (CatalogGate)
            {
                _catalog = releases;
                _allReleases = null;
            }
            AppLog.Info($"Upscaler catalog: {releases.Count} releases from DLSS Swapper's manifest.");
        }
        catch (Exception ex)
        {
            AppLog.Info("Upscaler catalog not refreshed (the pinned and last-known releases still apply): " + ex.Message);
            // Do not ask again until tomorrow when the server is down.
            try { if (File.Exists(CatalogPath)) File.SetLastWriteTimeUtc(CatalogPath, DateTime.UtcNow); } catch { }
        }
    }

    private static IReadOnlyList<PinnedFile> LoadCatalogCache()
    {
        try { return File.Exists(CatalogPath) ? ParseManifest(File.ReadAllBytes(CatalogPath)) : []; }
        catch (Exception ex)
        {
            AppLog.Debug("Upscaler catalog cache unreadable: " + ex.Message);
            return [];
        }
    }

    internal static IReadOnlyList<PinnedFile> ParseManifest(byte[] json)
    {
        var releases = new List<PinnedFile>();
        JsonNode? document;
        try { document = JsonNode.Parse(json); }
        catch (JsonException) { return releases; } // Malformed: no releases, so the pins apply.
        if (document is not JsonObject root) return releases;
        foreach (var (kind, fileName, vendor, product) in CatalogKinds)
        {
            if (root[kind] is not JsonArray list) continue;
            foreach (var node in list)
            {
                if (node is not JsonObject entry) continue;
                if (entry["is_dev_file"]?.GetValue<bool>() == true || entry["is_signature_valid"]?.GetValue<bool>() != true) continue;
                var version = entry["version"]?.GetValue<string>();
                var url = entry["download_url"]?.GetValue<string>();
                var md5 = entry["md5_hash"]?.GetValue<string>();
                var size = entry["file_size"]?.GetValue<long>() ?? 0;
                var zipSize = entry["zip_file_size"]?.GetValue<long>() ?? 0;
                if (TryParseVersion(version) is not { } parsed || size <= 0 || zipSize <= 0) continue;
                if (md5 is not { Length: 32 } || !md5.All(Uri.IsHexDigit)) continue;
                if (url is null || !url.StartsWith(DlssSwapper + kind + "/", StringComparison.Ordinal) || !url.EndsWith(".zip", StringComparison.Ordinal)) continue;
                // DLSS 1 is not interchangeable with 2+; Exo never offers it.
                if (vendor == "NVIDIA" && parsed.Major < 2) continue;
                var label = entry["additional_label"]?.GetValue<string>()?.Trim() ?? "";
                var display = TrimVersion(version) ?? version!;
                // FSR 3.1 builds are known by their 3.1.x label, not the loader's 1.0.x
                // version; one without that label cannot be placed and is left out.
                if (vendor == "AMD")
                {
                    if (label.Length == 0 || !char.IsDigit(label[0])) continue;
                    display = label;
                }
                var sdk = product + " " + display + (label.Length > 0 && !(vendor == "AMD" && char.IsDigit(label[0])) ? " " + label : "");
                releases.Add(new PinnedFile(fileName, CatalogReleaseId(kind, vendor, version!, display), sdk, version!, display,
                    vendor + "/catalog", "", "", size, "", new ZipSource(url, zipSize), md5.ToUpperInvariant()));
            }
        }
        return releases;
    }

    /// <summary>
    /// One id per release across the files that load together, so choosing it
    /// for one sets its siblings (<see cref="CoupledFiles"/>): DLSS's three
    /// files share their version, FSR 3.1's DirectX 12 and Vulkan files their
    /// 3.1.x release. XeSS files keep their own.
    /// </summary>
    private static string CatalogReleaseId(string kind, string vendor, string version, string display) => vendor switch
    {
        "NVIDIA" => "catalog-dlss-" + display,
        "AMD" => "catalog-fsr-" + display,
        _ => "catalog-" + kind + "-" + version,
    };

    private static string NormalizeVersion(string version) =>
        TryParseVersion(version) is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)).ToString() : version;

    /// <summary>A pin's built-in SHA-256, or a catalog file's once it was verified; null before that.</summary>
    internal static string? KnownSha(PinnedFile file) => file.Sha256.Length > 0 ? file.Sha256 : TrustedSha(file);

    /// <summary>The SHA-256 recorded the first time a catalog file passed verification.</summary>
    private static string? TrustedSha(PinnedFile file)
    {
        lock (CatalogGate)
        {
            _trustedSha ??= LoadTrusted();
            return _trustedSha.TryGetValue(file.Url, out var sha) ? sha : null;
        }
    }

    private static void RecordTrusted(PinnedFile file, string sha256)
    {
        lock (CatalogGate)
        {
            _trustedSha ??= LoadTrusted();
            _trustedSha[file.Url] = sha256;
            try
            {
                Directory.CreateDirectory(CacheRoot);
                var temp = TrustedPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_trustedSha));
                File.Move(temp, TrustedPath, overwrite: true);
            }
            catch (Exception ex) { AppLog.Warn("Upscaler catalog trust record not saved: " + ex.Message); }
        }
        AppLog.Info($"Upscaler catalog file verified: file={file.FileName}; release={file.Release}; sha256={sha256}.");
    }

    private static Dictionary<string, string> LoadTrusted()
    {
        try
        {
            return File.Exists(TrustedPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TrustedPath)) ?? []
                : [];
        }
        catch { return []; }
    }

    private static string? FileMd5(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(MD5.HashData(fs));
        }
        catch { return null; }
    }
}
