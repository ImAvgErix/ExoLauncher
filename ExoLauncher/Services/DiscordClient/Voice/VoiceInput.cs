using System.Runtime.InteropServices;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// What Discord settings > Voice & Video asks of the microphone: Exo Voice, everything Exo has tuned together
/// (DeepFilterNet noise suppression, echo cancellation, Voice Focus with its leveler,
/// automatic sensitivity), or Custom (each choice the user's own). Earlier profiles (Voice Isolation, Studio)
/// are Exo Voice now.
/// </summary>
internal readonly record struct VoiceProcessing(string Profile, bool NoiseSuppression, bool EchoCancellation,
    bool PushToTalk, bool AutoSensitivity, int Threshold, int PttDelay, bool Focus, bool OnlyMyVoice = false, string Suppressor = "ai")
{
    internal static VoiceProcessing Current => From(App.Services.Settings.Shared);

    /// <summary>
    /// Exo Voice, kept simple: noise suppression at one strength, Windows' echo cancellation, and Discord's own automatic
    /// gate. No Voice Focus, no gain riding and no tuning: each added delay or changed how a voice sounds.
    /// </summary>
    internal static readonly VoiceProcessing ExoVoice = new("exo", true, true, false, true, -60, 0, false);

    /// <summary>Exo Voice is on: the best of everything at once, so nothing else needs choosing (noise
    /// suppression's on switch is that switch; an earlier build's kind becomes Exo Voice too).</summary>
    internal static bool ExoOn(Models.AppSettings s) => s.DiscordNoiseSuppression;

    // Exo Voice, the whole smart preset: Exo's own AI (the tuned DeepFilterNet chain), Exo's echo
    // cancellation (on speakers only: Windows' smeared voices), the automatic gate read by DeepFilterNet's own VAD, and
    // the voice's shape and leveler; everyone else in the call is cleaned and evened out too (see ExoOn, where inbound
    // features read it). Standard is the plainer choice, run plain: no Exo stages. With suppression off, one set of
    // switches: echo cancellation, the noise gate (Input Sensitivity) and gain control (Exo's own). No Voice Focus.
    internal static VoiceProcessing From(Models.AppSettings s) => !s.DiscordNoiseSuppression
        ? new("custom", false, s.DiscordEchoCancellation,
            s.DiscordPushToTalk, s.DiscordAutoSensitivity, s.DiscordSensitivity, s.DiscordPttDelay, Focus: false,
            OnlyMyVoice: s.DiscordOnlyMyVoice && Voiceprint.Learned,
            Suppressor: s.DiscordNoiseSuppressionKind)
        : s.DiscordNoiseSuppressionKind is "standard"
        ? new("custom", true, s.DiscordEchoCancellation,
            s.DiscordPushToTalk, s.DiscordAutoSensitivity, s.DiscordSensitivity, s.DiscordPttDelay, Focus: false,
            OnlyMyVoice: s.DiscordOnlyMyVoice && Voiceprint.Learned,
            Suppressor: s.DiscordNoiseSuppressionKind)
        : new("exo", true, true, s.DiscordPushToTalk, true, s.DiscordSensitivity, s.DiscordPttDelay, Focus: false,
            // Exo Voice's Only My Voice is the learned voiceprint. "ai" (Custom's AI) reads as itself; any other kind,
            // an earlier build's included, is Exo Voice.
            OnlyMyVoice: s.DiscordOnlyMyVoice && Voiceprint.Learned,
            Suppressor: s.DiscordNoiseSuppressionKind is "ai" ? "ai" : "exo");
}

/// <summary>
/// Voice detection on 20 ms frames: automatic (the frame's level against a slowly followed noise
/// floor) or a fixed threshold in dB, held 300 ms after the voice falls, as Discord's own gate.
/// <see cref="Level"/> is the last frame's level for the Input Sensitivity meter. With noise suppression on, it is
/// handed the cleaned frame, so the automatic gate, like Discord's with Krisp (its VAD reads Krisp's verdict), judges
/// what suppression left, not the raw microphone.
/// </summary>
internal sealed class VoiceGate
{
    /// <summary>DeepFilterNet's local SNR (dB) over which a frame is voice whatever its level says.</summary>
    internal const float SpeechDb = 10f;

