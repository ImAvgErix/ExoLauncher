using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

// Ways Only My Voice's learning could fail silently: someone else's voice (a TV, a roommate) is folded in and becomes
// "you"; it drifts there a little at a time, each step close to the last; what is learned outweighs new speech for good,
// so it stops getting better; a judgment that only just passes is learned from; a voiceprint saved before learning no
// longer loads (Only My Voice quietly off), or loads as if from no speech, so the first sample replaces it whole; a new
// teach keeps speech judged against the voiceprint before it.
[Collection("DeepFilterNet")]
public sealed class VoiceprintLearningTests
{
    private const int Dim = 192;

    // A unit vector along `toward` from `from`, at the given cosine to `from` (both unit, `toward` orthogonal to `from`).
    private static float[] At(float[] from, float[] toward, float cosine)
    {
        var sin = MathF.Sqrt(1 - cosine * cosine);
        return [.. from.Select((x, i) => cosine * x + sin * toward[i])];
    }

    private static float[] Axis(int i)
    {
        var v = new float[Dim];
        v[i] = 1;
        return v;
    }

    private static VoiceprintLearning.State Taught(float seconds = 6) => new(Axis(0), Axis(0), seconds, 0);

    [Fact]
    public void ASampleOfTheSameVoiceMovesTheVoiceprintTowardIt()
    {
        var sample = At(Axis(0), Axis(1), 0.9f);
        var next = VoiceprintLearning.Fold(Taught(), sample, 30)!;
        Assert.Equal(36, next.Seconds);
        Assert.Equal(1, next.Updates);
        Assert.True(VoiceprintLearning.Dot(next.Print, sample) > VoiceprintLearning.Dot(Taught().Print, sample));
        Assert.Equal(Axis(0), next.Anchor); // what was taught stays as it was
        Assert.InRange(VoiceprintLearning.Dot(next.Print, next.Print), 0.999f, 1.001f);
    }

    [Fact]
    public void AnotherVoiceIsNeverFoldedIn()
    {
        // Another man's voice scored at most 0.47 against a voiceprint: far under the anchor's 0.65.
        var other = At(Axis(0), Axis(1), 0.47f);
        Assert.Null(VoiceprintLearning.Fold(Taught(), other, 30));
    }

    [Fact]
    public void YourVoiceAsLoudAsYoursTeachesWhatAShortTeachMissed()
    {
        // The voice a call hears (in a game, tired) at 0.52 like what was taught: folded in when it was as loud as the
        // user at their mic, never when it wasn't; another man's (0.47) never, loud or not.
        var calls = At(Axis(0), Axis(1), 0.52f);
        Assert.Null(VoiceprintLearning.Fold(Taught(), calls, 30));
        var next = VoiceprintLearning.Fold(Taught(), calls, 30, close: true)!;
        Assert.True(VoiceprintLearning.Dot(next.Print, calls) > VoiceprintLearning.Dot(Taught().Print, calls));
        Assert.True(VoiceprintLearning.Dot(next.Print, next.Anchor) >= VoiceprintLearning.AnchorKeep);
        Assert.Null(VoiceprintLearning.Fold(Taught(), At(Axis(0), Axis(1), 0.47f), 30, close: true));
    }

    [Fact]
    public void ItCannotDriftToSomeoneElseAStepAtATime()
    {
        // Each sample is clearly like the voiceprint as it stands (0.9) but leans further from what was taught.
        var state = Taught();
        for (var step = 0; step < 200; step++)
        {
            var sample = At(state.Print, OrthogonalTo(state.Print, Axis(1)), 0.9f);
            state = VoiceprintLearning.Fold(state, sample, 30) ?? state;
        }
        Assert.True(VoiceprintLearning.Dot(state.Print, state.Anchor) >= VoiceprintLearning.AnchorKeep,
            $"drifted to {VoiceprintLearning.Dot(state.Print, state.Anchor):0.00} like what was taught");
    }

    private static float[] OrthogonalTo(float[] v, float[] direction)
    {
        var d = VoiceprintLearning.Dot(v, direction);
        var o = direction.Select((x, i) => x - d * v[i]).ToArray();
        VoiceprintLearning.Normalize(o);
        return o;
    }

