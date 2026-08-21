using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Official Microsoft Store portraits for titles that have no Steam CDN poster.
/// Catalog JSON is public; images stay on store-images.s-microsoft.com.
/// </summary>
internal static class MicrosoftStoreArt
{
    private static readonly Dictionary<string, string> ProductIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["minecraft:java"] = "9NBLGGH2JHXJ",
        ["minecraft:bedrock"] = "9NBLGGH2JHXJ",
        ["roblox:player"] = "9PMF91N3LZ3M",
    };

    private static readonly string[] PortraitPurposes =
    [
        "Poster",
        "BoxArt",
        "BrandedKeyArt",
    ];

    public static string? ProductIdFor(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.Id) &&
            ProductIds.TryGetValue(game.Id.Trim(), out var mapped))
            return mapped;
        if (game.Store == StoreKind.Minecraft) return "9NBLGGH2JHXJ";
        if (game.Store == StoreKind.Roblox) return "9PMF91N3LZ3M";
        var launch = game.LaunchTarget?.Trim();
        return Storefront.LooksLikeMicrosoftStoreId(launch ?? "") ? launch : null;
    }

    public static string CatalogUrl(string productId) =>
        "https://displaycatalog.mp.microsoft.com/v7.0/products/" +
        Uri.EscapeDataString(productId) +
        "?market=US&languages=en-US";

    public static IReadOnlyList<string> PortraitUrlsFromCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Product", out var product) ||
            product.ValueKind != JsonValueKind.Object)
            return [];

        var ranked = new List<(int Rank, int Area, string Url)>();
        CollectImages(product, ranked);
        return ranked
            .OrderBy(row => row.Rank)
            .ThenByDescending(row => row.Area)
            .Select(row => row.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void CollectImages(JsonElement product, List<(int Rank, int Area, string Url)> ranked)
    {
        if (!product.TryGetProperty("LocalizedProperties", out var locales) ||
            locales.ValueKind != JsonValueKind.Array)
            return;

        foreach (var locale in locales.EnumerateArray())
        {
            if (locale.ValueKind != JsonValueKind.Object ||
                !locale.TryGetProperty("Images", out var images) ||
                images.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var image in images.EnumerateArray())
            {
                if (image.ValueKind != JsonValueKind.Object) continue;
                var purpose = image.TryGetProperty("ImagePurpose", out var purposeEl)
                    ? purposeEl.GetString()
                    : null;
                var rank = Array.FindIndex(
                    PortraitPurposes,
                    name => string.Equals(name, purpose, StringComparison.OrdinalIgnoreCase));
                if (rank < 0) continue;
                var url = AbsoluteImageUrl(image.TryGetProperty("Uri", out var uriEl) ? uriEl.GetString() : null);
                if (url is null) continue;
                var height = image.TryGetProperty("Height", out var heightEl) && heightEl.TryGetInt32(out var h) ? h : 0;
                var width = image.TryGetProperty("Width", out var widthEl) && widthEl.TryGetInt32(out var w) ? w : 0;
                ranked.Add((rank, Math.Max(0, height * width), url));
            }
        }
    }

    private static string? AbsoluteImageUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var raw = uri.Trim();
        if (raw.StartsWith("//", StringComparison.Ordinal))
            raw = "https:" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps)
            return null;
        return parsed.AbsoluteUri;
    }
}
