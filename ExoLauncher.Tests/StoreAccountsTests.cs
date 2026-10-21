using System.Text;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways account switching and hours across accounts can go wrong:
/// - loginusers.vdf: a persona name is lost or the account name shown instead; MostRecent is
///   read from the wrong block; a brace inside a persona name ends a block early;
/// - the switch rewrite: anything but the MostRecent/AllowAutoLogin values changes (line
///   endings, other keys, spacing); a second account keeps MostRecent=1; a missing flag is
///   not added, or added in another style; an account not in the file is written anyway;
/// - current Steam: its AutoLogin key is missed, or the switch leaves it 0; RememberPassword 0
///   is not read, so an account Steam cannot sign in silently is started hidden;
/// - removal: a neighbor block, the outer braces or a line ending goes with it;
/// - the signed-in account: ActiveUser is ignored while Steam runs;
/// - Steam hours: accounts are maxed instead of summed; an older LastPlayed wins; with the
///   switch off another account's minutes leak in;
/// - Epic hours: scopes are maxed, not summed; the active account counts twice (live and
///   saved); with the switch off saved accounts leak in; the older one-account file is lost;
/// - Nile: the Amazon name is not found, or a token field is read as one; a folder without
///   a session (a signed-out or other account's) supplies the library.
/// </summary>
public sealed class StoreAccountsTests
{
    private const string LoginUsers =
        "\"users\"\r\n{\r\n" +
        "\t\"76561197960265729\"\r\n\t{\r\n" +
        "\t\t\"AccountName\"\t\t\"mainlogin\"\r\n" +
        "\t\t\"PersonaName\"\t\t\"Main {one}\"\r\n" +
        "\t\t\"RememberPassword\"\t\t\"1\"\r\n" +
        "\t\t\"AllowAutoLogin\"\t\t\"1\"\r\n" +
        "\t\t\"MostRecent\"\t\t\"1\"\r\n" +
        "\t\t\"Timestamp\"\t\t\"1700000000\"\r\n" +
        "\t}\r\n" +
        "\t\"76561197960265730\"\r\n\t{\r\n" +
        "\t\t\"AccountName\"\t\t\"altlogin\"\r\n" +
        "\t\t\"PersonaName\"\t\t\"Rocket alt\"\r\n" +
        "\t\t\"RememberPassword\"\t\t\"1\"\r\n" +
        "\t\t\"Timestamp\"\t\t\"1600000000\"\r\n" +
        "\t}\r\n}\r\n";

