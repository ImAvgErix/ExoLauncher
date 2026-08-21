using ExoLauncher.Models;
using ExoLauncher.Ui;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class NowPickerTests
{
    [Fact]
    public void Pick_PrefersDownloadThenPlayingThenUpdateThenRecent()
    {
        var recent = Game("steam:1", lastPlayed: DateTimeOffset.UtcNow.AddDays(-2));
        var update = Game("steam:2", update: true);
        var playing = Game("steam:3");
        var downloading = Game("steam:4");
        var games = new[] { recent, update, playing, downloading };
        var progress = new InstallProgress { Phase = InstallPhase.Downloading, GameId = downloading.Id };

        Assert.Equal(downloading.Id, NowPicker.Pick(games, progress, [], _ => false)!.Value.Game.Id);
        Assert.Equal(playing.Id, NowPicker.Pick(games, null, [], g => g.Id == playing.Id)!.Value.Game.Id);
        Assert.Equal(update.Id, NowPicker.Pick(games, null, [], _ => false)!.Value.Game.Id);
        Assert.Equal(recent.Id, NowPicker.Pick([recent], null, [], _ => false)!.Value.Game.Id);
    }

    [Fact]
    public void Retain_KeepsHeldTitleUnlessDownloadOrPlayMovesTheBanner()
    {
        var held = Game("steam:1", lastPlayed: DateTimeOffset.UtcNow);
        var other = Game("steam:2", lastPlayed: DateTimeOffset.UtcNow.AddDays(-1));
        var games = new[] { held, other };
        var picked = NowPicker.Pick(games, null, [], _ => false);
        Assert.Equal(held.Id, picked!.Value.Game.Id);

        var retained = NowPicker.Retain(games, NowPicker.Pick([other], null, [], _ => false), held.Id, _ => false);
        Assert.Equal(held.Id, retained!.Value.Game.Id);

        var download = new InstallProgress { Phase = InstallPhase.Downloading, GameId = other.Id };
        var downloading = NowPicker.Pick(games, download, [], _ => false);
        var keepDownload = NowPicker.Retain(games, downloading, held.Id, _ => false);
        Assert.Equal(other.Id, keepDownload!.Value.Game.Id);
    }

    [Fact]
    public void ReactBanner_IsUpdateOrLastLaunched_WithoutAPicksStrip()
    {
        var now = ReadRepoFile("ui", "src", "lib", "now.ts");
        var stage = ReadRepoFile("ui", "src", "components", "NowStage.tsx");

        Assert.Contains("export function pickNow(", now, StringComparison.Ordinal);
        Assert.Contains("export function retainNow(", now, StringComparison.Ordinal);
        Assert.Contains("game.installed && game.lastPlayedUtc", now, StringComparison.Ordinal);
        Assert.DoesNotContain("export function nowPicks(", now, StringComparison.Ordinal);
        Assert.DoesNotContain("item.installed && item.isFavorite", now, StringComparison.Ordinal);
        Assert.DoesNotContain("kind: 'owned'", now, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-now-picks", stage, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-now-pick", stage, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));

    private static GameEntry Game(string id, bool update = false, DateTimeOffset? lastPlayed = null) =>
        new()
        {
            Id = id,
            Title = id,
            Store = StoreKind.Steam,
            Installed = true,
            UpdateAvailable = update,
            LastPlayedUtc = lastPlayed,
        };
}
