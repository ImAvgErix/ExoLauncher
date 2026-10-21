namespace ExoLauncher.Adapters.Cli;

/// <summary>The intent of one non-activating Steam client invocation.</summary>
public enum SteamUpdateCommandPurpose
{
    RequestInstallOrUpdate,
}

/// <summary>A Steam executable invocation whose argument boundaries are preserved.</summary>
public sealed record SteamUpdateCommand(
    SteamUpdateCommandPurpose Purpose,
    IReadOnlyList<string> Arguments);

/// <summary>
/// Selects the quiet Steam commands needed to start or promote an update.
/// Pure so queued-update behavior can be verified without launching Steam.
/// </summary>
public static class SteamUpdateCommandPlan
{
    private static readonly string[] QuietClientArguments =
    [
        "-silent",
        "-nofriendsui",
        "-nochatui",
    ];

    public static IReadOnlyList<string> HiddenClientStartArguments() =>
        [.. QuietClientArguments];

    public static IReadOnlyList<SteamUpdateCommand> BuildNudge(string appId)
    {
        ValidateAppId(appId);

        return
        [
            new(
                SteamUpdateCommandPurpose.RequestInstallOrUpdate,
                [.. QuietClientArguments, SteamProtocol.InstallUri(appId)]),
        ];
    }

    private static void ValidateAppId(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || appId.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("Steam app id must contain digits only.", nameof(appId));
    }
}
