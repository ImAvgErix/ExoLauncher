using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class TrophyNotificationServiceTests
{
    [Fact]
    public void PreviewUsesRepresentativeGoldArtContract()
    {
        var service = new TrophyNotificationService(new SettingsService());
        TrophyNotificationRequest? request = null;
        service.Requested += value => request = value;

        service.Preview();

        var payload = Assert.IsType<TrophyNotificationRequest>(request).Payload;
        Assert.Equal("First light", payload.AchievementName);
        Assert.Equal(TrophyRarity.Gold, payload.Rarity);
        Assert.Equal(4.8d, payload.RarityPercent);
        Assert.True(payload.IsPreview);
    }

    [Fact]
    public void DisabledNotificationsAcknowledgeWithoutAttemptingPresentation()
    {
        var settings = new SettingsService();
        settings.ApplyPatch(trophyNotificationsEnabled: false);
        var service = new TrophyNotificationService(settings);
        var requested = false;
        var acknowledged = false;
        service.Requested += _ => requested = true;

        service.Notify(new TrophyNotificationPayload("Game", "Achievement", "Detail"), () => acknowledged = true);

        Assert.False(requested);
        Assert.True(acknowledged);
    }
}
