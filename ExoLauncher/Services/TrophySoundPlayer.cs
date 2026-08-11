using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ExoLauncher.Services;

/// <summary>Plays Exo's compact, bundled achievement-unlock cue.</summary>
internal static class TrophySoundPlayer
{
    private const uint SndSync = 0x0000;
    private const uint SndNodefault = 0x0002;
    private const uint SndMemory = 0x0004;
    internal const string BundledCueRelativePath = @"Assets\Audio\exo-achievement-unlock.wav";
    private static readonly Lazy<byte[]> UnlockCue = new(
        LoadUnlockCue,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static int _isPlaying;

    internal static ReadOnlyMemory<byte> CueForTests => UnlockCue.Value;

    public static void Play()
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
                    // Synchronous playback happens off the UI thread so the managed
                    // buffer remains pinned for the entire native SND_MEMORY call.
                    if (!PlaySound(UnlockCue.Value, IntPtr.Zero, SndSync | SndNodefault | SndMemory))
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

    private static byte[] LoadUnlockCue()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, BundledCueRelativePath);
            var bytes = File.ReadAllBytes(path);
            if (IsExpectedWave(bytes))
                return bytes;

            Helpers.AppLog.Debug("Bundled trophy sound is invalid; using the built-in fallback.");
        }
        catch (Exception ex)
        {
            Helpers.AppLog.Debug("Bundled trophy sound is unavailable; using the built-in fallback: " + ex.Message);
        }

        return BuildWave(
            durationSeconds: 0.52,
            new Tone(523.25, 0.00, 0.25, 0.42),
            new Tone(783.99, 0.06, 0.30, 0.38),
            new Tone(1046.50, 0.14, 0.34, 0.36),
            new Tone(1567.98, 0.20, 0.24, 0.16));
    }

    private static bool IsExpectedWave(byte[] bytes)
    {
        const short bitsPerSample = 16;
        const int minimumDurationMilliseconds = 250;
        const int maximumDurationMilliseconds = 1_250;

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
                    && sampleRate is 44_100 or 48_000
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
        return dataLength >= bytesPerSecond * minimumDurationMilliseconds / 1_000
            && dataLength <= bytesPerSecond * maximumDurationMilliseconds / 1_000;
    }

    private static byte[] BuildWave(double durationSeconds, params Tone[] tones)
    {
        const int sampleRate = 44_100;
        const short channels = 1;
        const short bitsPerSample = 16;
        var sampleCount = (int)Math.Ceiling(sampleRate * durationSeconds);
        var dataLength = sampleCount * sizeof(short);

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

        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var time = sampleIndex / (double)sampleRate;
            var mixed = 0d;
            foreach (var tone in tones)
            {
                var local = time - tone.StartSeconds;
                if (local < 0d || local >= tone.DurationSeconds) continue;
                var attack = Math.Min(1d, local / 0.012d);
                var release = Math.Pow(Math.Max(0d, 1d - (local / tone.DurationSeconds)), 2.2d);
                var fundamental = Math.Sin(2d * Math.PI * tone.FrequencyHz * local);
                var overtone = Math.Sin(4d * Math.PI * tone.FrequencyHz * local) * 0.12d;
                mixed += (fundamental + overtone) * attack * release * tone.Gain;
            }
            var sample = (short)Math.Clamp((int)Math.Round(mixed * 9_500d), short.MinValue, short.MaxValue);
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private readonly record struct Tone(double FrequencyHz, double StartSeconds, double DurationSeconds, double Gain);

    [DllImport("winmm.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);
}
