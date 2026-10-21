using System.Text.Json.Nodes;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The calls behind Discord's User Settings, as its web client makes them: the account
/// (users/@me), the profile (users/@me/profile), the settings protobuf, data-use consent,
/// logged-in devices (auth/sessions), authorized apps (oauth2/tokens) and connections.
/// </summary>
internal sealed partial class DiscordRest
{
    internal Task<Reply> MeAsync(CancellationToken ct) => SendAsync(HttpMethod.Get, "users/@me", null, ct);

    /// <summary>Display name or avatar (a data: URI, or null to remove it).</summary>
    internal Task<Reply> UpdateMeAsync(JsonObject body, CancellationToken ct) => SendAsync(HttpMethod.Patch, "users/@me", body, ct);

    /// <summary>About me, pronouns and banner color, as Discord's Profiles saves them.</summary>
    internal Task<Reply> UpdateProfileAsync(JsonObject body, CancellationToken ct) => SendAsync(HttpMethod.Patch, "users/@me/profile", body, ct);

    /// <summary>PreloadedUserSettings (protobuf, base64 in "settings"): see <see cref="DiscordUserSettings"/>.</summary>
    internal Task<Reply> UserSettingsAsync(CancellationToken ct) => SendAsync(HttpMethod.Get, "users/@me/settings-proto/1", null, ct);

    internal Task<Reply> SaveUserSettingsAsync(string settings, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, "users/@me/settings-proto/1", new JsonObject { ["settings"] = settings }, ct);

    /// <summary>usage_statistics and personalization, each { consented }.</summary>
    internal Task<Reply> ConsentAsync(CancellationToken ct) => SendAsync(HttpMethod.Get, "users/@me/consent", null, ct);

    internal Task<Reply> SetConsentAsync(IEnumerable<string> grant, IEnumerable<string> revoke, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "users/@me/consent", new JsonObject
        {
            ["grant"] = new JsonArray([.. grant.Select(g => (JsonNode)g)]),
            ["revoke"] = new JsonArray([.. revoke.Select(r => (JsonNode)r)]),
        }, ct);

    /// <summary>Logged-in devices: user_sessions, each with id_hash, approx_last_used_time and client_info.</summary>
    internal Task<Reply> SessionsAsync(CancellationToken ct) => SendAsync(HttpMethod.Get, "auth/sessions", null, ct);

    internal Task<Reply> LogoutSessionsAsync(IEnumerable<string> idHashes, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "auth/sessions/logout", new JsonObject { ["session_id_hashes"] = new JsonArray([.. idHashes.Select(h => (JsonNode)h)]) }, ct);

    internal Task<Reply> AuthorizedAppsAsync(CancellationToken ct) => SendAsync(HttpMethod.Get, "oauth2/tokens", null, ct);

    internal Task<Reply> DeauthorizeAsync(string tokenId, CancellationToken ct) => SendAsync(HttpMethod.Delete, $"oauth2/tokens/{Id(tokenId)}", null, ct);

    internal Task<Reply> ConnectionsAsync(CancellationToken ct) => SendAsync(HttpMethod.Get, "users/@me/connections", null, ct);
}
