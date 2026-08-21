using ExoLauncher.Models;

namespace ExoLauncher.Ui;

public enum NowKind
{
    Download,
    Playing,
    Update,
    Recent,
}

public readonly record struct NowPick(GameEntry Game, NowKind Kind);

/// <summary>
/// Download, playing, update, or last launched. Tile click does not move this.
/// </summary>
internal static class NowPicker
{
    public static NowPick? Pick(
        IReadOnlyList<GameEntry> games,
        InstallProgress? progress,
        IReadOnlyList<string> recentIds,
        Func<GameEntry, bool> isPlaying)
    {
        var pool = games.Where(UiFormat.IsLibraryRow).ToList();

        if (progress is { IsActive: true } && !string.IsNullOrWhiteSpace(progress.GameId))
        {
            var downloading = pool.FirstOrDefault(game => Matches(game, progress.GameId));
            if (downloading is not null) return new NowPick(downloading, NowKind.Download);
        }

        var playing = pool.FirstOrDefault(isPlaying);
        if (playing is not null) return new NowPick(playing, NowKind.Playing);

        var update = pool.FirstOrDefault(game => game.Installed && HasUpdate(game));
        if (update is not null) return new NowPick(update, NowKind.Update);

        var byClock = pool
            .Where(game => game.Installed && game.LastPlayedUtc is not null)
            .OrderByDescending(game => game.LastPlayedUtc)
            .FirstOrDefault();
        if (byClock is not null) return new NowPick(byClock, NowKind.Recent);

        foreach (var id in recentIds)
        {
            var hit = pool.FirstOrDefault(game => game.Installed && Matches(game, id));
            if (hit is not null) return new NowPick(hit, NowKind.Recent);
        }

        return null;
    }

    /// <summary>Tile click / library churn must not steal the banner. Download and Play still can.</summary>
    public static NowPick? Retain(
        IReadOnlyList<GameEntry> games,
        NowPick? picked,
        string? holdId,
        Func<GameEntry, bool> isPlaying)
    {
        _ = isPlaying;
        if (picked is null) return null;
        if (string.IsNullOrWhiteSpace(holdId)) return picked;
        if (Matches(picked.Value.Game, holdId)) return picked;
        if (picked.Value.Kind is NowKind.Download or NowKind.Playing) return picked;
        var held = games.FirstOrDefault(game => Matches(game, holdId));
        if (held is null || !UiFormat.IsLibraryRow(held) || !held.Installed) return picked;
        return new NowPick(held, HasUpdate(held) ? NowKind.Update : NowKind.Recent);
    }

    public static bool Matches(GameEntry game, string id)
    {
        var needle = id.ToLowerInvariant();
        if (game.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return true;
        if (game.Variants.Any(variant => variant.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            return true;
        var app = SteamApp(id);
        var own = SteamApp(game.Id);
        return app is not null && app == own;
    }

    private static bool HasUpdate(GameEntry game) =>
        game.PrimaryAction == "update" ||
        game.UpdateAvailable ||
        game.Variants.Any(variant => variant.UpdateAvailable);

    private static string? SteamApp(string id)
    {
        if (!id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return null;
        var app = id["steam:".Length..];
        return app.All(char.IsDigit) ? app : null;
    }
}
