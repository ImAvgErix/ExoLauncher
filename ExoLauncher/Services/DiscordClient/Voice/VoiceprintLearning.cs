using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Only My Voice keeps learning: the voice taught once (Teach Exo my voice) is the anchor, and in calls every stretch that
/// is clearly the user's (a 1.5 s judgment at <see cref="LearnFrom"/> or over, well above <see cref="MyVoice.Mine"/>) is
/// kept; each <see cref="FoldSeconds"/> of those are averaged and folded into the voiceprint, so it fills in what one
/// short take couldn't (another microphone position, a tired or a loud voice). Learning is anchored: a sample, or the
/// voiceprint it would make, under <see cref="AnchorKeep"/> like what was taught is refused, so a TV or someone else in
/// the room can never become "you", not even a little at a time. What is learned already weighs at most
/// <see cref="MostWeightSeconds"/>, so new speech always counts. All of it stays on this PC, DPAPI-protected.
/// </summary>
internal static class VoiceprintLearning
{
    /// <summary>A 1.5 s judgment this like the voiceprint is clearly the user: kept to learn from.</summary>
    internal const float LearnFrom = 0.70f;
    /// <summary>
    /// A judgment this like the voiceprint, of a voice as loud as the user's at their microphone (VoiceChain.AsLoudAs), is
    /// the user too, and learned from: someone across the room or a TV never reaches a close mic that loud, and every other
    /// voice measured scored at most 0.47. At <see cref="LearnFrom"/> alone the voiceprint never learned in this PC's calls
    /// (the voice a call hears, in a game or tired, rarely scored that like a short teach), so only a teach changed it.
    /// </summary>
    internal const float CloseLearnFrom = 0.50f;
    /// <summary>How like what was taught a sample, and the voiceprint after it, must stay (another man's voice scored at most 0.47).</summary>
    internal const float AnchorKeep = 0.65f;
    /// <summary>Speech averaged into one sample before it is folded in.</summary>
    internal const float FoldSeconds = 30f;
    /// <summary>The most weight what is learned already has against a new sample (5 minutes).</summary>
    internal const float MostWeightSeconds = 300f;

    /// <summary>
    /// The voiceprint as learned: <see cref="Anchor"/> is what was taught, <see cref="Print"/> what calls are judged
    /// against, <see cref="Seconds"/> how much of the user's speech it comes from, <see cref="Updates"/> how many samples
    /// calls have added.
    /// </summary>
    internal sealed record State(float[] Anchor, float[] Print, float Seconds, int Updates);

    /// <summary>
    /// A sample of the user's voice (unit length, from <see cref="FoldSeconds"/> of speech) folded in, or null when it is
    /// refused. <paramref name="close"/>: it holds speech learned for its loudness (<see cref="CloseLearnFrom"/>), so it
    /// may be as far from what was taught as that; the voiceprint after it still keeps to <see cref="AnchorKeep"/>.
    /// </summary>
    internal static State? Fold(State now, float[] sample, float sampleSeconds, bool close = false)
    {
        if (sample.Length != now.Print.Length || !(sampleSeconds > 0)) return null;
        if (Dot(sample, now.Anchor) < (close ? CloseLearnFrom : AnchorKeep)) return null;
        var weight = Math.Min(now.Seconds, MostWeightSeconds);
        // A sample learned for its loudness never outweighs what is learned already, so the voiceprint moves toward the
        // voice calls hear a step at a time (after a 6 s teach, 30 s of it alone swung the voiceprint past AnchorKeep, and
        // it was refused every time).
        var sampleWeight = close ? Math.Min(sampleSeconds, weight) : sampleSeconds;
        var print = new float[now.Print.Length];
        for (var i = 0; i < print.Length; i++) print[i] = now.Print[i] * weight + sample[i] * sampleWeight;
        if (!Normalize(print) || Dot(print, now.Anchor) < AnchorKeep) return null;
        return now with { Print = print, Seconds = now.Seconds + sampleSeconds, Updates = now.Updates + 1 };
    }

