using System.Text.Json;
using ExoLauncher.Helpers;
using Song = ExoLauncher.Helpers.MusicMatch.Song;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways listen-along could play the wrong song or none on the listener's own service:
/// - another artist's song with the same title wins;
/// - the music video, an extended mix, a live or sped-up version wins over the album cut;
/// - nothing matches because one service writes (feat. X), "- Remastered 2011", "&amp;" or "Artist - Topic";
/// - a title that has a version word in its own name ("Live Forever") is treated as a live version;
/// - a short artist name matches inside a longer one ("Ye" in "Kanye West");
/// - a bad request (no song, too many or broken candidates) throws instead of answering.
/// </summary>
public sealed class MusicMatchTests
{
    private static Song S(string title, string artists, long? ms = null) => new(title, artists.Split('|'), ms);

    [Fact]
    public void FindsTheSameRecordingAcrossSpellings()
    {
        var wanted = S("Get Lucky (feat. Pharrell Williams & Nile Rodgers)", "Daft Punk; Pharrell Williams; Nile Rodgers", 369_000);
        var candidates = new[]
        {
            S("Get Lucky (Radio Edit)", "Daft Punk", 248_000),
            S("Get Lucky", "Daft Punk|Pharrell Williams|Nile Rodgers", 368_000),
        };
        Assert.Equal(1, MusicMatch.Best(wanted, candidates));
    }

    [Fact]
    public void FoldsRemastersTopicChannelsAndAmpersands()
    {
        var wanted = S("Here Comes The Sun - Remastered 2009", "The Beatles", 185_000);
        Assert.Equal(0, MusicMatch.Best(wanted, [S("Here Comes the Sun", "Beatles - Topic", 186_000)]));
        Assert.Equal(0, MusicMatch.Best(S("Simon & Garfunkel Song", "Simon and Garfunkel"), [S("Simon and Garfunkel Song", "Simon & Garfunkel")]));
    }

    [Fact]
    public void AnotherArtistsSongWithTheSameNameNeverWins()
    {
        var wanted = S("Hello", "Adele", 295_000);
        Assert.Equal(-1, MusicMatch.Best(wanted, [S("Hello", "Lionel Richie", 248_000)]));
        Assert.Equal(1, MusicMatch.Best(wanted, [S("Hello", "Lionel Richie", 295_000), S("Hello", "Adele", 295_500)]));
    }

    [Fact]
    public void LengthDecidesBetweenEqualTitles()
    {
        var wanted = S("Blinding Lights", "The Weeknd", 200_000);
        var candidates = new[] { S("Blinding Lights", "The Weeknd", 262_000), S("Blinding Lights", "The Weeknd", 201_000) };
        Assert.Equal(1, MusicMatch.Best(wanted, candidates));
    }

    [Theory]
    [InlineData("Blinding Lights (Live)")]
    [InlineData("Blinding Lights - Sped Up")]
    [InlineData("Blinding Lights (Instrumental)")]
    [InlineData("Blinding Lights (Extended Version)")]
    public void AnotherVersionLosesToTheStudioCut(string version)
    {
        var wanted = S("Blinding Lights", "The Weeknd", 200_000);
        Assert.Equal(1, MusicMatch.Best(wanted, [S(version, "The Weeknd", 200_000), S("Blinding Lights", "The Weeknd", 203_000)]));
        Assert.Equal(0, MusicMatch.Best(S(version, "The Weeknd", 200_000), [S(version, "The Weeknd", 200_000), S("Blinding Lights", "The Weeknd", 200_000)]));
    }

    [Fact]
    public void AVersionWordInTheSongsOwnNameIsNotAVersion()
    {
        Assert.Equal(0, MusicMatch.Best(S("Live Forever - Remastered", "Oasis", 276_000), [S("Live Forever", "Oasis", 277_000)]));
    }

    [Fact]
    public void AShortArtistNameMustMatchWhole()
    {
        Assert.Equal(-1, MusicMatch.Best(S("Carnival", "Ye"), [S("Carnival", "Kanye West")]));
        Assert.Equal(0, MusicMatch.Best(S("Carnival", "Ye"), [S("Carnival", "¥$|Ye")]));
    }

    [Fact]
    public void AnUnrelatedTitleIsNoMatch() =>
        Assert.Equal(-1, MusicMatch.Best(S("Bohemian Rhapsody", "Queen"), [S("Another One Bites the Dust", "Queen")]));

    [Fact]
    public void AnswersBadRequestsWithoutThrowing()
    {
        Assert.Contains("ok = False", MusicMatch.Answer(JsonDocument.Parse("{}").RootElement).ToString());
        var odd = JsonDocument.Parse("""{"song":{"title":"Hello","artists":["Adele",3],"durationMs":"x"},"candidates":[1,{"title":5},{"title":"Hello","artists":["Adele"]}]}""").RootElement;
        Assert.Contains("index = 2", MusicMatch.Answer(odd).ToString());
        var many = JsonDocument.Parse("{\"song\":{\"title\":\"Hello\"},\"candidates\":[" + string.Join(',', Enumerable.Repeat("{\"title\":\"x\"}", 500)) + "]}").RootElement;
        Assert.Contains("index = -1", MusicMatch.Answer(many).ToString());
    }
}
