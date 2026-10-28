using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam playtime + last played from userdata localconfig.vdf.
/// Reads the active Steam account and brace-matches app blocks (nested cloud
/// keys are common). Hours merge across every account on this PC; ownership is
/// always the active account's.
/// </summary>
internal static partial class SteamPlaytime
{
    private static readonly object Gate = new();
    private static AccountSnapshot? _snapshot;
    private static string? _loadedRoot;
    private static string? _loadedAccountId;
    private static (long Length, long WriteTicks) _loadedStamp;

    public readonly record struct Entry(int Minutes, DateTimeOffset? LastPlayedUtc);
    public sealed record AccountSnapshot(
        string AccountKey,
        IReadOnlyDictionary<string, Entry> Entries,
        IReadOnlySet<string> AppTicketIds,
        IReadOnlySet<string>? FamilyMemberIds = null);

    internal static string? TryGetLocalConfigPath(string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return null;
        var root = NormalizeRoot(steamRoot);
        var accountId = ResolveActiveAccountId(root, ReadRegistryActiveAccountId());
        if (!IsSafeAccountId(accountId)) return null;
        var config = Path.Combine(root, "userdata", accountId!, "config", "localconfig.vdf");
        return File.Exists(config) ? config : null;
    }

    public static AccountSnapshot? LoadActiveAccount(string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return null;
        var root = NormalizeRoot(steamRoot);
        // Resolve before consulting the cache. The prior cache
        // check keyed only by Steam root, so a shared PC could keep displaying
        // the previous account's localconfig for up to two minutes.
        var accountId = ResolveActiveAccountId(root, ReadRegistryActiveAccountId());
        if (!IsSafeAccountId(accountId)) return null;
        return LoadAccount(root, accountId);
    }

    /// <summary>
    /// Hours and last played for the library: the active account's, or with
    /// <paramref name="allAccounts"/> every account's on this PC (minutes summed,
    /// latest last played). Never used for ownership.
    /// </summary>
    public static IReadOnlyDictionary<string, Entry> LoadHours(string steamRoot, bool allAccounts) =>
        LoadHours(steamRoot, ResolveActiveAccountId(steamRoot, ReadRegistryActiveAccountId()), allAccounts);

