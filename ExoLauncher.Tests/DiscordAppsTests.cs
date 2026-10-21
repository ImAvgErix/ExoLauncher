using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Apps' commands and interactions, which fail silently when wrong (Discord answers "This interaction failed" or nothing):
/// a command listed where its contexts or the user's permissions don't allow it, or twice from both indexes; a subcommand's
/// values not nested under it; options sent out of the command's order, in the wrong type, outside their limits or choices;
/// a required option let through empty (or demanded during autocomplete); the focused option not marked; a body missing the
/// session id or nonce, or carrying guild_id in a DM; a select's values not sent, or ids that aren't ids; a modal's fields
/// sent without their row or label, or a required field left empty.
/// </summary>
public sealed class DiscordAppsTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static readonly JsonElement Music = Json("""
    {
      "id": "500", "application_id": "50", "version": "9", "type": 1, "name": "music", "name_default": "music",
      "options": [
        { "type": 2, "name": "queue", "options": [
          { "type": 1, "name": "add", "options": [
            { "type": 3, "name": "song", "required": true, "min_length": 2, "max_length": 20, "autocomplete": true },
            { "type": 4, "name": "position", "min_value": 1, "max_value": 10 },
            { "type": 5, "name": "next" }
          ] }
        ] },
        { "type": 1, "name": "volume", "options": [
          { "type": 10, "name": "level", "required": true, "choices": [ { "name": "Half", "value": 0.5 }, { "name": "Full", "value": 1 } ] },
          { "type": 6, "name": "for" }
        ] }
      ]
    }
    """);

    private static List<DiscordApps.OptionValue> Values(params (string Name, string Json)[] values) =>
        [.. values.Select(v => new DiscordApps.OptionValue(v.Name, Json(v.Json)))];

    [Fact]
    public void Usable_keeps_commands_their_contexts_and_permissions_allow_once()
    {
        var guild = Json("""
        { "applications": [ { "id": "50", "name": "Music", "bot_id": "51" } ],
          "application_commands": [
            { "id": "1", "application_id": "50", "name": "play", "contexts": [0] },
            { "id": "2", "application_id": "50", "name": "ban", "default_member_permissions": "4" },
            { "id": "3", "application_id": "50", "name": "dmonly", "contexts": [1, 2] } ] }
        """);
        var user = Json("""
        { "applications": [ { "id": "60", "name": "Mine" } ],
          "application_commands": [
            { "id": "1", "application_id": "50", "name": "play", "contexts": [0] },
            { "id": "4", "application_id": "60", "name": "note", "contexts": [0, 1, 2] } ] }
        """);
        var useCommands = 1UL << 31;
        var (commands, apps) = DiscordApps.Usable(guild, user, inGuild: true, [], useCommands);
        Assert.Equal(["1", "4"], commands.Select(c => c.GetProperty("id").GetString()));
        Assert.Equal(["50", "60"], apps.Select(a => a.GetProperty("id").GetString()).Order());
        (commands, _) = DiscordApps.Usable(guild, user, inGuild: true, [], useCommands | 4);
        Assert.Contains(commands, c => c.GetProperty("id").GetString() == "2");
        // No Use Application Commands in the channel: none.
        Assert.Empty(DiscordApps.Usable(guild, user, inGuild: true, [], 0).Commands);
        // A DM: commands for DMs only, a bot DM's (context 1) only with that app's bot in it.
        var dm = Json("""
        { "applications": [ { "id": "50", "name": "Music", "bot": { "id": "51" } } ],
          "application_commands": [ { "id": "5", "application_id": "50", "name": "help", "contexts": [1] } ] }
        """);
        (commands, _) = DiscordApps.Usable(dm, user, inGuild: false, ["7"], 0);
        Assert.Equal(["4"], commands.Select(c => c.GetProperty("id").GetString()));
        (commands, _) = DiscordApps.Usable(dm, user, inGuild: false, ["51"], 0);
        Assert.Equal(["5", "4"], commands.Select(c => c.GetProperty("id").GetString()));
    }

    [Fact]
    public void Command_data_nests_the_subcommand_path_in_option_order()
    {
        var (data, error) = DiscordApps.CommandData(Music, ["queue", "add"], Values(("next", "true"), ("song", "\"Halo\""), ("position", "\"3\"")));
        Assert.Null(error);
        Assert.Equal("500", data!["id"]!.GetValue<string>());
        Assert.Equal("9", data["version"]!.GetValue<string>());
        Assert.Equal(1, data["type"]!.GetValue<int>());
        Assert.Equal("music", data["application_command"]!["name"]!.GetValue<string>());
        var group = data["options"]!.AsArray().Single()!;
        Assert.Equal((2, "queue"), (group["type"]!.GetValue<int>(), group["name"]!.GetValue<string>()));
        var sub = group["options"]!.AsArray().Single()!;
        Assert.Equal("add", sub["name"]!.GetValue<string>());
        var leaf = sub["options"]!.AsArray();
        Assert.Equal(["song", "position", "next"], leaf.Select(o => o!["name"]!.GetValue<string>()));
        Assert.Equal(3L, leaf[1]!["value"]!.GetValue<long>());
        Assert.True(leaf[2]!["value"]!.GetValue<bool>());
        Assert.Null(leaf[0]!["focused"]);
    }

    [Theory]
    [InlineData("""{"song":"\"H\""}""", "song needs at least 2 characters.")]
    [InlineData("""{"song":"\"Halo\"","position":"\"11\""}""", "position needs to be from 1 to 10.")]
    [InlineData("""{"song":"\"Halo\"","position":"\"2.5\""}""", "position needs a whole number.")]
    [InlineData("""{"position":"\"2\""}""", "Fill in song first.")]
    [InlineData("""{"song":"\"Halo\"","next":"\"yes\""}""", "Choose True or False for next.")]
    [InlineData("""{"song":"\"Halo\"","nope":"1"}""", "That command changed. Try it again.")]
    public void Command_data_refuses_values_Discord_would(string given, string expected)
    {
        var values = JsonDocument.Parse(given).RootElement.EnumerateObject().Select(p => new DiscordApps.OptionValue(p.Name, Json(p.Value.GetString()!))).ToList();
        var (data, error) = DiscordApps.CommandData(Music, ["queue", "add"], values);
        Assert.Null(data);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Command_data_takes_only_a_choice_and_ids_as_ids()
    {
        Assert.Equal("Choose level from the list.", DiscordApps.CommandData(Music, ["volume"], Values(("level", "0.7"))).Error);
        Assert.Equal("Choose for from the list.", DiscordApps.CommandData(Music, ["volume"], Values(("level", "1"), ("for", "\"me\""))).Error);
        var (data, _) = DiscordApps.CommandData(Music, ["volume"], Values(("level", "0.5"), ("for", "\"123\"")));
        var leaf = data!["options"]![0]!["options"]!.AsArray();
        Assert.Equal(0.5, leaf[0]!["value"]!.GetValue<double>());
        Assert.Equal("123", leaf[1]!["value"]!.GetValue<string>());
        Assert.Equal("Choose what the command should do first.", DiscordApps.CommandData(Music, ["queue"], []).Error);
    }

    [Fact]
    public void Autocomplete_marks_the_focused_option_with_the_typed_text()
    {
        var (data, error) = DiscordApps.CommandData(Music, ["queue", "add"], Values(("song", "\"H\""), ("position", "\"2\"")), focused: "song");
        Assert.Null(error);
        var leaf = data!["options"]![0]!["options"]![0]!["options"]!.AsArray();
        Assert.Equal("H", leaf[0]!["value"]!.GetValue<string>());
        Assert.True(leaf[0]!["focused"]!.GetValue<bool>());
        Assert.Equal(2L, leaf[1]!["value"]!.GetValue<long>());
    }

    [Fact]
    public void Context_menu_commands_carry_their_target_and_no_slash_location()
    {
        var user = Json("""{ "id": "700", "application_id": "50", "version": "1", "type": 2, "name": "High Five" }""");
        var (data, _) = DiscordApps.CommandData(user, [], [], targetId: "42");
        Assert.Equal(2, data!["type"]!.GetValue<int>());
        Assert.Equal("42", data["target_id"]!.GetValue<string>());
        var body = DiscordApps.CommandBody("50", null, "9", "sess", "111", (JsonObject)data.DeepClone(), slash: false);
        Assert.Equal(2, body["type"]!.GetValue<int>());
        Assert.False(body.ContainsKey("guild_id"));
        Assert.False(body.ContainsKey("analytics_location"));
        Assert.Equal("sess", body["session_id"]!.GetValue<string>());
        Assert.Equal("111", body["nonce"]!.GetValue<string>());
        var slash = DiscordApps.CommandBody("50", "1", "9", "sess", "111", (JsonObject)data.DeepClone(), slash: true);
        Assert.Equal("1", slash["guild_id"]!.GetValue<string>());
        Assert.Equal("slash_ui", slash["analytics_location"]!.GetValue<string>());
    }

    [Fact]
    public void Attachment_options_name_a_staged_file_by_index()
    {
        var upload = Json("""{ "id": "800", "application_id": "50", "version": "1", "type": 1, "name": "scan", "options": [ { "type": 11, "name": "file", "required": true } ] }""");
        Assert.Equal("Add a file for file.", DiscordApps.CommandData(upload, [], Values(("file", "\"0\""))).Error);
        var staged = new[] { DiscordApps.Attachment(0, "a.png", "abc/a.png") };
        var (data, _) = DiscordApps.CommandData(upload, [], Values(("file", "\"0\"")), attachments: staged);
        Assert.Equal("0", data!["options"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("abc/a.png", data["attachments"]![0]!["uploaded_filename"]!.GetValue<string>());
    }

    [Fact]
    public void Component_bodies_are_Discords_own()
    {
        var (button, _) = DiscordApps.ComponentBody("50", "1", "9", "77", 64, "sess", "222", 2, "ok", null);
        Assert.Equal(3, button!["type"]!.GetValue<int>());
        Assert.Equal(64, button["message_flags"]!.GetValue<int>());
        Assert.Equal("77", button["message_id"]!.GetValue<string>());
        Assert.Equal("""{"component_type":2,"custom_id":"ok"}""", button["data"]!.ToJsonString());
        var (select, _) = DiscordApps.ComponentBody("50", null, "9", "77", 0, "sess", "222", 3, "pick", ["a", "b"]);
        Assert.False(select!.ContainsKey("guild_id"));
        Assert.Equal("""{"component_type":3,"custom_id":"pick","type":3,"values":["a","b"]}""", select["data"]!.ToJsonString());
        Assert.Null(DiscordApps.ComponentBody("50", null, "9", "77", 0, "sess", "222", 5, "who", ["me"]).Body);
        Assert.Null(DiscordApps.ComponentBody("50", null, "9", "77", 0, "sess", "222", 3, "pick", null).Body);
    }

    private static readonly JsonElement Form = Json("""
    {
      "id": "900", "custom_id": "feedback", "title": "Feedback", "application": { "id": "50", "name": "Music" },
      "components": [
        { "type": 1, "id": 1, "components": [ { "type": 4, "id": 2, "custom_id": "name", "label": "Name", "style": 1, "max_length": 10, "required": true } ] },
        { "type": 10, "id": 3, "content": "Tell us more" },
        { "type": 18, "id": 4, "label": "Mood", "component": { "type": 3, "id": 5, "custom_id": "mood", "required": true, "options": [ { "label": "Good", "value": "g" } ] } },
        { "type": 18, "id": 6, "label": "Notes", "component": { "type": 4, "id": 7, "custom_id": "notes", "style": 2 } }
      ]
    }
    """);

    [Fact]
    public void Modal_submit_keeps_the_forms_shape_with_the_answers()
    {
        var answers = new Dictionary<string, DiscordApps.ModalAnswer>
        {
            ["name"] = new("Ada", null, null),
            ["mood"] = new(null, ["g"], null),
        };
        var (body, error) = DiscordApps.ModalBody(Form, "1", "9", "sess", "333", answers);
        Assert.Null(error);
        Assert.Equal(5, body!["type"]!.GetValue<int>());
        Assert.Equal("50", body["application_id"]!.GetValue<string>());
        var data = body["data"]!;
        Assert.Equal("900", data["id"]!.GetValue<string>());
        Assert.Equal("feedback", data["custom_id"]!.GetValue<string>());
        Assert.Equal(
            """[{"type":1,"id":1,"components":[{"type":4,"id":2,"custom_id":"name","value":"Ada"}]},{"type":10,"id":3},{"type":18,"id":4,"component":{"type":3,"id":5,"custom_id":"mood","values":["g"]}},{"type":18,"id":6,"component":{"type":4,"id":7,"custom_id":"notes","value":""}}]""",
            data["components"]!.ToJsonString());
    }

    [Fact]
    public void Modal_submit_stops_on_an_empty_required_field_or_too_much_text()
    {
        var empty = new Dictionary<string, DiscordApps.ModalAnswer> { ["mood"] = new(null, ["g"], null) };
        Assert.Equal("Fill in Name first.", DiscordApps.ModalBody(Form, null, "9", "sess", "1", empty).Error);
        var noMood = new Dictionary<string, DiscordApps.ModalAnswer> { ["name"] = new("Ada", null, null) };
        Assert.Equal("Choose Mood first.", DiscordApps.ModalBody(Form, null, "9", "sess", "1", noMood).Error);
        var tooLong = new Dictionary<string, DiscordApps.ModalAnswer> { ["name"] = new("Ada Lovelace!", null, null), ["mood"] = new(null, ["g"], null) };
        Assert.Equal("Name can be 10 characters at most.", DiscordApps.ModalBody(Form, null, "9", "sess", "1", tooLong).Error);
    }
}
