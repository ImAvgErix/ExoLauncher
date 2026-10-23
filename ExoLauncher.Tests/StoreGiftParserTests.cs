using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways reading a store's waiting gifts could fail:
/// - Steam's page template, its hidden accepted/declined notes or a gift card show up as a gift to accept;
/// - a gift's name, picture, sender or message is taken from the next gift's row, or left HTML-encoded;
/// - the sender id Steam's decline needs is missed, or one from another row is used;
/// - a gift id that could break out of the page call (quotes, slashes) is accepted;
/// - an Epic gift already accepted, declined, expired or scheduled for later is offered again;
/// - Epic's portrait art is shown when a wide one exists, or an http:// picture gets through;
/// - Steam's "needs the base game" answer or Epic's error codes reach the page as raw codes.
/// </summary>
public sealed class StoreGiftParserTests
{
    // The shape of store.steampowered.com/gifts?l=english (October 2026), names and ids replaced.
    private const string SteamPage = """
        <div class="pending_gifts giftpopup">
          <div class="pending_gift pending_gift_template_0">
            <div id="unpacked_gift_111" style="display: none;"><b>Space Game</b> has been added to your account.</div>
            <div id="decline_gift_prompt_111" style="display: none;"><h2>Space Game</h2></div>
            <div id="pending_gift_111" class="gift_row">
              <div class="pending_gift_leftcol ">
                <div class="image_cont">
                  <img src="https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/10/capsule_616x353.jpg?t=1&amp;x=2">
                </div>
                <div class="gift_description_ctn">
                  <div class="gift_name">Space &amp; Time&#174;: Game</div>
                  <div class="gift_descriptions">A long description that is not the message.</div>
                </div>
              </div>
              <div class="pending_gift_rightcol">
                <div class="content gift_message">
                  <p class="gift_msg_sentiment">A gift from:</p>
                  <div class="signature_line"><div class="playerAvatar"><a href="https://steamcommunity.com//profiles/76561190000000001"><img src="x.jpg"></a></div><p>Sig Name</p></div>
                </div>
                <div class="content gift_controls">
                  <p>From <a href="https://steamcommunity.com//profiles/76561190000000001" data-miniprofile="1">Friend One</a> on Oct 22</p>
                  <div id="gift111_step_init">
                    <div role="button" class="btn_medium btn_green_steamui" onclick="UnpackGift( '111' )"><span>Accept gift</span></div>
                    <div role="button" class="btn_medium btn_grey_steamui" onclick="ShowDeclineGiftOptions( '111', '76561190000000001' );"><span>Decline gift</span></div>
                  </div>
                </div>
              </div>
            </div>
          </div>
          <div class="pending_gift pending_gift_template_0">
            <div id="pending_gift_222" class="gift_row">
              <div class="pending_gift_leftcol ">
                <div class="image_cont"><img src="http://insecure.example/b.jpg"></div>
                <div class="gift_description_ctn"><div class="gift_name">Second Game</div></div>
              </div>
              <div class="pending_gift_rightcol">
                <div class="content gift_message">
                  <p class="gift_msg_body">Happy   birthday!<br>Have &lt;fun&gt;</p>
                  <p class="gift_msg_sentiment">Best wishes,</p>
                  <div class="signature_line"><p>Pal</p></div>
                </div>
                <div class="content gift_controls">
                  <div id="gift222_step_init">
                    <div onclick="UnpackGift( '222' )"><span>Accept gift</span></div>
                    <div onclick="ShowDeclineGiftOptions( '222', '76561190000000002' );"><span>Decline gift</span></div>
                  </div>
                </div>
              </div>
            </div>
          </div>
          <div class="pending_gift pending_gift_template_0">
            <div id="pending_gift_333" class="gift_row">
              <div class="pending_giftcard_leftcol">$20.00 Steam Wallet</div>
              <div onclick="AcceptRejectGiftCard( '333', 1 )">Redeem</div>
            </div>
          </div>
        </div>
        """;

