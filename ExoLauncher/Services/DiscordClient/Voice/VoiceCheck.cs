namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Voice Check in Discord settings > Voice & Video: a few seconds from the chosen microphone, run through the
/// input profile's processing exactly as a call runs it (<see cref="VoiceChain"/>: noise suppression at the kept
/// tuning's strength, Voice Focus or the level gate, gain control; what the gate holds back is silence, as the call
/// sends nothing then), and what it sends through Opus as the call encodes it and back (<see cref="SentSound"/>), so
/// "after" is what the other person hears. The page plays both
/// takes, before and after, and shows how the mic is set: its peak and whether it clipped, the room's noise
/// before and after, and the voice's level as sent. Nothing is kept: both takes go to the page as WAV data.
/// </summary>
internal static class VoiceCheck
{
    private const int Rate = Opus.SampleRate, Frame = Opus.FrameSamples;
    private static int _running;

    /// <summary>The last take, cleaned, and which frames the gate let through: Only My Voice learns from it (memory only).</summary>
    internal static (float[] Cleaned, bool[] Voiced)? LastTake { get; private set; }

    internal static async Task<object> RecordAsync(int seconds, CancellationToken ct)
    {
        seconds = Math.Clamp(seconds, 3, 10);
        if (Interlocked.Exchange(ref _running, 1) == 1) return new { ok = false, message = "Voice Check is already recording." };
        try
        {
            var processing = VoiceProcessing.Current;
            var frames = seconds * Rate / Frame;
            var raw = new float[frames * Frame];
            var sent = new float[frames * Frame];
            var quiet = new float[frames * Frame];
            var voiced = new bool[frames];
            var gated = new bool[frames];
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // The call's own chain: the kept tuning for this mic, and its own noise suppression standing in where it does.
            using var chain = new VoiceChain();
            using var codec = new SentSound();
            await Task.Run(() => chain.Prepare(processing), ct).ConfigureAwait(false);
            var count = 0;
            var audio = new VoiceAudio();
            try
            {
                audio.Captured += frame =>
                {
                    if (count >= frames) return;
                    var at = count * Frame;
                    Mono(frame, raw, at);
                    var open = chain.Process(frame, processing, audio.MicId, audio.SystemGain);
                    Mono(frame, quiet, at);
                    if (open)
                    {
                        // The lead-in the call sends ahead of an opening, at its own place.
                        var lead = chain.TakeLead().ToArray();
                        for (var i = 0; i < lead.Length; i++)
                        {
                            if (at - (lead.Length - i) * Frame < 0) continue;
                            codec.Through(lead[i]);
                            Mono(lead[i], sent, at - (lead.Length - i) * Frame);
                        }
                        codec.Through(frame);
                        Mono(frame, sent, at);
                    }
                    voiced[count] = open;
                    gated[count] = chain.Voiced;
                    if (++count == frames) done.TrySetResult();
                };
                audio.Start(capture: true, render: false);
                if (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(seconds + 3), ct)).ConfigureAwait(false) != done.Task)
                    return new { ok = false, message = "The microphone didn't answer. Check it's plugged in and chosen above." };
            }
            finally { audio.Dispose(); }

            LastTake = (quiet, gated);
            var peak = raw.Max(MathF.Abs);
            return new
            {
                ok = true,
                message = (string?)null,
                before = Wav(raw),
                after = Wav(sent),
                peakDb = Db(peak),
                clipped = raw.Count(x => MathF.Abs(x) >= 0.999f),
                noiseDb = Db(Quietest(raw)),
                noiseAfterDb = Db(Quietest(quiet)),
                voiceDb = Db(VoicedRms(quiet, voiced)),
                voicedShare = Math.Round(voiced.Count(v => v) / (double)frames, 2),
            };
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    internal static void Mono(float[] stereo, float[] into, int at)
    {
        for (var i = 0; i < Frame; i++) into[at + i] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f;
    }

    private static double Db(double rms) => Math.Round(20 * Math.Log10(Math.Max(rms, 1e-6)), 1);

    // The room on its own: the quietest 200 ms of the take.
    private static double Quietest(float[] take)
    {
        const int span = Rate / 5;
        var best = double.MaxValue;
        for (var at = 0; at + span <= take.Length; at += span / 2)
        {
            double sum = 0;
            for (var i = at; i < at + span; i++) sum += take[i] * take[i];
            best = Math.Min(best, sum / span);
        }
        return Math.Sqrt(best);
    }

    private static double VoicedRms(float[] take, bool[] voiced)
    {
        double sum = 0;
        var n = 0;
        for (var f = 0; f < voiced.Length; f++)
        {
            if (!voiced[f]) continue;
            for (var i = f * Frame; i < (f + 1) * Frame; i++) sum += take[i] * take[i];
            n += Frame;
        }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }

    // 16-bit mono PCM at 48 kHz, as a data URL the page's audio element plays.
    internal static string Wav(float[] take)
    {
        var data = take.Length * 2;
        var bytes = new byte[44 + data];
        using (var w = new BinaryWriter(new MemoryStream(bytes)))
        {
            w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(data);
            foreach (var x in take) w.Write((short)Math.Round(Math.Clamp(x, -1f, 1f) * 32767f));
        }
        return "data:audio/wav;base64," + Convert.ToBase64String(bytes);
    }
}

/// <summary>
/// What the other person hears of a frame the call sends: Opus as the call's own encoder codes it (<see cref="Opus.NewEncoder"/>,
/// at Discord's 64 kbps default) and decoded again. Voice Check's and Voice Tune's "after" go through it.
/// </summary>
internal sealed class SentSound : IDisposable
{
    private nint _encoder = Opus.NewEncoder(), _decoder = Opus.NewDecoder();
    private readonly float[] _decoded = new float[VoiceAudio.FrameFloats];

    /// <summary>A sent 20 ms stereo frame, in place, as the listener's decoder gives it back.</summary>
    internal void Through(float[] frame)
    {
        Span<byte> opus = stackalloc byte[1275];
        var length = Opus.Encode(_encoder, frame, opus);
        if (length > 0 && Opus.Decode(_decoder, opus[..length], _decoded) > 0) _decoded.CopyTo(frame, 0);
    }

    public void Dispose()
    {
        if (_encoder != 0) Opus.EncoderDestroy(_encoder);
        if (_decoder != 0) Opus.DecoderDestroy(_decoder);
        _encoder = _decoder = 0;
    }
}
