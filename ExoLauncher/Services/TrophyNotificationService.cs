using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Session-bound notification broker. It never starts a resident process and
/// does not inspect or hook game memory; achievement providers publish normal
/// data into this in-process queue.
/// </summary>
public sealed class TrophyNotificationService
{
    private readonly SettingsService _settings;

    public TrophyNotificationService(SettingsService settings) => _settings = settings;

    public event Action<TrophyNotificationRequest>? Requested;

    /// <summary>
    /// Requests a toast and invokes <paramref name="onPresented"/> only when
    /// the native presenter confirms it created the notification window. When
    /// the user has disabled trophy notifications, the request is intentionally
    /// consumed without showing a toast.
    /// </summary>
    public void Notify(TrophyNotificationPayload payload, Action? onPresented = null)
    {
        if (!_settings.Current.TrophyNotificationsEnabled)
        {
            try { onPresented?.Invoke(); }
            catch (Exception ex) { Helpers.AppLog.Debug("Trophy notification acknowledgement failed: " + ex.Message); }
            return;
        }
        Publish(payload, onPresented);
    }

    public void Preview() => Publish(new TrophyNotificationPayload(
        GameTitle: "Exo Launcher",
        AchievementName: "First light",
        Detail: "Achievement notification preview",
        IsRare: true,
        Rarity: TrophyRarity.Gold,
        RarityPercent: 4.8d,
        IsPreview: true));

    private void Publish(TrophyNotificationPayload payload, Action? onPresented = null)
    {
        if (string.IsNullOrWhiteSpace(payload.AchievementName)) return;
        var requested = Requested;
        if (requested is null)
        {
            Helpers.AppLog.Warn("Trophy notification has no active presenter.");
            return;
        }
        try { requested.Invoke(new TrophyNotificationRequest(payload, onPresented)); }
        catch (Exception ex) { Helpers.AppLog.Debug("Trophy notification observer failed: " + ex.Message); }
    }
}

public sealed record TrophyNotificationPayload(
    string GameTitle,
    string AchievementName,
    string Detail,
    bool IsRare = false,
    bool IsPerfect = false,
    string? IconUrl = null,
    TrophyRarity Rarity = TrophyRarity.Unknown,
    double? RarityPercent = null,
    bool IsPreview = false);

/// <summary>Presentation request plus a one-shot acknowledgement callback.</summary>
public sealed record TrophyNotificationRequest(
    TrophyNotificationPayload Payload,
    Action? OnPresented = null);
