using ExoLauncher.Adapters;

namespace ExoLauncher.Services;

/// <summary>
/// Steam's login lives inside the Steam process; Exo never holds a Steam token.
/// The one real local signal is the active account — <c>ActiveProcess\ActiveUser</c>,
/// or a sole <c>userdata</c> folder — resolving to a config Exo can parse. That is
/// a session Exo can read for this account's library and playtime. It is never
/// proof the account is online.
/// </summary>
internal static class SteamSessionProbe
{
    /// <summary>True when Steam's active local account resolves on this PC.</summary>
    public static bool HasReadableAccount() => HasReadableAccount(SteamInstallation.ResolveRoot());

    /// <param name="steamRoot">Explicit Steam root, for callers that already resolved one.</param>
    public static bool HasReadableAccount(string? steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return false;
        try
        {
            return SteamPlaytime.LoadActiveAccount(steamRoot) is not null;
        }
        catch
        {
            // An unreadable Steam tree means "no account Exo can read", not a fault.
            return false;
        }
    }

}
