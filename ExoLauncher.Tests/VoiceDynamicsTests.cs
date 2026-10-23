using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

// Ways registering the call's audio threads with Windows' multimedia scheduler could fail silently: the import is
// wrong (the name, or ANSI where Windows wants UTF-16) and nothing joins; a task Windows doesn't know is asked for; a
// thread joins and never leaves, so it keeps its boost after the call.
public sealed class MmcssTaskTests
{
    [Theory]
    [InlineData(MmcssTask.ProAudio)]
    [InlineData(MmcssTask.Audio)]
    public void AnAudioThreadJoinsTheSchedulerAndLeavesIt(string task)
    {
        bool joined = false, left = false, leftTwice = true;
        var thread = new Thread(() =>
        {
            var mmcss = MmcssTask.Join(task);
            joined = mmcss.Joined;
            left = mmcss.Leave();
            leftTwice = mmcss.Leave();
        });
        thread.Start();
        thread.Join();
        // Only a PC with the scheduler's service gone or disabled (some Windows Server installs) can't join.
        var service = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\MMCSS", "Start", null);
        Assert.SkipWhen(service is null or 4, "Windows' multimedia scheduler (MMCSS) is off on this PC");
        Assert.True(joined);
        Assert.True(left);
        Assert.False(leftTwice); // given back once: a second revert is refused
    }

    [Fact]
    public void ATaskWindowsDoesntKnowJoinsNothing() => Assert.False(MmcssTask.Join("Exo No Such Task").Joined);
}

// Ways the gate's release fade could fail: the hold's last frame still ends on a step to silence (the click it is
// there to take out); a frame the gate holds back is faded instead of the last one sent; the next opening starts on a
// step from silence; the fade runs while Push to Talk decides what is sent.
public sealed class GateFadeTests
{
    private static readonly VoiceProcessing Manual = new("custom", false, false, false, false, -30, 0, false);

    // A 200 Hz tone moves at most A·2π·200/48000 a sample: a step past that is a click.
    private static double Natural(double db) => Math.Pow(10, db / 20) * Math.Sqrt(2) * 2 * Math.PI * 200 / Opus.SampleRate;

    private sealed class Take(VoiceChain chain)
    {
        internal readonly List<float> Heard = []; // what the other side hears: the frames sent, silence for those held back
        internal readonly List<bool> Opened = [];
        private int _n;

        internal void Run(int frames, double db, VoiceProcessing processing)
        {
            var amplitude = Math.Pow(10, db / 20) * Math.Sqrt(2);
            for (var f = 0; f < frames; f++, _n += Opus.FrameSamples)
            {
                var frame = new float[VoiceAudio.FrameFloats];
                for (var i = 0; i < Opus.FrameSamples; i++) frame[2 * i] = frame[2 * i + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * 200 * (_n + i) / Opus.SampleRate));
                var open = chain.Process(frame, processing, null, systemGain: false);
                Opened.Add(open);
                // The lead-in sent ahead of an opening is heard in place of the silence before it.
                var lead = chain.TakeLead().ToArray();
                for (var l = 0; l < lead.Length; l++)
                    for (var i = 0; i < Opus.FrameSamples; i++) Heard[Heard.Count - (lead.Length - l) * Opus.FrameSamples + i] = lead[l][2 * i];
                for (var i = 0; i < Opus.FrameSamples; i++) Heard.Add(open ? frame[2 * i] : 0f);
            }
        }

        internal double Steepest(int fromFrame, int toFrame)
        {
            var most = 0.0;
            for (var i = fromFrame * Opus.FrameSamples; i < toFrame * Opus.FrameSamples - 1; i++) most = Math.Max(most, Math.Abs(Heard[i + 1] - Heard[i]));
            return most;
        }

