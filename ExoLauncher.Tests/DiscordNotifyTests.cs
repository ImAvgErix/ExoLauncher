using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Discord's notification rules in Exo's client (DiscordCache.Notifies, after Discord's web client's shouldNotify, and
/// DiscordNotifier.Route) and what takes notifications away. Ways it could fail: a DM or a mention that should notify
/// doesn't; your own message, a @silent one, a blocked person's, a message request's or one under Do Not Disturb does;
/// a muted server, category, channel or DM still notifies (a thread under a muted category too); All Messages on a
/// voice channel's chat notifies while you aren't in the call; @everyone or a role notifies where the server suppresses
/// it; the same message notifies twice (a resume replaying it) or after it was read elsewhere; reading or deleting
/// leaves the notification; the conversation on screen notifies while Exo is in front, or Exo's banner shows while it
/// is in the background.
/// </summary>
public sealed class DiscordNotifyTests
{
    private static DiscordCache Ready(bool suppressEveryone = false, bool suppressRoles = false)
    {
        var cache = new DiscordCache();
        using var doc = JsonDocument.Parse($$"""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{
            "id": "1", "name": "Uhh", "owner_id": "9",
            "roles": [ { "id": "1", "permissions": "3072" }, { "id": "50", "permissions": "0" } ],
            "members": [ { "user": { "id": "100" }, "roles": ["50"] } ],
            "channels": [
              { "id": "10", "type": 0, "name": "general", "position": 0 },
              { "id": "11", "type": 0, "name": "muted", "position": 1 },
              { "id": "12", "type": 2, "name": "Lounge", "position": 2 },
              { "id": "13", "type": 0, "name": "mentions-only", "position": 3 },
              { "id": "30", "type": 4, "name": "Muted category", "position": 4 },
              { "id": "31", "type": 0, "name": "in-muted-category", "parent_id": "30", "position": 5 }
            ],
            "threads": [ { "id": "32", "type": 11, "name": "a thread", "parent_id": "31" } ]
          }],
          "private_channels": [
            { "id": "40", "type": 1, "recipients": [ { "id": "7", "username": "ada" } ] },
            { "id": "41", "type": 1, "is_message_request": true, "recipients": [ { "id": "8", "username": "stranger" } ] },
            { "id": "42", "type": 1, "recipients": [ { "id": "6", "username": "loud" } ] },
            { "id": "43", "type": 3, "name": "Squad", "recipients": [ { "id": "7", "username": "ada" }, { "id": "6", "username": "loud" } ] }
          ],
          "relationships": [ { "id": "9", "type": 2, "user": { "id": "9", "username": "blocked" } } ],
          "read_state": { "entries": [ { "id": "10", "last_message_id": "400" } ] },
          "user_guild_settings": { "entries": [
            { "guild_id": "1", "message_notifications": 1, "suppress_everyone": {{(suppressEveryone ? "true" : "false")}},
              "suppress_roles": {{(suppressRoles ? "true" : "false")}}, "channel_overrides": [
                { "channel_id": "10", "message_notifications": 0 },
                { "channel_id": "11", "muted": true, "message_notifications": 0 },
                { "channel_id": "12", "message_notifications": 0 },
                { "channel_id": "30", "muted": true } ] },
            { "guild_id": null, "channel_overrides": [ { "channel_id": "42", "muted": true } ] }
          ] }
        }
        """);
        cache.LoadReady(doc.RootElement);
        return cache;
    }

    private static List<(string Name, object Data)> Apply(DiscordCache cache, string type, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return cache.Apply(type, doc.RootElement);
    }

    private static string Message(string id, string channel, string author = "7", string extra = "") =>
        $$"""{ "id": "{{id}}", "channel_id": "{{channel}}", "author": { "id": "{{author}}", "username": "u{{author}}" }, "content": "hi", "mentions": []{{extra}} }""";

    private static DiscordCache.Notification? Notice(DiscordCache cache, string json) =>
        Apply(cache, "MESSAGE_CREATE", json).Select(e => e.Data).OfType<DiscordCache.Notification>().SingleOrDefault();

    private static bool Notifies(DiscordCache cache, string json) => Notice(cache, json) is not null;

    private const string MentionMe = """, "mentions": [ { "id": "100", "username": "me" } ]""";

    [Fact]
    public void DirectMessagesNotifyButNeverYourOwn()
    {
        var cache = Ready();
        Assert.True(Notifies(cache, Message("600", "40")));
        Assert.False(Notifies(cache, Message("601", "40", author: "100")));
    }

    [Fact]
    public void SilentBlockedRequestsAndMutedDmsDoNotNotify()
    {
        var cache = Ready();
        Assert.False(Notifies(cache, Message("600", "40", extra: """, "flags": 4096""")));
        Assert.False(Notifies(cache, Message("601", "40", author: "9")));
        Assert.False(Notifies(cache, Message("602", "41", author: "8")));
        Assert.False(Notifies(cache, Message("603", "42", author: "6")));
    }

    [Fact]
    public void DoNotDisturbSilencesEverything()
    {
        var cache = Ready();
        Apply(cache, "USER_SETTINGS_UPDATE", """{ "status": "dnd" }""");
        Assert.False(Notifies(cache, Message("600", "40")));
        Assert.False(Notifies(cache, Message("601", "10", extra: MentionMe)));
        Apply(cache, "USER_SETTINGS_UPDATE", """{ "status": "online" }""");
        Assert.True(Notifies(cache, Message("602", "40")));
    }

    [Fact]
    public void ServerLevelsAndMutesFollowDiscord()
    {
        var cache = Ready();
        Assert.True(Notifies(cache, Message("600", "10")));                      // All Messages
        Assert.False(Notifies(cache, Message("601", "13")));                     // only mentions: a plain message
        Assert.True(Notifies(cache, Message("602", "13", extra: MentionMe)));    // ... and a mention
        Assert.False(Notifies(cache, Message("603", "11", extra: MentionMe)));   // a muted channel, even a mention
        Assert.False(Notifies(cache, Message("604", "31", extra: MentionMe)));   // a muted category's channel
        Assert.False(Notifies(cache, Message("605", "32", extra: MentionMe)));   // a thread under a muted category
    }

    [Fact]
    public void EveryoneAndRolesCountUnlessTheServerSuppressesThem()
    {
        const string everyone = """, "mention_everyone": true""", role = """, "mention_roles": ["50"]""";
        var cache = Ready();
        Assert.True(Notifies(cache, Message("600", "13", extra: everyone)));
        Assert.True(Notifies(cache, Message("601", "13", extra: role)));
        var quiet = Ready(suppressEveryone: true, suppressRoles: true);
        Assert.False(Notifies(quiet, Message("600", "13", extra: everyone)));
        Assert.False(Notifies(quiet, Message("601", "13", extra: role)));
        Assert.True(Notifies(quiet, Message("602", "13", extra: MentionMe)));
    }

    [Fact]
    public void AVoiceChannelsChatNotifiesOfEveryMessageOnlyInTheCall()
    {
        var cache = Ready();
        Assert.False(Notifies(cache, Message("600", "12")));
        Assert.True(Notifies(cache, Message("601", "12", extra: MentionMe)));
        Apply(cache, "VOICE_STATE_UPDATE", """{ "guild_id": "1", "channel_id": "12", "user_id": "100", "session_id": "v" }""");
        Assert.True(Notifies(cache, Message("602", "12")));
    }

    [Fact]
    public void TheSameMessageNotifiesOnceAndNotAfterItWasRead()
    {
        var cache = Ready();
        Assert.True(Notifies(cache, Message("600", "40")));
        Assert.False(Notifies(cache, Message("600", "40")));                    // a resume replaying it
        Assert.False(Notifies(cache, Message("300", "10")));                    // older than what was read
        Apply(cache, "MESSAGE_ACK", """{ "channel_id": "40", "message_id": "700" }""");
        Assert.False(Notifies(cache, Message("650", "40")));                    // read in another Discord app first
        Assert.True(Notifies(cache, Message("701", "40")));
    }

    [Fact]
    public void ReadingOrDeletingTakesNotificationsAway()
    {
        var cache = Ready();
        var acked = Apply(cache, "MESSAGE_ACK", """{ "channel_id": "40", "message_id": "700" }""").Select(e => e.Data).OfType<DiscordCache.NotifyClear>().Single();
        Assert.Equal(new DiscordCache.NotifyClear("40", null), acked);
        var deleted = Apply(cache, "MESSAGE_DELETE", """{ "id": "700", "channel_id": "40" }""").Select(e => e.Data).OfType<DiscordCache.NotifyClear>().Single();
        Assert.Equal(new DiscordCache.NotifyClear("40", "700"), deleted);
    }

    [Fact]
    public void ANoticeSaysWhoAndWhere()
    {
        var cache = Ready();
        var server = Notice(cache, Message("600", "10", extra: """, "member": { "nick": "Ada" }"""))!;
        Assert.Equal(("Ada", "#general", "Uhh", "600", "10", "1"), (server.Author, server.Place, server.Server, server.MessageId, server.ChannelId, server.GuildId));
        var dm = Notice(cache, Message("601", "40"))!;
        Assert.Equal(((string?)null, (string?)null, "hi"), (dm.Place, dm.Server, dm.Body));
        Assert.Equal("Squad", Notice(cache, Message("602", "43"))!.Place);
        Assert.Equal("#general · Uhh", DiscordNotifier.Where(server.Place, server.Server));
        Assert.Null(DiscordNotifier.Where(null, null));
    }

    [Theory]
    [InlineData(true, "10", false, false, true, "None")]     // the conversation on screen, Exo in front
    [InlineData(true, "11", false, false, true, "InApp")]    // another conversation: Exo's banner
    [InlineData(false, "10", false, false, true, "Windows")] // in the background, even the one left open
    [InlineData(false, null, true, false, true, "None")]     // Game focus while a game runs
    [InlineData(false, null, false, true, true, "Sound")]    // Streamer Mode's Disable notifications
    [InlineData(true, null, false, false, false, "Sound")]   // desktop notifications off
    public void RoutesAsDiscordShowsThem(bool inFront, string? viewing, bool gameQuiet, bool streamer, bool desktop, string expected)
    {
        Assert.Equal(expected, DiscordNotifier.Route(inFront, viewing, "10", gameQuiet, streamer, desktop).ToString());
    }
}
