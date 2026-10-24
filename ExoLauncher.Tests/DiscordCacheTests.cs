using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class DiscordCacheTests
{
    private const string View = "1024", ViewSend = "3072";

    private static DiscordCache Ready(string myRoles, string everyonePerms = ViewSend, string owner = "9") =>
        Load($$"""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{
            "id": "1", "name": "Guild", "owner_id": "{{owner}}",
            "roles": [ { "id": "1", "permissions": "{{everyonePerms}}" }, { "id": "50", "permissions": "0" }, { "id": "60", "permissions": "8" } ],
            "members": [ { "user": { "id": "100" }, "roles": [{{myRoles}}] } ],
            "channels": [
              { "id": "10", "type": 0, "name": "general", "position": 0, "last_message_id": "500" },
              { "id": "11", "type": 0, "name": "staff", "position": 1,
                "permission_overwrites": [ { "id": "1", "type": 0, "allow": "0", "deny": "{{View}}" }, { "id": "50", "type": 0, "allow": "{{View}}", "deny": "0" } ] },
              { "id": "12", "type": 0, "name": "secret-member", "position": 2,
                "permission_overwrites": [ { "id": "1", "type": 0, "allow": "0", "deny": "{{View}}" }, { "id": "100", "type": 1, "allow": "{{View}}", "deny": "0" } ] },
              { "id": "13", "type": 0, "name": "blocked-for-me", "position": 3,
                "permission_overwrites": [ { "id": "100", "type": "member", "allow": "0", "deny": "{{View}}" } ] }
            ]
          }],
          "private_channels": [], "read_state": { "entries": [ { "id": "10", "last_message_id": "400" } ] }
        }
        """);

    private static DiscordCache Load(string json)
    {
        var cache = new DiscordCache();
        using var doc = JsonDocument.Parse(json);
        cache.LoadReady(doc.RootElement);
        return cache;
    }

    private static string[] Names(DiscordCache cache) =>
        JsonSerializer.SerializeToElement(cache.Channels("1")).GetProperty("channels").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()!).ToArray();

    [Fact]
    public void HiddenChannelsNeverListed()
    {
        Assert.Equal(["general", "secret-member"], Names(Ready("")));
    }

    [Fact]
    public void RoleOverwriteOpensItsChannel()
    {
        Assert.Equal(["general", "staff", "secret-member"], Names(Ready("\"50\"")));
    }

    [Fact]
    public void AdministratorAndOwnerSeeEverything()
    {
        Assert.Equal(4, Names(Ready("\"60\"")).Length);
        Assert.Equal(4, Names(Ready("", owner: "100")).Length);
    }

    [Fact]
    public void NoViewAtTheServerHidesAllButMemberOverwrites()
    {
        // Only a member overwrite that allows View opens a channel then.
        Assert.Equal(["secret-member"], Names(Ready("", everyonePerms: "0")));
    }

    [Fact]
    public void MessagesFromHiddenChannelsAreDropped()
    {
        var cache = Ready("");
        using var hidden = JsonDocument.Parse("""{ "id": "600", "channel_id": "11", "author": { "id": "7" }, "content": "x" }""");
        Assert.Empty(cache.Apply("MESSAGE_CREATE", hidden.RootElement));
        using var typing = JsonDocument.Parse("""{ "channel_id": "11", "user_id": "7" }""");
        Assert.Empty(cache.Apply("TYPING_START", typing.RootElement));
        Assert.Throws<UnauthorizedAccessException>(() => cache.GuildOf("11"));
        using var seen = JsonDocument.Parse("""{ "id": "600", "channel_id": "10", "author": { "id": "7" }, "content": "x" }""");
        Assert.Contains(cache.Apply("MESSAGE_CREATE", seen.RootElement), e => e.Name == "discord.message");
    }

    // A thread Discord sends with the server opens from a notification (listed with its parent), and one the
    // user never read in doesn't mark the server unread.
    [Fact]
    public void ThreadsAreKnownButOnlyReadOnesCountAsUnread()
    {
        var cache = Load("""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{
            "id": "1", "name": "Guild", "owner_id": "100", "roles": [ { "id": "1", "permissions": "3072" } ], "members": [],
            "channels": [ { "id": "10", "type": 0, "name": "general", "last_message_id": "400" } ],
            "threads": [ { "id": "20", "type": 11, "name": "a thread", "parent_id": "10", "last_message_id": "900" } ]
          }],
          "private_channels": [], "read_state": { "entries": [ { "id": "10", "last_message_id": "400" } ] }
        }
        """);
        var thread = JsonSerializer.SerializeToElement(cache.Channels("1")).GetProperty("threads")[0];
        Assert.Equal("10", thread.GetProperty("parentId").GetString());
        Assert.False(JsonSerializer.SerializeToElement(cache.Home()).GetProperty("guilds")[0].GetProperty("unread").GetBoolean());
        cache.MarkUnread("20", "800"); // read in up to 800 (Mark As Read now goes to the newest, 900)
        Assert.True(JsonSerializer.SerializeToElement(cache.Home()).GetProperty("guilds")[0].GetProperty("unread").GetBoolean());
    }

    // The member list keeps each row at Discord's index: a later range doesn't shift the first, and a range no
    // longer subscribed keeps what it showed.
    [Fact]
    public void MemberListRangesKeepTheirPlace()
    {
        var cache = Ready("", owner: "100");
        static string Member(string id) => $$"""{ "member": { "user": { "id": "{{id}}", "username": "u{{id}}" }, "roles": [] } }""";
        using var first = JsonDocument.Parse($$"""{ "guild_id": "1", "groups": [ { "id": "online", "count": 3 } ], "ops": [ { "op": "SYNC", "range": [0, 99], "items": [ { "group": { "id": "online" } }, {{Member("7")}} ] } ] }""");
        cache.Apply("GUILD_MEMBER_LIST_UPDATE", first.RootElement);
        using var later = JsonDocument.Parse($$"""{ "guild_id": "1", "ops": [ { "op": "SYNC", "range": [100, 199], "items": [ {{Member("8")}} ] }, { "op": "INVALIDATE", "range": [0, 99] } ] }""");
        cache.Apply("GUILD_MEMBER_LIST_UPDATE", later.RootElement);
        var items = JsonSerializer.SerializeToElement(cache.MemberList("1")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(101, items.Count);
        Assert.Equal("7", items[1].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, items[2].ValueKind); // not sent yet: a placeholder in its place
        Assert.Equal("8", items[100].GetProperty("id").GetString());
    }

    // A server update (a new icon, a boost) rebuilds the server; the subscribed member list and its counts stay,
    // since Discord won't send them again until the list changes.
    [Fact]
    public void MemberListSurvivesAServerUpdate()
    {
        var cache = Ready("", owner: "100");
        using var list = JsonDocument.Parse("""{ "guild_id": "1", "online_count": 1, "member_count": 5, "groups": [ { "id": "online", "count": 1 } ], "ops": [ { "op": "SYNC", "range": [0, 99], "items": [ { "group": { "id": "online" } }, { "member": { "user": { "id": "7", "username": "u7" }, "roles": [] } } ] } ] }""");
        cache.Apply("GUILD_MEMBER_LIST_UPDATE", list.RootElement);
        using var update = JsonDocument.Parse("""{ "id": "1", "name": "Renamed", "owner_id": "100", "roles": [ { "id": "1", "permissions": "3072" } ] }""");
        cache.Apply("GUILD_UPDATE", update.RootElement);
        var after = JsonSerializer.SerializeToElement(cache.MemberList("1"));
        Assert.Equal(2, after.GetProperty("items").GetArrayLength());
        Assert.Equal(5, after.GetProperty("total").GetInt32());
    }

    // Opening another channel must not empty the member list (the "members keep disappearing" bug): the shown list stays
    // until Discord sends the new one. Ways it fails: cleared on opening, cleared by a server update (the list id lost),
    // or a different list's rows mixed into the old one.
    [Fact]
    public void MemberListStaysUntilAnotherListArrives()
    {
        var cache = Ready("", owner: "100");
        static string Update(string listId, string op) => $$"""{ "guild_id": "1", "id": "{{listId}}", "groups": [ { "id": "online", "count": 1 } ], "ops": [ {{op}} ] }""";
        static string Sync(params string[] ids) => $$"""{ "op": "SYNC", "range": [0, 99], "items": [ { "group": { "id": "online" } }{{string.Concat(ids.Select(id => $$""", { "member": { "user": { "id": "{{id}}", "username": "u{{id}}" }, "roles": [] } }"""))}} ] }""";
        int Rows() => JsonSerializer.SerializeToElement(cache.MemberList("1")).GetProperty("items").GetArrayLength();
        void Apply(string type, string json) { using var doc = JsonDocument.Parse(json); cache.Apply(type, doc.RootElement); }

        Apply("GUILD_MEMBER_LIST_UPDATE", Update("everyone", Sync("7", "8", "9")));
        Assert.Equal(4, Rows());
        Apply("GUILD_UPDATE", """{ "id": "1", "name": "Renamed", "owner_id": "100" }""");
        Apply("GUILD_MEMBER_LIST_UPDATE", Update("everyone", """{ "op": "UPDATE", "index": 1, "item": { "member": { "user": { "id": "7", "username": "u7" }, "roles": [] } } }"""));
        Assert.Equal(4, Rows());
        Apply("GUILD_MEMBER_LIST_UPDATE", Update("staff", Sync("7")));
        Assert.Equal(2, Rows());
        // A late change to the list shown before doesn't replace the new one.
        Apply("GUILD_MEMBER_LIST_UPDATE", Update("everyone", """{ "op": "INSERT", "index": 1, "item": { "member": { "user": { "id": "9", "username": "u9" }, "roles": [] } } }"""));
        Assert.Equal(2, Rows());
    }

    // Opt-in mode keeps private channels listed, so a role-gated (mod) channel must read as private and a public one must not;
    // the flag comes from @everyone alone, never from the user's own roles.
    [Fact]
    public void PrivateIsWhatEveryoneCannotSee()
    {
        var channels = JsonSerializer.SerializeToElement(Ready("\"50\"").Channels("1")).GetProperty("channels").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("private").GetBoolean());
        Assert.False(channels["general"]);
        Assert.True(channels["staff"]);
        Assert.True(channels["secret-member"]);
        Assert.DoesNotContain("blocked-for-me", channels.Keys);
        // @everyone with Administrator sees everything, so nothing is private.
        var admin = JsonSerializer.SerializeToElement(Ready("", everyonePerms: "8").Channels("1")).GetProperty("channels").EnumerateArray();
        Assert.All(admin, c => Assert.False(c.GetProperty("private").GetBoolean()));
    }

    // A server that was unavailable in READY arrives later as GUILD_CREATE: the user's roles come from its members,
    // or every role-gated channel would vanish until the next restart.
    [Fact]
    public void LateServerReadsTheUsersRoles()
    {
        var cache = Load("""
        { "session_id": "s", "user": { "id": "100", "username": "me" }, "guilds": [ { "id": "1", "unavailable": true } ], "merged_members": [ [] ], "private_channels": [] }
        """);
        using var created = JsonDocument.Parse("""
        {
          "id": "1", "name": "Guild", "owner_id": "9",
          "roles": [ { "id": "1", "permissions": "3072" }, { "id": "50", "permissions": "0" } ],
          "members": [ { "user": { "id": "100" }, "roles": ["50"] } ],
          "channels": [ { "id": "11", "type": 0, "name": "staff",
            "permission_overwrites": [ { "id": "1", "type": 0, "allow": "0", "deny": "1024" }, { "id": "50", "type": 0, "allow": "1024", "deny": "0" } ] } ]
        }
        """);
        cache.Apply("GUILD_CREATE", created.RootElement);
        Assert.Equal(["staff"], Names(cache));
    }

    // A server-stats voice channel (View, no Connect) lists but its text chat can't be read or written, as Discord answers.
    [Fact]
    public void VoiceTextNeedsConnect()
    {
        var cache = Load("""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{ "id": "1", "name": "Guild", "owner_id": "9", "roles": [ { "id": "1", "permissions": "1117184" } ], "members": [],
            "channels": [
              { "id": "20", "type": 2, "name": "Members: 5", "permission_overwrites": [ { "id": "1", "type": 0, "allow": "0", "deny": "1048576" } ] },
              { "id": "21", "type": 2, "name": "General" } ] }],
          "private_channels": []
        }
        """);
        var channels = JsonSerializer.SerializeToElement(cache.Channels("1")).GetProperty("channels").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("canRead").GetBoolean());
        Assert.False(channels["Members: 5"]);
        Assert.True(channels["General"]);
        Assert.False(cache.CanSend("20"));
        Assert.True(cache.CanSend("21"));
    }

    // A timed mute (Mute for 1 hour) keeps muted: true after it ends; read as permanent, the channel would stay silent
    // and never show unread again until a restart.
    [Fact]
    public void TimedMuteEndsOnTime()
    {
        string Ready(string end) => $$"""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{ "id": "1", "name": "Guild", "owner_id": "100", "roles": [ { "id": "1", "permissions": "3072" } ], "members": [],
            "channels": [ { "id": "10", "type": 0, "name": "general", "last_message_id": "500" } ] }],
          "private_channels": [], "read_state": { "entries": [ { "id": "10", "last_message_id": "400" } ] },
          "user_guild_settings": { "entries": [ { "guild_id": "1", "channel_overrides": [ { "channel_id": "10", "muted": true, "mute_config": { "end_time": "{{end}}" } } ] } ] }
        }
        """;
        static JsonElement General(DiscordCache cache) => JsonSerializer.SerializeToElement(cache.Channels("1")).GetProperty("channels")[0];
        var ended = General(Load(Ready(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"))));
        Assert.False(ended.GetProperty("muted").GetBoolean());
        Assert.True(ended.GetProperty("unread").GetBoolean());
        var running = General(Load(Ready(DateTimeOffset.UtcNow.AddMinutes(30).ToString("O"))));
        Assert.True(running.GetProperty("muted").GetBoolean());
        Assert.False(running.GetProperty("unread").GetBoolean());
    }

    [Fact]
    public void UnreadFollowsReadState()
    {
        var cache = Ready("");
        var general = JsonSerializer.SerializeToElement(cache.Channels("1")).GetProperty("channels")[0];
        Assert.True(general.GetProperty("unread").GetBoolean());
        cache.MarkRead("10", "500");
        general = JsonSerializer.SerializeToElement(cache.Channels("1")).GetProperty("channels")[0];
        Assert.False(general.GetProperty("unread").GetBoolean());
    }

    // The server list (Discord's guild_folders). Ways it could fail silently: servers missing from it going to the bottom (Discord
    // puts them on top), a folder of one server shown loose, a server the user left or one in an outage kept or dropped wrongly
    // (the next save would then lose its place), or another Discord app's reorder not reaching the rail.
    [Fact]
    public void ServerListFollowsDiscordsFoldersAndOrder()
    {
        static string Guild(string id) => $$"""{ "id": "{{id}}", "name": "G{{id}}", "roles": [], "members": [], "channels": [] }""";
        var cache = Load($$"""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{{Guild("1")}}, {{Guild("2")}}, {{Guild("3")}}, { "id": "4", "unavailable": true }, {{Guild("5")}}],
          "private_channels": [],
          "user_settings": { "guild_folders": [
            { "id": null, "name": null, "color": null, "guild_ids": ["2"] },
            { "id": 77, "name": "F", "color": 255, "guild_ids": ["1", "4"] },
            { "id": 88, "name": null, "color": null, "guild_ids": ["5"] },
            { "id": null, "guild_ids": ["9"] }
          ] }
        }
        """);
        static string Order(DiscordCache cache) => string.Join(",", JsonSerializer.SerializeToElement(cache.Home()).GetProperty("guilds").EnumerateArray().Select(g => g.GetProperty("id").GetString()));
        static string Rail(DiscordCache cache) => string.Join(" ", JsonSerializer.SerializeToElement(cache.Home()).GetProperty("rail").EnumerateArray()
            .Select(f => $"{(f.GetProperty("id").ValueKind == JsonValueKind.Null ? "" : f.GetProperty("id").GetString() + ":")}{string.Join(",", f.GetProperty("guildIds").EnumerateArray().Select(g => g.GetString()))}"));
        Assert.Equal("3,2,1,5", Order(cache));
        Assert.Equal("3 2 77:1,4 88:5", Rail(cache));

        using var moved = JsonDocument.Parse("""{ "guild_folders": [ { "id": null, "guild_ids": ["5"] }, { "id": "77", "guild_ids": ["2", "1"] } ] }""");
        Assert.Contains(cache.Apply("USER_SETTINGS_UPDATE", moved.RootElement), e => e.Name == "discord.changed");
        Assert.Equal("3,5,2,1", Order(cache));

        using var joined = JsonDocument.Parse(Guild("6"));
        cache.Apply("GUILD_CREATE", joined.RootElement);
        Assert.Equal("6,3,5,2,1", Order(cache));

        var before = cache.ReplaceRail([new(null, null, null, ["1"]), new("90", null, null, ["2", "3"])]);
        Assert.Equal("6,5,1,2,3", Order(cache));
        cache.ReplaceRail(before);
        Assert.Equal("6,3,5,2,1", Order(cache));
    }

    // A server with a voice channel @everyone can join and talk in, a text channel it can talk in, one it can only read,
    // and a voice channel it can't join (everyone: View, Send, Connect).
    private static DiscordCache Rooms() => Load("""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{
            "id": "1", "name": "Guild", "owner_id": "9",
            "roles": [ { "id": "1", "permissions": "1051648" } ],
            "members": [ { "user": { "id": "100" }, "roles": [] } ],
            "channels": [
              { "id": "20", "type": 2, "name": "Lobby", "position": 0 },
              { "id": "21", "type": 0, "name": "chat", "position": 1 },
              { "id": "22", "type": 0, "name": "news", "position": 2, "permission_overwrites": [ { "id": "1", "type": 0, "allow": "0", "deny": "2048" } ] },
              { "id": "23", "type": 2, "name": "locked", "position": 3, "permission_overwrites": [ { "id": "1", "type": 0, "allow": "0", "deny": "1048576" } ] }
            ]
          }],
          "private_channels": [], "read_state": { "entries": [] }
        }
        """);

    private static string? StatusOf(DiscordCache cache, string channelId) =>
        JsonSerializer.SerializeToElement(cache.Channels("1")).GetProperty("channels").EnumerateArray()
            .First(c => c.GetProperty("id").GetString() == channelId).GetProperty("status").GetString();

    // A voice channel's status: all of a server's at once (CHANNEL_INFO, which clears the rest), then one changed or cleared.
    [Fact]
    public void VoiceChannelStatusesFollowDiscord()
    {
        var cache = Rooms();
        using var info = JsonDocument.Parse("""{ "guild_id": "1", "channels": [ { "id": "20", "status": "Ranked night" } ] }""");
        Assert.Contains(cache.Apply("CHANNEL_INFO", info.RootElement), e => e.Name == "discord.changed");
        Assert.Equal("Ranked night", StatusOf(cache, "20"));
        using var cleared = JsonDocument.Parse("""{ "id": "20", "guild_id": "1", "status": "" }""");
        cache.Apply("VOICE_CHANNEL_STATUS_UPDATE", cleared.RootElement);
        Assert.Null(StatusOf(cache, "20"));
    }

    // Onboarding's requirement counts what @everyone sees (a voice channel: and joins) and talks in, as Discord does.
    [Fact]
    public void OnboardingCountsWhatEveryoneSeesAndTalksIn()
    {
        var (seen, chats) = Rooms().EveryoneChannels("1");
        Assert.Equal(["20", "21", "22"], seen.Order());
        Assert.Equal(["20", "21"], chats.Order());
    }

    // Message requests: listed apart, no reply until accepted; accepted, a DM like any other; ignored, gone.
    [Fact]
    public void AMessageRequestWaitsForAcceptOrIgnore()
    {
        var cache = Load("""
            {
              "session_id": "s", "user": { "id": "100", "username": "me" }, "guilds": [],
              "private_channels": [
                { "id": "30", "type": 1, "recipient_ids": ["200"], "is_message_request": true },
                { "id": "31", "type": 1, "recipient_ids": ["201"], "is_spam": true },
                { "id": "32", "type": 1, "recipient_ids": ["202"] }
              ],
              "read_state": { "entries": [] }
            }
            """);
        Assert.True(cache.IsRequest("30"));
        Assert.True(cache.IsRequest("31"));
        Assert.False(cache.IsRequest("32"));
        Assert.False(cache.CanSend("30"));
        Assert.True(cache.CanSend("32"));
        var dms = JsonSerializer.SerializeToElement(cache.Home()).GetProperty("dms").EnumerateArray().ToDictionary(d => d.GetProperty("id").GetString()!);
        Assert.True(dms["30"].GetProperty("request").GetBoolean());
        Assert.True(dms["31"].GetProperty("spam").GetBoolean());
        cache.Answer("30", accepted: true);
        Assert.True(cache.CanSend("30"));
        Assert.False(cache.IsRequest("30"));
        cache.Answer("31", accepted: false);
        Assert.DoesNotContain(JsonSerializer.SerializeToElement(cache.Home()).GetProperty("dms").EnumerateArray(), d => d.GetProperty("id").GetString() == "31");
    }

    // # and @ in the message box: the channels the user sees (with their category) and every role but @everyone, best match first.
    [Fact]
    public void MentionSuggestionsFindChannelsAndRoles()
    {
        var cache = Load("""
            {
              "session_id": "s", "user": { "id": "100", "username": "me" },
              "guilds": [{
                "id": "1", "name": "Guild", "owner_id": "9",
                "roles": [
                  { "id": "1", "name": "@everyone", "permissions": "3072", "position": 0 },
                  { "id": "50", "name": "Moderators", "permissions": "0", "position": 2, "color": 255 },
                  { "id": "51", "name": "Game Mods", "permissions": "0", "position": 1 }
                ],
                "members": [ { "user": { "id": "100" }, "roles": [] } ],
                "channels": [
                  { "id": "20", "type": 4, "name": "Text", "position": 0 },
                  { "id": "21", "type": 0, "name": "general", "position": 0, "parent_id": "20" },
                  { "id": "22", "type": 2, "name": "General voice", "position": 1 },
                  { "id": "23", "type": 0, "name": "gen-hidden", "position": 2, "permission_overwrites": [ { "id": "1", "type": 0, "allow": "0", "deny": "1024" } ] }
                ]
              }],
              "private_channels": [], "read_state": { "entries": [] }
            }
            """);
        var channels = JsonSerializer.SerializeToElement(cache.FindChannels("1", "gen", 8)).EnumerateArray()
            .Select(c => (c.GetProperty("name").GetString(), c.GetProperty("kind").GetString(), c.GetProperty("category").GetString())).ToArray();
        Assert.Equal([("general", "text", "Text"), ("General voice", "voice", null)], channels);
        var roles = JsonSerializer.SerializeToElement(cache.FindRoles("1", "mod", 5)).EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToArray();
        Assert.Equal(["Moderators", "Game Mods"], roles);
        Assert.Empty(JsonSerializer.SerializeToElement(cache.FindRoles("1", "every", 5)).EnumerateArray());
    }
}
