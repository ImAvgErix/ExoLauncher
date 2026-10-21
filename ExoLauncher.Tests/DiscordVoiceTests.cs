using System.Net;
using System.Net.Sockets;
using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class DiscordVoiceTests
{
    [Fact]
    public void LossComesFromTheReportOnThisClientsVoice()
    {
        // A receiver report from the server (SSRC 7) with blocks on SSRC 9 (12.5 % lost) and on ours, 8671 (25 %).
        var packet = new byte[8 + 2 * 24];
        packet[0] = 0x80 | 2;
        packet[1] = 201;
        packet[3] = (byte)(packet.Length / 4 - 1);
        packet[7] = 7;
        packet[11] = 9;
        packet[12] = 32;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(32), 8671);
        packet[36] = 64;
        Assert.Equal(25, Rtcp.LossFor(packet, 8671));
        Assert.Equal(12.5, Rtcp.LossFor(packet, 9));
        Assert.Null(Rtcp.LossFor(packet, 1234));
        Assert.Null(Rtcp.LossFor(packet.AsSpan(0, 20), 8671));
    }

    [Fact]
    public void HChaCha20MatchesTheXChaChaDraftVector()
    {
        // draft-irtf-cfrg-xchacha-03, section 2.2.1.
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var nonce = Convert.FromHexString("000000090000004a0000000031415927");
        Assert.Equal("82413b4227b27bfed30e42508a877d73a0f9e4d58a74a853c12ec41326d3ecdc",
            Convert.ToHexString(VoiceTransport.HChaCha20(key, nonce)).ToLowerInvariant());
    }

    [Theory]
    [InlineData(VoiceTransport.Aes)]
    [InlineData(VoiceTransport.XChaCha)]
    public async Task PacketsRoundTripThroughTransportEncryption(string mode)
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var transport = new VoiceTransport("127.0.0.1", port, 1234);
        var received = new TaskCompletionSource<(uint, byte[])>();
        transport.Received += packet => received.TrySetResult((packet.Ssrc, packet.Payload.ToArray()));
        transport.Start(mode, Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray());

        var frame = Enumerable.Range(0, 120).Select(i => (byte)i).ToArray();
        transport.Send(frame);
        var packet = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(12 + frame.Length + 16 + 4, packet.Buffer.Length);
        Assert.False(packet.Buffer.AsSpan(12, frame.Length).SequenceEqual(frame)); // encrypted on the wire

        await server.SendAsync(packet.Buffer, packet.RemoteEndPoint);
        var (ssrc, payload) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1234u, ssrc);
        Assert.Equal(frame, payload);

        // A flipped bit fails authentication and is dropped.
        var tampered = (byte[])packet.Buffer.Clone();
        tampered[20] ^= 1;
        received = new TaskCompletionSource<(uint, byte[])>();
        await server.SendAsync(tampered, packet.RemoteEndPoint);
        await Assert.ThrowsAsync<TimeoutException>(() => received.Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task VideoPacketsCarryMarkerTypeAndTimestampAndLoseTheirExtension()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var transport = new VoiceTransport("127.0.0.1", port, 1234);
        var received = new TaskCompletionSource<VoiceTransport.Packet>();
        // The payload is the transport's own buffer, valid only in the handler.
        transport.Received += packet => received.TrySetResult(packet with { Payload = packet.Payload.ToArray() });
        transport.Start(VoiceTransport.Aes, Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray());

        var payload = Enumerable.Range(0, 900).Select(i => (byte)(i * 5)).ToArray();
        transport.SendVideo(5678, H264Rtp.PayloadType, marker: true, sequence: 42, timestamp: 90_000, payload);
        var wire = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendAsync(wire.Buffer, wire.RemoteEndPoint);
        var packet = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5678u, packet.Ssrc);
        Assert.Equal(H264Rtp.PayloadType, packet.PayloadType);
        Assert.True(packet.Marker);
        Assert.Equal((ushort)42, packet.Sequence);
        Assert.Equal(90_000u, packet.Timestamp);
        Assert.Equal(payload, packet.Payload.ToArray());
    }

    [Fact]
    public void TheVoiceGoesOutAsOneChannelAndPlaysOnBoth()
    {
        var encoder = Opus.NewEncoder();
        var decoder = Opus.NewDecoder();
        try
        {
            var pcm = new float[VoiceAudio.FrameFloats];
            for (var n = 0; n < Opus.FrameSamples; n++) pcm[2 * n] = pcm[2 * n + 1] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * n / Opus.SampleRate);
            var packet = new byte[1275];
            var length = Opus.Encode(encoder, pcm, packet);
            Assert.True(length > 0);
            Assert.Equal(0, packet[0] & 0x04); // the TOC's stereo flag
            var output = new float[VoiceAudio.FrameFloats];
            Assert.Equal(Opus.FrameSamples, Opus.Decode(decoder, packet.AsSpan(0, length), output));
        }
        finally { Opus.EncoderDestroy(encoder); Opus.DecoderDestroy(decoder); }
    }

    private static byte[] Frame(params byte[][] nals) => [.. nals.SelectMany(n => (byte[])[0, 0, 0, 1, .. n])];

    // A receiver with what it handed on, asked for, and whether it wanted a keyframe.
    private sealed class Watch
    {
        public readonly H264Rtp.H264Receiver Receiver = new();
        public readonly List<byte[]> Frames = [];
        public readonly List<ushort> Nacked = [];
        public int KeyRequests;
        public Watch()
        {
            Receiver.Frame += (_, frame) => Frames.Add(frame);
            Receiver.Nack += lost => Nacked.AddRange(lost);
            Receiver.KeyframeNeeded += () => KeyRequests++;
        }
    }

    // A frame's packets with consecutive sequence numbers from first.
    private static (ushort Seq, bool Marker, byte[] Payload)[] Packets(byte[] frame, ushort first)
    {
        var parts = H264Rtp.Packetize(frame);
        return [.. parts.Select((p, i) => ((ushort)(first + i), i == parts.Count - 1, p))];
    }

    private static byte[] Big(byte nal, int size) { var b = new byte[size]; b[0] = nal; for (var i = 1; i < size; i++) b[i] = (byte)(i % 200 + 2); return b; }

    [Fact]
    public void H264FramesSurvivePacketizingAndReassembly()
    {
        var sps = new byte[] { 0x67, 0x64, 0x00, 0x1f };
        var pps = new byte[] { 0x68, 0xee, 0x3c, 0x80 };
        var frame = Frame(sps, pps, Big(0x65, 5000));
        var packets = H264Rtp.Packetize(frame);
        Assert.True(packets.Count > 3); // the IDR slice is fragmented (FU-A)
        Assert.All(packets, p => Assert.True(p.Length <= 1100));

        var watch = new Watch();
        watch.Receiver.Add(65529, 1000, true, H264Rtp.Packetize(Frame(Big(0x41, 10)))[0], 0); // a frame end to start from
        foreach (var (seq, marker, payload) in Packets(frame, 65530)) watch.Receiver.Add(seq, 3000, marker, payload, 10);
        Assert.Single(watch.Frames);
        Assert.Equal(frame, watch.Frames[0]); // across the 16-bit sequence roll-over
        Assert.True(H264Rtp.IsKeyframe(watch.Frames[0]));
    }

    [Fact]
    public void H264StapAUnpacksIntoItsNalUnits()
    {
        byte[] sps = [0x67, 1, 2], pps = [0x68, 3];
        byte[] stap = [0x18, 0, (byte)sps.Length, .. sps, 0, (byte)pps.Length, .. pps];
        Assert.Equal(Frame(sps, pps, [0x65, 9, 9]), H264Rtp.Build([stap, [0x65, 9, 9]]));
    }

    [Fact]
    public void ALostPacketIsAskedForAndTheResentOneCompletesTheFrame()
    {
        var watch = new Watch();
        watch.Receiver.Add(9, 100, true, [0x41, 1], 0);
        var key = Packets(Frame(Big(0x65, 3000)), 10);
        Assert.True(key.Length >= 3);
        watch.Receiver.Add(key[0].Seq, 200, key[0].Marker, key[0].Payload, 10);
        for (var i = 2; i < key.Length; i++) watch.Receiver.Add(key[i].Seq, 200, key[i].Marker, key[i].Payload, 12);
        Assert.Empty(watch.Frames);
        Assert.Contains(key[1].Seq, watch.Nacked); // asked for at once
        watch.Receiver.Poll(60);
        Assert.Equal(2, watch.Nacked.Count(s => s == key[1].Seq)); // and again 40 ms later
        watch.Receiver.Add(key[1].Seq, 200, key[1].Marker, key[1].Payload, 80); // the RTX resend
        Assert.Single(watch.Frames);
        Assert.Equal(Frame(Big(0x65, 3000)), watch.Frames[0]);
    }

    [Fact]
    public void AGapThatNeverFillsIsGivenUpUntilAKeyframe()
    {
        var watch = new Watch();
        watch.Receiver.Add(9, 100, true, [0x41, 1], 0);
        var start = watch.KeyRequests;
        watch.Receiver.Add(10, 200, true, H264Rtp.Packetize(Frame([0x65, 2]))[0], 1); // a keyframe
        Assert.Single(watch.Frames);
        // Packet 11 never comes; 12 ends that frame, 13 is a delta, 14 a keyframe.
        watch.Receiver.Add(12, 300, true, [0x41, 3], 2);
        watch.Receiver.Add(13, 400, true, [0x41, 4], 3);
        watch.Receiver.Add(14, 500, true, H264Rtp.Packetize(Frame([0x65, 5]))[0], 4);
        Assert.Single(watch.Frames);
        watch.Receiver.Poll(600); // past the 300 ms wait (and the 500 ms between keyframe requests)
        Assert.True(watch.KeyRequests > start);
        Assert.Equal(2, watch.Frames.Count); // the delta after the gap is dropped, the keyframe shown
        Assert.True(H264Rtp.IsKeyframe(watch.Frames[1]));
    }

    [Fact]
    public void WaitingForAKeyframeAsksAgainUntilOneComes()
    {
        // Fails if a lost keyframe request leaves the picture frozen (asked once, never again), or if every frame
        // that can't be shown asks (a request storm that makes the sender spend its rate on keyframes).
        var watch = new Watch();
        watch.Receiver.Add(9, 100, true, [0x41, 1], 0); // the start: no keyframe yet, one asked for
        Assert.Equal(1, watch.KeyRequests);
        watch.Receiver.Add(10, 200, true, [0x41, 2], 100);
        watch.Receiver.Add(11, 300, true, [0x41, 3], 200);
        Assert.Equal(1, watch.KeyRequests); // within half a second: not again
        watch.Receiver.Add(12, 400, true, [0x41, 4], 700);
        Assert.Equal(2, watch.KeyRequests); // still none: asked again
        Assert.Empty(watch.Frames);
        watch.Receiver.Add(13, 500, true, H264Rtp.Packetize(Frame([0x65, 5]))[0], 800);
        Assert.Single(watch.Frames);
    }

    [Fact]
    public void AFrameThatFailsToDecryptHoldsTheRestUntilAKeyframe()
    {
        // Fails if the deltas after an unusable frame reach the decoder (a smeared picture) or no keyframe is asked for.
        var watch = new Watch();
        watch.Receiver.Add(9, 100, true, [0x41, 1], 0);
        watch.Receiver.Add(10, 200, true, H264Rtp.Packetize(Frame([0x65, 2]))[0], 1);
        Assert.Single(watch.Frames);
        var before = watch.KeyRequests;
        watch.Receiver.Lost(600);
        Assert.Equal(before + 1, watch.KeyRequests);
        watch.Receiver.Add(11, 300, true, [0x41, 3], 610);
        Assert.Single(watch.Frames);
        watch.Receiver.Add(12, 400, true, H264Rtp.Packetize(Frame([0x65, 4]))[0], 620);
        Assert.Equal(2, watch.Frames.Count);
    }

    [Theory]
    // Fails if a late or repeated packet plays again, a short loss isn't counted (no FEC or concealment), the 16-bit
    // roll-over reads as a loss, or a sender that starts counting from somewhere new is dropped for minutes.
    [InlineData(100, 101, 0)]
    [InlineData(100, 103, 2)]
    [InlineData(65535, 0, 0)]
    [InlineData(65534, 1, 2)]
    [InlineData(100, 100, -1)]
    [InlineData(100, 99, -1)]
    [InlineData(100, 51, -1)]
    [InlineData(100, 49, 0)]
    [InlineData(40000, 100, 25635)]
    [InlineData(1000, 500, 0)]
    public void ASpeakersLostPacketsAreCountedAndANewCountIsFollowed(int previous, int sequence, int missing) =>
        Assert.Equal(missing, VoiceConnection.Missing((ushort)previous, (ushort)sequence));

    [Fact]
    public void NacksAndKeyframeRequestsRoundTrip()
    {
        var nack = Rtcp.Nack(1, 77, [100, 101, 105, 140]);
        var (lost, keyframe) = Rtcp.Read(nack, 77);
        Assert.Equal(new ushort[] { 100, 101, 105, 140 }, lost.Order());
        Assert.False(keyframe);
        Assert.Empty(Rtcp.Read(nack, 78).Lost); // another stream's
        Assert.True(Rtcp.Read([.. Rtcp.Nack(1, 77, [5]), .. Rtcp.Pli(1, 77)], 77).Keyframe); // compound
    }

    [Fact]
    public void ALostSentPacketCanBeResentWithItsSequenceNumber()
    {
        var sent = new H264Rtp.SentHistory();
        sent.Remember(500, 9000, true, [1, 2, 3]);
        var resend = sent.Resend(500);
        Assert.NotNull(resend);
        Assert.Equal(new byte[] { 0x01, 0xF4, 1, 2, 3 }, resend.Value.Payload);
        Assert.True(resend.Value.Marker);
        Assert.Null(sent.Resend(501));
    }

    [Fact]
    public async Task RtcpRoundTripsThroughTransportEncryption()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var transport = new VoiceTransport("127.0.0.1", port, 1234);
        var received = new TaskCompletionSource<byte[]>();
        transport.RtcpReceived += packet => received.TrySetResult(packet);
        transport.Start(VoiceTransport.Aes, Enumerable.Range(0, 32).Select(i => (byte)(i * 11)).ToArray());
        var pli = Rtcp.Pli(1234, 99);
        transport.SendRtcp(pli);
        var wire = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(pli.Length + 16 + 4, wire.Buffer.Length);
        await server.SendAsync(wire.Buffer, wire.RemoteEndPoint);
        Assert.Equal(pli, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void OpusRoundTripsATone()
    {
        var encoder = Opus.NewEncoder();
        var decoder = Opus.NewDecoder();
        try
        {
            var pcm = new float[Opus.FrameSamples * 2];
            var output = new float[pcm.Length];
            var data = new byte[1275];
            double energy = 0;
            for (var frame = 0; frame < 5; frame++)
            {
                for (var i = 0; i < Opus.FrameSamples; i++)
                    pcm[i * 2] = pcm[i * 2 + 1] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * (frame * Opus.FrameSamples + i) / Opus.SampleRate);
                var length = Opus.Encode(encoder, pcm, data);
                Assert.True(length > 0);
                var samples = Opus.Decode(decoder, data.AsSpan(0, length), output);
                Assert.Equal(Opus.FrameSamples, samples);
                energy = output.Sum(s => (double)s * s) / output.Length;
            }
            Assert.True(energy > 0.01, $"decoded tone is too quiet: {energy}");
        }
        finally
        {
            Opus.EncoderDestroy(encoder);
            Opus.DecoderDestroy(decoder);
        }
    }

    [Fact]
    public void DaveSendsAKeyPackageForAnE2eeCallAndPassesThroughOtherwise()
    {
        Assert.True(DaveSession.MaxVersion >= 1);
        var sent = new List<(byte Op, byte[] Payload)>();
        using (var plain = new DaveSession("100000000000000001", 400000000000000001, (op, p) => sent.Add((op, p)), (_, _) => { }))
        {
            plain.AssignSsrc(5);
            plain.OnSessionDescription(0);
            var frame = new byte[] { 1, 2, 3, 4, 5 };
            var output = new byte[64];
            var length = plain.EncryptFrame(5, frame, output);
            Assert.Equal(frame, output[..length]);
        }
        Assert.Empty(sent);

        using var e2ee = new DaveSession("100000000000000001", 400000000000000001, (op, p) => sent.Add((op, p)), (_, _) => { });
        e2ee.AssignSsrc(5);
        e2ee.OnSessionDescription(1);
        var (opcode, keyPackage) = Assert.Single(sent);
        Assert.Equal(26, opcode);
        Assert.True(keyPackage.Length > 64);
    }
}

