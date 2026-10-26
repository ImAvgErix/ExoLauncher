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

    // A game Exo is starting counts as running until its process shows up, so HDR is on before the process starts;
    // failing quietly would mean HDR going back off at once (the game not yet "running") or staying on after a launch that never happened.
    [Fact]
    public void A_game_being_started_counts_until_it_runs_or_the_grace_ends()
    {
        var launching = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["steam:730"] = 1_000, ["epic:fn"] = 1_000 };

        // Still starting, inside the grace: both wait.
        Assert.Equal(["steam:730", "epic:fn"], MainWindow.PendingHdrLaunches(launching, [], 2_000, 120_000).OrderByDescending(id => id));

        // One shows up running (any letter case): it is the running look's now, not a pending launch.
        Assert.Equal(["epic:fn"], MainWindow.PendingHdrLaunches(launching, ["STEAM:730"], 3_000, 120_000));
        Assert.DoesNotContain("steam:730", launching.Keys);

        // The other never appears: past the grace it is dropped, so HDR goes back off.
        Assert.Empty(MainWindow.PendingHdrLaunches(launching, [], 1_000 + 120_001, 120_000));
        Assert.Empty(launching);
    }

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
