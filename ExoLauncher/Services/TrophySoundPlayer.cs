using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ExoLauncher.Services;

/// <summary>
/// Plays an original rarity-specific unlock cue. Bronze/silver/gold/platinum
/// escalate in register and note count, not in volume.
/// </summary>
internal static class TrophySoundPlayer
{
    private const uint SndSync = 0x0000;
    private const uint SndNodefault = 0x0002;
    private const uint SndMemory = 0x0004;
    private const uint SndFilename = 0x00020000;
    internal const string BundledCueRelativePath = @"Assets\Audio\exo-trophy-gold.wav";
    private static readonly Lazy<byte[]> BronzeCue = new(() => LoadCue(TrophyRarity.Bronze), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<byte[]> SilverCue = new(() => LoadCue(TrophyRarity.Silver), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<byte[]> GoldCue = new(() => LoadCue(TrophyRarity.Gold), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<byte[]> PlatinumCue = new(() => LoadCue(TrophyRarity.Platinum), LazyThreadSafetyMode.ExecutionAndPublication);
    private static int _isPlaying;

    internal static ReadOnlyMemory<byte> CueForTests => GoldCue.Value;

    internal static ReadOnlyMemory<byte> CueForTestsOf(TrophyRarity rarity) => Cue(rarity);

    internal static string RelativePathFor(TrophyRarity rarity) => rarity switch
    {
        TrophyRarity.Bronze => @"Assets\Audio\exo-trophy-bronze.wav",
        TrophyRarity.Silver => @"Assets\Audio\exo-trophy-silver.wav",
        TrophyRarity.Platinum => @"Assets\Audio\exo-trophy-platinum.wav",
        _ => BundledCueRelativePath,
    };

    public static void Play() => Play(TrophyRarity.Gold);

    public static void Play(TrophyRarity rarity)
    {
        // Presenter requests are serialized, but this guard also protects direct
        // preview calls and future callers from stacking the reward sound.
        if (Interlocked.CompareExchange(ref _isPlaying, 1, 0) != 0)
            return;

        try
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var path = Path.Combine(AppContext.BaseDirectory, RelativePathFor(rarity));
                    var played = File.Exists(path) &&
                                 PlaySoundPath(path, IntPtr.Zero, SndSync | SndNodefault | SndFilename);
                    if (!played && !PlaySoundMemory(Cue(rarity), IntPtr.Zero, SndSync | SndNodefault | SndMemory))
                        Helpers.AppLog.Debug("Trophy sound was not accepted by Windows.");
                }
                catch (Exception ex)
                {
                    Helpers.AppLog.Debug("Trophy sound failed: " + ex.Message);
                }
                finally
                {
                    Volatile.Write(ref _isPlaying, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _isPlaying, 0);
            Helpers.AppLog.Debug("Trophy sound could not be scheduled: " + ex.Message);
        }
    }

    private static byte[] Cue(TrophyRarity rarity) => rarity switch
    {
        TrophyRarity.Bronze => BronzeCue.Value,
        TrophyRarity.Silver => SilverCue.Value,
        TrophyRarity.Platinum => PlatinumCue.Value,
        _ => GoldCue.Value,
    };

    private static byte[] LoadCue(TrophyRarity rarity)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, RelativePathFor(rarity));
            if (!File.Exists(path) && rarity == TrophyRarity.Gold)
                path = Path.Combine(AppContext.BaseDirectory, @"Assets\Audio\exo-achievement-unlock.wav");
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                if (IsExpectedWave(bytes, rarity))
                    return bytes;
            }

