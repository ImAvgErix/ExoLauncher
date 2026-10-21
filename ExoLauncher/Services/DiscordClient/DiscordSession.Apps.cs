using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Apps for the session: the commands each conversation offers (asked when the user opens the command list, kept a few
/// minutes as Discord's client keeps them), the forms apps open, and the gateway's answers to the user's interactions
/// (created, done, failed, a form, autocomplete choices), passed to the room by nonce as discord.interaction.
/// </summary>
internal sealed partial class DiscordSession
{
    private static readonly TimeSpan IndexLife = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (DateTimeOffset At, List<JsonElement> Commands, List<JsonElement> Apps)> _commands = [];
    // The forms apps opened lately, by interaction id, so a submit is built from the form as Discord sent it.
    private readonly Dictionary<string, JsonElement> _modals = [];

    /// <summary>This gateway session's id: every interaction names it, as Discord's client does.</summary>
    internal string? SessionId => _sessionId;

    /// <summary>The commands usable in a conversation and their apps, from Discord's indexes (cached a few minutes).</summary>
    internal async Task<(List<JsonElement> Commands, List<JsonElement> Apps, string? Error)> CommandsAsync(string channelId, string? guildId, bool fresh, CancellationToken ct)
    {
        lock (_commands)
            if (!fresh && _commands.TryGetValue(channelId, out var kept) && DateTimeOffset.UtcNow - kept.At < IndexLife) return (kept.Commands, kept.Apps, null);
        var place = Rest.CommandIndexAsync(guildId, guildId is null ? channelId : null, ct);
        var user = Rest.CommandIndexAsync(null, null, ct);
        await Task.WhenAll(place, user).ConfigureAwait(false);
        // A DM with a person has no index of its own to speak of; the user's own apps still work there.
        if (place.Result.Error is not null && user.Result.Error is not null) return ([], [], place.Result.Error);
        var (commands, apps) = DiscordApps.Usable(place.Result.Body, user.Result.Body, guildId is not null, Cache.RecipientsOf(channelId),
            guildId is null ? 0 : Cache.ChannelPermissions(channelId));
        lock (_commands) _commands[channelId] = (DateTimeOffset.UtcNow, commands, apps);
        return (commands, apps, null);
    }

    /// <summary>A command this conversation offered (null: not one of them; open the list again).</summary>
    internal JsonElement? Command(string channelId, string commandId)
    {
        lock (_commands)
            return _commands.TryGetValue(channelId, out var kept) && kept.Commands.FirstOrDefault(c => c.TryGetProperty("id", out var id) && id.GetString() == commandId) is { ValueKind: JsonValueKind.Object } found
                ? found : null;
    }

    internal JsonElement? Modal(string interactionId)
    {
        lock (_modals) return _modals.TryGetValue(interactionId, out var modal) ? modal : null;
    }

    internal void ModalDone(string interactionId)
    {
        lock (_modals) _modals.Remove(interactionId);
    }

    // The gateway's answers to the user's own interactions, by nonce; true when the event was one of them.
    private bool OnAppsDispatch(string type, JsonElement data)
    {
        string? Str(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (type)
        {
            case "INTERACTION_CREATE":
                Event?.Invoke("discord.interaction", new { kind = "created", nonce = Str("nonce"), id = Str("id") });
                return true;
            case "INTERACTION_SUCCESS":
                Event?.Invoke("discord.interaction", new { kind = "success", nonce = Str("nonce"), id = Str("id") });
                return true;
            case "INTERACTION_FAILURE":
                Event?.Invoke("discord.interaction", new { kind = "failure", nonce = Str("nonce"), id = Str("id"),
                    reason = data.TryGetProperty("reason_code", out var reason) && reason.ValueKind == JsonValueKind.Number ? reason.GetInt32() : (int?)null });
                return true;
            case "APPLICATION_COMMAND_AUTOCOMPLETE_RESPONSE":
                Event?.Invoke("discord.interaction", new { kind = "choices", nonce = Str("nonce"), choices = data.TryGetProperty("choices", out var choices) ? choices.Clone() : (JsonElement?)null });
                return true;
            case "INTERACTION_MODAL_CREATE":
                if (Str("id") is not { } id || Str("channel_id") is not { } channelId || !Cache.CanSee(channelId)) return true;
                lock (_modals)
                {
                    if (_modals.Count >= 8) _modals.Remove(_modals.Keys.First());
                    _modals[id] = data.Clone();
                }
                Event?.Invoke("discord.interaction", new { kind = "modal", nonce = Str("nonce"), id, modal = data.Clone() });
                return true;
            case "INTERACTION_IFRAME_MODAL_CREATE":
                // An Activity's own page in a frame: Exo doesn't run Activities, and says so where the user pressed.
                Event?.Invoke("discord.interaction", new { kind = "failure", nonce = Str("nonce"), id = Str("id"), reason = (int?)null, activity = true });
                return true;
        }
        return false;
    }

    /// <summary>Posts an interaction the user started; the answer comes back on the gateway by this nonce.</summary>
    internal async Task<(string? Nonce, string? Error)> InteractAsync(JsonObject body, CancellationToken ct)
    {
        var reply = await Rest.InteractAsync(body, ct).ConfigureAwait(false);
        return (body["nonce"]?.GetValue<string>(), reply.Error);
    }

    /// <summary>A dismissed "Only you can see this" message leaves the room, as in Discord (it lives on this session only).</summary>
    internal static void Dismiss(string channelId, string messageId) =>
        Event?.Invoke("discord.message", new { kind = "delete", channelId, id = messageId });
}
