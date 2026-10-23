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
        // A voice over it, at a normal level, kept by suppression: open on its first frame, and held 500 ms after.
        Assert.True(gate.Voiced(AtDb(-30), automatic: true, -60, before: -29.5f));
        for (var f = 0; f < VoiceGate.Hold; f++) Assert.True(gate.Voiced(AtDb(-100), automatic: true, -60, before: -45f));
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

/// <summary>No gain stage: with noise suppression off, the chain changes nothing but the low cut.</summary>
public sealed class ChainGainTests
{
    [Fact]
    public void WithoutNoiseSuppressionOnlyTheLowCutRuns()
    {
        using var calm = new ExoLauncher.Services.DiscordClient.Voice.VoiceChain();
        var plain = new ExoLauncher.Services.DiscordClient.Voice.VoiceProcessing("custom", false, false, false, false, -100, 0, false);
        var lowCut = new ExoLauncher.Services.DiscordClient.Voice.LowCut();
        float[] Quiet()
        {
            var frame = new float[ExoLauncher.Services.DiscordClient.Voice.VoiceAudio.FrameFloats];
            for (var i = 0; i < frame.Length; i += 2) frame[i] = frame[i + 1] = 0.15f * MathF.Sin(i * 0.01f);
            return frame;
        }
        // The gate's opening fades in; after it, only the low cut (which every frame goes through first) changes a frame.
        var opening = Quiet();
        lowCut.Process((float[])opening.Clone());
        calm.Process(opening, plain, null, systemGain: false);
        var quiet = Quiet();
        var before = (float[])quiet.Clone();
        lowCut.Process(before);
        calm.Process(quiet, plain, null, systemGain: false);
        Assert.Equal(before, quiet);
    }
}

/// <summary>The microphone's low cut: a desk's knock under a voice is taken out, the voice itself kept.</summary>
public sealed class LowCutTests
{
    private static double Rms(Func<int, double> wave, bool cut)
    {
        var filter = new ExoLauncher.Services.DiscordClient.Voice.LowCut();
        double sum = 0; var n = 0;
        for (var f = 0; f < 50; f++)
        {
            var frame = new float[ExoLauncher.Services.DiscordClient.Voice.VoiceAudio.FrameFloats];
            for (var i = 0; i < frame.Length; i += 2) frame[i] = frame[i + 1] = (float)wave(f * frame.Length / 2 + i / 2);
            if (cut) filter.Process(frame);
            if (f < 10) continue; // past its first moment
            for (var i = 0; i < frame.Length; i += 2) { sum += frame[i] * (double)frame[i]; n++; }
        }
        return Math.Sqrt(sum / n);
    }

    [Fact]
    public void ADeskKnockIsCutAndAVoiceKept()
    {
        Func<int, double> knock = t => 0.3 * Math.Sin(2 * Math.PI * 30 * t / 48000.0);
        Func<int, double> voice = t => 0.3 * Math.Sin(2 * Math.PI * 200 * t / 48000.0);
        Assert.True(20 * Math.Log10(Rms(knock, false) / Rms(knock, true)) > 25, "a 30 Hz knock wasn't cut");
        Assert.True(20 * Math.Log10(Rms(voice, false) / Rms(voice, true)) < 1, "a 200 Hz voice was cut");
    }
}

/// <summary>A knock on the desk is told from a voice by how fast it rises, and keeps the automatic gate shut.</summary>
public sealed class KnockTests
{
    private const int Floats = ExoLauncher.Services.DiscordClient.Voice.VoiceAudio.FrameFloats;

    private static float[] Frame(Func<int, double> wave, int at)
    {
        var frame = new float[Floats];
        for (var i = 0; i < frame.Length; i += 2) frame[i] = frame[i + 1] = (float)wave(at * Floats / 2 + i / 2);
        return frame;
    }

    [Fact]
    public void AKnockIsCaughtAndAVoiceIsNot()
    {
        var detector = new ExoLauncher.Services.DiscordClient.Voice.KnockDetector();
        var random = new Random(3);
        // Quiet room, then a knock: 2 ms of loud noise ringing down over 40 ms.
        Assert.False(detector.Hit(Frame(_ => 0.001 * (random.NextDouble() * 2 - 1), 0)));
        const int knockAt = 960 + 300;
        Assert.True(detector.Hit(Frame(t => t < knockAt ? 0.001 * (random.NextDouble() * 2 - 1)
            : 0.5 * Math.Exp(-(t - knockAt) / 480.0) * (random.NextDouble() * 2 - 1), 1)));
        // A voice starting: 200 Hz rising over 30 ms from the same quiet room.
        var voice = new ExoLauncher.Services.DiscordClient.Voice.KnockDetector();
        voice.Hit(Frame(_ => 0.001 * (random.NextDouble() * 2 - 1), 0));
        for (var f = 1; f < 6; f++)
            Assert.False(voice.Hit(Frame(t => Math.Min(1, (t - 960) / 1440.0) * 0.2 * Math.Sin(2 * Math.PI * 200 * t / 48000.0), f)), $"frame {f}");
    }

