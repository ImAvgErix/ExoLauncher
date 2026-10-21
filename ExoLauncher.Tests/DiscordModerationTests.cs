using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// What the person menu and Server Settings offer, which fails silently when wrong: a moderator offered
/// Kick or Ban on someone above them or on the owner, a role at or above their own (or an app's managed
/// role) offered to give, the owner limited like a member, Move to listing channels they can't move into,
/// a self-mute read as a server mute, or pins tied to the old permission on a migrated server; an app's role
/// offered to edit but refused on save (or editable above the user), Prune offered without Kick Members and
/// Manage Server, Remove Timeout offered to someone not timed out, or a stage event sent as a voice one.
/// </summary>
public sealed class DiscordModerationTests
{
    private const ulong View = 1UL << 10, Connect = 1UL << 20;
    private const ulong Mod = DiscordCache.Kick | DiscordCache.Ban | DiscordCache.ModerateMembers | DiscordCache.ManageRoles
        | DiscordCache.ManageNicknames | DiscordCache.MuteMembers | DiscordCache.MoveMembers | Connect | View;

    private static DiscordCache Server(string myRoles, string owner = "9", string features = "")
    {
        var cache = new DiscordCache();
        using var ready = JsonDocument.Parse($$"""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{
            "id": "1", "name": "Guild", "owner_id": "{{owner}}", "features": [{{features}}],
            "roles": [
              { "id": "1", "permissions": "{{View | Connect}}", "position": 0, "name": "@everyone" },
              { "id": "20", "permissions": "{{Mod}}", "position": 5, "name": "Mods" },
              { "id": "30", "permissions": "0", "position": 8, "name": "Admins" },
              { "id": "40", "permissions": "0", "position": 2, "name": "Members" },
              { "id": "41", "permissions": "0", "position": 1, "name": "Bot", "managed": true },
              { "id": "50", "permissions": "{{DiscordCache.PinMessages | View}}", "position": 3, "name": "Pinners" },
              { "id": "51", "permissions": "{{DiscordCache.ManageMessages | View}}", "position": 4, "name": "Cleaners" }
            ],
            "members": [ { "user": { "id": "100" }, "roles": [{{myRoles}}] } ],
            "channels": [
              { "id": "10", "type": 0, "name": "general", "position": 0 },
              { "id": "70", "type": 2, "name": "Lounge", "position": 1 },
              { "id": "71", "type": 2, "name": "Games", "position": 2 },
              { "id": "72", "type": 2, "name": "Locked", "position": 3,
                "permission_overwrites": [ { "id": "20", "type": 0, "allow": "0", "deny": "{{Connect}}" } ] },
              { "id": "73", "type": 13, "name": "Stage", "position": 4 }
            ],
            "voice_states": [ { "user_id": "200", "channel_id": "70", "mute": true, "self_mute": false, "deaf": false, "self_deaf": true } ]
          }],
          "private_channels": [], "read_state": { "entries": [] }
        }
        """);
        cache.LoadReady(ready.RootElement);
        using var chunk = JsonDocument.Parse("""
        { "guild_id": "1", "members": [
          { "user": { "id": "200", "username": "low" }, "roles": ["40"], "communication_disabled_until": "2999-01-01T00:00:00+00:00" },
          { "user": { "id": "300", "username": "high" }, "roles": ["30"] },
          { "user": { "id": "9", "username": "owner" }, "roles": [] }
        ] }
        """);
        cache.Apply("GUILD_MEMBERS_CHUNK", chunk.RootElement);
        return cache;
    }

    private static JsonElement Person(DiscordCache cache, string user) =>
        JsonSerializer.SerializeToElement(cache.Person("1", user, out _));

    private static bool Can(JsonElement person, string what) => person.GetProperty("can").GetProperty(what).GetBoolean();

    [Fact]
    public void ModeratorActsOnlyOnPeopleBelowThem()
    {
        var cache = Server("\"20\"");
        Assert.True(Can(Person(cache, "200"), "kick"));
        Assert.True(Can(Person(cache, "200"), "ban"));
        Assert.True(Can(Person(cache, "200"), "timeout"));
        Assert.False(Can(Person(cache, "300"), "kick"));
        Assert.False(Can(Person(cache, "300"), "ban"));
        Assert.False(cache.Outranks("1", "300"));
        Assert.True(cache.Outranks("1", "200"));
    }

    // Someone the gateway never sent as a member (no message, not in the list's range) was offered no moderator
    // action at all; their profile on the server carries the member, and the menu offers what their rank allows.
    [Fact]
    public void AMemberKnownFromTheirProfileGetsTheModeratorActions()
    {
        var cache = Server("\"20\"");
        Assert.False(Can(Person(cache, "400"), "kick"));
        using var profile = JsonDocument.Parse("""
        { "user": { "id": "400", "username": "new" },
          "guild_member": { "user": { "id": "400", "username": "new" }, "roles": ["40", "50"], "nick": null } }
        """);
        var extras = JsonSerializer.SerializeToElement(cache.ProfileOn("400", "1", profile.RootElement));
        Assert.True(Can(Person(cache, "400"), "kick"));
        Assert.True(Can(Person(cache, "400"), "ban"));
        Assert.Equal(["Pinners", "Members"], extras.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToArray());
    }

