using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>Which upscaler file is newer, and what the vendor watch (DlssSwapService.VendorWatch.cs) will fetch.</summary>
public sealed class UpscalerReleaseTests
{
    private static DlssSwapService.PinnedFile Fsr31(string version, string label) =>
        new("amd_fidelityfx_dx12.dll", "fsr-" + label, "FidelityFX " + label, version, label, "GPUOpen-LibrariesAndSDKs/FidelityFX-SDK", "", "", 1, "");

    // Call of Duty ships FSR 3.1.2 as loader 1.0.2.38022, above 3.1.4's 1.0.1.41314: Exo took it for newer than its
    // own and never offered 3.1.4.
    [Fact]
    public void Fsr31BuildsAreComparedByTheReleaseTheyAreKnownBy()
    {
        DlssSwapService.PinnedFile[] releases = [Fsr31("1.0.1.41314", "3.1.4"), Fsr31("1.0.2.38022", "3.1.2")];

        Assert.Null(DlssSwapService.SkipApplyReason("amd_fidelityfx_dx12.dll", "1.0.2.38022", "1.0.1.41314", releases));
        Assert.NotNull(DlssSwapService.SkipApplyReason("amd_fidelityfx_dx12.dll", "1.0.1.41314", "1.0.2.38022", releases));
        // A build no list names is compared by its file version.
        Assert.NotNull(DlssSwapService.SkipApplyReason("amd_fidelityfx_dx12.dll", "1.0.1.42386", "1.0.1.41314", releases));
    }

    [Fact]
    public void TheWatchFetchesOnlyTheSdksOwnDllsThatAGameShips()
    {
        var fsr = DlssSwapService.VendorSdks.Single(sdk => sdk.ReleasePrefix == "fsr-");
        var blob = new string('a', 40);
        var listing = JsonNode.Parse($$"""
            [
              { "type": "file", "name": "amd_fidelityfx_upscaler_dx12.dll", "size": 28761864, "sha": "{{blob}}" },
              { "type": "file", "name": "amd_fidelityfx_denoiser_dx12.dll", "size": 13782280, "sha": "{{blob}}" },
              { "type": "file", "name": "readme.txt", "size": 10, "sha": "{{blob}}" },
              { "type": "dir", "name": "amd_fidelityfx_loader_dx12.dll", "size": 0, "sha": "{{blob}}" },
              { "type": "file", "name": "amd_fidelityfx_framegeneration_dx12.dll", "size": 1, "sha": "not-a-hash" }
            ]
            """);
        var used = new HashSet<string> { "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll" };

        var wanted = DlssSwapService.SdkListing(listing, fsr, used);

        Assert.Equal([("amd_fidelityfx_upscaler_dx12.dll", 28761864L, blob)], wanted);
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/signedbin/amd_fidelityfx_upscaler_dx12.dll", true)]
    [InlineData("https://raw.githubusercontent.com/NVIDIA/DLSS/374959484e79a640feaba44c93ac8cfb0a03f5b5/lib/Windows_x86_64/rel/nvngx_dlss.dll", true)]
    [InlineData("https://raw.githubusercontent.com/intel/xess/8fe81bdbbaf00b3c1b733fd0d830c333dc84e6f0/bin/libxess.dll", true)]
    // A tag or branch instead of a commit, another folder, another file, another repository, extra path.
    [InlineData("https://raw.githubusercontent.com/NVIDIA/DLSS/main/lib/Windows_x86_64/rel/nvngx_dlss.dll", false)]
    [InlineData("https://raw.githubusercontent.com/NVIDIA/DLSS/374959484e79a640feaba44c93ac8cfb0a03f5b5/lib/Windows_x86_64/dev/nvngx_dlss.dll", false)]
    [InlineData("https://raw.githubusercontent.com/intel/xess/8fe81bdbbaf00b3c1b733fd0d830c333dc84e6f0/bin/basic_sample_frame_generation.exe", false)]
    [InlineData("https://raw.githubusercontent.com/someone/DLSS/374959484e79a640feaba44c93ac8cfb0a03f5b5/lib/Windows_x86_64/rel/nvngx_dlss.dll", false)]
    [InlineData("https://raw.githubusercontent.com/intel/xess/8fe81bdbbaf00b3c1b733fd0d830c333dc84e6f0/bin/libxess.dll?x=1", false)]
    [InlineData("https://raw.githubusercontent.com/intel/xess/8FE81BDBBAF00B3C1B733FD0D830C333DC84E6F0/bin/libxess.dll", false)]
    public void TheWatchFetchesOnlyAVendorsSdkDllAtACommit(string url, bool allowed) =>
        Assert.Equal(allowed, DlssSwapService.IsVendorSdkUrl(url));

    [Fact]
    public void GitBlobHashMatchesGits()
    {
        var path = Path.Combine(Path.GetTempPath(), "exo-blob-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("hello\n"));
            // git hash-object of "hello\n".
            Assert.Equal("CE013625030BA8DBA906F756967F9E9CA394464A", DlssSwapService.GitBlobSha1(path));
        }
        finally { File.Delete(path); }
    }
}
