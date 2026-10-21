using System.Text;
using ExoLauncher.Adapters;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Riot account switching can go wrong:
/// - identity: a session without a refresh token (no Stay signed in) is saved as switchable;
///   the name is taken from the wrong claim; a malformed id_token throws instead of reading as none;
/// - saving: the copy is not DPAPI-wrapped (a token readable in Exo's folder), the PUUID becomes
///   the key the UI sees, Sessions files are left out, a newer sign-in does not replace the older copy;
/// - restoring: another account's Sessions files survive the swap; a saved path outside the Riot
///   Data folder (or a non-session file) is written; an unknown key clears the live session;
/// - VALORANT: its LastKnownUser is misread, so the wrong account shows as the one it last used.
/// All files are temp copies; nothing reads or writes the Riot Client's own folder.
/// </summary>
public sealed class RiotAccountsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-riot-accounts-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "Data");
    private string Store => Path.Combine(_root, "store");

    public RiotAccountsTests() => Directory.CreateDirectory(Data);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private static string B64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Yaml(string puuid, string? gameName, string? tag, bool refresh = true, string refreshValue = "rt-secret-value")
    {
        var acct = gameName is null ? "" : $",\"acct\":{{\"game_name\":\"{gameName}\",\"tag_line\":\"{tag}\"}}";
        var jwt = B64Url("{\"alg\":\"RS256\"}") + "." + B64Url($"{{\"sub\":\"{puuid}\"{acct},\"exp\":1}}") + ".c2ln";
        return "psl:\n    authorization:\n        riot-client:\n" +
               "            claims: \"\"\n" +
               $"            id_token: \"{jwt}\"\n" +
               "            is_dpop_bound: false\n" +
               (refresh ? $"            refresh_token: \"{refreshValue}\"\n" : "") +
               "riot-login:\n    persist: null\n";
    }

    private void WriteLive(string yaml, params (string Path, string Text)[] sessions)
    {
        File.WriteAllText(Path.Combine(Data, RiotAccounts.SettingsFile), yaml);
        foreach (var (path, text) in sessions)
        {
            var full = Path.Combine(Data, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
    }

    [Fact]
    public void Identity_needs_a_refresh_token_and_reads_the_riot_id()
    {
        var identity = RiotAccounts.ReadIdentity(Yaml("puuid-a", "Erix", "NA1"));
        Assert.Equal("puuid-a", identity?.Puuid);
        Assert.Equal("Erix#NA1", identity?.Name);
        Assert.DoesNotContain("puuid", identity!.Key, StringComparison.Ordinal);
        Assert.True(RiotAccounts.IsKey(identity.Key));

        Assert.Null(RiotAccounts.ReadIdentity(Yaml("puuid-a", "Erix", "NA1", refresh: false)));
        Assert.Null(RiotAccounts.ReadIdentity(Yaml("puuid-a", "Erix", "NA1", refreshValue: "")));
        Assert.Equal("Riot account", RiotAccounts.ReadIdentity(Yaml("puuid-a", null, null))?.Name);
        Assert.Null(RiotAccounts.ReadIdentity("psl:\n  id_token: \"not-a-jwt\"\n  refresh_token: \"x\"\n"));
        Assert.Null(RiotAccounts.ReadIdentity("psl:\n  id_token: \"a.@@@.c\"\n  refresh_token: \"x\"\n"));
        Assert.Null(RiotAccounts.ReadIdentity(null));
    }

    [Fact]
    public void Save_and_restore_swap_exactly_the_session_files()
    {
        WriteLive(Yaml("puuid-a", "Main", "NA1", refreshValue: "rt-a"), ("Sessions/a.dat", "session a"));
        var a = RiotAccounts.SaveLive(Data, Store);
        Assert.Equal("Main#NA1", a?.Name);

        // The user signs in to a second account in the Riot Client.
        RiotAccounts.ClearLive(Data);
        Assert.False(File.Exists(Path.Combine(Data, RiotAccounts.SettingsFile)));
        Assert.False(Directory.Exists(Path.Combine(Data, "Sessions")));
        WriteLive(Yaml("puuid-b", "Alt", "EUW", refreshValue: "rt-b"), ("Cookies/c.dat", "cookie b"));
        var b = RiotAccounts.SaveLive(Data, Store);

        var saved = RiotAccounts.List(Store);
        Assert.Equal(["Alt#EUW", "Main#NA1"], saved.Select(account => account.Name));
        // Exo's copies are DPAPI-wrapped: no token, PUUID or name in the clear.
        foreach (var file in Directory.EnumerateFiles(Store))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("rt-", text, StringComparison.Ordinal);
            Assert.DoesNotContain("puuid", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Main", text, StringComparison.Ordinal);
        }

        Assert.True(RiotAccounts.Restore(Data, Store, a!.Key));
        Assert.Equal("puuid-a", RiotAccounts.ReadLive(Data)?.Puuid);
        Assert.Equal("session a", File.ReadAllText(Path.Combine(Data, "Sessions", "a.dat")));
        Assert.False(Directory.Exists(Path.Combine(Data, "Cookies")));

        Assert.True(RiotAccounts.Restore(Data, Store, b!.Key));
        Assert.Equal("cookie b", File.ReadAllText(Path.Combine(Data, "Cookies", "c.dat")));
        Assert.False(Directory.Exists(Path.Combine(Data, "Sessions")));
    }

    [Fact]
    public void A_newer_sign_in_replaces_the_older_copy()
    {
        WriteLive(Yaml("puuid-a", "Main", "NA1", refreshValue: "rt-old"));
        var a = RiotAccounts.SaveLive(Data, Store)!;
        WriteLive(Yaml("puuid-a", "Main", "NA1", refreshValue: "rt-rotated"));
        RiotAccounts.SaveLive(Data, Store);
        Assert.Single(RiotAccounts.List(Store));

        RiotAccounts.ClearLive(Data);
        Assert.True(RiotAccounts.Restore(Data, Store, a.Key));
        Assert.Contains("rt-rotated", File.ReadAllText(Path.Combine(Data, RiotAccounts.SettingsFile)), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_saved_without_stay_signed_in_and_unknown_keys_leave_the_session()
    {
        WriteLive(Yaml("puuid-a", "Main", "NA1", refresh: false));
        Assert.Null(RiotAccounts.SaveLive(Data, Store));
        Assert.Empty(RiotAccounts.List(Store));

        Assert.False(RiotAccounts.Restore(Data, Store, "000000000000000000000000"));
        Assert.False(RiotAccounts.Restore(Data, Store, "../escape"));
        Assert.True(File.Exists(Path.Combine(Data, RiotAccounts.SettingsFile)));
        Assert.False(RiotAccounts.Remove(Store, "../escape"));
    }

    [Fact]
    public void A_saved_path_outside_the_session_files_is_refused()
    {
        foreach (var bad in new[] { "../outside.txt", "Config/RiotClientSettings.yaml", "Sessions" })
        {
            var yaml = Convert.ToBase64String(Encoding.UTF8.GetBytes(Yaml("puuid-x", "X", "1")));
            var json = $"{{\"v\":1,\"puuid\":\"puuid-x\",\"name\":\"X#1\",\"files\":{{\"{RiotAccounts.SettingsFile}\":\"{yaml}\",\"{bad}\":\"AA==\"}}}}";
            Directory.CreateDirectory(Store);
            var key = RiotAccounts.KeyFor("puuid-x");
            File.WriteAllBytes(Path.Combine(Store, key + ".bin"), ExoDpapi.Protect(Encoding.UTF8.GetBytes(json)));
            WriteLive(Yaml("puuid-live", "Live", "1"));

            Assert.False(RiotAccounts.Restore(Data, Store, key));
            Assert.Equal("puuid-live", RiotAccounts.ReadLive(Data)?.Puuid);
            Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
        }
    }

    [Fact]
    public void Remove_forgets_only_that_account()
    {
        WriteLive(Yaml("puuid-a", "Main", "NA1"));
        var a = RiotAccounts.SaveLive(Data, Store)!;
        WriteLive(Yaml("puuid-b", "Alt", "NA1"));
        RiotAccounts.SaveLive(Data, Store);
        Assert.True(RiotAccounts.Remove(Store, a.Key));
        Assert.Equal("Alt#NA1", Assert.Single(RiotAccounts.List(Store)).Name);
        Assert.False(RiotAccounts.Remove(Store, a.Key));
    }

    [Fact]
    public void Valorant_last_known_user_is_read()
    {
        Assert.Equal("049a5986-0000-5cad-ac2d-5da721e52973",
            RiotAccounts.ReadValorantLastUser("[UserInfo]\r\nLastKnownUser=049a5986-0000-5cad-ac2d-5da721e52973\r\n"));
        Assert.Null(RiotAccounts.ReadValorantLastUser("[UserInfo]\r\nLastKnownUser=\r\n"));
        Assert.Null(RiotAccounts.ReadValorantLastUser(null));
    }
}
