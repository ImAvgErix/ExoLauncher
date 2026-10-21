using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways handing a product key to its store can fail:
/// - the key is dropped from the Steam or GOG address, so the page opens empty and the user retypes it;
/// - Steam's key page opens in the browser instead of the signed-in Steam client;
/// - something typed into the key field escapes the address (a query or path of its own);
/// - a key with spaces or lowercase letters reaches the store unnormalized.
/// </summary>
public sealed class GameKeyActivationTests
{
    [Fact]
    public void Steam_opens_its_key_page_in_the_client_with_the_key()
    {
        var key = GameKeyActivation.Normalize(" abcde fghij-klmno ");
        Assert.Equal("ABCDE-FGHIJ-KLMNO", key);
        Assert.Equal("steam://openurl/https://store.steampowered.com/account/registerkey?key=ABCDE-FGHIJ-KLMNO",
            GameKeyActivation.RedeemUrl("steam", key));
    }

    [Fact]
    public void Gog_takes_the_key_in_its_path_and_Epic_takes_none()
    {
        Assert.Equal("https://www.gog.com/redeem/GOG1234567890ABCD", GameKeyActivation.RedeemUrl("gog", "GOG1234567890ABCD"));
        Assert.Equal("https://store.epicgames.com/redeem", GameKeyActivation.RedeemUrl("epic", "EPIC-1234"));
    }

    [Fact]
    public void Nothing_typed_escapes_the_address()
    {
        var key = GameKeyActivation.Normalize("ab&c/../d?e=#f");
        Assert.Matches("^[A-Z0-9-]*$", key);
        Assert.EndsWith("/redeem/" + key, GameKeyActivation.RedeemUrl("gog", key));
    }
}
