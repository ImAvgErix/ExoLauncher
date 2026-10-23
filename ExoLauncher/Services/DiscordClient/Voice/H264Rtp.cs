using System.Buffers.Binary;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// H.264 over RTP (RFC 6184) for Discord video. Receiving: one <see cref="H264Receiver"/> per SSRC
/// orders packets, asks again for lost ones (NACK, answered on the RTX stream), and rebuilds each
/// frame as Annex B with 4-byte start codes, the form DAVE's sender encrypted (libdave
/// codec_utils.cpp ProcessFrameH264 writes long start codes because WebRTC's depacketizer does).
/// Sending: <see cref="Packetize"/> splits an Annex B frame into single NAL units and FU-A fragments.
/// </summary>
internal static class H264Rtp
{
    internal const int PayloadType = 101, RtxPayloadType = 102;
    private const int MaxPayload = 1100;
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    /// <summary>The NAL units of an Annex B frame (3- or 4-byte start codes), without their start codes.</summary>
    internal static List<ArraySegment<byte>> Nals(byte[] frame)
    {
        var nals = new List<ArraySegment<byte>>();
        int? start = null;
        for (var i = 0; i + 2 < frame.Length; i++)
        {
            if (frame[i] != 0 || frame[i + 1] != 0 || frame[i + 2] != 1) continue;
            if (start is { } s)
            {
                var end = i > 0 && frame[i - 1] == 0 ? i - 1 : i;
                if (end > s) nals.Add(new ArraySegment<byte>(frame, s, end - s));
            }
            start = i + 3;
            i += 2;
        }
        if (start is { } last && last < frame.Length) nals.Add(new ArraySegment<byte>(frame, last, frame.Length - last));
        return nals;
    }

    /// <summary>RTP payloads for one frame: each NAL unit alone when it fits, else FU-A fragments.</summary>
    internal static List<byte[]> Packetize(byte[] frame)
    {
        var packets = new List<byte[]>();
        foreach (var nal in Nals(frame))
        {
            if (nal.Count <= MaxPayload) { packets.Add(nal.ToArray()); continue; }
            var header = nal[0];
            var indicator = (byte)((header & 0xE0) | 28);
            for (var offset = 1; offset < nal.Count; offset += MaxPayload - 2)
            {
                var size = Math.Min(MaxPayload - 2, nal.Count - offset);
                var packet = new byte[size + 2];
                packet[0] = indicator;
                packet[1] = (byte)((header & 0x1F) | (offset == 1 ? 0x80 : 0) | (offset + size >= nal.Count ? 0x40 : 0));
                nal.AsSpan(offset, size).CopyTo(packet.AsSpan(2));
                packets.Add(packet);
            }
        }
        return packets;
    }

    /// <summary>Whether an Annex B frame holds an IDR slice (a keyframe a decoder can start from).</summary>
    internal static bool IsKeyframe(byte[] frame) => Nals(frame).Any(n => n.Count > 0 && (n[0] & 0x1F) == 5);

    /// <summary>An Annex B frame's NAL unit types and sizes and its SPS's profile and level, for app.log ("7:25 8:4 5:81234, profile 100 level 42").</summary>
    internal static string Describe(byte[] frame)
    {
        var nals = Nals(frame);
        var text = string.Join(' ', nals.Where(n => n.Count > 0).Select(n => $"{n[0] & 0x1F}:{n.Count}"));
        var sps = nals.FirstOrDefault(n => n.Count >= 4 && (n[0] & 0x1F) == 7);
        return sps.Count >= 4 ? $"{text}, profile {sps[1]} level {sps[3]}" : text;
    }

    /// <summary>An Annex B frame's SPS in hex, for app.log ("" without one).</summary>
    internal static string SpsHex(byte[] frame) => Nals(frame).FirstOrDefault(n => n.Count > 0 && (n[0] & 0x1F) == 7) is { Count: > 0 } sps ? Convert.ToHexString(sps) : "";

