using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the bridge's size limit could fail:
/// - an edited snip (a few MB of base64) is dropped, so Save never answers;
/// - the larger limit leaks to every other method;
/// - a request over its limit is dropped without its id, so the page waits for a reply that never comes.
/// </summary>
public sealed class BridgeRequestSizeTests
{
    private static string Request(string method, int payload) =>
        "{\"version\":1,\"id\":\"r1\",\"method\":\"" + method + "\",\"params\":{\"png\":\"" + new string('A', payload) + "\"}}";

    [Fact]
    public void AnEditedSnipFits() =>
        Assert.True(ExoBridgeProtocol.TryParseRequest(Request("capture.saveImage", 8_000_000), out _));

    [Fact]
    public void OtherMethodsKeepTheSmallLimitAndKeepTheirId()
    {
        Assert.False(ExoBridgeProtocol.TryParseRequest(Request("app.log", 2_000_000), out var request));
        Assert.Equal("r1", request.Id);
        Assert.Equal("app.log", request.Method);
    }

    [Fact]
    public void MalformedRequestsHaveNoId()
    {
        Assert.False(ExoBridgeProtocol.TryParseRequest("{\"id\":5,\"method\":\"app.log\"}", out var request));
        Assert.Empty(request.Id);
    }
}