    // A server Discord sends after READY carries none of the user's roles: the moderator menu and role channels were
    // gone until their own member arrived, which only the member list or a chunk brings.
    [Fact]
    public void TheUsersRolesFromTheirMemberGiveBackTheModeratorActions()
    {
        var cache = Server("");
        Assert.False(Can(Person(cache, "200"), "kick"));
        using var chunk = JsonDocument.Parse("""
        { "guild_id": "1", "members": [ { "user": { "id": "100", "username": "me" }, "roles": ["20"] } ] }
        """);
        var events = cache.Apply("GUILD_MEMBERS_CHUNK", chunk.RootElement);
        Assert.True(Can(Person(cache, "200"), "kick"));
        Assert.Contains(events, e => e.Name == "discord.changed");
    }

    [Fact]
    public void TheOwnerIsNeverAModerationTarget()
    {
        var cache = Server("\"20\"");
        Assert.False(Can(Person(cache, "9"), "kick"));
        Assert.False(cache.Outranks("1", "9"));
    }

    [Fact]
    public void OnlyRolesBelowYoursAndNotAnAppsAreGiven()
    {
        var cache = Server("\"20\"");
        Assert.True(cache.CanAssign("1", "40"));
        Assert.False(cache.CanAssign("1", "20"));
        Assert.False(cache.CanAssign("1", "30"));
        Assert.False(cache.CanAssign("1", "41"));
        Assert.False(cache.CanAssign("1", "1"));
        var choices = Person(cache, "200").GetProperty("choices").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("assignable").GetBoolean());
        Assert.Equal(new Dictionary<string, bool> { ["30"] = false, ["20"] = false, ["51"] = true, ["50"] = true, ["40"] = true, ["41"] = false }, choices);
    }

    [Fact]
    public void TheOwnerManagesEveryoneAndEveryRoleButAnApps()
    {
        var cache = Server("", owner: "100");
        Assert.True(Can(Person(cache, "300"), "ban"));
        Assert.True(cache.CanAssign("1", "30"));
        Assert.False(cache.CanAssign("1", "41"));
        Assert.Equal(int.MaxValue, cache.TopOf("1"));
    }

    [Fact]
    public void WithoutThePermissionNothingIsOffered()
    {
        var cache = Server("\"40\"");
        var low = Person(cache, "200");
        Assert.False(Can(low, "kick"));
        Assert.False(Can(low, "roles"));
        Assert.False(Can(low, "mute"));
    }

    [Fact]
    public void VoiceReadsTheServerMuteAndMovesOnlyWhereAllowed()
    {
        var person = Person(Server("\"20\""), "200");
        var voice = person.GetProperty("voice");
        Assert.True(voice.GetProperty("mute").GetBoolean());
        Assert.False(voice.GetProperty("deaf").GetBoolean());
        Assert.True(Can(person, "move"));
        Assert.Equal(["Games", "Stage"], person.GetProperty("moveTo").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public void UnknownMembersAreAskedFor()
    {
        Server("\"20\"").Person("1", "555", out var unknown);
        Assert.True(unknown);
    }

    [Fact]
    public void AnAppsRoleEditsBelowYoursButIsNeverGiven()
    {
        var cache = Server("\"20\"");
        Assert.True(cache.CanEditRole("1", "41"));
        Assert.False(cache.CanAssign("1", "41"));
        Assert.True(cache.IsManagedRole("1", "41"));
        Assert.True(cache.CanEditRole("1", "40"));
        Assert.True(cache.CanEditRole("1", "1"));
        Assert.False(cache.CanEditRole("1", "20"));
        Assert.False(cache.CanEditRole("1", "30"));
        Assert.False(Server("\"40\"").CanEditRole("1", "41"));
    }

    [Fact]
    public void PruneNeedsKickAndManageServer()
    {
        Assert.False(Server("\"20\"").CanPrune("1"));
        Assert.True(Server("", owner: "100").CanPrune("1"));
        Assert.False(Server("\"40\"").CanPrune("1"));
    }

    [Fact]
    public void RemoveTimeoutOnlyForSomeoneTimedOut()
    {
        var cache = Server("\"20\"");
        Assert.True(Person(cache, "200").GetProperty("timedOut").GetBoolean());
        Assert.False(Person(cache, "300").GetProperty("timedOut").GetBoolean());
        cache.SetTimeout("1", "200", null);
        Assert.False(Person(cache, "200").GetProperty("timedOut").GetBoolean());
    }

    [Fact]
    public void AStageIsKnownByItsType()
    {
        var cache = Server("\"20\"");
        Assert.Equal(13, cache.ChannelType("73"));
        Assert.Equal(2, cache.ChannelType("70"));
        Assert.Null(cache.ChannelType("999"));
    }

    [Fact]
    public void PinsFollowDiscordsMigration()
    {
        static bool CanPin(DiscordCache cache) =>
            JsonSerializer.SerializeToElement(cache.Channels("1")).GetProperty("channels")[0].GetProperty("canPin").GetBoolean();
        const string migrated = "\"PIN_PERMISSION_MIGRATION_COMPLETE\"";
        Assert.True(CanPin(Server("\"50\"", features: migrated)));
        Assert.False(CanPin(Server("\"50\"")));
        Assert.True(CanPin(Server("\"51\"")));
        Assert.False(CanPin(Server("\"51\"", features: migrated)));
    }
}
