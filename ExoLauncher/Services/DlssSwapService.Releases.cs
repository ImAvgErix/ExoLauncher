using System.IO.Compression;
using System.Security.Cryptography;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The upscaler files Exo can put into a game, pinned to the vendors' own
/// GitHub repositories at a tagged commit. Each file is fetched from
/// raw.githubusercontent.com at that commit and accepted only when its size
/// and SHA-256 match the values below, and it carries the vendor's valid
/// Authenticode signature. Newer builds also come from DLSS Swapper's public
/// manifest (<see cref="AllReleases"/>, DlssSwapService.Catalog.cs), verified
/// the same way before first use; these pins are the floor that never depends
/// on it.
///
/// NVIDIA ships DLSS in games before it tags the SDK. Those builds come from
/// DLSS Swapper's archive (<see cref="PinnedFile.Zip"/>), pinned the same way:
/// the zip's size, then the DLL's size, SHA-256, NVIDIA signature and version.
///
/// Verified October 12, 2026: every file's git blob SHA-1 matched the tree of
/// the tagged commit, and its signature verified as NVIDIA Corporation,
/// Advanced Micro Devices or Intel Corporation. DLSS 310.9.16 verified
/// October 15, 2026: MD5 matched DLSS Swapper's manifest, signed by NVIDIA.
/// </summary>
public sealed partial class DlssSwapService
{
    /// <param name="Release">Stable id the page sends back, e.g. <c>dlss-310.9.1</c>.</param>
    /// <param name="Sdk">What the vendor calls the release, shown next to the version.</param>
    /// <param name="Version">The file's own four-part version resource.</param>
    /// <param name="Display">The version people know the file by.</param>
    public sealed record PinnedFile(
        string FileName,
        string Release,
        string Sdk,
        string Version,
        string Display,
        string Repo,
        string Commit,
        string RepoPath,
        long Size,
        string Sha256,
        ZipSource? Zip = null,
        string? Md5 = null)
    {
        internal string Url => Zip?.Url ?? $"https://raw.githubusercontent.com/{Repo}/{Commit}/{RepoPath}";
        internal string Vendor => Repo.StartsWith("NVIDIA", StringComparison.Ordinal) ? "NVIDIA"
            : Repo.StartsWith("intel", StringComparison.Ordinal) ? "Intel" : "AMD";
    }

    /// <summary>A file inside a zip at a fixed URL; <paramref name="Size"/> is the zip's.</summary>
    public sealed record ZipSource(string Url, long Size);

    private const string DlssSwapper = "https://dlss-swapper-downloads.beeradmoore.com/";
    private const string DlssRepo = "NVIDIA/DLSS";
    private const string DlssLatest = "374959484e79a640feaba44c93ac8cfb0a03f5b5"; // v310.9.1, Sep 8 2026
    private const string DlssPrevious = "a291cc7d2cc642a51566f3dfd5376f635cd1b284"; // v310.7.0, Jun 23 2026
    private const string FfxRepo = "GPUOpen-LibrariesAndSDKs/FidelityFX-SDK";
    private const string FfxLatest = "60f4ea81909200d8542eca14dccb2628b763a9a3"; // v2.3.0, Jun 24 2026
    private const string FfxPrevious = "e236f2304dcda35f282fdddd085f41e2ff48c86a"; // v2.2.0, Mar 23 2026
    private const string Ffx31 = "c6efa6bf7f2027b3ec94f28578bb5965eabb9e55"; // v1.1.4 (FSR 3.1.4)
    private const string XessRepo = "intel/xess";
    private const string XessLatest = "8fe81bdbbaf00b3c1b733fd0d830c333dc84e6f0"; // v3.0.2, Jul 24 2026
    private const string XessPrevious = "b2e8f705ca9a36586010fe7615a470fcf0608c35"; // v2.1.1, Nov 13 2025

    private const string DlssPath = "lib/Windows_x86_64/rel/";
    private const string FfxPath = "Kits/FidelityFX/signedbin/";

