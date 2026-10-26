using ExoLauncher.Helpers;
using ExoLauncher.Services.DiscordClient.Voice;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Dictation: the mic in Exo's text boxes turns what you say into text, on this PC. One take at a time, from the press
/// until Stop (at most <see cref="MaxSeconds"/>): the microphone chosen in Exo's Discord voice settings (Windows' default
/// otherwise), opened shared as the mic meter opens it (<see cref="VoiceAudio"/>, so a call keeps its own), cleaned by
/// Exo's noise suppression when calls use it (an instance of its own: a call's chain is never touched, and a call is never
/// transcribed), kept as 16 kHz mono; while it listens, dictation.level carries its loudness about 20 times a second.
/// Stop transcribes it with <see cref="DictationRecognizer"/>. The sound stays in memory and goes with the take; nothing
/// is written or sent anywhere.
/// </summary>
internal static class Dictation
{
    internal const int MaxSeconds = 60;
    private const int Rate = 16000;

    private static readonly Lock Gate = new();
    private static Take? _take;

    /// <summary>Starts listening; a message for the user when it can't (one already listens, mic access off, no model).</summary>
    internal static string? Start(Action<string, object> post)
    {
        if (!DictationRecognizer.Available) return "Dictation doesn't run on this PC.";
        if (MicBlocked() is { } blocked) return blocked;
        if (!DictationModel.Ready()) return "Dictation is still getting ready.";
        lock (Gate)
        {
            _take?.Dispose();
            _take = new Take(post);
        }
        // Loaded while you talk, so Stop only transcribes.
        _ = Task.Run(DictationRecognizer.Load);
        AppLog.Info("Dictation: listening.");
        return null;
    }

    /// <summary>Ends the take and transcribes it: the text to insert between <paramref name="before"/> and <paramref name="after"/>.</summary>
    internal static async Task<(string Text, string? Message)> StopAsync(string? before, string? after)
    {
        Take? take;
        lock (Gate) (take, _take) = (_take, null);
        if (take is null) return ("", null);
        take.Dispose();
        var voice = take.Voice();
        if (voice.Length < Rate / 4) return ("", null);
        Normalize(voice);
        var started = Environment.TickCount64;
        var spoken = await Task.Run(() => DictationRecognizer.Transcribe(voice)).ConfigureAwait(false);
        if (spoken is null) return ("", "Dictation couldn't run. Try again in a moment.");
        AppLog.Info($"Dictation: {voice.Length / (float)Rate:0.0} s transcribed in {Environment.TickCount64 - started} ms.");
        var text = Join(before, spoken, after);
        return (text, text.Length == 0 ? "Exo didn't catch any words." : null);
    }

    /// <summary>Escape, or the page left: the take goes, untranscribed.</summary>
    internal static void Cancel()
    {
        Take? take;
        lock (Gate) (take, _take) = (_take, null);
        take?.Dispose();
    }

    private static void End(Take take, object data)
    {
        lock (Gate)
        {
            if (_take != take) return;
            _take = null;
        }
        take.Dispose();
        take.Post("dictation.ended", data);
    }

    /// <summary>
    /// The model's words as they go into the text box: trimmed, a space before them after a word (or punctuation that
    /// ends one), none at the start, after a space or after an opening bracket or quote; a space after them when a word
    /// follows straight on. Punctuation and casing stay the model's.
    /// </summary>
    internal static string Join(string? before, string spoken, string? after)
    {
        var text = spoken.Trim();
        if (text.Length == 0) return "";
        if (before is { Length: > 0 } && !char.IsWhiteSpace(before[^1]) && !Opens(before[^1]) && !Closes(text[0])) text = " " + text;
        if (after is { Length: > 0 } && char.IsLetterOrDigit(after[0])) text += " ";
        return text;
    }

    private static bool Opens(char c) => c is '(' or '[' or '{' or '"' or '\'' or '“' or '‘' or '«' or '¿' or '¡' or '@' or '#' or '/';

    private static bool Closes(char c) => c is '.' or ',' or '!' or '?' or ';' or ':' or ')' or ']' or '}';

    /// <summary>A quiet take brought up to a usable level (peak 0.5, at most 10 times louder); a loud one is left as it is.</summary>
    internal static void Normalize(Span<float> voice)
    {
        var peak = 0f;
        foreach (var s in voice) peak = MathF.Max(peak, MathF.Abs(s));
        if (!(peak > 0) || peak >= 0.5f) return;
        var gain = MathF.Min(0.5f / peak, 10f);
        for (var i = 0; i < voice.Length; i++) voice[i] *= gain;
    }