    /// <summary>Rebuilds one frame's RTP payloads (single NAL, STAP-A, FU-A) as Annex B; null when malformed.</summary>
    internal static byte[]? Build(IEnumerable<byte[]> payloads)
    {
        using var output = new MemoryStream();
        foreach (var payload in payloads)
        {
            if (payload.Length == 0) return null;
            var type = payload[0] & 0x1F;
            if (type is >= 1 and <= 23)
            {
                output.Write(StartCode);
                output.Write(payload);
            }
            else if (type == 24)
            {
                // STAP-A: [size u16][NAL] repeated after the indicator byte.
                var at = 1;
                while (at + 2 <= payload.Length)
                {
                    var size = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(at));
                    at += 2;
                    if (size == 0 || at + size > payload.Length) return null;
                    output.Write(StartCode);
                    output.Write(payload, at, size);
                    at += size;
                }
            }
            else if (type == 28)
            {
                if (payload.Length < 2) return null;
                if ((payload[1] & 0x80) != 0)
                {
                    output.Write(StartCode);
                    output.WriteByte((byte)((payload[0] & 0xE0) | (payload[1] & 0x1F)));
                }
                output.Write(payload, 2, payload.Length - 2);
            }
            else return null;
        }
        return output.ToArray();
    }

    /// <summary>
    /// One sender's video, a small jitter buffer: packets are held in order; a gap is asked for again
    /// (NACK) every 40 ms, up to 6 times or 300 ms; Discord resends lost packets on the RTX stream,
    /// which <see cref="Add"/> takes with their original sequence numbers. Only when a gap can't be
    /// filled are its frames given up, and then everything until a keyframe, which is asked for (PLI).
    /// </summary>
    internal sealed class H264Receiver(bool hevc = false)
    {
        /// <summary>The frames are H.265 (RFC 7798), not H.264: how payloads are rebuilt and keyframes known.</summary>
        internal bool Hevc => hevc;

        private const long NackEveryMs = 40, GiveUpMs = 300;
        private const int MaxNacks = 6, MaxHeld = 2000;

        private sealed class Held(uint timestamp, bool marker, byte[] payload)
        {
            public uint Timestamp = timestamp;
            public bool Marker = marker;
            public byte[] Payload = payload;
        }

        private readonly SortedDictionary<long, Held> _held = [];
        private readonly Dictionary<long, (long First, long Last, int Count)> _asked = [];
        private long _next = -1;      // the first sequence of the next frame to hand on; -1 until known
        private long _highest = -1;
        private bool _broken = true;   // until a keyframe has been seen
        private long _lastKeyRequest = -1_000_000;

        /// <summary>A whole frame is ready (Annex B), in order.</summary>
        internal event Action<uint, byte[]>? Frame;

        /// <summary>Lost packets to ask for again (their sequence numbers).</summary>
        internal event Action<IReadOnlyList<ushort>>? Nack;

        /// <summary>A keyframe is needed (frames were given up).</summary>
        internal event Action? KeyframeNeeded;

        private long Extend(ushort sequence)
        {
            if (_highest < 0) return sequence + 0x10000L * 16; // room to go back
            var candidate = (_highest & ~0xFFFFL) | sequence;
            if (candidate - _highest > 0x8000) candidate -= 0x10000;
            else if (_highest - candidate > 0x8000) candidate += 0x10000;
            return candidate;
        }

        /// <summary>Adds one packet (original or resent) at the given time in milliseconds.</summary>
        internal void Add(ushort sequence, uint timestamp, bool marker, ReadOnlySpan<byte> payload, long now)
        {
            var seq = Extend(sequence);
            if (_next >= 0 && seq < _next) return; // too late, or a duplicate
            _held.TryAdd(seq, new Held(timestamp, marker, payload.ToArray()));
            _asked.Remove(seq);
            if (seq > _highest) _highest = seq;
            if (_next < 0)
            {
                // Start after the first frame end seen; the frame in progress may be missing its start.
                if (marker) { _next = seq + 1; Trim(); }
                RequestKeyframe(now);
                return;
            }
            Drain(now);
            Poll(now);
            while (_held.Count > MaxHeld) _held.Remove(_held.Keys.First());
        }

        /// <summary>A frame handed on couldn't be used (it didn't decrypt): nothing more until a keyframe, which is asked for.</summary>
        internal void Lost(long now)
        {
            _broken = true;
            RequestKeyframe(now);
        }

        /// <summary>Called on a timer (and on every packet): asks for gaps again and gives up on old ones.</summary>
        internal void Poll(long now)
        {
            if (_next < 0) return;
            List<ushort>? ask = null;
            long? givingUp = null;
            for (var seq = _next; seq < _highest; seq++)
            {
                if (_held.ContainsKey(seq)) continue;
                if (!_asked.TryGetValue(seq, out var asked))
                {
                    _asked[seq] = (now, now, 1);
                    (ask ??= []).Add((ushort)seq);
                    continue;
                }
                if (now - asked.First > GiveUpMs || asked.Count >= MaxNacks && now - asked.Last > NackEveryMs) { givingUp = seq; break; }
                if (now - asked.Last >= NackEveryMs)
                {
                    _asked[seq] = (asked.First, now, asked.Count + 1);
                    (ask ??= []).Add((ushort)seq);
                }
            }
            if (ask is { Count: > 0 }) Nack?.Invoke(ask);
            if (givingUp is { } lost) GiveUp(lost, now);
        }

        private void GiveUp(long lost, long now)
        {
            // Skip to the first packet after the lost one that starts a frame (the one after a frame's end).
            long? start = null;
            foreach (var (seq, held) in _held)
            {
                if (seq <= lost || !held.Marker) continue;
                start = seq + 1;
                break;
            }
            if (start is null) return; // no frame end after the gap yet: wait
            foreach (var seq in _asked.Keys.Where(s => s < start).ToList()) _asked.Remove(seq);
            _next = start.Value;
            Trim();
            _broken = true;
            RequestKeyframe(now);
            Drain(now);
        }

        private void RequestKeyframe(long now)
        {
            if (now - _lastKeyRequest < 500) return;
            _lastKeyRequest = now;
            KeyframeNeeded?.Invoke();
        }

        private void Trim()
        {
            foreach (var seq in _held.Keys.Where(s => s < _next).ToList()) _held.Remove(seq);
        }

        // Hands on every whole frame at the head of the buffer. While waiting for a keyframe, each frame that isn't one
        // asks again (at most twice a second): a request that got lost would otherwise leave the picture frozen.
        private void Drain(long now)
        {
            while (true)
            {
                var payloads = new List<byte[]>();
                var seq = _next;
                Held? last = null;
                while (_held.TryGetValue(seq, out var held))
                {
                    payloads.Add(held.Payload);
                    last = held;
                    if (held.Marker) break;
                    seq++;
                }
                if (last is null || !last.Marker) return;
                for (var s = _next; s <= seq; s++) _held.Remove(s);
                _next = seq + 1;
                var frame = hevc ? H265Rtp.Build(payloads) : Build(payloads);
                if (frame is null) { Lost(now); continue; }
                if (_broken && !(hevc ? H265Rtp.IsKeyframe(frame) : IsKeyframe(frame))) { RequestKeyframe(now); continue; }
                _broken = false;
                Frame?.Invoke(last.Timestamp, frame);
            }
        }
    }

    /// <summary>
    /// This client's sent video packets, kept briefly so a lost one can be sent again on the RTX
    /// stream (RFC 4588: the original sequence number, then the original payload).
    /// </summary>
    internal sealed class SentHistory
    {
        private const int Size = 1024;
        private readonly (ushort Sequence, uint Timestamp, bool Marker, byte[]? Payload, long ResentAt)[] _ring = new (ushort, uint, bool, byte[]?, long)[Size];

        // What went out and what the server asked for since the last report (Interlocked: frames, RTCP and the timer
        // are on different threads), so a stream nobody can see shows in app.log where it stops.
        internal int Frames, Packets, Unsealed, Nacked, Resent, KeyframeAsks, Rtcp;
        private long _reportAt;

        /// <summary>The report line ten seconds after the first frame, then each minute; null when nothing is due or nothing was sent.</summary>
        internal string? Due(long now)
        {
            if (Volatile.Read(ref Frames) + Volatile.Read(ref Unsealed) == 0) return null;
            if (_reportAt == 0) { _reportAt = now + 10_000; return null; }
            if (now < _reportAt) return null;
            _reportAt = now + 60_000;
            return $"sent {Interlocked.Exchange(ref Frames, 0)} frames in {Interlocked.Exchange(ref Packets, 0)} packets, " +
                $"{Interlocked.Exchange(ref Unsealed, 0)} not sent (DAVE didn't encrypt them); the server asked for " +
                $"{Interlocked.Exchange(ref KeyframeAsks, 0)} key frames and {Interlocked.Exchange(ref Nacked, 0)} lost packets ({Interlocked.Exchange(ref Resent, 0)} resent), " +
                $"in {Interlocked.Exchange(ref Rtcp, 0)} RTCP packets.";
        }

        internal void Remember(ushort sequence, uint timestamp, bool marker, byte[] payload) => _ring[sequence % Size] = (sequence, timestamp, marker, payload, 0);

        /// <summary>
        /// The RTX payload for a lost packet, with its timestamp and marker; null when it's no longer kept, or was resent
        /// in the last 150 ms (about a round trip: the server asks again while the first resend is still on its way, and
        /// answering each ask doubled a lossy stream until it carried more resends than video).
        /// </summary>
        internal (byte[] Payload, uint Timestamp, bool Marker)? Resend(ushort sequence, long now)
        {
            ref var entry = ref _ring[sequence % Size];
            if (entry.Payload is null || entry.Sequence != sequence) return null;
            if (entry.ResentAt != 0 && now - entry.ResentAt < 150) return null;
            entry.ResentAt = now;
            var rtx = new byte[entry.Payload.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(rtx, sequence);
            entry.Payload.CopyTo(rtx, 2);
            return (rtx, entry.Timestamp, entry.Marker);
        }
    }
}

