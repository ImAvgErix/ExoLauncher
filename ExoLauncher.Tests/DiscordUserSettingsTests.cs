using System.Text.Json;
using ExoLauncher.Services;
using ExoLauncher.Services.DiscordClient;
using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Discord's account settings protobuf (PreloadedUserSettings) as Exo reads and saves it. Ways it
/// could fail silently: a key mapped to the wrong field number (the toggle changes some other
/// setting on the account); a save that drops fields Exo doesn't know (wiping the user's other
/// settings), or sends sections it didn't change; false written by leaving a wrapper out (proto3
/// would then keep the old true); an out-of-range value reaching Discord; repeated sections not
/// merged; server ids misread; a person's User Volume or Mute written to the wrong person, dropping the
/// others' or their soundboard mute, or kept once back to 100 and unmuted; and, around it, the email or phone number reaching the page in full,
/// and the voice gate or gain control misbehaving at their edges.
/// </summary>
public sealed class DiscordUserSettingsTests
{
    // An independent writer, so the tests don't trust the code under test to build their input.
    private static byte[] Tag(int number, int wire, params byte[] value)
    {
        var tag = (byte)((number << 3) | wire);
        var head = number < 16 ? new[] { tag } : Varint((ulong)((number << 3) | wire));
        return wire == 2 ? [.. head, .. Varint((ulong)value.Length), .. value] : [.. head, .. value];
    }

    private static byte[] Varint(ulong v)
    {
        var bytes = new List<byte>();
        while (v >= 0x80) { bytes.Add((byte)(v | 0x80)); v >>= 7; }
        bytes.Add((byte)v);
        return [.. bytes];
    }

    private static byte[] Wrapped(int number, ulong value) => Tag(number, 2, value == 0 ? [] : Tag(1, 0, Varint(value)));

