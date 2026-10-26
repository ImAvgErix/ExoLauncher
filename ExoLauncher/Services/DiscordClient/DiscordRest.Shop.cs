using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Discord's Shop and collectibles, as its web client asks for them: the catalog (collectibles-categories/v2),
/// what the account owns (collectibles-purchases), one product with its full item (a profile effect's layers),
/// equipping an owned avatar decoration, nameplate or profile effect, and buying with a card saved on the account:
/// Discord's price for it (tax included), then the purchase at exactly that price. Exo never sees card numbers;
/// a payment the bank must confirm, or any other kind of payment method, finishes on Discord's own page.
/// </summary>
internal sealed partial class DiscordRest
{
    internal Task<Reply> ShopCategoriesAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "collectibles-categories/v2?include_bundles=true&variants_return_style=2", null, ct);

    internal Task<Reply> CollectiblesPurchasesAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "users/@me/collectibles-purchases?variants_return_style=2", null, ct);

    internal Task<Reply> CollectiblesProductAsync(string skuId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"collectibles-products/{Id(skuId)}", null, ct);

    internal Task<Reply> PaymentSourcesAsync(CancellationToken ct) => SendAsync(HttpMethod.Get, "users/@me/billing/payment-sources", null, ct);

    /// <summary>What buying the SKU with this payment method costs: amount (tax included), subtotal, tax, currency.</summary>
    internal Task<Reply> PurchasePreviewAsync(string skuId, string paymentSourceId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"store/skus/{Id(skuId)}/purchase?payment_source_id={Id(paymentSourceId)}&gift=false", null, ct);

    /// <summary>
    /// Buys the SKU with a saved card at the price the preview gave (Discord refuses it if the price changed), as
    /// Discord's checkout sends it for a card: no payment token or gateway context, a fresh purchase token.
    /// </summary>
    internal Task<Reply> PurchaseAsync(string skuId, string paymentSourceId, long amount, string currency, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"store/skus/{Id(skuId)}/purchase", new JsonObject
        {
            ["gift"] = false,
            ["payment_source_id"] = Id(paymentSourceId),
            ["payment_source_token"] = null,
            ["gateway_checkout_context"] = null,
            ["expected_amount"] = amount,
            ["expected_currency"] = currency,
            ["purchase_token"] = Guid.NewGuid().ToString(),
            ["apply_wallet_balance"] = false,
        }, ct);

    // ---- Adding a card, as Discord's Add Payment Method does (Stripe holds the card; Discord and Exo never see it) ----

    /// <summary>A Stripe setup intent for a new card: its client secret, for Stripe's own card form to confirm.</summary>
    internal Task<Reply> CardSetupIntentAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "users/@me/billing/stripe/setup-intents", new JsonObject(), ct);

    /// <summary>Discord checks the billing address and answers a token that goes with the new payment method.</summary>
    internal Task<Reply> ValidateBillingAddressAsync(BillingAddress address, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "users/@me/billing/payment-sources/validate-billing-address", new JsonObject { ["billing_address"] = address.Json() }, ct);

    /// <summary>Saves the card Stripe confirmed (its payment method id) to the account, as the default.</summary>
    internal Task<Reply> AddCardAsync(string paymentMethodId, BillingAddress address, string addressToken, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "users/@me/billing/payment-sources", new JsonObject
        {
            ["payment_gateway"] = 1, // Stripe
            ["token"] = paymentMethodId,
            ["billing_address"] = address.Json(),
            ["billing_address_token"] = addressToken,
            ["default"] = true,
        }, ct);

    /// <summary>A purchase the bank must confirm: the Stripe payment intent's client secret, for Stripe's confirm (3-D Secure).</summary>
    internal Task<Reply> PaymentIntentSecretAsync(string paymentId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"users/@me/billing/stripe/payment-intents/payments/{Id(paymentId)}", null, ct);

    /// <summary>A billing address as Discord's forms take it. Every field is plain text, trimmed and bounded.</summary>
    internal sealed record BillingAddress(string Name, string Line1, string? Line2, string City, string? State, string PostalCode, string Country)
    {
        internal JsonObject Json() => new()
        {
            ["name"] = Name, ["line_1"] = Line1, ["line_2"] = Line2 ?? "", ["city"] = City, ["state"] = State ?? "",
            ["postal_code"] = PostalCode, ["country"] = Country, ["email"] = null,
        };

        /// <summary>The address from the page, or null when a needed field is missing or the country isn't two letters.</summary>
        internal static BillingAddress? From(Func<string, string?> read)
        {
            string? Field(string name, int most) => read(name)?.Trim() is { Length: > 0 } v && v.Length <= most && !v.Any(char.IsControl) ? v : null;
            var country = Field("country", 2);
            if (Field("name", 128) is not { } name || Field("line1", 256) is not { } line1 || Field("city", 128) is not { } city ||
                Field("postalCode", 32) is not { } postal || country is not { Length: 2 } || !country.All(char.IsAsciiLetterUpper)) return null;
            return new(name, line1, Field("line2", 256), city, Field("state", 128), postal, country);
        }
    }

    /// <summary>The publishable key of Discord's Stripe account, read from Discord's own page (its GLOBAL_ENV), else the one it had.</summary>
    internal static async Task<string> StripeKeyAsync(CancellationToken ct)
    {
        const string known = "pk_live_CUQtlpQUF0vufWpnpUmQvcdi";
        if (_stripeKey is { } kept) return kept;
        try
        {
            using var page = await Shared.GetAsync("https://discord.com/app", ct).ConfigureAwait(false);
            var html = await page.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var found = System.Text.RegularExpressions.Regex.Match(html, @"""STRIPE_KEY""\s*:\s*""(pk_live_[A-Za-z0-9]{10,80})""");
            return _stripeKey = found.Success ? found.Groups[1].Value : known;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return known; }
    }

    private static string? _stripeKey;
    private static readonly HttpClient Shared = ExoHttp.Create(TimeSpan.FromSeconds(10));

    /// <summary>The payment a purchase must confirm (Discord's payment_id), when it answered one.</summary>
    internal static string? PaymentId(JsonElement? body) =>
        body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("payment_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    /// <summary>Discord's codes for a payment the bank or Discord must confirm in its own checkout (3-D Secure and the like).</summary>
    internal static bool NeedsConfirmation(JsonElement? body) =>
        body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
        && code.GetInt32() is 100029 or 100047;

    /// <summary>
    /// The account's saved payment methods as the checkout lists them: the brand and last four digits only (never
    /// the billing address), when it expires, the default, and whether Exo can pay with it (a valid card).
    /// </summary>
    internal static List<PaymentChoice> PaymentChoices(JsonElement sources)
    {
        if (sources.ValueKind != JsonValueKind.Array) return [];
        var choices = new List<PaymentChoice>();
        foreach (var s in sources.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object || Text(s, "id") is not { } id) continue;
            var type = s.TryGetProperty("type", out var t) && t.TryGetInt32(out var n) ? n : 0;
            var invalid = s.TryGetProperty("invalid", out var i) && i.ValueKind == JsonValueKind.True;
            var label = type switch
            {
                1 => $"{Brand(Text(s, "brand"))} •••• {Text(s, "last_4") ?? "????"}",
                2 => "PayPal",
                _ => "Another payment method",
            };
            var month = s.TryGetProperty("expires_month", out var m) && m.TryGetInt32(out var mm) ? mm : 0;
            var year = s.TryGetProperty("expires_year", out var y) && y.TryGetInt32(out var yy) ? yy : 0;
            choices.Add(new PaymentChoice(id, label, month > 0 && year > 0 ? $"{month:00}/{year % 100:00}" : null,
                s.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.True, type == 1 && !invalid));
        }
        return choices;
    }

    internal sealed record PaymentChoice(string Id, string Label, string? Expires, bool Default, bool Usable);

    private static readonly HashSet<string> WholeUnitCurrencies = new(StringComparer.OrdinalIgnoreCase) { "jpy", "krw", "vnd", "clp", "isk", "huf", "twd", "ugx", "xaf", "xof", "xpf" };

    /// <summary>A charge in words for the confirmation: "9.99 USD". Discord's amounts are in the currency's smallest unit.</summary>
    internal static string PriceText(long amount, string currency) =>
        (WholeUnitCurrencies.Contains(currency) ? amount.ToString("N0", CultureInfo.InvariantCulture) : (amount / 100m).ToString("N2", CultureInfo.InvariantCulture))
        + " " + currency.ToUpperInvariant();

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Brand(string? brand) => brand switch
    {
        null or "" => "Card",
        "amex" => "American Express",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(brand),
    };

    /// <summary>Puts on an owned decoration or nameplate (users/@me); null takes it off.</summary>
    internal Task<Reply> EquipAsync(string kind, string? skuId, CancellationToken ct)
    {
        JsonNode? sku = skuId is null ? null : Id(skuId);
        return SendAsync(HttpMethod.Patch, "users/@me", new JsonObject { [kind == "nameplate" ? "nameplate_sku_id" : "avatar_decoration_sku_id"] = sku }, ct);
    }

    /// <summary>
    /// The profile's collectibles as Discord's Profiles saves them: every one it keeps (profile effect, frame) by SKU,
    /// collectibles_sku_ids on users/@me/profile. The whole list goes each time; one left out comes off.
    /// </summary>
    internal Task<Reply> ProfileCollectiblesAsync(IEnumerable<string> skuIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, "users/@me/profile", new JsonObject { ["collectibles_sku_ids"] = new JsonArray([.. skuIds.Select(s => (JsonNode)Id(s))]) }, ct);

    /// <summary>The profile's collectibles (sku_id and type: 1 profile effect, 3 profile frame) from a profile reply.</summary>
    internal static List<(string Sku, int Type)> ProfileCollectibles(JsonElement profile)
    {
        var list = new List<(string, int)>();
        if (profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("user_profile", out var about) && about.ValueKind == JsonValueKind.Object &&
            about.TryGetProperty("collectibles", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("sku_id", out var sku) && sku.ValueKind == JsonValueKind.String &&
                    item.TryGetProperty("type", out var type) && type.TryGetInt32(out var t))
                    list.Add((sku.GetString()!, t));
        return list;
    }

    internal const int ProfileEffectType = 1;
}
