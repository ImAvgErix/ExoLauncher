using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Copy Image and Save Image could go wrong without anyone seeing it:
/// - the host fetches a page-supplied address that is not Discord's (any site, plain http, another port);
/// - a saved picture gets a name Windows refuses or that escapes the Downloads folder ("..\", "/", ":");
/// - the media proxy's format= is ignored, so a WebP is saved as .png (or the reverse);
/// - a name with no extension is saved without one and opens in nothing.
/// </summary>
public sealed class DiscordImagesTests
{
    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/a.png?ex=1", true)]
    [InlineData("https://media.discordapp.net/attachments/1/2/a.png", true)]
    [InlineData("https://images-ext-1.discordapp.net/external/x/https/pbs.twimg.com/media/a.jpg", true)]
    [InlineData("http://cdn.discordapp.com/attachments/1/2/a.png", false)]
    [InlineData("https://cdn.discordapp.com:8443/a.png", false)]
    [InlineData("https://discordapp.net.evil.example/a.png", false)]
    [InlineData("https://evil.example/a.png", false)]
    [InlineData("https://discord.com/api/v9/users/@me", false)]
    public void OnlyDiscordsOwnPicturesAreFetched(string url, bool expected) =>
        Assert.Equal(expected, DiscordImages.IsImageUrl(new Uri(url)));

    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/SLX_shot1_FINAL.png?ex=a&is=b&hm=c", null, "SLX_shot1_FINAL.png")]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/My%20Pic.jpg", null, "My Pic.jpg")]
    [InlineData("https://media.discordapp.net/attachments/1/2/shot.png?format=webp&width=100", null, "shot.webp")]
    [InlineData("https://cdn.discordapp.com/emojis/123456.webp?size=48", null, "123456.webp")]
    [InlineData("https://cdn.discordapp.com/avatars/1/abc", "image/jpeg", "abc.jpg")]
    [InlineData("https://cdn.discordapp.com/avatars/1/abc", null, "abc.png")]
    [InlineData("https://images-ext-1.discordapp.net/external/x/https/pbs.twimg.com/media/G8E.jpg:large", null, "G8E.jpg")]
    [InlineData("https://images-ext-1.discordapp.net/external/x/https/pbs.twimg.com/media/G8E@png", null, "G8E.png")]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/a%3Cb%3E%7C%3F.png", null, "a_b___.png")]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/..%5C..%5Cx.png", null, ".._.._x.png")]
    public void SavedNamesFollowDiscordsApp(string url, string? contentType, string expected) =>
        Assert.Equal(expected, DiscordImages.FileName(new Uri(url), contentType));
}
