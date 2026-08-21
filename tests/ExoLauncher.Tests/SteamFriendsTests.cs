using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamFriendsTests
{
    [Fact]
    public void ParseFriends_ReadsNamesAndSkipsSelf()
    {
        const string vdf = """
            "UserLocalConfigStore"
            {
            "friends"
            {
            "PersonaStateDesired""1"
            "1162499906"
            {
            "NameHistory"
            {
            "0""Erix"
            }
            "avatar""8df3fbb9717a9433d4c709138700c25228676cb9"
            "name""Erix"
            }
            "PersonaName""Erix"
            "883479345"
            {
            "name""Ketchup"
            "NameHistory"
            {
            "0""Ketchup"
            }
            "avatar""aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            }
            }
            }
            """;

        var friends = SteamFriends.ParseFriends(vdf, "1162499906");

        var self = SteamFriends.ParseSelf(vdf, "1162499906");
        Assert.Equal("Erix", self?.Name);

        var friend = Assert.Single(friends);
        Assert.Equal("Ketchup", friend.Name);
        Assert.Equal("https://avatars.steamstatic.com/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_full.jpg", friend.AvatarUrl);
        Assert.False(string.IsNullOrWhiteSpace(friend.AccountKey));
        Assert.Equal(SteamFriends.ToSteamId64("883479345"), friend.SteamId64);
    }

    [Fact]
    public void Friend_IsANameAndAvatar_NotPresence()
    {
        var fields = typeof(SteamFriends.Friend).GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // SteamId64 is the Web API join key. It is not a presence field.
        Assert.Equal(["AccountKey", "AvatarUrl", "Name", "SteamId64"], fields);
    }

    [Fact]
    public void ParseFriends_ExcludesClanRowsAndKeepsAFullIndividualSteamId()
    {
        const string individualSteamId = "76561197960265729";
        const string clanSteamId = "103582791429521409";
        const string vdf = $$"""
            "UserLocalConfigStore"
            {
            "friends"
            {
            "{{individualSteamId}}"
            {
            "name""A real person"
            }
            "{{clanSteamId}}"
            {
            "name""A game community"
            }
            }
            }
            """;

        var friend = Assert.Single(SteamFriends.ParseFriends(vdf, null));

        Assert.Equal("A real person", friend.Name);
        Assert.Equal(individualSteamId, friend.SteamId64);
        Assert.Null(SteamFriends.ToSteamId64(clanSteamId));
    }
}
