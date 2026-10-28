using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Game profiles for a DualSense or Edge, as Exo Mouse has them: named sets of the controller's own settings, each
/// linked to games. Every field is optional (null: the controller's usual setting). Exo sends a linked game's profile,
/// merged over each pad's usual settings, just before the game starts (never during one), and the usual settings again
/// when it ends. Kept in controllers\games.json for every controller alike. Covered by ControllerSettingsTests.
/// </summary>
internal static partial class ControllerLab
{
    /// <summary>
    /// What a game profile changes. Light: "#RRGGBB", "off", "games" (left to the game), or null for the usual one.
    /// Vibration and TriggerEffects as <see cref="Strengths"/>; null fields keep the usual setting.
    /// </summary>
    internal sealed record PadOverrides
    {
        public string? Light { get; init; }
        public bool? PlayerLight { get; init; }
        public bool? MicOff { get; init; }
        public bool? SpeakerOff { get; init; }
        public string? Vibration { get; init; }
        public string? TriggerEffects { get; init; }
    }

    internal sealed record GameProfile
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public PadOverrides Settings { get; init; } = new();
        public string[] Games { get; init; } = [];
    }

    /// <summary>A pad's usual settings with a game profile's on top: each field the profile sets replaces the usual one.</summary>
    internal static PadSettings Merge(PadSettings? usual, PadOverrides game)
    {
        var u = usual ?? new PadSettings();
        return u with
        {
            Light = game.Light switch { null => u.Light, "games" => null, var light => light },
            PlayerLight = game.PlayerLight ?? u.PlayerLight,
            MicOff = game.MicOff ?? u.MicOff,
            SpeakerOff = game.SpeakerOff ?? u.SpeakerOff,
            Vibration = game.Vibration ?? u.Vibration,
            TriggerEffects = game.TriggerEffects ?? u.TriggerEffects,
        };
    }

    /// <summary>Whether Exo can keep and send this profile: a name, a known light, strengths it knows.</summary>
    internal static bool Valid(GameProfile p) =>
        p.Id.Length is > 0 and <= 64 && p.Name.Trim().Length is > 0 and <= 40 && p.Games.Length <= 2000 && Valid(Merge(null, p.Settings))
        && p.Settings.Light is not "on";

    /// <summary>The profile for a game, by any of its ids (a card that groups a game's stores has one per store); the last one listed wins.</summary>
    internal static GameProfile? ProfileFor(IEnumerable<GameProfile> profiles, IEnumerable<string> gameIds)
    {
        var ids = gameIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return profiles.LastOrDefault(p => p.Games.Any(ids.Contains));
    }

    private static string GameProfilesPath => Path.Combine(PathHelper.AppDataDir, "controllers", "games.json");

    internal static List<GameProfile> LoadGameProfiles()
    {
        try { return File.Exists(GameProfilesPath) ? JsonSerializer.Deserialize<List<GameProfile>>(File.ReadAllText(GameProfilesPath), SavedJson) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Controller: couldn't read its game profiles: {ex.GetType().Name}");
            return [];
        }
    }

    internal static void StoreGameProfiles(IReadOnlyList<GameProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GameProfilesPath)!);
        var temporary = GameProfilesPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, SavedJson));
        File.Move(temporary, GameProfilesPath, overwrite: true);
    }
}
