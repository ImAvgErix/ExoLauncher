using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

// Ways Only My Voice could fail silently: one low score holds the user's own voice back (a cough, a shout);
// a held-back voice never comes back; the 48-to-16 kHz step lets the top octave fold down onto the voice (the
// voiceprint then judges noise) or dulls the voice itself; a voiceprint saved isn't the one read back, or reads
// without DPAPI; the real model can't tell the enrolled voice from another man's.
[Collection("DeepFilterNet")]
public sealed class MyVoiceTests
{
    // The safety stop: a voiceprint that misjudges the user's own voice held them back for the rest of a call (seen in a
    // game: 64 holds to 5 "your voice again"). Over half of 20 s of speech held back stops it; a voice held back now and
    // then (a cough, someone talking over them) never does.
    [Fact]
    public void AVoiceAsLoudAsYoursIsNeverHeldBack()
    {
        // The user at a tuned mic (-24 dBFS), shouting in a game (-14): never held back, whatever the voiceprint says.
        Assert.True(VoiceChain.AsLoudAs(-24f, -24f));
        Assert.True(VoiceChain.AsLoudAs(-14f, -24f));
        Assert.True(VoiceChain.AsLoudAs(-24f - VoiceChain.CloseDb, -24f));
        // Someone across the room or a TV at the same mic, 10 dB and more under the user: held back as before.
        Assert.False(VoiceChain.AsLoudAs(-34f, -24f));
        // The level follows the user's talking, a frame at a time, and a shout or silence can't run off with it.
        Assert.Equal(-24f + 10f / 75, VoiceChain.YourLevel(-24f, -14f), 3);
        Assert.Equal(-24f, VoiceChain.YourLevel(-24f, -100f));
        var level = -24f;
        for (var i = 0; i < 2000; i++) level = VoiceChain.YourLevel(level, 0f);
        Assert.Equal(-10f, level);
    }

    [Fact]
    public void HoldingBackMostOfTwentySecondsOfSpeechStopsItAndNowAndThenNeverDoes()
    {
        var mostly = Enumerable.Repeat(30, MyVoice.StopSeconds).ToArray(); // 30 of each second's 50 voiced frames held
        Assert.True(MyVoice.ShouldStop(mostly));
        var half = Enumerable.Repeat(25, MyVoice.StopSeconds).ToArray();
        Assert.False(MyVoice.ShouldStop(half)); // exactly half is not more than half
        var nowAndThen = Enumerable.Range(0, MyVoice.StopSeconds).Select(i => i % 4 == 0 ? 50 : 5).ToArray();
        Assert.False(MyVoice.ShouldStop(nowAndThen));
    }

    [Fact]
    public void AVoiceIsHeldBackOnlyAfterTwoLowJudgmentsAndComesBackAtOnce()
    {
        var v = MyVoice.Judge(default, 0.30f);
        Assert.False(v.Held); // one low judgment: the user's voice isn't cut on a cough
        v = MyVoice.Judge(v, 0.58f);
        Assert.False(v.Held);
        v = MyVoice.Judge(v, 0.30f);
        Assert.False(v.Held); // the uncertain judgment between reset the count
        v = MyVoice.Judge(v, 0.40f);
        Assert.True(v.Held);
        v = MyVoice.Judge(v, 0.58f);
        Assert.True(v.Held); // uncertain: stays as it was
        v = MyVoice.Judge(v, MyVoice.Mine);
        Assert.False(v.Held);
    }

    [Fact]
    public void TheVoiceKeepsItsLevelAndTheTopOctaveIsCutBefore16kHz()
    {
        static double GainDb(double hz)
        {
            var decimator = new MyVoice.Decimator();
            var frame = new float[Opus.FrameSamples];
            var down = new float[Opus.FrameSamples / 3];
            double inPower = 0, outPower = 0;
            for (var f = 0; f < 20; f++)
            {
                for (var i = 0; i < frame.Length; i++) frame[i] = (float)Math.Sin(2 * Math.PI * hz * (f * frame.Length + i) / Opus.SampleRate);
                decimator.Run(frame, down);
                if (f < 2) continue;
                inPower += frame.Sum(x => (double)x * x) / frame.Length;
                outPower += down.Sum(x => (double)x * x) / down.Length;
            }
            return 10 * Math.Log10(outPower / inPower);
        }
        Assert.InRange(GainDb(300), -0.5, 0.5);
        Assert.InRange(GainDb(3000), -1.0, 0.5);
        Assert.True(GainDb(12000) < -30, $"12 kHz only {GainDb(12000):F1} dB down: it would fold to 4 kHz");
    }

