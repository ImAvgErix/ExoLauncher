using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>Apps: the command indexes, interactions, and files staged for a command or a modal, as Discord's client sends them.</summary>
internal sealed partial class DiscordRest
{
    // Where Discord's client puts a file before an interaction names it (the upload_url Discord answers with).
    internal const string UploadHost = "discord-attachments-uploads-prd.storage.googleapis.com";

    /// <summary>
    /// A command index: a server's (guild), a DM's (channel), or the user's own apps' (neither). Discord answers 202 while it
    /// builds one; its client asks again after five seconds, at most three times.
    /// </summary>
    internal async Task<Reply> CommandIndexAsync(string? guildId, string? channelId, CancellationToken ct)
    {
        var path = guildId is not null ? $"guilds/{Id(guildId)}/application-command-index"
            : channelId is not null ? $"channels/{Id(channelId)}/application-command-index" : "users/@me/application-command-index";
        for (var attempt = 0; ; attempt++)
        {
            var reply = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
            if (reply.Error is not null || reply.Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("application_commands", out _) || attempt >= 2)
                return reply;
            await Task.Delay(5000, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Posts an interaction (a command, a component, an autocomplete ask or a modal's submit); Discord answers 204.</summary>
    internal Task<Reply> InteractAsync(JsonObject body, CancellationToken ct) => SendAsync(HttpMethod.Post, "interactions", body, ct);

    /// <summary>
    /// Stages files for an interaction, as Discord's client does: Discord gives each an upload address on its storage, the file
    /// goes there, and the interaction names it by its index and uploaded name. Null and the reason when one didn't go.
    /// </summary>
    internal async Task<(List<JsonObject>? Staged, string? Error)> StageAsync(string channelId, IReadOnlyList<Upload> files, CancellationToken ct)
    {
        var staged = new List<JsonObject>();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var ask = new JsonObject
            {
                ["files"] = new JsonArray(new JsonObject { ["filename"] = file.Name, ["file_size"] = file.Data.Length, ["id"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture), ["is_clip"] = false }),
            };
            var reply = await SendAsync(HttpMethod.Post, $"channels/{Id(channelId)}/attachments", ask, ct).ConfigureAwait(false);
            if (reply.Error is not null) return (null, reply.Error);
            if (reply.Body is not { ValueKind: JsonValueKind.Object } body || !body.TryGetProperty("attachments", out var list) || list.ValueKind != JsonValueKind.Array
                || list.GetArrayLength() == 0 || list[0].TryGetProperty("upload_url", out var url) is false || list[0].TryGetProperty("upload_filename", out var name) is false
                || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttps || target.Host != UploadHost)
                return (null, "Discord didn't take the file.");
            using var put = new HttpRequestMessage(HttpMethod.Put, target) { Content = new ByteArrayContent(file.Data) };
            put.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(file.Type, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
            try
            {
                using var answer = await _http.SendAsync(put, ct).ConfigureAwait(false);
                if (!answer.IsSuccessStatusCode) return (null, "The file didn't upload. Try again.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return (null, "The file didn't upload. Try again.");
            }
            staged.Add(DiscordApps.Attachment(i, file.Name, name.GetString() ?? ""));
        }
        return (staged, null);
    }

    /// <summary>A Discord nonce: now as a snowflake, as Discord's client makes one for each interaction.</summary>
    internal static string NewNonce() => Nonce();
}