    /// <summary>A 20 ms frame's loudness for the voice line, 0 (−55 dBFS and under) to 1 (−10 dBFS and over).</summary>
    internal static float Level(ReadOnlySpan<float> mono)
    {
        double sum = 0;
        foreach (var s in mono) sum += s * (double)s;
        var db = 10 * Math.Log10(sum / Math.Max(1, mono.Length) + 1e-10);
        return (float)Math.Clamp((db + 55) / 45, 0, 1);
    }

    /// <summary>
    /// Windows' microphone switches (Settings › Privacy &amp; security › Microphone) as a desktop app meets them: a message
    /// when one is off, null when Exo may use the microphone.
    /// </summary>
    internal static string? MicBlocked()
    {
        const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        static bool Denied(RegistryKey root, string path)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
        if (Denied(Registry.LocalMachine, Store) || Denied(Registry.CurrentUser, Store))
            return "Microphone access is off in Windows. Turn it on in Settings › Privacy & security › Microphone.";
        if (Denied(Registry.CurrentUser, Store + @"\NonPackaged"))
            return "Windows keeps desktop apps from the microphone. Turn on Let desktop apps access your microphone in Settings › Privacy & security › Microphone.";
        return null;
    }

    /// <summary>One take: its microphone, its noise suppression and what it heard, at 16 kHz.</summary>
    private sealed class Take : IDisposable
    {
        private readonly Action<string, object> _post;
        private readonly VoiceAudio _audio = new();
        private readonly NoiseSuppressor? _suppressor;
        private readonly MyVoice.Decimator _decimator = new();
        private readonly float[] _mono = new float[Opus.FrameSamples], _down = new float[Opus.FrameSamples / 3];
        private readonly float[] _voice = new float[MaxSeconds * Rate];
        private readonly Lock _bufferGate = new();
        private readonly Timer _watchdog;
        private int _length, _frames, _sinceLevel, _disposed, _fullSaid;
        private float _loudest;

        internal Take(Action<string, object> post)
        {
            _post = post;
            var processing = VoiceProcessing.Current;
            _suppressor = processing.NoiseSuppression && NoiseSuppressorDownload.Ready() ? NoiseSuppressor.Create() : null;
            _audio.Captured += OnFrame;
            _audio.Start(capture: true, render: false);
            // No sound at all after 2.5 s: there is no microphone to hear (none plugged in, or Windows has none on).
            _watchdog = new Timer(_ =>
            {
                if (Volatile.Read(ref _frames) > 0) return;
                AppLog.Info("Dictation: no sound came from the microphone.");
                End(this, new { message = "Exo heard nothing from your microphone. Check it's plugged in and on in Windows' sound settings." });
            }, null, 2500, Timeout.Infinite);
        }

        internal void Post(string name, object data) => _post(name, data);

        // On Exo's voice thread, each 20 ms stereo frame: cleaned when calls clean, to mono, to 16 kHz, kept; its level.
        private void OnFrame(float[] frame)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Interlocked.Increment(ref _frames);
            _suppressor?.Process(frame);
            for (var i = 0; i < _mono.Length; i++) _mono[i] = (frame[2 * i] + frame[2 * i + 1]) * 0.5f;
            _decimator.Run(_mono, _down);
            bool full;
            lock (_bufferGate)
            {
                var take = Math.Min(_down.Length, _voice.Length - _length);
                _down.AsSpan(0, take).CopyTo(_voice.AsSpan(_length));
                _length += take;
                full = _length == _voice.Length;
            }
            _loudest = MathF.Max(_loudest, Level(_mono));
            // 20 ms frames, a level every 50 ms: 20 a second.
            if ((_sinceLevel += 20) >= 50)
            {
                _sinceLevel -= 50;
                _post("dictation.level", new { level = MathF.Round(_loudest, 3) });
                _loudest = 0;
            }
            // A minute heard: the page stops the take (Stop's own path, so it's transcribed), once.
            if (full && Interlocked.Exchange(ref _fullSaid, 1) == 0) _post("dictation.ended", new { full = true });
        }

        /// <summary>What it heard, at 16 kHz mono.</summary>
        internal float[] Voice()
        {
            lock (_bufferGate) return _voice.AsSpan(0, _length).ToArray();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _watchdog.Dispose();
            _audio.Dispose();
            // After the voice thread stopped: it is the suppressor's only user.
            _suppressor?.Dispose();
            AppLog.Info($"Dictation: stopped listening after {_frames * 20 / 1000.0:0.0} s.");
        }
    }
}
