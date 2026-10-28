using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Lucid's own settings file (<c>%APPDATA%\LucidEQ\settings.json</c>), read and changed the way Lucid itself does.
/// Lucid loads it once at start and saves its whole copy on every change, so it is only written while Lucid is closed
/// (<see cref="LucidCompanion.ChangeAsync"/>). Every key Exo doesn't own (all of Lucid Voice, hotkeys, profiles, tone,
/// each game's own settings and the game EQ strength)
/// stays exactly as Lucid wrote it, and a save follows Lucid's Settings.Save: the last good file to <c>.bak</c>, the new
/// one to <c>.tmp</c>, read back and checked, then moved over.
/// </summary>
internal static class LucidSettings
{
    internal static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LucidEQ", "settings.json");

    /// <summary>The headphone id Lucid's own picker gives its IEMs (AudioSourceType 0).</summary>
    internal const string LucidIems = "lucid_iems";
    private const string LucidIemsName = "Lucid IEMs";
    // Lucid's signature tunings, which need its IEMs (ProfileManager.ProfileAccess).
    private static readonly string[] SignatureProfiles = ["north", "studio_s", "guru"];
    private static readonly TimeSpan NightStartDefault = new(21, 0, 0), NightEndDefault = new(6, 0, 0);

    internal sealed record View(
        string HeadphoneId, string HeadphoneName, bool HeadphoneChosen, IReadOnlyList<string> Favorites,
        bool GameMode, bool KeepOnAltTab, bool OnlyWhileGaming,
        bool NightShift, string NightStart, string NightEnd, bool NightSync,
        bool Notifications, bool Discord, int ToggleSound, int DetectSound,
        int HotkeyModifiers, int HotkeyKey, bool EqOn);

    /// <summary>One change from Exo; a null field is left alone.</summary>
    internal sealed record Change
    {
        public string? Headphone { get; init; }
        /// <summary>Headphone id to favorite (true) or not (false).</summary>
        public IReadOnlyDictionary<string, bool>? Favorites { get; init; }
        public bool? GameMode { get; init; }
        public bool? KeepOnAltTab { get; init; }
        public bool? OnlyWhileGaming { get; init; }
        public bool? NightShift { get; init; }
        public string? NightStart { get; init; }
        public string? NightEnd { get; init; }
        public bool? NightSync { get; init; }
        public bool? Notifications { get; init; }
        public bool? Discord { get; init; }
        /// <summary>Lucid's cue sounds, 0 (off) to 100: switching on or off, and a game detected.</summary>
        public int? ToggleSound { get; init; }
        public int? DetectSound { get; init; }
        /// <summary>Lucid's on/off hotkey: Windows' virtual key (0: none) and its modifiers (Alt 1, Ctrl 2, Shift 4, Win 8).</summary>
        public (int Modifiers, int Key)? Hotkey { get; init; }
        /// <summary>Lucid Voice off, as its own switch turns it off (Exo keeps it off: Exo Voice handles the mic).</summary>
        public bool VoiceOff { get; init; }

        /// <summary>This change with a later one laid over it.</summary>
        internal Change Then(Change next) => new()
        {
            Headphone = next.Headphone ?? Headphone,
            Favorites = Merge(Favorites, next.Favorites),
            GameMode = next.GameMode ?? GameMode,
            KeepOnAltTab = next.KeepOnAltTab ?? KeepOnAltTab,
            OnlyWhileGaming = next.OnlyWhileGaming ?? OnlyWhileGaming,
            NightShift = next.NightShift ?? NightShift,
            NightStart = next.NightStart ?? NightStart,
            NightEnd = next.NightEnd ?? NightEnd,
            NightSync = next.NightSync ?? NightSync,
            Notifications = next.Notifications ?? Notifications,
            Discord = next.Discord ?? Discord,
            ToggleSound = next.ToggleSound ?? ToggleSound,
            DetectSound = next.DetectSound ?? DetectSound,
            Hotkey = next.Hotkey ?? Hotkey,
            VoiceOff = next.VoiceOff || VoiceOff,
        };

