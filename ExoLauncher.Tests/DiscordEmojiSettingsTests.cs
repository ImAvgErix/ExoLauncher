using ExoLauncher.Services.DiscordClient;
using Xunit;
using static ExoLauncher.Services.DiscordClient.DiscordProto;

namespace ExoLauncher.Tests;

/// <summary>
/// The emoji picker's Favorites and Frequently Used, read from and written to Discord's FrecencyUserSettings.
/// Ways it could fail silently: favorites read from the wrong field (the picker shows none, or GIFs' keys);
/// a save dropping a field Exo doesn't know (Discord loses it on every device); a favorite added twice;
/// Frequently Used ranked by raw use count instead of Discord's frecency (old favorites crowd out today's);
/// the reaction list mixed with the message list; recent uses growing past Discord's 10; packed and
/// unpacked recent_uses not both read; favorite stickers read from the wrong field or saved unpacked or out of order.
/// </summary>
public sealed class DiscordEmojiSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static ulong DaysAgo(double days) => (ulong)Now.AddDays(-days).ToUnixTimeMilliseconds();

    private static byte[] Message(Action<List<byte>> write) { var output = new List<byte>(); write(output); return [.. output]; }

    private static byte[] Item(uint total, params ulong[] uses) => Message(m =>
    {
        WriteTag(m, 1, 0); WriteVarint(m, total);
        WriteBytes(m, 2, Message(p => { foreach (var use in uses) WriteVarint(p, use); }));
    });

    private static byte[] Map(params (string Key, byte[] Item)[] entries) => Message(m =>
    {
        foreach (var (key, item) in entries) WriteBytes(m, 1, Message(e => { WriteString(e, 1, key); WriteBytes(e, 2, item); }));
    });

    [Fact]
    public void FavoritesKeepTheirOrderAndWhatExoDoesNotKnow()
    {
        var field = Message(m => { WriteString(m, 1, "thumbsup"); WriteString(m, 1, "968812152267866172"); WriteTag(m, 9, 0); WriteVarint(m, 7); });
        var (emojis, unknown) = DiscordEmojiSettings.DecodeFavorites(field);
        Assert.Equal(["thumbsup", "968812152267866172"], emojis);
        Assert.Equal(field, DiscordEmojiSettings.EncodeFavorites(emojis, unknown));
    }

    [Fact]
    public void FrequentlyUsedIsDiscordsFrecencyNotTheUseCount()
    {
        // 50 uses three months ago: 50 x (1 x 10 / 10) = 50. Three uses today: 3 x (100 x 3 / 3) = 300.
        var old = Enumerable.Range(0, 10).Select(i => DaysAgo(90 + i)).ToArray();
        var map = DiscordEmojiSettings.DecodeFrecency(Map(("joy", Item(50, old)), ("fire", Item(3, DaysAgo(0.1), DaysAgo(0.2), DaysAgo(1))), ("gone", Item(4))));
        Assert.Equal(["fire", "joy"], DiscordEmojiSettings.Frequently(map, Now));
    }

    [Fact]
    public void StateReadsFavoritesAndBothFrecencyListsFromTheirOwnFields()
    {
        var proto = Message(m =>
        {
            WriteBytes(m, 2, Message(g => WriteString(g, 1, "gif")));
            WriteBytes(m, DiscordEmojiSettings.FavoritesField, Message(f => WriteString(f, 1, "heart")));
            WriteBytes(m, DiscordEmojiSettings.FrecencyField, Map(("skull", Item(1, DaysAgo(1)))));
            WriteBytes(m, DiscordEmojiSettings.ReactionFrecencyField, Map(("thumbsup", Item(1, DaysAgo(1)))));
        });
        var state = DiscordEmojiSettings.StateOf(proto, Now);
        Assert.Equal(["heart"], state.Favorites);
        Assert.Equal(["skull"], state.Frequent);
        Assert.Equal(["thumbsup"], state.FrequentReactions);
    }

    [Fact]
    public void AUseIsCountedAndOnlyTheLastTenAreKept()
    {
        var map = DiscordEmojiSettings.DecodeFrecency(Map(("joy", Item(10, [.. Enumerable.Range(1, 10).Select(i => DaysAgo(i))]))));
        DiscordEmojiSettings.Track(map, "joy", DaysAgo(0));
        DiscordEmojiSettings.Track(map, "eyes", DaysAgo(0));
        var back = DiscordEmojiSettings.DecodeFrecency(DiscordEmojiSettings.EncodeFrecency(map, Now));
        var joy = back.Entries.Single(e => e.Key == "joy").Item;
        Assert.Equal(11u, joy.TotalUses);
        Assert.Equal(10, joy.RecentUses.Count);
        Assert.Equal(DaysAgo(0), joy.RecentUses[^1]);
        Assert.DoesNotContain(DaysAgo(10), joy.RecentUses);
        Assert.Equal(1u, back.Entries.Single(e => e.Key == "eyes").Item.TotalUses);
    }

    [Fact]
    public void UnpackedRecentUsesAreReadToo()
    {
        var item = Message(m => { WriteTag(m, 1, 0); WriteVarint(m, 2); WriteTag(m, 2, 0); WriteVarint(m, DaysAgo(1)); WriteTag(m, 2, 0); WriteVarint(m, DaysAgo(2)); });
        var map = DiscordEmojiSettings.DecodeFrecency(Map(("eyes", item)));
        Assert.Equal([DaysAgo(1), DaysAgo(2)], map.Entries[0].Item.RecentUses);
    }

    [Fact]
    public void FavoriteStickersAreField3PackedFixed64AndKeepWhatExoDoesNotKnow()
    {
        ulong[] ids = [1234567890123456789, 749054660769218631];
        var field = DiscordEmojiSettings.EncodeStickers(ids, Message(m => { WriteTag(m, 9, 0); WriteVarint(m, 7); }));
        var proto = Message(m =>
        {
            WriteBytes(m, DiscordEmojiSettings.FavoritesField, Message(f => WriteString(f, 1, "heart")));
            WriteBytes(m, DiscordEmojiSettings.StickerFavoritesField, field);
        });
        Assert.Equal(["1234567890123456789", "749054660769218631"], DiscordEmojiSettings.StickersOf(proto));
        var (read, unknown) = DiscordEmojiSettings.DecodeStickers(field);
        Assert.Equal(field, DiscordEmojiSettings.EncodeStickers(read, unknown));
        // One tag and 16 bytes: packed, as Discord's client writes it.
        Assert.Equal(new byte[] { 0x0A, 16 }, field[..2]);
    }

    [Fact]
    public void UnpackedStickerIdsAreReadToo()
    {
        var field = Message(m => { WriteTag(m, 1, 1); m.AddRange(BitConverter.GetBytes(42UL)); });
        Assert.Equal([42UL], DiscordEmojiSettings.DecodeStickers(field).Ids);
    }
}
