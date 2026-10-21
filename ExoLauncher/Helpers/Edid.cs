namespace ExoLauncher.Helpers;

/// <summary>
/// What a screen's EDID says its input takes. Windows lists every mode the GPU has when "Hide modes
/// that this monitor cannot display" is off or the monitor isn't recognized, and drivers answer for
/// the link, not the screen (an RTX 3070 reports 12-bit on an 8-bit DisplayPort panel; AMD answers
/// format and depth apart), so System > Display offers only what both they and this allow.
/// MinHz and MaxHz are the range limits widened by every timing listed (0 when there are none:
/// any rate); Depths are the bits per color each format ("rgb", "444", "422", "420") takes, a format
/// missing when the screen doesn't take it; MaxTmdsHz is HDMI's TMDS limit (0 when unknown or the
/// screen takes HDMI 2.1 FRL, which it doesn't bound).
/// </summary>
internal sealed record MonitorCaps(int MinHz, int MaxHz, IReadOnlyDictionary<string, int[]> Depths, int? PanelBpc, long MaxTmdsHz)
{
    /// <summary>True when the screen takes this rate, within a hertz: Windows lists 59.94 Hz as 59.</summary>
    internal bool TakesRate(int hz) => MaxHz == 0 || (hz >= MinHz - 1 && hz <= MaxHz + 1);

    /// <summary>True when the screen takes this color and, on HDMI, its TMDS rate at <paramref name="pixelRate"/> (Hz, 0 unknown) fits.</summary>
    internal bool Takes(OutputColor color, long pixelRate) =>
        Depths.TryGetValue(color.Format, out var depths) && depths.Contains(color.Bpc)
        && (MaxTmdsHz == 0 || pixelRate == 0 || color.Format switch
        {
            "422" => pixelRate, // 4:2:2 always rides a 12-bit container at the pixel rate
            "420" => pixelRate * color.Bpc / 16,
            _ => pixelRate * color.Bpc / 8,
        } <= MaxTmdsHz);
}