// The tests that run DeepFilterNet at real time run one at a time: its worker threads, run side by side, fell behind
// and gave back the sound late, which failed whichever was measuring.
[CollectionDefinition("DeepFilterNet", DisableParallelization = true)]
public sealed class DeepFilterNetCollection;

// Ways the live view could lie: it says DeepFilterNet ran when the plugin didn't load or the mic's own stood in;
// it says tuned when no tuning shaped Voice Focus; it names Exo's gain control while Windows' is on; noise alone
// still opens the gate it says is holding back.
[Collection("DeepFilterNet")]
public sealed class VoiceChainTests
{
    private static readonly VoiceTuning Tuned = new(15, 2, new float[VoiceAnalysis.Bands.Length]);

    private static float[] Noise(Random random, float scale)
    {
        var frame = new float[VoiceAudio.FrameFloats];
        for (var i = 0; i < frame.Length; i += 2) frame[i] = frame[i + 1] = (float)(random.NextDouble() * 2 - 1) * scale;
        return frame;
    }

    [Fact]
    public void ExoVoiceSaysWhatRanAndSendsNoNoise()
    {
        NoiseSuppressorTests.UseTestPlugin();
        using var chain = new VoiceChain { StepsAside = false }; // no tuning: calls run noise suppression at full strength
        chain.Prepare(VoiceProcessing.ExoVoice);
        var random = new Random(3);
        var sent = 0;
        for (var f = 0; f < 150; f++) if (chain.Process(Noise(random, 0.01f), VoiceProcessing.ExoVoice, null, systemGain: false) && f >= 50) sent++;
        // DeepFilterNet ran: whole, or its light stage when the CPU was too busy for it (Overload).
        Assert.Equal(new VoiceStages(chain.Stages.Suppression == "light" ? "light" : "on", NoiseSuppressor.GentleDb, false, "off", false), chain.Stages);
        Assert.Equal(0, sent);
        chain.Process(Noise(random, 0.01f), VoiceProcessing.ExoVoice, null, systemGain: true);
        Assert.Equal("off", chain.Stages.Gain); // Exo Voice rides no gain, Windows' or its own
    }

