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
        Assert.Equal(steamVariant.PlaytimeMinutes, card.PlaytimeMinutes); // never Steam + Epic summed
        Assert.NotEqual(1_380, card.PlaytimeMinutes);
        var epicVariant = Assert.Single(card.Variants, v => v.Store == StoreKind.Epic);
        Assert.Equal("epic:Sugar", epicVariant.Id);
        Assert.Equal("Sugar", epicVariant.LaunchTarget);
        Assert.Equal(960, epicVariant.PlaytimeMinutes);

        var exactEpic = library.Find("epic:Sugar");
        Assert.NotNull(exactEpic);
        Assert.Equal(StoreKind.Epic, exactEpic!.Store);
        Assert.Equal("Sugar", exactEpic.LaunchTarget);
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
        Assert.Equal(960, card.PlaytimeMinutes);
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

    private static GameEntry Game(
        string id,
        string title,
        StoreKind store,
        bool installed,
        string target,
        int minutes,
        bool isFavorite = false) => new()
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
