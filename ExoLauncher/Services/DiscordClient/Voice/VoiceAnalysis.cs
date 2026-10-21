namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>Voice Tune's guided take, part by part, as the microphone gave it (48 kHz mono, before any processing).</summary>
internal sealed record TuneTake(float[] Quiet, float[] Reading, float[] Loud, float[] Soft, float[] Typing);

/// <summary>
/// What Voice Tune sets for one voice on one microphone: how far noise suppression may turn noise down (dB, at most
/// <see cref="NoiseSuppressor.MostDb"/>: more takes parts of the voice with it), and the gain to add (dB) so the loudest
/// words land near −6 dBFS. Spectrum is the voice's own tone (dB per band, <see cref="VoiceAnalysis.Bands"/>,
/// relative to its 500 Hz band), kept for reference: Exo doesn't correct tone, since a voice's natural tone
/// differs from the average as much as a coloured mic does, so a correction would change good voices.
/// </summary>
internal sealed record VoiceTuning(int SuppressionLimitDb, float GainDb, float[] Spectrum);

/// <summary>What Voice Tune heard, in dB (full scale unless said), and what it sets from that.</summary>
internal sealed record VoiceReport(
    bool Heard, float RoomDb, int? HumHz, bool BandLimited, float PeakDb, int Clipped,
    float SpeechDb, float SoftDb, float LoudDb, float PitchHz, int Pops, float TypingDb, VoiceTuning Tuning);

/// <summary>
/// Voice Tune's listening: one guided take measured offline (it runs once, so it can afford to look closely).
/// The room from the quiet part (its level, mains hum by its 50 or 60 Hz harmonics), the voice from the reading
/// (its active level as ITU-T P.56 measures speech, so a voice only a little over a noisy room still counts; its pitch by autocorrelation;
/// its tone in octave bands; its pops), the headroom from the
/// loud words, the soft ones, and the keyboard from the typing. Everything it sets is
/// bounded: it fixes what the mic and room do, never the voice itself.
/// </summary>
internal static class VoiceAnalysis
{
    internal const int Rate = 48000;
    internal static readonly int[] Bands = [125, 250, 500, 1000, 2000, 4000, 8000];
    private const int Frame = Rate / 50, Window = Rate / 10;

    internal static VoiceReport Analyze(TuneTake take)
    {
        var room = Floor(take.Quiet);
        var roomDb = Db(room);
        var reading = Speech(take.Reading, room);
        var heard = reading.Count >= 50; // a second of speech at the least
        var speech = heard ? Db(Power(take.Reading, reading)) : roomDb;
        var soft = Db(Power(take.Soft, Speech(take.Soft, room)));
        var loudPeak = Db(Peak(take.Loud));
        var peak = Db(Math.Max(Peak(take.Reading), Peak(take.Loud)));
        var clipped = Clips(take.Reading) + Clips(take.Loud) + Clips(take.Soft);
        var pitch = heard ? Pitch(take.Reading, reading) : 0f;
        var spectrum = heard ? Spectrum(take.Reading, reading) : new float[Bands.Length];
        var bandLimited = heard && BandLimited(take.Reading, reading);
        var pops = heard ? Pops(take.Reading, reading) : 0;
        var hum = Hum(take.Quiet);
        var typing = Db(Peak(VoiceBand(take.Typing), Rate / 200));

        var gain = Math.Clamp(-6 - loudPeak, -20f, 20f);
        if (clipped > 0) gain = Math.Min(gain, -6f);
        // A quiet room needs less cutting, and less cutting sounds more natural; a noisy one gets more, never all of it
        // (cut without a limit, the voice goes with the noise and sounds muffled).
        var snr = speech - roomDb;
        // Typing and clicks aren't in the room's floor, so even a quiet room keeps enough to take them out.
        var limit = snr >= 50 ? 24 : snr >= 40 ? 27 : snr >= 30 ? NoiseSuppressor.GentleDb : NoiseSuppressor.MostDb;
        var tuning = new VoiceTuning(limit, MathF.Round(gain, 1), spectrum);
        return new VoiceReport(heard, R(roomDb), hum, bandLimited, R(peak), clipped, R(speech), R(soft), R(loudPeak), R(pitch),
            pops, R(typing), tuning);
    }

