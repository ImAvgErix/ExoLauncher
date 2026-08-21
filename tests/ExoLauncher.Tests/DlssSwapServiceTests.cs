using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class DlssSwapServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "exo-dlss-swap-" + Guid.NewGuid().ToString("N"));

    public DlssSwapServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* fixture */ }
    }

    [Theory]
    [InlineData("https://dlss-swapper-downloads.beeradmoore.com/dlss/nvngx_dlss_v310.4.0.0.zip")]
    [InlineData("https://beeradmoore.github.io/dlss-swapper/manifest.json")]
    [InlineData("https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/download/v1.1.4/sdk.zip")]
    [InlineData("https://github.com/NVIDIA/DLSS/releases/download/v310.4.0/nvngx_dlss.dll")]
    [InlineData("https://raw.githubusercontent.com/NVIDIA/DLSS/a291cc7d2cc642a51566f3dfd5376f635cd1b284/lib/Windows_x86_64/rel/nvngx_dlss.dll")]
    [InlineData("https://github.com/NVIDIA-RTX/Streamline/releases/download/v2.8.0/streamline.zip")]
    [InlineData("https://github.com/intel/xess/releases/download/v2.1.0/libxess.dll")]
    [InlineData("https://developer.download.nvidia.com/downloads/nvngx_dlss.zip")]
    public void DownloadHosts_AllowOfficialSwapperAndVendorOrigins(string url) =>
        Assert.True(DlssSwapService.IsAllowedDownloadUrl(url));

    [Theory]
    [InlineData("http://dlss-swapper-downloads.beeradmoore.com/dlss/file.zip")]
    [InlineData("https://dlss-swapper-downloads.beeradmoore.com.evil.example/file.zip")]
    [InlineData("https://evil.beeradmoore.github.io/dlss-swapper/file.zip")]
    [InlineData("https://github.com/evil/DLSS/releases/download/v1/file.zip")]
    [InlineData("https://raw.githubusercontent.com/attacker/repo/main/file.zip")]
    [InlineData("https://raw.githubusercontent.com/NVIDIA/DLSS/main/lib/Windows_x86_64/rel/nvngx_dlss.dll")]
    [InlineData("https://raw.githubusercontent.com/NVIDIA/DLSS/a291cc7d2cc642a51566f3dfd5376f635cd1b284/doc/nvngx_dlss.dll")]
    [InlineData("https://gist.githubusercontent.com/attacker/id/raw/file.zip")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/file")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/file")]
    [InlineData("https://developer.download.nvidia.com:444/file.zip")]
    [InlineData("https://attacker@developer.download.nvidia.com/file.zip")]
    [InlineData("https://techpowerup.com/download/nvidia-dlss-dlls/file.zip")]
    [InlineData("https://filedn.eu/lbedFKCSyc9BjXqfklAANEV/dlss-swapper/file.zip")]
    [InlineData("https://developer.download.nvidia.com.evil.example/file.zip")]
    public void DownloadHosts_RejectHttpLookalikesAndRandomMirrors(string url) =>
        Assert.False(DlssSwapService.IsAllowedDownloadUrl(url));

    [Fact]
    public void DownloadRedirects_OnlyAllowGitHubAssetHostsFromAnApprovedVendorRelease()
    {
        const string asset = "https://release-assets.githubusercontent.com/github-production-release-asset/1/file";
        Assert.True(DlssSwapService.IsAllowedDownloadRedirect(
            "https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/download/v1.1.4/sdk.zip",
            asset));
        Assert.False(DlssSwapService.IsAllowedDownloadRedirect(
            "https://github.com/attacker/repo/releases/download/v1/sdk.zip",
            asset));
        Assert.False(DlssSwapService.IsAllowedDownloadRedirect(
            "https://dlss-swapper-downloads.beeradmoore.com/dlss/file.zip",
            asset));
    }

    [Fact]
    public void ReplacementDlls_RequireARealTrustedVendorSignature()
    {
        var fake = Path.Combine(_root, "nvngx_dlss.dll");
        WriteDummyPe(fake);

        Assert.False(DlssSwapService.HasTrustedVendorSignature(fake, "nvngx_dlss.dll"));
        Assert.False(DlssSwapService.IsTrustedReplacementDll(fake, "nvngx_dlss.dll"));
        Assert.False(File.Exists(fake + DlssSwapService.PinnedHashSuffix));
        Assert.False(DlssSwapService.HasTrustedVendorSignature(fake, "unknown.dll"));
    }

    [Fact]
    public void ReplacementDlls_RequireTheExpectedVendorApiExports()
    {
        Assert.True(DlssSwapService.ExportsMatchFileName(
            "nvngx_dlss.dll",
            ["NVSDK_NGX_D3D12_Init", "NVSDK_NGX_D3D12_CreateFeature"]));
        Assert.False(DlssSwapService.ExportsMatchFileName(
            "nvngx_dlss.dll",
            ["NVSDK_NGX_D3D12_Init"]));
        Assert.True(DlssSwapService.ExportsMatchFileName(
            "amd_fidelityfx_dx12.dll",
            ["ffxCreateContext", "ffxDestroyContext", "ffxConfigure", "ffxQuery", "ffxDispatch"]));
        Assert.True(DlssSwapService.ExportsMatchFileName(
            "libxess.dll",
            ["INTC_D3D12_XessCreateContext", "INTC_D3D12_XessGetVersion"]));
        Assert.True(DlssSwapService.ExportsMatchFileName(
            "libxess_fg.dll",
            ["xefgSwapChainGetVersion", "xefgSwapChainD3D12CreateContext"]));
        Assert.False(DlssSwapService.ExportsMatchFileName("unknown.dll", ["ffxCreateContext"]));

        var compatible = Path.Combine(_root, "compatible", "nvngx_dlss.dll");
        WritePeWithExports(
            compatible,
            "NVSDK_NGX_D3D12_Init",
            "NVSDK_NGX_D3D12_CreateFeature");
        Assert.True(DlssSwapService.HasCompatibleExports(compatible, "nvngx_dlss.dll"));

        var fake = Path.Combine(_root, "amd_fidelityfx_dx12.dll");
        WriteDummyPe(fake);
        Assert.False(DlssSwapService.HasCompatibleExports(fake, "amd_fidelityfx_dx12.dll"));

        var malformed = Path.Combine(_root, "malformed", "amd_fidelityfx_dx12.dll");
        WriteMalformedExportPe(malformed);
        Assert.True(DlssSwapService.IsValidAmd64Pe(malformed));
        Assert.False(DlssSwapService.HasCompatibleExports(malformed, "amd_fidelityfx_dx12.dll"));
    }

    [Fact]
    public void ReplacementDlls_AcceptObservedVendorMetadataWithoutAcceptingLookalikes()
    {
        Assert.True(DlssSwapService.VendorMetadataMatches(
            "nvngx_dlss.dll", "CL 37997616", "NVIDIA"));
        Assert.True(DlssSwapService.VendorMetadataMatches(
            "amd_fidelityfx_upscaler_dx12.dll", null, "Advanced Micro Devices, Inc."));
        Assert.True(DlssSwapService.VendorMetadataMatches(
            "libxess.dll", "libxess.dll", "Intel Corporation"));
        Assert.False(DlssSwapService.VendorMetadataMatches(
            "nvngx_dlss.dll", "unrelated.dll", "NVIDIA"));
        Assert.False(DlssSwapService.VendorMetadataMatches(
            "libxess.dll", "libxess.dll", "Intel Support Services LLC"));
    }

    [Fact]
    public void Detect_FindsNestedDlssFsrAndXess_AndIgnoresUnknownDlls()
    {
        var gameDir = Path.Combine(_root, "Control");
        var bin = Path.Combine(gameDir, "Win64");
        Directory.CreateDirectory(bin);
        WriteDummy(Path.Combine(bin, "nvngx_dlss.dll"), "dlss-old");
        WriteDummy(Path.Combine(bin, "amd_fidelityfx_dx12.dll"), "fsr-old");
        WriteDummy(Path.Combine(bin, "libxess.dll"), "xess-old");
        WriteDummy(Path.Combine(bin, "version.dll"), "proxy");

        var found = new DlssSwapService().Detect([Game("steam:control", gameDir)]).ToList();

        Assert.Equal(3, found.Count);
        Assert.Contains(found, item => item.FileName.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, item => item.FileName.Equals("amd_fidelityfx_dx12.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, item => item.FileName.Equals("libxess.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, item => item.FileName.Equals("version.dll", StringComparison.OrdinalIgnoreCase));
        Assert.All(found, item => Assert.True(item.Present));
    }

    [Fact]
    public void Detect_ResolvesInstallRootWhenPathIsTheGameExe()
    {
        var gameDir = Path.Combine(_root, "Portable");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        WriteDummy(exe, "exe");
        WriteDummy(Path.Combine(gameDir, "nvngx_dlss.dll"), "dlss");

        var found = new DlssSwapService().Detect([Game("local:portable", exe)]).ToList();

        Assert.Single(found);
        Assert.Equal("nvngx_dlss.dll", found[0].FileName, ignoreCase: true);
    }

    [Fact]
    public void Detect_SkipsReparsePointsOutsideTheInstallRoot()
    {
        var gameDir = Path.Combine(_root, "JunctionGame");
        var outside = Path.Combine(_root, "Outside");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(outside);
        WriteDummy(Path.Combine(outside, "nvngx_dlss.dll"), "escaped");
        var link = Path.Combine(gameDir, "Linked");
        CreateJunction(link, outside);

        var found = new DlssSwapService().Detect([Game("steam:junction", gameDir)]).ToList();

        Assert.Empty(found);
    }

    [Fact]
    public void ApplyPack_UpdatesOnlyThatGame_WritesExoBak_AndLeavesOtherGamesAlone()
    {
        var targetDir = Path.Combine(_root, "Target");
        var otherDir = Path.Combine(_root, "Other");
        Directory.CreateDirectory(Path.Combine(targetDir, "bin"));
        Directory.CreateDirectory(otherDir);
        var dest = Path.Combine(targetDir, "bin", "nvngx_dlss.dll");
        var other = Path.Combine(otherDir, "nvngx_dlss.dll");
        WriteDummy(dest, "original-target");
        WriteDummy(other, "original-other");
        var packDll = Path.Combine(_root, "pack", "nvngx_dlss.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(packDll)!);
        WriteDummy(packDll, "updated-pack");

        var svc = new DlssSwapService();
        var result = svc.ApplyPackToGame(
            Game("steam:target", targetDir),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["nvngx_dlss.dll"] = packDll,
            },
            "310.4.0.0");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, result.Updated);
        Assert.Equal("updated-pack", File.ReadAllText(dest));
        Assert.True(File.Exists(dest + DlssSwapService.BackupSuffix));
        Assert.Equal("original-target", File.ReadAllText(dest + DlssSwapService.BackupSuffix));
        Assert.Equal("original-other", File.ReadAllText(other));
        Assert.False(File.Exists(other + DlssSwapService.BackupSuffix));
    }

    [Fact]
    public void ApplyPack_DoesNotInventMissingUpscalers()
    {
        var gameDir = Path.Combine(_root, "NoDlss");
        Directory.CreateDirectory(gameDir);
        WriteDummy(Path.Combine(gameDir, "game.exe"), "exe");
        var packDll = Path.Combine(_root, "pack-missing", "nvngx_dlss.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(packDll)!);
        WriteDummy(packDll, "updated-pack");

        var result = new DlssSwapService().ApplyPackToGame(
            Game("steam:nodlss", gameDir),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["nvngx_dlss.dll"] = packDll,
            },
            "310.4.0.0");

        Assert.False(result.Ok);
        Assert.Equal(0, result.Updated);
        Assert.Contains("no swappable", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(gameDir, "nvngx_dlss.dll")));
    }

    [Fact]
    public void ApplyPack_DoesNotSkipVersionlessFilesOfTheSameLength()
    {
        var gameDir = Path.Combine(_root, "SameLen");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        var packDll = Path.Combine(_root, "pack-samelen", "nvngx_dlss.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(packDll)!);
        WriteDummy(dest, "AAAA");
        WriteDummy(packDll, "BBBB");

        var result = new DlssSwapService().ApplyPackToGame(
            Game("steam:samelen", gameDir),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["nvngx_dlss.dll"] = packDll,
            },
            "1.0");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, result.Updated);
        Assert.Equal("BBBB", File.ReadAllText(dest));
        Assert.Equal("AAAA", File.ReadAllText(dest + DlssSwapService.BackupSuffix));
    }

    [Fact]
    public void Restore_PutsBackExoBakAndSwapperFactoryFiles()
    {
        var gameDir = Path.Combine(_root, "Restore");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "swapped");
        WriteDummy(dest + DlssSwapService.BackupSuffix, "exo-original");
        WriteDummy(dest + DlssSwapService.SwapperBackupSuffix, "swapper-factory");

        var result = new DlssSwapService().RestoreGame(Game("steam:restore", gameDir));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("swapper-factory", File.ReadAllText(dest));
        Assert.False(File.Exists(dest + DlssSwapService.BackupSuffix));
        Assert.True(File.Exists(dest + DlssSwapService.SwapperBackupSuffix));
    }

    [Fact]
    public void Restore_RemovesInjectedFsr4CompanionsAndKeepsShippedFsr31()
    {
        var gameDir = Path.Combine(_root, "FsrRestore");
        Directory.CreateDirectory(gameDir);
        var dx12 = Path.Combine(gameDir, "amd_fidelityfx_dx12.dll");
        var upscaler = Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll");
        WriteDummy(dx12, new string('F', 600_000));
        WriteDummy(dx12 + DlssSwapService.BackupSuffix, new string('O', 600_000));
        WriteDummy(upscaler, "injected-fsr4");
        File.WriteAllText(Path.Combine(gameDir, DlssSwapService.SidecarName), JsonSerializer.Serialize(new
        {
            version = 1,
            injected = new[] { "amd_fidelityfx_upscaler_dx12.dll" },
        }));

        var result = new DlssSwapService().RestoreGame(Game("steam:fsr", gameDir));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(new string('O', 600_000), File.ReadAllText(dx12));
        Assert.False(File.Exists(upscaler));
        Assert.False(File.Exists(Path.Combine(gameDir, DlssSwapService.SidecarName)));
    }

    [Fact]
    public void Restore_ResolvesInstallRootWhenPathIsTheGameExe()
    {
        var gameDir = Path.Combine(_root, "RestoreExe");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(exe, "exe");
        WriteDummy(dest, "swapped");
        WriteDummy(dest + DlssSwapService.BackupSuffix, "original");

        var result = new DlssSwapService().RestoreGame(Game("local:restore-exe", exe));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("original", File.ReadAllText(dest));
    }

    [Fact]
    public void ManifestLatestVersions_ReadsOfficialKeys()
    {
        using var doc = JsonDocument.Parse("""
            {
              "dlss": [
                { "version": "310.4.0.0", "version_number": 2, "download_url": "https://dlss-swapper-downloads.beeradmoore.com/dlss/a.zip", "is_dev_file": false },
                { "version": "1.0.0.0", "version_number": 1, "download_url": "https://dlss-swapper-downloads.beeradmoore.com/dlss/b.zip", "is_dev_file": false }
              ],
              "xess": [
                { "version": "2.1.0", "version_number": 5, "download_url": "https://evil.example/xess.zip", "is_dev_file": false }
              ]
            }
            """);

        var latest = DlssSwapService.ManifestLatestVersions(doc.RootElement);

        Assert.Equal("310.4.0.0", latest["nvngx_dlss.dll"]);
        Assert.False(latest.ContainsKey("libxess.dll"));
    }

    [Fact]
    public void ManifestLatestDisplayVersions_UsesFsrSemanticReleaseInsteadOfFileResourceVersion()
    {
        using var doc = JsonDocument.Parse("""
            {
              "fsr_31_dx12": [
                { "version": "1.0.1.41314", "version_number": 281474976817506,
                  "internal_name": "3.1.4", "download_url": "https://dlss-swapper-downloads.beeradmoore.com/fsr_31_dx12/a.zip" },
                { "version": "1.0.2.38022", "version_number": 281474976879750,
                  "internal_name": "3.1.2", "download_url": "https://dlss-swapper-downloads.beeradmoore.com/fsr_31_dx12/b.zip" }
              ]
            }
            """);

        var display = DlssSwapService.ManifestLatestDisplayVersions(doc.RootElement);

        Assert.Equal("3.1.4", display["amd_fidelityfx_dx12.dll"]);
    }

    [Fact]
    public async Task CatalogCache_FreshDiskStartsOfflineWithoutFetching()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(_root, "catalog-fresh", "catalog-v1.json");
        WriteCatalogEnvelope(path, CatalogManifest("310.7.0.0"), now - TimeSpan.FromMinutes(5));
        var fetches = 0;
        var cache = new DlssSwapService.ManifestCatalogCache(
            path,
            _ =>
            {
                Interlocked.Increment(ref fetches);
                throw new HttpRequestException("offline");
            },
            () => now,
            freshness: TimeSpan.FromHours(6));

        var result = await cache.GetAsync(forceRefresh: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("310.7.0.0", DlssSwapService.ManifestLatestVersions(result)["nvngx_dlss.dll"]);
        Assert.Equal(0, fetches);
    }

    [Fact]
    public async Task CatalogCache_StaleDiskReturnsImmediatelyAndRefreshesInBackground()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(_root, "catalog-stale", "catalog-v1.json");
        WriteCatalogEnvelope(path, CatalogManifest("310.6.0.0"), now - TimeSpan.FromDays(1));
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        var cache = new DlssSwapService.ManifestCatalogCache(
            path,
            _ =>
            {
                Interlocked.Increment(ref fetches);
                return response.Task;
            },
            () => now,
            freshness: TimeSpan.FromHours(6));

        var staleTask = cache.GetAsync(forceRefresh: false, CancellationToken.None);
        Assert.True(staleTask.IsCompleted);
        var stale = await staleTask;
        Assert.Equal("310.6.0.0", DlssSwapService.ManifestLatestVersions(stale)["nvngx_dlss.dll"]);
        Assert.Equal(1, fetches);

        response.SetResult(CatalogManifest("310.7.0.0"));
        await cache.WaitForBackgroundRefreshAsync();
        var refreshed = await cache.GetAsync(forceRefresh: false, CancellationToken.None);
        Assert.Equal("310.7.0.0", DlssSwapService.ManifestLatestVersions(refreshed)["nvngx_dlss.dll"]);
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task CatalogCache_CorruptDiskUsesNetworkAndAtomicallyReplacesIt()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var folder = Path.Combine(_root, "catalog-corrupt");
        var path = Path.Combine(folder, "catalog-v1.json");
        Directory.CreateDirectory(folder);
        File.WriteAllText(path, "{not-json");
        var cache = new DlssSwapService.ManifestCatalogCache(
            path,
            _ => Task.FromResult(CatalogManifest("310.7.0.0")),
            () => now);

        var result = await cache.GetAsync(forceRefresh: false, CancellationToken.None);

        Assert.Equal("310.7.0.0", DlssSwapService.ManifestLatestVersions(result)["nvngx_dlss.dll"]);
        using var persisted = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, persisted.RootElement.GetProperty("schema").GetInt32());
        Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp-*"));
    }

    [Fact]
    public async Task CatalogCache_OfflineFirstStartIsUnavailableButStaleLastGoodSurvives()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var emptyPath = Path.Combine(_root, "catalog-offline-empty", "catalog-v1.json");
        var empty = new DlssSwapService.ManifestCatalogCache(
            emptyPath,
            _ => throw new HttpRequestException("offline"),
            () => now);
        Assert.Null(await empty.GetAsync(forceRefresh: false, CancellationToken.None));

        var stalePath = Path.Combine(_root, "catalog-offline-stale", "catalog-v1.json");
        WriteCatalogEnvelope(stalePath, CatalogManifest("310.6.0.0"), now - TimeSpan.FromDays(3));
        var stale = new DlssSwapService.ManifestCatalogCache(
            stalePath,
            _ => throw new HttpRequestException("offline"),
            () => now,
            freshness: TimeSpan.FromHours(6));
        var result = await stale.GetAsync(forceRefresh: false, CancellationToken.None);
        await stale.WaitForBackgroundRefreshAsync();

        Assert.Equal("310.6.0.0", DlssSwapService.ManifestLatestVersions(result)["nvngx_dlss.dll"]);
        using var persisted = JsonDocument.Parse(File.ReadAllText(stalePath));
        Assert.Equal("310.6.0.0", persisted.RootElement.GetProperty("manifest")
            .GetProperty("dlss")[0].GetProperty("version").GetString());
    }

    [Fact]
    public async Task CatalogCache_ExplicitRefreshBypassesFreshDiskAndPersistsNewLastGood()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(_root, "catalog-force", "catalog-v1.json");
        WriteCatalogEnvelope(path, CatalogManifest("310.6.0.0"), now - TimeSpan.FromMinutes(1));
        var fetches = 0;
        var cache = new DlssSwapService.ManifestCatalogCache(
            path,
            _ =>
            {
                Interlocked.Increment(ref fetches);
                return Task.FromResult(CatalogManifest("310.7.0.0"));
            },
            () => now);

        var result = await cache.GetAsync(forceRefresh: true, CancellationToken.None);

        Assert.Equal(1, fetches);
        Assert.Equal("310.7.0.0", DlssSwapService.ManifestLatestVersions(result)["nvngx_dlss.dll"]);
        using var persisted = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("310.7.0.0", persisted.RootElement.GetProperty("manifest")
            .GetProperty("dlss")[0].GetProperty("version").GetString());
    }

    [Fact]
    public void DetectionCache_ColdMissThenFreshDiskHitUsesExactFingerprint()
    {
        var cachePath = Path.Combine(_root, "status-fresh", "status-v1.json");
        var gameDir = Path.Combine(_root, "status-fresh-game");
        Directory.CreateDirectory(gameDir);
        var dll = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dll, "signed-shape-placeholder");
        var game = Game("steam:status-fresh", gameDir);
        var cache = new DlssSwapService.DetectionStatusCache(cachePath, "1.0.97");

        Assert.Null(cache.TryGet(game));
        cache.Store(game, [Detected(game, dll, "310.7.0.0")]);

        var restarted = new DlssSwapService.DetectionStatusCache(cachePath, "1.0.97");
        var hit = Assert.Single(restarted.TryGet(game)!);
        Assert.Equal(Path.GetFullPath(dll), hit.Path);
        Assert.Equal("310.7.0.0", hit.CurrentVersion);
        Assert.Equal(1, restarted.Count);
    }

    [Fact]
    public void DetectionCache_ChangedOrMissingDllInvalidatesOnlyThatGame()
    {
        var cachePath = Path.Combine(_root, "status-stale", "status-v1.json");
        var firstDir = Path.Combine(_root, "status-first");
        var secondDir = Path.Combine(_root, "status-second");
        Directory.CreateDirectory(firstDir);
        Directory.CreateDirectory(secondDir);
        var firstDll = Path.Combine(firstDir, "nvngx_dlss.dll");
        var secondDll = Path.Combine(secondDir, "libxess.dll");
        WriteDummy(firstDll, "first");
        WriteDummy(secondDll, "second");
        var first = Game("steam:status-first", firstDir);
        var second = Game("epic:status-second", secondDir);
        var cache = new DlssSwapService.DetectionStatusCache(cachePath, "1.0.97");
        cache.Store(first, [Detected(first, firstDll, "310.7.0.0")]);
        cache.Store(second, [Detected(second, secondDll, "2.0.2.68")]);

        File.WriteAllText(firstDll, "changed-size-and-bytes");
        File.SetLastWriteTimeUtc(firstDll, DateTime.UtcNow + TimeSpan.FromMinutes(1));

        Assert.Null(cache.TryGet(first));
        Assert.Single(cache.TryGet(second)!);
        Assert.Equal(1, cache.Count);

        File.Delete(secondDll);
        Assert.Null(cache.TryGet(second));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void DetectionCache_CorruptOrWrongAppVersionStartsColdAndRewritesAtomically()
    {
        var folder = Path.Combine(_root, "status-corrupt");
        var cachePath = Path.Combine(folder, "status-v1.json");
        Directory.CreateDirectory(folder);
        File.WriteAllText(cachePath, "{broken");
        var gameDir = Path.Combine(_root, "status-corrupt-game");
        Directory.CreateDirectory(gameDir);
        var dll = Path.Combine(gameDir, "nvngx_dlssd.dll");
        WriteDummy(dll, "rr");
        var game = Game("steam:status-corrupt", gameDir);
        var cache = new DlssSwapService.DetectionStatusCache(cachePath, "1.0.97");

        Assert.Null(cache.TryGet(game));
        cache.Store(game, [Detected(game, dll, "310.7.0.0")]);
        Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp-*"));
        using (var valid = JsonDocument.Parse(File.ReadAllText(cachePath)))
            Assert.Equal("1.0.97", valid.RootElement.GetProperty("AppVersion").GetString());

        var nextApp = new DlssSwapService.DetectionStatusCache(cachePath, "1.0.98");
        Assert.Null(nextApp.TryGet(game));
        Assert.Equal(0, nextApp.Count);
    }

    [Fact]
    public void DetectionCache_IsBoundedAndKeysByExactStoreAndSourceId()
    {
        var cachePath = Path.Combine(_root, "status-bounded", "status-v1.json");
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var cache = new DlssSwapService.DetectionStatusCache(
            cachePath,
            "1.0.97",
            maximumEntries: 2,
            utcNow: () => now);
        GameEntry? last = null;
        for (var i = 0; i < 3; i++)
        {
            var root = Path.Combine(_root, "bounded-" + i);
            Directory.CreateDirectory(root);
            var dll = Path.Combine(root, "nvngx_dlss.dll");
            WriteDummy(dll, "dll-" + i);
            var game = Game("steam:bounded-" + i, root);
            now += TimeSpan.FromMinutes(1);
            cache.Store(game, [Detected(game, dll, "310.7.0." + i)]);
            last = game;
        }

        Assert.Equal(2, cache.Count);
        Assert.NotNull(cache.TryGet(last!));
        var wrongStore = new GameEntry
        {
            Id = last!.Id,
            Title = last.Title,
            Store = StoreKind.Epic,
            Installed = true,
            Path = last.Path,
        };
        Assert.Null(cache.TryGet(wrongStore));
    }

    [Fact]
    public void AttachLatestVersions_DoesNotInventFsr4WhenThePackIsMissing()
    {
        var item = new DlssSwapService.DetectedDll(
            Path.Combine(_root, "amd_fidelityfx_dx12.dll"),
            "amd_fidelityfx_dx12.dll",
            "FSR",
            "steam:fsr",
            "FSR Game",
            "3.1.5",
            Eligible: true,
            SkipReason: null);

        var attached = DlssSwapService.AttachLatestVersions(
            [item],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["amd_fidelityfx_dx12.dll"] = "3.1.5",
            });

        Assert.Equal("3.1.5", attached[0].LatestVersion);
    }

    [Fact]
    public void EvaluateSdkStatus_DlssOnlyLatest_IsAlreadyBest_WithoutMissingVendors()
    {
        var status = DlssSwapService.EvaluateSdkStatus(
            haveDlss: true,
            haveFsr: false,
            haveXess: false,
            cachedDlss: "310.4.0.0",
            cachedFsr: null,
            cachedXess: null,
            remoteDlss: "310.4.0.0",
            remoteFsr: "3.1.5",
            remoteXess: "2.1.0",
            cachedLabel: "DLSS 310.4.0.0");

        Assert.True(status.AlreadyBest);
        Assert.Equal("Ready.", status.Message);
        Assert.DoesNotContain("Download the rest", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XeSS", status.LatestVersion ?? "", StringComparison.Ordinal);
        Assert.Contains("DLSS 310.4.0.0", status.LatestVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateSdkStatus_EmptyCache_IsNotAlreadyBest()
    {
        var status = DlssSwapService.EvaluateSdkStatus(
            haveDlss: false,
            haveFsr: false,
            haveXess: false,
            cachedDlss: null,
            cachedFsr: null,
            cachedXess: null,
            remoteDlss: "310.4.0.0",
            remoteFsr: "3.1.5",
            remoteXess: "2.1.0",
            cachedLabel: "");

        Assert.False(status.AlreadyBest);
        Assert.Equal("Download latest.", status.Message);
        Assert.DoesNotContain("Download the rest", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatLatestLabel_OmitsVersionlessVendorNames()
    {
        Assert.Equal(
            "DLSS 310.4.0.0 · FSR 3.1.5 · XeSS 2.1.0",
            DlssSwapService.FormatLatestLabel("310.4.0.0", "3.1.5", "2.1.0", "cached"));
        Assert.Equal(
            "DLSS 310.4.0.0",
            DlssSwapService.FormatLatestLabel("310.4.0.0", null, null, "cached"));
        Assert.Equal(
            "cached",
            DlssSwapService.FormatLatestLabel(null, null, null, "cached"));
        Assert.Equal(
            "DLSS 310.4.0.0",
            DlssSwapService.FormatLatestLabel("310.4.0.0", "FSR", "XeSS", "cached"));
        var label = DlssSwapService.FormatLatestLabel("310.4.0.0", null, null, "DLSS 310.4.0.0 · XeSS");
        Assert.DoesNotContain("XeSS", label, StringComparison.Ordinal);
        Assert.DoesNotContain("FSR 3.1", label, StringComparison.Ordinal);
    }

    [Fact]
    public void NeededPackFiles_DlssOnlyDoesNotFetchFsrOrXess()
    {
        var needed = DlssSwapService.NeededPackFiles(
        [
            new DlssSwapService.DetectedDll(
                Path.Combine(_root, "nvngx_dlss.dll"),
                "nvngx_dlss.dll",
                "DLSS",
                "steam:dlss-only",
                "DLSS Only",
                "2.5.1",
                Eligible: true,
                SkipReason: null),
        ]);

        Assert.Contains("nvngx_dlss.dll", needed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("amd_fidelityfx_dx12.dll", needed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("amd_fidelityfx_loader_dx12.dll", needed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("amd_fidelityfx_upscaler_dx12.dll", needed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("libxess.dll", needed, StringComparer.OrdinalIgnoreCase);
        Assert.False(DlssSwapService.NeedsFsr4PackFiles(needed));
    }

    [Fact]
    public void NeededPackFiles_Fsr31DoesNotAddCompanionsOrOtherVendors()
    {
        var needed = DlssSwapService.NeededPackFiles(
        [
            new DlssSwapService.DetectedDll(
                Path.Combine(_root, "amd_fidelityfx_dx12.dll"),
                "amd_fidelityfx_dx12.dll",
                "FSR",
                "steam:fsr",
                "FSR Game",
                "3.1.5",
                Eligible: true,
                SkipReason: null),
        ]);

        Assert.Contains("amd_fidelityfx_dx12.dll", needed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("amd_fidelityfx_loader_dx12.dll", needed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("amd_fidelityfx_upscaler_dx12.dll", needed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("nvngx_dlss.dll", needed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("libxess.dll", needed, StringComparer.OrdinalIgnoreCase);
        Assert.False(DlssSwapService.NeedsFsr4PackFiles(needed));
    }

    [Fact]
    public void FillMissingFromCache_RejectsUnsignedPeShapedFiles()
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _root);
        try
        {
            WriteDummyPe(Path.Combine(_root, "dlss", "dlss", "310.4.0", "nvngx_dlss.dll"));
            WriteDummyPe(Path.Combine(_root, "dlss", "xess", "2.1.0", "libxess.dll"));
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            DlssSwapService.FillMissingFromCache(files, ["nvngx_dlss.dll"]);
            Assert.False(files.ContainsKey("nvngx_dlss.dll"));
            Assert.False(files.ContainsKey("libxess.dll"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
        }
    }

    private static JsonElement CatalogManifest(string version)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "dlss": [
                {
                  "version": "{{version}}",
                  "version_number": 1,
                  "internal_name": "CL 37997616",
                  "download_url": "https://dlss-swapper-downloads.beeradmoore.com/dlss/test.zip",
                  "is_dev_file": false
                }
              ]
            }
            """);
        return document.RootElement.Clone();
    }

    private static void WriteCatalogEnvelope(string path, JsonElement manifest, DateTime fetchedUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {"schema":1,"fetchedUtc":"{{fetchedUtc:O}}","manifest":{{manifest.GetRawText()}}}
            """);
    }

    private static string WriteDummyPe(string path)
    {
        var bytes = new byte[40_000];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string WriteMalformedExportPe(string path)
    {
        WriteDummyPe(path);
        var bytes = File.ReadAllBytes(path);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 0x86); // one section
        BitConverter.GetBytes((ushort)0xf0).CopyTo(bytes, 0x94); // PE32+ optional header
        BitConverter.GetBytes((ushort)0x20b).CopyTo(bytes, 0x98);
        BitConverter.GetBytes(uint.MaxValue - 255).CopyTo(bytes, 0x108); // unmappable export RVA
        BitConverter.GetBytes((uint)0x1000).CopyTo(bytes, 0x190); // virtual size
        BitConverter.GetBytes((uint)0x1000).CopyTo(bytes, 0x194); // virtual address
        BitConverter.GetBytes((uint)0x1000).CopyTo(bytes, 0x198); // raw size
        BitConverter.GetBytes((uint)0x200).CopyTo(bytes, 0x19c); // raw pointer
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string WritePeWithExports(string path, params string[] exports)
    {
        var bytes = new byte[40_000];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        BitConverter.GetBytes(0x00004550u).CopyTo(bytes, 0x80);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 0x86);
        BitConverter.GetBytes((ushort)0xf0).CopyTo(bytes, 0x94);
        BitConverter.GetBytes((ushort)0x20b).CopyTo(bytes, 0x98);
        BitConverter.GetBytes(0x1000u).CopyTo(bytes, 0x108);
        BitConverter.GetBytes(0x800u).CopyTo(bytes, 0x10c);

        // One section maps RVA 0x1000 to raw offset 0x200.
        BitConverter.GetBytes(0x2000u).CopyTo(bytes, 0x190);
        BitConverter.GetBytes(0x1000u).CopyTo(bytes, 0x194);
        BitConverter.GetBytes(0x2000u).CopyTo(bytes, 0x198);
        BitConverter.GetBytes(0x200u).CopyTo(bytes, 0x19c);

        // IMAGE_EXPORT_DIRECTORY at RVA 0x1000 / raw 0x200.
        BitConverter.GetBytes((uint)exports.Length).CopyTo(bytes, 0x214);
        BitConverter.GetBytes((uint)exports.Length).CopyTo(bytes, 0x218);
        BitConverter.GetBytes(0x1100u).CopyTo(bytes, 0x220);
        var stringOffset = 0x400;
        for (var i = 0; i < exports.Length; i++)
        {
            var encoded = Encoding.ASCII.GetBytes(exports[i]);
            var stringRva = 0x1000u + (uint)(stringOffset - 0x200);
            BitConverter.GetBytes(stringRva).CopyTo(bytes, 0x300 + i * 4);
            encoded.CopyTo(bytes, stringOffset);
            stringOffset += encoded.Length + 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static GameEntry Game(string id, string path) => new()
    {
        Id = id,
        Title = Path.GetFileName(path),
        Store = StoreKind.Steam,
        Installed = true,
        Path = path,
    };

    private static DlssSwapService.DetectedDll Detected(
        GameEntry game,
        string path,
        string version) => new(
            Path.GetFullPath(path),
            Path.GetFileName(path),
            DlssSwapService.ClassifyKind(Path.GetFileName(path)),
            game.Id,
            game.Title,
            version,
            Eligible: true,
            SkipReason: null);

    private static void WriteDummy(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, Encoding.ASCII);
    }

    /// <summary>
    /// A version resource cannot be faked in a temp file, so these tests carry
    /// the version in the file's bytes: the map is keyed by contents, which is
    /// how a real version travels when the file is copied over a destination.
    /// </summary>
    private static DlssSwapService SvcWithVersions(Dictionary<string, string> versionsByContent) =>
        new()
        {
            FileVersion = path =>
            {
                try
                {
                    return File.Exists(path) &&
                           versionsByContent.TryGetValue(File.ReadAllText(path), out var hit)
                        ? hit
                        : null;
                }
                catch
                {
                    return null;
                }
            },
        };

    private static Dictionary<string, string> Pack(string fileName, string source) =>
        new(StringComparer.OrdinalIgnoreCase) { [fileName] = source };

    private static string Sha256Hex(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void DestNames_AreFourteenPresentOnly()
    {
        Assert.True(DlssSwapService.IsDestName("nvngx_dlss.dll"));
        Assert.True(DlssSwapService.IsDestName("nvngx_dlssg.dll"));
        Assert.True(DlssSwapService.IsDestName("nvngx_dlssd.dll"));
        Assert.True(DlssSwapService.IsDestName("amd_fidelityfx_dx12.dll"));
        Assert.True(DlssSwapService.IsDestName("amd_fidelityfx_vk.dll"));
        Assert.True(DlssSwapService.IsDestName("amd_fidelityfx_loader_dx12.dll"));
        Assert.True(DlssSwapService.IsDestName("amd_fidelityfx_upscaler_dx12.dll"));
        Assert.True(DlssSwapService.IsDestName("amd_fidelityfx_framegeneration_dx12.dll"));
        Assert.True(DlssSwapService.IsDestName("amd_fidelityfx_denoiser_dx12.dll"));
        Assert.True(DlssSwapService.IsDestName("amd_fidelityfx_radiancecache_dx12.dll"));
        Assert.True(DlssSwapService.IsDestName("libxess.dll"));
        Assert.True(DlssSwapService.IsDestName("libxess_dx11.dll"));
        Assert.True(DlssSwapService.IsDestName("libxess_fg.dll"));
        Assert.True(DlssSwapService.IsDestName("libxell.dll"));
        Assert.False(DlssSwapService.IsDestName("optiscaler.dll"));
        Assert.False(DlssSwapService.IsDestName("sl.dlss.dll"));
        Assert.False(DlssSwapService.IsDestName("nvngx.dll"));
    }

    [Fact]
    public void FullDestCatalog_ListsEveryDest_MissingStayAbsent()
    {
        var found = new DlssSwapService.DetectedDll[]
        {
            new(
                Path.Combine(_root, "nvngx_dlss.dll"),
                "nvngx_dlss.dll",
                "DLSS",
                "steam:one",
                "One",
                "310.4.0.0",
                Eligible: true,
                SkipReason: null,
                Present: true),
        };

        var rows = DlssSwapService.WithFullDestCatalog(found, "steam:one", "One");
        Assert.Equal(DlssSwapService.DestFileNames.Count, rows.Count);
        Assert.Equal(DlssSwapService.DestFileNames, rows.Select(row => row.FileName));
        var dlss = Assert.Single(rows, row => row.FileName.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
        Assert.True(dlss.Present);
        Assert.Equal("310.4.0.0", dlss.CurrentVersion);
        Assert.Contains(rows, row =>
            row.FileName.Equals("libxess.dll", StringComparison.OrdinalIgnoreCase) &&
            !row.Present &&
            !row.Eligible);
    }

    [Fact]
    public void Detect_SkipsStarCitizen()
    {
        var gameDir = Path.Combine(_root, "starcitizen", "LIVE");
        Directory.CreateDirectory(gameDir);
        WriteDummy(Path.Combine(gameDir, "nvngx_dlss.dll"), "dlss");
        var found = new DlssSwapService().Detect([Game("rsi:sc", gameDir)]).ToList();
        Assert.Empty(found);
    }

    [Fact]
    public void ApplyPack_SkipsWindowsApps()
    {
        var gameDir = Path.Combine(_root, "WindowsApps", "SomeGame");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "original");
        var packDll = Path.Combine(_root, "pack-store", "nvngx_dlss.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(packDll)!);
        WriteDummy(packDll, "updated-pack");

        var result = new DlssSwapService().ApplyPackToGame(
            Game("xbox:locked", gameDir),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["nvngx_dlss.dll"] = packDll,
            },
            "310.4.0.0");

        Assert.False(result.Ok);
        Assert.Equal(0, result.Updated);
        Assert.Contains("locked by the store", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(dest));
        Assert.False(File.Exists(dest + DlssSwapService.BackupSuffix));
    }

    [Fact]
    public void ApplyPack_WritesExoWritten_AndInvalidatesForeignChange()
    {
        var gameDir = Path.Combine(_root, "Written");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "original-target");
        var packDll = Path.Combine(_root, "pack-written", "nvngx_dlss.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(packDll)!);
        WriteDummy(packDll, "updated-pack");

        var svc = new DlssSwapService();
        var result = svc.ApplyPackToGame(
            Game("steam:written", gameDir),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["nvngx_dlss.dll"] = packDll,
            },
            "310.4.0.0");

        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(dest + DlssSwapService.WrittenSuffix));
        Assert.True(File.Exists(dest + DlssSwapService.SwapperBackupSuffix));
        WriteDummy(dest, "store-verify");
        DlssSwapService.InvalidateForeignWrite(dest);
        // Exo drops its claim on the live file. The captured shipped copy stays:
        // it is the only thing Restore can honestly put back.
        Assert.False(File.Exists(dest + DlssSwapService.WrittenSuffix));
        Assert.Equal("original-target", File.ReadAllText(dest + DlssSwapService.SwapperBackupSuffix));
        Assert.Equal("original-target", File.ReadAllText(dest + DlssSwapService.BackupSuffix));
    }

    [Fact]
    public void Restore_PrefersDlsssOverExoBak()
    {
        var gameDir = Path.Combine(_root, "FactoryPref");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "libxess_dx11.dll");
        WriteDummy(dest, "swapped");
        WriteDummy(dest + DlssSwapService.BackupSuffix, "exo-original");
        WriteDummy(dest + DlssSwapService.SwapperBackupSuffix, "swapper-factory");

        var result = new DlssSwapService().RestoreGame(Game("steam:xess-dx11", gameDir));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("swapper-factory", File.ReadAllText(dest));
        Assert.False(File.Exists(dest + DlssSwapService.BackupSuffix));
        Assert.True(File.Exists(dest + DlssSwapService.SwapperBackupSuffix));
    }

    [Fact]
    public void CompareVersions_PadsThreePartAgainstFourPart()
    {
        Assert.Equal(0, DlssSwapService.CompareVersions("310.4.0", "310.4.0.0"));
        Assert.True(DlssSwapService.CompareVersions("310.4.0.0", "310.2.1.0") > 0);
        Assert.True(DlssSwapService.CompareVersions("310.2.1.0", "310.4.0") < 0);
    }

    [Theory]
    // The three real shapes on disk: NVIDIA, AMD FidelityFX, Intel XeSS.
    [InlineData("310.4.0.0", "9.3.0.0")]
    [InlineData("310.4.0.0", "310.2.1.0")]
    [InlineData("310.40.0.0", "310.4.0.0")]
    [InlineData("1.0.2.0", "1.0.1.41314")]
    [InlineData("2.3.0.2740", "2.3.0.999")]
    [InlineData("1.0.1.41314", "1.0.1.9999")]
    public void CompareVersions_IsNumericPerPart_NotText(string newer, string older)
    {
        Assert.True(DlssSwapService.CompareVersions(newer, older) > 0, newer + " vs " + older);
        Assert.True(DlssSwapService.CompareVersions(older, newer) < 0, older + " vs " + newer);
    }

    [Fact]
    public void TryParseVersion_ReadsRealShapes_AndRefusesJunk()
    {
        Assert.Equal(new Version(310, 4, 0, 0), DlssSwapService.TryParseVersion("310.4.0.0"));
        Assert.Equal(new Version(1, 0, 1, 41314), DlssSwapService.TryParseVersion("1.0.1.41314"));
        Assert.Equal(new Version(2, 3, 0, 2740), DlssSwapService.TryParseVersion("2.3.0.2740"));
        Assert.Equal(new Version(3, 1, 5, 0), DlssSwapService.TryParseVersion("3.1.5"));
        Assert.Null(DlssSwapService.TryParseVersion(null));
        Assert.Null(DlssSwapService.TryParseVersion("—"));
        Assert.Null(DlssSwapService.TryParseVersion("310.4.0-dev"));
        Assert.Null(DlssSwapService.TryParseVersion("v310.4.0.0"));
        Assert.Null(DlssSwapService.TryParseVersion("1.2.3.4.5"));
        Assert.Null(DlssSwapService.TryParseVersion("1..3"));
        Assert.Null(DlssSwapService.TryParseVersion("2147483648.0.0.0"));
        Assert.True(DlssSwapService.CatalogVersionMatchesBinary("310.4.0.0", "310.4.0"));
        Assert.False(DlssSwapService.CatalogVersionMatchesBinary("310.2.0.0", "310.4.0"));
        Assert.False(DlssSwapService.CatalogVersionMatchesBinary("310.4.0.0", "310.4.0.0.1"));
    }

    [Fact]
    public void ShouldSkipAsNewerOrEqual_DoesNotDowngrade()
    {
        Assert.True(DlssSwapService.ShouldSkipAsNewerOrEqual("310.4.0.0", "310.2.1.0"));
        Assert.True(DlssSwapService.ShouldSkipAsNewerOrEqual("310.4.0", "310.4.0.0"));
        Assert.False(DlssSwapService.ShouldSkipAsNewerOrEqual("310.2.1.0", "310.4.0.0"));
        Assert.False(DlssSwapService.ShouldSkipAsNewerOrEqual(null, "310.4.0.0"));
        Assert.False(DlssSwapService.ShouldSkipAsNewerOrEqual("310.4.0.0", null));
    }

    [Fact]
    public void VersionsCompatible_ComparesNumbers_NotTextPrefixes()
    {
        Assert.True(DlssSwapService.VersionsCompatible("310.4.0.0", "310.4"));
        Assert.True(DlssSwapService.VersionsCompatible("310.40.0.0", "310.4"));
        Assert.False(DlssSwapService.VersionsCompatible("310.4", "310.40"));
        Assert.False(DlssSwapService.VersionsCompatible("1.0.1.41314", "1.0.2.0"));
    }

    [Fact]
    public void AttachLatestVersions_ShowsTheCatalogNumber_AndDoesNotAskToDowngrade()
    {
        var item = new DlssSwapService.DetectedDll(
            Path.Combine(_root, "amd_fidelityfx_dx12.dll"),
            "amd_fidelityfx_dx12.dll",
            "FSR",
            "steam:cyberpunk",
            "Cyberpunk",
            "2.3.0.2740",
            Eligible: true,
            SkipReason: null,
            LatestVersion: null,
            Present: true);

        var attached = DlssSwapService.AttachLatestVersions(
            [item],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["amd_fidelityfx_dx12.dll"] = "1.0.1.41314",
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["amd_fidelityfx_dx12.dll"] = "3.1.2",
            });

        // The raw file resource is 2.3, but this destination is the FSR 3.1
        // slot; the catalog's semantic display version must win.
        Assert.Equal("1.0.1.41314", attached[0].LatestVersion);
        Assert.False(DlssSwapService.IsPackCurrent(attached));
    }

    [Fact]
    public void PackCurrent_RequiresMatchingBytesWhenVersionsAreEqual()
    {
        var dest = Path.Combine(_root, "current-bytes", "nvngx_dlss.dll");
        var source = Path.Combine(_root, "catalog-bytes", "nvngx_dlss.dll");
        WriteDummy(dest, "same-version-store-build");
        WriteDummy(source, "same-version-catalog-build");
        var item = new DlssSwapService.DetectedDll(
            dest,
            "nvngx_dlss.dll",
            "DLSS",
            "steam:bytes",
            "Bytes",
            "310.4.0.0",
            Eligible: true,
            SkipReason: null,
            LatestVersion: "310.4.0",
            Present: true);

        Assert.False(DlssSwapService.IsPackCurrentWithSources([item], _ => source));
        File.Copy(source, dest, overwrite: true);
        Assert.True(DlssSwapService.IsPackCurrentWithSources([item], _ => source));
    }

    [Fact]
    public void SkipApplyReason_NeverDowngrades_AndOnlyCallsIdenticalBytesCurrent()
    {
        Assert.Equal(
            DlssSwapService.KeptNewerMessage,
            DlssSwapService.SkipApplyReason("2.3.0.2740", "1.0.1.41314", alreadyCurrent: false));
        Assert.Equal(
            DlssSwapService.KeptNewerMessage,
            DlssSwapService.SkipApplyReason("310.4.0.0", "310.2.1.0", alreadyCurrent: false));
        Assert.Null(DlssSwapService.SkipApplyReason("310.4.0.0", "310.4.0", alreadyCurrent: false));
        Assert.Equal(
            DlssSwapService.AlreadyNewestMessage,
            DlssSwapService.SkipApplyReason(null, null, alreadyCurrent: true));
        Assert.Null(DlssSwapService.SkipApplyReason("310.2.1.0", "310.4.0.0", alreadyCurrent: false));
        // Unreadable on either side: no downgrade can be proven, so the write runs.
        Assert.Null(DlssSwapService.SkipApplyReason(null, "310.4.0.0", alreadyCurrent: false));
        Assert.Null(DlssSwapService.SkipApplyReason("310.4.0.0", null, alreadyCurrent: false));
        Assert.Null(DlssSwapService.SkipApplyReason(
            "amd_fidelityfx_dx12.dll", "2.3.0.2740", "1.0.1.41314", alreadyCurrent: false));
    }

    [Fact]
    public void ApplyPack_KeepsTheNewerShippedFile_EvenWhenItMatchesTheCapturedOriginal()
    {
        var gameDir = Path.Combine(_root, "FactoryNewer");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "shipped-310.4");
        WriteDummy(dest + DlssSwapService.SwapperBackupSuffix, "shipped-310.4");
        var packDll = Path.Combine(_root, "pack-older", "nvngx_dlss.dll");
        WriteDummy(packDll, "catalog-310.2");

        var result = SvcWithVersions(new()
        {
            ["shipped-310.4"] = "310.4.0.0",
            ["catalog-310.2"] = "310.2.1.0",
        }).ApplyPackToGame(Game("steam:factory-newer", gameDir), Pack("nvngx_dlss.dll", packDll), "310.2.1.0");

        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("shipped-310.4", File.ReadAllText(dest));
        var outcome = Assert.Single(result.Files!);
        Assert.Equal("skipped", outcome.State);
        Assert.Equal(DlssSwapService.KeptNewerMessage, outcome.Message);
    }

    [Fact]
    public void ApplyPack_UpdatesFsr2WhenTheCatalogCarriesFsr31()
    {
        var gameDir = Path.Combine(_root, "FsrShapes");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "amd_fidelityfx_dx12.dll");
        WriteDummy(dest, "shipped-fsr");
        var packDll = Path.Combine(_root, "pack-fsr-older", "amd_fidelityfx_dx12.dll");
        WriteDummy(packDll, "catalog-fsr");

        var result = SvcWithVersions(new()
        {
            ["shipped-fsr"] = "2.3.0.2740",
            ["catalog-fsr"] = "1.0.1.41314",
        }).ApplyPackToGame(Game("steam:fsr-shapes", gameDir), Pack("amd_fidelityfx_dx12.dll", packDll), "1.0.1.41314");

        Assert.Equal(1, result.Updated);
        Assert.Equal("catalog-fsr", File.ReadAllText(dest));
        Assert.Equal("updated", Assert.Single(result.Files!).State);
    }

    [Fact]
    public void ApplyPack_ReplacesSameVersionWhenTheAuthenticatedBytesDiffer()
    {
        var gameDir = Path.Combine(_root, "AlreadyNewest");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "shipped-310.4");
        var packDll = Path.Combine(_root, "pack-same", "nvngx_dlss.dll");
        // Same resource version can represent a vendor rebuild. Only the
        // authenticated bytes prove that the destination is already current.
        WriteDummy(packDll, "catalog-310.4-rebuild");
        var result = SvcWithVersions(new()
        {
            ["shipped-310.4"] = "310.4.0.0",
            ["catalog-310.4-rebuild"] = "310.4.0",
        }).ApplyPackToGame(Game("steam:already-newest", gameDir), Pack("nvngx_dlss.dll", packDll), "310.4.0.0");

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Skipped);
        Assert.Equal("catalog-310.4-rebuild", File.ReadAllText(dest));
        Assert.True(File.Exists(dest + DlssSwapService.WrittenSuffix));
        Assert.True(File.Exists(dest + DlssSwapService.SwapperBackupSuffix));
        Assert.Equal("updated", Assert.Single(result.Files!).State);
    }

    [Fact]
    public void ApplyPack_TakesTheNewerCatalogFile()
    {
        var gameDir = Path.Combine(_root, "NewerCatalog");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "shipped-9.3");
        var packDll = Path.Combine(_root, "pack-newer", "nvngx_dlss.dll");
        WriteDummy(packDll, "catalog-310.4");

        var result = SvcWithVersions(new()
        {
            ["shipped-9.3"] = "9.3.0.0",
            ["catalog-310.4"] = "310.4.0.0",
        }).ApplyPackToGame(Game("steam:newer-catalog", gameDir), Pack("nvngx_dlss.dll", packDll), "310.4.0.0");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, result.Updated);
        Assert.Equal("catalog-310.4", File.ReadAllText(dest));
        var outcome = Assert.Single(result.Files!);
        Assert.Equal("updated", outcome.State);
        Assert.Equal("310.4.0.0", outcome.Version);
    }

    [Fact]
    public void ApplyPack_CapturesTheShippedFileOnce_AndKeepsItThroughLaterWrites()
    {
        var gameDir = Path.Combine(_root, "OriginalHeld");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "shipped-310.1");
        var first = Path.Combine(_root, "pack-first", "nvngx_dlss.dll");
        var second = Path.Combine(_root, "pack-second", "nvngx_dlss.dll");
        WriteDummy(first, "catalog-310.2");
        WriteDummy(second, "catalog-310.4");
        var game = Game("steam:original-held", gameDir);
        var svc = SvcWithVersions(new()
        {
            ["shipped-310.1"] = "310.1.0.0",
            ["catalog-310.2"] = "310.2.1.0",
            ["catalog-310.4"] = "310.4.0.0",
        });

        Assert.Equal(1, svc.ApplyPackToGame(game, Pack("nvngx_dlss.dll", first), "310.2.1.0").Updated);
        Assert.Equal("shipped-310.1", File.ReadAllText(dest + DlssSwapService.SwapperBackupSuffix));

        // The dest now holds an Exo file; the second write must not adopt it as
        // the original.
        Assert.Equal(1, svc.ApplyPackToGame(game, Pack("nvngx_dlss.dll", second), "310.4.0.0").Updated);
        Assert.Equal("catalog-310.4", File.ReadAllText(dest));
        Assert.Equal("shipped-310.1", File.ReadAllText(dest + DlssSwapService.SwapperBackupSuffix));
        Assert.Equal("shipped-310.1", File.ReadAllText(dest + DlssSwapService.BackupSuffix));

        var restore = svc.RestoreGame(game);
        Assert.True(restore.Ok, restore.Message);
        Assert.Equal("shipped-310.1", File.ReadAllText(dest));
    }

    [Fact]
    public void ApplyPack_PromotesALegacyExoBakInsteadOfTheLiveExoFile()
    {
        var gameDir = Path.Combine(_root, "LegacyBak");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "exo-applied-310.2");
        WriteDummy(dest + DlssSwapService.BackupSuffix, "shipped-original");
        var packDll = Path.Combine(_root, "pack-legacy", "nvngx_dlss.dll");
        WriteDummy(packDll, "exo-applied-310.4");
        var game = Game("steam:legacy-bak", gameDir);
        var svc = SvcWithVersions(new()
        {
            ["exo-applied-310.2"] = "310.2.1.0",
            ["exo-applied-310.4"] = "310.4.0.0",
            ["shipped-original"] = "310.1.0.0",
        });

        var result = svc.ApplyPackToGame(game, Pack("nvngx_dlss.dll", packDll), "310.4.0.0");

        Assert.True(result.Ok, result.Message);
        Assert.Equal("shipped-original", File.ReadAllText(dest + DlssSwapService.SwapperBackupSuffix));
        Assert.True(svc.RestoreGame(game).Ok);
        Assert.Equal("shipped-original", File.ReadAllText(dest));
    }

    [Fact]
    public void ForeignWrite_DropsExoClaim_ButKeepsTheShippedOriginal()
    {
        var gameDir = Path.Combine(_root, "ForeignWrite");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "shipped-original");
        var packDll = Path.Combine(_root, "pack-foreign", "nvngx_dlss.dll");
        WriteDummy(packDll, "exo-applied");
        var game = Game("steam:foreign-write", gameDir);
        var svc = SvcWithVersions(new()
        {
            ["shipped-original"] = "310.1.0.0",
            ["exo-applied"] = "310.4.0.0",
        });
        Assert.True(svc.ApplyPackToGame(game, Pack("nvngx_dlss.dll", packDll), "310.4.0.0").Ok);

        WriteDummy(dest, "some-other-tool");
        DlssSwapService.InvalidateForeignWrite(dest);

        Assert.False(File.Exists(dest + DlssSwapService.WrittenSuffix));
        Assert.Equal("shipped-original", File.ReadAllText(dest + DlssSwapService.SwapperBackupSuffix));
        var restore = svc.RestoreGame(game);
        Assert.False(restore.Ok);
        Assert.Equal(DlssSwapService.StaleRestoreMessage, restore.Message);
        Assert.Equal("some-other-tool", File.ReadAllText(dest));
    }

    [Fact]
    public void Restore_RefusesADestWithNoCapturedOriginal()
    {
        var gameDir = Path.Combine(_root, "NoOriginal");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "exo-applied");
        // Exo wrote here, but the captured shipped copy is gone.
        WriteDummy(dest + DlssSwapService.WrittenSuffix, Sha256Hex(dest));

        var result = new DlssSwapService().RestoreGame(Game("steam:no-original", gameDir));

        Assert.False(result.Ok);
        Assert.Equal(DlssSwapService.NoOriginalMessage, result.Message);
        Assert.Equal("exo-applied", File.ReadAllText(dest));
        var outcome = Assert.Single(result.Files!);
        Assert.Equal("failed", outcome.State);
        Assert.Equal(DlssSwapService.NoOriginalMessage, outcome.Message);
    }

    [Fact]
    public void MaxVersionText_PicksTheNewestAdvertised()
    {
        Assert.Equal("310.4.0.0", DlssSwapService.MaxVersionText("310.2.1", "310.4.0.0", null));
        Assert.Equal("310.4.0.0", DlssSwapService.MaxVersionText("310.4.0", "310.4.0.0"));
    }

    [Fact]
    public async Task UpdateAll_RefusesAnFsr4OnlyGameWhenTheGpuCannotRunIt()
    {
        var gameDir = Path.Combine(_root, "Fsr4NoGpu");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll");
        WriteDummy(dest, "shipped-fsr4");

        var svc = new DlssSwapService { Fsr4Supported = () => false };
        var result = await svc.UpdateGameAsync(Game("steam:fsr4-nogpu", gameDir), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(GpuCapability.Fsr4NeedsRdna4, result.Message);
        Assert.Equal("shipped-fsr4", File.ReadAllText(dest));
    }

    [Fact]
    public async Task UpdateGame_RefusesProtectedTitlesBeforeCancellationOrNetworkWork()
    {
        var gameDir = Path.Combine(_root, "ProtectedBeforeFetch");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "shipped");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var result = await new DlssSwapService().UpdateGameAsync(
            new GameEntry
            {
                Id = "riot:valorant",
                Title = "VALORANT",
                Store = StoreKind.Riot,
                Installed = true,
                Path = gameDir,
            },
            canceled.Token);

        Assert.False(result.Ok);
        Assert.Equal(DlssSwapService.AntiCheatMessage, result.Message);
        Assert.Equal("shipped", File.ReadAllText(dest));
        Assert.False(File.Exists(dest + DlssSwapService.SwapperBackupSuffix));
        Assert.False(File.Exists(dest + DlssSwapService.WrittenSuffix));
    }

    [Fact]
    public async Task StatusForProtectedTitleIsReadOnlyAndDoesNotFetch()
    {
        var gameDir = Path.Combine(_root, "ProtectedStatus");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "foreign");
        WriteDummy(dest + DlssSwapService.SwapperBackupSuffix, "original");
        WriteDummy(dest + DlssSwapService.WrittenSuffix, new string('A', 64));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var game = new GameEntry
        {
            Id = "riot:valorant-status",
            Title = "VALORANT",
            Store = StoreKind.Riot,
            Installed = true,
            Path = gameDir,
        };

        var status = await new DlssSwapService().GetStatusAsync([game], canceled.Token);

        Assert.True(status.AntiCheatWarning);
        Assert.Equal(DlssSwapService.AntiCheatMessage, status.Message);
        Assert.True(File.Exists(dest + DlssSwapService.WrittenSuffix));
        Assert.False(File.Exists(dest + DlssSwapService.StaleSuffix));
    }

    [Fact]
    public async Task MutationsForTheSameGameAreSerialized()
    {
        var gameDir = Path.Combine(_root, "Serialized");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "shipped");
        var first = Path.Combine(_root, "serialized-a", "nvngx_dlss.dll");
        var second = Path.Combine(_root, "serialized-b", "nvngx_dlss.dll");
        WriteDummy(first, "catalog-a");
        WriteDummy(second, "catalog-b");

        var inside = 0;
        var maximum = 0;
        using var ready = new ManualResetEventSlim(false);
        var svc = new DlssSwapService
        {
            FileVersion = path =>
            {
                if (!path.Equals(dest, StringComparison.OrdinalIgnoreCase))
                    return path.Equals(first, StringComparison.OrdinalIgnoreCase) ? "2.0.0.0" : "3.0.0.0";
                var current = Interlocked.Increment(ref inside);
                var observed = Volatile.Read(ref maximum);
                while (current > observed)
                {
                    var prior = Interlocked.CompareExchange(ref maximum, current, observed);
                    if (prior == observed) break;
                    observed = prior;
                }
                ready.Wait(TimeSpan.FromSeconds(1));
                Thread.Sleep(80);
                Interlocked.Decrement(ref inside);
                return "1.0.0.0";
            },
        };
        var game = Game("steam:serialized", gameDir);

        var one = Task.Run(() => svc.ApplyPackToGame(game, Pack("nvngx_dlss.dll", first), "2.0.0.0"));
        var two = Task.Run(() => svc.ApplyPackToGame(game, Pack("nvngx_dlss.dll", second), "3.0.0.0"));
        ready.Set();
        await Task.WhenAll(one, two);

        Assert.Equal(1, maximum);
        Assert.False(DlssSwapService.IsMutationGateRetained(game));
    }

    [Fact]
    public async Task UpdateAll_RefusesGamesWithNoDestAtAll()
    {
        var gameDir = Path.Combine(_root, "NoXess");
        Directory.CreateDirectory(gameDir);
        WriteDummy(Path.Combine(gameDir, "game.exe"), "exe");

        var result = await new DlssSwapService().UpdateGameAsync(
            Game("steam:no-xess", gameDir),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no swappable", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(gameDir, "libxess.dll")));
    }

    [Fact]
    public void ApplyPack_SkipsFsr4DestsWhenTheGpuCannotRunThem()
    {
        var gameDir = Path.Combine(_root, "MixedFsr");
        Directory.CreateDirectory(gameDir);
        var fsr31 = Path.Combine(gameDir, "amd_fidelityfx_dx12.dll");
        var upscaler = Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll");
        WriteDummy(fsr31, "shipped-fsr31");
        WriteDummy(upscaler, "shipped-fsr4");
        var packDir = Path.Combine(_root, "pack-mixed");
        Directory.CreateDirectory(packDir);
        WriteDummy(Path.Combine(packDir, "amd_fidelityfx_dx12.dll"), "catalog-fsr31");
        WriteDummy(Path.Combine(packDir, "amd_fidelityfx_upscaler_dx12.dll"), "catalog-fsr4");

        var svc = new DlssSwapService { Fsr4Supported = () => false };
        var result = svc.ApplyPackToGame(
            Game("steam:mixed-fsr", gameDir),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["amd_fidelityfx_dx12.dll"] = Path.Combine(packDir, "amd_fidelityfx_dx12.dll"),
                ["amd_fidelityfx_upscaler_dx12.dll"] = Path.Combine(packDir, "amd_fidelityfx_upscaler_dx12.dll"),
            },
            "1.0.1");

        Assert.True(result.Ok, result.Message);
        Assert.Equal("catalog-fsr31", File.ReadAllText(fsr31));
        Assert.Equal("shipped-fsr4", File.ReadAllText(upscaler));
    }

    [Fact]
    public void ApplyPack_RefusesAntiCheatTitles()
    {
        var gameDir = Path.Combine(_root, "Valorant");
        Directory.CreateDirectory(gameDir);
        var dest = Path.Combine(gameDir, "nvngx_dlss.dll");
        WriteDummy(dest, "shipped-dlss");
        var packDll = Path.Combine(_root, "pack-ac", "nvngx_dlss.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(packDll)!);
        WriteDummy(packDll, "catalog-dlss");
        var game = new GameEntry
        {
            Id = "riot:valorant",
            Title = "VALORANT",
            Store = StoreKind.Riot,
            Installed = true,
            Path = gameDir,
        };

        var result = new DlssSwapService().ApplyPackToGame(
            game,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["nvngx_dlss.dll"] = packDll,
            },
            "310.4.0.0");

        Assert.False(result.Ok);
        Assert.Equal(DlssSwapService.AntiCheatMessage, result.Message);
        Assert.Equal("shipped-dlss", File.ReadAllText(dest));
    }

    [Fact]
    public void Restore_PutsBackEveryDestInOnePass()
    {
        var gameDir = Path.Combine(_root, "RestoreAll");
        Directory.CreateDirectory(gameDir);
        var dlss = Path.Combine(gameDir, "nvngx_dlss.dll");
        var xess = Path.Combine(gameDir, "libxess.dll");
        WriteDummy(dlss, "swapped-dlss");
        WriteDummy(dlss + DlssSwapService.SwapperBackupSuffix, "shipped-dlss");
        WriteDummy(xess, "swapped-xess");
        WriteDummy(xess + DlssSwapService.SwapperBackupSuffix, "shipped-xess");

        var result = new DlssSwapService().RestoreGame(Game("steam:restore-all", gameDir));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.Updated);
        Assert.Equal("shipped-dlss", File.ReadAllText(dlss));
        Assert.Equal("shipped-xess", File.ReadAllText(xess));
        Assert.Equal(2, result.Files!.Count(file => file.State == "restored"));
    }

    [Fact]
    public void Restore_SaysNothingToRestoreWhenExoNeverWrote()
    {
        var gameDir = Path.Combine(_root, "RestoreNone");
        Directory.CreateDirectory(gameDir);
        WriteDummy(Path.Combine(gameDir, "nvngx_dlss.dll"), "shipped");

        var result = new DlssSwapService().RestoreGame(Game("steam:restore-none", gameDir));

        Assert.True(result.Ok, result.Message);
        Assert.Contains("Nothing to restore", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("shipped", File.ReadAllText(Path.Combine(gameDir, "nvngx_dlss.dll")));
    }

    [Fact]
    public void AnnotateRowActions_FlagsRestorableRowsAndFsr4OnTheWrongGpu()
    {
        var gameDir = Path.Combine(_root, "Annotate");
        Directory.CreateDirectory(gameDir);
        var dlss = Path.Combine(gameDir, "nvngx_dlss.dll");
        var upscaler = Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll");
        WriteDummy(dlss, "swapped");
        WriteDummy(dlss + DlssSwapService.SwapperBackupSuffix, "shipped");
        WriteDummy(upscaler, "shipped-fsr4");

        var rows = DlssSwapService.AnnotateRowActions(
            DlssSwapService.WithFullDestCatalog(
                new DlssSwapService().Detect([Game("steam:annotate", gameDir)]),
                "steam:annotate",
                "Annotate"),
            fsr4Supported: false);

        var dlssRow = Assert.Single(rows, row => row.FileName == "nvngx_dlss.dll");
        Assert.True(dlssRow.CanRestore);
        Assert.Null(dlssRow.UnsupportedReason);

        var fsr4Row = Assert.Single(rows, row => row.FileName == "amd_fidelityfx_upscaler_dx12.dll");
        Assert.False(fsr4Row.CanRestore);
        Assert.Equal(GpuCapability.Fsr4NeedsRdna4, fsr4Row.UnsupportedReason);

        var missing = Assert.Single(rows, row => row.FileName == "libxess.dll");
        Assert.False(missing.Present);
        Assert.False(missing.CanRestore);
    }

    [Fact]
    public void AnnotateRowActions_KeepsFsr4WhenTheGpuCanRunIt()
    {
        var rows = DlssSwapService.AnnotateRowActions(
            DlssSwapService.WithFullDestCatalog([], "steam:none", "None"),
            fsr4Supported: true);

        Assert.All(rows, row => Assert.Null(row.UnsupportedReason));
    }

    private static void CreateJunction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(process);
        process!.WaitForExit();
        Assert.True(process.ExitCode == 0 && Directory.Exists(link), "mklink /J failed");
    }
}
