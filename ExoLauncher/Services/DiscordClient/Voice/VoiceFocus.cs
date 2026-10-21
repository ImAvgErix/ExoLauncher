namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Voice Focus, after noise suppression: on each 20 ms frame in place, adding no delay (<see cref="DelaySamples"/>).
/// A 24 dB/octave high-pass at 80 Hz takes out rumble, desk thumps and mains hum's fundamental. The gate listens above
/// 150 Hz in 1 ms chunks and opens for sound that stays over its threshold (12 dB over the noise floor and at least
/// -55 dBFS, or the chosen Input Sensitivity) for 4 ms, so the peaks of steady noise never open it, fully within a
/// millisecond, and from 2 ms before where those are in the same frame (the whole frame is in hand before any of it is sent, so looking within it costs nothing); it holds 250 ms after the last,
/// then fades out over about 150 ms. Noise suppression ahead of it has already taken out the clicks and the keyboard,
/// so it doesn't guess at them. Then the level: Automatic Gain Control's slow rider when it's on, and a limiter that
/// keeps every sample under -1 dBFS (easing in over up to 4 ms within the frame). Mono (both channels get the
/// result), plain float math, nothing allocated per frame.
/// </summary>
internal sealed class VoiceFocus
{
    /// <summary>How far behind its input Voice Focus's output runs, in samples at 48 kHz: none.</summary>
    internal const int DelaySamples = 0;

    private const int Frame = Opus.FrameSamples, Block = 240, Chunk = 48, Chunks = Frame / Chunk, BlockChunks = Block / Chunk;
    private const int Hold = 250, Ahead = 2, Persist = 4, AttackChunks = 4, FloorSpan = 40, FloorSpans = 8; // hold in chunks; the floor in 5 ms blocks
    private const float ClosedGain = 0.01f, Ceiling = 0.891f; // -40 dB closed, -1 dBFS

    private static readonly float FloorMargin = Power(12), AutoMinimum = Power(-55);
    // One-pole rates per sample: the gate opening (0.5 ms) and fading after the hold (40 ms); per 1 ms chunk, the limiter's recovery.
    private static readonly float OpenRate = Rate(0.5f, 1), CloseRate = Rate(40, 1), LimitRelease = Rate(50, Chunk);

    private Biquad _low1 = Biquad.HighPass(80, 0.5412), _low2 = Biquad.HighPass(80, 1.3066), _key = Biquad.HighPass(150, 0.7071);
    private readonly float[] _current = new float[Frame];
    // This frame's 1 ms chunks: energy above 150 Hz, and the limiter's gain at each one's start (and at the frame's end).
    private readonly float[] _energy = new float[Chunks], _knots = new float[Chunks + 1];
    // Which chunks the gate opens for: part of a run over the threshold at least Persist ms long (noise peaks are
    // shorter), from Ahead ms before it; and how long the run at the end of the last frame was.
    private readonly bool[] _heard = new bool[Chunks];
    private int _runIn;
    // The noise floor: the quietest 5 ms of the last 1.6 s, kept as eight 200 ms minimums.
    private readonly float[] _floors = new float[FloorSpans];
    private readonly AutoGain _rider = new();
    private float _floorNow = float.MaxValue, _block, _gate = ClosedGain, _riderGain = 1f, _limit = 1f, _required = 1f;
    private int _hold, _floorChunks, _floorAt;

    internal VoiceFocus() => Array.Fill(_floors, float.MaxValue);

    /// <summary>Muted: the gate shuts, so unmuting starts from closed.</summary>
    internal void Mute()
    {
        _gate = ClosedGain;
        _hold = 0;
        _runIn = 0;
    }

    /// <summary>
    /// Focuses a 20 ms stereo frame in place; whether what comes out holds voice (the gate open, in its hold or fading).
    /// <paramref name="level"/> runs Automatic Gain Control's rider.
    /// </summary>
    internal bool Process(float[] frame, bool automatic, int threshold, bool level)
    {
        var open = automatic ? MathF.Max(Floor() * FloorMargin, AutoMinimum) : Power(threshold);
        double total = 0;
        var voiced = false;
        for (var c = 0; c < Chunks; c++)
        {
            double key = 0;
            for (var n = c * Chunk; n < (c + 1) * Chunk; n++)
            {
                var x = _low2.Run(_low1.Run((frame[2 * n] + frame[2 * n + 1]) * 0.5));
                _current[n] = (float)x;
                total += x * x;
                var k = _key.Run(x);
                key += k * k;
            }
            _energy[c] = (float)(key / Chunk);
            voiced |= _energy[c] > open;
            Listen(_energy[c]);
        }
        _low1.Flush(); _low2.Flush(); _key.Flush();
        var rider = level ? _rider.Next(voiced, 10 * MathF.Log10((float)(total / Frame) + 1e-10f)) : 1f;
        Limit(rider);
        Mark(open);
        return Output(frame, open, rider);
    }

