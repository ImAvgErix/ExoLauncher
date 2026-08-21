using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Epic hands Exo three things over HTTP and no more: who the user is friends
/// with, what those accounts are called, and when Epic last saw them. These
/// tests hold the parsers to exactly the payloads that were verified against a
/// live account, and hold the adapter to inventing nothing when a payload
/// changes shape.
/// </summary>
public sealed class EpicFriendsTests
{
    private const string AccountA = "9974fdd51134460196b6f453d72c24ee";
    private const string AccountB = "34fa1800b57a47d29089f484745fa817";

    [Fact]
    public void FriendIds_ComeFromTheSummaryPayloadEpicActuallyReturns()
    {
        var json = $$"""
        {
          "friends": [
            { "accountId": "{{AccountA}}", "groups": [], "mutual": 3, "favorite": false,
              "created": "2021-04-02T18:22:11.000Z" },
            { "accountId": "{{AccountB}}", "groups": [], "mutual": 0, "favorite": false,
              "created": "2020-01-09T10:02:00.000Z" }
          ],
          "incoming": [], "outgoing": [], "suggested": [], "blocklist": [],
          "settings": { "acceptInvites": "public", "mutualPrivacy": "ALL" },
          "limitsReached": { "incoming": false, "outgoing": false, "accepted": false }
        }
        """;

        Assert.True(EpicFriends.TryParseFriendIds(json, out var ids));
        Assert.Equal(new[] { AccountA, AccountB }, ids);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"errorCode\":\"errors.com.epicgames.common.authentication.token_verification_failed\"}")]
    public void FriendIds_TreatAChangedOrErrorPayloadAsUnavailable(string? json)
    {
        Assert.False(EpicFriends.TryParseFriendIds(json, out var ids));
        Assert.Empty(ids);
    }

    [Fact]
    public void FriendIds_DropAnythingThatIsNotAnAccountId()
    {
        var json = """
        {
          "friends": [
            { "accountId": "not-an-account-id" },
            { "accountId": "" },
            { "mutual": 2 },
            { "accountId": "9974fdd51134460196b6f453d72c24ee" },
            { "accountId": "9974fdd51134460196b6f453d72c24ee" }
          ]
        }
        """;

        Assert.True(EpicFriends.TryParseFriendIds(json, out var ids));
        Assert.Equal("9974fdd51134460196b6f453d72c24ee", Assert.Single(ids));
    }

    [Fact]
    public void AccountNames_ComeFromThePublicLookupBatch()
    {
        var json = $$"""
        [
          { "id": "{{AccountA}}", "displayName": "someone", "externalAuths": {} },
          { "id": "{{AccountB}}", "displayName": "  spaced  ", "externalAuths": {} }
        ]
        """;

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        EpicFriends.ReadAccountNames(json, names);

        Assert.Equal("someone", names[AccountA]);
        Assert.Equal("spaced", names[AccountB]);
    }

    [Fact]
    public void AccountNames_SkipAccountsEpicReturnsWithoutAName()
    {
        var json = $$"""
        [
          { "id": "{{AccountA}}", "externalAuths": {} },
          { "id": "{{AccountB}}", "displayName": "", "externalAuths": {} },
          { "displayName": "orphan" }
        ]
        """;

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        EpicFriends.ReadAccountNames(json, names);

        Assert.Empty(names);
    }

    [Fact]
    public void LastOnline_IsReadAsATimestampKeyedByAccount()
    {
        var json = $$"""
        {
          "{{AccountA}}": [ { "last_online": "2026-08-17T21:04:55.130Z" } ],
          "{{AccountB}}": [ { "last_online": "not a date" } ],
          "junk": [ { "last_online": "2026-08-17T21:04:55.130Z" } ]
        }
        """;

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        EpicFriends.ReadLastOnline(json, seen);

        Assert.Equal(
            DateTimeOffset.Parse("2026-08-17T21:04:55.130Z").ToUniversalTime().ToString("O"),
            seen[AccountA]);
        Assert.False(seen.ContainsKey(AccountB));
        Assert.False(seen.ContainsKey("junk"));
    }

    [Fact]
    public void LastOnline_DoesNotReadAGameOrStatusFieldAsPresence()
    {
        var json = $$"""
        {
          "{{AccountA}}": [ {
            "last_online": "2026-08-17T21:04:55.130Z",
            "status": "online",
            "gameName": "Fortnite",
            "productId": "fn"
          } ]
        }
        """;

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        EpicFriends.ReadLastOnline(json, seen);

        Assert.Equal(
            DateTimeOffset.Parse("2026-08-17T21:04:55.130Z").ToUniversalTime().ToString("O"),
            seen[AccountA]);
        Assert.Single(seen);
    }

    [Fact]
    public void Friend_IsANameAndLastSeen_NotALiveState()
    {
        var fields = typeof(EpicFriends.Friend).GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Id", "LastOnlineUtc", "Name"], fields);
    }

    [Fact]
    public void Build_SkipsAnyoneEpicWouldNotName()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { [AccountA] = "someone" };
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        var friends = EpicFriends.Build([AccountA, AccountB], names, seen);

        // A bare account id is not a person, so the unnamed row never lands.
        var only = Assert.Single(friends);
        Assert.Equal("someone", only.Name);
        Assert.Null(only.LastOnlineUtc);
    }

    [Fact]
    public void Build_NeverLetsARawEpicAccountIdOutAndSortsByName()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AccountA] = "zeta",
            [AccountB] = "alpha",
        };
        var seen = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AccountB] = "2026-08-17T21:04:55.1300000+00:00",
        };

        var friends = EpicFriends.Build([AccountA, AccountB], names, seen);

        Assert.Equal(["alpha", "zeta"], friends.Select(friend => friend.Name));
        foreach (var friend in friends)
        {
            Assert.StartsWith("epic:", friend.Id, StringComparison.Ordinal);
            Assert.DoesNotContain(AccountA, friend.Id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AccountB, friend.Id, StringComparison.OrdinalIgnoreCase);
        }

        // The same account always keys to the same row, so links survive a refetch.
        Assert.Equal(
            friends.Single(friend => friend.Name == "alpha").Id,
            EpicFriends.Build([AccountB], names, seen).Single().Id);
    }

    /// <summary>
    /// Epic serves presence over its chat service, not HTTP. Nothing this
    /// adapter produces may carry a live state.
    /// </summary>
    [Fact]
    public void Snapshot_WithNoSessionIsNotReachableAndCarriesNobody()
    {
        var snapshot = EpicFriends.Snapshot.NoSession;

        Assert.False(snapshot.Reachable);
        Assert.False(snapshot.SessionPresent);
        Assert.Empty(snapshot.Friends);
        Assert.Equal(EpicFriends.NoSessionNote, snapshot.Note);

        // A reachable Epic that answered is a session; an Epic that blipped
        // still is, so its last verified names are not thrown away.
        Assert.True(EpicFriends.Snapshot.Unreachable(EpicFriends.UnreachableNote).SessionPresent);
    }

    [Fact]
    public void ReachableNote_SaysPlainlyThatEpicGivesNoPresence()
    {
        Assert.Contains("does not hand out live presence", EpicFriends.ReachableNote, StringComparison.Ordinal);
        Assert.Contains("Backing off", EpicFriends.ThrottledNote, StringComparison.Ordinal);
    }
}