    [Fact]
    public void TheMicsOwnSuppressionAndCustomAreSaidAsTheyAre()
    {
        using var mic = new VoiceChain { Tuning = Tuned, StepsAside = true };
        mic.Process(new float[VoiceAudio.FrameFloats], VoiceProcessing.ExoVoice, null, systemGain: false);
        Assert.Equal("mic", mic.Stages.Suppression);
        using var custom = new VoiceChain { StepsAside = false };
        var plain = new VoiceProcessing("custom", false, false, false, false, true, -60, 0, false);
        custom.Process(new float[VoiceAudio.FrameFloats], plain, null, systemGain: false);
        Assert.Equal(new VoiceStages("off", NoiseSuppressor.GentleDb, false, "off", false), custom.Stages);
    }

    // Fan and room noise (pink and brown, as a PC's fans make), which DeepFilterNet keeps only about 5 dB down until it has
    // heard a voice, opened the automatic gate on nearly every frame. Through the real plugin, fed at real time as a call
    // feeds it: four seconds of a fan never open it, and a voice 15 dB over it opens it with its first syllable (one only
    // 10 dB over the fan opens it a few frames in, as its louder part comes).
    [Fact]
    public void FanNoiseNeverOpensTheAutomaticGateAndAVoiceOverItDoes()
    {
        NoiseSuppressorTests.UseTestPlugin();
        using var chain = new VoiceChain { StepsAside = false };
        chain.Prepare(VoiceProcessing.ExoVoice);
        const int fanFrames = 200, voiceFrames = 50;
        var fan = Fan((fanFrames + voiceFrames) * Opus.FrameSamples, -50, seed: 1);
        var voice = Vowels(voiceFrames * Opus.FrameSamples, -30);
        var frame = new float[VoiceAudio.FrameFloats];
        var opened = new bool[fanFrames + voiceFrames];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var f = 0; f < opened.Length; f++)
        {
            var due = TimeSpan.FromSeconds((double)f * Opus.FrameSamples / Opus.SampleRate) - clock.Elapsed;
            if (due > TimeSpan.Zero) Thread.Sleep(due);
            for (var i = 0; i < Opus.FrameSamples; i++)
            {
                var n = f * Opus.FrameSamples + i;
                frame[2 * i] = frame[2 * i + 1] = fan[n] + (f >= fanFrames ? voice[n - fanFrames * Opus.FrameSamples] : 0);
            }
            opened[f] = chain.Process(frame, VoiceProcessing.ExoVoice, null, systemGain: false);
        }
        Assert.Contains(chain.Stages.Suppression, new[] { "on", "light" });
        Assert.Equal(0, opened.Take(fanFrames).Count(o => o));
        // Suppression gives the voice back 20 to 30 ms after it reaches the mic: open on the frame that carries its start,
        // and through the voice.
        var first = Array.IndexOf(opened, true, fanFrames) - fanFrames;
        Assert.True(first is >= 0 and <= 2, $"opened {first} frames into the voice");
        Assert.True(opened.Skip(fanFrames).Count(o => o) >= voiceFrames * 9 / 10, $"{opened.Skip(fanFrames).Count(o => o)} of {voiceFrames} voice frames sent");
    }

    // A fan's sound: pink and brown noise (Paul Kellet's pink filter, a leaky integrator for brown), wavering 15% at 0.7 Hz.
    private static float[] Fan(int samples, double db, int seed)
    {
        var random = new Random(seed);
        var sound = new float[samples];
        double b0 = 0, b1 = 0, b2 = 0, brown = 0, power = 0;
        for (var n = 0; n < samples; n++)
        {
            var white = random.NextDouble() * 2 - 1;
            b0 = 0.99765 * b0 + white * 0.0990460;
            b1 = 0.96300 * b1 + white * 0.2965164;
            b2 = 0.57000 * b2 + white * 1.0526913;
            brown = 0.995 * brown + white * 0.1;
            var x = (0.6 * (b0 + b1 + b2 + white * 0.1848) + 0.6 * brown) * (1 + 0.15 * Math.Sin(2 * Math.PI * 0.7 * n / Opus.SampleRate));
            sound[n] = (float)x;
            power += x * x;
        }
        var gain = Math.Pow(10, db / 20) / Math.Sqrt(power / samples);
        for (var n = 0; n < samples; n++) sound[n] = (float)(sound[n] * gain);
        return sound;
    }

    // A voice DeepFilterNet keeps (a pulse train reads as noise to it): Rosenberg glottal pulses at 120 Hz with vibrato and
    // jitter through three formants, a vowel per 250 ms syllable (/a/, /i/, /o/, /e/), each starting over 20 ms and
    // ending over 40, as syllables do.
    private static float[] Vowels(int samples, double db)
    {
        var random = new Random(1);
        var sound = new float[samples];
        (double F1, double F2, double F3)[] vowels = [(700, 1220, 2600), (300, 2300, 3000), (500, 900, 2400), (400, 1900, 2600)];
        double[] widths = [80, 90, 120];
        var state = new double[6];
        double phase = 0, power = 0, last = 0;
        const int syllable = Opus.SampleRate / 4;
        for (var n = 0; n < samples; n++)
        {
            var vowel = vowels[n / syllable % vowels.Length];
            phase += (120 + 15 * Math.Sin(2 * Math.PI * n / (0.9 * Opus.SampleRate)) + 2 * (random.NextDouble() - 0.5)) / Opus.SampleRate;
            if (phase >= 1) phase -= 1;
            var x = (phase < 0.4 ? 0.5 * (1 - Math.Cos(Math.PI * phase / 0.4)) : phase < 0.56 ? Math.Cos(Math.PI * (phase - 0.4) / 0.32) : 0) + 0.02 * (random.NextDouble() - 0.5);
            double[] formants = [vowel.F1, vowel.F2, vowel.F3];
            for (var k = 0; k < 3; k++)
            {
                var r = Math.Exp(-Math.PI * widths[k] / Opus.SampleRate);
                var y = (1 - r) * x + 2 * r * Math.Cos(2 * Math.PI * formants[k] / Opus.SampleRate) * state[2 * k] - r * r * state[2 * k + 1];
                (state[2 * k + 1], state[2 * k]) = (state[2 * k], y);
                x = 3 * y;
            }
            var at = n % syllable;
            var voiced = x * Math.Min(1, at / (0.02 * Opus.SampleRate)) * Math.Min(1, (syllable - at) / (0.04 * Opus.SampleRate));
            sound[n] = (float)(voiced - 0.97 * last); // the lips' radiation: a gentle rise toward the highs
            last = voiced;
            power += sound[n] * (double)sound[n];
        }
        var gain = Math.Pow(10, db / 20) / Math.Sqrt(power / samples);
        for (var n = 0; n < samples; n++) sound[n] = (float)(sound[n] * gain);
        return sound;
    }

    // Cutting the microphone into 20 ms frames could lose or repeat samples where a frame ends mid-packet, stamp a
    // frame with its packet's start instead of its own first sample, or let silence or Input Volume through wrong.
    [Fact]
    public void MicrophonePacketsAreCutIntoWholeFramesStampedAtTheirFirstSample()
    {
        var frame = new float[VoiceAudio.FrameFloats];
        var filled = 0;
        var next = 0f;
        List<float> cut = [];
        List<int> starts = [];
        foreach (var samples in new[] { 480, 480, 720, 480, 240 }) // per channel: 10, 10, 15, 10 and 5 ms
        {
            var source = Enumerable.Range(0, samples * Opus.Channels).Select(_ => next++).ToArray();
            for (var used = 0; used < source.Length;)
            {
                if (filled == 0) starts.Add(used);
                used += VoiceAudio.Fill(source.AsSpan(used), silent: false, gain: 1f, frame, ref filled);
                if (filled < frame.Length) break;
                cut.AddRange(frame);
                filled = 0;
            }
        }
        Assert.Equal(Enumerable.Range(0, 2 * VoiceAudio.FrameFloats).Select(i => (float)i), cut);
        Assert.Equal(VoiceAudio.FrameFloats / 2, filled); // the third frame half cut, waiting for the next packet
        Assert.Equal(new[] { 0, 0, 480 }, starts); // the third starts 5 ms into the 10 ms packet
        Assert.Equal(50_000L, VoiceAudio.Offset(480));
        Assert.Equal(0L, VoiceAudio.Offset(0));
    }

    [Fact]
    public void SilentPacketsCutAsZerosAndInputVolumeScales()
    {
        var frame = Enumerable.Repeat(9f, VoiceAudio.FrameFloats).ToArray();
        var filled = 0;
        float[] loud = [0.5f, -0.25f, 1f, -1f];
        Assert.Equal(4, VoiceAudio.Fill(loud, silent: true, gain: 1f, frame, ref filled));
        Assert.Equal(new float[4], frame[..4]);
        Assert.Equal(4, VoiceAudio.Fill(loud, silent: false, gain: 1.5f, frame, ref filled));
        Assert.Equal(new[] { 0.75f, -0.375f, 1.5f, -1.5f }, frame[4..8]);
        Assert.Equal(9f, frame[8]); // past what was cut, untouched
        Assert.Equal(8, filled);
    }

    // The call's CPU line in app.log could misread a thread's time (a wrong import reads 0), or show a nonsense
    // share when the speakers reopened mid-minute and their thread's count started again.
    [Fact]
    public void TheCallsCpuIsReadAsAShareOfOneCore()
    {
        var before = VoiceAudio.ThreadCpu();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double spin = 0;
        while (watch.ElapsedMilliseconds < 100) spin += Math.Sqrt(spin + 1);
        Assert.True(VoiceAudio.ThreadCpu() > before, $"no thread time counted ({spin})");
        Assert.Equal(47.0, VoiceConnection.CoreShare(282_000_000, 600_000_000), 6);
        Assert.Equal(0d, VoiceConnection.CoreShare(-5, 600_000_000));
        Assert.Equal(0d, VoiceConnection.CoreShare(5, 0));
    }
}

