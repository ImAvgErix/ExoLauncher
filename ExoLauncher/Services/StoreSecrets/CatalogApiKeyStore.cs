namespace ExoLauncher.Services;

/// <summary>
/// Optional catalog keys. Never logged, never written to settings.json,
/// never sent back to the UI after save.
/// </summary>
internal static class CatalogApiKeyStore
{
    internal const string TwitchClientIdFile = "igdb-twitch-client-id.bin";
    internal const string TwitchClientSecretFile = "igdb-twitch-client-secret.bin";
    internal const string SteamGridDbFile = "steamgriddb.bin";

    internal static bool HasTwitchClientId() => StoreSecretStore.Has(TwitchClientIdFile, Normalize);
    internal static bool HasTwitchClientSecret() => StoreSecretStore.Has(TwitchClientSecretFile, Normalize);
    internal static bool HasSteamGridDb() => StoreSecretStore.Has(SteamGridDbFile, Normalize);

    /// <summary>The player's own OpenXBL key (xbl.io), for Xbox time played.</summary>
    internal const string OpenXblFile = "openxbl.bin";
    internal static bool HasOpenXbl() => StoreSecretStore.Has(OpenXblFile, Normalize);
    internal static string? TryReadOpenXbl() => StoreSecretStore.TryRead(OpenXblFile, Normalize);
    internal static bool SaveOpenXbl(string? raw) => StoreSecretStore.Save(OpenXblFile, raw, Normalize);

    internal static string? TryReadTwitchClientId() =>
        StoreSecretStore.TryRead(TwitchClientIdFile, Normalize) ?? ExoAppCredentials.TwitchClientId();
    internal static string? TryReadTwitchClientSecret() =>
        StoreSecretStore.TryRead(TwitchClientSecretFile, Normalize) ?? ExoAppCredentials.TwitchClientSecret();
    internal static string? TryReadSteamGridDb() => StoreSecretStore.TryRead(SteamGridDbFile, Normalize);
    internal static bool SaveTwitchClientId(string? raw) => StoreSecretStore.Save(TwitchClientIdFile, raw, Normalize);
    internal static bool SaveTwitchClientSecret(string? raw) => StoreSecretStore.Save(TwitchClientSecretFile, raw, Normalize);
    internal static bool SaveSteamGridDb(string? raw) => StoreSecretStore.Save(SteamGridDbFile, raw, Normalize);

    internal static string? Normalize(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length is < 8 or > 256) return null;
        if (!trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':'))
            return null;
        return trimmed;
    }
}
