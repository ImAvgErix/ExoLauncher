using ExoLauncher.Helpers;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// System > Display offers what Windows and the driver list, less what the screen's EDID rules out.
/// It fails silently if it: offers a rate above the screen's range (144 Hz on a 60 Hz screen, as
/// Windows lists with "Hide modes" off), drops a rate the screen lists only in a timing or a VIC
/// (the VG248's 165 Hz, a TV's 24 Hz), offers a depth past the panel's on DisplayPort (12-bit on an
/// 8-bit panel, as NVIDIA reports) or past HDMI's deep color, offers YCbCr the screen doesn't take,
/// offers an HDMI color whose TMDS rate doesn't fit the mode (RGB 10-bit at 4K 60 on HDMI 2.0),
/// hides the rate in use, or rules anything out when there is no EDID.
/// </summary>
public sealed class MonitorCapsTests
{
    private static OutputColor C(string format, int bpc, bool full = false) => new(format, bpc, full);

    // What a driver answers when it answers for the link alone: every format at 6-16 bits (RGB in both ranges).
    private static readonly OutputColor[] Everything =
    [
        .. new[] { 6, 8, 10, 12, 16 }.SelectMany(bpc => new[] { C("rgb", bpc, true), C("rgb", bpc) }),
        .. new[] { "444", "422", "420" }.SelectMany(format => new[] { 6, 8, 10, 12 }.Select(bpc => C(format, bpc))),
    ];

    // The modes Windows lists at the current resolution with "Hide modes that this monitor cannot display" off.
    private static readonly int[] AllModes = [24, 25, 30, 50, 59, 60, 75, 100, 120, 144, 165, 240];

    /// <summary>A base EDID block: version 1.<paramref name="minor"/>, input byte 20, features byte 24, a range limits descriptor, one detailed timing.</summary>
    internal static byte[] Base(byte minor, byte input, byte features, (int Min, int Max)? range, (int W, int H, int HTotal, int VTotal, int Hz)? timing = null, int extensions = 0)
    {
        var edid = new byte[128 * (1 + extensions)];
        byte[] header = [0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0];
        header.CopyTo(edid, 0);
        (edid[18], edid[19], edid[20], edid[24], edid[126]) = (1, minor, input, features, (byte)extensions);
        for (var at = 38; at < 54; at++) edid[at] = 1; // no standard timings
        if (timing is { } t)
        {
            var clock = t.HTotal * t.VTotal * t.Hz / 10_000;
            int hb = t.HTotal - t.W, vb = t.VTotal - t.H;
            byte[] dtd = [(byte)clock, (byte)(clock >> 8), (byte)t.W, (byte)hb, (byte)((t.W >> 8) << 4 | hb >> 8), (byte)t.H, (byte)vb, (byte)((t.H >> 8) << 4 | vb >> 8)];
            dtd.CopyTo(edid, 54);
        }
        if (range is { } r) (edid[72 + 3], edid[72 + 5], edid[72 + 6]) = (0xFD, (byte)r.Min, (byte)r.Max);
        return edid;
    }