[Collection("DeepFilterNet")]
public sealed class NoiseSuppressorTests
{
    // The plugin voice\build.ps1 fetched for the tests (the app fetches the same pinned file at run time).
    internal static void UseTestPlugin()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "voice"))) root = root.Parent;
        NoiseSuppressor.PluginPath = Path.Combine(root!.FullName, "voice", "build", "dfn", "exo-deepfilter.dll");
    }

    [Fact]
    public void DeepFilterNetTakesNoiseDownAndKeepsUpInRealTime()
    {
        UseTestPlugin();
        using var suppressor = NoiseSuppressor.Create();
        Assert.NotNull(suppressor);
        var random = new Random(7);
        double input = 0, output = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int frames = 150; // 3 s
        for (var f = 0; f < frames; f++)
        {
            var frame = new float[VoiceAudio.FrameFloats];
            for (var i = 0; i < frame.Length; i += 2) frame[i] = frame[i + 1] = (float)(random.NextDouble() * 2 - 1) * 0.1f;
            var energy = frame.Sum(s => (double)s * s);
            suppressor!.Process(frame);
            if (f >= frames / 2) { input += energy; output += frame.Sum(s => (double)s * s); }
        }
        var perFrameMs = watch.Elapsed.TotalMilliseconds / frames;
        var reductionDb = 10 * Math.Log10(input / Math.Max(output, 1e-12));
        Assert.True(reductionDb > 10, $"white noise only {reductionDb:F1} dB quieter");
        Assert.True(perFrameMs < 20, $"{perFrameMs:F2} ms per 20 ms frame");
    }

    // The plugin's delay, which a call's voice carries on top of its own: found from the sound by brute force over every
    // lag up to 50 ms, it is the low-latency model's 10 ms of STFT and the plugin's buffer, 10 ms at the start (a model
    // with lookahead, or a plugin that lagged, a warm-up that didn't take, would be more), and the suppressor measures
    // the same itself.
    [Fact]
    public void DeepFilterNetsDelayIsTwentyMillisecondsAndMeasuredSo()
    {
        UseTestPlugin();
        using var suppressor = NoiseSuppressor.Create();
        Assert.NotNull(suppressor);
        const int frames = 150, rate = 48000;
        var random = new Random(5);
        var input = new float[frames * Opus.FrameSamples];
        var output = new float[input.Length];
        double phase = 0, y0 = 0, y1 = 0, z0 = 0, z1 = 0;
        double r = Math.Exp(-Math.PI * 100 / rate), q = Math.Exp(-Math.PI * 150 / rate);
        for (var n = 0; n < input.Length; n++)
        {
            // A voice (pulses gliding 95-140 Hz through two formants, in syllables) over light noise.
            phase += (117.5 + 22.5 * Math.Sin(2 * Math.PI * n / (0.4 * rate))) / rate;
            var pulse = phase >= 1 ? 1.0 : 0;
            if (phase >= 1) phase -= 1;
            var a = pulse + 2 * r * Math.Cos(2 * Math.PI * 650 / rate) * y0 - r * r * y1;
            (y1, y0) = (y0, a);
            var b = a * (1 - r) + 2 * q * Math.Cos(2 * Math.PI * 1700 / rate) * z0 - q * q * z1;
            (z1, z0) = (z0, b);
            var syllable = Math.Sin(Math.PI * (n % (rate / 4)) / (rate / 4));
            input[n] = (float)(3 * b * (1 - q) * syllable * syllable) + 0.002f * (float)(random.NextDouble() - 0.5);
        }
        var frame = new float[VoiceAudio.FrameFloats];
        // Fed at real time, as a call feeds it: in a burst (and with the rest of the suite on the CPU) the plugin's worker
        // fell behind, held more or returned the dry input, and the measured delay drifted out of range.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var f = 0; f < frames; f++)
        {
            var due = TimeSpan.FromSeconds((double)f * Opus.FrameSamples / rate) - clock.Elapsed;
            if (due > TimeSpan.Zero) Thread.Sleep(due);
            for (var i = 0; i < Opus.FrameSamples; i++) frame[2 * i] = frame[2 * i + 1] = input[f * Opus.FrameSamples + i];
            suppressor!.Process(frame, 24); // a moderate strength: at full strength the synthetic voice (pulses) reads as noise
            for (var i = 0; i < Opus.FrameSamples; i++) output[f * Opus.FrameSamples + i] = frame[2 * i];
        }
        var from = 50 * Opus.FrameSamples;
        var best = Enumerable.Range(0, 2401).MaxBy(lag =>
        {
            double xy = 0, xx = 0, yy = 0;
            for (var n = from; n < output.Length; n++) { xy += output[n] * input[n - lag]; xx += input[n - lag] * input[n - lag]; yy += output[n] * output[n]; }
            return xy / Math.Sqrt(xx * yy + 1e-30);
        });
        // The plugin holds one or two 10 ms hops in real time and more when its worker falls behind (a busy PC, or frames fed
        // faster than real time): whatever it holds, it is within MostHops, and the tracker says a whole number of hops.
        Assert.InRange(best, 480 - 24, DelayTracker.MostHops * 480 + 24);
        Assert.InRange(suppressor!.DelaySamples / 480, 1, DelayTracker.MostHops);
        Assert.Equal(0, suppressor.DelaySamples % 480);
    }

    // The gate compares each cleaned frame with the microphone's sound it came from: reading the frame going in instead
    // (suppression's delay ahead of it) held a word's first frame shut.
    [Fact]
    public void TheInputLevelLinesUpWithTheOutputItCameFrom()
    {
        var tracker = new DelayTracker();
        var loud = Enumerable.Repeat(0.1f, Opus.FrameSamples).ToArray(); // -20 dBFS
        var quiet = new float[Opus.FrameSamples];
        tracker.Add(loud, quiet);
        Assert.Null(tracker.InputDb); // nothing went in before it: not silence (the gate would learn the room as silence)
        tracker.Add(quiet, quiet);
        Assert.Equal(-20f, tracker.InputDb!.Value, 0.1f); // the starting delay, two 10 ms hops: the frame before
        tracker.Add(quiet, quiet);
        Assert.Equal(-100f, tracker.InputDb);
        tracker.Reset();
        tracker.Add(loud, quiet);
        Assert.Null(tracker.InputDb); // a new user: the last one's sound isn't theirs
    }

    // The plugin's worker thread never ends, so a suppressor let go of could leak one per call or check, be handed to
    // two users at once (let go of twice), or carry its last user's sound into the next one's frames.
    [Fact]
    public void ASuppressorLetGoOfIsReusedOnceAndCarriesNoSoundOver()
    {
        UseTestPlugin();
        var first = NoiseSuppressor.Create();
        Assert.NotNull(first);
        var random = new Random(3);
        var loud = new float[VoiceAudio.FrameFloats];
        for (var i = 0; i < loud.Length; i++) loud[i] = (float)(random.NextDouble() * 2 - 1) * 0.5f;
        first!.Process(loud);
        first.Dispose();
        first.Dispose();
        using var second = NoiseSuppressor.Create();
        using var third = NoiseSuppressor.Create();
        Assert.Same(first, second);
        Assert.NotSame(second, third);
        var silence = new float[VoiceAudio.FrameFloats];
        second!.Process(silence);
        Assert.All(silence, s => Assert.True(MathF.Abs(s) < 1e-4f, $"{s} left over from the last user"));
    }

    // The light path is DeepFilterNet's first stage alone (its deep filtering stage's threshold at the lowest the plugin
    // takes): it must still take noise down, not pass the microphone through or stop the plugin.
    [Fact]
    public void LightDeepFilterNetStillTakesNoiseDown()
    {
        UseTestPlugin();
        using var suppressor = NoiseSuppressor.Create();
        Assert.NotNull(suppressor);
        OverloadTests.GoLight(suppressor!.Load);
        Assert.True(suppressor.Load.Light);
        var random = new Random(11);
        double input = 0, output = 0;
        for (var f = 0; f < 150; f++)
        {
            var frame = new float[VoiceAudio.FrameFloats];
            for (var i = 0; i < frame.Length; i += 2) frame[i] = frame[i + 1] = (float)(random.NextDouble() * 2 - 1) * 0.1f;
            var energy = frame.Sum(s => (double)s * s);
            suppressor.Process(frame);
            if (f >= 75) { input += energy; output += frame.Sum(s => (double)s * s); }
        }
        Assert.True(suppressor.Load.Light);
        Assert.True(10 * Math.Log10(input / Math.Max(output, 1e-12)) > 10, "light suppression left white noise up");
    }
}

