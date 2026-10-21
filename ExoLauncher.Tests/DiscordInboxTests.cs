using System.Text;
using System.Text.Json;
using ExoLauncher.Services;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Search, the Inbox and reactions, which fail silently when wrong: a filter sent under the wrong name (so
/// Discord ignores it and every message matches), a has: value Discord doesn't take reaching it, an id from
/// the page that isn't a snowflake going into the URL, the sort or page sent wrong (results in the wrong
/// order, a page repeated); Unreads listing a channel the user can't see, one already read, a muted server,
/// or in the wrong order; a reaction from the gateway losing whether it was a super reaction; a text
/// preview read from somewhere other than Discord's file server, or ending in half a character.
/// </summary>
public sealed class DiscordInboxTests
{
    private static DiscordRest.SearchFilters Filters(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return WebHostBridge.SearchFiltersOf(doc.RootElement);
    }

    [Fact]
    public void SearchSendsEveryFilterUnderDiscordsName()
    {
        var query = DiscordRest.SearchQuery(Filters("""
        { "content": "hello world", "authorIds": ["11"], "mentions": ["12"], "has": ["link", "-image"], "channelIds": ["13"],
          "authorTypes": ["bot"], "pinned": true, "minId": "100", "maxId": "200", "sort": "oldest", "offset": 50 }
        """));
        Assert.Equal("author_id=11&mentions=12&has=link&has=-image&channel_id=13&author_type=bot&pinned=true&min_id=100&max_id=200"
            + "&content=hello%20world&sort_by=timestamp&sort_order=asc&offset=50&include_nsfw=true", query);
    }

    [Fact]
    public void SearchLeavesOutWhatDiscordDoesNotTake()
    {
        var query = DiscordRest.SearchQuery(Filters("""{ "has": ["link", "nonsense", "-"], "authorTypes": ["robot"], "sort": "relevance", "offset": 37 }"""));
        Assert.Equal("has=link&sort_by=relevance&sort_order=desc&offset=25&include_nsfw=true", query);
        Assert.Equal("sort_by=timestamp&sort_order=desc&include_nsfw=true", DiscordRest.SearchQuery(Filters("{}")));
    }

    [Fact]
    public void SearchRefusesAnIdThatIsNotOne()
    {
        Assert.Throws<ArgumentException>(() => Filters("""{ "authorIds": ["1&channel_id=2"] }"""));
        Assert.Throws<ArgumentException>(() => Filters("""{ "minId": "../x" }"""));
    }

    [Fact]
    public void SearchNeverPagesPastDiscordsLimit() =>
        Assert.Contains("offset=9975", DiscordRest.SearchQuery(Filters("""{ "offset": 99999 }""")));

    private static DiscordCache Servers()
    {
        var cache = new DiscordCache();
        using var ready = JsonDocument.Parse("""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [
            { "id": "1", "name": "One", "owner_id": "9",
              "roles": [ { "id": "1", "permissions": "1024", "position": 0, "name": "@everyone" } ],
              "members": [ { "user": { "id": "100" }, "roles": [] } ],
              "channels": [
                { "id": "10", "type": 0, "name": "general", "position": 0, "last_message_id": "500" },
                { "id": "11", "type": 0, "name": "read", "position": 1, "last_message_id": "400" },
                { "id": "12", "type": 0, "name": "hidden", "position": 2, "last_message_id": "900",
                  "permission_overwrites": [ { "id": "1", "type": 0, "allow": "0", "deny": "1024" } ] },
                { "id": "13", "type": 0, "name": "newest", "position": 3, "last_message_id": "800" }
              ] },
            { "id": "2", "name": "Muted", "owner_id": "9",
              "roles": [ { "id": "2", "permissions": "1024", "position": 0, "name": "@everyone" } ],
              "members": [ { "user": { "id": "100" }, "roles": [] } ],
              "channels": [ { "id": "20", "type": 0, "name": "loud", "position": 0, "last_message_id": "999" } ] }
          ],
          "private_channels": [],
          "user_guild_settings": { "entries": [ { "guild_id": "2", "muted": true } ] },
          "read_state": { "entries": [ { "id": "10", "last_message_id": "450", "mention_count": 2 }, { "id": "11", "last_message_id": "400" } ] }
        }
        """);
        cache.LoadReady(ready.RootElement);
        return cache;
    }

    [Fact]
    public void UnreadsAreTheUnreadChannelsYouCanSeeNewestFirst()
    {
        var unreads = JsonSerializer.SerializeToElement(Servers().Unreads(50)).EnumerateArray().ToList();
        Assert.Equal(["13", "10"], unreads.Select(u => u.GetProperty("channelId").GetString()!).ToArray());
        var general = unreads[1];
        Assert.Equal("450", general.GetProperty("lastRead").GetString());
        Assert.Equal(2, general.GetProperty("mentions").GetInt32());
        Assert.Equal("One", general.GetProperty("guild").GetString());
    }

    [Fact]
    public void MarkingAServerReadEmptiesItsUnreads()
    {
        var cache = Servers();
        cache.UnreadIn("1");
        Assert.Empty(cache.Unreads(50));
    }

    [Fact]
    public void ASuperReactionKeepsItsBurst()
    {
        var cache = Servers();
        using var add = JsonDocument.Parse("""
        { "channel_id": "10", "message_id": "500", "user_id": "300", "burst": true, "burst_colors": ["#ff0000", "#00ff00"],
          "emoji": { "id": null, "name": "🔥" } }
        """);
        var reaction = JsonSerializer.SerializeToElement(Assert.Single(cache.Apply("MESSAGE_REACTION_ADD", add.RootElement), e => e.Name == "discord.reaction").Data);
        Assert.True(reaction.GetProperty("burst").GetBoolean());
        Assert.Equal(["#ff0000", "#00ff00"], reaction.GetProperty("burstColors").EnumerateArray().Select(c => c.GetString()!).ToArray());
        Assert.Equal("300", reaction.GetProperty("userId").GetString());
        Assert.False(reaction.GetProperty("me").GetBoolean());
    }

    // Each emoji as Discord's own list writes it, and the path its web client puts it in (seen on a real account:
    // the same five with a VS16 Discord's list lacks come back 10014 Unknown Emoji).
    [Theory]
    [InlineData("©", "%C2%A9")]
    [InlineData("™", "%E2%84%A2")]
    [InlineData("❤️", "%E2%9D%A4%EF%B8%8F")]
    [InlineData("1️⃣", "1%EF%B8%8F%E2%83%A3")]
    [InlineData("\U0001F44D\U0001F3FD", "%F0%9F%91%8D%F0%9F%8F%BD")]
    [InlineData("\U0001F441‍\U0001F5E8", "%F0%9F%91%81%E2%80%8D%F0%9F%97%A8")]
    [InlineData("\U0001F468‍\U0001F469‍\U0001F467", "%F0%9F%91%A8%E2%80%8D%F0%9F%91%A9%E2%80%8D%F0%9F%91%A7")]
    [InlineData("\U0001F1FA\U0001F1F8", "%F0%9F%87%BA%F0%9F%87%B8")]
    [InlineData("Troll:968812152267866172", "Troll%3A968812152267866172")]
    public void AReactionGoesInThePathAsItsCharacters(string emoji, string encoded) =>
        Assert.Equal($"channels/1/messages/2/reactions/{encoded}", DiscordRest.ReactionPath("1", "2", emoji));

    [Fact]
    public void ThePickerWritesEmojiAsDiscordsListDoes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ui", "src", "components", "discord", "emoji.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "ui", "src", "components", "discord", "emoji.json")));
        var emoji = doc.RootElement.EnumerateArray().SelectMany(c => c.GetProperty("emoji").EnumerateArray())
            .Select(line => line.GetString()!.Split('|')).ToDictionary(parts => parts[1].Split(' ')[0], parts => parts[0]);
        Assert.Equal("©", emoji["copyright"]);
        Assert.Equal("®", emoji["registered"]);
        Assert.Equal("™", emoji["tm"]);
        Assert.Equal("⚧", emoji["transgender_symbol"]);
        Assert.Equal("\U0001F441‍\U0001F5E8", emoji["eye_in_speech_bubble"]);
        Assert.Equal("❤️", emoji["heart"]);
        Assert.Equal("☺️", emoji["relaxed"]);
        Assert.Equal("1️⃣", emoji["one"]);
        // Favorites and frecency key a Unicode emoji by Discord's first name.
        Assert.Equal("\U0001FA85", emoji["piñata"]);
    }

    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/notes.txt", true)]
    [InlineData("https://media.discordapp.net/attachments/1/2/notes.txt", true)]
    [InlineData("http://cdn.discordapp.com/attachments/1/2/notes.txt", false)]
    [InlineData("https://cdn.discordapp.com/avatars/1/2.png", false)]
    [InlineData("https://cdn.discordapp.com.evil.test/attachments/1/2/a.txt", false)]
    [InlineData("https://cdn.discordapp.com:8443/attachments/1/2/a.txt", false)]
    [InlineData("https://discord.com/api/v9/users/@me", false)]
    public void TextPreviewsComeOnlyFromDiscordsFileServer(string url, bool allowed) =>
        Assert.Equal(allowed, DiscordRest.IsAttachmentUrl(new Uri(url)));

    [Fact]
    public void ACutPreviewDropsItsLastCharacter()
    {
        var bytes = Encoding.UTF8.GetBytes("﻿line one\nline twé");
        Assert.Equal("line one\nline twé", DiscordRest.DecodePreview(bytes, cut: false));
        // Cut through é: the broken last character goes, not a replacement mark.
        Assert.Equal("line one\nline tw", DiscordRest.DecodePreview(bytes.AsSpan(0, bytes.Length - 1), cut: true));
    }
}