    /// <summary>
    /// Frames the gate stays open after the last voice (500 ms, Discord's vadTrailing of 25 frames).
    /// </summary>
    internal const int Hold = 25;

    private float _noiseFloor = -60f;
    // After noise suppression: what comes out between words (the cleaned floor) and what the microphone hears all the
    // time (its own floor: the fan and the room). See Voiced.
    private readonly QuietFloor _cleanedFloor = new(), _micFloor = new();
    private int _hangover, _knockHold;

    /// <summary>The last frame's level, dBFS (-100 for silence).</summary>
    internal float Level { get; private set; } = -100f;

    /// <summary>The last frame passed the gate's own rules (a voice, not its hold).</summary>
    internal bool Speaking { get; private set; }

    /// <summary>The last frame let through was the hold's last: the gate shuts after it unless the next is a voice (the chain fades it out).</summary>
    internal bool Closing { get; private set; }

    /// <summary>Muted: the hold ends, so unmuting is judged afresh.</summary>
    internal void Mute() => _hangover = 0;

    /// <summary>Frames this gate let through and held shut, and the loudest frame it held shut (dBFS), since the last read.</summary>
    internal (int Open, int Shut, float LoudestShut) TakeStats()
    {
        var stats = (_open, _shut, _loudestShut);
        (_open, _shut, _loudestShut) = (0, 0, -100f);
        return stats;
    }

    private int _open, _shut;
    private float _loudestShut = -100f;

    /// <param name="frame">The frame as noise suppression left it, when it ran.</param>
    /// <param name="before">With noise suppression ahead, the level (dBFS) of the microphone's sound this frame came from
    /// (<see cref="NoiseSuppressor.InputDb"/>: the frame suppression's delay back, not the one going in now, which is ahead
    /// of it; comparing with that one held a word's first frame shut). The automatic gate then opens for a frame at a quiet
    /// voice's level (-65 dBFS and up; -55 held a quiet mic shut) that suppression kept (within 15 dB of what went in, as it
    /// keeps a voice, where it cuts typing and clicks by far more), when the microphone rose 6 dB over the quietest it heard
    /// lately and the frame 15 dB over the quietest of what came out lately. The floors are why steady fan and
    /// room noise never open it: DeepFilterNet keeps that kind of noise (pink or brown, as fans make) only about 5 dB down
    /// until it has heard a voice, then cuts it to silence, so neither "kept" nor the level alone tells it from a voice;
    /// but a voice rises over the room, and the room stays where it was. Measured on DeepFilterNet's own output (pink,
    /// brown and fan-like noise at -55 to -35 dBFS, speech from -48 to -22 dBFS over it): steady noise opened it on none
    /// of 6,600 frames (the old floor, followed down at once, sank to each frame suppression took to silence, and fans
    /// opened it on up to 549 of 550), and the frames still shut as each word began held 0.5% of what could have been
    /// sent; a fan that starts up opens it once, for 300 ms.
    /// Null without suppression (or in its first frame, before that sound is known): the level against the noise floor.</param>
    /// <param name="speech">DeepFilterNet's own judgment of the frame (<see cref="NoiseSuppressor.SpeechDb"/>, local SNR in
    /// dB). Over <see cref="SpeechDb"/> it opens the automatic gate by itself, as Discord's gate takes Krisp's voice
    /// activity: measured on the user's own voice through it, words scored 15 to 30 dB and the room around -12, a fan
    /// included, so a word's quiet start or end the level rules missed goes out too.</param>
    /// <param name="knock">A knock or slam began in this frame (<see cref="KnockDetector"/>): for 100 ms a shut automatic gate
    /// stays shut unless suppression clearly hears speech (<see cref="SpeechDb"/> + 4), so the desk is never sent, and a
    /// voice already going out isn't cut.</param>
    internal bool Voiced(ReadOnlySpan<float> frame, bool automatic, int threshold, float? before = null, float? speech = null, bool knock = false)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        var db = MathF.Max(-100f, 10 * MathF.Log10((float)(sum / Math.Max(1, frame.Length)) + 1e-10f));
        Level = db;
        _noiseFloor = db < _noiseFloor ? db : _noiseFloor + 0.05f;
        var open = !automatic ? db > threshold
            // After suppression: open when the model hears speech, or when what it left is clearly over its own quiet floor,
            // the mic is over its own, and suppression kept most of it. Noise loses about 39 dB to it (a fan, a key's click)
            // and a voice little, so under 15 dB taken is a voice; a steady fan is learned into both floors.
            : before is { } heard ? db > -65f && (speech > SpeechDb
                || (db > heard - 15f && db > _cleanedFloor.Db + 15f && heard > _micFloor.Db + 6f))
            : db > MathF.Max(_noiseFloor + 12f, -55f);
        if (before is { } mic)
        {
            // A floor never rises on a voice: talking non-stop has no quiet 100 ms for it to learn, so it would
            // climb to the voice and start holding words shut. It can still fall to a quieter room mid-sentence.
            _cleanedFloor.Add(open ? MathF.Min(db, _cleanedFloor.Db) : db);
            _micFloor.Add(open ? MathF.Min(mic, _micFloor.Db) : mic);
        }
        if (knock) _knockHold = 5;
        if (_knockHold > 0)
        {
            _knockHold--;
            if (automatic && _hangover <= 0 && !(speech > SpeechDb + 4f)) open = false;
        }
        Speaking = open;
        Closing = false;
        if (open) { _hangover = Hold; _open++; return true; }
        if (_hangover-- > 0) { Closing = _hangover == 0; _open++; return true; }
        _shut++;
        _loudestShut = MathF.Max(_loudestShut, db);
        return false;
    }
}

