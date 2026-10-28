using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways comparing versions went wrong before every check shared one way (each failure shows a wrong "Update available",
/// hides a real one, or calls a finished install failed):
/// - a missing part read as older (System.Version has 26.10 before 26.10.0): an update offered for what's installed;
/// - leading zeros read as different (AMD's chipset 7.06.02 is 7.6.2);
/// - only two numbers read (a third part ignored), or numbers compared as text (617.9 after 617.42);
/// - a "v" or a "-beta" or " WHQL" after the numbers makes it unreadable;
/// - text without a version reads as a version (an update offered over nothing).
/// </summary>
public sealed class VersionNumberTests
{
    [Theory]
    [InlineData("26.10", "26.10.0")]
    [InlineData("7.06.02.123", "7.6.2.123")]
    [InlineData("v3.16.0", "3.16.0")]
    [InlineData("3.16.0-beta.2", "3.16")]
    [InlineData("621.12 WHQL", "621.12")]
    public void TheSameVersionWrittenTwoWays(string a, string b)
    {
        Assert.Equal(0, VersionNumber.Compare(a, b));
        Assert.True(VersionNumber.Same(a, b));
        Assert.False(VersionNumber.Newer(a, b));
        Assert.False(VersionNumber.Behind(b, a));
    }

    [Theory]
    [InlineData("621.12", "621.01")]
    [InlineData("617.42", "617.9")]
    [InlineData("26.10.1", "26.9.2")]
    [InlineData("32.0.101.9150", "32.0.101.8509")]
    [InlineData("3.16.1", "3.16")]
    [InlineData("622.00", "621.99")]
    public void TheNewerOneIsNewer(string newer, string older)
    {
        Assert.True(VersionNumber.Compare(newer, older) > 0);
        Assert.True(VersionNumber.Newer(newer, older));
        Assert.True(VersionNumber.Behind(older, newer));
        Assert.True(VersionNumber.AtLeast(newer, older));
        Assert.False(VersionNumber.AtLeast(older, newer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Adrenalin 26.10.1")]
    [InlineData("unknown")]
    [InlineData("1234567890123456789")]
    public void NotAVersionIsNeverAnUpdateNorUpToDate(string? text)
    {
        Assert.Null(VersionNumber.Parse(text));
        Assert.False(VersionNumber.Newer(text, "1.0"));
        Assert.False(VersionNumber.Behind(text, "1.0"));
        Assert.False(VersionNumber.Same(text, text));
        Assert.True(VersionNumber.Compare(text, "0.1") < 0);
    }

    [Fact]
    public void WithNothingInstalledAnyRealVersionIsNewer()
    {
        Assert.True(VersionNumber.Newer("26.10.1", null));
        Assert.False(VersionNumber.Behind(null, "26.10.1"));
    }

    [Fact]
    public void TheNewestOfAListIsPicked() =>
        Assert.Equal("7.06.02.123", new[] { "6.10.17.152", "7.06.02.123", "7.1.8.0" }.Max(VersionNumber.Order));
}
