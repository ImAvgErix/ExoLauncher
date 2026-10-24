using System.Text.Json.Nodes;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Server Settings pages beyond the server itself, called as Discord's web client calls them: the server's profile
/// (Server Tag), soundboard sounds, templates, widget, custom invite link, rules screening (member verification) and
/// welcome screen.
/// </summary>
internal sealed partial class DiscordRest
{
    /// <summary>The server's tag: its name, badge (Discord's preset number) and two colors.</summary>
    internal Task<Reply> SaveGuildProfileAsync(string guildId, JsonObject changes, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/profile", changes, ct);

    internal Task<Reply> SoundsAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/soundboard-sounds", null, ct);

    /// <summary>Upload Sound: the name, the clip as a data URL, its volume (0 to 1) and an emoji.</summary>
    internal Task<Reply> UploadSoundAsync(string guildId, JsonObject sound, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/soundboard-sounds", sound, ct);

    internal Task<Reply> EditSoundAsync(string guildId, string soundId, JsonObject sound, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/soundboard-sounds/{Id(soundId)}", sound, ct);

    internal Task<Reply> DeleteSoundAsync(string guildId, string soundId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"guilds/{Id(guildId)}/soundboard-sounds/{Id(soundId)}", null, ct);

    internal Task<Reply> TemplatesAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/templates", null, ct);

    internal Task<Reply> CreateTemplateAsync(string guildId, string name, string? description, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"guilds/{Id(guildId)}/templates", new JsonObject { ["name"] = name, ["description"] = description }, ct);

    /// <summary>Sync Template: the template takes the server as it is now.</summary>
    internal Task<Reply> SyncTemplateAsync(string guildId, string code, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"guilds/{Id(guildId)}/templates/{Uri.EscapeDataString(code)}", null, ct);

    internal Task<Reply> DeleteTemplateAsync(string guildId, string code, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"guilds/{Id(guildId)}/templates/{Uri.EscapeDataString(code)}", null, ct);

    internal Task<Reply> WidgetAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/widget", null, ct);

    /// <summary>Server Widget on or off, and the channel its Join button invites to (null: none).</summary>
    internal Task<Reply> SaveWidgetAsync(string guildId, bool enabled, string? channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/widget", new JsonObject { ["enabled"] = enabled, ["channel_id"] = channelId is null ? null : Id(channelId) }, ct);

    internal Task<Reply> VanityAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/vanity-url", null, ct);

    internal Task<Reply> SaveVanityAsync(string guildId, string code, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/vanity-url", new JsonObject { ["code"] = code }, ct);

    internal Task<Reply> MemberVerificationAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/member-verification?with_guild=false", null, ct);

    /// <summary>Rules screening: the form (Discord's TERMS field with the rules) and whether it's on.</summary>
    internal Task<Reply> SaveMemberVerificationAsync(string guildId, JsonObject changes, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/member-verification", changes, ct);

    /// <summary>Applications waiting for review (Apply to Join): Discord's total of SUBMITTED requests.</summary>
    internal Task<Reply> PendingApplicationsAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/requests?status=SUBMITTED&limit=1", null, ct);

    /// <summary>Server Discovery's checklist for this server: safe environment, size, age, activity, content and 2FA.</summary>
    internal Task<Reply> DiscoveryRequirementsAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/discovery-requirements", null, ct);

    internal Task<Reply> DiscoveryMetadataAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/discovery-metadata", null, ct);

    /// <summary>Discoverable's category, tags and emoji information, as Discord's Access page saves them.</summary>
    internal Task<Reply> SaveDiscoveryMetadataAsync(string guildId, JsonObject changes, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/discovery-metadata", changes, ct);

    internal Task<Reply> DiscoveryCategoriesAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "discovery/categories?locale=en-US&primary_only=false", null, ct);

    /// <summary>Server Insights' weekly numbers (Discord's overview): engagement, growth or retention since <paramref name="start"/>.</summary>
    internal Task<Reply> InsightsAsync(string guildId, string kind, DateTimeOffset start, DateTimeOffset end, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/analytics/{kind}?start={Uri.EscapeDataString(start.UtcDateTime.ToString("O"))}&end={Uri.EscapeDataString(end.UtcDateTime.ToString("O"))}&interval=2", null, ct);

    internal Task<Reply> WelcomeScreenAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/welcome-screen", null, ct);

    internal Task<Reply> SaveWelcomeScreenAsync(string guildId, JsonObject screen, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"guilds/{Id(guildId)}/welcome-screen", screen, ct);

    /// <summary>Onboarding: its questions, Default Channels, on or off and its mode.</summary>
    internal Task<Reply> OnboardingAsync(string guildId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"guilds/{Id(guildId)}/onboarding", null, ct);

    /// <summary>Onboarding's changed parts, as Discord's page saves each (only the fields sent change).</summary>
    internal Task<Reply> SaveOnboardingAsync(string guildId, JsonObject changes, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"guilds/{Id(guildId)}/onboarding", changes, ct);

    /// <summary>The whole Server Guide (welcome sign, to-dos, resources, on or off), as Discord's page saves it.</summary>
    internal Task<Reply> SaveServerGuideAsync(string guildId, JsonObject guide, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"guilds/{Id(guildId)}/new-member-welcome", guide, ct);
}
