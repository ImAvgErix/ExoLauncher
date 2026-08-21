using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Ui;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Store-by-action honesty: a button that is enabled must either perform the
/// official action or report that it cannot. Silent success is the failure.
/// </summary>
public sealed class StoreActionMatrixTests
{
    [Theory]
    [InlineData(StoreKind.Steam, "steam:730", "730", "steam://store/730")]
    [InlineData(StoreKind.Gog, "gog:1207658924", "the_witcher_3_wild_hunt", "https://www.gog.com/en/game/the_witcher_3_wild_hunt")]
    [InlineData(StoreKind.Epic, "epic:catalog:fortnite", "fortnite", "https://store.epicgames.com/en-US/p/fortnite")]
    [InlineData(StoreKind.Riot, "riot:valorant", "valorant", "https://playvalorant.com/")]
    [InlineData(StoreKind.Riot, "riot:league_of_legends", "league_of_legends", "https://www.leagueoflegends.com/")]
    [InlineData(StoreKind.Riot, "riot:bacon", "bacon", "https://playruneterra.com/")]
    [InlineData(StoreKind.Riot, "riot:lion", "lion", "https://2xko.riotgames.com/")]
    [InlineData(StoreKind.Xbox, "xbox:starfield", "9NBLGGH4R2R6", "ms-windows-store://pdp/?ProductId=9NBLGGH4R2R6")]
    [InlineData(StoreKind.Ubisoft, "ubisoft:1234", "1234", "https://store.ubisoft.com/search?q=Catalog%20title")]
    [InlineData(StoreKind.Minecraft, "minecraft:java", "", "https://www.minecraft.net/get-minecraft")]
    [InlineData(StoreKind.Roblox, "roblox:player", "", "https://www.roblox.com/")]
    public void BuyUrl_OpensTheOfficialDestinationForUnownedCatalogHits(
        StoreKind store, string id, string launchTarget, string expected)
    {
        var game = CatalogHit(store, id, launchTarget);
        Assert.Equal(expected, Storefront.BuyUrl(game));
        Assert.Equal(expected, UiFormat.BuyUrl(game));
    }