        internal double Loudest(int frame) => Heard.Skip(frame * Opus.FrameSamples).Take(Opus.FrameSamples).Max(MathF.Abs);
    }

    [Fact]
    public void TheGateFadesOutOnItsLastFrameAndBackInWithoutAClick()
    {
        using var chain = new VoiceChain { StepsAside = false };
        var take = new Take(chain);
        take.Run(10, -20, Manual); // a voice over the threshold
        take.Run(30, -40, Manual); // under it: the 500 ms hold, then shut
        take.Run(5, -20, Manual);  // the voice again, its lead-in heard in the silence's last 100 ms
        Assert.Equal(Enumerable.Repeat(true, 35).Concat(Enumerable.Repeat(false, 5)).Concat(Enumerable.Repeat(true, 5)), take.Opened);
        var release = take.Steepest(34, 35); // the hold's last frame, into silence
        var attack = take.Steepest(34, 41); // silence into the lead-in, and on into the opening
        TestContext.Current.TestOutputHelper?.WriteLine($"release step {release:E2} (tone's own {Natural(-40):E2}, a cut {take.Loudest(23):E2}); opening step {attack:E2} (tone's own {Natural(-20):E2})");
        Assert.True(release < 1.1 * Natural(-40), $"release step {release:E2}, the tone's own {Natural(-40):E2}");
        Assert.True(attack < 1.1 * Natural(-20), $"opening step {attack:E2}, the tone's own {Natural(-20):E2}");
        Assert.True(Math.Abs(take.Heard[35 * Opus.FrameSamples - 1]) < 1e-6, "the last frame sent doesn't end at silence");
        Assert.True(take.Loudest(33) > 0.012, "a frame before the hold's last was faded");
    }

    [Fact]
    public void PushToTalkIsNeverFaded()
    {
        using var chain = new VoiceChain { StepsAside = false };
        var take = new Take(chain);
        var ptt = Manual with { PushToTalk = true };
        take.Run(10, -20, ptt);
        take.Run(30, -40, ptt);
        Assert.True(take.Opened[34] && !take.Opened[35]);
        Assert.True(take.Loudest(34) > 0.012, "the gate's last frame was faded under Push to Talk");
        Assert.True(Math.Abs(take.Heard[0]) < 1e-9 && take.Loudest(0) > 0.1);
    }
}

