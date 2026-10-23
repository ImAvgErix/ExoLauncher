using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Adapters;

/// <summary>
/// A gift waiting on a store for this account. <see cref="Id"/> is the store's own gift id and <see cref="SenderId"/>
/// Steam's sender SteamID64, which its decline call needs; neither reaches the page beyond the id.
/// </summary>
internal sealed record StoreGift(string Store, string Id, string Title, string? Image, string? Sender, string? Message, string? SenderId = null);

/// <summary>
/// Reads the gifts each store's own page shows. Steam: the pending gifts on store.steampowered.com/gifts (one
/// <c>#pending_gift_&lt;id&gt;</c> row each, accepted to the library by its page's <c>UnpackGift</c>). Epic: the
/// received gifts egs-platform-service lists for store.epicgames.com/gifts, still new or opened, delivered and not
/// expired, with their offer's title and key art. Accept and decline answers are read the way each page reads them.
/// </summary>
internal static partial class StoreGiftParser
{
    private const int TitleMax = 200;
    private const int NameMax = 100;
    private const int MessageMax = 1000;

    [GeneratedRegex(@"<div\b[^>]*\bid=""pending_gift_(\d{1,20})""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SteamRow();

    [GeneratedRegex(@"\bUnpackGift\(\s*'(\d{1,20})'\s*\)")]
    private static partial Regex SteamAccept();

    [GeneratedRegex(@"\bShowDeclineGiftOptions\(\s*'(\d{1,20})'\s*,\s*'(\d{17})'\s*\)")]
    private static partial Regex SteamDecline();

    [GeneratedRegex(@"class=""gift_name""[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SteamName();

    [GeneratedRegex(@"class=""image_cont""[^>]*>\s*<img\b[^>]*\bsrc=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SteamImage();

    // The gift_controls line "From <a href=…/profiles/…>name</a> on Oct 22" (the page is asked for in English).
    [GeneratedRegex(@"\bFrom\s*<a\b[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex SteamFrom();

    [GeneratedRegex(@"class=""signature_line"".*?<p>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SteamSignature();

    // The sender's own words; Steam's gift style sheet names the block gift_msg_body.
    [GeneratedRegex(@"class=""[^""]*\bgift_msg_body\b[^""]*""[^>]*>(.*?)</(?:p|div)>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SteamMessage();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex EpicIdShape();

    /// <summary>True when <paramref name="id"/> is a gift id this store's calls may carry (digits on Steam, a hex id on Epic).</summary>
    internal static bool IsGiftId(string store, string? id) => id is not null && store switch
    {
        "steam" => id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit),
        "epic" => EpicIdShape().IsMatch(id),
        _ => false,
    };

    /// <summary>Steam's pending gift page: every game gift it offers to add to the library. Gift cards are left to Steam.</summary>
    internal static IReadOnlyList<StoreGift> ParseSteam(string? html)
    {
        if (string.IsNullOrEmpty(html)) return [];
        var rows = SteamRow().Matches(html);
        var gifts = new List<StoreGift>();
        for (var i = 0; i < rows.Count; i++)
        {
            var id = rows[i].Groups[1].Value;
            var end = i + 1 < rows.Count ? rows[i + 1].Index : html.Length;
            var block = html[rows[i].Index..end];
            if (block.Contains("pending_giftcard_leftcol", StringComparison.Ordinal)) continue;
            if (SteamAccept().Match(block) is not { Success: true } accept || accept.Groups[1].Value != id) continue;
            var title = Text(SteamName().Match(block), TitleMax);
            if (title is null) continue;
            var decline = SteamDecline().Match(block);
            gifts.Add(new StoreGift(
                "steam",
                id,
                title,
                Https(SteamImage().Match(block) is { Success: true } image ? WebUtility.HtmlDecode(image.Groups[1].Value) : null),
                Text(SteamFrom().Match(block), NameMax) ?? Text(SteamSignature().Match(block), NameMax),
                Text(SteamMessage().Match(block), MessageMax),
                decline.Success && decline.Groups[1].Value == id ? decline.Groups[2].Value : null));
        }
        return gifts;
    }

    /// <summary>
    /// The answer to Steam's unpack (add to library) or decline call: <c>success</c> 1 is done, 24 is a gift that
    /// needs its base game first, and anything else carries Steam's own error line when it has one.
    /// </summary>
    internal static (bool Ok, string? Message) ParseSteamAnswer(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (false, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (false, null);
            var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) ? n : 0;
            if (success == 1) return (true, null);
            if (success == 24) return (false, "This needs its base game in your library first.");
            return (false, root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? Clean(e.GetString(), TitleMax) : null);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Epic's received gifts as Exo's page reads them: <c>{"data":[gift…]}</c>, each gift's <c>offer</c> carrying the
    /// catalog's <c>title</c> and <c>keyImages</c>. Kept: new or opened, delivered by <paramref name="now"/>, not expired.
    /// </summary>
    internal static IReadOnlyList<StoreGift> ParseEpic(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
            var gifts = new List<StoreGift>();
            foreach (var gift in data.EnumerateArray())
            {
                if (gift.ValueKind != JsonValueKind.Object) continue;
                var id = String(gift, "id");
                var status = String(gift, "status");
                if (!IsGiftId("epic", id) ||
                    !(string.Equals(status, "NEW", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "OPENED", StringComparison.OrdinalIgnoreCase)) ||
                    Date(gift, "deliveryDate") > now ||
                    Date(gift, "expireDate") is { } expires && expires <= now)
                    continue;
                var offer = gift.TryGetProperty("offer", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
                gifts.Add(new StoreGift(
                    "epic",
                    id!,
                    Clean(offer.ValueKind == JsonValueKind.Object ? String(offer, "title") : null, TitleMax) ?? "Gift",
                    offer.ValueKind == JsonValueKind.Object ? EpicArt(offer) : null,
                    Clean(String(gift, "senderDisplayName"), NameMax),
                    Clean(String(gift, "message"), MessageMax)));
            }
            return gifts;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The answer to Epic's status change: <paramref name="status"/> 2xx is done; otherwise Epic's
    /// <c>errorMessage</c>, when it is a readable line rather than a code.
    /// </summary>
    internal static (bool Ok, string? Message) ParseEpicAnswer(int status, string? body)
    {
        if (status is >= 200 and < 300) return (true, null);
        if (string.IsNullOrWhiteSpace(body)) return (false, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.ValueKind == JsonValueKind.Object ? Clean(String(doc.RootElement, "errorMessage"), TitleMax) : null;
            return (false, message is not null && message.Contains(' ') ? message : null);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    // Wide art first: the Library shows gifts as landscape cards.
    private static readonly string[] EpicArtOrder = ["OfferImageWide", "DieselStoreFrontWide", "featuredMedia", "Thumbnail", "OfferImageTall", "DieselStoreFrontTall"];

    private static string? EpicArt(JsonElement offer)
    {
        if (!offer.TryGetProperty("keyImages", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        string? best = null;
        var rank = int.MaxValue;
        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object || Https(String(image, "url")) is not { } url) continue;
            var at = Array.IndexOf(EpicArtOrder, String(image, "type"));
            if (at < 0) at = EpicArtOrder.Length;
            if (at < rank) (best, rank) = (url, at);
        }
        return best;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        DateTimeOffset.TryParse(String(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    private static string? Text(Match match, int max) => match.Success ? Clean(Tag().Replace(match.Groups[1].Value, " "), max, decode: true) : null;

    private static string? Clean(string? text, int max, bool decode = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var plain = Space().Replace(decode ? WebUtility.HtmlDecode(text) : text, " ").Trim();
        if (plain.Length == 0) return null;
        return plain.Length > max ? plain[..max].TrimEnd() + "…" : plain;
    }

    private static string? Https(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;
}
