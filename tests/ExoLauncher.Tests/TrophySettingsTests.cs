using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class TrophySettingsTests
{
    [Theory]
    [InlineData("top-left", 0d, 0d)]
    [InlineData("top-center", 0.5d, 0d)]
    [InlineData("top-right", 1d, 0d)]
    [InlineData("center-left", 0d, 0.5d)]
    [InlineData("center", 0.5d, 0.5d)]
    [InlineData("center-right", 1d, 0.5d)]
    [InlineData("bottom-left", 0d, 1d)]
    [InlineData("bottom-center", 0.5d, 1d)]
    [InlineData("bottom-right", 1d, 1d)]
    public void PositionPatchUsesOneOfTheNineCanonicalAnchors(string requestedPosition, double expectedX, double expectedY)
    {
        var settings = new SettingsService();

        settings.ApplyPatch(trophyNotificationPreset: "legacy", trophyNotificationPosition: requestedPosition);

        var current = settings.Current;
        Assert.Equal("exo", current.TrophyNotificationPreset);
        Assert.Equal(requestedPosition, current.TrophyNotificationPosition);
        Assert.Equal(expectedX, current.TrophyNotificationPositionX);
        Assert.Equal(expectedY, current.TrophyNotificationPositionY);
    }

    [Theory]
    [InlineData(0.00d, 0.00d, "top-left")]
    [InlineData(0.24d, 0.74d, "center-left")]
    [InlineData(0.25d, 0.75d, "bottom-center")]
    [InlineData(0.99d, 0.01d, "top-right")]
    public void LegacyCoordinatesAreQuantizedToAnExactAnchor(double x, double y, string expectedPosition)
    {
        var settings = new SettingsService();

        settings.ApplyPatch(trophyNotificationPositionX: x, trophyNotificationPositionY: y);

        var current = settings.Current;
        Assert.Equal(expectedPosition, current.TrophyNotificationPosition);
        Assert.Contains(current.TrophyNotificationPositionX, new[] { 0d, 0.5d, 1d });
        Assert.Contains(current.TrophyNotificationPositionY, new[] { 0d, 0.5d, 1d });
    }

    [Fact]
    public void NonFiniteCoordinatesKeepTheExistingCanonicalAnchor()
    {
        var settings = new SettingsService();
        settings.ApplyPatch(trophyNotificationPosition: "center-right");

        settings.ApplyPatch(
            trophyNotificationPositionX: double.NaN,
            trophyNotificationPositionY: double.PositiveInfinity);

        var current = settings.Current;
        Assert.Equal("center-right", current.TrophyNotificationPosition);
        Assert.Equal(1d, current.TrophyNotificationPositionX);
        Assert.Equal(0.5d, current.TrophyNotificationPositionY);
    }

    [Fact]
    public void LegacySettingsFileQuantizesFreeCoordinatesAndKeepsTheSingleExoCue()
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var root = Path.Combine(Path.GetTempPath(), "ExoLauncherTrophySettings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, root);
            File.WriteAllText(PathHelper.SettingsPath, """
            {
              "trophyNotificationPreset": "arcade",
              "trophyNotificationPositionX": 0.28,
              "trophyNotificationPositionY": 0.82,
              "trophyNotificationSoundCue": "soft"
            }
            """);

            var settings = new SettingsService();
            settings.Load();

            var current = settings.Current;
            Assert.Equal("exo", current.TrophyNotificationPreset);
            Assert.Equal("bottom-center", current.TrophyNotificationPosition);
            Assert.Equal(0.5d, current.TrophyNotificationPositionX);
            Assert.Equal(1d, current.TrophyNotificationPositionY);
            Assert.Equal("exo", current.TrophyNotificationSoundCue);
            Assert.True(current.TrophyNotificationSound);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
            try { Directory.Delete(root, recursive: true); }
            catch { /* temporary test cleanup is best effort */ }
        }
    }

    [Fact]
    public void CanonicalAnchorSurvivesARoundTrip()
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var root = Path.Combine(Path.GetTempPath(), "ExoLauncherTrophyRoundTrip", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, root);
            var writer = new SettingsService();
            writer.ApplyPatch(trophyNotificationPositionX: 0.76d, trophyNotificationPositionY: 0.51d);

            var reader = new SettingsService();
            reader.Load();

            var current = reader.Current;
            Assert.Equal("center-right", current.TrophyNotificationPosition);
            Assert.Equal(1d, current.TrophyNotificationPositionX);
            Assert.Equal(0.5d, current.TrophyNotificationPositionY);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
            try { Directory.Delete(root, recursive: true); }
            catch { /* temporary test cleanup is best effort */ }
        }
    }
}