// Ways gain control after suppression could fail: it learns from noise, clicks or pauses and pumps them up; it steps
// the gain between frames (a zipper); it goes past its bounds on a very quiet or very hot voice; one loud first syllable
// throws it off; through the chain, a fan with clicks and no voice moves the pre-gain or the leveling.
[Collection("DeepFilterNet")]
public sealed class SpeechLevelerTests
{
    private static float[] Tone(double db, int seed = 0)
    {
        var amplitude = Math.Pow(10, db / 20) * Math.Sqrt(2);
        var frame = new float[VoiceAudio.FrameFloats];
        for (var i = 0; i < Opus.FrameSamples; i++) frame[2 * i] = frame[2 * i + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * 180 * (i + seed * 7) / Opus.SampleRate));
        return frame;
    }

    [Theory]
    [InlineData(-34)] // a quiet voice
    [InlineData(-14)] // a hot mic
    public void AVoiceComesToTheSpeakingLevel(double db)
    {
        var leveler = new SpeechLeveler();
        var frame = Tone(db);
        for (var f = 0; f < 150; f++) { frame = Tone(db, f); leveler.Process(frame, voiced: true); }
        var level = SpeechLeveler.Level(frame);
        TestContext.Current.TestOutputHelper?.WriteLine($"{db} dBFS in, {level:0.0} dBFS out after 3 s, gain {leveler.GainDb:+0.0;-0.0}");
        Assert.InRange(level, SpeechLeveler.TargetDb - 1.5, SpeechLeveler.TargetDb + 1.5);
    }

    [Fact]
    public void ItStaysWithinTwelveDecibels()
    {
        var quiet = new SpeechLeveler();
        var hot = new SpeechLeveler();
        for (var f = 0; f < 500; f++) { quiet.Process(Tone(-50, f), voiced: true); hot.Process(Tone(-3, f), voiced: true); }
        Assert.Equal(SpeechLeveler.RangeDb, quiet.GainDb, 0.01f);
        Assert.Equal(-SpeechLeveler.RangeDb, hot.GainDb, 0.01f);
    }

    // Noise, clicks and pauses between words: the gain stays exactly where the voice left it, and moves smoothly when it moves.
    [Fact]
    public void NoiseClicksAndPausesNeverMoveIt()
    {
        var leveler = new SpeechLeveler();
        for (var f = 0; f < 100; f++) leveler.Process(Tone(-30, f), voiced: true);
        var settled = leveler.GainDb;
        var random = new Random(9);
        for (var f = 0; f < 500; f++)
        {
            var frame = new float[VoiceAudio.FrameFloats];
            for (var i = 0; i < frame.Length; i++) frame[i] = (float)(random.NextDouble() * 2 - 1) * (f % 18 == 0 && i < 480 ? 0.5f : 0.01f);
            leveler.Process(frame, voiced: false);
            Assert.Equal(settled, leveler.GainDb);
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"gain {settled:+0.0;-0.0} dB, unchanged over 10 s of noise and clicks");
        // A hot voice after it: the gain glides, never stepping more than the frame's own ramp.
        var hot = Tone(-10, 3);
        var before = (float[])hot.Clone();
        leveler.Process(hot, voiced: true);
        var ratios = Enumerable.Range(0, Opus.FrameSamples).Where(i => MathF.Abs(before[2 * i]) > 0.01f).Select(i => hot[2 * i] / before[2 * i]).ToArray();
        for (var i = 1; i < ratios.Length; i++) Assert.True(ratios[i] <= ratios[i - 1] + 1e-5f, "the gain stepped up inside a frame");
    }

    // A fan with keyboard and mouse clicks and nobody talking, then a quiet voice over it, through the real chain
    // (DeepFilterNet at Krisp's level step): the room goes to silence and the voice keeps its own level (no gain stage).
    [Fact]
    public void ThroughTheChainTheRoomGoesAndTheVoiceKeepsItsLevel()
    {
        NoiseSuppressorTests.UseTestPlugin();
        using var chain = new VoiceChain { StepsAside = false };
        var processing = new VoiceProcessing("custom", true, false, false, true, -60, 0, false);
        chain.Prepare(processing);
        const int fanFrames = 200, voiceFrames = 500;
        var sound = VoiceChainTests.Fan((fanFrames + voiceFrames) * Opus.FrameSamples, -50, seed: 4);
        var random = new Random(6);
        for (var n = 0; n < fanFrames * Opus.FrameSamples; n++)
            if (n % (Opus.SampleRate * 37 / 100) < 240) sound[n] += (float)(random.NextDouble() * 2 - 1) * 0.2f;
        var voice = VoiceChainTests.Vowels(voiceFrames * Opus.FrameSamples, -45);
        for (var i = 0; i < voice.Length; i++) sound[fanFrames * Opus.FrameSamples + i] += voice[i];
        var frame = new float[VoiceAudio.FrameFloats];
        List<float> room = [], sent = [];
        var peak = 0f;
        for (var f = 0; f < fanFrames + voiceFrames; f++)
        {
            for (var i = 0; i < Opus.FrameSamples; i++) frame[2 * i] = frame[2 * i + 1] = sound[f * Opus.FrameSamples + i];
            var open = chain.Process(frame, processing, null, systemGain: false);
            Assert.Equal("off", chain.Stages.Gain);
            foreach (var s in frame) peak = MathF.Max(peak, MathF.Abs(s));
            if (f >= 50 && f < fanFrames && (f * Opus.FrameSamples) % (Opus.SampleRate * 37 / 100) > 4800) room.Add(SpeechLeveler.Level(frame));
            if (open && f >= fanFrames + voiceFrames - 100) sent.Add(SpeechLeveler.Level(frame));
        }
        // The voice's own frames (DeepFilterNet takes some vowels of this synthetic voice down 20 dB, and the hold sends
        // the quiet after each word): those within 10 dB of the loudest.
        var sentDb = sent.Where(db => db > sent.Max() - 10).Average();
        TestContext.Current.TestOutputHelper?.WriteLine($"the room at {room.Average():0.0} dBFS, a -45 dBFS voice sent at {sentDb:0.0} dBFS, peak {20 * MathF.Log10(peak):0.0} dBFS");
        Assert.True(room.Average() < -50, $"the room at {room.Average():0.0} dBFS");
        Assert.InRange(sentDb, -52, -38); // the voice at its own level, give or take what suppression keeps of a vowel
        Assert.True(peak <= 1f, $"peak {peak}");
    }
}