/// <summary>
/// A floor for <see cref="VoiceGate"/> after noise suppression, fed one frame's level at a time: the quietest 100 ms of
/// the last 1.6 s (eight 200 ms spans), on the level smoothed over 100 ms. Smoothed, so the odd frame suppression takes
/// to silence doesn't pull it down; the quietest over a span, so a new steady sound is learned within 1.6 s and talking,
/// which pauses between words, never lifts it. Before its first frame it is above everything (nothing opens on it).
/// </summary>
internal sealed class QuietFloor
{
    private const int Span = 10, Spans = 8;
    private static readonly float Smoothing = 1 - MathF.Exp(-20f / 100f);
    private readonly float[] _spans = new float[Spans];
    private float _smooth = -1f, _now = float.MaxValue;
    private int _frames, _at;

    internal QuietFloor() => Array.Fill(_spans, float.MaxValue);

    /// <summary>The floor, dBFS.</summary>
    internal float Db
    {
        get
        {
            var quietest = _now;
            foreach (var span in _spans) quietest = MathF.Min(quietest, span);
            return quietest == float.MaxValue ? float.MaxValue : 10 * MathF.Log10(quietest + 1e-10f);
        }
    }

    internal void Add(float db)
    {
        var power = MathF.Pow(10, db / 10);
        _smooth = _smooth < 0 ? power : _smooth + (power - _smooth) * Smoothing;
        _now = MathF.Min(_now, _smooth);
        if (++_frames < Span) return;
        _spans[_at] = _now;
        _at = (_at + 1) % Spans;
        _now = float.MaxValue;
        _frames = 0;
    }
}

/// <summary>
/// Automatic Gain Control in software, for microphones Windows can't do it for: while the user
/// speaks, the gain eases toward a -20 dBFS speaking level (at most +18 dB, at least -6 dB, about
/// 1 dB a second up and faster down), and holds still in the pauses so noise is never pulled up.
/// </summary>
internal sealed class AutoGain
{
    private const float TargetDb = -20f, MaxGainDb = 18f, MinGainDb = -6f;
    private float _gainDb;

    internal void Process(float[] frame, bool voiced, float levelDb)
    {
        var gain = Next(voiced, levelDb);
        if (gain == 1f) return;
        for (var i = 0; i < frame.Length; i++) frame[i] = MathF.Max(-1f, MathF.Min(1f, frame[i] * gain));
    }

