using System.Net;
using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Reading Discord's "it's really you" ask (401, code 60003, an mfa object), which fails silently when wrong: any other
/// 401 (a signed-out token, a 60003 without a ticket) taken for a check that can never finish; a real check missed, so
/// the owner's switch only says Discord refused; a way Exo can't do (a passkey) offered, or the same way twice; Discord's
/// order lost, so the dialog opens on a different way than Discord's.
/// </summary>
public sealed class DiscordMfaTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ReadsTheTicketAndWaysInDiscordsOrder()
    {
        var ask = DiscordRest.ReadMfa(HttpStatusCode.Unauthorized, Json("""
            { "code": 60003, "message": "Two factor is required for this operation",
              "mfa": { "ticket": "t.abc", "methods": [ { "type": "password" }, { "type": "totp" }, { "type": "backup" } ] } }
            """));
        Assert.NotNull(ask);
        Assert.Equal("t.abc", ask.Ticket);
        Assert.Equal(["password", "totp", "backup"], ask.Methods);
    }

    [Fact]
    public void LeavesOutWaysExoCannotDoAndRepeats()
    {
        var ask = DiscordRest.ReadMfa(HttpStatusCode.Unauthorized, Json("""
            { "code": 60003, "mfa": { "ticket": "t", "methods": [ { "type": "webauthn", "challenge": "{}" }, { "type": "totp" }, { "type": "totp" }, { "type": "sms" }, { "type": "future" } ] } }
            """));
        Assert.Equal(["totp", "sms"], ask!.Methods);
    }

    [Fact]
    public void APasskeyOnlyCheckHasNoWays()
    {
        var ask = DiscordRest.ReadMfa(HttpStatusCode.Unauthorized, Json("""{ "code": 60003, "mfa": { "ticket": "t", "methods": [ { "type": "webauthn" } ] } }"""));
        Assert.NotNull(ask);
        Assert.Empty(ask.Methods);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": 0, "message": "401: Unauthorized" }""")]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": 60003, "message": "Two factor is required for this operation" }""")]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": 60003, "mfa": { "methods": [ { "type": "totp" } ] } }""")]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": 60003, "mfa": { "ticket": "", "methods": [ { "type": "totp" } ] } }""")]
    [InlineData(HttpStatusCode.Forbidden, """{ "code": 60003, "mfa": { "ticket": "t", "methods": [ { "type": "totp" } ] } }""")]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": 60008, "mfa": { "ticket": "t", "methods": [ { "type": "totp" } ] } }""")]
    [InlineData(HttpStatusCode.Unauthorized, """[]""")]
    public void AnythingElseIsNoCheck(HttpStatusCode status, string body) =>
        Assert.Null(DiscordRest.ReadMfa(status, Json(body)));

    [Fact]
    public void NoBodyIsNoCheck() => Assert.Null(DiscordRest.ReadMfa(HttpStatusCode.Unauthorized, null));
}