    internal static float Dot(float[] a, float[] b)
    {
        float sum = 0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    internal static bool Normalize(float[] v)
    {
        var length = MathF.Sqrt(Dot(v, v));
        if (!(length > 0) || float.IsInfinity(length)) return false;
        for (var i = 0; i < v.Length; i++) v[i] /= length;
        return true;
    }

    // ------------------------------------------------------------------ the file

    private static ReadOnlySpan<byte> Magic => "EXV2"u8;
    /// <summary>What a voiceprint saved before learning (192 numbers alone) is taken to come from: the least a teach takes, and a little.</summary>
    internal const float EarlierSeconds = 5f;

    /// <summary>The state as bytes: EXV2, the length, seconds, updates, then the anchor and the voiceprint.</summary>
    internal static byte[] Encode(State s)
    {
        var bytes = new byte[16 + 8 * s.Print.Length];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), s.Print.Length);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), s.Seconds);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), s.Updates);
        MemoryMarshal.AsBytes(s.Anchor.AsSpan()).CopyTo(bytes.AsSpan(16));
        MemoryMarshal.AsBytes(s.Print.AsSpan()).CopyTo(bytes.AsSpan(16 + 4 * s.Print.Length));
        return bytes;
    }

    /// <summary>The state from its bytes; a voiceprint saved before learning (its numbers alone) is its own anchor. Null when they aren't one.</summary>
    internal static State? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 16 && bytes[..4].SequenceEqual(Magic))
        {
            var dim = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
            if (dim is <= 0 or > 1024 || bytes.Length != 16 + 8 * dim) return null;
            var seconds = BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]);
            var updates = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
            if (!float.IsFinite(seconds) || seconds < 0 || updates < 0) return null;
            var anchor = MemoryMarshal.Cast<byte, float>(bytes.Slice(16, 4 * dim)).ToArray();
            var print = MemoryMarshal.Cast<byte, float>(bytes.Slice(16 + 4 * dim, 4 * dim)).ToArray();
            return new State(anchor, print, seconds, updates);
        }
        if (bytes.Length is > 0 and <= 4096 && bytes.Length % 4 == 0)
        {
            var print = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
            return new State(print, print.ToArray(), EarlierSeconds, 0);
        }
        return null;
    }

    // ------------------------------------------------------------------ learning in calls

    private static readonly object Gate = new();
    private static float[]? _sum;
    private static float _sumSeconds;
    private static bool _sumClose;

    /// <summary>
    /// A 1.5 s embedding from a call and how like the voiceprint it judged: kept when it is clearly the user (or, as loud
    /// as their voice at their mic, <paramref name="close"/>, when it is like enough: <see cref="CloseLearnFrom"/>), and
    /// every <see cref="FoldSeconds"/> of those folded into the voiceprint and saved. The new voiceprint when one was
    /// saved, else null. Off the audio thread (MyVoice's judgment).
    /// </summary>
    internal static float[]? Learn(float[] embedding, float similarity, float seconds, bool close = false)
    {
        if (similarity < (close ? CloseLearnFrom : LearnFrom)) return null;
        float[] sample;
        float sampleSeconds;
        bool sampleClose;
        lock (Gate)
        {
            if (_sum is null || _sum.Length != embedding.Length) (_sum, _sumSeconds, _sumClose) = (new float[embedding.Length], 0, false);
            for (var i = 0; i < embedding.Length; i++) _sum[i] += embedding[i] * seconds;
            _sumSeconds += seconds;
            _sumClose |= similarity < LearnFrom;
            if (_sumSeconds < FoldSeconds) return null;
            (sample, sampleSeconds, sampleClose) = (_sum, _sumSeconds, _sumClose);
            (_sum, _sumSeconds, _sumClose) = (null, 0, false);
        }
        if (!Normalize(sample) || Voiceprint.LoadState() is not { } now) return null;
        if (Fold(now, sample, sampleSeconds, sampleClose) is not { } next)
        {
            AppLog.Info($"Discord voice: kept your voiceprint as it was ({Dot(sample, now.Anchor):0.00} like the voice you taught, under {AnchorKeep:0.00}).");
            return null;
        }
        Voiceprint.Save(next);
        AppLog.Info($"Discord voice: your voiceprint learned from {sampleSeconds:0} s more of your voice ({next.Seconds / 60:0.0} min in all, {next.Updates} updates; {Dot(next.Print, next.Anchor):0.00} like the voice you taught).");
        return next.Print;
    }

    /// <summary>A new teach or Forget: speech kept toward the next sample was judged against the voiceprint before it.</summary>
    internal static void Reset()
    {
        lock (Gate) (_sum, _sumSeconds, _sumClose) = (null, 0, false);
    }
}
