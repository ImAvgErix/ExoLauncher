using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// The call's UDP leg to Discord's voice server: IP discovery, then RTP packets of Opus frames under
/// Discord's transport encryption, "rtpsize" variants (the RTP header and extension preamble are the
/// additional data; a 32-bit counter is appended as the nonce): aead_aes256_gcm_rtpsize when offered,
/// else aead_xchacha20_poly1305_rtpsize. Received packets are checked and decrypted here and handed
/// on by SSRC; anything that fails to authenticate is dropped.
/// </summary>
internal sealed class VoiceTransport : IDisposable
{
    internal const string Aes = "aead_aes256_gcm_rtpsize", XChaCha = "aead_xchacha20_poly1305_rtpsize";
    private const int HeaderSize = 12, TagSize = 16, NonceSize = 4;

    private readonly UdpClient _udp;
    private readonly IPEndPoint _server;
    private readonly uint _ssrc;
    private readonly CancellationTokenSource _stop = new();
    private AesGcm? _aes;
    private byte[]? _chachaKey;
    private string _mode = Aes;
    private uint _nonce;
    private ushort _sequence;
    private uint _timestamp;

    /// <summary>One received RTP packet, transport-decrypted (its payload still DAVE-encrypted when the call is E2EE).</summary>
    /// <summary>One received RTP packet; Orientation is its urn:3gpp:video-orientation byte (C, F, R1 R0), or -1 when it carries none.</summary>
    internal readonly record struct Packet(uint Ssrc, int PayloadType, bool Marker, ushort Sequence, uint Timestamp, ReadOnlyMemory<byte> Payload, int Orientation = -1, int TransportSequence = -1);

    // The video-orientation extension's id: Discord's media servers use libwebrtc's own ids (playout-delay 5, which Exo
    // sends, video-orientation 13).
    private const int OrientationId = 13;

    /// <summary>The video-orientation byte in a one-byte-header extension block (RFC 8285), or -1.</summary>
    internal static int Orientation(ReadOnlySpan<byte> block)
    {
        for (var at = 0; at < block.Length;)
        {
            var head = block[at++];
            if (head == 0) continue; // padding
            int id = head >> 4, length = (head & 0x0F) + 1;
            if (id == 15 || at + length > block.Length) return -1;
            if (id == OrientationId && length == 1) return block[at];
            at += length;
        }
        return -1;
    }

    /// <summary>
    /// The transport-wide sequence number in a one-byte-header extension block, or -1: the only libwebrtc extension two
    /// bytes long (draft-holmer-rmcat-transport-wide-cc-extensions), whatever id the server gave it.
    /// </summary>
    internal static int TransportSequence(ReadOnlySpan<byte> block)
    {
        for (var at = 0; at < block.Length;)
        {
            var head = block[at++];
            if (head == 0) continue; // padding
            int id = head >> 4, length = (head & 0x0F) + 1;
            if (id == 15 || at + length > block.Length) return -1;
            if (length == 2) return BinaryPrimitives.ReadUInt16BigEndian(block[at..]);
            at += length;
        }
        return -1;
    }

    /// <summary>A decrypted RTP packet from an SSRC. Its payload is the receive buffer: read or copied before the handler returns.</summary>
    internal event Action<Packet>? Received;

    /// <summary>A decrypted RTCP packet (compound, header included): the server's NACKs and keyframe requests.</summary>
    internal event Action<byte[]>? RtcpReceived;

