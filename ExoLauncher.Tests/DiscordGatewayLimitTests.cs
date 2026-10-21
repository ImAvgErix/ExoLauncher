using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways this fails: Exo asks Discord again and again for authors who aren't members (webhooks), the
/// reply to such a request makes the room ask once more, an unanswered request is never retried, and
/// the gateway sends past Discord's 120 a minute and is closed with 4008.
/// </summary>
public sealed class DiscordGatewayLimitTests
{
    private static DiscordCache Ready()
    {
        var cache = new DiscordCache();
        using var doc = JsonDocument.Parse("""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{ "id": "1", "name": "Guild", "owner_id": "100", "roles": [], "members": [], "channels": [] }],
          "private_channels": []
        }
        """);
        cache.LoadReady(doc.RootElement);
        return cache;
    }

    private static List<(string Name, object Data)> Chunk(DiscordCache cache, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return cache.Apply("GUILD_MEMBERS_CHUNK", doc.RootElement);
    }

    [Fact]
    public void NotFoundIdsAreNeverAskedAgain()
    {
        var cache = Ready();
        Assert.Equal(["7", "8"], cache.Colors("1", ["7", "8"], now: 0).Unknown);
        Assert.Empty(Chunk(cache, """{ "guild_id": "1", "members": [], "not_found": ["7"] }"""));
        Assert.Empty(cache.Colors("1", ["7", "8"], now: 1000).Unknown);
        // Long after: the unanswered id goes again, the one Discord said isn't a member never does.
        Assert.Equal(["8"], cache.Colors("1", ["7", "8"], now: DiscordCache.AskAgainMs + 1).Unknown);
    }

    [Fact]
    public void ArrivedMembersAnnounceNewColors()
    {
        var cache = Ready();
        cache.Colors("1", ["9"], now: 0);
        var events = Chunk(cache, """{ "guild_id": "1", "members": [ { "user": { "id": "9", "username": "x" }, "roles": [] } ] }""");
        Assert.Contains(events, e => e.Name == "discord.colors");
        var (colors, unknown) = cache.Colors("1", ["9"], now: DiscordCache.AskAgainMs * 2);
        Assert.True(colors.ContainsKey("9"));
        Assert.Empty(unknown);
    }

    [Fact]
    public void LimiterHoldsTheHundredAndFirstSendInAMinute()
    {
        var limit = new GatewaySendLimit();
        for (var i = 0; i < 100; i++) Assert.Equal(0, limit.Take(i));
        Assert.Equal(60_000 - 500, limit.Take(500));
        Assert.Equal(0, limit.Take(60_000));
        limit.Reset();
        for (var i = 0; i < 100; i++) Assert.Equal(0, limit.Take(60_001));
    }
}
