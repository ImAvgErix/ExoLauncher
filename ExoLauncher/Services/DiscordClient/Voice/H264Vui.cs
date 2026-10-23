using System.Numerics;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Makes a frame's SPS say no frame is reordered (VUI bitstream_restriction with max_num_reorder_frames 0 and
/// max_dec_frame_buffering at most max_num_ref_frames), as libwebrtc's SpsVuiRewriter writes it. Discord's apps receive
/// with libwebrtc, whose depacketizer rewrites any other SPS (sps_vui_rewriter.cc, Direction::kIncoming) after DAVE
/// sealed the frame: the tag no longer matches, every key frame fails to decrypt, and viewers wait forever. Rewritten
/// here, before DAVE, the SPS arrives as libwebrtc wants it and is left alone.
/// </summary>
internal static class H264Vui
{
    /// <summary>The Annex B frame with each SPS that needed it rewritten; the same array when none did.</summary>
    internal static byte[] NoReordering(byte[] frame)
    {
        var nals = H264Rtp.Nals(frame);
        byte[]?[]? rewritten = null;
        for (var i = 0; i < nals.Count; i++)
            if (nals[i].Count > 1 && (nals[i][0] & 0x1F) == 7 && Rewrite(nals[i]) is { } sps)
                (rewritten ??= new byte[]?[nals.Count])[i] = sps;
        if (rewritten is null) return frame;
        using var output = new MemoryStream(frame.Length + 16);
        for (var i = 0; i < nals.Count; i++)
        {
            output.Write([0, 0, 0, 1]);
            if (rewritten[i] is { } sps) output.Write(sps);
            else output.Write(nals[i].AsSpan());
        }
        return output.ToArray();
    }

    /// <summary>The SPS NAL unit as libwebrtc would rewrite it; null when it needs nothing (or doesn't parse, and is kept).</summary>
    internal static byte[]? Rewrite(ReadOnlySpan<byte> nal)
    {
        var rbsp = Unescape(nal[1..]);
        var source = new BitReader(rbsp);
        if (UpToVui(source) is not { } maxRefFrames) return null;
        var target = new BitWriter();
        var prefix = new BitReader(rbsp);
        for (var left = source.Position; left > 0; left -= Math.Min(32, left)) Copy(prefix, target, Math.Min(32, left));

        bool changed;
        var vui = source.Bits(1);
        target.Bits(1, 1);
        if (vui == 0)
        {
            // No VUI: aspect ratio, overscan, video signal type, chroma location, timing, both HRDs and pic_struct all
            // absent, then the restriction.
            target.Bits(0, 8);
            target.Bits(1, 1);
            Restriction(target, maxRefFrames);
            changed = true;
        }
        else
        {
            if (Copy(source, target, 1) == 1 && Copy(source, target, 8) == 255) Copy(source, target, 32); // aspect ratio, Extended_SAR
            if (Copy(source, target, 1) == 1) Copy(source, target, 1); // overscan
            if (Copy(source, target, 1) == 1 && (Copy(source, target, 5) & 1) == 1) Copy(source, target, 24); // video signal type, colors
            if (Copy(source, target, 1) == 1) { CopyUe(source, target); CopyUe(source, target); } // chroma location
            if (Copy(source, target, 1) == 1) { Copy(source, target, 32); Copy(source, target, 32); Copy(source, target, 1); } // timing
            var nalHrd = Copy(source, target, 1) == 1;
            if (nalHrd) Hrd(source, target);
            var vclHrd = Copy(source, target, 1) == 1;
            if (vclHrd) Hrd(source, target);
            if (nalHrd || vclHrd) Copy(source, target, 1); // low_delay_hrd_flag
            Copy(source, target, 1); // pic_struct_present_flag
            target.Bits(1, 1);
            if (source.Bits(1) == 0)
            {
                Restriction(target, maxRefFrames);
                changed = true;
            }
            else
            {
                Copy(source, target, 1);
                for (var i = 0; i < 4; i++) CopyUe(source, target);
                var reorder = source.Ue();
                var buffering = source.Ue();
                target.Ue(0);
                target.Ue(maxRefFrames);
                changed = reorder != 0 || buffering > maxRefFrames;
            }
        }
        if (!changed || !source.Ok) return null;
        // rbsp_trailing_bits: the stop bit, then zeros to the byte.
        target.Bits(1, 1);
        target.Align();
        return [nal[0], .. Escape(target.ToArray())];
    }