    /// <summary>
    /// Sends one RTCP packet (a NACK or PLI): as with RTP under rtpsize, its first 8 bytes (header and
    /// sender SSRC) are the additional data, the rest is encrypted, and the counter nonce follows the tag.
    /// </summary>
    internal void SendRtcp(ReadOnlySpan<byte> rtcp)
    {
        if (rtcp.Length < 8 || !Started) return;
        Span<byte> packet = stackalloc byte[rtcp.Length + TagSize + NonceSize];
        rtcp[..8].CopyTo(packet);
        var counter = Interlocked.Increment(ref _nonce) - 1; // atomic: sound, video and RTCP send from different threads, and a nonce must never repeat
        BinaryPrimitives.WriteUInt32BigEndian(packet[^NonceSize..], counter);
        Seal(counter, packet[..8], rtcp[8..], packet.Slice(8, rtcp.Length - 8), packet.Slice(rtcp.Length, TagSize));
        try { _udp.Client.Send(packet); }
        catch (SocketException ex) { AppLog.Debug("Discord video: RTCP send failed " + ex.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    internal VoiceTransport(string ip, int port, uint ssrc)
    {
        _server = new IPEndPoint(IPAddress.Parse(ip), port);
        _ssrc = ssrc;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.ReceiveBufferSize = 1 << 20;
        _udp.Connect(_server);
    }

    /// <summary>Asks the voice server for this PC's public address and port (Discord's IP discovery packet).</summary>
    internal async Task<(string Address, int Port)> DiscoverAsync(CancellationToken ct)
    {
        var request = new byte[74];
        BinaryPrimitives.WriteUInt16BigEndian(request, 1);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 70);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), _ssrc);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await _udp.SendAsync(request, ct).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var reply = (await _udp.ReceiveAsync(wait.Token).ConfigureAwait(false)).Buffer;
                if (reply.Length < 74 || BinaryPrimitives.ReadUInt16BigEndian(reply) != 2) continue;
                var address = Encoding.ASCII.GetString(reply, 8, 64).TrimEnd('\0');
                return (address, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(72)));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        throw new TimeoutException("Discord's voice server didn't answer.");
    }

    /// <summary>The session key from op 4 Session Description; starts receiving.</summary>
    internal void Start(string mode, byte[] key)
    {
        _mode = mode;
        if (mode == Aes) _aes = new AesGcm(key, TagSize);
        else _chachaKey = key;
        Started = true;
        _ = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>The session key is in: until then (a reconnect's new transport) nothing can be sealed, so nothing is sent.</summary>
    internal bool Started { get; private set; }

    /// <summary>Sends one 20 ms Opus frame (already DAVE-encrypted when the call is E2EE).</summary>
    internal void Send(ReadOnlySpan<byte> frame)
    {
        if (!Started) return;
        Span<byte> packet = stackalloc byte[HeaderSize + frame.Length + TagSize + NonceSize];
        packet[0] = 0x80;
        packet[1] = 0x78;
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], _sequence++);
        SyncTimestamp();
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], _timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], _ssrc);
        _timestamp += Opus.FrameSamples;
        var counter = Interlocked.Increment(ref _nonce) - 1; // atomic: sound, video and RTCP send from different threads, and a nonce must never repeat
        BinaryPrimitives.WriteUInt32BigEndian(packet[^NonceSize..], counter);
        Seal(counter, packet[..HeaderSize], frame, packet.Slice(HeaderSize, frame.Length), packet.Slice(HeaderSize + frame.Length, TagSize));
        try { _udp.Client.Send(packet); }
        catch (SocketException ex) { AppLog.Debug("Discord voice: send failed " + ex.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    // RTP time is the sound's own clock, as Discord's client keeps it: after a pause (silence, mute, a dropped late
    // packet) it jumps by the time that passed. Counted only per frame sent, a 10 s pause read as 20 ms, so the
    // listener's jitter buffer took the next words for packets 10 s late and grew its delay each time.
    private long _clockStart;
    private uint _timestampStart;

    private void SyncTimestamp()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_clockStart == 0) { _clockStart = now; _timestampStart = _timestamp; return; }
        var frames = (now - _clockStart) * Opus.SampleRate / System.Diagnostics.Stopwatch.Frequency / Opus.FrameSamples;
        var due = unchecked(_timestampStart + (uint)(frames * Opus.FrameSamples));
        // Within a frame of the clock, a talk spurt keeps its even 20 ms steps (send jitter never shifts them).
        if (unchecked((int)(due - _timestamp)) >= 2 * Opus.FrameSamples) _timestamp = due;
    }

    /// <summary>Sends one video RTP packet (its frame already DAVE-encrypted), on the call's own counter.</summary>
    internal void SendVideo(uint ssrc, int payloadType, bool marker, ushort sequence, uint timestamp, ReadOnlySpan<byte> payload)
    {
        // A one-byte header extension (RFC 8285) with playout-delay 0/0, as Discord's video senders use;
        // with rtpsize the preamble is additional data and the element is encrypted with the payload.
        if (!Started) return;
        Span<byte> extension = [0x50 | 2, 0, 0, 0];
        var body = extension.Length + payload.Length;
        Span<byte> packet = stackalloc byte[HeaderSize + 4 + body + TagSize + NonceSize];
        packet[0] = 0x90;
        packet[1] = (byte)(payloadType | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], ssrc);
        packet[12] = 0xBE; packet[13] = 0xDE;
        BinaryPrimitives.WriteUInt16BigEndian(packet[14..], 1);
        Span<byte> plain = stackalloc byte[body];
        extension.CopyTo(plain);
        payload.CopyTo(plain[extension.Length..]);
        var counter = Interlocked.Increment(ref _nonce) - 1; // atomic: sound, video and RTCP send from different threads, and a nonce must never repeat
        BinaryPrimitives.WriteUInt32BigEndian(packet[^NonceSize..], counter);
        Seal(counter, packet[..(HeaderSize + 4)], plain, packet.Slice(HeaderSize + 4, body), packet.Slice(HeaderSize + 4 + body, TagSize));
        try { _udp.Client.Send(packet); }
        catch (SocketException ex) { AppLog.Debug("Discord video: send failed " + ex.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    // One buffer for what arrives and one for what it decrypts to, for the whole call: a packet costs no allocation
    // (UdpClient.ReceiveAsync made a new array per datagram, and each payload was copied out again).
    private async Task ReceiveLoopAsync()
    {
        var packet = new byte[4096];
        var plain = new byte[4096];
        while (!_stop.IsCancellationRequested)
        {
            int length;
            try { length = await _udp.Client.ReceiveAsync(packet, SocketFlags.None, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            Handle(packet.AsSpan(0, length), plain);
        }
    }

    private void Handle(ReadOnlySpan<byte> packet, byte[] plain)
    {
        if (packet.Length < HeaderSize + TagSize + NonceSize || (packet[0] >> 6) != 2) return;
        if (packet[1] is >= 200 and <= 206)
        {
            // RTCP: the 8-byte header is additional data, the rest is encrypted.
            var bodyLength = packet.Length - 8 - TagSize - NonceSize;
            if (bodyLength < 0 || bodyLength > plain.Length) return;
            var rtcpCounter = BinaryPrimitives.ReadUInt32BigEndian(packet[^NonceSize..]);
            if (!Open(rtcpCounter, packet[..8], packet.Slice(8, bodyLength), packet.Slice(8 + bodyLength, TagSize), plain.AsSpan(0, bodyLength)))
                return;
            var rtcp = new byte[8 + bodyLength];
            packet[..8].CopyTo(rtcp);
            plain.AsSpan(0, bodyLength).CopyTo(rtcp.AsSpan(8));
            RtcpReceived?.Invoke(rtcp);
            return;
        }
        var type = packet[1] & 0x7F;
        // rtpsize: the header, CSRCs and the extension preamble are additional data; the extension body is encrypted.
        var aad = HeaderSize + (packet[0] & 0x0F) * 4;
        var extended = (packet[0] & 0x10) != 0;
        var extensionWords = 0;
        var oneByte = false;
        if (extended)
        {
            if (packet.Length < aad + 4) return;
            oneByte = BinaryPrimitives.ReadUInt16BigEndian(packet[aad..]) == 0xBEDE;
            extensionWords = BinaryPrimitives.ReadUInt16BigEndian(packet[(aad + 2)..]);
            aad += 4;
        }
        var cipherLength = packet.Length - aad - TagSize - NonceSize;
        if (cipherLength <= 0 || cipherLength > plain.Length) return;
        var counter = BinaryPrimitives.ReadUInt32BigEndian(packet[^NonceSize..]);
        if (!Open(counter, packet[..aad], packet.Slice(aad, cipherLength), packet.Slice(aad + cipherLength, TagSize), plain.AsSpan(0, cipherLength)))
            return;
        var skip = extensionWords * 4;
        if (skip >= cipherLength) return;
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        var length = cipherLength - skip;
        // Padding (P bit): its last byte says how many bytes of padding end the payload.
        if ((packet[0] & 0x20) != 0 && length > 0) length -= Math.Min(length, plain[skip + length - 1]);
        if (length <= 0) return;
        var orientation = oneByte ? Orientation(plain.AsSpan(0, skip)) : -1;
        var transport = oneByte ? TransportSequence(plain.AsSpan(0, skip)) : -1;
        Received?.Invoke(new Packet(ssrc, type, (packet[1] & 0x80) != 0, sequence, timestamp, plain.AsMemory(skip, length), orientation, transport));
    }

    private void Seal(uint counter, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plain, Span<byte> cipher, Span<byte> tag)
    {
        if (_aes is not null)
        {
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(nonce, counter);
            _aes.Encrypt(nonce, plain, cipher, tag, aad);
            return;
        }
        var (chacha, nonce12) = XChaChaFor(counter);
        using (chacha) chacha.Encrypt(nonce12, plain, cipher, tag, aad);
    }

    private bool Open(uint counter, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, Span<byte> plain)
    {
        try
        {
            if (_aes is not null)
            {
                Span<byte> nonce = stackalloc byte[12];
                BinaryPrimitives.WriteUInt32BigEndian(nonce, counter);
                _aes.Decrypt(nonce, cipher, tag, plain, aad);
                return true;
            }
            if (_chachaKey is null) return false;
            var (chacha, nonce12) = XChaChaFor(counter);
            using (chacha) chacha.Decrypt(nonce12, cipher, tag, plain, aad);
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    // XChaCha20-Poly1305 with a 24-byte nonce of the counter then zeros: HChaCha20 of the key and the
    // nonce's first 16 bytes gives the subkey; the IETF nonce is 4 zero bytes and the last 8 (zeros here).
    private (ChaCha20Poly1305, byte[]) XChaChaFor(uint counter)
    {
        Span<byte> nonce16 = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(nonce16, counter);
        return (new ChaCha20Poly1305(HChaCha20(_chachaKey!, nonce16)), new byte[12]);
    }

    internal static byte[] HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce16)
    {
        Span<uint> s = stackalloc uint[16];
        s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
        for (var i = 0; i < 8; i++) s[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        for (var i = 0; i < 4; i++) s[12 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce16[(i * 4)..]);
        for (var round = 0; round < 10; round++)
        {
            Quarter(s, 0, 4, 8, 12); Quarter(s, 1, 5, 9, 13); Quarter(s, 2, 6, 10, 14); Quarter(s, 3, 7, 11, 15);
            Quarter(s, 0, 5, 10, 15); Quarter(s, 1, 6, 11, 12); Quarter(s, 2, 7, 8, 13); Quarter(s, 3, 4, 9, 14);
        }
        var output = new byte[32];
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4), s[i]);
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16 + i * 4), s[12 + i]);
        return output;
    }

    private static void Quarter(Span<uint> s, int a, int b, int c, int d)
    {
        s[a] += s[b]; s[d] = uint.RotateLeft(s[d] ^ s[a], 16);
        s[c] += s[d]; s[b] = uint.RotateLeft(s[b] ^ s[c], 12);
        s[a] += s[b]; s[d] = uint.RotateLeft(s[d] ^ s[a], 8);
        s[c] += s[d]; s[b] = uint.RotateLeft(s[b] ^ s[c], 7);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        _aes?.Dispose();
        if (_chachaKey is not null) CryptographicOperations.ZeroMemory(_chachaKey);
    }
}