        private static Dictionary<string, bool>? Merge(IReadOnlyDictionary<string, bool>? earlier, IReadOnlyDictionary<string, bool>? later)
        {
            if (earlier is null && later is null) return null;
            var merged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in (earlier ?? new Dictionary<string, bool>()).Concat(later ?? new Dictionary<string, bool>())) merged[key] = value;
            return merged;
        }
    }

    /// <summary>The file as a JSON object; null when it is missing, unreadable or not Lucid's shape.</summary>
    internal static JsonObject? Load(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static View Read(JsonObject root)
    {
        var other = Int(root, "AudioSourceType", 0) == 1;
        return new View(
            other ? Text(root, "SelectedHeadphoneId") : LucidIems,
            other ? Text(root, "SelectedHeadphoneName") : LucidIemsName,
            Bool(root, "HasCompletedAudioSourceOnboarding", false),
            root["FavoriteHeadphoneIds"] is JsonArray favorites ? favorites.Select(id => id?.GetValueKind() == JsonValueKind.String ? (string)id! : null).OfType<string>().ToArray() : [],
            Bool(root, "OptimizeForEsports", Bool(root, "AutoDetectionEnabled", true)),
            Bool(root, "KeepGameProfileOnAltTab", false),
            Bool(root, "OnlyWhileGaming", false),
            Bool(root, "NightShiftEnabled", false),
            Clock(Time(root, "NightShiftStartTime", NightStartDefault)),
            Clock(Time(root, "NightShiftEndTime", NightEndDefault)),
            Bool(root, "NightShiftSyncWithWindows", false),
            Bool(root, "EnableNotifications", true),
            Bool(root, "EnableDiscordRPC", false),
            Math.Clamp(Int(root, "ToggleSoundVolume", 25), 0, 100),
            Math.Clamp(Int(root, "AutoDetectionSoundVolume", 25), 0, 100),
            Int(root, "HotkeyModifiers", 0), Int(root, "HotkeyVirtualKey", 0),
            Bool(root, "LastEqState", true));
    }

    /// <summary>
    /// Applies a change as Lucid's own settings window and headphone picker would; null when it took, else why not.
    /// <paramref name="headphoneName"/> names a selectable headphone in Lucid's catalog (null: not in it);
    /// <paramref name="windowsSchedule"/> is Windows' Night light schedule, for Sync with Windows.
    /// </summary>
    internal static string? Apply(JsonObject root, Change change, Func<string, string?> headphoneName, Func<(TimeSpan Start, TimeSpan End)?> windowsSchedule)
    {
        var before = Read(root);
        if (change.Headphone is { } id)
        {
            if (id == LucidIems)
            {
                root["AudioSourceType"] = 0;
                root["SelectedHeadphoneId"] = "";
                root["SelectedHeadphoneName"] = LucidIemsName;
                // Lucid's ClearPendingApoDeviceSelectorPrompt: the IEMs need no device prompt.
                root["PendingApoDeviceSelectorPrompt"] = false;
                root["ApoDeviceSelectorDeferredUntilUtc"] = "0001-01-01T00:00:00";
            }
            else
            {
                if (headphoneName(id) is not { } name) return "Lucid doesn't have that headphone.";
                root["AudioSourceType"] = 1;
                root["SelectedHeadphoneId"] = id;
                root["SelectedHeadphoneName"] = name;
                if (SignatureProfiles.Contains(Text(root, "ActiveProfileId"), StringComparer.OrdinalIgnoreCase)) root["ActiveProfileId"] = "lucid";
            }
            // Lucid's AnswerHeadsetAsync: the first answer also turns the EQ on.
            if (!before.HeadphoneChosen) root["LastEqState"] = true;
            root["HasCompletedAudioSourceOnboarding"] = true;
        }
        if (change.Favorites is { Count: > 0 } favorites)
        {
            var ids = new HashSet<string>(before.Favorites, StringComparer.OrdinalIgnoreCase);
            foreach (var (favorite, on) in favorites)
                if (on) ids.Add(favorite); else ids.Remove(favorite);
            root["FavoriteHeadphoneIds"] = new JsonArray(ids.Order(StringComparer.OrdinalIgnoreCase).Select(one => (JsonNode)one!).ToArray());
        }
        if (change.GameMode is { } gameMode)
        {
            root["OptimizeForEsports"] = gameMode;
            root.Remove("AutoDetectionEnabled"); // Lucid's old name for it, which would win on load if it came later
        }
        if (change.KeepOnAltTab is { } keep) root["KeepGameProfileOnAltTab"] = keep;
        if (change.OnlyWhileGaming is { } only)
        {
            // Lucid's OnlyWhileGamingPolicy.Release: turning the option off with Game Mode on gives the EQ back.
            if (before.OnlyWhileGaming && !only && (change.GameMode ?? before.GameMode)) root["LastEqState"] = true;
            root["OnlyWhileGaming"] = only;
        }
        if (change.NightStart is { } start)
        {
            if (ParseClock(start) is not { } time) return "Pick a start time.";
            root["NightShiftStartTime"] = time.ToString("c", CultureInfo.InvariantCulture);
        }
        if (change.NightEnd is { } end)
        {
            if (ParseClock(end) is not { } time) return "Pick an end time.";
            root["NightShiftEndTime"] = time.ToString("c", CultureInfo.InvariantCulture);
        }
        if (change.NightSync is { } sync)
        {
            root["NightShiftSyncWithWindows"] = sync;
            // Lucid's SyncWithWindows_Changed: the times follow Windows' Night light, else its defaults.
            if (sync)
            {
                var (from, to) = windowsSchedule() ?? (NightStartDefault, NightEndDefault);
                root["NightShiftStartTime"] = from.ToString("c", CultureInfo.InvariantCulture);
                root["NightShiftEndTime"] = to.ToString("c", CultureInfo.InvariantCulture);
            }
        }
        if (change.NightShift is { } night)
        {
            root["NightShiftEnabled"] = night;
            // Lucid's ResyncNightShiftActiveToActiveProfile.
            root["NightShiftActive"] = Bool(root, "LastEqState", false) && string.Equals(Text(root, "ActiveProfileId"), "nightshift", StringComparison.OrdinalIgnoreCase);
        }
        if (change.Notifications is { } notifications) root["EnableNotifications"] = notifications;
        if (change.Discord is { } discord) root["EnableDiscordRPC"] = discord;
        // Lucid's own range (Settings.Validate resets anything outside 0-100 to 25).
        if (change.ToggleSound is { } toggle) { if (toggle is < 0 or > 100) return "Sound volume is 0 to 100%."; root["ToggleSoundVolume"] = toggle; }
        if (change.DetectSound is { } detect) { if (detect is < 0 or > 100) return "Sound volume is 0 to 100%."; root["AutoDetectionSoundVolume"] = detect; }
        if (change.Hotkey is { } hotkey)
        {
            var (modifiers, key) = hotkey;
            if (key is < 0 or > 254 || modifiers is < 0 or > 15) return "Pick another key.";
            // 0 is no key, as Lucid's own quick-profile hotkeys store it.
            root["HotkeyVirtualKey"] = key;
            root["HotkeyModifiers"] = key == 0 ? 0 : modifiers;
        }
        if (change.VoiceOff)
        {
            // Lucid's LucidVoiceProfileStore.MarkDisabledInSettings.
            root["LucidVoiceEnabled"] = false;
            root["LucidVoicePausedForMicChange"] = false;
        }
        return null;
    }

    /// <summary>Lucid's Settings.Save: the last good file to .bak, the new one through .tmp, read back, then moved over.</summary>
    internal static void Save(string path, JsonObject root)
    {
        var tmp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            if (Load(path) is not null) File.Copy(path, path + ".bak", overwrite: true);
            var text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), -1, leaveOpen: true)) writer.Write(text);
                stream.Flush(flushToDisk: true);
            }
            // The checks Lucid's own save makes before it trusts the new file.
            if (Load(tmp) is not { } back || Int(back, "GlobalGameEqStrength", 100) is < 50 or > 100 ||
                Int(back, "ToggleSoundVolume", 25) is < 0 or > 100 || Int(back, "AutoDetectionSoundVolume", 25) is < 0 or > 100)
                throw new InvalidDataException("Lucid's settings didn't read back.");
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Windows' Night light schedule, read as Lucid reads it (bytes 23, 24, 27 and 28 of its settings blob).</summary>
    internal static (TimeSpan Start, TimeSpan End)? WindowsNightLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.settings\windows.data.bluelightreduction.settings");
            return key?.GetValue("Data") is byte[] data ? NightLightSchedule(data) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    internal static (TimeSpan Start, TimeSpan End)? NightLightSchedule(byte[] data) =>
        data.Length > 28 && data[23] < 24 && data[24] < 60 && data[27] < 24 && data[28] < 60
            ? (new TimeSpan(data[23], data[24], 0), new TimeSpan(data[27], data[28], 0))
            : null;

    /// <summary>"21:30" on Lucid's half-hour steps.</summary>
    internal static TimeSpan? ParseClock(string text) =>
        TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out var time) && time.TotalHours < 24 && time.Minutes % 30 == 0
            ? time
            : null;

    private static string Clock(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static bool Bool(JsonObject root, string name, bool fallback) =>
        root[name]?.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback };

    private static int Int(JsonObject root, string name, int fallback) =>
        root[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;

    private static string Text(JsonObject root, string name) =>
        root[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : "";

    private static TimeSpan Time(JsonObject root, string name, TimeSpan fallback) =>
        TimeSpan.TryParse(Text(root, name), CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero && time.TotalHours < 24 ? time : fallback;
}
