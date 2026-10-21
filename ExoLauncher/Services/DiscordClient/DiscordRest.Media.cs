using System.Text.Json.Nodes;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>Stickers, polls and the soundboard, as Discord's own client calls them.</summary>
internal sealed partial class DiscordRest
{
    /// <summary>Discord's own sticker packs (Wumpus and friends), the list its sticker picker shows everyone.</summary>
    internal Task<Reply> StickerPacksAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "sticker-packs?locale=en-US", null, ct);

    /// <summary>Your vote on a poll: the answers chosen, or none to take the vote back.</summary>
    internal Task<Reply> PollVoteAsync(string channelId, string messageId, IReadOnlyList<int> answers, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"channels/{Id(channelId)}/polls/{Id(messageId)}/answers/@me",
            new JsonObject { ["answer_ids"] = new JsonArray([.. answers.Select(a => (JsonNode)JsonValue.Create(a.ToString(System.Globalization.CultureInfo.InvariantCulture)))]) }, ct);

    /// <summary>Posts a poll (Discord's Create Poll): a question, 2 to 10 answers, how many hours it runs, one answer or several.</summary>
    internal Task<Reply> PostPollAsync(string channelId, string question, IReadOnlyList<string> answers, int hours, bool multiple, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/messages", new JsonObject
        {
            ["content"] = "",
            ["nonce"] = Nonce(),
            ["poll"] = new JsonObject
            {
                ["question"] = new JsonObject { ["text"] = question },
                ["answers"] = new JsonArray([.. answers.Select(a => (JsonNode)new JsonObject { ["poll_media"] = new JsonObject { ["text"] = a } })]),
                ["duration"] = hours,
                ["allow_multiselect"] = multiple,
                ["layout_type"] = 1,
            },
        }, ct);

    /// <summary>Ends your own poll now.</summary>
    internal Task<Reply> PollEndAsync(string channelId, string messageId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/polls/{Id(messageId)}/expire", null, ct);

    /// <summary>Discord's default soundboard sounds.</summary>
    internal Task<Reply> SoundboardDefaultsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "soundboard-default-sounds", null, ct);

    /// <summary>A server's own soundboard sounds.</summary>
    internal Task<Reply> SoundboardAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/soundboard-sounds", null, ct);

    /// <summary>Plays a sound in the voice channel you're in; a server's sound names its server (a default one doesn't).</summary>
    internal Task<Reply> SendSoundAsync(string channelId, string soundId, string? sourceGuildId, string? emojiId, string? emojiName, CancellationToken ct)
    {
        var body = new JsonObject { ["sound_id"] = Id(soundId), ["emoji_id"] = emojiId is null ? null : Id(emojiId), ["emoji_name"] = emojiName };
        if (sourceGuildId is not null && sourceGuildId != "0") body["source_guild_id"] = Id(sourceGuildId);
        return SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/send-soundboard-sound", body, ct);
    }
}
