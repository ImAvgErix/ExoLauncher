using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam Web API <c>playtime_forever</c> for one Steam entry. PlaytimeService
/// takes the larger of it and localconfig; the account ledger fills owned
/// titles this PC never installed.
/// </summary>
internal static class SteamOwnedPlaytime
{
    public static int MinutesFor(GameEntry game, IReadOnlyDictionary<string, int>? minutesByAppId) =>
        game.Store == StoreKind.Steam &&
        minutesByAppId is { Count: > 0 } &&
        AppId(game) is { } appId &&
        minutesByAppId.TryGetValue(appId, out var minutes) &&
        minutes > 0
            ? minutes
            : 0;

    private static string? AppId(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.LaunchTarget) &&
            game.LaunchTarget.All(char.IsDigit))
            return game.LaunchTarget.Trim();
        if (game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) &&
            game.Id.Length > "steam:".Length)
        {
            var suffix = game.Id["steam:".Length..].Trim();
            if (suffix.Length > 0 && suffix.All(char.IsDigit))
                return suffix;
        }

        return null;
    }
}
