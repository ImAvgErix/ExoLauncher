using ExoLauncher.Helpers;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Discord's own sounds for Exo's Discord client: New Message, Incoming Ring, Mute, Deafen, PTT,
/// User Join and the rest, the files Discord's web client plays (discord.com/assets, content-named,
/// so a file never changes), fetched once into Exo's data folder and played on the call's speakers.
/// Discord settings' Sounds lists turn each off, Sound Volume sets how loud all of them play, Disable All
/// Notification Sounds silences all of them, and so does Streamer Mode's Disable all sound effects.
/// </summary>
internal static class DiscordSounds
{
    // Discord's sound name -> its file in Discord's web client (October 2026).
    private static readonly Dictionary<string, string> Files = new()
    {
        ["message1"] = "3ed22d14f3c30bc4", ["message3"] = "4f53c2f31ea0cdd8", ["call_ringing"] = "c2a7111bb44b8da0",
        ["call_calling"] = "5146737af413d88e", ["deafen"] = "529ff198eac567af", ["undeafen"] = "b150f03c89944403",
        ["mute"] = "2d3b4ba32c34c862", ["unmute"] = "e74c4a06134a20e4", ["camera_on"] = "855607d0932ea396",
        ["camera_off"] = "b1dd204729fe78b4", ["disconnect"] = "dd0911025fe00b2e", ["ptt_start"] = "f5c8ec31889c5317",
        ["ptt_stop"] = "34b5969be3a063ba", ["user_join"] = "b135ff6c8e091b43", ["user_leave"] = "7b9a183742515fc2",
        ["user_moved"] = "4003230e4086b680",
    };

    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromSeconds(20));
    private static readonly Lock Gate = new();
    private static readonly HashSet<MediaPlayer> Playing = [];

    /// <summary>Streamer Mode with Disable all sound effects on.</summary>
    internal static bool StreamerSilent { get; set; }

    internal static bool IsSound(string? name) => name is not null && Files.ContainsKey(name);

    /// <summary>Plays a sound unless the user turned it off; <paramref name="preview"/> (Preview Sound) always plays.</summary>
    internal static void Play(string name, bool preview = false)
    {
        if (!Files.TryGetValue(name, out var file)) return;
        var s = App.Services.Settings.Current;
        if (!preview && (s.DiscordDisableAllSounds || StreamerSilent || s.DiscordDisabledSounds.Contains(name)
            || (name == "message3" && !s.DiscordSelectedChannelSound))) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await AssetAsync("sounds", file, "mp3").ConfigureAwait(false) is { } path) Start(path, s.DiscordOutputDevice, s.DiscordSoundVolume / 100.0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Debug("Discord sound not played: " + ex.GetType().Name);
            }
        });
    }

    /// <summary>A file of Discord's web client (discord.com/assets, content-named, so it never changes), fetched once into Exo's data folder.</summary>
    internal static async Task<string?> AssetAsync(string folder, string file, string extension)
    {
        var path = Path.Combine(PathHelper.AppDataDir, "voice", folder, $"{file}.{extension}");
        if (File.Exists(path)) return path;
        using var response = await Http.GetAsync($"https://discord.com/assets/{file}.{extension}").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 1_000_000) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (bytes.Length is 0 or > 1_000_000) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    private static void Start(string path, string? device, double volume)
    {
        var player = new MediaPlayer { Volume = volume, AudioCategory = MediaPlayerAudioCategory.SoundEffects };
        if (!string.IsNullOrEmpty(device))
        {
            // The Windows endpoint id Exo keeps, as the device id Windows' media player takes.
            try { player.AudioDevice = Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync($@"\\?\SWD#MMDEVAPI#{device}#{{e6327cad-dcec-4949-ae8a-991e976a79d2}}").AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        lock (Gate) Playing.Add(player);
        void Done()
        {
            lock (Gate) Playing.Remove(player);
            player.Dispose();
        }
        player.MediaEnded += (_, _) => Done();
        player.MediaFailed += (_, _) => Done();
        player.Source = MediaSource.CreateFromUri(new Uri(path));
        player.Play();
    }
}
