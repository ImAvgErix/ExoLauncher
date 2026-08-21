using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// A link says "this store account is the same human as this Exo handle". Only
/// the user can say that, so these tests hold the store to recording claims and
/// never deriving them, and to keeping them on this PC.
/// </summary>
[Collection(nameof(FriendLinksTests))]
[CollectionDefinition(nameof(FriendLinksTests), DisableParallelization = true)]
public sealed class FriendLinksTests
{
    [Fact]
    public void Add_RecordsTheClaimAndHidesThatRowFromTheStoreList()
    {
        InIsolatedDataDirectory(() =>
        {
            Assert.Null(FriendLinks.Add("pal", "epic:ab12cd34", "epic"));

            var link = Assert.Single(FriendLinks.For("pal"));
            Assert.Equal("epic:ab12cd34", link.Id);
            Assert.Equal("epic", link.Store);
            Assert.Contains("epic:ab12cd34", FriendLinks.LinkedIds());
        });
    }

    [Fact]
    public void Add_AcceptsTheExoPrefixedIdTheBridgeSends()
    {
        InIsolatedDataDirectory(() =>
        {
            Assert.Null(FriendLinks.Add("exo:pal", "9f3a1c2b", "steam"));
            Assert.Single(FriendLinks.For("pal"));
        });
    }

    [Fact]
    public void Add_RefusesToGiveOneStoreAccountToTwoPeople()
    {
        InIsolatedDataDirectory(() =>
        {
            Assert.Null(FriendLinks.Add("pal", "epic:ab12cd34", "epic"));

            var message = FriendLinks.Add("other", "epic:ab12cd34", "epic");

            Assert.NotNull(message);
            Assert.Contains("@pal", message!, StringComparison.Ordinal);
            Assert.Empty(FriendLinks.For("other"));
        });
    }

    [Fact]
    public void Add_SaysSoWhenTheSameClaimIsMadeTwice()
    {
        InIsolatedDataDirectory(() =>
        {
            Assert.Null(FriendLinks.Add("pal", "epic:ab12cd34", "epic"));
            Assert.Equal("Already linked to this person.", FriendLinks.Add("pal", "epic:ab12cd34", "epic"));
            Assert.Single(FriendLinks.For("pal"));
        });
    }

    [Theory]
    [InlineData("", "epic:ab12", "epic")]
    [InlineData("pal", "", "epic")]
    [InlineData("pal", "epic:ab12", "")]
    [InlineData("pal", "epic:ab 12", "epic")]
    [InlineData("pal", "epic:ab12", "Epic 7")]
    [InlineData("!!!", "epic:ab12", "epic")]
    public void Add_RejectsAnythingItCannotStoreCleanly(string handle, string friendId, string store)
    {
        InIsolatedDataDirectory(() =>
        {
            Assert.NotNull(FriendLinks.Add(handle, friendId, store));
            Assert.Empty(FriendLinks.LinkedIds());
        });
    }

    [Fact]
    public void Remove_PutsTheRowBackInTheStoreList()
    {
        InIsolatedDataDirectory(() =>
        {
            FriendLinks.Add("pal", "epic:ab12cd34", "epic");

            Assert.True(FriendLinks.Remove("pal", "epic:ab12cd34"));

            Assert.Empty(FriendLinks.For("pal"));
            Assert.Empty(FriendLinks.LinkedIds());
            Assert.False(FriendLinks.Remove("pal", "epic:ab12cd34"));
        });
    }

    [Fact]
    public void Forget_DropsEveryClaimForSomeoneWhoIsNoLongerOnTheList()
    {
        InIsolatedDataDirectory(() =>
        {
            FriendLinks.Add("pal", "epic:ab12cd34", "epic");
            FriendLinks.Add("pal", "9f3a1c2b", "steam");
            FriendLinks.Add("other", "epic:99887766", "epic");

            FriendLinks.Forget("pal");

            Assert.Empty(FriendLinks.For("pal"));
            Assert.Single(FriendLinks.For("other"));
        });
    }

    [Fact]
    public void Links_SurviveARestartAndStayOnThisPc()
    {
        InIsolatedDataDirectory(() =>
        {
            FriendLinks.Add("pal", "epic:ab12cd34", "epic");
            FriendLinks.Add("pal", "9f3a1c2b", "steam");

            var path = Path.Combine(PathHelper.AppDataDir, "friend-links.json");
            Assert.True(File.Exists(path));

            var saved = File.ReadAllText(path);
            Assert.Contains("epic:ab12cd34", saved, StringComparison.Ordinal);

            var reloaded = FriendLinks.All();
            Assert.Equal(["epic", "steam"], reloaded["pal"].Select(link => link.Store));
        });
    }

    [Fact]
    public void OnePerson_HoldsABoundedNumberOfAccounts()
    {
        InIsolatedDataDirectory(() =>
        {
            for (var i = 0; i < 12; i++)
                Assert.Null(FriendLinks.Add("pal", $"epic:aaaa{i:0000}", "epic"));

            Assert.NotNull(FriendLinks.Add("pal", "epic:bbbb0000", "epic"));
            Assert.Equal(12, FriendLinks.For("pal").Count);
        });
    }

    private static void InIsolatedDataDirectory(Action test)
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var root = Path.Combine(
            Path.GetTempPath(),
            "ExoLauncherFriendLinksTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, root);
            test();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
            try { Directory.Delete(root, recursive: true); }
            catch { /* temporary test cleanup is best effort */ }
        }
    }
}