    /// <summary>A CTA-861 extension at block 1 with byte 3 <paramref name="flags"/> and these data blocks.</summary>
    private static byte[] WithCta(byte[] edid, byte flags, params byte[][] blocks)
    {
        var data = blocks.SelectMany(block => block).ToArray();
        (edid[128], edid[129], edid[130], edid[131]) = (0x02, 0x03, (byte)(4 + data.Length), flags);
        data.CopyTo(edid, 132);
        return edid;
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static (int[] Rates, List<OutputColor> Colors) Offer(byte[]? edid, int current, long pixelRate, IEnumerable<int>? modes = null, IEnumerable<OutputColor>? driver = null)
    {
        var caps = Edid.Parse(edid);
        return (DisplayOutput.OfferedRates(modes ?? AllModes, current, caps), DisplayOutput.Offered(driver ?? Everything, caps, pixelRate));
    }

    [Fact]
    public void A_60_hz_8_bit_hdmi_monitor_gets_60_hz_and_8_bit_rgb()
    {
        // EDID 1.3, range limits 50-75 Hz, 1080p 60, an HDMI VSDB with no deep color (max TMDS 225 MHz), no YCbCr.
        var edid = WithCta(Base(3, 0x80, 0x0A, (50, 75), (1920, 1080, 2200, 1125, 60), extensions: 1), 0x00,
            [0x67, 0x03, 0x0C, 0x00, 0x10, 0x00, 0x00, 0x2D]);
        var (rates, colors) = Offer(edid, 60, 148_500_000);
        Assert.Equal([50, 59, 60, 75], rates);
        Assert.Equal([C("rgb", 8, true), C("rgb", 8)], colors);
        Assert.Equal(C("rgb", 8, true), DisplayOutput.BestColor(colors, 60, Edid.Parse(edid)!.PanelBpc).Color);
    }

    [Fact]
    public void A_144_hz_10_bit_displayport_monitor_gets_up_to_144_hz_and_10_bit()
    {
        // EDID 1.4, 10 bits per color, DisplayPort, RGB + YCbCr 4:4:4 + 4:2:2, range 48-144 Hz, 1440p 144.
        var edid = Base(4, 0xB5, 0x18, (48, 144), (2560, 1440, 2720, 1481, 144));
        var (rates, colors) = Offer(edid, 144, 580_000_000, [60, 75, 100, 120, 144, 165, 240]);
        Assert.Equal([60, 75, 100, 120, 144], rates);
        Assert.Equal(
            [C("rgb", 6, true), C("rgb", 6), C("rgb", 8, true), C("rgb", 8), C("rgb", 10, true), C("rgb", 10), C("444", 8), C("444", 10), C("422", 8), C("422", 10)],
            colors);
        Assert.Equal(C("rgb", 10, true), DisplayOutput.BestColor(colors, 144, Edid.Parse(edid)!.PanelBpc).Color);
    }

    [Fact]
    public void A_4k_60_hz_hdmi_2_monitor_gets_60_hz_and_only_what_fits_the_cable()
    {
        // EDID 1.3, range 24-60 Hz, 4K 60; CTA: YCbCr 4:4:4 + 4:2:2, VICs 97 95 94 93 16, HDMI VSDB
        // (DC_36, DC_30, DC_Y444, 340 MHz), HDMI Forum VSDB (600 MHz, 10 and 12-bit 4:2:0), 4:2:0 map.
        var edid = WithCta(Base(3, 0x80, 0x0A, (24, 60), (3840, 2160, 4400, 2250, 60), extensions: 1), 0x30,
            [0x45, 97, 95, 94, 93, 16],
            [0x67, 0x03, 0x0C, 0x00, 0x10, 0x00, 0x38, 0x44],
            [0x67, 0xD8, 0x5D, 0xC4, 0x01, 0x78, 0x80, 0x03],
            [0xE2, 0x0F, 0x01]);
        var (rates, colors) = Offer(edid, 60, 594_000_000);
        Assert.Equal([24, 25, 30, 50, 59, 60], rates);
        // 594 MHz: 8-bit RGB and 4:4:4 fit 600 MHz, deeper doesn't; 4:2:2 and 4:2:0 carry 12-bit.
        Assert.Equal(
            [C("rgb", 8, true), C("rgb", 8), C("444", 8), C("422", 8), C("422", 10), C("422", 12), C("420", 8), C("420", 10), C("420", 12)],
            colors);
        Assert.Equal(C("rgb", 8, true), DisplayOutput.BestColor(colors, 60).Color);
        // At 30 Hz (297 MHz) 12-bit RGB fits.
        Assert.Contains(C("rgb", 12, true), Offer(edid, 30, 297_000_000).Colors);
    }

    [Fact]
    public void The_vg248_at_165_hz_keeps_every_option_it_has()
    {
        // The ASUS VG248 on this PC's RTX 3070 (DisplayPort), as Windows holds its EDID: 1.4, 8-bit, range 48-165 Hz.
        var edid = Hex("00ffffffffffff0006b3ac2401010101241f0104a5351e783b57e0a5544f9d26125054b7ef00714f8180814081c081009500b3000101866f80a07038404030203500132b2100001a000000fd0030a5c1c129010a202020202020000000fc0056473234380a20202020202020000000ff003030303030303030303030300a016c"
            + "020318f14b900504030201111213141f23090707830100008a4d80a070382c4030203500132b2100001afe5b80a07038354030203500132b2100001afc7e80887038124018203500132b2100001ea49c80a07038594030203500132b2100001a023a801871382d40582c4500132b2100001e000000000000000000000000000f");
        var caps = Edid.Parse(edid)!;
        Assert.Equal((48, 165, (int?)8, 0L), (caps.MinHz, caps.MaxHz, caps.PanelBpc, caps.MaxTmdsHz));
        // What the RTX 3070 reports for it: RGB 6-16, YCbCr 4:4:4 and 4:2:2 8-12.
        OutputColor[] driver =
        [
            C("rgb", 6, true), C("rgb", 8, true), C("rgb", 10, true), C("rgb", 12, true), C("rgb", 16, true), C("rgb", 6), C("rgb", 8), C("rgb", 10), C("rgb", 12),
            C("444", 8), C("444", 10), C("444", 12), C("422", 8), C("422", 10), C("422", 12),
        ];
        var (rates, colors) = Offer(edid, 165, 401_000_000, [60, 85, 100, 120, 144, 165], driver);
        Assert.Equal([60, 85, 100, 120, 144, 165], rates);
        Assert.Equal([C("rgb", 6, true), C("rgb", 8, true), C("rgb", 6), C("rgb", 8), C("444", 8), C("422", 8)], colors);
    }

    [Fact]
    public void An_hdmi_screen_that_states_deep_color_gets_it()
    {
        // The onn monitor on this PC: EDID 1.3, range 48-76 Hz, HDMI VSDB with DC_36, DC_30, DC_Y444 and 225 MHz.
        var edid = Hex("00ffffffffffff003dce010101010101211d0103803420782aa5c5a6544f9c23125054bfef00d1c0b30095008180814081c03140b30f023a801871382d40582c450005432100001e662156aa51001e30468f330005432100001e000000fd00304c1e5511000a202020202020000000fc003130303030323438370a2020200175"
            + "020320b14b901f0403020e1e011f121367030c001000382d83010000e3050301023a801871382d40582c450005432100001e011d8018711c1620582c250005432100009e011d007251d01e206e28550005432100001e8c0ad08a20e02d10103e9600054321000018000000000000000000000000000000000000000000000005");
        var (rates, colors) = Offer(edid, 60, 148_500_000);
        Assert.Equal([50, 59, 60, 75], rates);
        Assert.Contains(C("rgb", 12, true), colors);
        Assert.Contains(C("444", 12), colors);
        Assert.DoesNotContain(colors, color => color.Bpc == 6 || color.Format == "420");
    }

    [Fact]
    public void Rates_listed_only_in_a_timing_or_a_vic_count()
    {
        // Range limits up to 144, a detailed timing at 165, and a 4K 24 Hz VIC under the range's 48.
        var edid = WithCta(Base(4, 0xA5, 0, (48, 144), (1920, 1080, 2080, 1169, 165), extensions: 1), 0, [0x41, 93]);
        var caps = Edid.Parse(edid)!;
        Assert.Equal((24, 165), (caps.MinHz, caps.MaxHz));
        Assert.Equal([24, 165], DisplayOutput.OfferedRates([24, 165, 240], 165, caps));
    }

    [Fact]
    public void Without_an_edid_nothing_is_ruled_out_and_the_rate_in_use_stays()
    {
        var (rates, colors) = Offer(null, 60, 0);
        Assert.Equal(AllModes, rates);
        Assert.DoesNotContain(colors, color => color.Bpc == 16);
        Assert.Contains(C("rgb", 12, true), colors);
        Assert.Null(Edid.Parse(new byte[128]));
        // A rate in use stays listed even when the EDID rules it out.
        Assert.Contains(144, DisplayOutput.OfferedRates([60, 144], 144, Edid.Parse(Base(4, 0xA5, 0, (50, 75)))));
    }

    [Theory]
    [InlineData(16, 60)]
    [InlineData(31, 50)]
    [InlineData(63, 120)]
    [InlineData(93, 24)]
    [InlineData(97, 60)]
    [InlineData(118, 120)]
    [InlineData(199, 60)]
    [InlineData(219, 120)]
    [InlineData(0, 0)]
    [InlineData(128, 0)]
    public void Vics_read_their_rate(int vic, int hz) => Assert.Equal(hz, Edid.VicRate(vic));
}
