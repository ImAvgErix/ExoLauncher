using ExoLauncher.Models;
using ExoLauncher.Ui;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// URL construction for the shared "Buy cheapest key" action.
/// Keep aligned with <c>ggDealsUrl</c> in <c>ui/src/lib/stores.ts</c>.
/// </summary>
public sealed class GgDealsUrlTests
{
    [Fact]
    public void SteamAppId_OpensDocumentedGameRedirect()
    {
        Assert.Equal(
            "https://gg.deals/steam/app/1091500/",
            GgDealsUrl(Steam("1091500", "Cyberpunk 2077")));
        Assert.Equal(
            "https://gg.deals/steam/app/359550/",
            GgDealsUrl(Steam("359550", "Tom Clancy's Rainbow Six Siege")));
        Assert.Equal(
            "https://gg.deals/steam/app/814380/",
            GgDealsUrl(Steam("814380", "Sekiro™: Shadows Die Twice")));
        Assert.Equal(
            "https://gg.deals/steam/app/322330/",
            GgDealsUrl(Steam("322330", "I Am Bread")));
    }

    [Fact]
    public void TitleSearch_UrlEncodesPunctuationAndTrademark()
    {
        Assert.Equal(
            "https://gg.deals/games/?title=Cyberpunk%202077",
            GgDealsUrl(Epic("cyberpunk-2077", "Cyberpunk 2077")));
        Assert.Equal(
            "https://gg.deals/games/?title=Tom%20Clancy's%20Rainbow%20Six%20Siege",
            GgDealsUrl(Epic("rainbow-six-siege", "Tom Clancy's Rainbow Six Siege")));
        Assert.Equal(
            "https://gg.deals/games/?title=Sekiro%E2%84%A2%3A%20Shadows%20Die%20Twice",
            GgDealsUrl(Epic("sekiro", "Sekiro™: Shadows Die Twice")));
        Assert.Equal(
            "https://gg.deals/games/?title=I%20Am%20Bread",
            GgDealsUrl(Gog("i_am_bread", "I Am Bread")));
    }

    [Fact]
    public void SteamAppId_WinsOverTitle_EvenWhenTitleIsEmpty()
    {
        Assert.Equal(
            "https://gg.deals/steam/app/1091500/",
            GgDealsUrl(Steam("1091500", "")));
    }

    [Fact]
    public void NoButton_WhenOwnedOrInstalled()
    {
        Assert.False(ShowsCheapestKey(Steam("1091500", "Cyberpunk 2077", owned: true)));
        Assert.False(ShowsCheapestKey(Steam("1091500", "Cyberpunk 2077", installed: true)));
    }

    [Fact]
    public void StaleInstallCapabilityWithoutOwnership_StillOffersBuy()
    {
        Assert.True(ShowsCheapestKey(Steam("1091500", "Cyberpunk 2077", canInstall: true)));
    }

    [Fact]
    public void NoButton_WhenTitleCannotResolveAndThereIsNoSteamAppId()
    {
        Assert.Null(GgDealsUrl(Epic("empty", "")));
        Assert.Null(GgDealsUrl(Epic("spaces", "   ")));
        Assert.Null(GgDealsUrl(Epic("mark", "™")));
        Assert.Null(GgDealsUrl(Epic("punct", "!!!")));
        Assert.False(ShowsCheapestKey(Epic("empty", "")));
        Assert.False(ShowsCheapestKey(Epic("mark", "™")));
    }

    [Fact]
    public void NoButton_WhenStoreHasNoBuyAction()
    {
        Assert.False(ShowsCheapestKey(new GameEntry
        {
            Id = "local:foo",
            Title = "Cyberpunk 2077",
            Store = StoreKind.Local,
            Owned = false,
            Installed = false,
            CanInstall = false,
        }));
    }

