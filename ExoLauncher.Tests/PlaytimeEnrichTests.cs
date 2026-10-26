using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways enriched hours can come out wrong:
/// - a figure an earlier pass borrowed (Galaxy, tracker.gg) is read back as the store's own, loses its note and is added to a sibling store;
/// - Steam Web API hours land after the source was chosen, so Galaxy or a tracker wins over Steam's own figure;
/// - one account-wide tracker.gg total is counted once per store on a grouped card;
/// - a second Riot ID's import keeps the first player's larger hours, or a cleared Riot ID keeps them at all.
/// Fixture ids and titles match nothing on this PC (no Steam app, Galaxy row or tracker.gg game).
/// </summary>
public sealed class PlaytimeEnrichTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-enrich-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public PlaytimeEnrichTests()
    {
        Directory.CreateDirectory(_folder);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _folder);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    private const string SteamAppId = "999999917";

    private static GameEntry Steam(int? minutes) => new()
    {
        Id = "steam:" + SteamAppId, Title = "Exo Enrich Fixture Qx", Store = StoreKind.Steam,
        LaunchTarget = SteamAppId, PlaytimeMinutes = minutes,
    };

    [Fact]
    public void AnIncomingFigureIsNeverTakenAsTheStores()
    {
        // A painted or cached row still carrying an earlier pass's borrowed figure.
        var enriched = Assert.Single(PlaytimeService.Enrich([Steam(777)]));
        Assert.Null(enriched.PlaytimeMinutes);
    }

    [Fact]
    public void SteamWebApiHoursAreTheStoresFigureAndStayOnReEnrich()
    {
        var account = new Dictionary<string, int> { [SteamAppId] = 300 };
        var once = Assert.Single(PlaytimeService.Enrich([Steam(null)], account));
        Assert.Equal(300, once.PlaytimeMinutes);
        var twice = Assert.Single(PlaytimeService.Enrich([once], account));
        Assert.Equal(300, twice.PlaytimeMinutes);
    }

    [Fact]
    public void StoreFiguresFromDifferentStoresStillAddUp()
    {
        var steam = new GameEntry { Id = "steam:exo-add-fixture", Title = "Exo Add Fixture", Store = StoreKind.Steam, PlaytimeMinutes = 100 };
        var epic = new GameEntry { Id = "epic:exo-add-fixture", Title = "Exo Add Fixture", Store = StoreKind.Epic, PlaytimeMinutes = 50 };
        Assert.Equal(150, Assert.Single(LibraryService.GroupVariants([steam, epic])).PlaytimeMinutes);
    }

    [Fact]
    public void ALifetimeTotalCorrectsAnEarlierOverCountButAPartialReadingOnlyRaises()
    {
        var riot = new GameEntry { Id = "riot:exo-fixture-lifetime", Title = "Exo Riot Lifetime Fixture Qx", Store = StoreKind.Riot };
        int? Minutes() => Assert.Single(PlaytimeService.Enrich([riot])).PlaytimeMinutes;
        var lifetime = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { riot.Id };

        Assert.True(PlaytimeService.TryImportRiotLifetime("PlayerC#EUW", new Dictionary<string, int> { [riot.Id] = 9000 }));
        // The same Riot ID read again, and tracker.gg now gives the real total: the old, higher figure goes.
        Assert.True(PlaytimeService.TryImportRiotLifetime("PlayerC#EUW", new Dictionary<string, int> { [riot.Id] = 6000 }, lifetime));
        Assert.Equal(6000, Minutes());
        // Queues added up, or one season, can be less than the player has: that never lowers what is shown.
        Assert.True(PlaytimeService.TryImportRiotLifetime("PlayerC#EUW", new Dictionary<string, int> { [riot.Id] = 1200 }));
        Assert.Equal(6000, Minutes());
        PlaytimeService.ForgetRiotImportUnlessFor(null);
    }

    [Fact]
    public void RiotImportBelongsToOneRiotId()
    {
        var riot = new GameEntry { Id = "riot:exo-fixture", Title = "Exo Riot Fixture Qx", Store = StoreKind.Riot };
        int? Minutes() => Assert.Single(PlaytimeService.Enrich([riot])).PlaytimeMinutes;

        Assert.True(PlaytimeService.TryImportRiotLifetime("PlayerA#EUW", new Dictionary<string, int> { [riot.Id] = 600 }));
        Assert.Equal(600, Minutes());
        Assert.True(PlaytimeService.TryImportRiotLifetime("PlayerB#EUW", new Dictionary<string, int> { [riot.Id] = 100 }));
        Assert.Equal(100, Minutes());

        PlaytimeService.ForgetRiotImportUnlessFor("playerb#euw");
        Assert.Equal(100, Minutes());
        PlaytimeService.ForgetRiotImportUnlessFor(null);
        Assert.Null(Minutes());
        Assert.False(File.Exists(Path.Combine(_folder, "exo-imported-lifetime.json")));
    }
}