    [Fact]
    public void Loginusers_reads_persona_names_and_most_recent()
    {
        var accounts = SteamLoginUsers.Parse(LoginUsers);
        Assert.Equal(2, accounts.Count);
        Assert.Equal("Main {one}", accounts[0].PersonaName);
        Assert.True(accounts[0].MostRecent);
        Assert.Equal("1", accounts[0].AccountId);
        Assert.Equal("Rocket alt", accounts[1].PersonaName);
        Assert.False(accounts[1].MostRecent);
        Assert.NotEqual(accounts[0].Key, accounts[1].Key);
        Assert.DoesNotContain("7656", accounts[0].Key, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_account_prefers_active_user_then_auto_login_then_most_recent()
    {
        var accounts = SteamLoginUsers.Parse(LoginUsers);
        Assert.Equal("Rocket alt", SteamLoginUsers.Current(accounts, 2, "mainlogin")?.PersonaName);
        Assert.Equal("Rocket alt", SteamLoginUsers.Current(accounts, 0, "ALTLOGIN")?.PersonaName);
        Assert.Equal("Main {one}", SteamLoginUsers.Current(accounts, 0, null)?.PersonaName);
    }

    [Fact]
    public void Switch_rewrite_changes_only_the_flags()
    {
        var next = SteamLoginUsers.SetMostRecent(LoginUsers, "76561197960265730");
        Assert.NotNull(next);
        var expected = LoginUsers
            .Replace("\"MostRecent\"\t\t\"1\"", "\"MostRecent\"\t\t\"0\"", StringComparison.Ordinal)
            .Replace(
                "\t\t\"Timestamp\"\t\t\"1600000000\"\r\n",
                "\t\t\"Timestamp\"\t\t\"1600000000\"\r\n\t\t\"MostRecent\"\t\t\"1\"\r\n\t\t\"AllowAutoLogin\"\t\t\"1\"\r\n",
                StringComparison.Ordinal);
        Assert.Equal(expected, next);

        var accounts = SteamLoginUsers.Parse(next);
        Assert.False(accounts[0].MostRecent);
        Assert.True(accounts[0].AllowAutoLogin);
        Assert.True(accounts[1].MostRecent);
        Assert.True(accounts[1].AllowAutoLogin);
    }

    [Fact]
    public void Switch_rewrite_back_is_byte_identical_and_unknown_account_is_refused()
    {
        var once = SteamLoginUsers.SetMostRecent(LoginUsers, "76561197960265729");
        Assert.Equal(LoginUsers, once);
        Assert.Equal(Encoding.UTF8.GetBytes(LoginUsers), Encoding.UTF8.GetBytes(once!));
        Assert.Null(SteamLoginUsers.SetMostRecent(LoginUsers, "76561197960265799"));
    }

    // Current Steam writes AutoLogin instead of AllowAutoLogin and leaves out MostRecent.
    private const string CurrentSteamLoginUsers =
        "\"users\"\n{\n" +
        "\t\"76561197960265729\"\n\t{\n" +
        "\t\t\"AccountName\"\t\t\"mainlogin\"\n" +
        "\t\t\"PersonaName\"\t\t\"Main\"\n" +
        "\t\t\"RememberPassword\"\t\t\"1\"\n" +
        "\t\t\"AutoLogin\"\t\t\"1\"\n" +
        "\t\t\"timestamp\"\t\t\"1700000000\"\n" +
        "\t}\n" +
        "\t\"76561197960265730\"\n\t{\n" +
        "\t\t\"AccountName\"\t\t\"altlogin\"\n" +
        "\t\t\"PersonaName\"\t\t\"Alt\"\n" +
        "\t\t\"RememberPassword\"\t\t\"0\"\n" +
        "\t\t\"AutoLogin\"\t\t\"0\"\n" +
        "\t\t\"timestamp\"\t\t\"1600000000\"\n" +
        "\t}\n}\n";

    [Fact]
    public void Current_steam_auto_login_and_remembered_are_read()
    {
        var accounts = SteamLoginUsers.Parse(CurrentSteamLoginUsers);
        Assert.True(accounts[0].AllowAutoLogin);
        Assert.True(accounts[0].Remembered);
        Assert.False(accounts[1].AllowAutoLogin);
        Assert.False(accounts[1].Remembered);
        // No RememberPassword line (older files) counts as remembered.
        Assert.True(SteamLoginUsers.Parse(LoginUsers.Replace("\t\t\"RememberPassword\"\t\t\"1\"\r\n", "", StringComparison.Ordinal))[0].Remembered);
    }

    [Fact]
    public void Switch_on_current_steam_sets_its_auto_login_and_keeps_line_endings()
    {
        var next = SteamLoginUsers.SetMostRecent(CurrentSteamLoginUsers, "76561197960265730")!;
        Assert.DoesNotContain("\r", next, StringComparison.Ordinal);
        var alt = SteamLoginUsers.Parse(next)[1];
        Assert.True(alt.MostRecent);
        Assert.True(alt.AllowAutoLogin);
        Assert.Contains("\t\t\"AutoLogin\"\t\t\"1\"\n\t\t\"timestamp\"\t\t\"1600000000\"", next, StringComparison.Ordinal);
        // The other account keeps its own AutoLogin; Steam signs in AutoLoginUser.
        Assert.True(SteamLoginUsers.Parse(next)[0].AllowAutoLogin);
        Assert.False(SteamLoginUsers.Parse(next)[0].MostRecent);
    }

    [Fact]
    public void Remove_takes_out_one_block_and_nothing_else()
    {
        var removed = SteamLoginUsers.RemoveAccount(LoginUsers, "76561197960265730");
        var expected = LoginUsers[..LoginUsers.IndexOf("\t\"76561197960265730\"", StringComparison.Ordinal)] + "}\r\n";
        Assert.Equal(expected, removed);
        Assert.Single(SteamLoginUsers.Parse(removed));

        var first = SteamLoginUsers.RemoveAccount(CurrentSteamLoginUsers, "76561197960265729")!;
        Assert.Equal("\"users\"\n{\n" + CurrentSteamLoginUsers[CurrentSteamLoginUsers.IndexOf("\t\"76561197960265730\"", StringComparison.Ordinal)..], first);
        Assert.Equal("Alt", Assert.Single(SteamLoginUsers.Parse(first)).PersonaName);

        Assert.Null(SteamLoginUsers.RemoveAccount(LoginUsers, "76561197960265799"));
    }

    [Fact]
    public void Steam_hours_sum_across_accounts_only_when_on()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-accounts-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteLocalConfig(root, "11", "\"252950\" { \"PlaytimeForever\" \"600\" \"LastPlayed\" \"1700000000\" }");
            WriteLocalConfig(root, "22", "\"252950\" { \"PlaytimeForever\" \"28200\" \"LastPlayed\" \"1600000000\" } \"730\" { \"PlaytimeForever\" \"5\" }");

            var off = SteamPlaytime.LoadHours(root, "11", allAccounts: false);
            Assert.Equal(600, off["252950"].Minutes);
            Assert.False(off.ContainsKey("730"));

            var on = SteamPlaytime.LoadHours(root, "11", allAccounts: true);
            Assert.Equal(28800, on["252950"].Minutes);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), on["252950"].LastPlayedUtc);
            Assert.Equal(5, on["730"].Minutes);
        }
        finally
        {
            SteamPlaytime.Invalidate();
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Steam_merge_keeps_the_latest_last_played()
    {
        var older = DateTimeOffset.FromUnixTimeSeconds(1600000000);
        var newer = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var merged = SteamPlaytime.MergeAccounts([
            new Dictionary<string, SteamPlaytime.Entry> { ["1"] = new(10, newer) },
            new Dictionary<string, SteamPlaytime.Entry> { ["1"] = new(20, older) },
        ]);
        Assert.Equal(new SteamPlaytime.Entry(30, newer), merged["1"]);
    }

    [Fact]
    public void Epic_hours_sum_scopes_only_when_on_and_count_the_active_account_once()
    {
        var active = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Sugar"] = 120 };
        var saved = new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["aaaa"] = new Dictionary<string, int> { ["Sugar"] = 100 }, // the active account's older reading
            ["bbbb"] = new Dictionary<string, int> { ["Sugar"] = 28000, ["Fortnite"] = 3 },
        };

        Assert.Same(active, EpicPlaytime.HoursFor("aaaa", active, saved, allAccounts: false));
        var all = EpicPlaytime.HoursFor("aaaa", active, saved, allAccounts: true);
        Assert.Equal(28120, all["Sugar"]);
        Assert.Equal(3, all["Fortnite"]);
    }

    [Fact]
    public void Epic_saved_readings_read_both_file_shapes()
    {
        var legacy = EpicPlaytime.ParsePersistedScopes("{\"Scope\":\"aaaa\",\"Minutes\":{\"Sugar\":42}}");
        Assert.Equal(42, legacy["aaaa"]["Sugar"]);

        var current = EpicPlaytime.ParsePersistedScopes(
            "{\"Scopes\":{\"aaaa\":{\"Sugar\":42},\"bbbb\":{\"Sugar\":7,\"Bad\":-1}}}");
        Assert.Equal(2, current.Count);
        Assert.Equal(7, current["bbbb"]["Sugar"]);
        Assert.False(current["bbbb"].ContainsKey("Bad"));
        Assert.Empty(EpicPlaytime.ParsePersistedScopes("not json"));
    }

    [Fact]
    public void Legendary_identity_reads_name_and_id_but_not_tokens()
    {
        var (id, name) = EpicPlaytime.ReadUserIdentity(
            "{\"account_id\":\"abc\",\"displayName\":\"Player\",\"access_token\":\"secret\"}");
        Assert.Equal("abc", id);
        Assert.Equal("Player", name);
    }

    [Fact]
    public void Nile_display_name_comes_from_customer_info()
    {
        var files = new Dictionary<string, string>
        {
            [Path.Combine("a", "user.json")] = "{\"tokens\":{\"bearer\":{\"access_token\":\"x\"}}}",
            [Path.Combine("b", "user.json")] = "{\"extensions\":{\"customer_info\":{\"given_name\":\"Sam\",\"name\":\"Sam Lee\"}}}",
        };
        Assert.Equal("Sam", NileCli.ReadDisplayName(["a", "b"], path => files.GetValueOrDefault(path)));
        Assert.Null(NileCli.ReadDisplayName(["a"], path => files.GetValueOrDefault(path)));
    }

    [Fact]
    public void Nile_library_comes_only_from_the_signed_in_folder()
    {
        const string oldLibrary = "[{\"product\":{\"id\":\"old\",\"title\":\"Old account game\"}}]";
        var files = new Dictionary<string, string>
        {
            // Exo's Nile folder: signed out, but the last account's library is still there.
            [Path.Combine("own", "library.json")] = oldLibrary,
            // Heroic's folder: signed in, with its own library.
            [Path.Combine("heroic", "current_user.json")] = "{\"user_id\":\"u1\"}",
            [Path.Combine("heroic", "library.json")] = "[{\"product\":{\"id\":\"new\",\"title\":\"Signed-in game\"}}]",
        };
        string? Read(string path) => files.GetValueOrDefault(path);

        Assert.Equal("heroic", NileCli.SessionRoot(["own", "heroic"], files.ContainsKey, Read));
        var rows = NileCli.ReadCachedLibrary(["own", "heroic"], files.ContainsKey, Read);
        Assert.Equal("new", Assert.Single(rows).ProductId);

        // No session anywhere: no library, even though one is cached.
        Assert.Empty(NileCli.ReadCachedLibrary(["own"], files.ContainsKey, Read));
        Assert.False(NileCli.HasLocalSession(["own"], files.ContainsKey, Read));
    }

    private static void WriteLocalConfig(string root, string accountId, string apps)
    {
        var dir = Path.Combine(root, "userdata", accountId, "config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "localconfig.vdf"),
            "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { " + apps + " } } } } }");
    }
}
