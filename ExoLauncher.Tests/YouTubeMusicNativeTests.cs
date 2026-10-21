using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the YouTube Music room's ids can let the wrong thing into the page script, or keep the right thing out:
/// - a browse page's own params (base64 with %3D, + and /) are refused, so Explore, moods and discographies never open;
/// - a quote, angle bracket or space slips through as an id;
/// - a queue move's target (before|song|row) is refused, or accepted for an action that is not a move;
/// - a row id carries something other than the page's index or setVideoId.
/// </summary>
public sealed class YouTubeMusicNativeTests
{
    [Theory]
    [InlineData("ytm:track:FGBhQbmPwH8")]
    [InlineData("ytm:browse:FEmusic_explore")]
    [InlineData("ytm:browse:FEmusic_moods_and_genres_category:ggMPOg1uX1JOQWZFeDByc2Jm")]
    [InlineData("ytm:browse:MPADUCRr1xG_2WIDs18a6cIiCxeA:ggMIegYIARoCAQI%3D")]
    [InlineData("ytm:browse:UCRr1xG_2WIDs18a6cIiCxeA:6gPgAUdx+VUJX/cFlC==")]
    [InlineData("ytm:filter:community_playlists")]
    public void PageIdsPass(string uri) => Assert.True(YouTubeMusicNative.IsUri(uri));

    [Theory]
    [InlineData("ytm:track:a'b")]
    [InlineData("ytm:track:<x>")]
    [InlineData("ytm:track:a b")]
    [InlineData("spotify:track:abc")]
    [InlineData("ytm:")]
    public void OtherTextIsRefused(string uri) => Assert.False(YouTubeMusicNative.IsUri(uri));

    [Fact]
    public void MovesNameTheirTarget()
    {
        Assert.True(YouTubeMusicNative.IsContext("queueMove", "before|ytm:track:FGBhQbmPwH8|51"));
        // A playlist move names the playlist; the row it goes before travels in the typed words.
        Assert.True(YouTubeMusicNative.IsContext("movePlaylistTrack", "ytm:playlist:VLPLx"));
        Assert.False(YouTubeMusicNative.IsContext("movePlaylistTrack", "after||"));
        Assert.True(YouTubeMusicNative.IsContext("play", "ytm:list:RDAMVMFGBhQbmPwH8"));
        Assert.False(YouTubeMusicNative.IsContext("play", "before|ytm:track:FGBhQbmPwH8|51"));
        Assert.False(YouTubeMusicNative.IsContext("queueMove", "before|ytm:track:x|5'1"));
    }

    [Fact]
    public void RowIdsAreThePagesOwn()
    {
        Assert.True(YouTubeMusicNative.IsUid("51"));
        Assert.True(YouTubeMusicNative.IsUid("56B44F6D10557CC6"));
        Assert.True(YouTubeMusicNative.IsUid(null));
        Assert.False(YouTubeMusicNative.IsUid("5;1"));
    }
}
