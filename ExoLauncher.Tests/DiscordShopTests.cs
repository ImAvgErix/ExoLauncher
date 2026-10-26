using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Buying in Discord's Shop with a saved card. Ways it could fail silently: the billing address or more than the
/// last four digits reach the page; Exo offers to pay with PayPal, a bank method or an expired (invalid) card,
/// which need Discord's own checkout; a payment the bank must confirm reads as a plain failure, so nothing tells
/// the user to finish it in Discord.
/// </summary>
public sealed class DiscordShopTests
{
    private static List<DiscordRest.PaymentChoice> Choices(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return DiscordRest.PaymentChoices(doc.RootElement);
    }

    [Fact]
    public void CardShowsBrandAndLastFourOnly()
    {
        var card = Assert.Single(Choices("""
        [ { "id": "1", "type": 1, "brand": "visa", "last_4": "4242", "expires_month": 4, "expires_year": 2028, "default": true,
            "billing_address": { "name": "Someone", "line_1": "1 Street", "postal_code": "12345" } } ]
        """));
        Assert.Equal("Visa •••• 4242", card.Label);
        Assert.Equal("04/28", card.Expires);
        Assert.True(card.Default);
        Assert.True(card.Usable);
        var serialized = JsonSerializer.Serialize(card);
        Assert.DoesNotContain("Street", serialized);
        Assert.DoesNotContain("12345", serialized);
    }

    [Fact]
    public void OnlyValidCardsArePayableHere()
    {
        var choices = Choices("""
        [ { "id": "1", "type": 1, "brand": "mastercard", "last_4": "1111", "invalid": true },
          { "id": "2", "type": 2, "email": "someone@example.com" },
          { "id": "3", "type": 6 } ]
        """);
        Assert.All(choices, c => Assert.False(c.Usable));
        Assert.Equal("PayPal", choices[1].Label);
        Assert.DoesNotContain("example.com", JsonSerializer.Serialize(choices));
    }

    [Theory]
    [InlineData("""{ "code": 100029, "message": "Authentication required" }""", true)]
    [InlineData("""{ "code": 100047, "message": "Confirmation required" }""", true)]
    [InlineData("""{ "code": 100008, "message": "Invalid payment source" }""", false)]
    public void BankConfirmationIsRecognized(string json, bool confirm)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(confirm, DiscordRest.NeedsConfirmation(doc.RootElement));
    }

    // The native confirmation states the charge in words the user can check against their card.
    [Theory]
    [InlineData(999L, "usd", "9.99 USD")]
    [InlineData(150000L, "eur", "1,500.00 EUR")]
    [InlineData(1200L, "JPY", "1,200 JPY")]
    [InlineData(5L, "gbp", "0.05 GBP")]
    public void TheChargeIsStatedInTheCurrencysOwnUnit(long amount, string currency, string text) =>
        Assert.Equal(text, DiscordRest.PriceText(amount, currency));
}
