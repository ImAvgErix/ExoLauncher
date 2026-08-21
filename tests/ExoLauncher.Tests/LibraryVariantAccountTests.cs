using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class LibraryVariantAccountTests
{
    [Fact]
    public async Task RocketLeagueAcrossSteamAndEpic_IsOneCardWithExactSourceVariants()
    {
        var steam = Game("steam:252950", "Rocket League®", StoreKind.Steam, true, "252950", 420);
        var epic = Game("epic:Sugar", "Rocket League", StoreKind.Epic, true, "Sugar", 960);
        var library = new LibraryService(
            [new FixedAdapter("steam", StoreKind.Steam, steam), new FixedAdapter("epic", StoreKind.Epic, epic)],
            new SettingsService());

        var card = Assert.Single(await library.GetLibraryAsync(force: true));

        Assert.Equal("steam:252950", card.Id); // deterministic when both install states tie
        Assert.Equal("steam:252950", card.SelectedVariantId);
        Assert.Equal(2, card.Variants.Count);
        var steamVariant = Assert.Single(card.Variants, v => v.Store == StoreKind.Steam);
        var epicVariant = Assert.Single(card.Variants, v => v.Store == StoreKind.Epic);
        Assert.Equal("epic:Sugar", epicVariant.Id);
        Assert.Equal("Sugar", epicVariant.LaunchTarget);
        // Each source keeps its own store's hours. Steam's row is enriched from
        // this machine's localconfig, so only the relationship is asserted.
        Assert.Equal(960, epicVariant.PlaytimeMinutes);
        Assert.NotNull(steamVariant.PlaytimeMinutes);
        Assert.Equal(steamVariant.PlaytimeMinutes + 960, card.PlaytimeMinutes);
        Assert.True(card.PlaytimeMinutes > 960);

        var exactEpic = library.Find("epic:Sugar");
        Assert.NotNull(exactEpic);
        Assert.Equal(StoreKind.Epic, exactEpic!.Store);
        Assert.Equal("Sugar", exactEpic.LaunchTarget);
        // Switching source in the details overlay must show Epic's own hours,
        // never the card total and never Steam's number under an Epic label.
        Assert.Equal(960, exactEpic.PlaytimeMinutes);
    }

    [Fact]
    public async Task PreferredVariant_IsInstalledSourceBeforeStoreTieBreaker()
    {
        var steam = Game("steam:252950", "Rocket League", StoreKind.Steam, false, "252950", 420);
        var epic = Game("epic:Sugar", "Rocket League", StoreKind.Epic, true, "Sugar", 960);
        var library = new LibraryService(
            [new FixedAdapter("steam", StoreKind.Steam, steam), new FixedAdapter("epic", StoreKind.Epic, epic)],
            new SettingsService());

        var card = Assert.Single(await library.GetLibraryAsync(force: true));

        Assert.Equal("epic:Sugar", card.Id);
        Assert.Equal(StoreKind.Epic, card.Store);
        Assert.Equal("Sugar", card.LaunchTarget);
        Assert.Equal(
            card.Variants.Single(v => v.Store == StoreKind.Steam).PlaytimeMinutes + 960,
            card.PlaytimeMinutes);
        Assert.True(card.PlaytimeMinutes > 960);
    }

    [Fact]
    public void PreferredVariant_SurfacesAnInstalledSourcesPendingUpdate()
    {
        var steam = Game("steam:252950", "Rocket League", StoreKind.Steam, true, "252950", 420);
        var epic = new GameEntry
        {
            Id = "epic:Sugar",
            Title = "Rocket League",
            Store = StoreKind.Epic,
            Installed = true,
            Owned = true,
            UpdateAvailable = true,
            CanInstall = true,
            Path = Path.GetTempPath(),
            LaunchTarget = "Sugar",
            Status = "Update available",
        };

        var card = Assert.Single(LibraryService.GroupVariants([steam, epic]));

        Assert.Equal("epic:Sugar", card.Id);
        Assert.Equal("update", card.PrimaryAction);
        Assert.True(card.UpdateAvailable);
    }

    [Fact]
    public void GroupedCard_StaysPinnedWhenOnlyAlternateSourceWasFavorite()
    {
        var steam = Game("steam:252950", "Rocket League", StoreKind.Steam, true, "252950", 420);
        var epic = Game("epic:Sugar", "Rocket League", StoreKind.Epic, true, "Sugar", 960, isFavorite: true);

        var card = Assert.Single(LibraryService.GroupVariants([steam, epic]));

        Assert.Equal("steam:252950", card.Id);
        Assert.True(card.IsFavorite);
        Assert.All(card.Variants.Select(variant => variant.ToGameEntry(card)), game => Assert.True(game.IsFavorite));
    }

    [Fact]
    public async Task GroupedCard_UsesPersistedFavoriteAndRecencyFromAlternateSource()
    {
        var lastPlayed = DateTimeOffset.Parse("2026-08-11T12:34:56Z");
        var settings = new SettingsService(new AppSettings
        {
            Favorites = ["epic:Sugar"],
            LastPlayed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["epic:Sugar"] = lastPlayed.ToString("O"),
            },
        });
        var steam = Game("steam:252950", "Rocket League", StoreKind.Steam, true, "252950", 420);
        var epic = Game("epic:Sugar", "Rocket League", StoreKind.Epic, true, "Sugar", 960);
        var library = new LibraryService(
            [new FixedAdapter("steam", StoreKind.Steam, steam), new FixedAdapter("epic", StoreKind.Epic, epic)],
            settings);

        var card = Assert.Single(await library.GetLibraryAsync(force: true));

        Assert.Equal("steam:252950", card.Id);
        Assert.True(card.IsFavorite);
        Assert.Equal(lastPlayed, card.LastPlayedUtc);
    }

    [Fact]
    public void GroupedCard_TotalsEachStoresOwnHoursAndKeepsVariantsStoreLocal()
    {
        var steamPlayed = DateTimeOffset.Parse("2026-08-18T09:00:00Z");
        var epicPlayed = DateTimeOffset.Parse("2026-08-18T18:30:00Z");
        var steam = Game("steam:4000001", Title, StoreKind.Steam, true, "4000001", 420, lastPlayed: steamPlayed);
        var epic = Game("epic:FixtureTwoStore", Title, StoreKind.Epic, true, "FixtureTwoStore", 960, lastPlayed: epicPlayed);

        var card = Assert.Single(LibraryService.GroupVariants([steam, epic]));

        // Steam wins the tie-break, so the old projection published Steam's 420
        // and Steam's morning session on a card labelled "Steam · Epic".
        Assert.Equal(StoreKind.Steam, card.Store);
        Assert.Equal(1_380, card.PlaytimeMinutes);
        Assert.Equal(epicPlayed, card.LastPlayedUtc);
        Assert.Equal(420, card.Variants.Single(v => v.Store == StoreKind.Steam).PlaytimeMinutes);
        Assert.Equal(steamPlayed, card.Variants.Single(v => v.Store == StoreKind.Steam).LastPlayedUtc);
        Assert.Equal(960, card.Variants.Single(v => v.Store == StoreKind.Epic).PlaytimeMinutes);
        Assert.Equal(epicPlayed, card.Variants.Single(v => v.Store == StoreKind.Epic).LastPlayedUtc);
    }

    [Fact]
    public void OverlaySourcePlaytime_IsTheSelectedStoreNotTheCardTotal()
    {
        // Live Rocket League: Epic 11837, Steam 43. The card totals 11880.
        // Reading the card on an overlay whose Epic chip is already pressed
        // showed 198 hr, then 197 hr after the same Epic chip was clicked.
        var steam = Game("steam:4000099", Title, StoreKind.Steam, false, "4000099", 43);
        var epic = Game("epic:FixtureRlHours", Title, StoreKind.Epic, true, "FixtureRlHours", 11_837);

        var card = Assert.Single(LibraryService.GroupVariants([steam, epic]));

        Assert.Equal("epic:FixtureRlHours", card.Id);
        Assert.Equal(11_880, card.PlaytimeMinutes);
        Assert.Equal(11_837, LibraryService.PlaytimeMinutesForSource(card));
        Assert.Equal(11_837, LibraryService.PlaytimeMinutesForSource(card, card.Id));
        Assert.Equal(43, LibraryService.PlaytimeMinutesForSource(card, "steam:4000099"));
        Assert.Equal(
            LibraryService.PlaytimeMinutesForSource(card),
            LibraryService.PlaytimeMinutesForSource(card));
    }

    [Fact]
    public void GroupVariants_ScanOrderDoesNotChangePlaytimeOrVariantOrder()
    {
        var steam = Game("steam:4000088", Title, StoreKind.Steam, true, "4000088", 43);
        var epic = Game("epic:FixtureScanOrder", Title, StoreKind.Epic, true, "FixtureScanOrder", 11_837);

        var forward = Assert.Single(LibraryService.GroupVariants([steam, epic]));
        var reverse = Assert.Single(LibraryService.GroupVariants([epic, steam]));
        var again = Assert.Single(LibraryService.GroupVariants([steam, epic]));

        Assert.Equal(forward.Id, reverse.Id);
        Assert.Equal(forward.PlaytimeMinutes, reverse.PlaytimeMinutes);
        Assert.Equal(
            forward.Variants.Select(variant => (variant.Id, variant.PlaytimeMinutes)),
            reverse.Variants.Select(variant => (variant.Id, variant.PlaytimeMinutes)));
        Assert.Equal(forward.Id, again.Id);
        Assert.Equal(forward.PlaytimeMinutes, again.PlaytimeMinutes);
        Assert.Equal(
            forward.Variants.Select(variant => (variant.Id, variant.PlaytimeMinutes)),
            again.Variants.Select(variant => (variant.Id, variant.PlaytimeMinutes)));
        Assert.Equal(
            LibraryService.PlaytimeMinutesForSource(forward),
            LibraryService.PlaytimeMinutesForSource(reverse));
    }

    [Fact]
    public async Task GetLibraryAsync_SamePlaytimeOnASecondRead()
    {
        var steam = Game("steam:4000087", Title, StoreKind.Steam, true, "4000087", 43);
        var epic = Game("epic:FixtureSecondRead", Title, StoreKind.Epic, true, "FixtureSecondRead", 11_837);
        var library = new LibraryService(
            [new FixedAdapter("steam", StoreKind.Steam, steam), new FixedAdapter("epic", StoreKind.Epic, epic)],
            new SettingsService());

        var first = Assert.Single(await library.GetLibraryAsync(force: true));
        var second = Assert.Single(await library.GetLibraryAsync(force: true));

        Assert.Equal(first.PlaytimeMinutes, second.PlaytimeMinutes);
        Assert.Equal(
            LibraryService.PlaytimeMinutesForSource(first),
            LibraryService.PlaytimeMinutesForSource(second));
        Assert.Equal(
            first.Variants.Select(variant => (variant.Id, variant.PlaytimeMinutes)),
            second.Variants.Select(variant => (variant.Id, variant.PlaytimeMinutes)));
    }

    [Fact]
    public void GroupedCard_ShowsTheOneStoreThatKnowsHoursWithoutInventingAZero()
    {
        var steam = Game("steam:4000002", Title, StoreKind.Steam, true, "4000002", minutes: null);
        var epic = Game("epic:FixtureOneKnown", Title, StoreKind.Epic, true, "FixtureOneKnown", 960);

        var card = Assert.Single(LibraryService.GroupVariants([steam, epic]));

        Assert.Equal(960, card.PlaytimeMinutes);
        Assert.Null(card.Variants.Single(v => v.Store == StoreKind.Steam).PlaytimeMinutes);
        Assert.Equal(960, card.Variants.Single(v => v.Store == StoreKind.Epic).PlaytimeMinutes);
    }

    [Fact]
    public void GroupedCard_HasNoHoursWhenNoStoreReportsAny()
    {
        var steam = Game("steam:4000003", Title, StoreKind.Steam, true, "4000003", minutes: null);
        var gog = Game("gog:4000004", Title, StoreKind.Gog, true, "4000004", minutes: null);

        var card = Assert.Single(LibraryService.GroupVariants([steam, gog]));

        Assert.Null(card.PlaytimeMinutes);
        Assert.Null(card.LastPlayedUtc);
    }

    [Fact]
    public void GroupedCard_CountsOneStoreOnceEvenWithTwoRowsFromIt()
    {
        var steam = Game("steam:4000005", Title, StoreKind.Steam, true, "4000005", 420);
        var sameSteamAgain = Game("steam:4000006", Title, StoreKind.Steam, false, "4000006", 300);
        var epic = Game("epic:FixtureDuplicate", Title, StoreKind.Epic, true, "FixtureDuplicate", 960);

        var card = Assert.Single(LibraryService.GroupVariants([steam, sameSteamAgain, epic]));

        // One store is one history: 420 and 300 are the same account's rows, so
        // the larger reading wins instead of adding 720 to Epic's 960.
        Assert.Equal(1_380, card.PlaytimeMinutes);
    }

    [Fact]
    public async Task JustLaunchedSource_StampsTheCardAndItsOwnVariantNotTheSibling()
    {
        var launchedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var staleSteamSession = DateTimeOffset.UtcNow.AddHours(-5);
        var settings = new SettingsService(new AppSettings
        {
            LastPlayed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // What LaunchOrchestrator records the moment Exo starts a game.
                ["epic:FixtureLaunched"] = launchedAt.ToString("O"),
            },
        });
        var steam = Game("steam:4000007", Title, StoreKind.Steam, true, "4000007", 420, lastPlayed: staleSteamSession);
        var epic = Game("epic:FixtureLaunched", Title, StoreKind.Epic, true, "FixtureLaunched", 960);
        var library = new LibraryService(
            [new FixedAdapter("steam", StoreKind.Steam, steam), new FixedAdapter("epic", StoreKind.Epic, epic)],
            settings);

        var card = Assert.Single(await library.GetLibraryAsync(force: true));

        // Steam is the projected source and its own last session is five hours
        // old. The card still reports the launch, and so does the exact Epic
        // source the details overlay switches to.
        Assert.Equal(StoreKind.Steam, card.Store);
        Assert.Equal(launchedAt, card.LastPlayedUtc);
        Assert.Equal(launchedAt, card.Variants.Single(v => v.Store == StoreKind.Epic).LastPlayedUtc);
        Assert.Equal(launchedAt, library.Find("epic:FixtureLaunched")!.LastPlayedUtc);
        // The Steam copy was not the one that ran; it keeps its own last session.
        Assert.Equal(staleSteamSession, card.Variants.Single(v => v.Store == StoreKind.Steam).LastPlayedUtc);
    }

    [Fact]
    public async Task AccountSwitch_RescansInsteadOfReturningOtherUsersCachedLibrary()
    {
        var adapter = new ScopedAdapter("scope-a", Game("steam:one", "Account A", StoreKind.Steam, true, "1", 20));
        var library = new LibraryService([adapter], new SettingsService());
        Assert.Equal("steam:one", Assert.Single(await library.GetLibraryAsync(force: true)).Id);

        adapter.Scope = "scope-b";
        adapter.Game = Game("steam:two", "Account B", StoreKind.Steam, true, "2", 90);
        var switched = Assert.Single(await library.GetLibraryAsync());

        Assert.Equal("steam:two", switched.Id);
        Assert.Equal(90, switched.PlaytimeMinutes);
        Assert.DoesNotContain("steam:one", library.PeekCachedLibrary().Select(game => game.Id));
    }

    [Fact]
    public async Task AccountSwitchWithFailedRescan_DoesNotFallBackToOtherUsersEntries()
    {
        var adapter = new ScopedAdapter("scope-a", Game("epic:one", "Account A", StoreKind.Epic, true, "One", 20));
        var library = new LibraryService([adapter], new SettingsService());
        _ = await library.GetLibraryAsync(force: true);

        adapter.Scope = "scope-b";
        adapter.Fail = true;

        Assert.Empty(await library.GetLibraryAsync());
        Assert.Null(library.Find("epic:one"));
    }

    [Fact]
    public async Task UnknownAccount_KeepsMachineInstallButStripsAccountClaims()
    {
        var game = Game("epic:Sugar", "Rocket League", StoreKind.Epic, true, "Sugar", 960);
        var adapter = new ScopedAdapter(null, game);
        var library = new LibraryService([adapter], new SettingsService());

        var visible = Assert.Single(await library.GetLibraryAsync(force: true));

        Assert.True(visible.Installed);
        Assert.False(visible.Owned);
        Assert.Null(visible.PlaytimeMinutes);
        Assert.Null(visible.LastPlayedUtc);
        Assert.Equal("Sugar", visible.LaunchTarget);
    }

    /// <summary>
    /// Cross-store fixtures use ids no store on this machine can enrich, so the
    /// numbers under test are the fixture's own.
    /// </summary>
    private const string Title = "Exo cross store fixture";

    private static GameEntry Game(
        string id,
        string title,
        StoreKind store,
        bool installed,
        string target,
        int? minutes,
        bool isFavorite = false,
        DateTimeOffset? lastPlayed = null) => new()
    {
        Id = id,
        Title = title,
        Store = store,
        Installed = installed,
        Owned = true,
        CanInstall = true,
        Path = installed ? Path.GetTempPath() : null,
        LaunchTarget = target,
        PlaytimeMinutes = minutes,
        LastPlayedUtc = lastPlayed,
        Status = installed ? "Ready" : "Not installed",
        IsFavorite = isFavorite,
    };

    private class FixedAdapter(string id, StoreKind store, params GameEntry[] games) : IStoreAdapter
    {
        public StoreKind Store { get; } = store;
        public string Id { get; } = id;
        public string DisplayName => Id;
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) => Task.FromResult(new AuthResult { Ok = true });
        public virtual Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GameEntry>>(games);
        public Task<InstallResult> InstallAsync(GameEntry game, string? path, IProgress<InstallProgress>? progress, CancellationToken ct = default) => Task.FromResult(new InstallResult());
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) => Task.FromResult(new InstallResult());
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) => Task.FromResult(new LaunchResult());
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) => Task.FromResult(new InstallResult());
        public InstallProgress GetDownloadProgress(string gameId) => new() { GameId = gameId };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ScopedAdapter(string? scope, GameEntry game) : FixedAdapter("scoped", game.Store, game), IStoreAccountScope
    {
        public string? Scope { get; set; } = scope;
        public GameEntry Game { get; set; } = game;
        public bool Fail { get; set; }
        public string? GetActiveAccountScope() => Scope;
        public override Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("fixture failure");
            return Task.FromResult<IReadOnlyList<GameEntry>>([Game]);
        }
    }
}