// Ways Noise Suppression for Others could fail: their voice comes out uncleaned, out of order, late or not at all; the end of
// a sentence waits in the model until their next word; a frame is played twice or after they left.
[Collection("DeepFilterNet")]
public sealed class InboundCleanerTests
{
    [Fact]
    public void AVoiceIsCleanedInOrderAndItsEndIsPlayed()
    {
        NoiseSuppressorTests.UseTestPlugin();
        var played = new System.Collections.Concurrent.ConcurrentQueue<(float[] Frame, float Gain)>();
        var cleaner = new InboundCleaner((frame, gain) => played.Enqueue((frame, gain)), () => new float[VoiceAudio.FrameFloats]);
        var random = new Random(9);
        const int frames = 150; // 3 s, fed at real time as packets come
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double input = 0;
        for (var f = 0; f < frames; f++)
        {
            var due = TimeSpan.FromMilliseconds(20.0 * f) - clock.Elapsed;
            if (due > TimeSpan.Zero) Thread.Sleep(due);
            var frame = new float[VoiceAudio.FrameFloats];
            for (var i = 0; i < frame.Length; i += 2) frame[i] = frame[i + 1] = (float)(random.NextDouble() * 2 - 1) * 0.1f;
            frame[0] = f; // its place, read back below (one sample, inaudible to the measure)
            if (f >= frames - 50) input += frame.Skip(2).Sum(v => (double)v * v);
            cleaner.Play(frame, 0.5f);
        }
        cleaner.Drain();
        Thread.Sleep(300);
        cleaner.Dispose();
        var heard = played.ToArray();
        Assert.Equal(frames + 1, heard.Length); // every frame once, and the drain's
        Assert.All(heard.Take(frames), h => Assert.Equal(0.5f, h.Gain));
        var output = heard.Skip(frames - 50).Take(50).Sum(h => h.Frame.Skip(2).Sum(v => (double)v * v));
        Assert.True(10 * Math.Log10(input / Math.Max(output, 1e-12)) > 10, "their noise wasn't taken down");
        Assert.Equal(1, played.Count(h => ReferenceEquals(h.Frame, heard[^1].Frame)));
        Thread.Sleep(100);
        Assert.Equal(frames + 1, played.Count); // nothing after it was let go of
    }
}