    private static float R(float v) => float.IsFinite(v) ? MathF.Round(v, 1) : -120f;
    private static float Db(double rms) => (float)(20 * Math.Log10(Math.Max(rms, 1e-6)));

    // The room: the median of its 100 ms windows (a stray sound in the quiet doesn't count).
    private static float Floor(float[] x)
    {
        var levels = new List<double>();
        for (var at = 0; at + Window <= x.Length; at += Window) levels.Add(Rms(x, at, Window));
        if (levels.Count == 0) return 1e-5f;
        levels.Sort();
        return (float)levels[levels.Count / 2];
    }

    private static double Rms(float[] x, int at, int n)
    {
        double sum = 0;
        for (var i = at; i < at + n; i++) sum += x[i] * x[i];
        return Math.Sqrt(sum / n);
    }

    // The 20 ms frames that hold speech, as ITU-T P.56 finds them: loudest first, each frame kept while it is within
    // 15.9 dB of the active level of those kept before it, and only frames 4 dB over the room at all.
    private static List<int> Speech(float[] x, double room)
    {
        var floor = room * room * Math.Pow(10, 0.4);
        var frames = new List<(int Frame, double Power)>();
        for (var f = 0; (f + 1) * Frame <= x.Length; f++)
        {
            var rms = Rms(x, f * Frame, Frame);
            if (rms * rms > floor) frames.Add((f, rms * rms));
        }
        frames.Sort((a, b) => b.Power.CompareTo(a.Power));
        double sum = 0;
        var kept = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            sum += frames[i].Power;
            if (10 * Math.Log10(sum / (i + 1) / frames[i].Power) > 15.9) break;
            kept = i + 1;
        }
        return frames.Take(kept).Select(f => f.Frame).Order().ToList();
    }

    private static double Power(float[] x, List<int> frames)
    {
        if (frames.Count == 0) return 0;
        double sum = 0;
        foreach (var f in frames) for (var i = f * Frame; i < (f + 1) * Frame; i++) sum += x[i] * x[i];
        return Math.Sqrt(sum / (frames.Count * Frame));
    }

    private static float Peak(float[] x) => x.Length == 0 ? 0 : x.Max(MathF.Abs);

    // The loudest short block (keyboard clicks are a few ms).
    private static double Peak(float[] x, int block)
    {
        double best = 0;
        for (var at = 0; at + block <= x.Length; at += block / 2) best = Math.Max(best, Rms(x, at, block));
        return best;
    }

    private static int Clips(float[] x) => x.Count(v => MathF.Abs(v) >= 0.999f);

    private static float[] VoiceBand(float[] x) => Filter(Filter(x, Biquad.HighPass(150)), Biquad.LowPass(4000));

    private static float[] Filter(float[] x, Biquad b)
    {
        var y = new float[x.Length];
        for (var i = 0; i < x.Length; i++) y[i] = (float)b.Run(x[i]);
        return y;
    }

    // Each band's power over the speech, in dB relative to the 500 Hz band.
    private static float[] Spectrum(float[] x, List<int> frames)
    {
        var levels = Bands.Select(hz => Db(Power(Filter(x, Biquad.BandPass(hz, 1.41)), frames))).ToArray();
        var reference = levels[Array.IndexOf(Bands, 500)];
        return levels.Select(l => MathF.Round(l - reference, 1)).ToArray();
    }

    // Nothing above about 8 kHz while 1–2 kHz is strong: a Bluetooth hands-free or 16 kHz format.
    private static bool BandLimited(float[] x, List<int> frames)
    {
        var top = Db(Power(Filter(Filter(x, Biquad.HighPass(10000)), Biquad.HighPass(10000)), frames));
        var mid = Db(Power(Filter(x, Biquad.BandPass(1500, 1.41)), frames));
        return top < mid - 55;
    }

    // Pops: speech frames whose energy under 100 Hz jumps 15 dB over the speech's usual and outweighs its 300 Hz band.
    private static int Pops(float[] x, List<int> frames)
    {
        var low = Filter(Filter(x, Biquad.LowPass(100)), Biquad.LowPass(100));
        var body = Filter(x, Biquad.BandPass(300, 1.41));
        var levels = frames.Select(f => (Low: Db(Rms(low, f * Frame, Frame)), Body: Db(Rms(body, f * Frame, Frame)))).ToArray();
        if (levels.Length == 0) return 0;
        var usual = levels.Select(l => l.Low).Order().ElementAt(levels.Length / 2);
        var pops = 0;
        for (var i = 0; i < levels.Length; i++)
            if (levels[i].Low > usual + 15 && levels[i].Low > levels[i].Body && (i == 0 || levels[i - 1].Low <= usual + 15)) pops++;
        return pops;
    }

    // Mains hum: the first four harmonics of 50 or 60 Hz standing 10 dB over the room just beside them.
    private static int? Hum(float[] x)
    {
        if (x.Length < Rate) return null;
        double Score(int mains)
        {
            double on = 0, off = 0;
            for (var k = 1; k <= 4; k++)
            {
                on += Goertzel(x, mains * k);
                off += (Goertzel(x, mains * k - 7) + Goertzel(x, mains * k + 7)) / 2;
            }
            return 10 * Math.Log10(Math.Max(on, 1e-20) / Math.Max(off, 1e-20));
        }
        var fifty = Score(50);
        var sixty = Score(60);
        var best = Math.Max(fifty, sixty);
        return best < 10 ? null : fifty >= sixty ? 50 : 60;
    }

    private static double Goertzel(float[] x, double hz)
    {
        var w = 2 * Math.PI * hz / Rate;
        var c = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        foreach (var v in x)
        {
            var s0 = v + c * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return (s1 * s1 + s2 * s2 - c * s1 * s2) / x.Length;
    }

    // The voice's pitch: the median, over speech frames, of the strongest autocorrelation lag between 60 and 400 Hz.
    private static float Pitch(float[] x, List<int> frames)
    {
        const int n = Rate / 25, minLag = Rate / 400, maxLag = Rate / 60;
        var low = Filter(x, Biquad.LowPass(1000));
        var pitches = new List<float>();
        foreach (var f in frames.Where((_, i) => i % 3 == 0))
        {
            var at = f * Frame;
            if (at + n + maxLag > low.Length) continue;
            double energy = 0;
            for (var i = at; i < at + n; i++) energy += low[i] * low[i];
            if (energy <= 0) continue;
            double best = 0;
            var lag = 0;
            for (var l = minLag; l <= maxLag; l++)
            {
                double sum = 0;
                for (var i = at; i < at + n; i++) sum += low[i] * low[i + l];
                if (sum > best) { best = sum; lag = l; }
            }
            if (lag > 0 && best / energy > 0.5) pitches.Add((float)Rate / lag);
        }
        if (pitches.Count == 0) return 0;
        pitches.Sort();
        return pitches[pitches.Count / 2];
    }

    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        internal double Run(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }

        // RBJ cookbook filters.
        private static Biquad Make(double b0, double b1, double b2, double a0, double a1, double a2) =>
            new() { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };

        internal static Biquad HighPass(double hz, double q = 0.7071)
        {
            var w = 2 * Math.PI * hz / Rate; var a = Math.Sin(w) / (2 * q); var c = Math.Cos(w);
            return Make((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + a, -2 * c, 1 - a);
        }

        internal static Biquad LowPass(double hz, double q = 0.7071)
        {
            var w = 2 * Math.PI * hz / Rate; var a = Math.Sin(w) / (2 * q); var c = Math.Cos(w);
            return Make((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + a, -2 * c, 1 - a);
        }

        internal static Biquad BandPass(double hz, double q)
        {
            var w = 2 * Math.PI * hz / Rate; var a = Math.Sin(w) / (2 * q); var c = Math.Cos(w);
            return Make(a, 0, -a, 1 + a, -2 * c, 1 - a);
        }
    }
}