    /// <summary>The gain for this frame, from its level and whether it is voice (<see cref="VoiceFocus"/> applies it itself).</summary>
    internal float Next(bool voiced, float levelDb)
    {
        if (voiced && levelDb > -70f)
        {
            var error = TargetDb - (levelDb + _gainDb);
            _gainDb = Math.Clamp(_gainDb + Math.Clamp(error, -0.6f, 0.02f), MinGainDb, MaxGainDb);
        }
        return _gainDb == 0f ? 1f : MathF.Pow(10f, _gainDb / 20f);
    }
}

/// <summary>
/// Gain control on a cleaned voice, as WebRTC's sits after its noise suppression: it learns the speaking level only
/// from frames that are a voice (for your microphone, those the gate's rules passed, <see cref="VoiceGate.Speaking"/>; for
/// someone in the call, those over their own quiet, see <see cref="VoiceAudio"/>), so a fan, a click or a pause never moves
/// it, and turns the voice toward <see cref="TargetDb"/>, by at most <see cref="RangeDb"/> either way. The level is the
/// mean, in dB, of the voice frames heard, starting from the target as if 10 frames of it had been (so one loud first
/// syllable moves it a tenth), then of about the last 1.5 s of talking (75 frames). The gain glides across each frame
/// from the last frame's to the new one, so it never steps. Nothing is allocated per frame.
/// </summary>
internal sealed class SpeechLeveler
{
    internal const float TargetDb = -24f, RangeDb = 12f;
    private const int Start = 10, Span = 75;
    private readonly float _target;
    private float _speechDb, _gain = 1f;
    private int _weight = Start;

    internal SpeechLeveler(float targetDb = TargetDb) => _speechDb = _target = targetDb;

    /// <summary>The gain now, in dB.</summary>
    internal float GainDb => 20 * MathF.Log10(_gain);

    /// <summary>The voice's speaking level as learned, in dBFS.</summary>
    internal float SpeechDb => _speechDb;

    /// <summary>Levels a 20 ms stereo frame in place; <paramref name="voiced"/>: it is a voice to learn the level from.</summary>
    internal void Process(Span<float> stereo, bool voiced)
    {
        if (voiced && Level(stereo) is var db && db > -70f)
        {
            if (_weight < Span) _weight++;
            _speechDb += (db - _speechDb) / _weight;
        }
        var target = MathF.Pow(10, Math.Clamp(_target - _speechDb, -RangeDb, RangeDb) / 20);
        var gain = _gain;
        var step = (target - gain) / (stereo.Length / 2);
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            gain += step;
            stereo[i] *= gain;
            stereo[i + 1] *= gain;
        }
        _gain = target;
    }

    /// <summary>A frame's level, dBFS (-100 for silence).</summary>
    internal static float Level(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        return MathF.Max(-100f, 10 * MathF.Log10((float)(sum / Math.Max(1, frame.Length)) + 1e-10f));
    }
}

/// <summary>
/// The voice's shape on the way out, what a studio chain does for a microphone: a low shelf takes off the
/// capsule's boom (−3 dB under 220 Hz), a presence shelf adds the clarity a cheap mic is missing (+3 dB over
/// 5 kHz), a de-esser keeps the lifted sibilance honest (up to 9 dB off the 5.5 kHz band alone, and only when
/// it is a large share of the voice, so "s" and "sh" stay bright without a spit), then a soft compressor
/// (2.5:1 over −16 dBFS, ~3 ms attack, ~150 ms release) keeps loud moments from spiking over quiet ones, then
/// a ceiling just under 0 dBFS so nothing ever clips. Gain is computed on peak envelopes once per 2.5 ms block
/// and glides across it, so it never steps. Runs after noise suppression, so the shelves lift a clean voice,
/// not noise. Nothing is allocated per frame.
/// </summary>
internal sealed class VoiceShape
{
    private const int Block = 120; // 2.5 ms of a channel at 48 kHz
    private const float ThresholdDb = -16f, Ratio = 2.5f, Ceiling = 0.98f;
    // One-pole envelope coefficients: compressor attack ~3 ms (144 samples), release ~150 ms (7200 samples);
    // de-esser attack ~2 ms (96 samples), release ~50 ms (2400 samples).
    private static readonly float Attack = 1 - MathF.Exp(-1f / 144f), Release = 1 - MathF.Exp(-1f / 7200f),
        EssAttack = 1 - MathF.Exp(-1f / 96f), EssRelease = 1 - MathF.Exp(-1f / 2400f);
    private readonly Biquad _lowL = Biquad.LowShelf(220, -3f), _lowR = Biquad.LowShelf(220, -3f),
        _highL = Biquad.HighShelf(5000, 3f), _highR = Biquad.HighShelf(5000, 3f),
        _essL = Biquad.HighPass(5500), _essR = Biquad.HighPass(5500);
    private float _envelope, _gain = 1f, _essEnv, _fullEnv, _essGain = 1f, _target = 1f, _essTarget = 1f;