// Ways the overload swap could fail: one stall (a garbage collection, a busy moment) sends it light, as a late-run rule
// did at rest; a CPU that stays busy never does; it swaps back while still busy, and so back and forth; it never comes
// back once the CPU is free; a busy PC flips it every 20 s.
public sealed class OverloadTests
{
    internal static void GoLight(Overload load)
    {
        for (var i = 0; i < Overload.SlowRuns; i++) load.Add(2 * Overload.SlowMs);
    }

    [Fact]
    public void SustainedSlowRunsGoLightAndStallsDoNot()
    {
        var load = new Overload();
        for (var i = 0; i < 3000; i++) load.Add(i % 50 == 0 ? 150 : i % 7 == 0 ? 12 : 5); // a 150 ms stall a second, some slow runs
        Assert.False(load.Light);
        for (var i = 0; i < Overload.SlowRuns; i++) load.Add(12);
        Assert.True(load.Light);
    }

    [Fact]
    public void ItComesBackOnlyOnceRunsSettle()
    {
        var load = new Overload();
        GoLight(load);
        Assert.True(load.Light);
        for (var i = 0; i < 4 * Overload.Settle; i++) load.Add(i % 20 == 0 ? 12 : 5); // lighter, but the CPU still busy: 5% slow
        Assert.True(load.Light);
        for (var i = 0; i < Overload.Settle; i++) load.Add(i == 500 ? 150 : 6); // free: one stall in 20 s
        Assert.False(load.Light);
        for (var i = 0; i < Overload.SlowRuns - 1; i++) load.Add(12); // the runs before it came back don't count again
        Assert.False(load.Light);
    }