    /// <summary>Newest release first within each file name.</summary>
    internal static readonly PinnedFile[] PinnedFiles =
    [
        new("nvngx_dlss.dll", "dlss-310.9.16", "DLSS 310.9.16", "310.9.16.0", "310.9.16", DlssRepo, "", "", 74187376, "858ccff96360c3098db9264cd6f13d04a027e09b6ce7ad5bbc297570e824b4f2",
            new(DlssSwapper + "dlss/dlss_v310.9.16.0_C77DDAE731F54720DDEEEE06AC356C5A.zip", 45333524)),
        new("nvngx_dlss.dll", "dlss-310.9.1", "DLSS 310.9.1", "310.9.1.0", "310.9.1", DlssRepo, DlssLatest, DlssPath + "nvngx_dlss.dll", 58956912, "3975567b8943c53acce397f2b72380092f84f162d00b0d2c7d08a1025c563983"),
        new("nvngx_dlss.dll", "dlss-310.7.0", "DLSS 310.7.0", "310.7.0.0", "310.7.0", DlssRepo, DlssPrevious, DlssPath + "nvngx_dlss.dll", 58977904, "be6e434a94ca32499515eb62ca0e6c274526055d568d0426e4c652dcdfb6ee6e"),
        new("nvngx_dlssd.dll", "dlss-310.9.16", "DLSS 310.9.16", "310.9.16.0", "310.9.16", DlssRepo, "", "", 79669360, "7006c3a729276e876e06a97c6262672a1be96a58d69b0e85877f911146b98fbe",
            new(DlssSwapper + "dlss_d/dlss_d_v310.9.16.0_DF0F34E37778B6A4652F6A5B5567B485.zip", 38231551)),
        new("nvngx_dlssd.dll", "dlss-310.9.1", "DLSS 310.9.1", "310.9.1.0", "310.9.1", DlssRepo, DlssLatest, DlssPath + "nvngx_dlssd.dll", 48343664, "4bc7ea5fcb2f32cf86bc2cb072e8d2860914a0be511cbdcb2fc206c1d9d83b80"),
        new("nvngx_dlssd.dll", "dlss-310.7.0", "DLSS 310.7.0", "310.7.0.0", "310.7.0", DlssRepo, DlssPrevious, DlssPath + "nvngx_dlssd.dll", 40946800, "f4e97624f70fbb769acb11ebd751b512ecc9463d4bd6aef04896d3956e6084a0"),
        new("nvngx_dlssg.dll", "dlss-310.9.16", "DLSS 310.9.16", "310.9.16.0", "310.9.16", DlssRepo, "", "", 7506544, "cadee1aa6db3907babdc3285da2aebc4553d92b89c38ba3eae8abd8bba1bea72",
            new(DlssSwapper + "dlss_g/dlss_g_v310.9.16.0_F80D3622BE5AF2DEA01EA7FC4A741520.zip", 3740212)),
        new("nvngx_dlssg.dll", "dlss-310.9.1", "DLSS 310.9.1", "310.9.1.0", "310.9.1", DlssRepo, DlssLatest, DlssPath + "nvngx_dlssg.dll", 7460976, "ff6e90eb78b827927dff5b4ecc6b1c870c2e9bca29ed9f48c7d348cc9e170b82"),
        new("nvngx_dlssg.dll", "dlss-310.7.0", "DLSS 310.7.0", "310.7.0.0", "310.7.0", DlssRepo, DlssPrevious, DlssPath + "nvngx_dlssg.dll", 7519856, "135eaf0733c1e37381a8c28abcf7a862404a54132b81787c04e35d09efc5e36f"),

        new("amd_fidelityfx_dx12.dll", "fsr-1.1.4", "FidelityFX SDK 1.1.4", "1.0.1.41314", "3.1.4", FfxRepo, Ffx31, "PrebuiltSignedDLL/amd_fidelityfx_dx12.dll", 6675664, "12a5081257ec95b0b53ad51b4a87fb3c03f97fe0bbb59f9496968f8d50ef93a6"),
        new("amd_fidelityfx_vk.dll", "fsr-1.1.4", "FidelityFX SDK 1.1.4", "1.0.1.41314", "3.1.4", FfxRepo, Ffx31, "PrebuiltSignedDLL/amd_fidelityfx_vk.dll", 9332432, "a1624cc4238fef046f30c4d80ce3f47be63fc5f5373f49e3ee9edb9960f54c78"),

        new("amd_fidelityfx_loader_dx12.dll", "fsr-2.3.0", "FidelityFX SDK 2.3.0", "2.3.0.2740", "2.3.0", FfxRepo, FfxLatest, FfxPath + "amd_fidelityfx_loader_dx12.dll", 26376, "e2d85aa05a9bd9ed8b38935fdf5199372cca6f74c12015143bb6f945ee1608aa"),
        new("amd_fidelityfx_loader_dx12.dll", "fsr-2.2.0", "FidelityFX SDK 2.2.0", "2.2.0.0", "2.2.0", FfxRepo, FfxPrevious, FfxPath + "amd_fidelityfx_loader_dx12.dll", 25864, "d98c027fbd4cc074a1c48574eecd58ded2bc9e4cb6bfc5c28d6595ab5037d371"),
        new("amd_fidelityfx_upscaler_dx12.dll", "fsr-2.3.0", "FidelityFX SDK 2.3.0", "4.1.1.2740", "4.1.1", FfxRepo, FfxLatest, FfxPath + "amd_fidelityfx_upscaler_dx12.dll", 28761864, "d0dcccc74a43c44ba435b7a369b456e0970d8a4464e4bd683119b374f2c9fb46"),
        new("amd_fidelityfx_upscaler_dx12.dll", "fsr-2.2.0", "FidelityFX SDK 2.2.0", "4.1.0.0", "4.1.0", FfxRepo, FfxPrevious, FfxPath + "amd_fidelityfx_upscaler_dx12.dll", 15605520, "ec7ed3ca674e288240e6f04b986342aece47454c41d9b0959449e82e22bd7f6d"),
        new("amd_fidelityfx_framegeneration_dx12.dll", "fsr-2.3.0", "FidelityFX SDK 2.3.0", "4.0.1.2740", "4.0.1", FfxRepo, FfxLatest, FfxPath + "amd_fidelityfx_framegeneration_dx12.dll", 40085776, "02297beedd285e822d3a64f314cf00faf378dcec0edc47ff0c4dd71b3a8c2f18"),
        new("amd_fidelityfx_framegeneration_dx12.dll", "fsr-2.2.0", "FidelityFX SDK 2.2.0", "4.0.0.0", "4.0.0", FfxRepo, FfxPrevious, FfxPath + "amd_fidelityfx_framegeneration_dx12.dll", 38246160, "8deaf970b84dac8f761fefa546e73517e02b34a6bb05a590a74e7faea03baa4a"),
        new("amd_fidelityfx_denoiser_dx12.dll", "fsr-2.3.0", "FidelityFX SDK 2.3.0", "1.2.0.2740", "1.2.0", FfxRepo, FfxLatest, FfxPath + "amd_fidelityfx_denoiser_dx12.dll", 13782280, "48f1e5888ba6a0a3d59a98b9751e37c392b0f7b8c223d0082d5c1f40642879d3"),
        new("amd_fidelityfx_denoiser_dx12.dll", "fsr-2.2.0", "FidelityFX SDK 2.2.0", "1.1.0.0", "1.1.0", FfxRepo, FfxPrevious, FfxPath + "amd_fidelityfx_denoiser_dx12.dll", 3633936, "f003e1298f7bc09e76578f995f64e46e6f5355f0597b88ff85e9a9922fdf92c6"),
        new("amd_fidelityfx_radiancecache_dx12.dll", "fsr-2.3.0", "FidelityFX SDK 2.3.0", "0.9.0.2740", "0.9.0", FfxRepo, FfxLatest, FfxPath + "amd_fidelityfx_radiancecache_dx12.dll", 798992, "256db18d924c8cd38923d04e3ecd210695d3f0f796b240eab9663ad4d54e31a0"),
        new("amd_fidelityfx_radiancecache_dx12.dll", "fsr-2.2.0", "FidelityFX SDK 2.2.0", "0.9.0.0", "0.9.0", FfxRepo, FfxPrevious, FfxPath + "amd_fidelityfx_radiancecache_dx12.dll", 795912, "e87b31ec27f8dedcda5d669916c7504d972907e09caf81172f6d14d42d98123e"),

        // XeSS SDK 3.0.2 ships the same libxess.dll and libxess_dx11.dll bytes
        // as 2.1.1 (2.0.2.68); only frame generation and XeLL changed.
        new("libxess.dll", "xess-3.0.2", "XeSS SDK 3.0.2", "2.0.2.68", "2.0.2.68", XessRepo, XessLatest, "bin/libxess.dll", 77795704, "251659dd84a3e84de67c886a4186e01f3eca49b00641906fe38bb6b807e5d5b7"),
        new("libxess_dx11.dll", "xess-3.0.2", "XeSS SDK 3.0.2", "2.0.2.68", "2.0.2.68", XessRepo, XessLatest, "bin/libxess_dx11.dll", 156016, "c7cfe86f0c9d94e4fb3696d3cd5035e2bbb6a8b1b0572f8b7395a4cdfd0c625e"),
        new("libxess_fg.dll", "xess-3.0.2", "XeSS SDK 3.0.2", "1.3.1.78", "1.3.1.78", XessRepo, XessLatest, "bin/libxess_fg.dll", 22957432, "ec5e0c65e075570c6ede72618bb666d0be0c2e10b2ea9762c0fe8cb8e375ab27"),
        new("libxess_fg.dll", "xess-2.1.1", "XeSS SDK 2.1.1", "1.2.2.118", "1.2.2.118", XessRepo, XessPrevious, "bin/libxess_fg.dll", 47119216, "e042a111df5e4d3e5fe9d1779b54890f42a54d3ea47487673b07003b931f2568"),
        new("libxell.dll", "xess-3.0.2", "XeSS SDK 3.0.2", "1.3.2.10", "1.3.2.10", XessRepo, XessLatest, "bin/libxell.dll", 415368, "d2030dcd694fda8f2ec7e044b13e6db8f0b56d4ba9113a5efad334e3f3ded8c7"),
        new("libxell.dll", "xess-2.1.1", "XeSS SDK 2.1.1", "1.2.1.13", "1.2.1.13", XessRepo, XessPrevious, "bin/libxell.dll", 360312, "7d1133af79d1ef02e214a91680d6622037583eee09b0667544bb92e3553118a9"),
    ];