    // The noise floor, fed a chunk at a time and kept per 5 ms block.
    private void Listen(float energy)
    {
        _block += energy;
        if (++_floorChunks % BlockChunks != 0) return;
        _floorNow = MathF.Min(_floorNow, _block / BlockChunks);
        _block = 0;
        if (_floorChunks < FloorSpan * BlockChunks) return;
        _floors[_floorAt] = _floorNow;
        _floorAt = (_floorAt + 1) % FloorSpans;
        _floorNow = float.MaxValue;
        _floorChunks = 0;
    }

    private float Floor()
    {
        var floor = _floorNow;
        foreach (var f in _floors) floor = MathF.Min(floor, f);
        return MathF.Max(floor, 1e-10f);
    }

    // The limiter's gain at each chunk's start, after the rider: no more than this chunk and the one before allow (a
    // chunk's gain eases from its start to its end, so each sample stays under the ceiling), back up over about 50 ms,
    // and reached by a 4 ms ramp down through the chunks before it in this frame. A peak in the frame's first chunk
    // can't reach back into the frame already sent: the gain steps down at its start.
    private void Limit(float rider)
    {
        var riderStep = (rider - _riderGain) / Frame;
        _knots[0] = _limit;
        for (var c = 0; c < Chunks; c++)
        {
            var peak = 0f;
            for (var n = c * Chunk; n < (c + 1) * Chunk; n++) peak = MathF.Max(peak, MathF.Abs(_current[n] * (_riderGain + riderStep * n)));
            var required = peak > Ceiling ? Ceiling / peak : 1f;
            var gain = c == 0
                ? MathF.Min(_knots[0], required)
                : MathF.Min(MathF.Min(required, _required), _knots[c - 1] + (1 - _knots[c - 1]) * LimitRelease);
            _knots[c] = gain;
            for (var j = 1; j <= AttackChunks && c - j >= 0; j++) _knots[c - j] = MathF.Min(_knots[c - j], gain + (1 - gain) * j / (AttackChunks + 1));
            _required = required;
        }
        _knots[Chunks] = _limit = MathF.Min(_required, _knots[Chunks - 1] + (1 - _knots[Chunks - 1]) * LimitRelease);
    }

    // The frame through the rider, the gate and the limiter, to both channels; whether any of it is open.
    private bool Output(float[] frame, float open, float rider)
    {
        var riderStep = (rider - _riderGain) / Frame;
        var loudest = 0f;
        for (var c = 0; c < Chunks; c++)
        {
            if (Heard(c, open)) _hold = Hold;
            else if (_hold > 0) _hold--;
            var (target, rate) = _hold > 0 ? (1f, OpenRate) : (ClosedGain, CloseRate);
            var limit = _knots[c];
            var step = (_knots[c + 1] - limit) / Chunk;
            for (var n = c * Chunk; n < (c + 1) * Chunk; n++)
            {
                _gate += (target - _gate) * rate;
                frame[2 * n] = frame[2 * n + 1] = _current[n] * (_riderGain + riderStep * n) * _gate * limit;
                limit += step;
            }
            loudest = MathF.Max(loudest, _gate);
        }
        _riderGain = rider;
        return loudest > 2 * ClosedGain;
    }

    // Sound over the threshold for at least Persist ms (a run carried over from the last frame counts), from Ahead ms
    // before it. The whole frame is in hand, so looking within it adds no delay; a run that starts in a frame's last
    // few ms opens with the next frame.
    private void Mark(float open)
    {
        Array.Clear(_heard);
        var run = _runIn;
        for (var c = 0; c <= Chunks; c++)
        {
            if (c < Chunks && _energy[c] > open) { run++; continue; }
            if (run >= Persist)
            {
                var start = Math.Max(0, c - Math.Min(run, c) - Ahead);
                for (var j = start; j < c; j++) _heard[j] = true;
            }
            if (c < Chunks) run = 0;
        }
        _runIn = run;
    }

    private bool Heard(int c, float open) => _heard[c];

    private static float Power(float db) => MathF.Pow(10, db / 10);

    private static float Rate(float ms, int samples) => 1 - MathF.Exp(-samples / (ms * Opus.SampleRate / 1000));

    // A biquad in transposed direct form II, in double so an 80 Hz pole at 48 kHz stays exact.
    private struct Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _s1, _s2;

        internal double Run(double x)
        {
            var y = b0 * x + _s1;
            _s1 = b1 * x - a1 * y + _s2;
            _s2 = b2 * x - a2 * y;
            return y;
        }

        // Silence decays the state toward denormals, which are slow: past 1e-30 it is silence.
        internal void Flush()
        {
            if (Math.Abs(_s1) < 1e-30) _s1 = 0;
            if (Math.Abs(_s2) < 1e-30) _s2 = 0;
        }

        // RBJ's high-pass; two at Q 0.5412 and 1.3066 make a 4th-order Butterworth.
        internal static Biquad HighPass(double hz, double q)
        {
            var w = 2 * Math.PI * hz / Opus.SampleRate;
            var cos = Math.Cos(w);
            var a0 = 1 + Math.Sin(w) / (2 * q);
            return new((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - Math.Sin(w) / (2 * q)) / a0);
        }
    }
}
