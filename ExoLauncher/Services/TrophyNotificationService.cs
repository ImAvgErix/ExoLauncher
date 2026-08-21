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

    private int _previewRarity;

    public bool Preview(Action? onPainted = null) => Preview(null, null, null, onPainted);

    public bool Preview(string? gameTitle, string? unlockName, string? coverUrl, Action? onPainted = null) =>
        Preview(gameTitle, unlockName, coverUrl, rarity: null, onPainted);

    public bool Preview(
        string? gameTitle,
        string? unlockName,
        string? coverUrl,
        TrophyRarity? rarity,
        Action? onPainted = null)
    {
        if (!_settings.Current.TrophyNotificationsEnabled) return false;
        var sample = TrophyBannerDesign.Current.Preview;
        var cycle = TrophyBannerDesign.Current.Cycle();
        var resolved = rarity ?? cycle[Math.Abs(_previewRarity++) % Math.Max(1, cycle.Length)];
        var name = string.IsNullOrWhiteSpace(unlockName) ? sample.AchievementName : unlockName.Trim();
        var game = string.IsNullOrWhiteSpace(gameTitle) ? sample.GameTitle : gameTitle.Trim();
        return Publish(new TrophyNotificationPayload(
            GameTitle: game,
            AchievementName: name,
            Detail: sample.Detail,
            IsRare: resolved is TrophyRarity.Gold or TrophyRarity.Platinum,
            IsPerfect: resolved == TrophyRarity.Platinum,
            CoverUrl: string.IsNullOrWhiteSpace(coverUrl) ? null : coverUrl.Trim(),
            Rarity: resolved,
            IsPreview: true),
            onPainted);
    }

    private bool Publish(TrophyNotificationPayload payload, Action? onPresented = null)
    {
        if (string.IsNullOrWhiteSpace(payload.AchievementName)) return false;
        var requested = Requested;
        if (requested is null)
        {
            Helpers.AppLog.Warn("Trophy notification has no active presenter.");
            return false;
        }
        try
        {
            requested.Invoke(new TrophyNotificationRequest(payload, onPresented));
            return true;
        }
        catch (Exception ex)
        {
            Helpers.AppLog.Debug("Trophy notification observer failed: " + ex.Message);
            return false;
        }
    }
}

public sealed record TrophyNotificationPayload(
    string GameTitle,
    string AchievementName,
    string Detail,
    bool IsRare = false,
    bool IsPerfect = false,
    string? IconUrl = null,
    string? CoverUrl = null,
    TrophyRarity Rarity = TrophyRarity.Unknown,
    double? RarityPercent = null,
    bool IsPreview = false);

/// <summary>Presentation request plus a one-shot acknowledgement callback.</summary>
public sealed record TrophyNotificationRequest(
    TrophyNotificationPayload Payload,
    Action? OnPresented = null);