    [Fact]
    public void NewSpeechStillCountsAfterHoursOfIt()
    {
        var sample = At(Axis(0), Axis(1), 0.9f);
        var fresh = VoiceprintLearning.Fold(Taught(VoiceprintLearning.MostWeightSeconds), sample, 30)!;
        var veteran = VoiceprintLearning.Fold(Taught(10 * 3600), sample, 30)!;
        // Weight is capped: ten hours learned moves exactly as far as five minutes.
        Assert.Equal(VoiceprintLearning.Dot(fresh.Print, sample), VoiceprintLearning.Dot(veteran.Print, sample), 4);
        Assert.True(VoiceprintLearning.Dot(veteran.Print, sample) > VoiceprintLearning.Dot(Axis(0), sample));
    }

    [Fact]
    public void TheStateReadBackIsTheOneSavedAndAnEarlierVoiceprintIsItsOwnAnchor()
    {
        var state = new VoiceprintLearning.State(Axis(0), At(Axis(0), Axis(1), 0.8f), 150, 4);
        var back = VoiceprintLearning.Decode(VoiceprintLearning.Encode(state))!;
        Assert.Equal(state.Anchor, back.Anchor);
        Assert.Equal(state.Print, back.Print);
        Assert.Equal(150, back.Seconds);
        Assert.Equal(4, back.Updates);

        // Before learning, the file held the voiceprint's numbers alone.
        var earlier = System.Runtime.InteropServices.MemoryMarshal.AsBytes(Axis(3).AsSpan()).ToArray();
        var old = VoiceprintLearning.Decode(earlier)!;
        Assert.Equal(Axis(3), old.Anchor);
        Assert.Equal(Axis(3), old.Print);
        Assert.Equal(VoiceprintLearning.EarlierSeconds, old.Seconds);
        Assert.Null(VoiceprintLearning.Decode([1, 2, 3])); // not a voiceprint
    }

    [Fact]
    public void CallsLearnOnlyFromClearJudgmentsAndFoldEveryThirtySeconds()
    {
        var path = Path.Combine(Path.GetTempPath(), "exo-tests-" + Guid.NewGuid().ToString("N"), "my-voice.bin");
        var before = Voiceprint.FilePath;
        Voiceprint.FilePath = path;
        VoiceprintLearning.Reset();
        try
        {
            Voiceprint.Save(Taught());
            var mine = At(Axis(0), Axis(1), 0.9f);
            // Only just the user's (over Mine, under LearnFrom): judged, never learned from.
            for (var i = 0; i < 40; i++) Assert.Null(VoiceprintLearning.Learn(mine, VoiceprintLearning.LearnFrom - 0.01f, 1.5f));
            Assert.Equal(0, Voiceprint.LoadState()!.Updates);
            // Clearly the user's: nineteen windows (28.5 s) wait, the twentieth folds them in and saves.
            for (var i = 0; i < 19; i++) Assert.Null(VoiceprintLearning.Learn(mine, 0.9f, 1.5f));
            var learned = VoiceprintLearning.Learn(mine, 0.9f, 1.5f);
            Assert.NotNull(learned);
            var saved = Voiceprint.LoadState()!;
            Assert.Equal(1, saved.Updates);
            Assert.Equal(36, saved.Seconds);
            Assert.Equal(learned, saved.Print);
            // A new teach drops what was kept toward the next sample.
            for (var i = 0; i < 10; i++) VoiceprintLearning.Learn(mine, 0.9f, 1.5f);
            VoiceprintLearning.Reset();
            for (var i = 0; i < 19; i++) Assert.Null(VoiceprintLearning.Learn(mine, 0.9f, 1.5f));
            // A judgment under LearnFrom counts when the voice was as loud as the user's (close), and only from CloseLearnFrom.
            VoiceprintLearning.Reset();
            for (var i = 0; i < 40; i++) Assert.Null(VoiceprintLearning.Learn(mine, VoiceprintLearning.CloseLearnFrom - 0.01f, 1.5f, close: true));
            for (var i = 0; i < 19; i++) Assert.Null(VoiceprintLearning.Learn(mine, 0.55f, 1.5f, close: true));
            Assert.NotNull(VoiceprintLearning.Learn(mine, 0.55f, 1.5f, close: true));
        }
        finally
        {
            VoiceprintLearning.Reset();
            Voiceprint.FilePath = before;
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
