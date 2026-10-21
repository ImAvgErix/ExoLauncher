using System.Text.Json;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Someone's collectibles wherever Discord sends a user or member: avatar decoration (avatar_decoration_data),
/// nameplate (collectibles.nameplate) and display name style (display_name_styles). A server member's own win
/// over their user's, as in Discord's member list.
/// </summary>
internal sealed partial class DiscordCache
{
    internal sealed record Look(string? DecorationAsset, string? DecorationSku, string? NameplateSku, string? NameplateAsset, string? NameplatePalette,
        int NameFont, int NameEffect, int[]? NameColors);

    // Null when the payload carries none of the three (a trimmed user): what was known stays.
    private static Look? ReadLook(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var hasDecoration = e.TryGetProperty("avatar_decoration_data", out var decoration);
        var hasCollectibles = e.TryGetProperty("collectibles", out var collectibles);
        var hasStyles = e.TryGetProperty("display_name_styles", out var styles);
        if (!hasDecoration && !hasCollectibles && !hasStyles) return null;
        var plate = collectibles.ValueKind == JsonValueKind.Object && collectibles.TryGetProperty("nameplate", out var n) ? n : default;
        // Each may be null (nothing on), which reads as none.
        var styled = styles.ValueKind == JsonValueKind.Object;
        int[] colors = [.. Array(styles, "colors").Where(c => c.ValueKind == JsonValueKind.Number).Select(c => c.GetInt32())];
        return new Look(Str(decoration, "asset"), Str(decoration, "sku_id"), Str(plate, "sku_id"), Str(plate, "asset"), Str(plate, "palette"),
            styled ? Int(styles, "font_id") : 0, styled ? Int(styles, "effect_id") : 0, colors.Length > 0 ? colors : null);
    }

    // Under _gate: what the room draws for someone (the member's own first), or null when they have nothing.
    private object? LookOf(string userId, Look? member = null) => LookDto(member is { } m && HasAny(m) ? m : _users.GetValueOrDefault(userId)?.Look);

    private static bool HasAny(Look l) => l.DecorationAsset is not null || l.NameplateSku is not null || l.NameColors is not null;

    private static object? LookDto(Look? l) => l is null || !HasAny(l) ? null : new
    {
        decoration = l.DecorationAsset is null ? null : new { asset = l.DecorationAsset, skuId = l.DecorationSku },
        nameplate = l.NameplateSku is null ? null : new { skuId = l.NameplateSku, asset = l.NameplateAsset, palette = l.NameplatePalette },
        nameStyle = l.NameColors is null ? null : new { font = l.NameFont, effect = l.NameEffect, colors = l.NameColors },
    };
}
