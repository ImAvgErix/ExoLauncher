using System.Runtime.InteropServices;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// What Discord settings > Voice & Video asks of the microphone: Exo Voice, everything Exo has tuned together
/// (DeepFilterNet noise suppression, echo cancellation, Voice Focus with its leveler,
/// automatic sensitivity), or Custom (each choice the user's own). Earlier profiles (Voice Isolation, Studio)
/// are Exo Voice now.
/// </summary>
internal readonly record struct VoiceProcessing(string Profile, bool NoiseSuppression, bool EchoCancellation, bool AutoGain,
    bool PushToTalk, bool AutoSensitivity, int Threshold, int PttDelay, bool Focus)
{
    internal static VoiceProcessing Current => From(App.Services.Settings.Shared);

    /// <summary>
    /// Exo Voice, kept simple: noise suppression at one strength, Windows' echo cancellation, and Discord's own automatic
    /// gate. No Voice Focus, no gain riding and no tuning: each added delay or changed how a voice sounds.
    /// </summary>
    internal static readonly VoiceProcessing ExoVoice = new("exo", true, true, false, false, true, -60, 0, false);

    // One set of switches: noise suppression, echo cancellation (Exo's own, on speakers only: Windows' smeared voices), the
    // noise gate (Input Sensitivity) and gain control (Exo's own). No profiles, no Voice Focus, no tuning.
    internal static VoiceProcessing From(Models.AppSettings s) =>
        new("custom", s.DiscordNoiseSuppression, s.DiscordEchoCancellation, s.DiscordAutoGain,
            s.DiscordPushToTalk, s.DiscordAutoSensitivity, s.DiscordSensitivity, s.DiscordPttDelay, Focus: false);
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
    private float _noiseFloor = -60f;
    // After noise suppression: what comes out between words (the cleaned floor) and what the microphone hears all the
    // time (its own floor: the fan and the room). See Voiced.
    private readonly QuietFloor _cleanedFloor = new(), _micFloor = new();
    private int _hangover;

    /// <summary>The last frame's level, dBFS (-100 for silence).</summary>
    internal float Level { get; private set; } = -100f;

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
    /// voice's level (-65 dBFS and up; -55 held a quiet mic shut) that suppression kept (within 6 dB of what went in, as it
    /// keeps a voice, where it cuts typing and clicks by far more), when the microphone rose 12 dB over the quietest it heard
    /// lately, or 6 dB with the frame 18 dB over the quietest of what came out lately. The floors are why steady fan and
    /// room noise never open it: DeepFilterNet keeps that kind of noise (pink or brown, as fans make) only about 5 dB down
    /// until it has heard a voice, then cuts it to silence, so neither "kept" nor the level alone tells it from a voice;
    /// but a voice rises over the room, and the room stays where it was. Measured on DeepFilterNet's own output (pink,
    /// brown and fan-like noise at -55 to -35 dBFS, speech from -48 to -22 dBFS over it): steady noise opened it on none
    /// of 6,600 frames (the old floor, followed down at once, sank to each frame suppression took to silence, and fans
    /// opened it on up to 549 of 550), and the frames still shut as each word began held 0.5% of what could have been
    /// sent; a fan that starts up opens it once, for 300 ms.
    /// Null without suppression (or in its first frame, before that sound is known): the level against the noise floor.</param>
    internal bool Voiced(ReadOnlySpan<float> frame, bool automatic, int threshold, float? before = null)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        var db = MathF.Max(-100f, 10 * MathF.Log10((float)(sum / Math.Max(1, frame.Length)) + 1e-10f));
        Level = db;
        _noiseFloor = db < _noiseFloor ? db : _noiseFloor + 0.05f;
        var open = !automatic ? db > threshold
            : before is { } heard ? db > -65f && db > heard - 6f
                && (heard > _micFloor.Db + 12f || (heard > _micFloor.Db + 6f && db > _cleanedFloor.Db + 18f))
            : db > MathF.Max(_noiseFloor + 12f, -55f);
        if (before is { } mic)
        {
            _cleanedFloor.Add(db);
            _micFloor.Add(mic);
        }
        if (open) { _hangover = 15; _open++; return true; }
        if (_hangover-- > 0) { _open++; return true; }
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
/// Gain control ahead of noise suppression (the call's Automatic Gain Control): a quiet microphone is brought up to a
/// normal speaking level before anything judges it. A mic opened raw (no Windows or driver boost, no Equalizer APO chain)
/// can speak at -45 to -60 dBFS: DeepFilterNet then hears a voice barely over the room, the gate holds most of it, and
/// what it sends is 25 dB under everyone else's voice, which the old gain control (after the gate, 1 dB a second, at
/// most 18 dB) never made up. While the frame rises 10 dB over the room (the quietest frame lately: followed down at once,
/// up 2.5 dB a second, so a pause between words shows it), the gain moves a tenth of the way to <see cref="TargetDb"/> each frame (up at most 0.5 dB, so a quiet mic is
/// at its level after a second or two of talking; down at most 1 dB), from 0 to <see cref="MaxGainDb"/>; it never turns a voice down, and a frame whose peak
/// would pass <see cref="Ceiling"/> gets less for that frame only. The gain learned for a microphone is where the next
/// call on it starts (<see cref="Learned"/>).
/// </summary>
internal sealed class InputLeveler
{
    internal const float TargetDb = -26f, MaxGainDb = 30f, Ceiling = 0.9f;
    private static readonly Dictionary<string, float> Learned = [];
    private float _floorDb = float.NaN;
    private readonly string? _mic;
    private float _gainDb;

    internal InputLeveler(string? mic)
    {
        _mic = mic;
        if (mic is not null) lock (Learned) Learned.TryGetValue(mic, out _gainDb);
    }

    internal string? Mic => _mic;

    /// <summary>The gain now, in dB.</summary>
    internal float GainDb => _gainDb;

    internal void Process(float[] frame)
    {
        double sum = 0;
        var peak = 0f;
        foreach (var s in frame) { sum += s * s; peak = MathF.Max(peak, MathF.Abs(s)); }
        var db = MathF.Max(-100f, 10 * MathF.Log10((float)(sum / Math.Max(1, frame.Length)) + 1e-10f));
        var floor = _floorDb;
        _floorDb = float.IsNaN(floor) || db < floor ? db : floor + 0.05f;
        if (db > -80f && !float.IsNaN(floor) && db > floor + 10f)
            _gainDb = Math.Clamp(_gainDb + Math.Clamp((TargetDb - (db + _gainDb)) * 0.1f, -1f, 0.5f), 0f, MaxGainDb);
        var gain = MathF.Pow(10f, _gainDb / 20f);
        if (peak * gain > Ceiling) gain = Ceiling / peak;
        if (gain <= 1f) return;
        for (var i = 0; i < frame.Length; i++) frame[i] *= gain;
    }

    /// <summary>Keeps the gain for the microphone's next call.</summary>
    internal void Keep()
    {
        if (_mic is not null) lock (Learned) Learned[_mic] = _gainDb;
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
