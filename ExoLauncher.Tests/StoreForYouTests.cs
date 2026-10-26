using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Discover's owned filter, which nobody would notice failing: a game the library has from Epic or Battle.net (no
/// Steam id) still shows on Discover; Steam's "®" and "™" or a colon keep the same game from matching; two different
/// games whose names share a word are taken for one.
/// </summary>
public sealed class StoreForYouTests
{
    private static StoreService.OwnedGames Owned(string[] ids, string[] titles) =>
        new(ids.ToHashSet(StringComparer.Ordinal), titles.Select(StoreService.TitleKey).ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void A_game_owned_on_steam_is_known_by_its_app_id()
    {
        var owned = Owned(["730"], []);
        Assert.True(owned.Has(new StoreService.Tile("730", "Counter-Strike 2")));
        Assert.False(owned.Has(new StoreService.Tile("570", "Dota 2")));
    }

    [Fact]
    public void A_game_owned_on_another_store_is_known_by_its_title_despite_marks_and_case()
    {
        var owned = Owned([], ["Call of Duty: Modern Warfare 4", "EA SPORTS FC 27"]);
        Assert.True(owned.Has(new StoreService.Tile("4435490", "Call of Duty®: Modern Warfare® 4")));
        Assert.True(owned.Has(new StoreService.Tile("1", "EA SPORTS FC™ 27")));
    }

    [Fact]
    public void A_different_game_sharing_words_is_not_taken_for_an_owned_one()
    {
        var owned = Owned([], ["Call of Duty"]);
        Assert.False(owned.Has(new StoreService.Tile("4435490", "Call of Duty®: Modern Warfare® 4")));
    }
}