    // As Krisp's: each time it has to go light again it waits longer before going back (20, 20, 40, 60 s), and a new call
    // starts over.
    [Fact]
    public void ItWaitsLongerEachTimeAndANewCallStartsOver()
    {
        var load = new Overload();
        foreach (var spans in new[] { 1, 1, 2, 3, 5 })
        {
            GoLight(load);
            Assert.True(load.Light);
            Assert.Equal(spans * Overload.Settle, load.SettleRuns);
            for (var i = 0; i < load.SettleRuns - 1; i++) load.Add(4);
            Assert.True(load.Light);
            load.Add(4);
            Assert.False(load.Light);
        }
        load.Reset();
        GoLight(load);
        Assert.Equal(Overload.Settle, load.SettleRuns);
    }
}

// Ways echo cancellation could fail: exo_apm.dll doesn't load or isn't shipped beside Exo; it leaves the echo of what Exo
// plays in the microphone; it changes a voice when nothing plays (on headphones it never runs, but between words on speakers
// it does); the two halves of a 20 ms frame, or the two channels, come back out of place.
public sealed class EchoCancellerTests
{
    private const int Rate = 48000, Frame = Opus.FrameSamples;

    // Speech-like sound: noise in syllables, four a second.
    private static float[] Talk(int seed, int samples, float scale)
    {
        var random = new Random(seed);
        var sound = new float[samples];
        for (var n = 0; n < samples; n++)
        {
            var syllable = MathF.Sin(MathF.PI * (n % (Rate / 4)) / (Rate / 4));
            sound[n] = (float)(random.NextDouble() * 2 - 1) * scale * syllable * syllable;
        }
        return sound;
    }