    private static Dictionary<string, JsonElement> Changes(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static readonly byte[] Sample =
    [
        // text_and_images (6): message_display_compact = true, convert_emoticons = false, an unknown field 99,
        // explicit_content_settings.explicit_content_friend_dm = BLOCK (3).
        .. Tag(6, 2, [.. Wrapped(17, 1), .. Tag(21, 2), .. Tag(99, 0, 7), .. Tag(29, 2, Tag(2, 0, 3))]),
        // privacy (8): friend_source_flags = 6, default_guilds_restricted = true, restricted guilds 1 and 2 (packed).
        .. Tag(8, 2, [.. Wrapped(11, 6), .. Tag(4, 0, 1), .. Tag(3, 2, [.. BitConverter.GetBytes(1UL), .. BitConverter.GetBytes(2UL)])]),
        // appearance (13): developer_mode = true. localization (12): locale "fr".
        .. Tag(13, 2, Tag(2, 0, 1)),
        .. Tag(12, 2, Tag(1, 2, Tag(1, 2, "fr"u8.ToArray()))),
        // status (11) is left alone.
        .. Tag(11, 2, Tag(1, 2, Tag(1, 2, "dnd"u8.ToArray()))),
    ];

    [Fact]
    public void ReadsDiscordsFieldsAndDefaults()
    {
        var values = DiscordUserSettings.Decode(Sample);
        Assert.True(values["messageDisplayCompact"]!.GetValue<bool>());
        Assert.False(values["convertEmoticons"]!.GetValue<bool>()); // an empty wrapper is false, not the default
        Assert.Equal(3, values["explicitContentFriendDm"]!.GetValue<int>());
        Assert.Equal(0, values["explicitContentGuilds"]!.GetValue<int>());
        Assert.Equal(6u, values["friendSourceFlags"]!.GetValue<uint>());
        Assert.True(values["defaultGuildsRestricted"]!.GetValue<bool>());
        Assert.Equal(["1", "2"], values["restrictedGuildIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(values["developerMode"]!.GetValue<bool>());
        Assert.Equal("fr", values["locale"]!.GetValue<string>());
        // Not in the proto: Discord's defaults.
        Assert.True(values["inlineAttachmentMedia"]!.GetValue<bool>());
        Assert.Equal("ON_CLICK", values["renderSpoilers"]!.GetValue<string>());
        Assert.True(values["showCurrentGame"]!.GetValue<bool>());
        Assert.Null(values["defaultGuildsRestrictedV2"]);
        Assert.Equal(14u, DiscordUserSettings.Decode([])["friendSourceFlags"]!.GetValue<uint>());
    }

    [Fact]
    public void SavesOnlyTheChangedSectionWithEverythingElseKept()
    {
        var patch = DiscordUserSettings.Patch(Sample, Changes("""{"messageDisplayCompact": false}"""));
        Assert.NotNull(patch);
        var sections = DiscordProto.Fields(patch!).ToList();
        Assert.Equal([6], sections.Select(s => s.Number));
        var section = DiscordProto.Fields(sections[0].Value).ToList();
        // False is a present, empty BoolValue.
        Assert.Contains(section, f => f.Number == 17 && f.Wire == 2 && f.Value.Length == 0);
        // Fields Exo doesn't know, and the ones it didn't change, are sent back as they were.
        Assert.Contains(section, f => f.Number == 99 && f.Wire == 0 && f.Value.SequenceEqual(new byte[] { 7 }));
        Assert.Contains(section, f => f.Number == 21 && f.Value.Length == 0);
        Assert.Contains(section, f => f.Number == 29);
        // Read back over the original, the change lands and nothing else moves.
        var merged = DiscordUserSettings.Decode([.. Sample, .. patch!]);
        Assert.False(merged["messageDisplayCompact"]!.GetValue<bool>());
        Assert.Equal(3, merged["explicitContentFriendDm"]!.GetValue<int>());
    }

    [Fact]
    public void WritesNestedPlainAndListValues()
    {
        var patch = DiscordUserSettings.Patch(Sample, Changes("""{"explicitContentGuilds": 2, "developerMode": false, "restrictedGuildIds": ["5"], "friendSourceFlags": 14}"""))!;
        Assert.Equal([6, 8, 13], DiscordProto.Fields(patch).Select(f => f.Number).Order());
        var values = DiscordUserSettings.Decode(ReplaceSections(Sample, patch));
        Assert.Equal(2, values["explicitContentGuilds"]!.GetValue<int>());
        Assert.Equal(3, values["explicitContentFriendDm"]!.GetValue<int>()); // its sibling in the nested message kept
        Assert.False(values["developerMode"]!.GetValue<bool>()); // a plain false is left out, as proto3 does
        Assert.Equal(["5"], values["restrictedGuildIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(14u, values["friendSourceFlags"]!.GetValue<uint>());
        Assert.True(values["defaultGuildsRestricted"]!.GetValue<bool>());
        Assert.Equal("dnd", ReadStatus(ReplaceSections(Sample, patch)));
    }

    [Theory]
    [InlineData("""{"nope": true}""")]
    [InlineData("""{"messageDisplayCompact": 1}""")]
    [InlineData("""{"friendSourceFlags": 99}""")]
    [InlineData("""{"dmSpamFilterV2": 4}""")]
    [InlineData("""{"dmSpamFilterV2": -1}""")]
    [InlineData("""{"renderSpoilers": "SOMETIMES"}""")]
    [InlineData("""{"locale": "xx-YY"}""")]
    [InlineData("""{"restrictedGuildIds": ["abc"]}""")]
    [InlineData("""{}""")]
    public void RefusesValuesDiscordWouldNotTake(string json)
    {
        try { Assert.Null(DiscordUserSettings.Patch(Sample, Changes(json))); }
        catch (ArgumentException) { } // a bad id is refused before anything is sent
    }

    // audio_context_settings (16) > user (1): one map entry, fixed64 id (1) and AudioContextSetting (2).
    private static byte[] AudioEntry(ulong id, params byte[] setting) => Tag(1, 2, [.. Tag(1, 1, BitConverter.GetBytes(id)), .. Tag(2, 2, setting)]);

    [Fact]
    public void ReadsAndWritesEachPersonsVolumeAndMute()
    {
        // Person 7 at 50 with Mute Soundboard, person 8 muted at 0 (proto3 leaves the volume out), and a stream entry (2).
        byte[] proto =
        [
            .. Tag(16, 2, [.. AudioEntry(7, [.. Tag(2, 5, BitConverter.GetBytes(50f)), .. Tag(4, 0, 1)]), .. AudioEntry(8, Tag(1, 0, 1)), .. Tag(2, 2, Tag(1, 1, BitConverter.GetBytes(9UL)))]),
            .. Tag(13, 2, Tag(2, 0, 1)),
        ];
        var people = DiscordUserSettings.AudioUsers(proto);
        Assert.Equal(new DiscordUserSettings.AudioUser(false, 50, true), people["7"]);
        Assert.Equal(new DiscordUserSettings.AudioUser(true, 0), people["8"]);
        Assert.Equal(50, DiscordUserSettings.Decode(proto)["audioUsers"]!["7"]!["volume"]!.GetValue<float>());

        // 7 to 150 and muted; 8 back to 100 unmuted (left out); 9 new at 120.
        var patch = DiscordUserSettings.Patch(proto, Changes("""{"audioUsers":{"7":{"muted":true,"volume":150},"8":{"muted":false,"volume":100},"9":{"volume":120}}}"""))!;
        Assert.Equal([16], DiscordProto.Fields(patch).Select(f => f.Number));
        var after = DiscordUserSettings.AudioUsers(patch);
        Assert.Equal(new DiscordUserSettings.AudioUser(true, 150, true), after["7"]);
        Assert.False(after.ContainsKey("8"));
        Assert.Equal(new DiscordUserSettings.AudioUser(false, 120), after["9"]);
        // The stream map stays as it was.
        Assert.Contains(DiscordProto.Fields(DiscordProto.Fields(patch).Single().Value), f => f.Number == 2);
        Assert.Null(DiscordUserSettings.Patch(proto, Changes("""{"audioUsers":{"x":{"volume":1}}}""")));
        // A mute alone, as the save sends it (no volume yet): taken, not thrown on.
        var muteOnly = DiscordUserSettings.Patch(proto, Changes("""{"audioUsers":{"9":{"muted":true,"volume":null}}}"""))!;
        Assert.True(DiscordUserSettings.AudioUsers(muteOnly)["9"].Muted);
    }

    [Fact]
    public void ReadsAndWritesEachStreamsVolumeAndMuteWithoutTouchingPeople()
    {
        // Person 7 at 50 (map 1); 7's stream at 30 (map 2).
        byte[] proto = [.. Tag(16, 2, [.. AudioEntry(7, Tag(2, 5, BitConverter.GetBytes(50f))), .. Tag(2, 2, [.. Tag(1, 1, BitConverter.GetBytes(7UL)), .. Tag(2, 2, Tag(2, 5, BitConverter.GetBytes(30f)))])])];
        Assert.Equal(new DiscordUserSettings.AudioUser(false, 30), DiscordUserSettings.AudioUsers(proto, DiscordUserSettings.StreamMap)["7"]);
        Assert.Equal(30, DiscordUserSettings.Decode(proto)["audioStreams"]!["7"]!["volume"]!.GetValue<float>());

        var patch = DiscordUserSettings.Patch(proto, Changes("""{"audioStreams":{"7":{"muted":true}}}"""))!;
        Assert.Equal(new DiscordUserSettings.AudioUser(true, 30), DiscordUserSettings.AudioUsers(patch, DiscordUserSettings.StreamMap)["7"]);
        Assert.Equal(new DiscordUserSettings.AudioUser(false, 50), DiscordUserSettings.AudioUsers(patch)["7"]);
    }

    [Fact]
    public void WritesTheServerListAsDiscordsClientDoes()
    {
        // guild_folders (14): an old folder, guild_positions (2) 1 and 2 (packed), and an unknown field 9.
        byte[] proto = [.. Sample, .. Tag(14, 2, [.. Tag(1, 2, Tag(1, 2, BitConverter.GetBytes(3UL))), .. Tag(2, 2, [.. BitConverter.GetBytes(1UL), .. BitConverter.GetBytes(2UL)]), .. Tag(9, 0, 1)])];
        var patch = DiscordUserSettings.Patch(proto, Changes("""
            {"guildFolders": [
              {"id": null, "name": null, "color": null, "guildIds": ["5"]},
              {"id": "4000000000", "name": "Games", "color": 5793266, "guildIds": ["6", "7"]},
              {"id": "12", "name": "", "color": 0, "guildIds": ["8"]},
              {"id": "13", "name": "Empty", "color": null, "guildIds": []}
            ]}
            """))!;
        var section = Assert.Single(DiscordProto.Fields(patch));
        Assert.Equal(14, section.Number);
        var fields = DiscordProto.Fields(section.Value).ToList();
        // Each item a GuildFolder in order; a server alone is only its id, an empty folder is left out, and the rest stays.
        var folders = fields.Where(f => f.Number == 1).Select(f => f.Value).ToList();
        Assert.Equal(3, folders.Count);
        Assert.Equal(Tag(1, 2, BitConverter.GetBytes(5UL)), folders[0]);
        Assert.Equal((byte[])[.. Tag(1, 2, [.. BitConverter.GetBytes(6UL), .. BitConverter.GetBytes(7UL)]), .. Tag(2, 2, Tag(1, 0, Varint(4000000000))),
            .. Tag(3, 2, Tag(1, 2, "Games"u8.ToArray())), .. Tag(4, 2, Tag(1, 0, Varint(5793266)))], folders[1]);
        // No name is left out; color 0 is a present, empty wrapper.
        Assert.Equal((byte[])[.. Tag(1, 2, BitConverter.GetBytes(8UL)), .. Tag(2, 2, Tag(1, 0, Varint(12))), .. Tag(4, 2)], folders[2]);
        Assert.Contains(fields, f => f.Number == 2 && f.Value.Length == 16);
        Assert.Contains(fields, f => f.Number == 9);
        Assert.DoesNotContain(fields, f => f.Number == 1 && f.Value.SequenceEqual(Tag(1, 2, BitConverter.GetBytes(3UL))));
    }

    [Theory]
    [InlineData("""{"guildFolders": {}}""")]
    [InlineData("""{"guildFolders": [{"guildIds": ["x"]}]}""")]
    [InlineData("""{"guildFolders": [{"guildIds": [5]}]}""")]
    [InlineData("""{"guildFolders": [{"id": "folder", "guildIds": ["5"]}]}""")]
    [InlineData("""{"guildFolders": [{"id": "1"}]}""")]
    public void RefusesAServerListDiscordWouldNotTake(string json) => Assert.Null(DiscordUserSettings.Patch(Sample, Changes(json)));

    [Fact]
    public void MergesRepeatedSectionsAndReadsUnpackedIds()
    {
        byte[] proto = [.. Tag(6, 2, Wrapped(17, 1)), .. Tag(6, 2, Tag(21, 2)), .. Tag(8, 2, Tag(3, 1, BitConverter.GetBytes(9UL)))];
        var values = DiscordUserSettings.Decode(proto);
        Assert.True(values["messageDisplayCompact"]!.GetValue<bool>());
        Assert.False(values["convertEmoticons"]!.GetValue<bool>());
        Assert.Equal(["9"], values["restrictedGuildIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        var patch = DiscordUserSettings.Patch(proto, Changes("""{"renderEmbeds": false}"""))!;
        Assert.Single(DiscordProto.Fields(patch)); // one section 6 holding both halves
        var after = DiscordUserSettings.Decode(ReplaceSections(proto, patch));
        Assert.True(after["messageDisplayCompact"]!.GetValue<bool>());
        Assert.False(after["renderEmbeds"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("jane.doe@gmail.com", "j*******@gmail.com")]
    [InlineData("a@b.co", "a*@b.co")]
    [InlineData(null, null)]
    public void EmailNeverReachesThePageWhole(string? email, string? masked)
    {
        Assert.Equal(masked, WebHostBridge.MaskEmail(email));
    }

    [Fact]
    public void PhoneShowsOnlyItsLastFourDigits() =>
        Assert.Equal("*******1234", WebHostBridge.MaskPhone("+1 (555) 501-1234"));

    [Fact]
    public void ManualSensitivityOpensAboveTheThresholdAndHolds500Ms()
    {
        var gate = new VoiceGate();
        var quiet = Enumerable.Repeat(0.001f, 1920).ToArray(); // -60 dBFS
        var loud = Enumerable.Repeat(0.1f, 1920).ToArray(); // -20 dBFS
        Assert.False(gate.Voiced(quiet, automatic: false, threshold: -40));
        Assert.True(gate.Voiced(loud, automatic: false, threshold: -40));
        for (var i = 0; i < 25; i++) Assert.True(gate.Voiced(quiet, false, -40));
        Assert.False(gate.Voiced(quiet, false, -40));
        Assert.InRange(gate.Level, -61f, -59f);
    }

    [Fact]
    public void AutoGainRaisesQuietSpeechSlowlyAndNeverNoise()
    {
        var gain = new AutoGain();
        var frame = Enumerable.Repeat(0.001f, 1920).ToArray();
        gain.Process(frame, voiced: false, levelDb: -60);
        Assert.Equal(0.001f, frame[0]); // a pause is never pulled up
        for (var i = 0; i < 5000; i++) { frame = Enumerable.Repeat(0.001f, 1920).ToArray(); gain.Process(frame, true, -60); }
        Assert.InRange(frame[0], 0.0079f, 0.0080f); // at most +18 dB
    }

    // Applies a patch the way Discord's server does: each section sent replaces the stored one.
    private static byte[] ReplaceSections(byte[] stored, byte[] patch)
    {
        var sent = DiscordProto.Fields(patch).Select(f => f.Number).ToHashSet();
        var output = new List<byte>();
        foreach (var (number, wire, value) in DiscordProto.Fields(stored))
            if (!sent.Contains(number)) DiscordProto.Raw(output, number, wire, value);
        output.AddRange(patch);
        return [.. output];
    }

    private static string ReadStatus(byte[] proto)
    {
        var status = DiscordProto.Fields(proto).Single(f => f.Number == 11).Value;
        var wrapper = DiscordProto.Fields(status).Single(f => f.Number == 1).Value;
        return System.Text.Encoding.UTF8.GetString(DiscordProto.Fields(wrapper).Single().Value);
    }
}