/// <summary>
/// Reads <see cref="MonitorCaps"/> from an EDID (VESA E-EDID 1.4, CTA-861-H, DisplayID 1.3/2.0;
/// byte offsets as Linux's drm_edid.c and drm_displayid.c read them):
/// - base block: byte 20 input (bit 7 digital; 1.4: bits 6-4 depth 6-16, bits 3-0 interface, 5
///   DisplayPort), byte 24 bits 4-3 on a 1.4 digital input (YCbCr 4:2:2, 4:4:4), the range limits
///   descriptor (0xFD: min and max vertical Hz at bytes 5 and 6, +255 when byte 4 bits 0 and 1 say so),
///   detailed timings, standard timings (refresh = byte 2 bits 5-0 + 60);
/// - CTA-861 extension (tag 2): byte 3 bits 5-4 (YCbCr 4:4:4, 4:2:2), detailed timings, video data
///   blocks' VICs, HDMI VSDB (OUI 00-0C-03: byte 6 DC_36bit bit 5, DC_30bit 4, DC_Y444 3; byte 7 max
///   TMDS in 5 MHz), HDMI Forum VSDB (OUI C4-5D-D8: byte 5 max TMDS in 5 MHz, byte 7 Max_FRL_Rate bits
///   7-4, DC_36bit_420 bit 1, DC_30bit_420 bit 0), YCbCr 4:2:0 video and capability map blocks
///   (extended tags 14 and 15);
/// - DisplayID extension (tag 0x70): type I (0x03, 10 kHz clock) and VII (0x22, 1 kHz) detailed timings,
///   type IX (0x24) formula timings.
/// Depth on DisplayPort is the 1.4 base block's (8 when it doesn't say; 6-bit RGB is DisplayPort's
/// fallback every sink takes); on HDMI it is the VSDB's deep color (8 without one; 4:2:2 takes 8-12).
/// </summary>
internal static class Edid
{
    internal static MonitorCaps? Parse(byte[]? edid)
    {
        if (edid is not { Length: >= 128 } || edid[0] != 0 || edid[1] != 0xFF || edid[7] != 0 || (edid[20] & 0x80) == 0) return null;
        var v14 = edid[18] == 1 && edid[19] >= 4;
        var depthCode = (edid[20] >> 4) & 7;
        int? panel = v14 && depthCode is >= 1 and <= 6 ? 4 + depthCode * 2 : null;
        var displayPort = v14 && (edid[20] & 0x0F) == 5;
        bool y444 = v14 && (edid[24] & 0x08) != 0, y422 = v14 && (edid[24] & 0x10) != 0, y420 = false;
        bool vsdb = false, dc30 = false, dc36 = false, dcY444 = false, dc30x420 = false, dc36x420 = false, frl = false;
        long tmds = 0;
        int rangeMin = 0, rangeMax = 0;
        var rates = new List<double>();

        for (var at = 54; at < 126; at += 18)
        {
            if (edid[at] != 0 || edid[at + 1] != 0) rates.Add(DetailedRate(edid, at));
            else if (edid[at + 3] == 0xFD)
            {
                rangeMin = edid[at + 5] + ((edid[at + 4] & 3) == 3 ? 255 : 0);
                rangeMax = edid[at + 6] + ((edid[at + 4] & 2) != 0 ? 255 : 0);
            }
        }
        for (var at = 38; at < 54; at += 2)
            if (edid[at] is not (0 or 1)) rates.Add((edid[at + 1] & 0x3F) + 60);

        for (var block = 128; block + 128 <= edid.Length; block += 128)
        {
            if (edid[block] == 0x02)
            {
                y444 |= (edid[block + 3] & 0x20) != 0;
                y422 |= (edid[block + 3] & 0x10) != 0;
                var dtd = edid[block + 2];
                for (var at = block + 4; at < block + Math.Min((int)dtd, 127);)
                {
                    int tag = edid[at] >> 5, length = edid[at] & 0x1F;
                    if (at + length >= block + 127) break;
                    var oui = length >= 3 ? edid[at + 1] | edid[at + 2] << 8 | edid[at + 3] << 16 : 0;
                    if (tag == 2 || (tag == 7 && length >= 1 && edid[at + 1] == 14))
                        for (var i = tag == 2 ? 1 : 2; i <= length; i++)
                        {
                            var svd = edid[at + i];
                            if (VicRate(svd is >= 129 and <= 192 ? svd & 0x7F : svd) is > 0 and var hz) rates.Add(hz);
                        }
                    if (tag == 7 && length >= 1 && edid[at + 1] is 14 or 15) y420 = true;
                    if (tag == 3 && oui == 0x000C03)
                    {
                        vsdb = true;
                        if (length >= 6) (dc36, dc30, dcY444) = ((edid[at + 6] & 0x20) != 0, (edid[at + 6] & 0x10) != 0, (edid[at + 6] & 0x08) != 0);
                        if (length >= 7) tmds = Math.Max(tmds, edid[at + 7] * 5_000_000L);
                    }
                    if (tag == 3 && oui == 0xC45DD8)
                    {
                        if (length >= 5) tmds = Math.Max(tmds, edid[at + 5] * 5_000_000L);
                        if (length >= 7) (frl, dc36x420, dc30x420) = ((edid[at + 7] >> 4) != 0, (edid[at + 7] & 0x02) != 0, (edid[at + 7] & 0x01) != 0);
                    }
                    at += 1 + length;
                }
                if (dtd >= 4)
                    for (var at = block + dtd; at + 18 <= block + 127 && (edid[at] != 0 || edid[at + 1] != 0); at += 18)
                        rates.Add(DetailedRate(edid, at));
            }
            else if (edid[block] == 0x70)
            {
                var end = Math.Min(block + 5 + edid[block + 2], block + 127);
                for (var at = block + 5; at + 3 <= end;)
                {
                    int tag = edid[at], length = edid[at + 2], payload = at + 3;
                    if (payload + length > end) break;
                    if (tag is 0x03 or 0x22)
                        for (var d = payload; d + 20 <= payload + length; d += 20)
                        {
                            var clock = (edid[d] | edid[d + 1] << 8 | edid[d + 2] << 16) + 1.0;
                            var total = (U16(edid, d + 4) + 1.0 + U16(edid, d + 6) + 1) * (U16(edid, d + 12) + 1.0 + U16(edid, d + 14) + 1);
                            rates.Add(clock * (tag == 0x03 ? 10_000 : 1_000) / total);
                        }
                    if (tag == 0x24)
                        for (var d = payload; d + 6 <= payload + length; d += 6)
                            rates.Add(edid[d + 5] + 1);
                    at = payload + length;
                }
            }
        }

        rates.RemoveAll(rate => rate is < 1 or > 1000 || double.IsNaN(rate));
        var minHz = rates.Count == 0 ? rangeMin : (int)Math.Round(rates.Min());
        if (rangeMin > 0) minHz = Math.Min(minHz, rangeMin);
        var maxHz = Math.Max(rangeMax, rates.Count == 0 ? 0 : (int)Math.Round(rates.Max()));

        var hdmi = vsdb && !displayPort;
        var depths = new Dictionary<string, int[]>();
        if (hdmi)
        {
            var deep = Deep(dc30, dc36);
            depths["rgb"] = deep;
            if (y444) depths["444"] = dcY444 ? deep : [8];
            if (y422) depths["422"] = [8, 10, 12];
            if (y420) depths["420"] = Deep(dc30x420, dc36x420);
        }
        else
        {
            var rgb = new[] { 6, 8, 10, 12 }.Where(bpc => bpc <= (panel ?? 8)).ToArray();
            var ycc = rgb.Where(bpc => bpc >= 8).ToArray();
            depths["rgb"] = rgb;
            foreach (var (format, on) in new[] { ("444", y444), ("422", y422), ("420", y420) })
                if (on && ycc.Length > 0) depths[format] = ycc;
        }
        return new MonitorCaps(minHz, maxHz, depths, panel, hdmi && !frl ? tmds : 0);
    }