    private static double Power(float[] mono, int from) => mono.Skip(from).Sum(s => (double)s * s) / (mono.Length - from);

    // A room: what plays reaches the mic 50 ms later, 6 dB down, with 0.2 s of reverb.
    private static float[] Echo(float[] played)
    {
        var random = new Random(2);
        var room = new float[Rate / 5];
        for (var i = 0; i < room.Length; i++) room[i] = (float)(random.NextDouble() * 2 - 1) * MathF.Exp(-6.9f * i / room.Length) * 0.05f;
        room[0] = 0.5f;
        var echo = new float[played.Length];
        const int lag = Rate / 20;
        for (var n = lag; n < echo.Length; n++)
        {
            double sum = 0;
            for (var k = 0; k < room.Length && k <= n - lag; k += 4) sum += room[k] * played[n - lag - k];
            echo[n] = (float)sum;
        }
        return echo;
    }

    private static float[] Run(EchoCanceller echo, float[] played, float[] mic)
    {
        var output = new float[mic.Length];
        var stereo = new float[VoiceAudio.FrameFloats];
        for (var f = 0; f + Frame <= mic.Length; f += Frame)
        {
            for (var i = 0; i < Frame; i++) stereo[2 * i] = stereo[2 * i + 1] = played[f + i];
            echo.Render(stereo, silent: false);
            for (var i = 0; i < Frame; i++) stereo[2 * i] = stereo[2 * i + 1] = mic[f + i];
            echo.Capture(stereo);
            for (var i = 0; i < Frame; i++)
            {
                Assert.Equal(stereo[2 * i], stereo[2 * i + 1]);
                output[f + i] = stereo[2 * i];
            }
        }
        return output;
    }

    [Fact]
    public void TheCallsEchoComesOutOfTheMic()
    {
        using var echo = EchoCanceller.Create();
        Assert.NotNull(echo);
        var played = Talk(1, 8 * Rate, 0.3f);
        var heard = Echo(played);
        var output = Run(echo!, played, heard);
        var removedDb = 10 * Math.Log10(Power(heard, 4 * Rate) / Math.Max(Power(output, 4 * Rate), 1e-20));
        Assert.True(removedDb > 20, $"the echo only {removedDb:F1} dB down");
    }

    [Fact]
    public void AVoiceWithNothingPlayingKeepsItsLevel()
    {
        using var echo = EchoCanceller.Create();
        Assert.NotNull(echo);
        var voice = Talk(3, 6 * Rate, 0.1f);
        var output = Run(echo!, new float[voice.Length], voice);
        var changeDb = 10 * Math.Log10(Power(output, Rate) / Power(voice, Rate));
        Assert.InRange(changeDb, -1, 1);
    }

    [Fact]
    public void ExoAPMShipsBesideExo() => Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "exo_apm.dll")));
}
