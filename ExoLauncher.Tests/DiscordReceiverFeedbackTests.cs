using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Tests;

/// <summary>
/// The reports a viewer sends so Discord sends a stream at the quality the connection takes. Ways they could go wrong:
/// a REMB whose exponent and mantissa say another bitrate; transport-cc feedback with the wrong chunk symbols, deltas,
/// reference time or padding (the server would read every packet as late or lost); a transport sequence number read
/// from another extension.
/// </summary>
public sealed class DiscordReceiverFeedbackTests
{
    [Fact]
    public void RembSaysTheBitrateInSixBitsOfExponentAndEighteenOfMantissa()
    {
        var packet = Rtcp.Remb(1, 15_000_000, [2]);
        Assert.Equal(24, packet.Length);
        Assert.Equal([0x8F, 206, 0, 5], packet[..4]);
        Assert.Equal("REMB"u8.ToArray(), packet[12..16]);
        Assert.Equal(1, packet[16]);
        // 15,000,000 = 234,375 << 6.
        var exponent = packet[17] >> 2;
        var mantissa = ((packet[17] & 3) << 16) | (packet[18] << 8) | packet[19];
        Assert.Equal(15_000_000, (long)mantissa << exponent);
        Assert.Equal([0, 0, 0, 2], packet[20..24]);
    }

    [Fact]
    public void TransportFeedbackMarksArrivalsAndLossesAgainstItsReferenceTime()
    {
        // Three packets from 100: one at 64.5 ms, one lost, one at 65 ms.
        var packet = Rtcp.TransportFeedback(1, 2, 100, 7, [64_500, null, 65_000]);
        Assert.Equal(24, packet.Length);
        Assert.Equal([0x8F, 205, 0, 5], packet[..4]);
        Assert.Equal([0, 100, 0, 3], packet[12..16]);   // base sequence, status count
        Assert.Equal([0, 0, 1, 7], packet[16..20]);     // reference 1 x 64 ms, feedback count
        Assert.Equal([0xD1, 0x00], packet[20..22]);     // two-bit chunk: received, lost, received
        Assert.Equal([2, 2], packet[22..24]);           // 500 us, then 500 us more, in 250 us steps
    }

    [Fact]
    public void TransportFeedbackPadsToAWordAndCarriesLargeGapsInTwoBytes()
    {
        var packet = Rtcp.TransportFeedback(1, 2, 0, 0, [0, 100_000]);
        Assert.Equal(0, packet.Length % 4);
        Assert.True((packet[0] & 0x20) != 0);
        Assert.Equal((byte)(packet.Length - 25), packet[^1]); // 20 + 2 chunk + 1 small + 2 large = 25 bytes, padded
        Assert.Equal([0x01, 0x90], packet[23..25]);           // 100 ms = 400 steps of 250 us
    }

    [Fact]
    public void ReadsTheTransportSequenceAsTheTwoByteExtensionWhateverItsId()
    {
        // Orientation (13, one byte), then a two-byte element (id 3).
        Assert.Equal(0x1234, VoiceTransport.TransportSequence([0xD0, 0x01, 0x31, 0x12, 0x34, 0x00]));
        Assert.Equal(-1, VoiceTransport.TransportSequence([0xD0, 0x01]));
    }
}

/// <summary>
/// The automatic noise gate after noise suppression, fed frames shaped as DeepFilterNet leaves them. Ways it could fail:
/// steady fan noise, which suppression keeps only about 5 dB down until it has heard a voice, opens it; the odd frame
/// suppression takes to silence drags its floor down, so the noise left stands over it (the old gate, on up to 549 of
/// 550 frames); a word's first frame stays shut (clipped syllable); a quiet mic's voice stays shut (it did at -55 dBFS);
/// something suppression cuts (a key click) opens it; a steady sound suppression keeps (music in the mic) holds it open,
/// or holds a voice over it shut.
/// </summary>
public sealed class VoiceGateTests
{
    private static float[] Tone(double rms)
    {
        var frame = new float[ExoLauncher.Services.DiscordClient.Voice.VoiceAudio.FrameFloats];
        for (var i = 0; i < frame.Length; i += 2) frame[i] = frame[i + 1] = (float)(rms * Math.Sqrt(2) * Math.Sin(2 * Math.PI * 200 * (i / 2) / 48000.0));
        return frame;
    }

    private static float[] AtDb(double db) => Tone(Math.Pow(10, db / 20));

