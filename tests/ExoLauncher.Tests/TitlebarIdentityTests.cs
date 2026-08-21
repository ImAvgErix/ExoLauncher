using ExoLauncher.Ui;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// The titlebar avatar must paint the user's pick, not the fallback initial,
/// when the profile payload names a library game. A boot read that lands before
/// the library scan used to look like "no avatar" and stay that way.
/// </summary>
public sealed class TitlebarIdentityTests
{
    [Fact]
    public void PopulatedProfile_SelectsAvatarArtNotInitial()
    {
        var pick = TitlebarIdentity.Pick(
            avatarImageUrl: null,
            avatarGameId: "steam:1620730",
            name: "Erix",
            libraryGameIds: ["steam:1620730"]);

        Assert.Equal("game", pick.Kind);
        Assert.Equal("steam:1620730", pick.GameId);
        Assert.NotEqual("initial", pick.Kind);
    }

    [Fact]
    public void UploadedPicture_OutranksTheLibraryCover()
    {
        var pick = TitlebarIdentity.Pick(
            avatarImageUrl: "https://covers.exo-launcher.local/avatar.webp",
            avatarGameId: "steam:1620730",
            name: "Erix",
            libraryGameIds: ["steam:1620730"]);

        Assert.Equal("image", pick.Kind);
        Assert.Equal("https://covers.exo-launcher.local/avatar.webp", pick.ImageUrl);
    }

    [Fact]
    public void AvatarIdWithoutLibraryMatch_FallsBackToInitial()
    {
        var pick = TitlebarIdentity.Pick(null, "steam:1620730", "Erix", []);
        Assert.Equal("initial", pick.Kind);
        Assert.Equal("E", pick.Initial);
    }

    [Fact]
    public void StrippedProfile_BeforeLibraryReady_KeepsPreviousArt()
    {
        var next = TitlebarIdentity.Apply(
            ok: true,
            incomingName: "Erix",
            incomingGameId: null,
            incomingImageUrl: null,
            currentGameId: "steam:1620730",
            currentImageUrl: null,
            libraryReady: false);

        Assert.Equal("steam:1620730", next.AvatarGameId);
        Assert.False(next.Cacheable);
    }

    [Fact]
    public void StrippedProfile_AfterLibraryReady_ClearsAvatar()
    {
        var next = TitlebarIdentity.Apply(
            ok: true,
            incomingName: "Erix",
            incomingGameId: null,
            incomingImageUrl: null,
            currentGameId: "steam:1620730",
            currentImageUrl: null,
            libraryReady: true);

        Assert.Null(next.AvatarGameId);
        Assert.True(next.Cacheable);
    }

    [Fact]
    public void Coalesce_KeepsSavedIdWhenLibraryPeekDroppedIt()
    {
        Assert.Equal("steam:1620730", TitlebarIdentity.CoalesceSavedAvatarGameId(null, "steam:1620730"));
        Assert.Equal("steam:99", TitlebarIdentity.CoalesceSavedAvatarGameId("steam:99", "steam:1620730"));
        Assert.Null(TitlebarIdentity.CoalesceSavedAvatarGameId(null, null));
        Assert.Null(TitlebarIdentity.CoalesceSavedAvatarGameId("  ", "  "));
    }

    [Fact]
    public void Titlebar_AppliesIdentityWithoutCachingAStrippedProfile()
    {
        var app = ReadRepoFile("ui", "src", "components", "LauncherApp.tsx");
        var identity = ReadRepoFile("ui", "src", "lib", "titlebarIdentity.ts");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");

        Assert.Contains("applyTitlebarIdentity", app, StringComparison.Ordinal);
        Assert.Contains("the game they chose as avatar", app, StringComparison.Ordinal);
        Assert.Contains("selfAvatarImage", app, StringComparison.Ordinal);
        Assert.Contains("export function applyTitlebarIdentity", identity, StringComparison.Ordinal);
        Assert.Contains("TitlebarIdentity.CoalesceSavedAvatarGameId", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("onHostEvent('library.updated', load)", app, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
}
