using System.Text.RegularExpressions;
using ExoLauncher.Adapters.Cli;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam friends already stored in the active account's localconfig.vdf.
/// Names and avatar hashes only — never tokens, never a network scrape.
/// </summary>
internal static partial class SteamFriends
{
    private const ulong PublicIndividualSteamIdBase = 76561197960265728UL;
    private const int AccountTypeShift = 52;
    private const int UniverseShift = 56;
    private const ulong AccountTypeMask = 0xFUL;
    private const ulong UniverseMask = 0xFFUL;
    private const ulong AccountIdMask = uint.MaxValue;
    private const ulong IndividualAccountType = 1UL;

    /// <param name="SteamId64">
    /// Join key for Steam's Web API only. Never sent to the UI, logs, or link file.
    /// </param>
    public sealed record Friend(string AccountKey, string Name, string? AvatarUrl, string? SteamId64 = null);
    public sealed record Profile(string Name, string? AvatarUrl);

    public static Profile? LoadSelf(string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return null;
        var config = SteamPlaytime.TryGetLocalConfigPath(steamRoot);
        if (config is null) return null;
        var text = SteamPlaytime.ReadSharedText(config);
        if (text is null) return null;
        var self = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(config)));
        if (string.IsNullOrWhiteSpace(self)) return null;
        return ParseSelf(text, self);
    }

    internal static string? LoadSelfSteamId64(string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return null;
        var config = SteamPlaytime.TryGetLocalConfigPath(steamRoot);
        if (config is null) return null;
        var self = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(config)));
        return ToSteamId64(self);
    }

    internal static Profile? ParseSelf(string text, string selfAccountId)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(selfAccountId))
            return null;
        var start = FriendsHeaderRegex().Match(text);
        if (!start.Success) return null;
        var body = SliceBraceBlock(text, start.Index + start.Length - 1);
        if (body is null) return null;

        foreach (Match idMatch in FriendIdRegex().Matches(body))
        {
            if (!string.Equals(idMatch.Groups[1].Value, selfAccountId, StringComparison.Ordinal))
                continue;
            var cursor = idMatch.Index + idMatch.Length;
            while (cursor < body.Length && char.IsWhiteSpace(body[cursor])) cursor++;
            if (cursor >= body.Length || body[cursor] != '{') continue;
            var inner = SliceBraceBlock(body, cursor);
            if (inner is null) continue;
            var name = FriendNameRegex().Match(inner).Groups[1].Value.Trim();
            if (name.Length == 0)
                name = PersonaNameRegex().Match(body).Groups[1].Value.Trim();
            if (name.Length == 0) return null;
            var avatar = FriendAvatarRegex().Match(inner).Groups[1].Value.Trim();
            string? avatarUrl = null;
            if (avatar.Length == 40 && avatar.All(IsHex))
                avatarUrl = "https://avatars.steamstatic.com/" + avatar + "_full.jpg";
            return new Profile(name, avatarUrl);
        }

        var fallback = PersonaNameRegex().Match(body).Groups[1].Value.Trim();
        return fallback.Length == 0 ? null : new Profile(fallback, null);
    }

    public static IReadOnlyList<Friend> LoadActiveAccount(string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return Array.Empty<Friend>();
        var config = SteamPlaytime.TryGetLocalConfigPath(steamRoot);
        if (config is null) return Array.Empty<Friend>();
        var text = SteamPlaytime.ReadSharedText(config);
        if (text is null) return Array.Empty<Friend>();
        var self = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(config)));
        return ParseFriends(text, self);
    }

    internal static IReadOnlyList<Friend> ParseFriends(string text, string? selfAccountId)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<Friend>();
        var start = FriendsHeaderRegex().Match(text);
        if (!start.Success) return Array.Empty<Friend>();
        var open = start.Index + start.Length - 1;
        var body = SliceBraceBlock(text, open);
        if (body is null) return Array.Empty<Friend>();
        var friends = new List<Friend>();
        foreach (Match idMatch in FriendIdRegex().Matches(body))
        {
            var accountId = idMatch.Groups[1].Value;
            if (string.Equals(accountId, selfAccountId, StringComparison.Ordinal))
                continue;
            // Steam stores both people and community/clan rows in this block.
            // Only an Individual SteamID can join GetPlayerSummaries.
            var steamId64 = ToSteamId64(accountId);
            if (steamId64 is null) continue;
            var cursor = idMatch.Index + idMatch.Length;
            while (cursor < body.Length && char.IsWhiteSpace(body[cursor])) cursor++;
            if (cursor >= body.Length || body[cursor] != '{') continue;
            var inner = SliceBraceBlock(body, cursor);
            if (inner is null) continue;

            var name = FriendNameRegex().Match(inner).Groups[1].Value.Trim();
            if (name.Length is 0 or > 64) continue;

            var avatar = FriendAvatarRegex().Match(inner).Groups[1].Value.Trim();
            string? avatarUrl = null;
            if (avatar.Length == 40 && avatar.All(IsHex))
                avatarUrl = "https://avatars.steamstatic.com/" + avatar + "_full.jpg";

            friends.Add(new Friend(HashAccount(accountId), name, avatarUrl, steamId64));
        }

        return friends
            .GroupBy(friend => friend.AccountKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(friend => friend.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Steam may key the local friends block by a 32-bit account id or by a
    /// complete SteamID64. Normalize both forms, but reject clans, chats, and
    /// other non-Individual identities that cannot be player-summary rows.
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

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string HashAccount(string accountId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("exo-steam-friend:" + accountId));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string? SliceBraceBlock(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return text[(openIndex + 1)..i];
            }
        }

        return null;
    }

    [GeneratedRegex("\"friends\"\\s*\\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FriendsHeaderRegex();

    [GeneratedRegex("\"(\\d{5,18})\"", RegexOptions.CultureInvariant)]
    private static partial Regex FriendIdRegex();

    [GeneratedRegex("\"name\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FriendNameRegex();

    [GeneratedRegex("\"avatar\"\\s*\"([0-9a-fA-F]{40})\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FriendAvatarRegex();

    [GeneratedRegex("\"PersonaName\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PersonaNameRegex();
}