    internal void Process(Span<float> stereo)
    {
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            var l = _highL.Run(_lowL.Run(stereo[i]));
            var r = _highR.Run(_lowR.Run(stereo[i + 1]));
            // The de-esser sees the sibilant band the presence shelf lifted; only it is pulled down.
            var essL = _essL.Run(l);
            var essR = _essR.Run(r);
            var sibilant = MathF.Max(MathF.Abs(essL), MathF.Abs(essR));
            var peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            _envelope += (peak - _envelope) * (peak > _envelope ? Attack : Release);
            _essEnv += (sibilant - _essEnv) * (sibilant > _essEnv ? EssAttack : EssRelease);
            _fullEnv += (peak - _fullEnv) * (peak > _fullEnv ? EssAttack : EssRelease);
            // A block boundary: the compressor's and de-esser's gain for the block ahead.
            if (i / 2 % Block == 0)
            {
                var envDb = 20 * MathF.Log10(MathF.Max(_envelope, 1e-5f));
                var overDb = envDb - ThresholdDb;
                var gainDb = overDb > 0 ? overDb * (1f / Ratio - 1f) : 0f;
                _target = MathF.Pow(10, gainDb / 20);
                var sibDb = 20 * MathF.Log10(MathF.Max(_essEnv, 1e-5f));
                var fullDb = 20 * MathF.Log10(MathF.Max(_fullEnv, 1e-5f));
                var sibOver = sibDb - Math.Max(-24f, fullDb - 14f);
                _essTarget = sibOver > 0 ? MathF.Pow(10, -MathF.Min(9f, sibOver * (1 - 1f / 3f)) / 20) : 1f;
            }
            _gain += (_target - _gain) / Block;
            _essGain += (_essTarget - _essGain) / Block;
            stereo[i] = Math.Clamp((l - essL * (1 - _essGain)) * _gain, -Ceiling, Ceiling);
            stereo[i + 1] = Math.Clamp((r - essR * (1 - _essGain)) * _gain, -Ceiling, Ceiling);
        }
    }

    /// <summary>An RBJ biquad shelf for one channel of the frame.</summary>
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
            => (_b0, _b1, _b2, _a1, _a2) = (b0 / a0, b1 / a0, b2 / a0, -a1 / a0, -a2 / a0);

        internal static Biquad LowShelf(double hz, double db) => Shelf(hz, db, low: true);
        internal static Biquad HighShelf(double hz, double db) => Shelf(hz, db, low: false);

        /// <summary>An RBJ Butterworth high-pass (Q = √2/2): the de-esser's view of the sibilant band.</summary>
        internal static Biquad HighPass(double hz)
        {
            var w = 2 * Math.PI * hz / 48000;
            var alpha = Math.Sin(w) / Math.Sqrt(2);
            var cos = Math.Cos(w);
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        private static Biquad Shelf(double hz, double db, bool low)
        {
            var a = Math.Pow(10, db / 40);
            var w = 2 * Math.PI * hz / 48000;
            var alpha = Math.Sin(w) / 2 * Math.Sqrt(2); // S = 1, a slope of one
            var cos = Math.Cos(w);
            var two = 2 * Math.Sqrt(a) * alpha;
            var plus = a + 1;
            var minus = a - 1;
            double b0, b1, b2, a0, a1, a2;
            if (low)
            {
                b0 = a * (plus - minus * cos + two); b1 = 2 * a * (minus - plus * cos); b2 = a * (plus - minus * cos - two);
                a0 = plus + minus * cos + two; a1 = -2 * (minus + plus * cos); a2 = plus + minus * cos - two;
            }
            else
            {
                b0 = a * (plus + minus * cos + two); b1 = -2 * a * (minus + plus * cos); b2 = a * (plus + minus * cos - two);
                a0 = plus - minus * cos + two; a1 = 2 * (minus - plus * cos); a2 = plus - minus * cos - two;
            }
            return new Biquad(b0, b1, b2, a0, a1, a2);
        }

        internal float Run(float x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 + _a1 * _y1 + _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return (float)y;
        }
    }
}

