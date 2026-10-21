namespace ExoLauncher.Services;

/// <summary>
/// Exo's own developer apps. Users authorize; they do not paste keys.
/// Public client ids may ship later. Secrets come from environment on this
/// PC — never from React, never from settings.json. A Settings "Your own app"
/// paste is a Debug override (or EXO_SHOW_DEV_KEYS), not the shipped path.
/// </summary>
internal static class ExoAppCredentials
{
    internal const string TwitchIdEnv = "EXO_TWITCH_CLIENT_ID";
    internal const string TwitchSecretEnv = "EXO_TWITCH_CLIENT_SECRET";
    internal const string ShowDeveloperKeysEnv = "EXO_SHOW_DEV_KEYS";

#if DEBUG
    internal const bool DebugBuild = true;
#else
    internal const bool DebugBuild = false;
#endif

    /// <summary>
    /// Developer-app paste fields. Debug always. Shipped only with EXO_SHOW_DEV_KEYS=1.
    /// </summary>
    internal static bool DeveloperKeysVisible() => Visible(DebugBuild, Env(ShowDeveloperKeysEnv));

    internal static bool Visible(bool debugBuild, string? envFlag) =>
        debugBuild || envFlag is "1" or "true" or "TRUE" or "yes" or "YES";

    /// <summary>Public Twitch client id for IGDB. Null until an official Exo app exists.</summary>
    internal static string? BundledTwitchClientId => null;

    internal static string? TwitchClientId() =>
        CatalogApiKeyStore.Normalize(Env(TwitchIdEnv)) ?? BundledTwitchClientId;

    internal static string? TwitchClientSecret() =>
        CatalogApiKeyStore.Normalize(Env(TwitchSecretEnv));

    private static string? Env(string name)
    {
        try
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }
}
