
namespace ExoLauncher.Adapters;

internal static class SteamSelfIdentity
{
    private const ulong PublicIndividualSteamIdBase = 76561197960265728UL;
    private const int AccountTypeShift = 52;
    private const int UniverseShift = 56;
    private const ulong AccountTypeMask = 0xFUL;
    private const ulong UniverseMask = 0xFFUL;
    private const ulong AccountIdMask = uint.MaxValue;
    private const ulong IndividualAccountType = 1UL;

    internal static string? LoadSelfSteamId64(string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return null;
        var config = SteamPlaytime.TryGetLocalConfigPath(steamRoot);
        if (config is null) return null;
        var self = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(config)));
        return ToSteamId64(self);
    }

    /// <summary>
    /// Steam may key the local config by a 32-bit account id or by a complete
    /// SteamID64. Normalize both forms, but reject clans, chats, and other
    /// non-Individual identities.
    /// </summary>
    internal static string? ToSteamId64(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) ||
            !ulong.TryParse(accountId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ||
            id == 0)
            return null;

        if (id <= uint.MaxValue)
            return (PublicIndividualSteamIdBase + id).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var universe = (id >> UniverseShift) & UniverseMask;
        var accountType = (id >> AccountTypeShift) & AccountTypeMask;
        var embeddedAccountId = id & AccountIdMask;
        if (universe == 0 || accountType != IndividualAccountType || embeddedAccountId == 0)
            return null;

        return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
