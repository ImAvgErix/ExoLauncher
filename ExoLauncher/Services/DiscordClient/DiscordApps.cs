using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Apps in Exo's Discord client, built the way Discord's web client builds them: the commands a
/// conversation offers (the server's or the DM's command index with the user's own apps' index), a
/// command's data from the values typed into its options (subcommands nested, in the command's own
/// option order, each value checked against its type, limits and choices), and the bodies Discord's
/// client posts to /interactions for a command (2), a click or choice on a message's component (3),
/// an option's autocomplete (4) and a modal's submit (5). Pure: the session sends what this builds.
/// </summary>
internal static class DiscordApps
{
    internal const int CommandType = 2, ComponentType = 3, AutocompleteType = 4, ModalSubmitType = 5;

    // Command option types.
    private const int SubCommand = 1, SubCommandGroup = 2, Text = 3, Integer = 4, Boolean = 5, UserOption = 6, ChannelOption = 7, RoleOption = 8,
        Mentionable = 9, NumberOption = 10, AttachmentOption = 11;

    // USE_APPLICATION_COMMANDS: a server channel that lacks it offers no commands.
    private const ulong UseCommands = 1UL << 31;

    /// <summary>A value typed into one option: text, a number, true or false, an id, or an attachment's index.</summary>
    internal sealed record OptionValue(string Name, JsonElement Value);

    /// <summary>
    /// The commands usable in a conversation, each once: the place's index (a server's, or a DM's) and the user's own apps'
    /// index, kept to the commands whose contexts include this place (0 a server, 1 a DM with the app's bot, 2 any other DM)
    /// and, in a server, that the user's permissions there allow.
    /// </summary>
    internal static (List<JsonElement> Commands, List<JsonElement> Applications) Usable(JsonElement? place, JsonElement? user, bool inGuild,
        IReadOnlyCollection<string> dmRecipients, ulong channelPerms)
    {
        var commands = new List<JsonElement>();
        var apps = new Dictionary<string, JsonElement>();
        var seen = new HashSet<string>();
        if (inGuild && (channelPerms & UseCommands) == 0) return (commands, []);
        foreach (var index in new[] { place, user })
        {
            if (index is not { ValueKind: JsonValueKind.Object } body) continue;
            var botOf = new Dictionary<string, string?>();
            if (body.TryGetProperty("applications", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var app in list.EnumerateArray())
                    if (Str(app, "id") is { } appId)
                    {
                        apps.TryAdd(appId, app);
                        botOf[appId] = app.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.Object ? Str(bot, "id") : Str(app, "bot_id");
                    }
            if (!body.TryGetProperty("application_commands", out var all) || all.ValueKind != JsonValueKind.Array) continue;
            foreach (var command in all.EnumerateArray())
            {
                if (Str(command, "id") is not { } id || Str(command, "application_id") is not { } appId || !seen.Add(id)) continue;
                if (!Allowed(command, inGuild, dmRecipients, botOf.GetValueOrDefault(appId), channelPerms)) continue;
                commands.Add(command);
            }
        }
        var used = commands.Select(c => Str(c, "application_id")).ToHashSet();
        return (commands, [.. apps.Where(a => used.Contains(a.Key)).Select(a => a.Value)]);
    }

    private static bool Allowed(JsonElement command, bool inGuild, IReadOnlyCollection<string> recipients, string? botId, ulong perms)
    {
        if (command.TryGetProperty("contexts", out var contexts) && contexts.ValueKind == JsonValueKind.Array)
        {
            var here = contexts.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Number).Select(c => c.GetInt32()).ToList();
            var fits = inGuild ? here.Contains(0) : here.Contains(2) || (here.Contains(1) && botId is not null && recipients.Contains(botId));
            if (!fits) return false;
        }
        else if (!inGuild && command.TryGetProperty("dm_permission", out var dm) && dm.ValueKind == JsonValueKind.False) return false;
        // default_member_permissions: the permissions a member needs ("0": only admins); none set, anyone.
        if (inGuild && Str(command, "default_member_permissions") is { } needed && ulong.TryParse(needed, CultureInfo.InvariantCulture, out var bits))
            return bits == 0 ? perms == ulong.MaxValue : (perms & bits) == bits;
        return true;
    }

