using System.Text.Json;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    /// <summary>
    /// game.settings: a game's best settings for this PC (<see cref="GameSettings"/>), each group with whether it is in place;
    /// supported false when Exo has none for it, found false until the game has written its settings file. With group
    /// (or all, which Optimize sends) and on, that group is set or put back first, which is refused while the game runs.
    /// </summary>
    private async Task<object> GameSettingsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };
        var group = ReadString(p, hasParams, "group");
        var all = ReadBool(p, hasParams, "all") == true;
        var on = ReadBool(p, hasParams, "on");
        return await Task.Run(object () =>
        {
            if (GameSettings.For(game, GameSettings.ThisPc()) is not { } profile) return new { ok = true, supported = false };
            var running = MainWindow.RunningLibraryGames().ContainsKey(game.Id) || GameSettings.Running(profile);
            if ((group is not null || all) && on is { } turnOn)
            {
                if (running) return new { ok = false, message = $"Close {profile.Title} first." };
                if (!GameSettings.Set(profile, all ? null : group, turnOn)) return new { ok = false, message = "Could not save. Try again." };
            }
            var files = GameSettings.ReadAll(profile);
            var groups = files.Count == 0 ? null : GameSettings.States(profile, files, GameSettings.Kept(profile.Id))
                .Select(state => new { id = state.Id, label = state.Label, on = state.On, exo = state.Exo }).ToList();
            return new { ok = true, supported = true, title = profile.Title, running, found = files.Count > 0, groups };
        }, ct).ConfigureAwait(false);
    }
}