    [Fact]
    public void SteadyFanNoiseNeverOpensItAndAVoiceOpensItAtOnce()
    {
        var gate = new VoiceGate();
        var random = new Random(4);
        // A fan at -45 dBFS: suppression lets it through 2 to 8 dB down, wandering frame to frame, and now and then takes
        // a frame to silence. From the very first frame, ten seconds of it never open the gate.
        for (var f = 0; f < 500; f++)
        {
            var heard = -45 + (float)(random.NextDouble() * 2 - 1);
            var cleaned = random.Next(8) == 0 ? -100 : heard - 2 - 6 * random.NextDouble();
            Assert.False(gate.Voiced(AtDb(cleaned), automatic: true, -60, before: heard), $"frame {f}");
        }
        // A voice over it, at a normal level, kept by suppression: open on its first frame, and held 300 ms after.
        Assert.True(gate.Voiced(AtDb(-30), automatic: true, -60, before: -29.5f));
        for (var f = 0; f < 15; f++) Assert.True(gate.Voiced(AtDb(-100), automatic: true, -60, before: -45f));
        Assert.False(gate.Voiced(AtDb(-100), automatic: true, -60, before: -45f));
    }

    [Fact]
    public void AQuietVoiceOpensItAndWhatSuppressionCutsDoesNot()
    {
        var quiet = new VoiceGate();
        for (var f = 0; f < 50; f++) Assert.False(quiet.Voiced(AtDb(-100), automatic: true, -60, before: -80f));
        // A quiet mic's voice: -60 dBFS after suppression, -59 before (suppression kept it).
        Assert.True(quiet.Voiced(AtDb(-60), automatic: true, -60, before: -59f));
        var clicks = new VoiceGate();
        for (var f = 0; f < 50; f++) clicks.Voiced(AtDb(-100), automatic: true, -60, before: -80f);
        // A key click: -30 before, cut to -50 (20 dB taken off): it isn't a voice.
        Assert.False(clicks.Voiced(AtDb(-50), automatic: true, -60, before: -30f));
        // Far too quiet to be a voice, kept or not.
        Assert.False(clicks.Voiced(AtDb(-80), automatic: true, -60, before: -79f));
        // Without suppression, a -60 dBFS sound stays under the -55 floor.
        Assert.False(new VoiceGate().Voiced(AtDb(-60), automatic: true, -60));
    }

    [Fact]
    public void ASteadySoundSuppressionKeepsIsLearnedAndAVoiceOverItOpens()
    {
        var steady = new VoiceGate();
        // Music in the mic at -40, kept by suppression: learned at once, so it never opens the gate.
        for (var i = 0; i < 400; i++) Assert.False(steady.Voiced(AtDb(-40), automatic: true, -60, before: -39f), $"frame {i}");
        // A voice over it.
        Assert.True(steady.Voiced(AtDb(-20), automatic: true, -60, before: -19.5f));
    }
}

/// <summary>Clip Guard: a voice over full scale is held under -1 dBFS with no delay, and one under it passes untouched.</summary>
public sealed class ClipGuardTests
{
    [Fact]
    public void LoudSoundStaysUnderTheCeilingAndQuietSoundIsUntouched()
    {
        using var chain = new ExoLauncher.Services.DiscordClient.Voice.VoiceChain();
        var plain = new ExoLauncher.Services.DiscordClient.Voice.VoiceProcessing("custom", false, false, false, false, false, -100, 0, false);
        var loud = new float[ExoLauncher.Services.DiscordClient.Voice.VoiceAudio.FrameFloats];
        for (var i = 0; i < loud.Length; i += 2) loud[i] = loud[i + 1] = 2f * MathF.Sin(i * 0.01f);
        chain.Process(loud, plain, null, systemGain: false);
        Assert.All(loud, s => Assert.InRange(MathF.Abs(s), 0f, 0.8911f));

        using var calm = new ExoLauncher.Services.DiscordClient.Voice.VoiceChain();
        var quiet = new float[ExoLauncher.Services.DiscordClient.Voice.VoiceAudio.FrameFloats];
        for (var i = 0; i < quiet.Length; i += 2) quiet[i] = quiet[i + 1] = 0.3f * MathF.Sin(i * 0.01f);
        var before = (float[])quiet.Clone();
        calm.Process(quiet, plain, null, systemGain: false);
        Assert.Equal(before, quiet);
    }
}
