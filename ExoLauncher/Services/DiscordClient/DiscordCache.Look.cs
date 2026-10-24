using System.Text.Json;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Someone's collectibles wherever Discord sends a user or member: avatar decoration (avatar_decoration_data),
/// nameplate (collectibles.nameplate), display name style (display_name_styles) and server tag (primary_guild, when
/// its identity is on: the server, its tag and badge). A server member's own win over their user's, as in Discord's
/// member list.
/// </summary>
internal sealed partial class DiscordCache
{
    internal sealed record Look(string? DecorationAsset, string? DecorationSku, string? NameplateSku, string? NameplateAsset, string? NameplatePalette,
        int NameFont, int NameEffect, int[]? NameColors, string? TagGuild = null, string? Tag = null, string? TagBadge = null);

    // Null when the payload carries none of the three (a trimmed user): what was known stays.
    private static Look? ReadLook(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var hasDecoration = e.TryGetProperty("avatar_decoration_data", out var decoration);
        var hasCollectibles = e.TryGetProperty("collectibles", out var collectibles);
        var hasStyles = e.TryGetProperty("display_name_styles", out var styles);
        var hasTag = e.TryGetProperty("primary_guild", out var tag);
        if (!hasDecoration && !hasCollectibles && !hasStyles && !hasTag) return null;
        // Discord shows a tag only while its identity is on and it names a server and a tag.
        var tagOn = tag.ValueKind == JsonValueKind.Object && Bool(tag, "identity_enabled") && Str(tag, "identity_guild_id") is not null && Str(tag, "tag") is not null;
        var plate = collectibles.ValueKind == JsonValueKind.Object && collectibles.TryGetProperty("nameplate", out var n) ? n : default;
        // Each may be null (nothing on), which reads as none.
        var styled = styles.ValueKind == JsonValueKind.Object;
        int[] colors = [.. Array(styles, "colors").Where(c => c.ValueKind == JsonValueKind.Number).Select(c => c.GetInt32())];
        return new Look(Str(decoration, "asset"), Str(decoration, "sku_id"), Str(plate, "sku_id"), Str(plate, "asset"), Str(plate, "palette"),
            styled ? Int(styles, "font_id") : 0, styled ? Int(styles, "effect_id") : 0, colors.Length > 0 ? colors : null,
            tagOn ? Str(tag, "identity_guild_id") : null, tagOn ? Str(tag, "tag") : null, tagOn ? Str(tag, "badge") : null);
    }

    // Under _gate: what the room draws for someone (the member's own first), or null when they have nothing.
    // The tag is the user's own (a member payload has none), kept under a member's collectibles.
    private object? LookOf(string userId, Look? member = null)
    {
        var user = _users.GetValueOrDefault(userId)?.Look;
        if (member is not { } m || !HasAny(m)) return LookDto(user);
        return LookDto(user?.Tag is null ? m : m with { TagGuild = user.TagGuild, Tag = user.Tag, TagBadge = user.TagBadge });
    }

    private static bool HasAny(Look l) => l.DecorationAsset is not null || l.NameplateSku is not null || l.NameColors is not null || l.Tag is not null;

    private static object? LookDto(Look? l) => l is null || !HasAny(l) ? null : new
    {
        decoration = l.DecorationAsset is null ? null : new { asset = l.DecorationAsset, skuId = l.DecorationSku },
        nameplate = l.NameplateSku is null ? null : new { skuId = l.NameplateSku, asset = l.NameplateAsset, palette = l.NameplatePalette },
        nameStyle = l.NameColors is null ? null : new { font = l.NameFont, effect = l.NameEffect, colors = l.NameColors },
        tag = l.Tag is null ? null : new { guildId = l.TagGuild, tag = l.Tag, badge = l.TagBadge },
    };
}