    [Fact]
    public void TheVoiceprintReadBackIsTheOneSaved()
    {
        var path = Path.Combine(Path.GetTempPath(), "exo-tests-" + Guid.NewGuid().ToString("N"), "my-voice.bin");
        var before = Voiceprint.FilePath;
        Voiceprint.FilePath = path;
        try
        {
            Assert.False(Voiceprint.Learned);
            var print = Enumerable.Range(0, 192).Select(i => MathF.Sin(i)).ToArray();
            Voiceprint.Save(new VoiceprintLearning.State(print, print, 12.5f, 3));
            Assert.True(Voiceprint.Learned);
            Assert.Equal(print, Voiceprint.Load());
            Assert.Equal(12, Voiceprint.Seconds);
            Assert.Equal(3, Voiceprint.LoadState()!.Updates);
            var clear = System.Runtime.InteropServices.MemoryMarshal.AsBytes(print.AsSpan(0, 8)).ToArray();
            Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf(clear) < 0); // not stored in the clear
            Voiceprint.Forget();
            Assert.False(Voiceprint.Learned);
        }
        finally
        {
            Voiceprint.FilePath = before;
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    // The real model on three TTS voices (16 kHz mono WAVs from Windows' speech synthesis: david-enroll, david-talk,
    // zira-talk, mark-talk) and titanet_small.onnx, in the folder EXO_SPEAKER_TEST_DIR names; skipped without it.
    [Fact]
    public void TheModelTellsTheLearnedVoiceFromOthers()
    {
        var dir = Environment.GetEnvironmentVariable("EXO_SPEAKER_TEST_DIR");
        Assert.SkipWhen(dir is null || !File.Exists(Path.Combine(dir, "titanet_small.onnx")), "EXO_SPEAKER_TEST_DIR isn't set");
        var before = SpeakerModel.ModelPath;
        SpeakerModel.ModelPath = Path.Combine(dir!, "titanet_small.onnx");
        try
        {
            Assert.True(SpeakerModel.Ready());
            var me = SpeakerModel.Embed(Voiced(Wav(Path.Combine(dir!, "david-enroll.wav"))));
            Assert.NotNull(me);
            float[] Scores(string who)
            {
                var talk = Voiced(Wav(Path.Combine(dir!, who + "-talk.wav")));
                var scores = new List<float>();
                for (var at = 0; at + 16000 <= talk.Length; at += 8000) scores.Add(VoiceprintLearning.Dot(SpeakerModel.Embed(talk.AsSpan(at, 16000))!, me!));
                return [.. scores];
            }
            Assert.True(Scores("david").Min() >= MyVoice.Mine, $"the learned voice scored {Scores("david").Min():0.00}");
            Assert.True(Scores("zira").Max() < MyVoice.Other, $"another voice scored {Scores("zira").Max():0.00}");
            Assert.True(Scores("mark").Max() < MyVoice.Other, $"another man's voice scored {Scores("mark").Max():0.00}");
        }
        finally { SpeakerModel.ModelPath = before; }
    }

    private static float[] Wav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var data = 12;
        while (data + 8 <= bytes.Length && !(bytes[data] == 'd' && bytes[data + 1] == 'a' && bytes[data + 2] == 't' && bytes[data + 3] == 'a'))
            data += 8 + BitConverter.ToInt32(bytes, data + 4);
        var samples = new float[BitConverter.ToInt32(bytes, data + 4) / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, data + 8 + 2 * i) / 32768f;
        return samples;
    }

    // The 20 ms frames (at 16 kHz) loud enough to be voice, as the gate lets through.
    private static float[] Voiced(float[] x)
    {
        var kept = new List<float>();
        for (var at = 0; at + 320 <= x.Length; at += 320)
            if (x.AsSpan(at, 320).ToArray().Average(v => v * v) > 3e-5f) kept.AddRange(x.AsSpan(at, 320).ToArray());
        return [.. kept];
    }
}
