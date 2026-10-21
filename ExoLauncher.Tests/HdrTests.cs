using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class HdrTests
{
    [Theory]
    [InlineData("{{Video\n|hdr                        = true\n|hdr notes = HDR10+ too\n}}", "true")]
    [InlineData("|hdr = false\n", "false")]
    [InlineData("|hdr= hackable\n", "hackable")]
    [InlineData("|hdr = \n", "unknown")]
    [InlineData("|ray tracing = true\n", null)]
    public void Reads_the_wiki_hdr_field(string wikitext, string? expected) =>
        Assert.Equal(expected, HdrGames.ReadHdrField(wikitext));

    [Fact]
    public void Only_a_pcgamingwiki_page_link_names_a_page()
    {
        Assert.Equal("Hell is Us", HdrGames.PageFromWikiUrl(new Uri("https://www.pcgamingwiki.com/wiki/Hell_is_Us")));
        Assert.Null(HdrGames.PageFromWikiUrl(new Uri("https://example.com/wiki/Hell_is_Us")));
        Assert.Null(HdrGames.PageFromWikiUrl(new Uri("http://www.pcgamingwiki.com/wiki/Hell_is_Us")));
        Assert.Null(HdrGames.PageFromWikiUrl(null));
    }

    [Theory]
    [InlineData("auto", true, true)]
    [InlineData("auto", false, false)]
    [InlineData("auto", null, false)]
    [InlineData("on", false, true)]
    [InlineData("off", true, false)]
    public void Hdr_comes_on_only_for_games_that_use_it(string mode, bool? supports, bool expected) =>
        Assert.Equal(expected, MainWindow.WantsHdr(mode, supports));

    [Fact]
    public void Peak_brightness_comes_from_the_edid_hdr_block()
    {
        var edid = new byte[256];
        edid[128] = 0x02; // CTA-861 extension
        edid[130] = 20;   // detailed timings start after the data blocks
        // Extended tag block (7 << 5 | length 6): HDR static metadata (6), EOTFs, descriptor, max code 115.
        byte[] block = [0xE6, 0x06, 0x05, 0x01, 115, 90, 0];
        block.CopyTo(edid, 132);
        // 50 x 2^(115/32) is about 604 nits.
        Assert.Equal(604, WindowsHdr.EdidPeakNits(edid));
        Assert.Null(WindowsHdr.EdidPeakNits(new byte[128]));
    }
}