    [Fact]
    public void Overlay_OpensGgDealsInTheSystemBrowser_WithoutAffiliateParams()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "GamePage.tsx"));
        var helper = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "stores.ts"));
        var surface = page + helper;
        Assert.Contains("Buy cheapest key", page, StringComparison.Ordinal);
        Assert.Contains("host.openUrl(dealsUrl)", page, StringComparison.Ordinal);
        Assert.Contains("import { ggDealsUrl } from '../lib/stores'", page, StringComparison.Ordinal);
        Assert.Contains("https://gg.deals/steam/app/${appId}/", helper, StringComparison.Ordinal);
        Assert.Contains("https://gg.deals/games/?title=${encodeURIComponent(title)}", helper, StringComparison.Ordinal);
        Assert.Contains("aria-label={`Buy cheapest key for ${selected.title} on gg.deals`}", page, StringComparison.Ordinal);
        Assert.DoesNotContain("window.open", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("affiliate=", surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("utm_", surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?ref=", surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("&ref=", surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" title=", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendDetail_OffersUnmatchedKeyShopOnlyForAnAuthoritativeSteamBuy()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "components", "FriendsRoom.tsx"));
        var social = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src", "lib", "social.ts"));

        Assert.Contains("hostedBuyUrl(game)", page, StringComparison.Ordinal);
        Assert.Contains("ggDealsUrl(game)", page, StringComparison.Ordinal);
        Assert.Contains("playing.kind === 'buy' && playing.steamAppId", page, StringComparison.Ordinal);
        Assert.Contains("id: `steam:${playing.steamAppId}`", page, StringComparison.Ordinal);
        Assert.Contains("import { ggDealsUrl } from '../lib/stores'", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ggDealsTitleUrl(playing.title)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ggDealsTitleUrl", page, StringComparison.Ordinal);
        Assert.Contains("steamAppId: id", social, StringComparison.Ordinal);
        Assert.Contains("steamAppId: null", social, StringComparison.Ordinal);
        Assert.Contains("host.openUrl(dealsUrl)", page, StringComparison.Ordinal);
        Assert.Contains("Buy cheapest key", page, StringComparison.Ordinal);
        Assert.Contains("<Download size={16}", page, StringComparison.Ordinal);
        Assert.Contains("<HeroWash game={artGame} />", page, StringComparison.Ordinal);
        Assert.Contains(
            "aria-label={`Buy cheapest key for ${playing.title} on gg.deals`}",
            page,
            StringComparison.Ordinal);
        Assert.Contains("<CoverArt game={artGame} className=\"h-full w-full\" />", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<HeroWash game={game} />", page, StringComparison.Ordinal);
        Assert.DoesNotContain("window.open", page, StringComparison.Ordinal);
    }

    private static bool ShowsCheapestKey(GameEntry game) =>
        UiFormat.BuyUrl(game) is not null && GgDealsUrl(game) is not null;

    private static string? GgDealsUrl(GameEntry game)
    {
        var appId = SteamAppId(game);
        if (appId is not null) return "https://gg.deals/steam/app/" + appId + "/";
        var title = (game.Title ?? "").Trim();
        if (!TitleSearchable(title)) return null;
        return "https://gg.deals/games/?title=" + EncodeUriComponent(title);
    }

    private static string? SteamAppId(GameEntry game)
    {
        var target = (game.LaunchTarget ?? "").Trim();
        if (game.Store == StoreKind.Steam && target.Length > 0 && target.All(char.IsDigit))
            return target;
        if (game.Id.StartsWith("steam:", StringComparison.Ordinal))
        {
            var id = game.Id["steam:".Length..];
            if (id.Length > 0 && id.All(char.IsDigit)) return id;
        }
        return null;
    }

    private static bool TitleSearchable(string title) =>
        title.Any(c => char.IsLetter(c) || char.IsNumber(c));

    /// <summary>Same reserved set as JavaScript <c>encodeURIComponent</c>.</summary>
    private static string EncodeUriComponent(string value) =>
        Uri.EscapeDataString(value)
            .Replace("%21", "!", StringComparison.Ordinal)
            .Replace("%27", "'", StringComparison.Ordinal)
            .Replace("%28", "(", StringComparison.Ordinal)
            .Replace("%29", ")", StringComparison.Ordinal)
            .Replace("%2A", "*", StringComparison.Ordinal);

    private static GameEntry Steam(
        string app,
        string title,
        bool installed = false,
        bool owned = false,
        bool canInstall = false) =>
        new()
        {
            Id = "steam:" + app,
            Title = title,
            Store = StoreKind.Steam,
            Installed = installed,
            Owned = owned,
            CanInstall = canInstall,
            LaunchTarget = app,
        };

    private static GameEntry Epic(string slug, string title) =>
        new()
        {
            Id = "epic:catalog:" + slug,
            Title = title,
            Store = StoreKind.Epic,
            Owned = false,
            Installed = false,
            CanInstall = false,
            LaunchTarget = slug,
        };

    private static GameEntry Gog(string slug, string title) =>
        new()
        {
            Id = "gog:" + slug,
            Title = title,
            Store = StoreKind.Gog,
            Owned = false,
            Installed = false,
            CanInstall = false,
            LaunchTarget = slug,
        };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
