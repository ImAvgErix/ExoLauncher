using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Calls in Exo's Discord client. Ways this goes wrong silently: a full voice channel is "joined" and the call waits
/// forever on a voice server Discord never sends; Move Members (which Discord lets past the limit) is refused; a DM
/// call shows nobody because the voice states of a call without a server are dropped, or keeps someone who left;
/// your own soundboard sound plays twice; a reconnect's new transport seals with no key and throws on the capture
/// thread, which has no handler; a phone's camera drawn sideways or upside down because its video-orientation
/// byte was missed among the other extensions, or another extension's byte read as a turn.
/// </summary>
public sealed class DiscordCallTests
{
    [Theory]
    // Padding, abs-send-time (id 2, 3 bytes), then video-orientation (id 13, 1 byte): 180 degrees.
    [InlineData(new byte[] { 0, 0x22, 1, 2, 3, 0xD0, 0x02, 0 }, 2)]
    // Only video-content-type (id 6, 1 byte) and a three-byte element (id 5): no orientation, never a turn.
    [InlineData(new byte[] { 0x60, 0x01, 0x52, 0, 0, 0 }, -1)]
    // Flip and a quarter turn; then a truncated element after it is never reached.
    [InlineData(new byte[] { 0xD0, 0x05, 0x23 }, 5)]
    // An element that runs past the block, or the reserved id 15: nothing read.
    [InlineData(new byte[] { 0x23, 1 }, -1)]
    [InlineData(new byte[] { 0xF0, 0xD0, 0x01 }, -1)]
    public void ReadsTheVideoOrientationByte(byte[] block, int orientation) =>
        Assert.Equal(orientation, VoiceTransport.Orientation(block));

    // View Channel and Connect; Move Members.
    private const string ViewConnect = "1049600", MoveMembers = "16777216";

    private static DiscordCache Ready(string myRoles = "") => Load($$"""
        {
          "session_id": "s", "user": { "id": "100", "username": "me" },
          "guilds": [{
            "id": "1", "name": "Guild", "owner_id": "9",
            "roles": [ { "id": "1", "permissions": "{{ViewConnect}}" }, { "id": "70", "permissions": "{{MoveMembers}}" } ],
            "members": [ { "user": { "id": "100" }, "roles": [{{myRoles}}] } ],
            "channels": [
              { "id": "20", "type": 2, "name": "Squad", "position": 0, "user_limit": 2 },
              { "id": "21", "type": 2, "name": "Lounge", "position": 1, "user_limit": 0 }
            ],
            "voice_states": [ { "user_id": "7", "channel_id": "20" }, { "user_id": "8", "channel_id": "20" } ]
          }],
          "private_channels": [ { "id": "30", "type": 1, "recipient_ids": ["7"] } ],
          "read_state": { "entries": [] }
        }
        """);

    private static DiscordCache Load(string json)
    {
        var cache = new DiscordCache();
        using var doc = JsonDocument.Parse(json);
        cache.LoadReady(doc.RootElement);
        return cache;
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string[] CallOf(DiscordCache cache, string dm) =>
        JsonSerializer.SerializeToElement(cache.Home()).GetProperty("dms").EnumerateArray()
            .Where(d => d.GetProperty("id").GetString() == dm)
            .SelectMany(d => d.GetProperty("call").ValueKind == JsonValueKind.Array ? d.GetProperty("call").EnumerateArray().Select(m => m.GetProperty("id").GetString()!) : [])
            .ToArray();

    [Fact]
    public void FullChannelIsRefusedWithWhy()
    {
        var cache = Ready();
        Assert.Equal("This voice channel is full.", cache.JoinRefusal("20"));
        Assert.Null(cache.JoinRefusal("21"));
    }

    [Fact]
    public void MoveMembersJoinsAFullChannel()
    {
        Assert.Null(Ready("\"70\"").JoinRefusal("20"));
    }

    [Fact]
    public void DmCallShowsWhoIsInItUntilTheyLeave()
    {
        var cache = Ready();
        cache.ApplyCall("CALL_CREATE", Json("""{ "channel_id": "30", "ringing": ["100"], "voice_states": [ { "user_id": "7", "channel_id": "30" } ] }"""));
        Assert.Equal(["7"], CallOf(cache, "30"));
        cache.Apply("VOICE_STATE_UPDATE", Json("""{ "user_id": "100", "channel_id": "30", "session_id": "x" }"""));
        Assert.Equal(["100", "7"], CallOf(cache, "30").Order(StringComparer.Ordinal).ToArray());
        cache.Apply("VOICE_STATE_UPDATE", Json("""{ "user_id": "7", "channel_id": null }"""));
        Assert.Equal(["100"], CallOf(cache, "30"));
        cache.ApplyCall("CALL_DELETE", Json("""{ "channel_id": "30" }"""));
        Assert.Empty(CallOf(cache, "30"));
    }

    [Fact]
    public void OwnSoundboardSoundIsNotPlayedTwice()
    {
        var cache = Ready();
        Assert.DoesNotContain(cache.Apply("VOICE_CHANNEL_EFFECT_SEND", Json("""{ "channel_id": "20", "sound_id": "5", "user_id": "100" }""")), e => e.Item1 == "discord.soundboard");
        Assert.Contains(cache.Apply("VOICE_CHANNEL_EFFECT_SEND", Json("""{ "channel_id": "20", "sound_id": "5", "user_id": "7" }""")), e => e.Item1 == "discord.soundboard");
    }

    [Fact]
    public void TransportSendsNothingBeforeItsKey()
    {
        using var transport = new VoiceTransport("127.0.0.1", 9, 1);
        Assert.False(transport.Started);
        transport.Send([1, 2, 3]);
        transport.SendVideo(2, 101, true, 1, 0, [1, 2, 3]);
        transport.SendRtcp(new byte[12]);
    }
}
