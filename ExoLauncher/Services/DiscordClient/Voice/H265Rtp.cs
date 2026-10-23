using System.Buffers.Binary;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// H.265 over RTP (RFC 7798) for Discord video, beside <see cref="H264Rtp"/>: Discord's apps list H.265 ahead of H.264,
/// and the server picks it for a stream when everyone in it can play it. NAL units have two-byte headers (type in bits
/// 1-6 of the first). Sending splits an Annex B frame into single NAL units and fragmentation units (type 49);
/// receiving also takes aggregation packets (48), and rebuilds frames with 4-byte start codes as DAVE sealed them.
/// </summary>
internal static class H265Rtp
{
    internal const int PayloadType = 103, RtxPayloadType = 104;
    private const int MaxPayload = 1100;
    private const int Aggregation = 48, Fragment = 49;
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    private static int Type(byte first) => (first >> 1) & 0x3F;

    /// <summary>RTP payloads for one frame: each NAL unit alone when it fits, else fragmentation units.</summary>
    internal static List<byte[]> Packetize(byte[] frame)
    {
        var packets = new List<byte[]>();
        foreach (var nal in H264Rtp.Nals(frame))
        {
            if (nal.Count <= MaxPayload) { packets.Add(nal.ToArray()); continue; }
            byte first = nal[0], second = nal[1];
            for (var offset = 2; offset < nal.Count; offset += MaxPayload - 3)
            {
                var size = Math.Min(MaxPayload - 3, nal.Count - offset);
                var packet = new byte[size + 3];
                // The payload header is the NAL's own with type 49; the FU header carries start, end and the NAL's type.
                packet[0] = (byte)((first & 0x81) | (Fragment << 1));
                packet[1] = second;
                packet[2] = (byte)(Type(first) | (offset == 2 ? 0x80 : 0) | (offset + size >= nal.Count ? 0x40 : 0));
                nal.AsSpan(offset, size).CopyTo(packet.AsSpan(3));
                packets.Add(packet);
            }
        }
        return packets;
    }

    /// <summary>Whether an Annex B frame holds a picture a decoder can start from (IRAP: BLA, IDR or CRA).</summary>
    internal static bool IsKeyframe(byte[] frame) => H264Rtp.Nals(frame).Any(n => n.Count > 1 && Type(n[0]) is >= 16 and <= 21);

    /// <summary>An Annex B frame's NAL unit types and sizes, for app.log ("32:24 33:40 34:7 19:81234").</summary>
    internal static string Describe(byte[] frame) =>
        string.Join(' ', H264Rtp.Nals(frame).Where(n => n.Count > 1).Select(n => $"{Type(n[0])}:{n.Count}"));

    /// <summary>Rebuilds one frame's RTP payloads (single NAL, aggregation, fragmentation) as Annex B; null when malformed.</summary>
    internal static byte[]? Build(IEnumerable<byte[]> payloads)
    {
        using var output = new MemoryStream();
        foreach (var payload in payloads)
        {
            if (payload.Length < 2) return null;
            var type = Type(payload[0]);
            if (type < Aggregation)
            {
                output.Write(StartCode);
                output.Write(payload);
            }
            else if (type == Aggregation)
            {
                // [size u16][NAL] repeated after the two-byte payload header (no DONL: Discord's senders don't interleave).
                var at = 2;
                while (at + 2 <= payload.Length)
                {
                    var size = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(at));
                    at += 2;
                    if (size < 2 || at + size > payload.Length) return null;
                    output.Write(StartCode);
                    output.Write(payload, at, size);
                    at += size;
                }
            }
            else if (type == Fragment)
            {
                if (payload.Length < 3) return null;
                if ((payload[2] & 0x80) != 0)
                {
                    output.Write(StartCode);
                    output.WriteByte((byte)((payload[0] & 0x81) | ((payload[2] & 0x3F) << 1)));
                    output.WriteByte(payload[1]);
                }
                output.Write(payload, 3, payload.Length - 3);
            }
            else return null;
        }
        return output.ToArray();
    }
}
