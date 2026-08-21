using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam playtime + last played from userdata localconfig.vdf.
/// Reads only the active Steam account and brace-matches app blocks (nested
/// cloud keys are common). Never merge multiple people's userdata on a shared PC.
/// </summary>
internal static partial class SteamPlaytime
{
    private static readonly object Gate = new();
    private static AccountSnapshot? _snapshot;
    private static string? _loadedRoot;
    private static string? _loadedAccountId;
    private static DateTime _loadedUtc = DateTime.MinValue;

    public readonly record struct Entry(int Minutes, DateTimeOffset? LastPlayedUtc);
    public sealed record AccountSnapshot(
        string AccountKey,
        IReadOnlyDictionary<string, Entry> Entries,
        IReadOnlySet<string> AppTicketIds);

    public static int? TryGetMinutes(string steamRoot, string appId)
    {
        var e = TryGet(steamRoot, appId);
        return e is { Minutes: > 0 } ? e.Value.Minutes : null;
    }

    public static DateTimeOffset? TryGetLastPlayed(string steamRoot, string appId) =>
        TryGet(steamRoot, appId)?.LastPlayedUtc;

    public static Entry? TryGet(string steamRoot, string appId)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || string.IsNullOrWhiteSpace(appId))
            return null;
        var snapshot = LoadActiveAccount(steamRoot);
        return snapshot is not null && snapshot.Entries.TryGetValue(appId, out var e) ? e : null;
    }

    /// <summary>
    /// Positive, active-account evidence that Steam has issued an app ticket.
    /// Absence is deliberately treated as unknown rather than not owned because
    /// Steam does not guarantee a ticket for every dormant entitlement.
    /// </summary>
    public static bool HasActiveAppTicket(string steamRoot, string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsDigit)) return false;
        return LoadActiveAccount(steamRoot)?.AppTicketIds.Contains(appId) == true;
    }

    /// <summary>
    /// Positive evidence the active Steam account has this app in its local
    /// library: an app ticket, a localconfig Apps block, or a librarycache file.
    /// Absence stays unknown, never a negative ownership claim.
    /// </summary>
    public static bool HasActiveLibraryEvidence(string steamRoot, string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsDigit)) return false;
        var snapshot = LoadActiveAccount(steamRoot);
        if (snapshot is not null &&
            (snapshot.AppTicketIds.Contains(appId) || snapshot.Entries.ContainsKey(appId)))
            return true;
        return SteamAccountLibrary.HasCache(TryGetLibraryCacheDirectory(steamRoot), appId);
    }

    internal static string? TryGetLibraryCacheDirectory(string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return null;
        var root = NormalizeRoot(steamRoot);
        var accountId = ResolveActiveAccountId(root, ReadRegistryActiveAccountId());
        if (!IsSafeAccountId(accountId)) return null;
        var dir = Path.Combine(root, "userdata", accountId!, "config", "librarycache");
        return Directory.Exists(dir) ? dir : null;
    }

    internal static string? TryGetLocalConfigPath(string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return null;
        var root = NormalizeRoot(steamRoot);
        var accountId = ResolveActiveAccountId(root, ReadRegistryActiveAccountId());
        if (!IsSafeAccountId(accountId)) return null;
        var config = Path.Combine(root, "userdata", accountId!, "config", "localconfig.vdf");
        return File.Exists(config) ? config : null;
    }

    /// <summary>All app ids for the active Steam account only.</summary>
    public static IReadOnlyDictionary<string, Entry> LoadAll(string steamRoot) =>
        LoadActiveAccount(steamRoot)?.Entries ?? new Dictionary<string, Entry>();

    public static AccountSnapshot? LoadActiveAccount(string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return null;
        var root = NormalizeRoot(steamRoot);
        // Resolve before consulting the short-lived cache. The prior cache
        // check keyed only by Steam root, so a shared PC could keep displaying
        // the previous account's localconfig for up to two minutes.
        var accountId = ResolveActiveAccountId(root, ReadRegistryActiveAccountId());
        if (!IsSafeAccountId(accountId)) return null;
        lock (Gate)
        {
            if (_snapshot is not null &&
                string.Equals(_loadedRoot, root, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_loadedAccountId, accountId, StringComparison.Ordinal) &&
                DateTime.UtcNow - _loadedUtc < CacheLifetime())
                return _snapshot;
        }
        return LoadAccount(root, accountId);
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
        if (!File.Exists(config)) return null;

        lock (Gate)
        {
            if (_snapshot is not null &&
                string.Equals(_loadedRoot, root, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_loadedAccountId, accountId, StringComparison.Ordinal) &&
                DateTime.UtcNow - _loadedUtc < CacheLifetime())
                return _snapshot;

            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            IReadOnlySet<string> appTickets;
            try
            {
                var text = ReadSharedText(config);
                if (text is null) return null;
                MergeFile(map, text);
                appTickets = ParseAppTickets(text);
            }
            catch { return null; }

            _snapshot = new AccountSnapshot(HashAccountId(accountId!), map, appTickets);
            _loadedRoot = root;
            _loadedAccountId = accountId;
            _loadedUtc = DateTime.UtcNow;
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
            _loadedUtc = DateTime.MinValue;
        }
    }

    private static TimeSpan CacheLifetime() =>
        ProcessHelper.IsProcessRunning("steam") ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(2);

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
            return accounts.Length == 1 ? accounts[0] : null;
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

    [GeneratedRegex(@"""PlaytimeForever""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaytimeForeverRegex();

    [GeneratedRegex(@"""Playtime""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaytimeExactRegex();

    [GeneratedRegex(@"""LastPlayed""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LastPlayedRegex();
}