/// <summary>
/// Discord's keybinds, read with GetAsyncKeyState while a call is on (Discord's own are global too):
/// nothing is hooked or sent, a key is only looked at. Push to Talk is read on each 20 ms microphone
/// frame; the toggles on a 30 ms tick (<see cref="DiscordSession"/>).
/// </summary>
internal static partial class Keybinds
{
    internal const string PushToTalk = "PUSH_TO_TALK", PushToMute = "PUSH_TO_MUTE", ToggleMute = "TOGGLE_MUTE",
        ToggleDeafen = "TOGGLE_DEAFEN", ToggleVoiceMode = "TOGGLE_VOICE_MODE", ToggleStreamerMode = "TOGGLE_STREAMER_MODE",
        Disconnect = "DISCONNECT_FROM_VOICE";

    private static readonly HashSet<string> Actions = [PushToTalk, PushToMute, ToggleMute, ToggleDeafen, ToggleVoiceMode, ToggleStreamerMode, Disconnect];

    internal static bool IsAction(string? action) => action is not null && Actions.Contains(action);

    /// <summary>True while every key of one of this action's keybinds is held.</summary>
    internal static bool Held(string action)
    {
        foreach (var bind in App.Services.Settings.Shared.DiscordKeybinds)
            if (bind.Action == action && bind.Keys.Count > 0 && bind.Keys.TrueForAll(Down)) return true;
        return false;
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
}

/// <summary>Push to Talk on the capture thread: open while a Push to Talk keybind is held and for the release delay after.</summary>
internal sealed class PushToTalkGate
{
    private long _releasedAt = long.MinValue / 2;
    private bool _held;

    /// <summary>Whether to send this frame; <paramref name="changed"/> is set when the key went down or up (Discord's PTT sounds).</summary>
    internal bool Open(int delayMs, out bool changed)
    {
        var held = Keybinds.Held(Keybinds.PushToTalk);
        changed = held != _held;
        if (_held && !held) _releasedAt = Environment.TickCount64;
        _held = held;
        return held || Environment.TickCount64 - _releasedAt < delayMs;
    }

    internal bool Held => _held;
}

/// <summary>
/// Whether the microphone sends anything, as Discord's "we're not detecting any input from your mic": silent when no
/// 20 ms frame of its raw sound came over <see cref="QuietDb"/> for <see cref="Frames"/> frames (15 s) while unmuted,
/// heard again at the first frame over <see cref="HeardDb"/>. A working mic in a quiet room still hears its own noise,
/// a breath or a key over -72 dBFS within 15 s; a mic whose gain is at its lowest sits near -90 (measured on a fifine
/// with its knob down). Between the two levels nothing changes, so a mic hovering there doesn't flicker it.
/// </summary>
internal sealed class MicSilence
{
    internal const float QuietDb = -72f, HeardDb = -66f;
    internal const int Frames = 750;
    private int _quiet;
    private bool _silent;

    /// <summary>Takes one raw frame; the new state when it changed, else null.</summary>
    internal bool? Hear(ReadOnlySpan<float> frame, bool muted)
    {
        if (muted) { _quiet = 0; return Set(false); }
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        var db = 10 * MathF.Log10((float)(sum / Math.Max(1, frame.Length)) + 1e-12f);
        if (db > HeardDb) _quiet = 0;
        else if (db < QuietDb && _quiet < Frames) _quiet++;
        return Set(_silent ? _quiet > 0 : _quiet >= Frames);
    }

    private bool? Set(bool silent)
    {
        if (silent == _silent) return null;
        _silent = silent;
        return silent;
    }
}