    /// <summary>
    /// A command's data, as Discord's client sends it: the chosen subcommand path wrapping the values (in the command's option
    /// order), the root command itself, and for a user or message command the person or message it's on. With focused, an
    /// autocomplete ask: that option carries the text typed so far and focused, and required options may still be empty.
    /// </summary>
    internal static (JsonObject? Data, string? Error) CommandData(JsonElement command, IReadOnlyList<string> path, IReadOnlyList<OptionValue> values,
        string? targetId = null, string? focused = null, IReadOnlyList<JsonObject>? attachments = null)
    {
        var level = Options(command);
        var steps = new List<(int Type, string Name)>();
        foreach (var name in path.Take(2))
        {
            var step = level.FirstOrDefault(o => Str(o, "name") == name && Int(o, "type") is SubCommand or SubCommandGroup);
            if (step.ValueKind != JsonValueKind.Object) return (null, "That command changed. Try it again.");
            steps.Add((Int(step, "type"), name));
            level = Options(step);
        }
        if (level.Any(o => Int(o, "type") is SubCommand or SubCommandGroup)) return (null, "Choose what the command should do first.");
        var given = values.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.Last().Value);
        if (given.Keys.Any(name => level.All(o => Str(o, "name") != name))) return (null, "That command changed. Try it again.");
        var options = new JsonArray();
        foreach (var option in level)
        {
            var name = Str(option, "name")!;
            var type = Int(option, "type");
            if (focused == name)
            {
                var typed = given.TryGetValue(name, out var t) ? (t.ValueKind == JsonValueKind.String ? t.GetString() : t.ToString()) : "";
                options.Add(new JsonObject { ["type"] = type, ["name"] = name, ["value"] = typed ?? "", ["focused"] = true });
                continue;
            }
            if (!given.TryGetValue(name, out var raw) || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || raw.ValueKind == JsonValueKind.String && raw.GetString()!.Length == 0)
            {
                if (focused is null && option.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True)
                    return (null, $"Fill in {Label(option)} first.");
                continue;
            }
            var (value, error) = Coerce(option, raw, attachments?.Count ?? 0);
            if (error is not null) return (null, error);
            options.Add(new JsonObject { ["type"] = type, ["name"] = name, ["value"] = value });
        }
        for (var i = steps.Count - 1; i >= 0; i--)
            options = [new JsonObject { ["type"] = steps[i].Type, ["name"] = steps[i].Name, ["options"] = options }];
        var data = new JsonObject
        {
            ["version"] = Str(command, "version"),
            ["id"] = Str(command, "id"),
            ["name"] = Str(command, "name_default") ?? Str(command, "name"),
            ["type"] = Int(command, "type") is var kind and > 0 ? kind : 1,
            ["options"] = options,
            ["application_command"] = JsonNode.Parse(command.GetRawText()),
        };
        if (Str(command, "guild_id") is { } commandGuild) data["guild_id"] = commandGuild;
        if (targetId is not null) data["target_id"] = targetId;
        if (attachments is { Count: > 0 }) data["attachments"] = new JsonArray([.. attachments.Select(a => (JsonNode)a.DeepClone())]);
        return (data, null);
    }

    // One option's value in its type: text within its lengths, a whole number or a number within its range, true or false,
    // an id, or an attachment's index; with choices, one of them.
    private static (JsonNode? Value, string? Error) Coerce(JsonElement option, JsonElement raw, int attachments)
    {
        var label = Label(option);
        JsonNode? value;
        switch (Int(option, "type"))
        {
            case Text:
                var text = raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.ToString();
                if (option.TryGetProperty("min_length", out var minLength) && minLength.TryGetInt32(out var least) && text.Length < least)
                    return (null, $"{label} needs at least {least} characters.");
                if (option.TryGetProperty("max_length", out var maxLength) && maxLength.TryGetInt32(out var most) && text.Length > most)
                    return (null, $"{label} can be {most} characters at most.");
                if (text.Length > 6000) return (null, $"{label} is too long.");
                value = text;
                break;
            case Integer:
                if (!(raw.ValueKind == JsonValueKind.Number ? raw.TryGetInt64(out var whole) : long.TryParse(raw.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out whole)))
                    return (null, $"{label} needs a whole number.");
                if (OutOfRange(option, whole) is { } wholeRange) return (null, $"{label} {wholeRange}.");
                value = whole;
                break;
            case NumberOption:
                if (!(raw.ValueKind == JsonValueKind.Number ? raw.TryGetDouble(out var number) : double.TryParse(raw.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) || !double.IsFinite(number))
                    return (null, $"{label} needs a number.");
                if (OutOfRange(option, number) is { } range) return (null, $"{label} {range}.");
                value = number;
                break;
            case Boolean:
                if (raw.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, $"Choose True or False for {label}.");
                value = raw.GetBoolean();
                break;
            case UserOption or ChannelOption or RoleOption or Mentionable:
                if (raw.ValueKind != JsonValueKind.String || raw.GetString() is not { Length: > 0 and <= 20 } id || !id.All(char.IsAsciiDigit))
                    return (null, $"Choose {label} from the list.");
                value = id;
                break;
            case AttachmentOption:
                if (raw.ValueKind != JsonValueKind.String || !int.TryParse(raw.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var at) || at < 0 || at >= attachments)
                    return (null, $"Add a file for {label}.");
                value = at.ToString(CultureInfo.InvariantCulture);
                break;
            default:
                return (null, "That command has an option Exo can't fill in yet.");
        }
        if (option.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && !choices.EnumerateArray().Any(c => c.TryGetProperty("value", out var v) && SameValue(v, value!)))
            return (null, $"Choose {label} from the list.");
        return (value, null);
    }

    private static string? OutOfRange(JsonElement option, double value)
    {
        var min = option.TryGetProperty("min_value", out var lo) && lo.TryGetDouble(out var l) ? l : (double?)null;
        var max = option.TryGetProperty("max_value", out var hi) && hi.TryGetDouble(out var h) ? h : (double?)null;
        if (value < min || value > max)
            return min is not null && max is not null ? $"needs to be from {min.Value.ToString(CultureInfo.InvariantCulture)} to {max.Value.ToString(CultureInfo.InvariantCulture)}"
                : min is not null ? $"needs to be at least {min.Value.ToString(CultureInfo.InvariantCulture)}" : $"can be {max!.Value.ToString(CultureInfo.InvariantCulture)} at most";
        return null;
    }

    private static bool SameValue(JsonElement choice, JsonNode value) => choice.ValueKind switch
    {
        JsonValueKind.String => value.GetValueKind() == JsonValueKind.String && choice.GetString() == value.GetValue<string>(),
        JsonValueKind.Number => value.GetValueKind() == JsonValueKind.Number
            && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && Math.Abs(choice.GetDouble() - number) < 1e-9,
        _ => false,
    };

    /// <summary>Running a command (2): who, where, this session, the data, and the nonce its answer comes back with.</summary>
    internal static JsonObject CommandBody(string applicationId, string? guildId, string channelId, string sessionId, string nonce, JsonObject data, bool slash)
    {
        var body = new JsonObject { ["type"] = CommandType, ["application_id"] = applicationId };
        if (guildId is not null) body["guild_id"] = guildId;
        body["channel_id"] = channelId;
        body["session_id"] = sessionId;
        body["data"] = data;
        body["nonce"] = nonce;
        // Typed from the message box; a person's or a message's Apps menu sends none.
        if (slash) body["analytics_location"] = "slash_ui";
        return body;
    }

    /// <summary>An option's autocomplete ask (4): the command's data with the focused option, answered by the app as choices.</summary>
    internal static JsonObject AutocompleteBody(string applicationId, string? guildId, string channelId, string sessionId, string nonce, JsonObject data)
    {
        var body = new JsonObject { ["type"] = AutocompleteType, ["application_id"] = applicationId };
        if (guildId is not null) body["guild_id"] = guildId;
        body["channel_id"] = channelId;
        body["session_id"] = sessionId;
        body["data"] = data;
        body["nonce"] = nonce;
        return body;
    }

    // Component types that take a choice.
    private const int StringSelect = 3, UserSelect = 5, RoleSelect = 6, MentionableSelect = 7, ChannelSelect = 8;

    /// <summary>
    /// A press on a message's button or a choice in its select (3): the message, its flags and app, and the component's custom id,
    /// with a select's chosen values (the chosen people, roles or channels as their ids).
    /// </summary>
    internal static (JsonObject? Body, string? Error) ComponentBody(string applicationId, string? guildId, string channelId, string messageId, int messageFlags,
        string sessionId, string nonce, int componentType, string customId, IReadOnlyList<string>? values)
    {
        if (customId.Length is 0 or > 100) return (null, "That button doesn't work here.");
        var data = new JsonObject { ["component_type"] = componentType, ["custom_id"] = customId };
        if (componentType is StringSelect or UserSelect or RoleSelect or MentionableSelect or ChannelSelect)
        {
            if (values is null || values.Count > 25 || values.Any(v => v.Length is 0 or > 100)) return (null, "Choose from the list.");
            if (componentType != StringSelect && values.Any(v => !v.All(char.IsAsciiDigit))) return (null, "Choose from the list.");
            data["type"] = componentType;
            data["values"] = new JsonArray([.. values.Select(v => (JsonNode)v)]);
        }
        else if (componentType != 2) return (null, "That doesn't work here.");
        var body = new JsonObject { ["type"] = ComponentType, ["nonce"] = nonce };
        if (guildId is not null) body["guild_id"] = guildId;
        body["channel_id"] = channelId;
        body["message_flags"] = messageFlags;
        body["message_id"] = messageId;
        body["application_id"] = applicationId;
        body["session_id"] = sessionId;
        body["data"] = data;
        return (body, null);
    }

    /// <summary>An answer to one of a modal's fields: typed text, chosen values, or a checkbox.</summary>
    internal sealed record ModalAnswer(string? Value, IReadOnlyList<string>? Values, bool? Checked);

    // Modal component types.
    private const int ActionRow = 1, TextInput = 4, TextDisplay = 10, LabelBox = 18, FileUpload = 19, RadioGroup = 21, CheckboxGroup = 22, Checkbox = 23;

    /// <summary>
    /// A modal's submit (5): its fields in the modal's own shape (rows of text inputs, labels around one field, text displays by
    /// id), each with what was entered, keyed by custom id. A required field left empty, or text outside its lengths, stops it.
    /// </summary>
    internal static (JsonObject? Body, string? Error) ModalBody(JsonElement modal, string? guildId, string channelId, string sessionId, string nonce,
        IReadOnlyDictionary<string, ModalAnswer> answers, IReadOnlyList<JsonObject>? attachments = null)
    {
        if (Str(modal, "id") is not { } id || Str(modal, "custom_id") is not { } customId || modal.TryGetProperty("application", out var app) is false || Str(app, "id") is not { } appId)
            return (null, "That form isn't open anymore.");
        var components = new JsonArray();
        foreach (var top in Array(modal, "components"))
        {
            var (node, error) = Submitted(top, answers);
            if (error is not null) return (null, error);
            if (node is not null) components.Add(node);
        }
        var data = new JsonObject { ["id"] = id, ["custom_id"] = customId, ["components"] = components };
        if (attachments is { Count: > 0 }) data["attachments"] = new JsonArray([.. attachments.Select(a => (JsonNode)a.DeepClone())]);
        var body = new JsonObject { ["type"] = ModalSubmitType, ["application_id"] = appId, ["channel_id"] = channelId };
        if (guildId is not null) body["guild_id"] = guildId;
        body["data"] = data;
        body["session_id"] = sessionId;
        body["nonce"] = nonce;
        return (body, null);
    }

    private static (JsonNode? Node, string? Error) Submitted(JsonElement component, IReadOnlyDictionary<string, ModalAnswer> answers)
    {
        var type = Int(component, "type");
        var node = new JsonObject { ["type"] = type };
        if (component.TryGetProperty("id", out var cid) && cid.ValueKind == JsonValueKind.Number) node["id"] = cid.GetInt32();
        switch (type)
        {
            case ActionRow:
                var row = new JsonArray();
                foreach (var child in Array(component, "components"))
                {
                    var (inner, error) = Submitted(child, answers);
                    if (error is not null) return (null, error);
                    if (inner is not null) row.Add(inner);
                }
                node["components"] = row;
                return (node, null);
            case LabelBox:
                if (!component.TryGetProperty("component", out var field)) return (null, null);
                var (labelled, labelError) = Submitted(field, answers, Str(component, "label"));
                if (labelError is not null) return (null, labelError);
                node["component"] = labelled;
                return (node, null);
            case TextDisplay:
                return (node, null);
            default:
                return Submitted(component, answers, null);
        }
    }

    private static (JsonNode? Node, string? Error) Submitted(JsonElement field, IReadOnlyDictionary<string, ModalAnswer> answers, string? label)
    {
        var type = Int(field, "type");
        if (type is ActionRow or LabelBox or TextDisplay) return Submitted(field, answers);
        if (Str(field, "custom_id") is not { } customId) return (null, null);
        var name = label ?? Str(field, "label") ?? "this field";
        var answer = answers.GetValueOrDefault(customId);
        // Discord's client reads a field without "required" as optional.
        var required = field.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
        var node = new JsonObject { ["type"] = type };
        if (field.TryGetProperty("id", out var fid) && fid.ValueKind == JsonValueKind.Number) node["id"] = fid.GetInt32();
        node["custom_id"] = customId;
        switch (type)
        {
            case TextInput:
                var text = answer?.Value ?? "";
                if (required && text.Trim().Length == 0) return (null, $"Fill in {name} first.");
                if (text.Length > 0 && field.TryGetProperty("min_length", out var min) && min.TryGetInt32(out var least) && text.Length < least)
                    return (null, $"{name} needs at least {least} characters.");
                var most = field.TryGetProperty("max_length", out var max) && max.TryGetInt32(out var m) ? m : 4000;
                if (text.Length > most) return (null, $"{name} can be {most} characters at most.");
                node["value"] = text;
                return (node, null);
            case Checkbox:
                node["value"] = answer?.Checked == true;
                return (node, null);
            case RadioGroup:
                if (required && string.IsNullOrEmpty(answer?.Value)) return (null, $"Choose {name} first.");
                node["value"] = answer?.Value;
                return (node, null);
            case StringSelect or UserSelect or RoleSelect or MentionableSelect or ChannelSelect or CheckboxGroup or FileUpload:
                var values = answer?.Values ?? [];
                var minValues = field.TryGetProperty("min_values", out var mv) && mv.TryGetInt32(out var mvs) ? mvs : required ? 1 : 0;
                if (values.Count < minValues) return (null, $"Choose {name} first.");
                if (field.TryGetProperty("max_values", out var xv) && xv.TryGetInt32(out var most2) && values.Count > most2) return (null, $"Choose at most {most2} for {name}.");
                node["values"] = new JsonArray([.. values.Take(25).Select(v => (JsonNode)v)]);
                return (node, null);
            default:
                return (null, null);
        }
    }

    /// <summary>A file staged for an interaction, as Discord's client lists it: its index, name and where it was uploaded.</summary>
    internal static JsonObject Attachment(int index, string filename, string uploadedFilename) =>
        new() { ["id"] = index.ToString(CultureInfo.InvariantCulture), ["filename"] = filename, ["uploaded_filename"] = uploadedFilename };

    private static List<JsonElement> Options(JsonElement e) => [.. Array(e, "options")];

    private static string Label(JsonElement option) => Str(option, "name") ?? "that option";

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
