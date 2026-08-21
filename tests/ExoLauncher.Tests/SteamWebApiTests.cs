using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamWebApiTests
{
    [Fact]
    public void PublicInGame_MapsToInGameAndKeepsTheTitleSteamReturned()
    {
        var players = new Dictionary<string, SteamWebApi.Summary>(StringComparer.Ordinal);
        Assert.True(SteamWebApi.TryParseSummaries("""
            {"response":{"players":[{
              "steamid":"76561197960361544",
              "communityvisibilitystate":3,
              "personastate":1,
              "gameid":"570",
              "gameextrainfo":"Dota 2"
            }]}}
            """, players));

        var player = Assert.Single(players);
        Assert.Equal("76561197960361544", player.Key);
        Assert.Equal("ingame", player.Value.Status);
        Assert.Equal("steam:570", player.Value.PlayingId);
        Assert.Equal("Dota 2", player.Value.PlayingTitle);
    }

    [Fact]
    public void PrivateProfileWithPersonaStateZero_IsOfflineOnTheLiveApi()
    {
        var players = new Dictionary<string, SteamWebApi.Summary>(StringComparer.Ordinal);
        Assert.True(SteamWebApi.TryParseSummaries("""
            {"response":{"players":[{
              "steamid":"77561198355051011",
              "communityvisibilitystate":1,
              "personastate":0
            }]}}
            """, players));

        var player = Assert.Single(players).Value;
        Assert.Equal("offline", player.Status);
        Assert.Null(player.PlayingId);
        Assert.Null(player.PlayingTitle);
    }

    [Fact]
    public void PrivateProfileWithoutPersonaState_StaysUnknown()
    {
        var players = new Dictionary<string, SteamWebApi.Summary>(StringComparer.Ordinal);
        Assert.True(SteamWebApi.TryParseSummaries("""
            {"response":{"players":[{
              "steamid":"77561198355051011",
              "communityvisibilitystate":1
            }]}}
            """, players));

        var player = Assert.Single(players).Value;
        Assert.Equal("unknown", player.Status);
        Assert.Null(player.LastSeenUtc);
        Assert.Null(player.PlayingId);
        Assert.Null(player.PlayingTitle);
    }

    [Fact]
    public void MissingPersonaStateWithPositiveLastLogoff_IsEvidenceBackedOffline()
    {
        var players = new Dictionary<string, SteamWebApi.Summary>(StringComparer.Ordinal);
        Assert.True(SteamWebApi.TryParseSummaries("""
            {"response":{"players":[{
              "steamid":"77561198355051011",
              "communityvisibilitystate":1,
              "lastlogoff":1700000000
            }]}}
            """, players));

        var player = Assert.Single(players).Value;
        Assert.Equal("offline", player.Status);
        Assert.Equal("2023-11-14T22:13:20.0000000+00:00", player.LastSeenUtc);
        Assert.Null(player.PlayingId);
        Assert.Null(player.PlayingTitle);
    }

    [Fact]
    public void OutOfRangeLastLogoff_DoesNotInventOfflinePresence()
    {
        var players = new Dictionary<string, SteamWebApi.Summary>(StringComparer.Ordinal);
        Assert.True(SteamWebApi.TryParseSummaries("""
            {"response":{"players":[{
              "steamid":"77561198355051011",
              "lastlogoff":9223372036854775807
            }]}}
            """, players));

        var player = Assert.Single(players).Value;
        Assert.Equal("unknown", player.Status);
        Assert.Null(player.LastSeenUtc);
    }

    [Theory]
    [InlineData(1, false, "online", null)]
    [InlineData(2, false, "dnd", null)]
    [InlineData(3, false, "away", null)]
    [InlineData(4, false, "away", "Snooze")]
    [InlineData(5, false, "online", "Looking to trade")]
    [InlineData(6, false, "online", "Looking to play")]
    [InlineData(0, false, "offline", null)]
    [InlineData(1, true, "ingame", null)]
    public void MapState_FollowsThePublishedPersonaStates(
        int state, bool inGame, string status, string? statusText)
    {
        Assert.Equal((status, statusText), SteamWebApi.MapState(state, inGame));
    }

    [Fact]
    public void InGameWithoutANumericGameId_KeepsTheTitle_DoesNotInventAnId()
    {
        var players = new Dictionary<string, SteamWebApi.Summary>(StringComparer.Ordinal);
        Assert.True(SteamWebApi.TryParseSummaries("""
            {"response":{"players":[{
              "steamid":"76561197960361544",
              "communityvisibilitystate":3,
              "personastate":1,
              "gameextrainfo":"A non-Steam shortcut"
            }]}}
            """, players));

        var player = Assert.Single(players).Value;
        Assert.Equal("ingame", player.Status);
        Assert.Null(player.PlayingId);
        Assert.Equal("A non-Steam shortcut", player.PlayingTitle);
    }

    [Fact]
    public void BatchSize_IsOneHundred()
    {
        Assert.Equal(100, SteamWebApi.BatchSize);
        Assert.Equal(
            "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/",
            SteamWebApi.SummariesUrl);
        Assert.DoesNotContain("ISteamUser/GetFriendList", File.ReadAllText(
            Path.Combine(RepoRoot(), "ExoLauncher", "Adapters", "SteamWebApi.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void RequestIdentity_UsesTheSteamIdSet_NotOnlyItsCount()
    {
        const string apiKey = "identity-fixture-key";
        var first = SteamWebApi.RequestIdentity(
            apiKey,
            ["76561197960361544", "77561198355051011"]);
        var reordered = SteamWebApi.RequestIdentity(
            apiKey,
            ["77561198355051011", "76561197960361544", "76561197960361544"]);
        var different = SteamWebApi.RequestIdentity(
            apiKey,
            ["76561197960361544", "78561199000000000"]);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, different);

        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "ExoLauncher", "Adapters", "SteamWebApi.cs"));
        Assert.Contains("var requestIdentity = RequestIdentity(key, ids);", source, StringComparison.Ordinal);
        Assert.Contains("InFlight.TryGetValue(requestIdentity", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(_cacheKey, requestIdentity", source, StringComparison.Ordinal);
        Assert.Contains("RetryByIdentity.TryGetValue(requestIdentity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ids.Count.ToString", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestIdentity_ChangesWithCredential_WithoutExposingTheCredential()
    {
        const string firstKey = "first-sensitive-steam-api-key";
        const string secondKey = "second-sensitive-steam-api-key";
        string[] ids = ["76561197960361544"];

        var first = SteamWebApi.RequestIdentity(firstKey, ids);
        var repeated = SteamWebApi.RequestIdentity(firstKey, ids);
        var changed = SteamWebApi.RequestIdentity(secondKey, ids);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, changed);
        Assert.DoesNotContain(firstKey, first, StringComparison.Ordinal);
        Assert.DoesNotContain(secondKey, changed, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnedGamesParser_EmptyGamesArrayIsAuthoritative()
    {
        Assert.True(SteamWebApi.TryParseOwnedGames(
            "{\"response\":{\"game_count\":0,\"games\":[]}}",
            out var appIds,
            out var authoritative));

        Assert.True(authoritative);
        Assert.Empty(appIds);
    }

    [Fact]
    public void OwnedGamesParser_MissingGamesArrayIsNotAuthoritative()
    {
        Assert.False(SteamWebApi.TryParseOwnedGames(
            "{\"response\":{}}",
            out var appIds,
            out var authoritative));

        Assert.False(authoritative);
        Assert.Empty(appIds);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