    [Fact]
    public void BuyUrl_UsesOfficialHomepagesWhenTheTitleHasNoProductId()
    {
        Assert.StartsWith("ms-windows-store://search/?query=", Storefront.BuyUrl(CatalogHit(StoreKind.Xbox, "xbox:forza", "", "Forza Horizon 5")));
        Assert.Contains("ea.com", Storefront.BuyUrl(CatalogHit(StoreKind.Ea, "ea:apex", "", "Apex Legends")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shop.battle.net", Storefront.BuyUrl(CatalogHit(StoreKind.BattleNet, "battlenet:wow", "", "World of Warcraft")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gaming.amazon.com", Storefront.BuyUrl(CatalogHit(StoreKind.Amazon, "amazon:hades", "", "Hades")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rockstargames.com", Storefront.BuyUrl(CatalogHit(StoreKind.Rockstar, "rockstar:gta", "", "Grand Theft Auto V")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("itch.io", Storefront.BuyUrl(CatalogHit(StoreKind.Itch, "itch:celeste", "", "Celeste")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("paradoxinteractive.com", Storefront.BuyUrl(CatalogHit(StoreKind.Paradox, "paradox:stellaris", "", "Stellaris")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wargaming.com", Storefront.BuyUrl(CatalogHit(StoreKind.Wargaming, "wargaming:wot", "", "World of Tanks")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://www.riotgames.com/", Storefront.Destination(CatalogHit(StoreKind.Riot, "riot:unknown", "unknown")));
    }

    [Fact]
    public void BuyUrl_IsNullWhenInstallIsTheRealActionOrThereIsNoStorefront()
    {
        Assert.Null(Storefront.BuyUrl(CatalogHit(StoreKind.Steam, "steam:730", "730", owned: true, canInstall: true)));
        Assert.Null(Storefront.BuyUrl(new GameEntry
        {
            Id = "steam:730",
            Title = "Counter-Strike 2",
            Store = StoreKind.Steam,
            Installed = true,
            LaunchTarget = "730",
        }));
        Assert.Null(Storefront.BuyUrl(CatalogHit(StoreKind.Local, "local:some-game", "")));
        Assert.Null(Storefront.Destination(CatalogHit(StoreKind.Local, "local:some-game", "")));
        Assert.False(Storefront.HasPurchasableStorefront(StoreKind.Local));
        Assert.Contains("not sold", Storefront.UnavailableReason(StoreKind.Local), StringComparison.OrdinalIgnoreCase);
        Assert.Null(Storefront.UnavailableReason(StoreKind.Xbox));
    }

    [Fact]
    public void InstalledBuyUrl_RequiresExplicitNotOwnedState()
    {
        var refunded = new GameEntry
        {
            Id = "steam:730",
            Title = "Counter-Strike 2",
            Store = StoreKind.Steam,
            Installed = true,
            Owned = false,
            EntitlementState = EntitlementState.NotOwned,
            LaunchTarget = "730",
        };
        var unverified = new GameEntry
        {
            Id = "steam:730",
            Title = "Counter-Strike 2",
            Store = StoreKind.Steam,
            Installed = true,
            Owned = false,
            EntitlementState = EntitlementState.Unverified,
            LaunchTarget = "730",
        };

        Assert.Equal("steam://store/730", Storefront.BuyUrl(refunded));
        Assert.Null(Storefront.BuyUrl(unverified));
    }

    [Fact]
    public void EveryStoreKind_HasABuyDestination_ExceptLocal()
    {
        foreach (StoreKind store in Enum.GetValues<StoreKind>())
        {
            var game = CatalogHit(store, $"{store}:{store}", "", store.ToString());
            if (store == StoreKind.Local)
            {
                Assert.Null(Storefront.BuyUrl(game));
                Assert.Null(Storefront.Destination(game));
                Assert.False(Storefront.HasPurchasableStorefront(store));
                continue;
            }

            Assert.True(Storefront.HasPurchasableStorefront(store), store.ToString());
            Assert.False(string.IsNullOrWhiteSpace(Storefront.BuyUrl(game)), store.ToString());
            Assert.Equal(Storefront.BuyUrl(game), UiFormat.BuyUrl(game));
        }
    }

    [Fact]
    public async Task Repair_ReportsNotSupportedForListAndLaunchStores()
    {
        var xbox = new XboxAdapter();
        var orchestrator = new LaunchOrchestrator(
            new IStoreAdapter[] { xbox, new LocalAdapter() },
            new SettingsService(new AppSettings { AutoInstallRedistributables = false }),
            new DependencyService());

        var xboxGame = new GameEntry
        {
            Id = "xbox:fixture",
            Title = "Xbox fixture",
            Store = StoreKind.Xbox,
            Installed = true,
            LaunchTarget = @"C:\Games\Fixture\game.exe",
        };
        var xboxRepair = await orchestrator.RepairAsync(xboxGame);
        Assert.False(xboxRepair.Ok);
        Assert.Contains("cannot be verified", xboxRepair.Message, StringComparison.OrdinalIgnoreCase);

        var local = new GameEntry
        {
            Id = "local:fixture",
            Title = "Portable fixture",
            Store = StoreKind.Local,
            Installed = true,
            Path = Path.GetTempPath(),
        };
        var localRepair = await orchestrator.RepairAsync(local);
        Assert.False(localRepair.Ok);
        Assert.Contains("cannot be verified", localRepair.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AbsentOfficialClient_InstallAndLaunchFailHonestly()
    {
        var adapter = new EaAdapter();
        var game = new GameEntry
        {
            Id = "ea:missing-target",
            Title = "Missing target",
            Store = StoreKind.Ea,
        };

        var install = await adapter.InstallAsync(game, null, progress: null);
        var launch = await adapter.LaunchAsync(game, new LaunchOptions());
        var uninstall = await adapter.UninstallAsync(game);

        Assert.False(install.Ok);
        Assert.False(install.HandoffOnly);
        Assert.Contains("no install target", install.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(launch.Ok);
        Assert.Contains("no proven launch target", launch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(uninstall.Ok);
        Assert.Contains("no install to remove", uninstall.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalUpdate_IsHonestlyUnsupported()
    {
        var result = await new LocalAdapter().UpdateAsync(new GameEntry
        {
            Id = "local:portable",
            Title = "Portable",
            Store = StoreKind.Local,
            Installed = true,
        }, progress: null);
        Assert.False(result.Ok);
        Assert.Contains("No store updater", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmazonRepair_RequiresNileAndAProductIdNotAnExePath()
    {
        var adapter = new AmazonAdapter();
        Assert.False(adapter.CanRepair(new GameEntry
        {
            Id = "amazon:hades",
            Title = "Hades",
            Store = StoreKind.Amazon,
            Installed = true,
            LaunchTarget = @"D:\Amazon\Hades\Game.exe",
        }));
        Assert.False(adapter.CanRepair(new GameEntry
        {
            Id = "amazon:hades",
            Title = "Hades",
            Store = StoreKind.Amazon,
            Installed = false,
            LaunchTarget = "prime-hades",
        }));
    }

    [Fact]
    public void AntiCheatProcesses_AreNeverCleanupTargetsAndCannotBeTerminated()
    {
        var exitNames = StoreClientCleanup.TargetsFor(StoreKind.Local)
            .SelectMany(target => target.ExactProcessNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in StoreClientActivity.AntiCheatProcessNames)
        {
            Assert.DoesNotContain(name, exitNames);
            Assert.True(StoreClientActivity.IsAntiCheatProcess(name), name);
            Assert.Contains(name, ProcessHelper.NeverTerminateProcessNames, StringComparer.OrdinalIgnoreCase);
            Assert.True(RiotCli.IsProtectedProcess(name), name);
        }

        Assert.DoesNotContain("VALORANT-Win64-Shipping", exitNames);
        Assert.DoesNotContain("League of Legends", exitNames);
        Assert.DoesNotContain("FortniteClient-Win64-Shipping", exitNames);
        Assert.DoesNotContain("RobloxPlayerBeta", exitNames);
        Assert.DoesNotContain("legendary", exitNames);
        Assert.DoesNotContain("gogdl", exitNames);
        Assert.DoesNotContain("nile", exitNames);
    }

    [Fact]
    public async Task ExitUnused_LeavesAClientThatIsDownloadingOrHostingAGame()
    {
        var controller = FakeKeepController.ForAllTargets();
        var report = await StoreClientCleanup.ExitUnusedAsync(
            StoreKind.Epic,
            controller,
            TimeSpan.Zero,
            shouldKeep: store => store == StoreKind.Steam);

        Assert.DoesNotContain(StoreKind.Steam, controller.GracefulStores);
        Assert.Contains(StoreKind.Riot, controller.GracefulStores);
        Assert.True(report.GracefulStoreRequests > 0);
    }

    [Fact]
    public void ShouldKeep_HostingRiotOrSteamOverlayOrATransferCli()
    {
        Assert.True(StoreClientActivity.ShouldKeep(StoreClientActivity.Evaluate(
            StoreKind.Riot,
            name => string.Equals(name, "VALORANT-Win64-Shipping", StringComparison.OrdinalIgnoreCase))));
        Assert.True(StoreClientActivity.ShouldKeep(StoreClientActivity.Evaluate(
            StoreKind.Steam,
            name => string.Equals(name, "GameOverlayUI", StringComparison.OrdinalIgnoreCase))));
        Assert.True(StoreClientActivity.ShouldKeep(StoreClientActivity.Evaluate(
            StoreKind.Epic,
            name => string.Equals(name, "legendary", StringComparison.OrdinalIgnoreCase))));
        Assert.True(StoreClientActivity.ShouldKeep(StoreClientActivity.Evaluate(
            StoreKind.Gog,
            name => string.Equals(name, "gogdl", StringComparison.OrdinalIgnoreCase))));
        Assert.True(StoreClientActivity.ShouldKeep(StoreClientActivity.Evaluate(
            StoreKind.Amazon,
            name => string.Equals(name, "nile", StringComparison.OrdinalIgnoreCase))));
        Assert.True(StoreClientActivity.ShouldKeep(StoreClientActivity.Evaluate(
            StoreKind.Steam,
            _ => false,
            suspended: true)));
        Assert.False(StoreClientActivity.ShouldKeep(StoreClientActivity.Evaluate(
            StoreKind.Steam,
            _ => false)));
    }

    [Fact]
    public void OpeningOneLauncher_ClearsTheOtherSuspension()
    {
        HiddenStoreRuntime.Resume(StoreKind.Steam);
        HiddenStoreRuntime.Resume(StoreKind.Epic);
        HiddenStoreRuntime.SuspendFor(StoreKind.Steam, TimeSpan.FromMinutes(30));
        Assert.True(HiddenStoreRuntime.IsSuspended(StoreKind.Steam));

        HiddenStoreRuntime.SuspendFor(StoreKind.Epic, TimeSpan.FromMinutes(30));
        Assert.True(HiddenStoreRuntime.IsSuspended(StoreKind.Epic));
        Assert.False(HiddenStoreRuntime.IsSuspended(StoreKind.Steam));
        HiddenStoreRuntime.Resume(StoreKind.Epic);
    }

    [Fact]
    public void SteamDownloadingFolder_IsDetectedWithoutWalkingSizes()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-dl-" + Guid.NewGuid().ToString("N"));
        var downloading = Path.Combine(root, "steamapps", "downloading", "730");
        Directory.CreateDirectory(downloading);
        File.WriteAllText(Path.Combine(downloading, "chunk.bin"), "x");
        try
        {
            Assert.True(SteamContentLogProgress.AnyDownloadingFolder(root));
            Directory.Delete(downloading, recursive: true);
            Assert.False(SteamContentLogProgress.AnyDownloadingFolder(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    private static GameEntry CatalogHit(
        StoreKind store,
        string id,
        string launchTarget,
        string title = "Catalog title",
        bool owned = false,
        bool canInstall = false) =>
        new()
        {
            Id = id,
            Title = title,
            Store = store,
            Owned = owned,
            CanInstall = canInstall,
            LaunchTarget = string.IsNullOrEmpty(launchTarget) ? null : launchTarget,
        };

    private sealed class FakeKeepController : IStoreClientProcessController
    {
        private readonly HashSet<string> _running;

        private FakeKeepController(IEnumerable<string> running) =>
            _running = running.ToHashSet(StringComparer.OrdinalIgnoreCase);

        public List<StoreKind> GracefulStores { get; } = [];

        public static FakeKeepController ForAllTargets() =>
            new(StoreClientCleanup.TargetsFor(StoreKind.Local).SelectMany(target => target.ExactProcessNames));

        public bool IsRunning(string exactProcessName) => _running.Contains(exactProcessName);

        public void RequestGracefulExit(StoreCleanupTarget target)
        {
            GracefulStores.Add(target.Store);
            foreach (var name in target.ExactProcessNames)
                _running.Remove(name);
        }
    }
}