    // SpsParser::ParseSpsUpToVui: reads to just before vui_parameters_present_flag; max_num_ref_frames, or null when
    // libwebrtc would refuse it.
    private static uint? UpToVui(BitReader r)
    {
        var profile = r.Bits(8);
        r.Bits(16); // constraint flags, level
        if (r.Ue() > 31) return null; // seq_parameter_set_id
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134)
        {
            var chroma = r.Ue();
            if (chroma > 3) return null;
            if (chroma == 3) r.Bits(1); // separate_colour_plane_flag
            r.Ue(); // bit_depth_luma_minus8
            r.Ue(); // bit_depth_chroma_minus8
            r.Bits(1); // qpprime_y_zero_transform_bypass_flag
            if (r.Bits(1) == 1) // seq_scaling_matrix_present_flag
                for (var i = 0; i < (chroma == 3 ? 12 : 8); i++)
                {
                    if (r.Bits(1) == 0) continue;
                    int last = 8, next = 8;
                    for (var j = 0; j < (i < 6 ? 16 : 64); j++)
                    {
                        if (next != 0)
                        {
                            var delta = r.Se();
                            if (delta is < -128 or > 127) return null;
                            next = (last + delta + 256) % 256;
                        }
                        if (next != 0) last = next;
                    }
                }
        }
        if (r.Ue() > 12) return null; // log2_max_frame_num_minus4
        var pocType = r.Ue();
        if (pocType > 2) return null;
        if (pocType == 0 && r.Ue() > 12) return null; // log2_max_pic_order_cnt_lsb_minus4
        if (pocType == 1)
        {
            r.Bits(1); // delta_pic_order_always_zero_flag
            r.Se(); // offset_for_non_ref_pic
            r.Se(); // offset_for_top_to_bottom_field
            var cycle = r.Ue();
            for (var i = 0u; i < cycle && r.Ok; i++) r.Se();
        }
        var maxRefFrames = r.Ue();
        r.Bits(1); // gaps_in_frame_num_value_allowed_flag
        r.Ue(); // pic_width_in_mbs_minus1
        r.Ue(); // pic_height_in_map_units_minus1
        if (r.Bits(1) == 0) r.Bits(1); // frame_mbs_only_flag, mb_adaptive_frame_field_flag
        r.Bits(1); // direct_8x8_inference_flag
        if (r.Bits(1) == 1) for (var i = 0; i < 4; i++) r.Ue(); // frame cropping
        return r.Ok ? maxRefFrames : null;
    }

    // AddBitstreamRestriction: the defaults, no reordering, and as many frames buffered as are referenced.
    private static void Restriction(BitWriter w, uint maxRefFrames)
    {
        w.Bits(1, 1); // motion_vectors_over_pic_boundaries_flag
        w.Ue(2); // max_bytes_per_pic_denom
        w.Ue(1); // max_bits_per_mb_denom
        w.Ue(16); // log2_max_mv_length_horizontal
        w.Ue(16); // log2_max_mv_length_vertical
        w.Ue(0); // max_num_reorder_frames
        w.Ue(maxRefFrames); // max_dec_frame_buffering
    }

    private static void Hrd(BitReader from, BitWriter to)
    {
        var count = from.Ue(); // cpb_cnt_minus1
        to.Ue(count);
        Copy(from, to, 8); // bit_rate_scale, cpb_size_scale
        for (var i = 0u; i <= count && i < 32 && from.Ok; i++)
        {
            CopyUe(from, to);
            CopyUe(from, to);
            Copy(from, to, 1); // cbr_flag
        }
        Copy(from, to, 20); // the four delay lengths
    }

    private static uint Copy(BitReader from, BitWriter to, int count)
    {
        var value = from.Bits(count);
        to.Bits(value, count);
        return value;
    }

    private static void CopyUe(BitReader from, BitWriter to) => to.Ue(from.Ue());

    // Emulation prevention (H.264 7.4.1): 00 00 03 0x carries 00 00 0x.
    private static byte[] Unescape(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        var zeros = 0;
        foreach (var b in data)
        {
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return [.. output];
    }

    private static byte[] Escape(byte[] data)
    {
        var output = new List<byte>(data.Length + 4);
        var zeros = 0;
        foreach (var b in data)
        {
            if (zeros >= 2 && b <= 3) { output.Add(3); zeros = 0; }
            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return [.. output];
    }

    private sealed class BitReader(byte[] data)
    {
        private int _bit;

        internal int Position => _bit;
        internal bool Ok => _bit <= data.Length * 8;

        internal uint Bits(int count)
        {
            var value = 0u;
            for (var i = 0; i < count; i++, _bit++)
                value = value << 1 | (uint)(_bit < data.Length * 8 ? data[_bit >> 3] >> (7 - (_bit & 7)) & 1 : 0);
            return value;
        }

        internal uint Ue()
        {
            var zeros = 0;
            while (Bits(1) == 0)
                if (++zeros > 31 || !Ok) { _bit = data.Length * 8 + 1; return 0; }
            return (uint)((1UL << zeros) - 1 + Bits(zeros));
        }

        internal int Se()
        {
            var k = Ue();
            return (k & 1) == 1 ? (int)((k + 1) / 2) : -(int)(k / 2);
        }
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bits;

        internal void Bits(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--, _bits++)
            {
                if ((_bits & 7) == 0) _bytes.Add(0);
                if ((value >> i & 1) != 0) _bytes[^1] |= (byte)(0x80 >> (_bits & 7));
            }
        }

        internal void Ue(uint value)
        {
            var code = (ulong)value + 1;
            var length = 64 - BitOperations.LeadingZeroCount(code);
            Bits(0, length - 1);
            Bits((uint)code, length);
        }

        internal void Align()
        {
            while ((_bits & 7) != 0) Bits(0, 1);
        }

        internal byte[] ToArray() => [.. _bytes];
    }
}
