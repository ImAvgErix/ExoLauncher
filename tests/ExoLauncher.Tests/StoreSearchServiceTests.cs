using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public class StoreSearchServiceTests
{
    [Theory]
    [InlineData("Mortal Shell", "mortal shell 2")]
    [InlineData("Mortal Shell", "mrotal sheel")]
    [InlineData("NieR:Automata", "nier automata")]
    [InlineData("Café Owner Simulator", "cafe owner")]
    [InlineData("Red Dead Redemption 2", "red redemption dead")]
    public void TitleMatchesQuery_AcceptsBoundedHumanSearchMistakes(string title, string query)
    {
        Assert.True(StoreSearchService.TitleMatchesQuery(title, query));
    }

    [Theory]
    [InlineData("Mortal Shell", "mortal kombat")]
    [InlineData("Mortal Shell", "mortal shell 22")]
    [InlineData("Far Cry 6", "war")]
    [InlineData("Control", "contour")]
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
    public async Task SearchAsync_FirstQueryIncludesNewlyWarmedEpicOwnedHit()
    {
        var releaseLoader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var epicPartial = new TaskCompletionSource<StoreSearchHit>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        LaunchTarget = "Fortnite",
                        Owned = true,
                        Installed = false,
                        CanInstall = true,
                        Source = "epic",
                    },
                ];
            },
            (_, _, _) => Task.FromResult<IReadOnlyList<StoreSearchHit>>(Array.Empty<StoreSearchHit>()));

        var initial = await service.SearchAsync(
            "Fortnite",
            Array.Empty<GameEntry>(),
            CancellationToken.None,
            hits =>
            {
                var match = hits.FirstOrDefault(item => item.Id == "epic:Fortnite");
                if (match is not null) epicPartial.TrySetResult(match);
            });
        Assert.Empty(initial);
        releaseLoader.SetResult();

        var hit = await epicPartial.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("epic:Fortnite", hit.Id);
        Assert.True(hit.Owned);
        Assert.True(hit.CanInstall);
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

        _ = await service.SearchAsync(
            "Fort",
            Array.Empty<GameEntry>(),
            cancelled.Token,
            hits => cancelledReceivedHit |= hits.Any(item => item.Id == "epic:Fortnite"));
        _ = await service.SearchAsync(
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
        _ = await currentPartial.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();

        Assert.False(cancelledReceivedHit);
    }
}
