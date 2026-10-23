using System.IO.Compression;
using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the AMD and Intel driver row could go wrong without anyone seeing it (no Radeon or Intel GPU on this PC; the pages
/// are AMD's and Intel's own, saved October 22, 2026 under Fixtures/GpuDrivers):
/// - AMD's product list doesn't parse, a card gets another card's page (the RX 9070 the 9070 XT's), "Radeon Graphics"
///   matches some product, or a GPU built into a Ryzen finds no page (it is its processor's);
/// - the Windows 10, Windows 7 or Linux section, or AMD Software: PRO Edition, is read instead of Windows 11's Adrenalin;
///   an older Adrenalin wins, or the optional label is lost;
/// - an Intel GPU gets the wrong family's driver (an Arc card the 11th–14th Gen one, an Iris Xe laptop the Arc one, a
///   UHD 630 the 11th–14th Gen one), or "Intel(R) UHD Graphics" matches a page for another chip; i7-12700 matches i7-12700K;
/// - Intel's version, installer, size, SHA-512 or date is misread, or another version's installer is taken;
/// - versions compare as text (26.9.2 after 26.10.1);
/// - the install command loses a switch (Intel's -s, AMD's log), restarts the PC, or cleans away another Intel GPU's driver;
/// - AMD's log result is missed (UTF-16, an earlier result), so a failure reads as done or a restart isn't said;
/// - a driver that installed reads as failed because the installer said restart, or a stop says nothing useful;
/// - the wait for a game or a call goes on when neither is there, repeats itself every poll, or never ends.
/// </summary>
public sealed class GpuDriverUpdateTests
{
    private const string AmdRoot = "https://www.amd.com/en/support/downloads/drivers.html/";

    /// <summary>A saved page under Fixtures/GpuDrivers (ChipsetUpdateTests reads Intel's chipset page from there too).</summary>
    internal static string Page(string name)
    {
        using var file = File.OpenRead(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "GpuDrivers", name + ".html.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static readonly Lazy<IReadOnlyList<(string Title, string Url)>> Products = new(() => GpuDriverUpdate.AmdProducts(Page("amd-drivers")));

    [Fact]
    public void AmdSelectorListsEveryProductWithItsPage()
    {
        Assert.True(Products.Value.Count > 1000);
        Assert.Contains(Products.Value, product => product.Title == "AMD Radeon™ RX 9070 XT"
            && product.Url == AmdRoot + "graphics/radeon-rx/radeon-rx-9000-series/amd-radeon-rx-9070-xt.html");
    }

    [Theory]
    [InlineData("AMD Radeon RX 9070 XT", null, "graphics/radeon-rx/radeon-rx-9000-series/amd-radeon-rx-9070-xt.html")]
    [InlineData("AMD Radeon RX 9070", null, "graphics/radeon-rx/radeon-rx-9000-series/amd-radeon-rx-9070.html")]
    [InlineData("AMD Radeon RX 7800 XT", "AMD Ryzen 7 7800X3D 8-Core Processor", "graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7800-xt.html")]
    [InlineData("Radeon RX 580 Series", null, "graphics/radeon-600-500-400/radeon-rx-500-series/radeon-rx-580.html")]
    [InlineData("AMD Radeon PRO W7900", null, "graphics/radeon-pro/radeon-pro-w7000-series/amd-radeon-pro-w7900.html")]
    [InlineData("AMD Radeon(TM) Graphics", "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "processors/ryzen/ryzen-7000-series/amd-ryzen-7-7840hs.html")]
    [InlineData("AMD Radeon 780M Graphics", "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "processors/ryzen/ryzen-7000-series/amd-ryzen-7-7840hs.html")]
    [InlineData("AMD Radeon(TM) Graphics", "AMD Ryzen 9 7950X3D 16-Core Processor", "processors/ryzen/ryzen-7000-series/amd-ryzen-9-7950x3d.html")]
    [InlineData("AMD Radeon(TM) Graphics", null, null)]
    [InlineData("AMD Radeon(TM) Graphics", "12th Gen Intel(R) Core(TM) i7-12700K", null)]
    public void AmdFindsTheCardsPageOrItsProcessors(string card, string? cpu, string? page) =>
        Assert.Equal(page is null ? null : AmdRoot + page, GpuDriverUpdate.AmdProductPage(Products.Value, card, cpu));

    [Theory]
    [InlineData("amd-radeon-rx-9070-xt", "26.10.1", "https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.10.1-win11-b.exe", 887_000_000L)]
    [InlineData("amd-radeon-rx-6800-xt", "26.10.1", "https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.10.1-win11-a.exe", 941_000_000L)]
    [InlineData("radeon-rx-580", "26.5.2", "https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-win11-may-vega-polaris.exe", 623_000_000L)]
    [InlineData("amd-ryzen-7-7840hs", "26.10.1", "https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.10.1-win11-b.exe", 887_000_000L)]
    [InlineData("amd-radeon-pro-w7900", "26.10.1", "https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.10.1-win11-b.exe", 887_000_000L)]
    public void AmdPageGivesWindows11Adrenalin(string page, string version, string url, long size)
    {
        var driver = GpuDriverUpdate.AmdDriver(Page(page));
        Assert.NotNull(driver);
        Assert.Equal(("amd", version, url, size, "recommended"), (driver.Vendor, driver.Version, driver.Url, driver.Size, driver.Kind));
        Assert.Null(driver.Sha512);
    }

    [Fact]
    public void AmdReleaseDateIsTheDayAtLocalMidnight() =>
        Assert.Equal("2026-10-20T00:00:00", GpuDriverUpdate.AmdDriver(Page("amd-radeon-rx-9070-xt"))?.Released);

    private static string AmdArticle(string revision, string file) =>
        $"""<article class="container-fluid driver-download-details"><div class="row"><div class="col-12"><h4>AMD Software: Adrenalin Edition</h4></div>""" +
        $"""<div><strong>Revision Number</strong> <p>{revision}</p></div><div><strong>File Size</strong> <p>887 MB</p></div>""" +
        $"""<div><strong>Release Date</strong> <p> 2026-10-28 </p></div><a href="https://drivers.amd.com/drivers/{file}" role="button">Download</a></div></article>""";

    private static string AmdSection(string os, params string[] articles) =>
        $"""<div class="accordion-item" id="x"><h2 class="accordion-header"><button class="accordion-button collapsed" type="button"> {os} </button></h2>""" +
        $"""<div class="accordion-collapse collapse"><div class="accordion-body">{string.Concat(articles)}</div></div></div>""";

    [Fact]
    public void AmdNewerOptionalWinsAndSaysSo()
    {
        var html = AmdSection("Windows 11 - 64-Bit Edition",
            AmdArticle("Adrenalin 26.10.1 WHQL (Recommended)", "whql-26.10.1.exe"),
            AmdArticle("Adrenalin 26.10.2 (Optional)", "26.10.2.exe"));
        Assert.Equal(("26.10.2", "optional"), (GpuDriverUpdate.AmdDriver(html)?.Version, GpuDriverUpdate.AmdDriver(html)?.Kind));
    }

    [Fact]
    public void AmdSameVersionPrefersRecommended()
    {
        var html = AmdSection("Windows 11 - 64-Bit Edition",
            AmdArticle("Adrenalin 26.10.1 (Optional)", "a.exe"),
            AmdArticle("Adrenalin 26.10.1 WHQL (Recommended)", "b.exe"));
        Assert.Equal("recommended", GpuDriverUpdate.AmdDriver(html)?.Kind);
    }

    [Fact]
    public void AmdOtherSystemsAndForeignLinksAreLeftOut()
    {
        Assert.Null(GpuDriverUpdate.AmdDriver(AmdSection("Windows 10 - 64-Bit Edition", AmdArticle("Adrenalin 26.10.1 WHQL (Recommended)", "a.exe"))));
        var foreign = AmdSection("Windows 11 - 64-Bit Edition", AmdArticle("Adrenalin 26.10.1 WHQL (Recommended)", "a.exe")
            .Replace("https://drivers.amd.com/drivers/", "https://evil.example/drivers/", StringComparison.Ordinal));
        Assert.Null(GpuDriverUpdate.AmdDriver(foreign));
    }

    [Fact]
    public async Task AmdLookupReadsTheSelectorThenTheCardsPage()
    {
        var asked = new List<string>();
        Task<string> Get(string url)
        {
            asked.Add(url);
            return Task.FromResult(url == GpuDriverUpdate.AmdSelector ? Page("amd-drivers")
                : url == AmdRoot + "graphics/radeon-rx/radeon-rx-9000-series/amd-radeon-rx-9070-xt.html" ? Page("amd-radeon-rx-9070-xt")
                : throw new InvalidOperationException("Not one of AMD's pages: " + url));
        }
        Assert.Equal("26.10.1", (await GpuDriverUpdate.AmdLatestAsync("AMD Radeon RX 9070 XT", null, Get))?.Version);
        Assert.Equal(2, asked.Count);
        asked.Clear();
        Assert.Null(await GpuDriverUpdate.AmdLatestAsync("AMD Radeon(TM) Graphics", null, Get));
        Assert.Single(asked);
    }

    private static readonly Dictionary<string, string> IntelFixtures = new()
    {
        [GpuDriverUpdate.IntelPages[0]] = "intel-arc-785597",
        [GpuDriverUpdate.IntelPages[1]] = "intel-11th-14th-gen-864990",
        [GpuDriverUpdate.IntelPages[2]] = "intel-7th-10th-gen-776137",
    };

    [Theory]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", "13th Gen Intel(R) Core(TM) i5-13400F", 0, 1, "32.0.101.9150")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", 0, 1, "32.0.101.9150")]
    [InlineData("Intel(R) Arc(TM) Graphics", "Intel(R) Core(TM) Ultra 7 155H", 0, 1, "32.0.101.9150")]
    [InlineData("Intel(R) Graphics", "Intel(R) Core(TM) Ultra 9 285K", 0, 1, "32.0.101.9150")]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", 1, 2, "32.0.101.7092")]
    [InlineData("Intel(R) UHD Graphics 770", "12th Gen Intel(R) Core(TM) i7-12700K", 1, 2, "32.0.101.7092")]
    [InlineData("Intel(R) UHD Graphics 630", null, 2, 3, "31.0.101.2145")]
    [InlineData("Intel(R) UHD Graphics 630", "Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", 2, 3, "31.0.101.2145")]
    [InlineData("Intel(R) UHD Graphics", "Intel(R) Celeron(R) N5095 @ 2.00GHz", 2, 3, "31.0.101.2145")]
    public async Task IntelPicksTheFamilysPage(string card, string? cpu, int page, int reads, string version)
    {
        var asked = new List<string>();
        var driver = await GpuDriverUpdate.IntelLatestAsync(card, cpu, url => { asked.Add(url); return Task.FromResult(Page(IntelFixtures[url])); });
        Assert.Equal((GpuDriverUpdate.IntelPages[page], version), (driver?.Page, driver?.Version));
        Assert.Equal(reads, asked.Count);
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics", "Intel(R) N100")]
    [InlineData("Intel(R) UHD Graphics", null)]
    [InlineData("Intel(R) HD Graphics 530", "Intel(R) Core(TM) i5-6500 CPU @ 3.20GHz")]
    public async Task IntelGivesNothingForAChipNoPageNames(string card, string? cpu) =>
        Assert.Null(await GpuDriverUpdate.IntelLatestAsync(card, cpu, url => Task.FromResult(Page(IntelFixtures[url]))));

    [Theory]
    [InlineData("intel-arc-785597", "32.0.101.9150", "https://downloadmirror.intel.com/930514/gfx_win_101.9150.exe", 862_600_000L,
        "3fffb91635055fadb4ad77afabd8d8801f71b805aa6ffd8d1ec60f1197b68e81c3157eccac9f9f8031a3a996105ac0657c2fa81c076ba7c567ad2ebdbb4d62c9", "2026-10-02T00:00:00")]
    [InlineData("intel-11th-14th-gen-864990", "32.0.101.7092", "https://downloadmirror.intel.com/929468/gfx_win_101.7092.exe", 744_700_000L,
        "35a863fc2ace37e267a1602eb992e54f909dbce6122b7224ad500301bbdb2389c38741800ffc4e389d91e1444bc96c2e26ee4283c97c4a7c7060064724385b9d", "2026-09-25T00:00:00")]
    [InlineData("intel-7th-10th-gen-776137", "31.0.101.2145", "https://downloadmirror.intel.com/929187/gfx_win_101.2145.exe", 278_100_000L,
        "d30369a17f66a787d477fe77787d934a1e74581f27cb19ba1608df22e76c8dce68b589db327456527ae9228d8788663ce005467df84c722c03f59a2e9297c2d5", "2026-09-22T00:00:00")]
    public void IntelPageGivesItsDriver(string page, string version, string url, long size, string sha, string released)
    {
        var driver = GpuDriverUpdate.IntelDriver(Page(page), "https://example.test/page");
        Assert.NotNull(driver);
        Assert.Equal(("intel", version, url, size, sha, released, "intel", "https://example.test/page"),
            (driver.Vendor, driver.Version, driver.Url, driver.Size, driver.Sha512, driver.Released, driver.Kind, driver.Page));
    }

    [Fact]
    public void IntelInstallerForAnotherVersionIsRefused()
    {
        var html = Page("intel-arc-785597").Replace("gfx_win_101.9150.exe", "gfx_win_101.9034.exe", StringComparison.Ordinal);
        Assert.Null(GpuDriverUpdate.IntelDriver(html, GpuDriverUpdate.IntelPages[0]));
    }

    [Fact]
    public void IntelModelMatchesWholeNamesOnly()
    {
        const string list = """<li class="dc-page-detailed-other-valid-products-panel__product"> <a href="/x">Intel® Core™ i7-12700K Processor (25M Cache, up to 5.00 GHz)</a> </li>""";
        Assert.True(GpuDriverUpdate.IntelCovers(list, "Intel(R) UHD Graphics 770", "12th Gen Intel(R) Core(TM) i7-12700K"));
        Assert.False(GpuDriverUpdate.IntelCovers(list, "Intel(R) UHD Graphics 770", "12th Gen Intel(R) Core(TM) i7-12700"));
        Assert.False(GpuDriverUpdate.IntelCovers(list, "Intel(R) UHD Graphics 770", "12th Gen Intel(R) Core(TM) i7-12700KF"));
    }

    [Theory]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", "i7-12700K")]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", "155H")]
    [InlineData("Intel(R) Core(TM) i5-10400 CPU @ 2.90GHz", "i5-10400")]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "i7-1165G7")]
    [InlineData("Intel(R) N100", "N100")]
    [InlineData("Intel(R) Celeron(R) N4500 @ 1.10GHz", "N4500")]
    [InlineData("Intel(R) Core(TM)", null)]
    [InlineData(null, null)]
    public void IntelCpuModelIsWhatIntelsListsWrite(string? cpu, string? model) => Assert.Equal(model, GpuDriverUpdate.IntelCpuModel(cpu));

    [Theory]
    [InlineData("amd", new[] { "AMD Radeon(TM) Graphics", "AMD Radeon RX 7800 XT" }, "AMD Radeon RX 7800 XT")]
    [InlineData("amd", new[] { "AMD Radeon 780M Graphics" }, "AMD Radeon 780M Graphics")]
    [InlineData("intel", new[] { "Intel(R) UHD Graphics 770", "Intel(R) Arc(TM) A770 Graphics" }, "Intel(R) Arc(TM) A770 Graphics")]
    [InlineData("intel", new[] { "NVIDIA GeForce RTX 3070", "Intel(R) UHD Graphics 770" }, "Intel(R) UHD Graphics 770")]
    [InlineData("amd", new[] { "NVIDIA GeForce RTX 3070" }, null)]
    public void PickTakesTheCard(string vendor, string[] adapters, string? card) => Assert.Equal(card, GpuDriverUpdate.Pick(vendor, adapters));

    [Theory]
    [InlineData("26.10.1", "26.9.2", true)]
    [InlineData("26.9.2", "26.10.1", false)]
    [InlineData("26.10.1", "26.10.1", false)]
    [InlineData("32.0.101.9150", "32.0.101.7092", true)]
    [InlineData("32.0.101.7092", "32.0.101.8864", false)]
    [InlineData("26.10.1", null, true)]
    [InlineData("not a version", "26.1.1", false)]
    public void NewerComparesNumbers(string latest, string? installed, bool newer) => Assert.Equal(newer, GpuDriverUpdate.Newer(latest, installed));

    [Theory]
    [InlineData("887 MB", 887_000_000L)]
    [InlineData("862.6 MB", 862_600_000L)]
    [InlineData("1.2 GB", 1_200_000_000L)]
    [InlineData("21 KB", 21_000L)]
    [InlineData("", 0L)]
    [InlineData(null, 0L)]
    public void BytesAsThePagesCount(string? text, long bytes) => Assert.Equal(bytes, GpuDriverUpdate.Bytes(text));

    [Fact]
    public void AmdInstallsSilentlyWithItsLogAndNoRestart()
    {
        var command = GpuDriverUpdate.AmdArguments(@"C:\Users\A B\AppData\Local\ExoLauncher\drivers-amd\26.10.1\install.log");
        Assert.Equal(@"-INSTALL -LOG ""C:\Users\A B\AppData\Local\ExoLauncher\drivers-amd\26.10.1\install.log""", command);
        Assert.DoesNotContain("-BOOT", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntelInstallsTheDriverAloneSilentlyWithoutRestartOrCleanup()
    {
        var parts = GpuDriverUpdate.IntelArguments.Split(' ');
        Assert.Equal("-s", parts[^1]);
        Assert.Contains("--overwrite", parts);
        Assert.Contains("--noExtras", parts);
        foreach (var never in new[] { "-b", "--reboot", "-f", "--fresh", "--terminateProcesses", "--unsigned" }) Assert.DoesNotContain(never, parts);
    }

    [Theory]
    [InlineData("[ResponseResult]\r\nResultCode =0 \r\n", 0)]
    [InlineData("[ResponseResult]\r\nResultCode =2 \r\nOS Reboot is recommended.\r\n", 2)]
    [InlineData("[ResponseResult]\nResultCode = 1\n", 1)]
    [InlineData("ResultCode =1 \r\n... retried ...\r\n[ResponseResult]\r\nResultCode =0 \r\n", 0)]
    [InlineData("Install started\r\n", null)]
    [InlineData(null, null)]
    public void AmdResultFromItsLog(string? log, int? result) => Assert.Equal(result, GpuDriverUpdate.AmdResult(log));

    [Fact]
    public void AmdResultFromAUtf16LogReadAsText() =>
        Assert.Equal(2, GpuDriverUpdate.AmdResult(string.Concat("[ResponseResult]\r\nResultCode =2 \r\n".Select(c => c + "\0"))));

    private static readonly GpuDriverUpdate.Latest Amd = new("amd", "26.10.1", "https://drivers.amd.com/drivers/x.exe", 0, null, "recommended");
    private static readonly GpuDriverUpdate.Latest Intel = new("intel", "32.0.101.9150", "https://downloadmirror.intel.com/1/gfx_win_101.9150.exe", 0, null, "intel");

    [Theory]
    [InlineData("amd", 0, 0, "26.10.1", true, "Driver 26.10.1 installed")]
    [InlineData("amd", 0, 2, "26.9.2", true, "Driver 26.10.1 installed. Restart your PC to finish")]
    [InlineData("amd", 0, 1, "26.10.1", true, "Driver 26.10.1 installed")]
    [InlineData("amd", 0, 1, "26.9.2", false, "AMD's installer stopped. Your driver is unchanged")]
    [InlineData("amd", 0, null, "26.9.2", false, "AMD's installer finished, but the driver is still 26.9.2")]
    [InlineData("amd", 5, null, "26.9.2", false, "AMD's installer stopped (code 5). Your driver is unchanged")]
    [InlineData("intel", 0, null, "32.0.101.9150", true, "Driver 32.0.101.9150 installed")]
    [InlineData("intel", 14, null, "32.0.101.7088", true, "Driver 32.0.101.9150 installed. Restart your PC to finish")]
    [InlineData("intel", 18, null, "32.0.101.7088", true, "Driver 32.0.101.9150 installed. Shut down your PC, then turn it on to finish")]
    [InlineData("intel", 8, null, "32.0.101.7088", false, "Intel's driver doesn't support this graphics card. Your driver is unchanged")]
    [InlineData("intel", 16, null, "32.0.101.7088", false, "Another install is running. Update again when it's done")]
    [InlineData("intel", 99, null, "32.0.101.7088", false, "Intel's installer stopped (code 99). Your driver is unchanged")]
    [InlineData("intel", 0, null, "32.0.101.7088", false, "Intel's installer finished, but the driver is still 32.0.101.7088")]
    [InlineData("intel", 0, null, null, false, "Intel's installer finished, but the driver is still the one before")]
    public void OutcomeTrustsTheVersionThenTheVendorsCode(string vendor, int exit, int? amdResult, string? installed, bool ok, string message) =>
        Assert.Equal(new GpuDriverUpdate.Ending(ok, message), GpuDriverUpdate.Outcome(vendor == "amd" ? Amd : Intel, exit, amdResult, installed));

    [Theory]
    [InlineData("\uFEFF{\"BuildInfo\":{\"ExternalVersion\":\"26.10.1\",\"RadeonSoftwareVersion\":\"26.10.1\"},\"Packages\":{}}", "26.10.1")]
    [InlineData("{\"BuildInfo\":{\"ExternalVersion\":\"26.10.1\"}}", null)]
    [InlineData("{\"BuildInfo\":[]}", null)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public void AmdPackageVersionFromItsManifest(string? manifest, string? version) => Assert.Equal(version, GpuDriverUpdate.AmdPackageVersion(manifest));

    [Fact]
    public async Task WaitSaysEachNewReasonOnceThenGoesOn()
    {
        var reasons = new Queue<string?>(["Waiting for your game to close", "Waiting for your game to close", "Waiting for your Discord call to end", null]);
        var said = new List<string>();
        await GpuDriverUpdate.WaitQuietAsync(() => reasons.Dequeue(), said.Add, TimeSpan.FromMilliseconds(1));
        Assert.Equal(["Waiting for your game to close", "Waiting for your Discord call to end"], said);
        Assert.Empty(reasons);
    }

    [Fact]
    public async Task WaitGoesStraightOnWithNothingToWaitFor()
    {
        var said = new List<string>();
        await GpuDriverUpdate.WaitQuietAsync(() => null, said.Add, TimeSpan.FromMilliseconds(1));
        Assert.Empty(said);
    }
}