            Helpers.AppLog.Debug("Bundled trophy sound is invalid; using the built-in fallback.");
        }
        catch (Exception ex)
        {
            Helpers.AppLog.Debug("Bundled trophy sound is unavailable; using the built-in fallback: " + ex.Message);
        }

        return BuildWave(rarity);
    }

    private static bool IsExpectedWave(byte[] bytes, TrophyRarity rarity)
    {
        const short bitsPerSample = 16;
        var (minMs, maxMs) = DurationRange(rarity);

        if (bytes.Length < 44
            || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF"
            || BitConverter.ToInt32(bytes, 4) != bytes.Length - 8
            || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            return false;

        short channels = 0;
        var sampleRate = 0;
        var formatIsValid = false;
        var dataLength = 0;
        for (var offset = 12; offset <= bytes.Length - 8;)
        {
            var chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            var chunkLength = BitConverter.ToInt32(bytes, offset + 4);
            var chunkData = offset + 8;
            if (chunkLength < 0 || chunkData > bytes.Length - chunkLength)
                return false;

            if (chunkId == "fmt " && chunkLength >= 16)
            {
                channels = BitConverter.ToInt16(bytes, chunkData + 2);
                sampleRate = BitConverter.ToInt32(bytes, chunkData + 4);
                var blockAlign = BitConverter.ToInt16(bytes, chunkData + 12);
                formatIsValid = BitConverter.ToInt16(bytes, chunkData) == 1
                    && channels is 1 or 2
                    && sampleRate == 48_000
                    && BitConverter.ToInt32(bytes, chunkData + 8) == sampleRate * channels * (bitsPerSample / 8)
                    && blockAlign == channels * (bitsPerSample / 8)
                    && BitConverter.ToInt16(bytes, chunkData + 14) == bitsPerSample;
            }
            else if (chunkId == "data")
            {
                dataLength = chunkLength;
            }

            var paddedLength = chunkLength + (chunkLength & 1);
            offset = chunkData + paddedLength;
        }

        if (!formatIsValid || dataLength <= 0)
            return false;

        var bytesPerSecond = sampleRate * channels * (bitsPerSample / 8);
        return dataLength >= bytesPerSecond * minMs / 1_000
            && dataLength <= bytesPerSecond * maxMs / 1_000;
    }

    internal static (int MinMs, int MaxMs) DurationRange(TrophyRarity rarity) => rarity switch
    {
        TrophyRarity.Bronze => (420, 720),
        TrophyRarity.Silver => (620, 920),
        TrophyRarity.Platinum => (1_050, 1_280),
        _ => (880, 1_180),
    };

    internal static byte[] BuildWave() => BuildWave(TrophyRarity.Gold);

    internal static byte[] BuildWave(TrophyRarity rarity)
    {
        const int sampleRate = 48_000;
        const short channels = 2;
        const short bitsPerSample = 16;
        var spec = Voice(rarity);
        var frameCount = (int)Math.Ceiling(sampleRate * spec.DurationSeconds);
        var left = new double[frameCount];
        var right = new double[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var time = frame / (double)sampleRate;
            var mixed = 0d;
            foreach (var note in spec.Notes)
            {
                var local = time - note.Start;
                if (local < 0d || local >= note.Duration) continue;
                mixed += Strike(local, note, spec.FadeIn, spec.FadeOut);
            }

            var width = spec.Stereo;
            left[frame] = mixed * (1d + width);
            right[frame] = mixed * (1d - width);
        }

        RemoveDc(left);
        RemoveDc(right);
        Normalize(left, right, targetRms: 2_200d, peakLimit: 20_500d);

        var dataLength = frameCount * channels * sizeof(short);
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (var frame = 0; frame < frameCount; frame++)
        {
            writer.Write(ToPcm16(left[frame]));
            writer.Write(ToPcm16(right[frame]));
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static VoiceSpec Voice(TrophyRarity rarity) => rarity switch
    {
        TrophyRarity.Bronze => new VoiceSpec(0.56, 0.018, 0.14, 0.010,
        [
            new(196.0, 0.00, 0.50, 0.58),
            new(293.7, 0.05, 0.44, 0.32),
        ]),
        TrophyRarity.Silver => new VoiceSpec(0.76, 0.018, 0.16, 0.014,
        [
            new(261.6, 0.00, 0.64, 0.48),
            new(392.0, 0.08, 0.58, 0.34),
            new(523.3, 0.18, 0.42, 0.18),
        ]),
        TrophyRarity.Platinum => new VoiceSpec(1.16, 0.020, 0.24, 0.018,
        [
            new(261.6, 0.00, 1.02, 0.30),
            new(329.6, 0.03, 0.98, 0.26),
            new(392.0, 0.06, 0.92, 0.22),
            new(523.3, 0.12, 0.78, 0.16),
            new(659.3, 0.56, 0.38, 0.10),
        ]),
        _ => new VoiceSpec(1.02, 0.018, 0.18, 0.016,
        [
            new(392.0, 0.00, 0.82, 0.40),
            new(493.9, 0.08, 0.74, 0.30),
            new(587.3, 0.16, 0.64, 0.22),
        ]),
    };

    private static double Strike(double local, Note note, double fadeIn, double fadeOut)
    {
        var env = SmoothEnvelope(local, note.Duration, fadeIn, fadeOut);
        if (env <= 0d) return 0d;
        var tone = Math.Sin(2d * Math.PI * note.Hz * local)
                   + 0.16d * Math.Sin(2d * Math.PI * note.Hz * 2.003d * local)
                   + 0.06d * Math.Sin(2d * Math.PI * note.Hz * 3.01d * local);
        return tone * env * note.Gain;
    }

    private static double SmoothEnvelope(double time, double duration, double fadeIn, double fadeOut)
    {
        if (time < 0d || time >= duration) return 0d;
        var attack = fadeIn <= 0d ? 1d : RaisedCosine(Math.Min(1d, time / fadeIn));
        var remain = duration - time;
        var release = remain >= fadeOut || fadeOut <= 0d ? 1d : RaisedCosine(remain / fadeOut);
        var body = Math.Exp(-1.55d * time / duration);
        return attack * release * body;
    }

    private static double RaisedCosine(double t) => 0.5d * (1d - Math.Cos(Math.PI * Math.Clamp(t, 0d, 1d)));

    private static void RemoveDc(double[] samples)
    {
        if (samples.Length == 0) return;
        var mean = samples.Average();
        for (var i = 0; i < samples.Length; i++) samples[i] -= mean;
    }

    private static void Normalize(double[] left, double[] right, double targetRms, double peakLimit)
    {
        var sumSquares = 0d;
        var peak = 0d;
        for (var i = 0; i < left.Length; i++)
        {
            sumSquares += left[i] * left[i] + right[i] * right[i];
            peak = Math.Max(peak, Math.Abs(left[i]));
            peak = Math.Max(peak, Math.Abs(right[i]));
        }

        var count = left.Length * 2;
        var rms = count == 0 ? 0d : Math.Sqrt(sumSquares / count);
        var gain = rms > 1e-9 ? targetRms / rms : 0d;
        if (peak * gain > peakLimit && peak > 1e-9)
            gain = peakLimit / peak;
        for (var i = 0; i < left.Length; i++)
        {
            left[i] *= gain;
            right[i] *= gain;
        }

        if (left.Length > 0)
        {
            left[0] = 0d;
            right[0] = 0d;
            left[^1] = 0d;
            right[^1] = 0d;
        }
    }

    private static short ToPcm16(double sample) =>
        (short)Math.Clamp((int)Math.Round(sample), short.MinValue, short.MaxValue);

    private readonly record struct Note(double Hz, double Start, double Duration, double Gain);

    private readonly record struct VoiceSpec(double DurationSeconds, double FadeIn, double FadeOut, double Stereo, Note[] Notes);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundPath(string sound, IntPtr module, uint flags);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundMemory(byte[] sound, IntPtr module, uint flags);
}