    [Fact]
    public void AKnockKeepsTheGateShutButNotAVoiceAlreadyGoing()
    {
        var gate = new ExoLauncher.Services.DiscordClient.Voice.VoiceGate();
        var tone = new float[Floats];
        for (var i = 0; i < tone.Length; i += 2) tone[i] = tone[i + 1] = (float)(0.03 * Math.Sin(2 * Math.PI * 200 * (i / 2) / 48000.0));
        var silence = new float[Floats];
        for (var f = 0; f < 50; f++) gate.Voiced(silence, automatic: true, -60, before: -80f);
        // The knock: loud, kept by suppression, but not speech by its estimate: shut, and for 100 ms after.
        Assert.False(gate.Voiced(tone, automatic: true, -60, before: -33f, speech: 5f, knock: true));
        for (var f = 0; f < 4; f++) Assert.False(gate.Voiced(tone, automatic: true, -60, before: -33f, speech: 5f));
        // Clear speech opens it even then.
        var other = new ExoLauncher.Services.DiscordClient.Voice.VoiceGate();
        for (var f = 0; f < 50; f++) other.Voiced(silence, automatic: true, -60, before: -80f);
        Assert.True(other.Voiced(tone, automatic: true, -60, before: -33f, speech: 25f, knock: true));
        // Already talking: a knock doesn't cut it.
        Assert.True(other.Voiced(tone, automatic: true, -60, before: -33f, speech: 5f, knock: true));
    }
}

/// <summary>Discord's lead-in: the gate's opening brings the 100 ms before it, oldest first, faded in at its own start.</summary>
public sealed class GateLeadTests
{
    [Fact]
    public void TheFramesBeforeAnOpeningGoFirst()
    {
        using var chain = new ExoLauncher.Services.DiscordClient.Voice.VoiceChain();
        var manual = new ExoLauncher.Services.DiscordClient.Voice.VoiceProcessing("custom", false, false, false, false, -50, 0, false);
        float[] At(float amplitude, int f)
        {
            var frame = new float[ExoLauncher.Services.DiscordClient.Voice.VoiceAudio.FrameFloats];
            for (var i = 0; i < frame.Length; i += 2) frame[i] = frame[i + 1] = amplitude * MathF.Sin((f * frame.Length / 2 + i / 2) * 0.05f);
            return frame;
        }
        for (var f = 0; f < 20; f++) Assert.False(chain.Process(At(0.001f, f), manual, null, systemGain: false));
        Assert.Empty(chain.TakeLead());
        var opening = At(0.3f, 20);
        Assert.True(chain.Process(opening, manual, null, systemGain: false));
        var lead = chain.TakeLead().ToArray();
        Assert.Equal(ExoLauncher.Services.DiscordClient.Voice.VoiceChain.LeadFrames, lead.Length);
        Assert.Equal(0f, lead[0][0]); // faded in from silence at the lead-in's first sample
        Assert.True(MathF.Abs(opening[2 * 200]) > 0.01f); // the opening frame itself isn't faded again
        Assert.True(chain.Process(At(0.3f, 21), manual, null, systemGain: false));
        Assert.Empty(chain.TakeLead()); // only on the opening
    }
}

/// <summary>A recording's pauses turned down 30 dB, never a word: the gain is up before the gate opens, and glides.</summary>
public sealed class RecordingGateTests
{
    [Fact]
    public void PausesGoDownAndAWordIsWholeFromItsStart()
    {
        var gate = new ExoLauncher.Services.DiscordClient.Voice.RecordingGate();
        var outputs = new List<(float[] Frame, long At)>();
        for (var f = 0; f < 30; f++)
        {
            var frame = Enumerable.Repeat(0.1f, ExoLauncher.Services.DiscordClient.Voice.VoiceAudio.FrameFloats).ToArray();
            var (late, at) = gate.Push(frame, open: f >= 15, at: f);
            if (late is not null) outputs.Add(((float[])late.Clone(), at));
        }
        Assert.Equal(0, outputs[0].At); // frames keep their own times, 5 frames late
        var floor = MathF.Pow(10, ExoLauncher.Services.DiscordClient.Voice.RecordingGate.FloorDb / 20) * 0.1f;
        Assert.InRange(outputs[5].Frame[^1], floor * 0.99f, floor * 1.01f); // a pause, turned down
        Assert.All(outputs.Where(o => o.At >= 15), o => Assert.All(o.Frame, s => Assert.Equal(0.1f, s, 1e-5f))); // the word, whole from its first frame
        var ramp = outputs.First(o => o.At == 10).Frame; // the frame 100 ms before the word glides up
        for (var i = 2; i < ramp.Length; i += 2) Assert.True(ramp[i] >= ramp[i - 2] && ramp[i] - ramp[i - 2] < 0.001f);
    }
}

/// <summary>Discord's "no input from your mic": 15 s of nothing says it, the first sound clears it, mute never does.</summary>
public sealed class MicSilenceTests
{
    private static float[] AtDb(float db) => Enumerable.Repeat(MathF.Pow(10, db / 20), 1920).ToArray();

    [Fact]
    public void FifteenSecondsOfNothingIsSilentAndASoundClearsIt()
    {
        var mic = new ExoLauncher.Services.DiscordClient.Voice.MicSilence();
        for (var f = 0; f < ExoLauncher.Services.DiscordClient.Voice.MicSilence.Frames - 1; f++) Assert.Null(mic.Hear(AtDb(-90), muted: false));
        Assert.True(mic.Hear(AtDb(-90), muted: false));
        Assert.Null(mic.Hear(AtDb(-69), muted: false)); // between the levels: still silent
        Assert.False(mic.Hear(AtDb(-40), muted: false));
    }

    [Fact]
    public void AQuietRoomOrAMutedMicIsNeverCalledSilent()
    {
        var room = new ExoLauncher.Services.DiscordClient.Voice.MicSilence();
        for (var f = 0; f < 2000; f++) Assert.Null(room.Hear(AtDb(-68), muted: false));
        var muted = new ExoLauncher.Services.DiscordClient.Voice.MicSilence();
        for (var f = 0; f < 2000; f++) Assert.Null(muted.Hear(AtDb(-100), muted: true));
    }
}
