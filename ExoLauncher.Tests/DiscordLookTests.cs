using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Collectibles (avatar decoration, nameplate, display name style) as the cache hands them to the room. Ways it
/// could fail silently: a null field (nothing on) throws and READY is lost with every server; a later payload that
/// carries a trimmed user (a message author) wipes what was known; your own change (USER_UPDATE) never shows.
/// </summary>
public sealed class DiscordLookTests
{
    private static DiscordCache Ready()
    {
        var cache = new DiscordCache();
        using var doc = JsonDocument.Parse("""
        {
          "session_id": "s",
          "user": { "id": "100", "username": "me", "avatar_decoration_data": null, "collectibles": null, "display_name_styles": null },
          "guilds": [],
          "private_channels": [ { "id": "7", "type": 1, "recipient_ids": ["200"], "last_message_id": "7" } ],
          "users": [ {
            "id": "200", "username": "friend",
            "avatar_decoration_data": { "asset": "a_deco", "sku_id": "11" },
            "collectibles": { "nameplate": { "sku_id": "22", "asset": "nameplates/x/22/", "palette": "cobalt" } },
            "display_name_styles": { "font_id": 8, "effect_id": 2, "colors": [16711680, 255] }
          } ],
          "read_state": { "entries": [] }
        }
        """);
        cache.LoadReady(doc.RootElement);
        return cache;
    }

    private static JsonElement Home(DiscordCache cache) => JsonSerializer.SerializeToElement(cache.Home());

    private static void Apply(DiscordCache cache, string type, string json)
    {
        using var doc = JsonDocument.Parse(json);
        cache.Apply(type, doc.RootElement);
    }

    [Fact]
    public void NullCollectiblesReadAsNone()
    {
        var home = Home(Ready());
        Assert.Equal(JsonValueKind.Null, home.GetProperty("me").GetProperty("look").ValueKind);
    }

    [Fact]
    public void DmCarriesTheirLook()
    {
        var look = Home(Ready()).GetProperty("dms")[0].GetProperty("look");
        Assert.Equal("a_deco", look.GetProperty("decoration").GetProperty("asset").GetString());
        Assert.Equal("cobalt", look.GetProperty("nameplate").GetProperty("palette").GetString());
        Assert.Equal(2, look.GetProperty("nameStyle").GetProperty("effect").GetInt32());
    }

    [Fact]
    public void TrimmedUserKeepsKnownLook()
    {
        var cache = Ready();
        Apply(cache, "MESSAGE_CREATE", """{ "id": "8", "channel_id": "7", "author": { "id": "200", "username": "friend" }, "content": "hi" }""");
        Assert.Equal("22", Home(cache).GetProperty("dms")[0].GetProperty("look").GetProperty("nameplate").GetProperty("skuId").GetString());
    }

    [Fact]
    public void YourOwnChangeShows()
    {
        var cache = Ready();
        Apply(cache, "USER_UPDATE", """{ "id": "100", "username": "me", "avatar_decoration_data": { "asset": "a_mine", "sku_id": "33" }, "collectibles": null, "display_name_styles": null }""");
        Assert.Equal("33", Home(cache).GetProperty("me").GetProperty("look").GetProperty("decoration").GetProperty("skuId").GetString());
        Apply(cache, "USER_UPDATE", """{ "id": "100", "username": "me", "avatar_decoration_data": null, "collectibles": null, "display_name_styles": null }""");
        Assert.Equal(JsonValueKind.Null, Home(cache).GetProperty("me").GetProperty("look").ValueKind);
    }
}