    internal static IReadOnlyDictionary<string, Entry> LoadHours(string steamRoot, string? activeAccountId, bool allAccounts)
    {
        var active = string.IsNullOrWhiteSpace(steamRoot) ? null : LoadAccount(steamRoot, activeAccountId);
        IReadOnlyDictionary<string, Entry> activeOnly = active?.Entries ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!allAccounts || string.IsNullOrWhiteSpace(steamRoot)) return activeOnly;
        var maps = new List<IReadOnlyDictionary<string, Entry>>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.Combine(NormalizeRoot(steamRoot), "userdata")))
            {
                if (!IsSafeAccountId(Path.GetFileName(dir))) continue;
                if (LoadAccountEntries(Path.Combine(dir, "config", "localconfig.vdf")) is { } map) maps.Add(map);
            }
        }
        catch { /* userdata unreadable: the active account alone */ }
        return maps.Count == 0 ? activeOnly : MergeAccounts(maps);
    }

    /// <summary>Per game: the sum of each account's minutes and the latest last played.</summary>
    internal static IReadOnlyDictionary<string, Entry> MergeAccounts(IEnumerable<IReadOnlyDictionary<string, Entry>> accounts)
    {
        var merged = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            foreach (var (appId, entry) in account)
            {
                if (!merged.TryGetValue(appId, out var known))
                {
                    merged[appId] = entry;
                    continue;
                }
                var minutes = (int)Math.Min(int.MaxValue, (long)known.Minutes + entry.Minutes);
                var last = known.LastPlayedUtc;
                if (entry.LastPlayedUtc is not null && (last is null || entry.LastPlayedUtc > last)) last = entry.LastPlayedUtc;
                merged[appId] = new Entry(minutes, last);
            }
        }
        return merged;
    }

    private static readonly Dictionary<string, ((long, long) Stamp, IReadOnlyDictionary<string, Entry> Map)> AccountFiles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One account's entries, re-parsed only when Steam rewrote its file.</summary>
    private static IReadOnlyDictionary<string, Entry>? LoadAccountEntries(string config)
    {
        var info = new FileInfo(config);
        if (!info.Exists) return null;
        var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
        lock (Gate)
        {
            if (AccountFiles.TryGetValue(config, out var cached) && cached.Stamp == stamp) return cached.Map;
        }
        var text = ReadSharedText(config);
        if (text is null) return null;
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try { MergeFile(map, text); }
        catch { return null; }
        lock (Gate) AccountFiles[config] = (stamp, map);
        return map;
    }

    /// <summary>Returns a one-way tag for Steam's active local account only.</summary>
    internal static string? GetActiveAccountScope(string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return null;
        var accountId = ResolveActiveAccountId(steamRoot, ReadRegistryActiveAccountId());
        return IsSafeAccountId(accountId) ? HashAccountId(accountId!) : null;
    }

    /// <summary>Explicit account loader used by tests and trusted callers that
    /// already resolved Steam's active account. The identifier never leaves
    /// this process; cloud provenance uses <see cref="AccountSnapshot.AccountKey"/>.</summary>
    internal static AccountSnapshot? LoadAccount(string steamRoot, string? accountId)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || !IsSafeAccountId(accountId)) return null;
        var root = NormalizeRoot(steamRoot);
        var config = Path.Combine(root, "userdata", accountId!, "config", "localconfig.vdf");
        var info = new FileInfo(config);
        if (!info.Exists) return null;
        // Re-parse only when Steam rewrote the file. The old 20 s / 2 min
        // lifetime re-read it on a timer and enumerated processes per check.
        var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);

        lock (Gate)
        {
            if (_snapshot is not null &&
                string.Equals(_loadedRoot, root, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_loadedAccountId, accountId, StringComparison.Ordinal) &&
                _loadedStamp == stamp)
                return _snapshot;

            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            IReadOnlySet<string> appTickets;
            IReadOnlySet<string> family;
            try
            {
                var text = ReadSharedText(config);
                if (text is null) return null;
                MergeFile(map, text);
                appTickets = ParseAppTickets(text);
                family = ParseFamilyMembers(text);
            }
            catch { return null; }

            _snapshot = new AccountSnapshot(HashAccountId(accountId!), map, appTickets, family);
            _loadedRoot = root;
            _loadedAccountId = accountId;
            _loadedStamp = stamp;
            return _snapshot;
        }
    }

    /// <summary>Force reload after a long play session so Steam’s updated VDF is seen.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _snapshot = null;
            _loadedRoot = null;
            _loadedAccountId = null;
            _loadedStamp = default;
        }
    }

    internal static string? ReadSharedText(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(40);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    internal static string? ResolveActiveAccountId(string steamRoot, string? registryAccountId)
    {
        try
        {
            var userdata = Path.Combine(NormalizeRoot(steamRoot), "userdata");
            if (!Directory.Exists(userdata)) return null;
            var accounts = Directory.EnumerateDirectories(userdata)
                .Select(Path.GetFileName)
                .Where(IsSafeAccountId)
                .Where(id => File.Exists(Path.Combine(userdata, id!, "config", "localconfig.vdf")))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (IsSafeAccountId(registryAccountId) &&
                accounts.Contains(registryAccountId!, StringComparer.Ordinal))
                return registryAccountId;
            if (accounts.Length == 1) return accounts[0];
            // Steam closed (ActiveUser is 0): the account it signs in next is AutoLoginUser, else MostRecent.
            var recent = SteamLoginUsers.Current(
                SteamLoginUsers.Parse(ReadSharedText(SteamLoginUsers.PathFor(NormalizeRoot(steamRoot)))),
                0, SteamLoginUsers.ReadAutoLoginUser())?.AccountId;
            return recent is not null && accounts.Contains(recent, StringComparer.Ordinal) ? recent : null;
        }
        catch { return null; }
    }

    private static string? ReadRegistryActiveAccountId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            var value = key?.GetValue("ActiveUser");
            uint account = value switch
            {
                int signed => unchecked((uint)signed),
                uint unsigned => unsigned,
                long wide when wide is > 0 and <= uint.MaxValue => (uint)wide,
                string text when uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0,
            };
            return account == 0 ? null : account.ToString(CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    private static string NormalizeRoot(string steamRoot) =>
        steamRoot.Replace('/', Path.DirectorySeparatorChar).TrimEnd('\\', '/');

    private static bool IsSafeAccountId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 20 && value.All(char.IsDigit);

    private static string HashAccountId(string accountId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("steam-account\0" + accountId));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    internal static void MergeFile(Dictionary<string, Entry> map, string text)
    {
        foreach (Match m in AppIdOpenRegex().Matches(text))
        {
            var appId = m.Groups[1].Value;
            var open = m.Index + m.Length - 1; // '{'
            if (open < 0 || open >= text.Length || text[open] != '{') continue;
            var block = SliceBraceBlock(text, open);
            if (block is null) continue;

            var mins = ReadPlaytimeMinutes(block);
            var lastUnix = ReadInt(block, LastPlayedRegex());
            if (mins is null && lastUnix is null) continue;

            DateTimeOffset? last = null;
            if (lastUnix is > 0)
            {
                try { last = DateTimeOffset.FromUnixTimeSeconds(lastUnix.Value); }
                catch { /* */ }
            }

            if (map.TryGetValue(appId, out var existing))
            {
                var bestMins = Math.Max(existing.Minutes, mins ?? 0);
                var bestLast = existing.LastPlayedUtc;
                if (last is not null && (bestLast is null || last > bestLast))
                    bestLast = last;
                map[appId] = new Entry(bestMins, bestLast);
            }
            else
            {
                map[appId] = new Entry(mins ?? 0, last);
            }
        }
    }

    internal static IReadOnlySet<string> ParseAppTickets(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return result;

        var section = AppTicketsOpenRegex().Match(text);
        if (!section.Success) return result;
        var open = section.Index + section.Length - 1;
        if (open < 0 || open >= text.Length || text[open] != '{') return result;
        var block = SliceBraceBlock(text, open);
        if (block is null) return result;

        foreach (Match ticket in AppTicketEntryRegex().Matches(block))
            result.Add(ticket.Groups[1].Value);
        return result;
    }

    /// <summary>
    /// The account ids in the account's Steam Family, as Steam writes them to
    /// localconfig.vdf (<c>"FamilyGroup" { "groupid" "N" "members" { "0" {
    /// "accountid" "N" } } }</c>). A missing block or group 0 is no family.
    /// </summary>
    internal static IReadOnlySet<string> ParseFamilyMembers(string text)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        var section = FamilyGroupOpenRegex().Match(text ?? "");
        if (!section.Success) return members;
        var block = SliceBraceBlock(text!, section.Index + section.Length - 1);
        if (block is null || ReadInt(block, FamilyGroupIdRegex()) is not > 0) return members;
        foreach (Match member in FamilyMemberIdRegex().Matches(block))
            members.Add(member.Groups[1].Value);
        return members;
    }

    private static int? ReadPlaytimeMinutes(string block)
    {
        // PlaytimeForever is the lifetime counter. Bare "Playtime" is the
        // legacy lifetime field and must not lose to a 2-week key.
        var forever = ReadInt(block, PlaytimeForeverRegex());
        if (forever is > 0) return forever;
        return ReadInt(block, PlaytimeExactRegex());
    }

    private static int? ReadInt(string block, Regex re)
    {
        var m = re.Match(block);
        if (!m.Success) return null;
        return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    private static string? SliceBraceBlock(string text, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return text[openBrace..(i + 1)];
            }
        }
        return null;
    }

    [GeneratedRegex(@"""(\d{1,10})""\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdOpenRegex();

    [GeneratedRegex(@"""apptickets""\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppTicketsOpenRegex();

    [GeneratedRegex(@"(?m)^\s*""(\d{1,10})""\s+""[0-9a-f]+""\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppTicketEntryRegex();

    [GeneratedRegex(@"""FamilyGroup""\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex FamilyGroupOpenRegex();

    [GeneratedRegex(@"""groupid""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FamilyGroupIdRegex();

    [GeneratedRegex(@"""accountid""\s+""(\d{1,10})""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FamilyMemberIdRegex();

    [GeneratedRegex(@"""PlaytimeForever""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaytimeForeverRegex();

    [GeneratedRegex(@"""Playtime""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaytimeExactRegex();

    [GeneratedRegex(@"""LastPlayed""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LastPlayedRegex();
}