/// <summary>
/// Go Live's bit rate, steered as libwebrtc's sender steers it: never over what Discord's server says it takes (its REMB,
/// less a tenth for the sound, resends and headers), down by half the loss the server reports once it passes 10 %, up 8 %
/// a second while it stays under 2 %, between 0.5 Mbit/s and the plan's rate. Exo sent its plan's 12 Mbit/s into a 9 Mbit/s
/// REMB and lost a third of the stream. Fed and asked from the connection's receive thread only.
/// </summary>
internal sealed class StreamRate(int start, int most)
{
    private const int Least = 500_000;
    private long _remb = long.MaxValue, _decidedAt;
    private double _loss = -1, _rate = start;

    /// <summary>The rate the encoder was last asked for.</summary>
    internal int Rate { get; private set; } = start;

    internal void Remb(long bitsPerSecond) => _remb = bitsPerSecond;

    /// <summary>The share of the video the server's last receiver report lost (0 to 1).</summary>
    internal void Loss(double share) => _loss = share;

    /// <summary>The rate to ask the encoder for, at most once a second and only when it moved 5 % or more; else null.</summary>
    internal int? Next(long now)
    {
        if (_decidedAt != 0 && now - _decidedAt < 1000) return null;
        _decidedAt = now;
        if (_loss > 0.10) _rate *= 1 - 0.5 * _loss;
        else if (_loss is >= 0 and < 0.02) _rate *= 1.08;
        _loss = -1; // each report counts once: no report, no change
        _rate = Math.Clamp(Math.Min(_rate, _remb * 0.9), Least, Math.Max(Least, most));
        if (Math.Abs(_rate - Rate) < Rate * 0.05) return null;
        Rate = (int)_rate;
        return Rate;
    }
}