// Ways evening out the voices in a call could fail: one person's level leaks into another's (one leveler for the
// call); the noise an open mic sends is learned as their voice and pulled up; their first frame, before their quiet is
// known, throws the level off; someone who leaves and whose SSRC comes back keeps the old gain.
public sealed class VoiceLevelingTests
{
    // Someone talking as Discord sends it: 400 ms of a voice at their level, then their gate's 300 ms tail of their room
    // 40 dB under it, then nothing (no frames) until their next sentence.
    private static IEnumerable<float[]> Talking(double db, int sentences, int seed)
    {
        var random = new Random(seed);
        for (var s = 0; s < sentences; s++)
            for (var f = 0; f < 35; f++)
            {
                var frame = new float[VoiceAudio.FrameFloats];
                var amplitude = f < 20 ? Math.Pow(10, db / 20) * Math.Sqrt(2) : 0;
                var room = Math.Pow(10, (db - 40) / 20) * Math.Sqrt(3);
                for (var i = 0; i < Opus.FrameSamples; i++)
                    frame[2 * i] = frame[2 * i + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * 160 * (f * Opus.FrameSamples + i) / Opus.SampleRate) + room * (random.NextDouble() * 2 - 1));
                yield return frame;
            }
    }

    [Fact]
    public void EachPersonComesToTheSameLevelOnTheirOwn()
    {
        var leveling = new VoiceLeveling();
        var hot = Talking(-12, 8, 1).GetEnumerator();
        var quiet = Talking(-34, 8, 2).GetEnumerator();
        float hotDb = 0, quietDb = 0;
        // Talking over each other, frame for frame.
        for (var f = 0; hot.MoveNext() && quiet.MoveNext(); f++)
        {
            leveling.Process(1, hot.Current);
            leveling.Process(2, quiet.Current);
            if (f % 35 == 10) (hotDb, quietDb) = (SpeechLeveler.Level(hot.Current), SpeechLeveler.Level(quiet.Current));
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"a -12 dBFS voice plays at {hotDb:0.0} dBFS ({leveling.GainDb(1):+0.0;-0.0} dB), a -34 dBFS one at {quietDb:0.0} ({leveling.GainDb(2):+0.0;-0.0} dB)");
        Assert.InRange(hotDb, SpeechLeveler.TargetDb - 2, SpeechLeveler.TargetDb + 2);
        Assert.InRange(quietDb, SpeechLeveler.TargetDb - 2, SpeechLeveler.TargetDb + 2);
        // Gone, and the SSRC handed to someone new: they start at no gain.
        leveling.Forget(1);
        Assert.Equal(0f, leveling.GainDb(1));
    }

    // An open mic sending its fan all the time (no gate on their side), and a mic sending clicks over its room: neither
    // is pulled up or down, however long it goes on.
    [Fact]
    public void TheNoiseAMicSendsNeverMovesIt()
    {
        var leveling = new VoiceLeveling();
        var random = new Random(3);
        var fan = 0.014f; // -40 dBFS
        for (var f = 0; f < 1500; f++)
        {
            var frame = new float[VoiceAudio.FrameFloats];
            for (var i = 0; i < frame.Length; i++) frame[i] = (float)(random.NextDouble() * 2 - 1) * fan * 1.73f;
            leveling.Process(1, frame);
            var clicks = new float[VoiceAudio.FrameFloats];
            for (var i = 0; i < clicks.Length; i++) clicks[i] = (float)(random.NextDouble() * 2 - 1) * (f % 20 == 0 && i < 480 ? 0.5f : 0.0005f);
            leveling.Process(2, clicks);
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"after 30 s: the fan's gain {leveling.GainDb(1):0.00} dB, the clicks' {leveling.GainDb(2):0.00} dB");
        Assert.Equal(0f, leveling.GainDb(1));
        Assert.InRange(leveling.GainDb(2), -1f, 1f);
    }
}
