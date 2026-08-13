using ExoLauncher.Models;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public class StoreSearchServiceTests
{
    [Fact]
    public void BuildSteamCatalogHit_UnknownOwnershipRemainsAPurchaseAction()
    {
        const string appId = "999999991";
        var hit = StoreSearchService.BuildSteamCatalogHit(
            appId,
            "Catalog Only Test Game",
            Array.Empty<GameEntry>());

        Assert.Equal("steam:" + appId, hit.Id);
        Assert.Equal(appId, hit.LaunchTarget);
        Assert.False(hit.Owned);
        Assert.False(hit.Installed);
        Assert.False(hit.CanInstall);
    }

    [Fact]
    public void BuildSteamCatalogHit_PreservesLocallyProvenOwnershipWithoutSteamClient()
    {
        var library = new[]
        {
            new GameEntry
            {
                Id = "steam:1817070",
                Title = "Marvel's Spider-Man Remastered",
                Store = StoreKind.Steam,
                LaunchTarget = "1817070",
                Owned = true,
                Installed = false,
                CanInstall = true,
            },
        };

        var hit = StoreSearchService.BuildSteamCatalogHit(
            "1817070",
            "Marvel's Spider-Man Remastered",
            library);

        Assert.True(hit.Owned);
        Assert.False(hit.Installed);
        Assert.True(hit.CanInstall);
    }

    [Theory]
    [InlineData("1620730", "Hell is Us")]
    [InlineData("1817070", "Marvel's Spider-Man Remastered")]
    [InlineData("252950", "Rocket League")]
    public void BuildSteamCatalogHit_UsesAccountProvenOwnershipForAnyExactAppId(string appId, string title)
    {
        var library = new[]
        {
            new GameEntry
            {
                Id = "steam:" + appId,
                Title = title,
                Store = StoreKind.Steam,
                LaunchTarget = appId,
                Owned = true,
                Installed = false,
                CanInstall = true,
            },
        };

        var hit = StoreSearchService.BuildSteamCatalogHit(appId, title, library);

        Assert.True(hit.Owned);
        Assert.True(hit.CanInstall);
        Assert.False(hit.Installed);
    }

    [Fact]
    public void BuildSteamCatalogHit_MatchesLibraryIdWhenLaunchTargetIsMissing()
    {
        var library = new[]
        {
            new GameEntry
            {
                Id = "steam:1817070",
                Title = "Marvel's Spider-Man Remastered",
                Store = StoreKind.Steam,
                LaunchTarget = null,
                Owned = true,
                Installed = false,
                CanInstall = true,
            },
        };

        var hit = StoreSearchService.BuildSteamCatalogHit(
            "1817070",
            "Marvel's Spider-Man Remastered",
            library);

        Assert.True(hit.Owned);
        Assert.True(hit.CanInstall);
    }

    [Fact]
    public void BuildSteamCatalogHit_MatchesSteamVariantOnAGroupedCard()
    {
        var library = new[]
        {
            new GameEntry
            {
                Id = "epic:Fortnite",
                Title = "Fortnite",
                Store = StoreKind.Epic,
                LaunchTarget = "Fortnite",
                Owned = true,
                Installed = true,
                Variants =
                [
                    new GameVariant
                    {
                        Id = "steam:1172470",
                        Store = StoreKind.Steam,
                        Installed = false,
                        Owned = true,
                        CanInstall = true,
                        LaunchTarget = "1172470",
                    },
                ],
            },
        };

        var hit = StoreSearchService.BuildSteamCatalogHit("1172470", "Apex Legends", library);

        Assert.True(hit.Owned);
        Assert.False(hit.Installed);
        Assert.True(hit.CanInstall);
    }

    [Theory]
    [InlineData("Mortal Shell", "mortal shell 2")]
    [InlineData("Mortal Shell", "mrotal sheel")]
    [InlineData("NieR:Automata", "nier automata")]
    [InlineData("Café Owner Simulator", "cafe owner")]
    [InlineData("Red Dead Redemption 2", "red redemption dead")]
    [InlineData("Marvel's Spider-Man Remastered", "spiderman remastered")]
    public void TitleMatchesQuery_AcceptsBoundedHumanSearchMistakes(string title, string query)
    {
        Assert.True(StoreSearchService.TitleMatchesQuery(title, query));
    }

    [Theory]
    [InlineData("Mortal Shell", "mortal kombat")]
    [InlineData("Mortal Shell", "mortal shell 22")]
    [InlineData("Far Cry 6", "war")]
    [InlineData("Control", "contour")]
    [InlineData("Marvel's Spider-Man Remastered", "steam")]
    [InlineData("Tom Clancy's Rainbow Six Siege", "steam")]
    public void TitleMatchesQuery_RejectsUnrelatedOrOverFuzzyResults(string title, string query)
    {
        Assert.False(StoreSearchService.TitleMatchesQuery(title, query));
    }

    [Fact]
    public void TitleMatchScore_KeepsExactAndPrefixAheadOfFuzzyMatches()
    {
        var exact = StoreSearchService.TitleMatchScore("Mortal Shell", "mortal shell");
        var prefix = StoreSearchService.TitleMatchScore("Mortal Shell", "mortal");
        var fuzzy = StoreSearchService.TitleMatchScore("Mortal Shell", "mrotal sheel");

        Assert.True(exact > prefix);
        Assert.True(prefix > fuzzy);
    }

    [Fact]
    public void TitleMatchScore_RanksConcatenatedHyphenatedTitleAsARealMatch()
    {
        var match = StoreSearchService.TitleMatchScore("Marvel's Spider-Man Remastered", "spiderman remastered");
        var unrelated = StoreSearchService.TitleMatchScore("Marvel's Guardians of the Galaxy", "spiderman remastered");

        Assert.True(match >= 0);
        Assert.True(unrelated < 0);
    }

    [Fact]
    public async Task SearchAsync_SurfacesAndRanksInstalledMatchForAccidentalSequelSuffix()
    {
        var service = new StoreSearchService(
            _ => Task.FromResult(new List<StoreSearchHit>()),
            (_, _, _) => Task.FromResult<IReadOnlyList<StoreSearchHit>>(Array.Empty<StoreSearchHit>()));
        var library = new[]
        {
            new GameEntry
            {
                Id = "steam:111",
                Title = "Mortal Shell",
                Store = StoreKind.Steam,
                LaunchTarget = "111",
                Installed = true,
                Owned = true,
            },
            new GameEntry
            {
                Id = "steam:222",
                Title = "Mortal Kombat 11",
                Store = StoreKind.Steam,
                LaunchTarget = "222",
                Installed = true,
                Owned = true,
            },
        };

        var hits = await service.SearchAsync("mortal shell 2", library);

        var hit = Assert.Single(hits);
        Assert.Equal("Mortal Shell", hit.Title);
        Assert.True(hit.Installed);
    }

    [Fact]
    public async Task SearchAsync_ExcludesTheAddPortableUtilityRow()
    {
        var service = new StoreSearchService(
            _ => Task.FromResult(new List<StoreSearchHit>()),
            (_, _, _) => Task.FromResult<IReadOnlyList<StoreSearchHit>>(Array.Empty<StoreSearchHit>()));
        var library = new[]
        {
            new GameEntry
            {
                Id = LocalAdapter.AddPortableId,
                Title = "Add portable game",
                Store = StoreKind.Local,
                Owned = true,
                CanInstall = true,
            },
        };

        var hits = await service.SearchAsync("portable", library);

        Assert.Empty(hits);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    public async Task SearchAsync_EmptyOrShortQuery_ReturnsEmpty(string query)
    {
        var svc = new StoreSearchService();
        var hits = await svc.SearchAsync(query, Array.Empty<GameEntry>());
        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchAsync_FinalResultWaitsForDelayedEpicOwnedProvider()
    {
        var releaseLoader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StoreSearchService(
            async ct =>
            {
                loaderStarted.TrySetResult();
                await releaseLoader.Task.WaitAsync(ct);
                return
                [
                    new StoreSearchHit
                    {
                        Id = "epic:Fortnite",
                        Title = "Fortnite",
                        Store = StoreKind.Epic,
                        LaunchTarget = "Fortnite",
                        Owned = true,
                        Installed = false,
                        CanInstall = true,
                        Source = "epic",
                    },
                ];
            },
            (_, _, _) => Task.FromResult<IReadOnlyList<StoreSearchHit>>(Array.Empty<StoreSearchHit>()));

        var search = service.SearchAsync(
            "Fortnite",
            Array.Empty<GameEntry>(),
            CancellationToken.None,
            _ => { });
        await loaderStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(search.IsCompleted);

        releaseLoader.SetResult();

        var hit = Assert.Single(await search.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("epic:Fortnite", hit.Id);
        Assert.True(hit.Owned);
        Assert.True(hit.CanInstall);
    }

    [Fact]
    public async Task SearchAsync_PublishesSteamResultWhileOwnedProviderFinishes()
    {
        var releaseLoader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steamPartial = new TaskCompletionSource<StoreSearchHit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StoreSearchService(
            async ct =>
            {
                loaderStarted.TrySetResult();
                await releaseLoader.Task.WaitAsync(ct);
                return [];
            },
            (_, _, _) => Task.FromResult<IReadOnlyList<StoreSearchHit>>(
            [
                new StoreSearchHit
                {
                    Id = "steam:393080",
                    Title = "Call of the Sea",
                    Store = StoreKind.Steam,
                    LaunchTarget = "393080",
                    CanInstall = true,
                    Source = "steam",
                },
            ]));

        var search = service.SearchAsync(
            "Call of the Sea",
            Array.Empty<GameEntry>(),
            CancellationToken.None,
            hits =>
            {
                var match = hits.FirstOrDefault(item => item.Id == "steam:393080");
                if (match is not null) steamPartial.TrySetResult(match);
            });

        await loaderStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var painted = await steamPartial.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Call of the Sea", painted.Title);
        Assert.False(search.IsCompleted);

        releaseLoader.SetResult();
        Assert.Contains(await search.WaitAsync(TimeSpan.FromSeconds(2)), item => item.Id == "steam:393080");
    }

    [Theory]
    [InlineData(StoreKind.Epic, "epic:Strinova")]
    [InlineData(StoreKind.Gog, "gog:1544321")]
    [InlineData(StoreKind.Xbox, "xbox:Strinova")]
    [InlineData(StoreKind.Riot, "riot:strinova")]
    [InlineData(StoreKind.Ea, "ea:Strinova")]
    [InlineData(StoreKind.Ubisoft, "ubisoft:Strinova")]
    [InlineData(StoreKind.BattleNet, "battlenet:Strinova")]
    [InlineData(StoreKind.Amazon, "amazon:Strinova")]
    [InlineData(StoreKind.Rockstar, "rockstar:Strinova")]
    [InlineData(StoreKind.Local, "local:Strinova")]
    [InlineData(StoreKind.Steam, "steam:2276390")]
    public async Task SearchAsync_LibraryTitleOnAnyStoreSuppressesSteamCatalogBuy(
        StoreKind store, string libraryId)
    {
        var service = new StoreSearchService(
            _ => Task.FromResult(new List<StoreSearchHit>()),
            (_, _, _) => Task.FromResult<IReadOnlyList<StoreSearchHit>>(
            [
                new StoreSearchHit
                {
                    Id = "steam:2276390",
                    Title = "Strinova™",
                    Store = StoreKind.Steam,
                    LaunchTarget = "2276390",
                    Owned = false,
                    Installed = false,
                    CanInstall = false,
                    Source = "steam",
                },
            ]));

        var hits = await service.SearchAsync("strinova",
        [
            new GameEntry
            {
                Id = libraryId,
                Title = "Strinova",
                Store = store,
                LaunchTarget = store == StoreKind.Steam ? "2276390" : libraryId,
                Owned = true,
                Installed = true,
                CanInstall = false,
            },
        ]);

        Assert.Contains(hits, hit => hit.Id == libraryId && hit.Installed);
        Assert.DoesNotContain(hits, hit =>
            hit.Store == StoreKind.Steam && !hit.Owned && !hit.Installed && !hit.CanInstall);
    }

    [Fact]
    public async Task SearchAsync_DifferentTitlesDoNotCollapseAcrossStores()
    {
        var service = new StoreSearchService(
            _ => Task.FromResult(new List<StoreSearchHit>()),
            (_, _, _) => Task.FromResult<IReadOnlyList<StoreSearchHit>>(
            [
                new StoreSearchHit
                {
                    Id = "steam:11450",
                    Title = "Hades II",
                    Store = StoreKind.Steam,
                    LaunchTarget = "11450",
                    Owned = false,
                    Source = "steam",
                },
            ]));

        var hits = await service.SearchAsync("hades",
        [
            new GameEntry
            {
                Id = "epic:Hades",
                Title = "Hades",
                Store = StoreKind.Epic,
                Owned = true,
                Installed = true,
            },
        ]);

        Assert.Contains(hits, hit => hit.Id == "epic:Hades");
        Assert.Contains(hits, hit => hit.Id == "steam:11450" && !hit.Owned);
    }

    [Fact]
    public async Task SearchAsync_AccountProvenEpicOwnershipIsInstallable()
    {
        var service = new StoreSearchService(
            _ => Task.FromResult(new List<StoreSearchHit>()),
            (_, _, _) => Task.FromResult<IReadOnlyList<StoreSearchHit>>(Array.Empty<StoreSearchHit>()));

        var hits = await service.SearchAsync("Fortnite",
        [
            new GameEntry
            {
                Id = "epic:Fortnite",
                Title = "Fortnite",
                Store = StoreKind.Epic,
                LaunchTarget = "Fortnite",
                Owned = true,
                Installed = false,
                CanInstall = true,
            },
        ]);

        var hit = Assert.Single(hits);
        Assert.Equal("epic:Fortnite", hit.Id);
        Assert.True(hit.Owned);
        Assert.True(hit.CanInstall);
    }

    [Theory]
    [InlineData("Fortnite")]
    [InlineData("Rocket League")]
    public void IsSearchableEpicTitle_AllowsGames(string title)
    {
        Assert.True(StoreSearchService.IsSearchableEpicTitle(title));
    }

    [Theory]
    [InlineData("Wait For Players System")]
    [InlineData("AI for NPC, MetaHuman Framework")]
    [InlineData("Unreal Engine Blueprint Toolkit Sample")]
    public void IsSearchableEpicTitle_RejectsDeveloperAssets(string title)
    {
        Assert.False(StoreSearchService.IsSearchableEpicTitle(title));
    }

    [Fact]
    public void IsSearchableEpicRow_UsesMetadataCategoriesAndHasAnExplicitUnknownPolicy()
    {
        var game = new LegendaryCli.GameRow("Fortnite", "Fortnite", null, null, false)
        {
            Categories = ["games"],
        };
        var asset = new LegendaryCli.GameRow("BlandAsset", "Creative Pack", null, null, false)
        {
            Categories = ["assets"],
        };
        var unknown = new LegendaryCli.GameRow("Unknown", "Rocket League", null, null, false);

        Assert.True(StoreSearchService.IsSearchableEpicRow(game));
        Assert.False(StoreSearchService.IsSearchableEpicRow(asset));
        Assert.True(StoreSearchService.IsSearchableEpicRow(unknown));
    }

    [Fact]
    public async Task SearchAsync_CancelledQueryCannotReceiveAnotherQuerysWarmPartial()
    {
        var releaseLoader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledReceivedHit = false;
        var currentPartial = new TaskCompletionSource<StoreSearchHit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StoreSearchService(
            async ct =>
            {
                await releaseLoader.Task.WaitAsync(ct);
                return
                [
                    new StoreSearchHit
                    {
                        Id = "epic:Fortnite",
                        Title = "Fortnite",
                        Store = StoreKind.Epic,
                        Owned = true,
                        CanInstall = true,
                        Source = "epic",
                    },
                ];
            },
            (_, _, _) => Task.FromResult<IReadOnlyList<StoreSearchHit>>(Array.Empty<StoreSearchHit>()));
        using var cancelled = new CancellationTokenSource();

        var firstSearch = service.SearchAsync(
            "Fort",
            Array.Empty<GameEntry>(),
            cancelled.Token,
            hits => cancelledReceivedHit |= hits.Any(item => item.Id == "epic:Fortnite"));
        await Task.Yield();
        var currentSearch = service.SearchAsync(
            "Fortnite",
            Array.Empty<GameEntry>(),
            CancellationToken.None,
            hits =>
            {
                var match = hits.FirstOrDefault(item => item.Id == "epic:Fortnite");
                if (match is not null) currentPartial.TrySetResult(match);
            });

        cancelled.Cancel();
        releaseLoader.SetResult();
        var final = await currentSearch.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(final, item => item.Id == "epic:Fortnite");
        try { await firstSearch; } catch (OperationCanceledException) { }
        await Task.Yield();

        Assert.False(cancelledReceivedHit);
    }
}