    [Fact]
    public void ReadsEachSteamGiftFromItsOwnRow()
    {
        var gifts = StoreGiftParser.ParseSteam(SteamPage);

        Assert.Equal(2, gifts.Count);
        var first = gifts[0];
        Assert.Equal(new StoreGift("steam", "111", "Space & Time®: Game",
            "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/10/capsule_616x353.jpg?t=1&x=2",
            "Friend One", null, "76561190000000001"), first);

        var second = gifts[1];
        Assert.Equal("222", second.Id);
        Assert.Equal("Second Game", second.Title);
        Assert.Null(second.Image);
        Assert.Equal("Pal", second.Sender);
        Assert.Equal("Happy birthday! Have <fun>", second.Message);
        Assert.Equal("76561190000000002", second.SenderId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html><body>You have no pending gifts.</body></html>")]
    [InlineData("""<div id="pending_gift_9" class="gift_row"><div class="gift_name">No buttons</div></div>""")]
    [InlineData("""<div id="pending_gift_9" class="gift_row"><div class="gift_name">Other</div><div onclick="UnpackGift( '8' )"></div></div>""")]
    public void FindsNoSteamGiftWithoutAnAcceptForThatRow(string? html) => Assert.Empty(StoreGiftParser.ParseSteam(html));

    [Theory]
    [InlineData("""{"success":1}""", true, null)]
    [InlineData("""{"success":24}""", false, "This needs its base game in your library first.")]
    [InlineData("""{"success":2,"error":"  The gift   was already redeemed. "}""", false, "The gift was already redeemed.")]
    [InlineData("""{"success":8}""", false, null)]
    [InlineData("<html>Sign in</html>", false, null)]
    [InlineData(null, false, null)]
    public void ReadsSteamsAnswer(string? body, bool ok, string? message) =>
        Assert.Equal((ok, message), StoreGiftParser.ParseSteamAnswer(body));

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private const string EpicList = """
        {"data":[
          {"id":"a1b2c3","status":"NEW","senderDisplayName":"Epic Friend","message":"Enjoy!","deliveryDate":"2026-10-21T10:00:00.000Z","expireDate":"2026-11-04T10:00:00.000Z",
           "offer":{"offerId":"o1","sandboxId":"s1","title":"Wide Game","keyImages":[
             {"type":"OfferImageTall","url":"https://cdn1.epicgames.com/offer/s1/tall.jpg"},
             {"type":"OfferImageWide","url":"https://cdn1.epicgames.com/offer/s1/wide.jpg"}]}},
          {"id":"d4e5f6","status":"OPENED","senderDisplayName":"Other","deliveryDate":"2026-10-22T11:00:00.000Z",
           "offer":{"offerId":"o2","sandboxId":"s2","keyImages":[{"type":"OfferImageTall","url":"http://cdn1.epicgames.com/x.jpg"},{"type":"Thumbnail","url":"https://cdn1.epicgames.com/thumb.jpg"}]}},
          {"id":"later1","status":"NEW","deliveryDate":"2026-10-25T00:00:00.000Z","offer":{"title":"Scheduled"}},
          {"id":"old1","status":"NEW","deliveryDate":"2026-10-01T00:00:00.000Z","expireDate":"2026-10-15T00:00:00.000Z","offer":{"title":"Expired"}},
          {"id":"acc1","status":"ACCEPTED","offer":{"title":"Taken"}},
          {"id":"dec1","status":"DECLINED","offer":{"title":"Refused"}},
          {"id":"bad'id","status":"NEW","offer":{"title":"Injected"}},
          {"status":"NEW","offer":{"title":"No id"}}
        ],"paging":{"count":50,"start":0,"total":8}}
        """;

    [Fact]
    public void KeepsOnlyEpicGiftsStillWaiting()
    {
        var gifts = StoreGiftParser.ParseEpic(EpicList, Now);

        Assert.Equal(2, gifts.Count);
        Assert.Equal(new StoreGift("epic", "a1b2c3", "Wide Game", "https://cdn1.epicgames.com/offer/s1/wide.jpg", "Epic Friend", "Enjoy!"), gifts[0]);
        Assert.Equal(new StoreGift("epic", "d4e5f6", "Gift", "https://cdn1.epicgames.com/thumb.jpg", "Other", null), gifts[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":[],"paging":{"total":0}}""")]
    public void FindsNoEpicGiftInAnEmptyOrBrokenAnswer(string? json) => Assert.Empty(StoreGiftParser.ParseEpic(json, Now));

    [Theory]
    [InlineData(200, null, true, null)]
    [InlineData(204, "", true, null)]
    [InlineData(400, """{"errorCode":"errors.com.epicgames.gifting.already_owned","errorMessage":"You already own this item."}""", false, "You already own this item.")]
    [InlineData(400, """{"errorMessage":"GIFT_EXPIRED"}""", false, null)]
    [InlineData(403, "<html>Just a moment...</html>", false, null)]
    [InlineData(0, null, false, null)]
    public void ReadsEpicsAnswer(int status, string? body, bool ok, string? message) =>
        Assert.Equal((ok, message), StoreGiftParser.ParseEpicAnswer(status, body));

    [Theory]
    [InlineData("steam", "237160366753599703", true)]
    [InlineData("steam", "12a", false)]
    [InlineData("steam", "", false)]
    [InlineData("steam", "123456789012345678901", false)]
    [InlineData("epic", "9f1c2d3e4a5b6c7d8e9f", true)]
    [InlineData("epic", "a'b", false)]
    [InlineData("epic", "a/b", false)]
    [InlineData("gog", "123", false)]
    [InlineData("epic", null, false)]
    public void AcceptsOnlyIdsThatStayInsideThePageCall(string store, string? id, bool valid) =>
        Assert.Equal(valid, StoreGiftParser.IsGiftId(store, id));
}
