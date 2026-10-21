using System.Text.Json;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Services;

/// <summary>
/// Apps in the Discord room (discord.apps): the commands a conversation offers, and the interactions the user starts there
/// by their own press or Enter: running a command, an option's autocomplete, a message's button or select, a form's submit,
/// and dismissing an "Only you can see this" message. Nothing here runs on its own; every call is one thing the user did.
/// </summary>
public sealed partial class WebHostBridge
{
    private static async Task<object> DiscordAppsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (Channel(p, hasParams) is not { } c) return new { ok = false, message = "Discord isn't connected." };
        var session = c.Session;
        var action = ReadString(p, hasParams, "action");
        if (action == "commands")
        {
            var (commands, apps, error) = await session.CommandsAsync(c.Id, c.Guild, ReadBool(p, hasParams, "fresh") == true, ct).ConfigureAwait(false);
            return new { ok = error is null, message = error, commands, applications = apps };
        }
        if (action == "dismiss")
        {
            if (ReadString(p, hasParams, "messageId") is not { } dismissed) return new { ok = false };
            DiscordSession.Dismiss(c.Id, DiscordRest.Id(dismissed));
            return new { ok = true };
        }
        if (session.SessionId is not { } sessionId) return new { ok = false, message = "Discord is still connecting. Try again in a moment." };
        var nonce = DiscordRest.NewNonce();
        switch (action)
        {
            case "run" or "autocomplete":
            {
                if (ReadString(p, hasParams, "commandId") is not { } commandId || session.Command(c.Id, DiscordRest.Id(commandId)) is not { } command)
                    return new { ok = false, message = "That command isn't here anymore. Type / to see what is." };
                var autocomplete = action == "autocomplete";
                var target = ReadString(p, hasParams, "targetId") is { } t ? DiscordRest.Id(t) : null;
                var files = autocomplete ? [] : TakeUploads(p, hasParams);
                List<System.Text.Json.Nodes.JsonObject>? staged = null;
                if (files.Count > 0)
                {
                    (staged, var stageError) = await session.Rest.StageAsync(c.Id, files, ct).ConfigureAwait(false);
                    if (staged is null) return new { ok = false, message = stageError };
                }
                var (data, error) = DiscordApps.CommandData(command, Strings(p, "path", 2), OptionValues(p), target,
                    autocomplete ? ReadString(p, hasParams, "focused") : null, staged);
                if (data is null) return new { ok = false, message = error };
                var appId = command.GetProperty("application_id").GetString()!;
                var body = autocomplete
                    ? DiscordApps.AutocompleteBody(appId, c.Guild, c.Id, sessionId, nonce, data)
                    : DiscordApps.CommandBody(appId, c.Guild, c.Id, sessionId, nonce, data, slash: target is null);
                return Sent(await session.InteractAsync(body, ct).ConfigureAwait(false));
            }
            case "component":
            {
                if (ReadString(p, hasParams, "messageId") is not { } messageId || ReadString(p, hasParams, "applicationId") is not { } appId
                    || ReadString(p, hasParams, "customId") is not { } customId)
                    return new { ok = false, message = "That doesn't work here." };
                var (body, error) = DiscordApps.ComponentBody(DiscordRest.Id(appId), c.Guild, c.Id, DiscordRest.Id(messageId), ReadInt(p, "messageFlags", 0, int.MaxValue, 0),
                    sessionId, nonce, ReadInt(p, "componentType", 0, 30, 0), customId, hasParams && p.TryGetProperty("values", out _) ? Strings(p, "values", 25) : null);
                return body is null ? new { ok = false, message = error } : Sent(await session.InteractAsync(body, ct).ConfigureAwait(false));
            }
            case "modal":
            {
                if (ReadString(p, hasParams, "interactionId") is not { } interactionId || session.Modal(DiscordRest.Id(interactionId)) is not { } modal)
                    return new { ok = false, message = "That form isn't open anymore." };
                var files = TakeUploads(p, hasParams);
                List<System.Text.Json.Nodes.JsonObject>? staged = null;
                if (files.Count > 0)
                {
                    (staged, var stageError) = await session.Rest.StageAsync(c.Id, files, ct).ConfigureAwait(false);
                    if (staged is null) return new { ok = false, message = stageError };
                }
                var (body, error) = DiscordApps.ModalBody(modal, c.Guild, c.Id, sessionId, nonce, ModalAnswers(p), staged);
                if (body is null) return new { ok = false, message = error };
                var sent = await session.InteractAsync(body, ct).ConfigureAwait(false);
                if (sent.Error is null) session.ModalDone(interactionId);
                return Sent(sent);
            }
        }
        return new { ok = false, message = "Not an app action." };
    }

    private static object Sent((string? Nonce, string? Error) sent) => new { ok = sent.Error is null, message = sent.Error, nonce = sent.Nonce };

    private static List<string> Strings(JsonElement p, string name, int most) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Take(most)]
            : [];

    // The values typed into a command's options: [{ name, value }].
    private static List<DiscordApps.OptionValue> OptionValues(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object && o.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && o.TryGetProperty("value", out _))
                .Take(25).Select(o => new DiscordApps.OptionValue(o.GetProperty("name").GetString()!, o.GetProperty("value").Clone()))]
            : [];

    // What was entered in a form: { customId: { value | values | checked } }.
    private static Dictionary<string, DiscordApps.ModalAnswer> ModalAnswers(JsonElement p)
    {
        var answers = new Dictionary<string, DiscordApps.ModalAnswer>();
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("answers", out var all) || all.ValueKind != JsonValueKind.Object) return answers;
        foreach (var field in all.EnumerateObject().Take(25))
        {
            var a = field.Value;
            if (a.ValueKind != JsonValueKind.Object) continue;
            answers[field.Name] = new DiscordApps.ModalAnswer(
                a.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null,
                a.TryGetProperty("values", out _) ? Strings(a, "values", 25) : null,
                a.TryGetProperty("checked", out var ch) && ch.ValueKind is JsonValueKind.True or JsonValueKind.False ? ch.GetBoolean() : null);
        }
        return answers;
    }
}
