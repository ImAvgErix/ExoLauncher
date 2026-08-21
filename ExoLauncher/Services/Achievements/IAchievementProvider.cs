using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Models;

namespace ExoLauncher.Services.Achievements;

public interface IAchievementProvider
{
    string Id { get; }
    StoreKind Store { get; }
    AchievementProviderCapabilities Capabilities { get; }
    bool Supports(GameEntry game);
    Task<AchievementSnapshot> GetSnapshotAsync(
        GameEntry game,
        CancellationToken cancellationToken = default);

    /// <summary>Current signed-in account provenance, without a network read.</summary>
    string? GetCurrentCoverageKey(GameEntry game) => null;

    /// <summary>
    /// False when this store has no local file or official API Exo is allowed
    /// to read. Coverage must not promise a later refresh.
    /// </summary>
    bool CanObserveUnlocks => true;

    /// <summary>Honest limitation when <see cref="CanObserveUnlocks"/> is false.</summary>
    string CoverageMessage => string.Empty;

    /// <summary>
    /// Session poll hint. Floor is enforced by <see cref="AchievementService"/>
    /// so a provider cannot spin during gameplay.
    /// </summary>
    TimeSpan SuggestedPollInterval => TimeSpan.FromSeconds(12);
}

internal static class AchievementCoverageKeys
{
    /// <summary>Creates stable provenance without persisting or syncing a raw account id.</summary>
    public static string FromAccount(string providerId, string accountId)
    {
        var payload = Encoding.UTF8.GetBytes(
            "exo-achievements\0" + providerId.ToLowerInvariant() + "\0" + accountId);
        var digest = SHA256.HashData(payload);
        return providerId.ToLowerInvariant() + ":" +
               Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }
}
