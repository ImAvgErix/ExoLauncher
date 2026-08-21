using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class TrophyNotificationServiceTests
{
    [Fact]
    public void PreviewUsesSharedDesignCopyWithoutProductBrand()
    {
        var service = new TrophyNotificationService(new SettingsService());
        TrophyNotificationRequest? request = null;
        service.Requested += value => request = value;
        var sample = TrophyBannerDesign.Current.Preview;

        Assert.True(service.Preview());

        var payload = Assert.IsType<TrophyNotificationRequest>(request).Payload;
        Assert.Equal(sample.AchievementName, payload.AchievementName);
        Assert.Equal(sample.GameTitle, payload.GameTitle);
        Assert.Equal(sample.Detail, payload.Detail);
        Assert.DoesNotContain("Exo Launcher", payload.GameTitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exo Launcher", payload.AchievementName, StringComparison.OrdinalIgnoreCase);
        Assert.Null(payload.RarityPercent);
        Assert.Equal(TrophyRarity.Bronze, payload.Rarity);
        Assert.True(payload.IsPreview);

        Assert.True(service.Preview());
        Assert.Equal(TrophyRarity.Silver, Assert.IsType<TrophyNotificationRequest>(request).Payload.Rarity);
    }

    [Fact]
    public void PreviewCanPinATierWithoutAdvancingTheCycle()
    {
        var service = new TrophyNotificationService(new SettingsService());
        TrophyNotificationRequest? request = null;
        service.Requested += value => request = value;

        Assert.True(service.Preview(null, null, null, TrophyRarity.Platinum, null));
        Assert.Equal(TrophyRarity.Platinum, Assert.IsType<TrophyNotificationRequest>(request).Payload.Rarity);

        Assert.True(service.Preview());
        Assert.Equal(TrophyRarity.Bronze, Assert.IsType<TrophyNotificationRequest>(request).Payload.Rarity);
    }

    [Fact]
    public void PreviewWithoutPresenterReturnsFalse()
    {
        var service = new TrophyNotificationService(new SettingsService());
        Assert.False(service.Preview());
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