    private static int[] Deep(bool ten, bool twelve) => new[] { 8, ten ? 10 : 0, twelve ? 12 : 0 }.Where(bpc => bpc > 0).ToArray();

    private static int U16(byte[] data, int at) => data[at] | data[at + 1] << 8;

    /// <summary>A detailed timing descriptor's refresh rate; 0 for an interlaced one.</summary>
    private static double DetailedRate(byte[] e, int at)
    {
        if ((e[at + 17] & 0x80) != 0) return 0;
        var clock = U16(e, at) * 10_000.0;
        var h = e[at + 2] + ((e[at + 4] & 0xF0) << 4) + e[at + 3] + ((e[at + 4] & 0x0F) << 8);
        var v = e[at + 5] + ((e[at + 7] & 0xF0) << 4) + e[at + 6] + ((e[at + 7] & 0x0F) << 8);
        return h == 0 || v == 0 ? 0 : clock / (h * v);
    }

    private static readonly int[] Seven = [24, 25, 30, 50, 60, 100, 120], Five = [24, 25, 30, 50, 60], Big = [24, 25, 30, 48, 50, 60, 100, 120];

    /// <summary>A CTA-861-H VIC's frame (or field) rate; 0 for a code that isn't one.</summary>
    internal static int VicRate(int vic) => vic switch
    {
        (>= 1 and <= 16) or 35 or 36 => 60,
        (>= 17 and <= 31) or (>= 37 and <= 39) => 50,
        32 or 60 => 24,
        33 or 61 => 25,
        34 or 62 => 30,
        (>= 40 and <= 45) or 64 => 100,
        (>= 46 and <= 51) or 63 => 120,
        >= 52 and <= 55 => 200,
        >= 56 and <= 59 => 240,
        >= 65 and <= 92 => Seven[(vic - 65) % 7],
        >= 93 and <= 107 => Five[(vic - 93) % 5],
        >= 108 and <= 116 => 48,
        117 or 119 or 218 => 100,
        118 or 120 or 193 or 219 => 120,
        >= 121 and <= 127 => Big[vic - 121],
        >= 194 and <= 217 => Big[(vic - 194) % 8],
        _ => 0,
    };
}