    /// <summary>
    /// Files that load each other and must come from one release. Choosing a
    /// release for one sets the others the game ships in the same folder.
    /// </summary>
    private static readonly string[][] CoupledFiles =
    [
        ["amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll",
         "amd_fidelityfx_denoiser_dx12.dll", "amd_fidelityfx_radiancecache_dx12.dll"],
        ["libxess_fg.dll", "libxell.dll"],
        // NVIDIA ships super resolution, frame generation and ray reconstruction
        // as one release; FSR 3.1's two loaders are one release too.
        ["nvngx_dlss.dll", "nvngx_dlssg.dll", "nvngx_dlssd.dll"],
        ["amd_fidelityfx_dx12.dll", "amd_fidelityfx_vk.dll"],
    ];

    internal static IReadOnlyList<PinnedFile> ReleasesFor(string fileName) =>
        AllReleases.Where(file => file.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();

    internal static PinnedFile? LatestFor(string fileName) =>
        AllReleases.FirstOrDefault(file => file.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    internal static PinnedFile? FindPinned(string fileName, string release) =>
        AllReleases.FirstOrDefault(file =>
            file.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
            file.Release.Equals(release, StringComparison.OrdinalIgnoreCase));

    internal static IEnumerable<string> CoupledWith(string fileName) =>
        CoupledFiles.FirstOrDefault(set => set.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            ?.Where(name => !name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) ?? [];

    /// <summary>Content-addressed, so two releases with identical bytes share one file.</summary>
    private static string PinnedCachePath(PinnedFile file) =>
        Path.Combine(CacheRoot, "pinned", file.Sha256.Length > 0 ? file.Sha256[..16] : "m" + file.Md5![..15], file.FileName);

    /// <summary>Cheap: presence and size only. Swaps verify the full hash.</summary>
    internal static bool IsPinnedCached(PinnedFile file)
    {
        try
        {
            var info = new FileInfo(PinnedCachePath(file));
            return info.Exists && info.Length == file.Size;
        }
        catch { return false; }
    }

    internal static bool IsAllowedDownloadUrl(string url) =>
        AllReleases.Any(file => file.Url.Equals(url, StringComparison.Ordinal));

    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    /// <summary>
    /// Returns a verified local copy of each pinned file, downloading only what
    /// the cache lacks. A file that fails its size, SHA-256, signature or
    /// export check is deleted and the whole call fails.
    /// </summary>
    private static async Task<IReadOnlyDictionary<PinnedFile, string>> EnsurePinnedAsync(
        IEnumerable<PinnedFile> wanted,
        CancellationToken ct)
    {
        var result = new Dictionary<PinnedFile, string>();
        await DownloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var file in wanted.Distinct())
            {
                ct.ThrowIfCancellationRequested();
                var dest = PinnedCachePath(file);
                if (!IsVerifiedPinned(dest, file))
                {
                    TryDelete(dest);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (!TryAdoptLegacyCache(file, dest))
                    {
                        AppLog.Info($"Upscaler download: vendor={file.Vendor}; file={file.FileName}; release={file.Release}; bytes={file.Size}.");
                        if (file.Zip is { } zip) await DownloadFromZipAsync(file, zip, dest, ct).ConfigureAwait(false);
                        else await DownloadToFileAsync(file.Url, dest, file.Size, ct).ConfigureAwait(false);
                    }
                    if (!IsVerifiedPinned(dest, file))
                    {
                        TryDelete(dest);
                        throw new InvalidDataException($"{file.FileName} from {file.Sdk} did not match its published SHA-256 or signature.");
                    }
                }
                result[file] = dest;
            }
            PruneCache();
        }
        finally
        {
            DownloadGate.Release();
        }
        return result;
    }

    /// <summary>
    /// A pin by its SHA-256. A catalog file by the SHA-256 recorded at its first
    /// verification, or, before that, by the manifest's MD5; in every case the
    /// vendor's signature, exports and the exact version are checked too.
    /// </summary>
    internal static bool IsVerifiedPinned(string path, PinnedFile file)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size) return false;
            if (!IsTrustedReplacementDll(path, file.FileName) || CompareVersions(TryFileVersion(path), file.Version) != 0) return false;
            var sha = FileSha256(path);
            if (sha is null) return false;
            if (file.Sha256.Length > 0) return file.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase);
            if (TrustedSha(file) is { } known) return known.Equals(sha, StringComparison.OrdinalIgnoreCase);
            if (file.Md5 is null || !file.Md5.Equals(FileMd5(path), StringComparison.OrdinalIgnoreCase)) return false;
            RecordTrusted(file, sha);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Earlier Exo builds kept the same official files in per-vendor folders.
    /// A byte-identical copy is moved in instead of downloaded again.
    /// </summary>
    private static bool TryAdoptLegacyCache(PinnedFile file, string dest)
    {
        try
        {
            var pinnedRoot = Path.Combine(CacheRoot, "pinned");
            foreach (var candidate in Directory.EnumerateFiles(CacheRoot, file.FileName, SearchOption.AllDirectories))
            {
                if (candidate.StartsWith(pinnedRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Sha256.Length == 0 || new FileInfo(candidate).Length != file.Size) continue;
                if (!file.Sha256.Equals(FileSha256(candidate), StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(candidate, dest, overwrite: true);
                return true;
            }
        }
        catch (Exception ex) { AppLog.Debug("Upscaler legacy cache skipped: " + ex.Message); }
        return false;
    }

    /// <summary>Keeps only pinned files and the scan cache; everything else in the cache is from older builds.</summary>
    private static void PruneCache()
    {
        try
        {
            var root = CacheRoot;
            if (!Directory.Exists(root)) return;
            // Move byte-identical official files out of older builds' folders
            // first, so pruning them never costs a download later.
            foreach (var file in PinnedFiles)
            {
                if (IsPinnedCached(file)) continue;
                var dest = PinnedCachePath(file);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                TryAdoptLegacyCache(file, dest);
            }
            var keep = AllReleases.Select(file => Path.GetFullPath(PinnedCachePath(file)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(dir).Equals("pinned", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var sub in Directory.GetDirectories(dir))
                        if (!Directory.EnumerateFiles(sub).Any(path => keep.Contains(Path.GetFullPath(path))))
                            Directory.Delete(sub, recursive: true);
                    continue;
                }
                Directory.Delete(dir, recursive: true);
            }
            TryDelete(Path.Combine(root, "catalog-v1.json"));
            // Keep upscalers updated's list of games Exo wrote to; the feature is gone.
            TryDelete(Path.Combine(root, "written-games.json"));
            // catalog-v2.json and catalog-trusted.json are the live catalog and its trust record.
            TryDelete(Path.Combine(root, "status-v1.json"));
        }
        catch (Exception ex) { AppLog.Debug("Upscaler cache prune skipped: " + ex.Message); }
    }

    /// <summary>Downloads the pinned zip, then takes out only the one DLL, refusing anything larger than pinned.</summary>
    private static async Task DownloadFromZipAsync(PinnedFile file, ZipSource zip, string destination, CancellationToken ct)
    {
        var archive = destination + ".zip-" + Guid.NewGuid().ToString("N");
        var temp = destination + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            await DownloadToFileAsync(zip.Url, archive, zip.Size, ct).ConfigureAwait(false);
            using (var read = ZipFile.OpenRead(archive))
            {
                var entry = read.Entries.SingleOrDefault(e => e.Name.Equals(file.FileName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"The archive has no {file.FileName}.");
                if (entry.Length != file.Size)
                    throw new InvalidDataException("The file in the archive is not the size Exo expects.");
                entry.ExtractToFile(temp, overwrite: false);
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            TryDelete(archive);
            TryDelete(temp);
        }
    }

    private static async Task DownloadToFileAsync(string url, string destination, long expectedSize, CancellationToken ct)
    {
        // A pinned or catalog file, or a vendor's new SDK file at its release's commit (DlssSwapService.VendorWatch.cs).
        static bool Allowed(string url) => IsAllowedDownloadUrl(url) || IsVendorSdkUrl(url);
        if (!Allowed(url))
            throw new InvalidDataException("Exo only downloads the upscaler files it has pinned.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var idle = TimeSpan.FromSeconds(30);
        inactivity.CancelAfter(idle);
        var token = inactivity.Token;
        var temp = destination + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.ToString() is { } final && !Allowed(final))
                throw new InvalidDataException("The download was redirected away from the vendor's repository.");
            if (response.Content.Headers.ContentLength is { } length && length != expectedSize)
                throw new InvalidDataException("The vendor's file is not the size Exo expects.");
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    inactivity.CancelAfter(idle);
                    total += read;
                    if (total > expectedSize)
                        throw new InvalidDataException("The vendor's file is larger than Exo expects.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (total != expectedSize)
                    throw new InvalidDataException("The download ended before the whole file arrived.");
            }
            File.Move(temp, destination, overwrite: true);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && inactivity.IsCancellationRequested)
        {
            throw new TimeoutException("The download stalled. Try again.", ex);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = ExoHttp.Create(TimeSpan.FromMinutes(8), new SocketsHttpHandler { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("ExoLauncher", "1.0"));
        return http;
    }

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
}
