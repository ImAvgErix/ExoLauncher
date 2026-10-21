using System.Globalization;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using static ExoLauncher.Helpers.CoreAudioInterop;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Voice Tune in Discord settings > Voice & Video: one guided take (quiet, a passage read normally, a few words
/// loud, a few soft, typing while silent; the page shows each part and says how long), analyzed by
/// <see cref="VoiceAnalysis"/> into a tuning for this voice on this microphone, kept per microphone when the user
/// keeps it. In a call, the user is muted while it listens and unmuted after, so the call never hears the
/// passage. It also reads what Windows does to the microphone: its input level (the mic's own hardware gain on
/// most USB mics, which Voice Tune sets first), whether Windows has the mic muted, and Windows' ducking.
/// </summary>
internal static class VoiceTune
{
    private const int Rate = VoiceAnalysis.Rate;
    private static int _running;
    private static (string Mic, VoiceReport Report)? _last;

    /// <summary>Records the parts (seconds each: quiet, reading, loud, soft, typing) and says what it heard.</summary>
    internal static async Task<object> RecordAsync(int[] seconds, CancellationToken ct)
    {
        if (seconds.Length != 5) return new { ok = false, message = "Voice Tune needs its five parts." };
        var parts = seconds.Select(s => Math.Clamp(s, 3, 40)).ToArray();
        if (Interlocked.Exchange(ref _running, 1) == 1) return new { ok = false, message = "Voice Tune is already listening." };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancel = cancel;
        var muted = DiscordSession.Current?.MuteForTune() == true;
        try
        {
            var total = parts.Sum() * Rate;
            var take = new float[total];
            var count = 0;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var audio = new VoiceAudio { Raw = true };
            try
            {
                audio.Captured += frame =>
                {
                    // The guide's clock starts with the microphone's first sound, not with the button.
                    if (count == 0) DiscordSession.Publish("discord.voiceTune", new { state = "recording" });
                    for (var i = 0; i < frame.Length / 2 && count < total; i++) take[count++] = (frame[2 * i] + frame[2 * i + 1]) * 0.5f;
                    if (count >= total) done.TrySetResult();
                };
                audio.Start(capture: true, render: false);
                try
                {
                    if (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(parts.Sum() + 4), cancel.Token)).ConfigureAwait(false) != done.Task)
                        return new { ok = false, message = "The microphone didn't answer. Check it's plugged in and chosen above." };
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new { ok = false, message = (string?)null, cancelled = true }; }
            }
            finally { audio.Dispose(); }

            var pieces = Split(take, parts);
            var report = VoiceAnalysis.Analyze(pieces);
            var mic = MicKey();
            _last = (mic, report);
            var stepsAside = VoiceChain.MicStepsAside(App.Services.Settings.Shared.DiscordInputDevice);
            return new
            {
                ok = true, message = (string?)null, report, windows = WindowsState(), kept = Kept(mic) is not null, mic = Mic(),
                before = VoiceCheck.Wav(pieces.Reading), after = VoiceCheck.Wav(Render(pieces.Reading, report.Tuning, stepsAside)),
            };
        }
        finally
        {
            _cancel = null;
            if (muted) DiscordSession.Current?.SetVoice(false, null);
            Volatile.Write(ref _running, 0);
        }
    }

    private static CancellationTokenSource? _cancel;

    /// <summary>Cancel: stops listening at once (and unmutes, when it muted the call).</summary>
    internal static object Cancel()
    {
        try { _cancel?.Cancel(); }
        catch (ObjectDisposedException) { }
        return new { ok = true, message = (string?)null };
    }

    // The reading through Exo Voice as tuned, what the call would send and the other person hear: the call's own chain
    // with this tuning (and the mic's own noise suppression standing in where the call would let it), after the gain
    // Keep would set, and Opus as the call codes it. Fed a frame at a time, as a call feeds it.
    private static float[] Render(float[] reading, VoiceTuning tuning, bool stepsAside)
    {
        var gain = MathF.Pow(10, tuning.GainDb / 20);
        using var chain = new VoiceChain { Tuning = tuning, StepsAside = stepsAside };
        using var codec = new SentSound();
        chain.Prepare(VoiceProcessing.ExoVoice);
        var frames = reading.Length / Opus.FrameSamples;
        var output = new float[frames * Opus.FrameSamples];
        var frame = new float[Opus.FrameSamples * 2];
        for (var f = 0; f < frames; f++)
        {
            for (var i = 0; i < Opus.FrameSamples; i++) frame[2 * i] = frame[2 * i + 1] = Math.Clamp(reading[f * Opus.FrameSamples + i] * gain, -1f, 1f);
            if (!chain.Process(frame, VoiceProcessing.ExoVoice, null, systemGain: false)) continue;
            codec.Through(frame);
            VoiceCheck.Mono(frame, output, f * Opus.FrameSamples);
        }
        return output;
    }

    // Each part without its first 0.7 s (the time to start the next thing) and its last 0.2 s.
    private static TuneTake Split(float[] take, int[] parts)
    {
        var at = 0;
        var pieces = new float[5][];
        for (var p = 0; p < 5; p++)
        {
            var start = at + Rate * 7 / 10;
            var end = at + parts[p] * Rate - Rate / 5;
            pieces[p] = take[start..end];
            at += parts[p] * Rate;
        }
        return new TuneTake(pieces[0], pieces[1], pieces[2], pieces[3], pieces[4]);
    }

    /// <summary>
    /// Keeps the last take's tuning for this microphone, and sets its gain: on Windows' input level first when it is
    /// the mic's own hardware gain, the rest (or all of it, on a mic without one) on Exo's Microphone Volume.
    /// </summary>
    internal static object Keep()
    {
        // Once per take: the gain is relative to the mic as it was recorded, so a second Keep would add it again.
        if (_last is not { } last) return new { ok = false, message = "Record first." };
        _last = null;
        var gain = last.Report.Tuning.GainDb;
        var left = gain;
        if (Math.Abs(gain) >= 1 && Endpoint() is { } volume)
        {
            try
            {
                if (volume.QueryHardwareSupport(out var mask) == 0 && (mask & 1) != 0
                    && volume.GetMasterVolumeLevel(out var now) == 0 && volume.GetVolumeRange(out var min, out var max, out _) == 0)
                {
                    var next = Math.Clamp(now + gain, min, max);
                    if (volume.SetMasterVolumeLevel(next, 0) == 0) left = gain - (next - now);
                }
            }
            finally { Marshal.ReleaseComObject(volume); }
        }
        App.Services.Settings.UpdateProfile(s =>
        {
            s.DiscordVoiceTunes[last.Mic] = Format(last.Report.Tuning);
            // Never above 100%: Exo's gain control lifts a quiet voice by itself, and boosting the whole input raised the
            // keyboard and the room with it (it had reached 190%).
            if (Math.Abs(left) >= 1) s.DiscordInputVolume = (int)Math.Round(Math.Clamp(s.DiscordInputVolume * Math.Pow(10, left / 20), 25, 100));
        });
        return new { ok = true, message = (string?)null, windows = WindowsState() };
    }

    /// <summary>Whether the chosen microphone has a kept tuning (Forget This Mic's Tuning shows then).</summary>
    internal static object State()
    {
        var mic = MicKey();
        return new { ok = true, message = (string?)null, kept = Kept(mic) is not null, processing = OtherProcessing(mic) };
    }

    /// <summary>Forgets this microphone's tuning: Exo Voice goes back to its general settings for it.</summary>
    internal static object Forget()
    {
        var mic = MicKey();
        App.Services.Settings.UpdateProfile(s => s.DiscordVoiceTunes.Remove(mic));
        return new { ok = true, message = (string?)null };
    }

    /// <summary>The kept tuning for a microphone (by endpoint id), if there is one.</summary>
    internal static VoiceTuning? For(string? mic) => Kept(mic ?? "default");

    private static VoiceTuning? Kept(string mic) =>
        App.Services.Settings.Shared.DiscordVoiceTunes.TryGetValue(mic, out var text) ? Parse(text) : null;

    /// <summary>Windows' ducking: whether it turns other sounds down when any app starts a call ("Do nothing" is 3).</summary>
    internal static object SetNoDucking()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Multimedia\Audio");
        key.SetValue("UserDuckingPreference", 3, RegistryValueKind.DWord);
        return new { ok = true, message = (string?)null, windows = WindowsState() };
    }

    /// <summary>Unmutes the microphone in Windows (muted there, nothing reaches any app).</summary>
    internal static object Unmute()
    {
        if (Endpoint() is { } volume)
        {
            try { volume.SetMute(false, 0); }
            finally { Marshal.ReleaseComObject(volume); }
        }
        return new { ok = true, message = (string?)null, windows = WindowsState() };
    }

    // Which microphone this is (Exo's catalog of mics, interfaces and headsets) and what to set on it: its own app's
    // steps (what the device does better, what to turn off so it doesn't work twice) and setup tips.
    private static object? Mic()
    {
        if (AudioDevices.Identify(App.Services.Settings.Shared.DiscordInputDevice) is not { } id) return null;
        var p = MicCatalog.Describe(id);
        return new
        {
            title = p.Title, kind = p.Kind.ToString(), app = p.App, tips = p.SetupTips,
            steps = p.Features.Select(f => new { name = f.Name, versus = f.Versus.ToString().ToLowerInvariant(), steps = f.Steps }).ToArray(),
            exoStepsAside = MicCatalog.StandsDown(p, MicStage.NoiseSuppression),
        };
    }

    private static object WindowsState()
    {
        float? level = null;
        bool hardware = false, muted = false;
        if (Endpoint() is { } volume)
        {
            try
            {
                if (volume.GetMasterVolumeLevelScalar(out var scalar) == 0) level = MathF.Round(scalar * 100);
                hardware = volume.QueryHardwareSupport(out var mask) == 0 && (mask & 1) != 0;
                muted = volume.GetMute(out var m) == 0 && m;
            }
            finally { Marshal.ReleaseComObject(volume); }
        }
        int ducking;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Multimedia\Audio"))
            ducking = key?.GetValue("UserDuckingPreference") is int value ? value : 1;
        return new { level, hardware, muted, ducking, processing = OtherProcessing(MicKey()) };
    }

    // Equalizer APO's pre-mix and post-mix effects, as Windows lists them on an endpoint: Lucid Voice and others tune a
    // mic through it, and Exo Voice then gates, compresses and de-esses the same voice a second time.
    private static readonly string[] EqualizerApo = ["{EACD2258-FCAC-4FF4-B36D-419E924A6D79}", "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}"];
    // PKEY_AudioEndpoint_Disable_SysFx: Windows' "Audio enhancements" off for the endpoint, so no effect on it runs.
    private const string EnhancementsOff = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";

    /// <summary>What else processes this microphone before Exo hears it, said plainly; null when nothing does. Read only.</summary>
    private static string? OtherProcessing(string micId)
    {
        var guid = micId[(micId.LastIndexOf('.') + 1)..];
        if (!guid.StartsWith('{')) return null;
        try
        {
            var endpoint = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\{guid}";
            using (var fx = Registry.LocalMachine.OpenSubKey(endpoint + @"\FxProperties"))
                if (fx is null || !fx.GetValueNames().Any(name => NamesEqualizerApo(fx.GetValue(name)))) return null;
            using (var properties = Registry.LocalMachine.OpenSubKey(endpoint + @"\Properties"))
                if (properties?.GetValue(EnhancementsOff) is int off && off != 0) return null;
            string folder;
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO"))
                folder = key?.GetValue("ConfigPath") as string
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO", "config");
            var config = File.ReadAllText(Path.Combine(folder, "config.txt"));
            string? Include(string file)
            {
                var path = Path.Combine(folder, file);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            var lucidDsp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LucidEQ", "lucid_voice_dsp.json");
            var lucidOn = LucidVoiceOn(File.Exists(lucidDsp) ? File.ReadAllText(lucidDsp) : null);
            return EqualizerApoUse(config, guid, Include, lucidOn) switch
            {
                "lucid" => "Lucid Voice is processing this microphone before Exo hears it, so your voice is gated, compressed and de-essed twice. Turn Lucid Voice off for this mic, then run Voice Tune.",
                "apo" => "Equalizer APO is processing this microphone before Exo hears it. Exo Voice tunes your voice itself: turn Equalizer APO off for this mic in its Configurator, then run Voice Tune.",
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // An FxProperties value naming Equalizer APO: a plain effect slot (REG_SZ) or Windows 11's list of them (REG_MULTI_SZ).
    private static bool NamesEqualizerApo(object? value) => value switch
    {
        string one => EqualizerApo.Contains(one, StringComparer.OrdinalIgnoreCase),
        string[] many => many.Any(one => EqualizerApo.Contains(one, StringComparer.OrdinalIgnoreCase)),
        _ => false,
    };

    /// <summary>
    /// Whether Lucid Voice is on, from the settings its plugin reads (%ProgramData%\LucidEQ\lucid_voice_dsp.json):
    /// turned off, Lucid leaves its block in Equalizer APO's config and its plugin passes the sound through. Without
    /// that file (or one Exo can't read) the plugin is taken as on, since it is still in the mic's chain.
    /// </summary>
    internal static bool LucidVoiceOn(string? dspSettings)
    {
        if (dspSettings is null) return true;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(dspSettings);
            return !(doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("enabled", out var on) && on.ValueKind == System.Text.Json.JsonValueKind.False);
        }
        catch (System.Text.Json.JsonException) { return true; }
    }

    /// <summary>
    /// Whether Equalizer APO's config.txt does anything to this microphone: "lucid" when Lucid Voice's block does and
    /// Lucid Voice is on, "apo" for anything else that applies to it (a Device line naming it, or all), null when
    /// nothing does. Include lines are followed (<paramref name="include"/> reads a file beside config.txt, null when
    /// there is none); Lucid Voice's plugin, turned off, passes the sound through.
    /// </summary>
    internal static string? EqualizerApoUse(string config, string guid, Func<string, string?> include, bool lucidOn)
    {
        string? use = null;
        return Walk(config, inLucid: false, depth: 0) ? "lucid" : use;

        // True as soon as Lucid Voice's own processing applies; anything else that applies sets use to "apo".
        bool Walk(string text, bool inLucid, int depth)
        {
            var applies = true;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("# Lucid Voice begin", StringComparison.OrdinalIgnoreCase)) inLucid = true;
                else if (line.StartsWith("# Lucid Voice end", StringComparison.OrdinalIgnoreCase)) inLucid = false;
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var command = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (command.Equals("Device", StringComparison.OrdinalIgnoreCase))
                {
                    applies = value.Equals("all", StringComparison.OrdinalIgnoreCase) || value.Contains(guid, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!applies || command.Equals("Channel", StringComparison.OrdinalIgnoreCase) || command.Equals("Stage", StringComparison.OrdinalIgnoreCase)) continue;
                if (command.Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    // A file that isn't there does nothing; Equalizer APO's own nesting is shallow, so a loop stops here.
                    if (depth < 4 && include(value.Trim('"')) is { } included && Walk(included, inLucid, depth + 1)) return true;
                    continue;
                }
                // A preamp of 0 dB changes nothing.
                if (command.Equals("Preamp", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(value.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var db) && db == 0) continue;
                if (inLucid && !lucidOn && command.Equals("VSTPlugin", StringComparison.OrdinalIgnoreCase)
                    && value.Contains("lucid_dsp", StringComparison.OrdinalIgnoreCase)) continue;
                if (inLucid && lucidOn) return true;
                use = "apo";
            }
            return false;
        }
    }

    // The microphone as Voice & Video chose it (else Windows' default for calls), by its endpoint id.
    private static string MicKey()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            var device = AudioDevices.Open(enumerator, capture: true, App.Services.Settings.Shared.DiscordInputDevice);
            if (device is null) return "default";
            try { return device.GetId(out var id) == 0 && id is { Length: > 0 and <= 512 } ? id : "default"; }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    private static IAudioEndpointVolume? Endpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            var device = AudioDevices.Open(enumerator, capture: true, App.Services.Settings.Shared.DiscordInputDevice);
            if (device is null) return null;
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                return device.Activate(ref iid, 23, 0, out var o) == 0 ? o as IAudioEndpointVolume : null;
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    // A kept tuning as one line in settings: "limit=15;gain=-3;spec=-6,1,0,-5,-9,-12,-15". One kept before 3.11 also has
    // hp=, gate= and hum= (Voice Focus no longer tunes those) and may say limit=100: it is read with the limit at its most.
    internal static string Format(VoiceTuning t) => string.Create(CultureInfo.InvariantCulture,
        $"limit={t.SuppressionLimitDb};gain={t.GainDb};spec={string.Join(',', t.Spectrum.Select(v => v.ToString(CultureInfo.InvariantCulture)))}");

    internal static VoiceTuning? Parse(string text)
    {
        // A key given twice takes the last (ToDictionary would throw, on the audio thread that reads it).
        var f = new Dictionary<string, string>();
        foreach (var p in text.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)) f[p[0]] = p[1];
        static float Num(string? v, float min, float max) =>
            float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && float.IsFinite(n) ? Math.Clamp(n, min, max) : float.NaN;
        var limit = Num(f.GetValueOrDefault("limit"), 6, NoiseSuppressor.MostDb);
        var gain = Num(f.GetValueOrDefault("gain"), -20, 20);
        if (float.IsNaN(limit) || float.IsNaN(gain)) return null;
        var spectrum = (f.GetValueOrDefault("spec") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => Num(v, -40, 40)).ToArray();
        if (spectrum.Length != VoiceAnalysis.Bands.Length || spectrum.Any(float.IsNaN)) spectrum = new float[VoiceAnalysis.Bands.Length];
        return new VoiceTuning((int)limit, gain, spectrum);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, nint context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, nint context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, nint context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, nint context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, nint context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(nint context);
        [PreserveSig] int VolumeStepDown(nint context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
    }
}