/// <summary>
/// RTCP for Discord video (RFC 4585 feedback): building NACKs and keyframe requests (PLI), and
/// reading the ones the server sends this client about its own video.
/// </summary>
internal static class Rtcp
{
    internal const byte RtpFeedback = 205, PayloadFeedback = 206;
    private const byte SenderReport = 200, ReceiverReport = 201;

    /// <summary>
    /// The share of a source's packets lost (percent) in a sender or receiver report's block for it, across a
    /// compound packet; null when none of it reports on that source.
    /// </summary>
    internal static double? LossFor(ReadOnlySpan<byte> packet, uint ssrc)
    {
        for (var at = 0; at + 8 <= packet.Length;)
        {
            var size = (BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]) + 1) * 4;
            if (at + size > packet.Length) return null;
            var type = packet[at + 1];
            if (type is SenderReport or ReceiverReport)
            {
                var block = at + (type == SenderReport ? 28 : 8);
                for (var k = 0; k < (packet[at] & 31) && block + 24 <= at + size; k++, block += 24)
                    if (BinaryPrimitives.ReadUInt32BigEndian(packet[block..]) == ssrc) return packet[block + 4] / 2.56;
            }
            at += size;
        }
        return null;
    }

    /// <summary>A generic NACK (RTPFB, FMT 1) for lost packets of a media SSRC.</summary>
    internal static byte[] Nack(uint sender, uint media, IReadOnlyList<ushort> lost)
    {
        var fci = new List<(ushort Pid, ushort Blp)>();
        foreach (var seq in lost.Order())
        {
            if (fci.Count > 0)
            {
                var (pid, blp) = fci[^1];
                var distance = (ushort)(seq - pid);
                if (distance is >= 1 and <= 16) { fci[^1] = (pid, (ushort)(blp | (1 << (distance - 1)))); continue; }
            }
            fci.Add((seq, 0));
        }
        var packet = new byte[12 + fci.Count * 4];
        packet[0] = 0x80 | 1;
        packet[1] = RtpFeedback;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(packet.Length / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), sender);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), media);
        for (var i = 0; i < fci.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12 + i * 4), fci[i].Pid);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14 + i * 4), fci[i].Blp);
        }
        return packet;
    }

    /// <summary>A compound packet's parts for app.log ("RR video lost 0/256 total 0 seq 1234, PSFB/15 REMB 2500 kbit/s").</summary>
    internal static string Describe(ReadOnlySpan<byte> packet, uint video)
    {
        var parts = new List<string>();
        for (var at = 0; at + 8 <= packet.Length;)
        {
            var size = (BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]) + 1) * 4;
            if (at + size > packet.Length) break;
            int type = packet[at + 1], count = packet[at] & 31;
            var part = type switch { SenderReport => "SR", ReceiverReport => "RR", RtpFeedback => $"RTPFB/{count}", PayloadFeedback => $"PSFB/{count}", _ => type.ToString() };
            if (type is SenderReport or ReceiverReport)
            {
                var blocks = new List<string>();
                var block = at + (type == SenderReport ? 28 : 8);
                for (var k = 0; k < count && block + 24 <= at + size; k++, block += 24)
                {
                    var source = BinaryPrimitives.ReadUInt32BigEndian(packet[block..]);
                    var total = packet[block + 5] << 16 | packet[block + 6] << 8 | packet[block + 7];
                    var highest = BinaryPrimitives.ReadUInt32BigEndian(packet[(block + 8)..]) & 0xFFFF;
                    blocks.Add($"{(source == video ? "video" : source.ToString())} lost {packet[block + 4]}/256 total {total} seq {highest}");
                }
                part += blocks.Count == 0 ? " (no blocks)" : " " + string.Join("; ", blocks);
            }
            else if (RembBits(packet.Slice(at, size)) is { } remb)
                part += $" REMB {remb / 1000} kbit/s";
            else if (size >= 12)
            {
                var media = BinaryPrimitives.ReadUInt32BigEndian(packet[(at + 8)..]);
                part += $" for {(media == video ? "video" : media.ToString())}";
            }
            parts.Add(part);
            at += size;
        }
        return string.Join(", ", parts);
    }

    /// <summary>The most a REMB in a compound packet lets this sender send, in bits per second; null without one.</summary>
    internal static long? RembBits(ReadOnlySpan<byte> packet)
    {
        for (var at = 0; at + 8 <= packet.Length;)
        {
            var size = (BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]) + 1) * 4;
            if (at + size > packet.Length) return null;
            if (packet[at + 1] == PayloadFeedback && (packet[at] & 31) == 15 && size >= 20 && packet.Slice(at + 12, 4).SequenceEqual("REMB"u8))
                return (long)((packet[at + 17] & 3) << 16 | packet[at + 18] << 8 | packet[at + 19]) << (packet[at + 17] >> 2);
            at += size;
        }
        return null;
    }

    /// <summary>A Picture Loss Indication (PSFB, FMT 1): send a keyframe.</summary>
    internal static byte[] Pli(uint sender, uint media)
    {
        var packet = new byte[12];
        packet[0] = 0x80 | 1;
        packet[1] = PayloadFeedback;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), sender);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), media);
        return packet;
    }

    /// <summary>
    /// REMB (draft-alvestrand-rmcat-remb): the most this receiver can take, in bits per second, for these media SSRCs.
    /// A libwebrtc sender and media server raise a stream's bitrate only as far as its receivers say they can take.
    /// </summary>
    internal static byte[] Remb(uint sender, long bitsPerSecond, IReadOnlyList<uint> media)
    {
        var exponent = 0;
        var mantissa = Math.Max(0, bitsPerSecond);
        while (mantissa > 0x3FFFF) { mantissa >>= 1; exponent++; }
        var packet = new byte[20 + media.Count * 4];
        packet[0] = 0x80 | 15;
        packet[1] = PayloadFeedback;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(packet.Length / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), sender);
        // Media source 0, then "REMB", the SSRC count, the exponent (6 bits) and mantissa (18 bits).
        "REMB"u8.CopyTo(packet.AsSpan(12));
        packet[16] = (byte)media.Count;
        packet[17] = (byte)(exponent << 2 | (int)(mantissa >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(18), (ushort)mantissa);
        for (var i = 0; i < media.Count; i++) BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(20 + i * 4), media[i]);
        return packet;
    }

    /// <summary>
    /// Transport-wide congestion control feedback (draft-holmer-rmcat-transport-wide-cc-extensions-01, RTPFB FMT 15): when
    /// each packet of a run of transport sequence numbers arrived (microseconds; null when it didn't), from which the
    /// sender's bandwidth estimate is made. Two-bit status chunks of seven; deltas in 250 us steps, one byte when 0-63.75 ms
    /// after the last, else two (signed).
    /// </summary>
    internal static byte[] TransportFeedback(uint sender, uint media, ushort baseSequence, byte count, IReadOnlyList<long?> arrivals)
    {
        var first = arrivals.FirstOrDefault(a => a is not null) ?? 0;
        // The reference time, a signed 24-bit count of 64 ms; deltas are from it, then from the packet before.
        var reference = first / 64_000;
        var last = reference * 64_000;
        var symbols = new List<int>();
        var deltas = new List<byte>();
        foreach (var arrival in arrivals)
        {
            if (arrival is not { } at) { symbols.Add(0); continue; }
            var delta = (at - last) / 250;
            last += delta * 250;
            if (delta is >= 0 and <= 255) { symbols.Add(1); deltas.Add((byte)delta); }
            else
            {
                symbols.Add(2);
                var large = (short)Math.Clamp(delta, short.MinValue, short.MaxValue);
                deltas.Add((byte)(large >> 8));
                deltas.Add((byte)large);
            }
        }
        var chunks = new List<byte>();
        for (var i = 0; i < symbols.Count; i += 7)
        {
            // 1 (status vector), 1 (two-bit symbols), then seven symbols.
            var chunk = 0xC000;
            for (var j = 0; j < 7 && i + j < symbols.Count; j++) chunk |= symbols[i + j] << (12 - j * 2);
            chunks.Add((byte)(chunk >> 8));
            chunks.Add((byte)chunk);
        }
        var length = 20 + chunks.Count + deltas.Count;
        var padded = (length + 3) & ~3;
        var packet = new byte[padded];
        packet[0] = (byte)(0x80 | 15 | (padded > length ? 0x20 : 0));
        packet[1] = RtpFeedback;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(padded / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), sender);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), media);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12), baseSequence);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14), (ushort)arrivals.Count);
        packet[16] = (byte)(reference >> 16);
        packet[17] = (byte)(reference >> 8);
        packet[18] = (byte)reference;
        packet[19] = count;
        chunks.CopyTo(packet, 20);
        deltas.CopyTo(packet, 20 + chunks.Count);
        if (padded > length) packet[^1] = (byte)(padded - length);
        return packet;
    }

    /// <summary>What a (compound) RTCP packet asks of a media SSRC: lost sequence numbers, and whether it wants a keyframe.</summary>
    internal static (List<ushort> Lost, bool Keyframe) Read(ReadOnlySpan<byte> packet, uint media)
    {
        var lost = new List<ushort>();
        var keyframe = false;
        while (packet.Length >= 4)
        {
            var length = (BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) + 1) * 4;
            if (length > packet.Length) break;
            var one = packet[..length];
            var format = one[0] & 0x1F;
            if (length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(one[8..]) == media)
            {
                if (one[1] == RtpFeedback && format == 1)
                    for (var at = 12; at + 4 <= length; at += 4)
                    {
                        var pid = BinaryPrimitives.ReadUInt16BigEndian(one[at..]);
                        var blp = BinaryPrimitives.ReadUInt16BigEndian(one[(at + 2)..]);
                        lost.Add(pid);
                        for (var bit = 0; bit < 16; bit++) if ((blp & (1 << bit)) != 0) lost.Add((ushort)(pid + bit + 1));
                    }
                else if (one[1] == PayloadFeedback && format is 1 or 4) keyframe = true;
            }
            // FIR (PSFB FMT 4) names the media SSRC in its FCI, not the header.
            if (one[1] == PayloadFeedback && format == 4 && length >= 20 && BinaryPrimitives.ReadUInt32BigEndian(one[12..]) == media) keyframe = true;
            packet = packet[length..];
        }
        return (lost, keyframe);
    }
}
