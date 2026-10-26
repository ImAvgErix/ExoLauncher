using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Steam's outage handling could go wrong, silently:
/// - the logs: one dropped connection Steam gets back in seconds (a normal CM rebalance, even one 'Service Unavailable'
///   kick) reads as an outage and the notice nags; a real outage (logons refused for minutes) reads as normal; an account
///   answer (Access Denied) reads as an outage, though offline mode doesn't help; a stale outage from an earlier Steam
///   run (before this one started) still counts; a closed Steam (session ended) still counts; Steam's own failed
///   internet check is called a server outage; the "Loading User Data" hang (signed on, library never ready) is missed,
///   or a sign-in that is merely a few seconds in is called stuck;
/// - the decision: a signed-in Steam (its games start) is restarted for nothing; Steam is restarted while a game runs;
///   offline mode is offered without a saved sign-in (it can't start), or offered again when Steam is offline already;
/// - loginusers.vdf: any byte besides the two values changes (line endings, tabs, other keys, the other account's
///   block); the put-back leaves a "1" or an added line behind, or drops a line Steam had; another account is switched;
///   the copy and record are left behind after the put-back, or a second switch overwrites the first values.
/// </summary>
public sealed class SteamOfflineTests
{
    // Steam's connection_log.txt during the 2026-10-13 outage on the developer's PC (account id and address replaced).
    private const string OutageLog = """
        [2026-10-13 09:26:58] [Logged On, 4, 7] [U:1:12345] RecvMsgClientLogOnResponse() : [U:1:12345] 'OK'
        [2026-10-13 09:26:58] [Logged On, 4, 7] [U:1:12345] RecvMsgClientLogOnResponse() : processing complete
        [2026-10-13 17:01:22] [Logged Off, 4, 0] [U:1:12345] ConnectionDisconnected('Disconnected By Remote Host') : 'OK' (162.254.199.184:27018, WebSocket)
        [2026-10-13 17:01:22] [Logged Off, 4, 0] [U:1:12345] StartAutoReconnect() will start in 18.0 seconds (attempt 1)
        [2026-10-13 17:01:22] [Logged Off, 4, 0] [U:1:12345] Sending SteamServersDisconnected_t because we were logged on
        [2026-10-13 17:01:48] [Connecting, 4, 0] [U:1:12345] ConnectFailed('Connection Failed':1f6) (0.0.0.0:0, WebSocket)
        [2026-10-13 17:01:48] [Logged Off, 4, 0] [U:1:12345] StartAutoReconnect() will start in 13.0 seconds (attempt 2)
        [2026-10-13 17:01:49] Connectivity test: result=Connected (since 27295.2s ago), prev=Unknown, in progress=0
        [2026-10-13 17:02:02] [Connected, 4, 2] [U:1:12345] Logging on [U:1:12345]
        [2026-10-13 17:02:02] [Logging On, 4, 2] [U:1:12345] RecvMsgClientLogOnResponse() : [I:0:0] 'Service Unavailable'
        [2026-10-13 17:02:02] [Logged Off, 4, 2] [U:1:12345] RecvMsgClientLogOnResponse() : processing complete
        [2026-10-13 17:02:03] [Logged Off, 4, 0] [U:1:12345] ConnectionDisconnected('Disconnected By Remote Host') : 'Service Unavailable' (162.254.192.101:27017, UDP)
        [2026-10-13 17:02:03] [Logged Off, 4, 0] [U:1:12345] StartAutoReconnect() will start in 30.0 seconds (attempt 3)
        [2026-10-13 17:02:03] [Logged Off, 4, 0] [U:1:12345] Sending SteamServerConnectFailure_t Service Unavailable Reconnect
        [2026-10-13 17:02:38] [Logging On, 4, 15] [U:1:12345] RecvMsgClientLogOnResponse() : [I:0:0] 'Service Unavailable'
        [2026-10-13 17:02:39] [Logged Off, 4, 0] [U:1:12345] ConnectionDisconnected('Disconnected By Remote Host') : 'Service Unavailable' (162.254.199.165:443, WebSocket)
        [2026-10-13 17:02:39] [Logged Off, 4, 0] [U:1:12345] StartAutoReconnect() will start in 43.0 seconds (attempt 4)
        [2026-10-13 17:03:32] [Logging On, 4, 19] [U:1:12345] RecvMsgClientLogOnResponse() : [I:0:0] 'Service Unavailable'
        [2026-10-13 17:03:33] [Logged Off, 4, 0] [U:1:12345] StartAutoReconnect() will start in 57.0 seconds (attempt 5)
        [2026-10-13 17:04:32] [Logging On, 4, 23] [U:1:12345] RecvMsgClientLogOnResponse() : [I:0:0] 'Service Unavailable'
        [2026-10-13 17:04:33] [Logged Off, 4, 0] [U:1:12345] StartAutoReconnect() will start in 420.0 seconds (attempt 6)
        """;

    // The same Steam signing in again at 17:11, and two normal reconnects later that day and the next.
    private const string RecoveredLog = OutageLog + "\n" + """
        [2026-10-13 17:11:34] [Connected, 4, 27] [U:1:12345] Logging on [U:1:12345]
        [2026-10-13 17:11:34] [Logging On, 4, 27] [U:1:12345] RecvMsgClientLogOnResponse() : [U:1:12345] 'OK'
        [2026-10-13 17:11:34] [Logged On, 4, 27] [U:1:12345] RecvMsgClientLogOnResponse() : processing complete
        """;

    private const string KickedAndBackLog = """
        [2026-10-13 17:11:34] [Logging On, 4, 27] [U:1:12345] RecvMsgClientLogOnResponse() : [U:1:12345] 'OK'
        [2026-10-13 22:32:44] [Logged On, 4, 27] [U:1:12345] RecvMsgClientLoggedOff('Service Unavailable')
        [2026-10-13 22:32:44] [Logged Off, 4, 0] [U:1:12345] ConnectionDisconnected('Disconnected By Remote Host') : 'Service Unavailable' (162.254.194.56:443, WebSocket)
        [2026-10-13 22:32:44] [Logged Off, 4, 0] [U:1:12345] StartAutoReconnect() will start in 7.0 seconds (attempt 1)
        [2026-10-13 22:32:52] [Logging On, 4, 31] [U:1:12345] RecvMsgClientLogOnResponse() : [U:1:12345] 'OK'
        [2026-10-14 02:07:16] [Logged Off, 4, 0] [U:1:12345] ConnectionDisconnected('Disconnected By Remote Host') : 'OK' (162.254.199.184:27018, WebSocket)
        [2026-10-14 02:07:16] [Logged Off, 4, 0] [U:1:12345] StartAutoReconnect() will start in 4.0 seconds (attempt 1)
        [2026-10-14 02:07:21] [Logging On, 4, 32] [U:1:12345] RecvMsgClientLogOnResponse() : [U:1:12345] 'OK'
        """;

    // steamui_login.txt: a normal sign-in, and one that stops at "Loading User Data" (WaitingForLibraryReady).
    private const string SignedInLogin = """
        [2026-10-26 12:25:10] [ None ] Starting login
        [2026-10-26 12:25:10] [ None ] SetLoginState: WaitingForNetwork - OK
        [2026-10-26 12:25:10] [ WaitingForNetwork ] SetLoginState: WaitingForServerResponse - OK
        [2026-10-26 12:25:11] [ WaitingForServerResponse ] Received logon success response
        [2026-10-26 12:25:11] [ WaitingForServerResponse ] SetLoginState: WaitingForLibraryReady - OK
        [2026-10-26 12:25:11] [ WaitingForLibraryReady ] Received post-logon callback: L=0,P=1,A=1,R=0,C=1,I=0,CM=0
        [2026-10-26 12:25:11] [ WaitingForLibraryReady ] SetLoginState: Success - OK
        """;

    private const string LoadingUserDataLogin = """
        [2026-10-26 12:25:10] [ None ] Starting login
        [2026-10-26 12:25:10] [ None ] SetLoginState: WaitingForNetwork - OK
        [2026-10-26 12:25:10] [ WaitingForNetwork ] SetLoginState: WaitingForServerResponse - OK
        [2026-10-26 12:25:11] [ WaitingForServerResponse ] Received logon success response
        [2026-10-26 12:25:11] [ WaitingForServerResponse ] SetLoginState: WaitingForLibraryReady - OK
        [2026-10-26 12:25:11] [ WaitingForLibraryReady ] Received post-logon callback: L=1,P=1,A=1,R=0,C=1,I=0,CM=0
        """;

    private const string LoggedOnAt1225 = """
        [2026-10-26 12:25:08] Connectivity test: result=Connected (since 0.0s ago), prev=Unknown, in progress=0
        [2026-10-26 12:25:11] [Logging On, 4, 7] [U:1:12345] RecvMsgClientLogOnResponse() : [U:1:12345] 'OK'
        """;

    private static readonly DateTime Oct13 = new(2026, 10, 13, 9, 0, 0);
    private static readonly DateTime Oct26 = new(2026, 10, 26, 12, 25, 0);
    private static DateTime At(string time) => DateTime.ParseExact(time, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void RefusedLogonsForMinutesAreAnOutage()
    {
        var reading = SteamOutage.Read(OutageLog, SignedInLogin.Replace("2026-10-26 12:25", "2026-10-13 09:26", StringComparison.Ordinal), Oct13);
        Assert.False(reading.Connected);
        Assert.True(reading.SignedIn);
        Assert.Equal(5, reading.Failures);
        Assert.Equal(At("2026-10-13 17:01:22"), reading.FailingSince);
        Assert.Equal(SteamOutage.Cause.Servers, SteamOutage.Judge(reading, At("2026-10-13 17:04:33")));
    }

    [Fact]
    public void TheFirstFailuresOfAnOutageAreNotYetOne()
    {
        var firstFailure = OutageLog[..(OutageLog.IndexOf("[2026-10-13 17:01:49]", StringComparison.Ordinal))];
        var reading = SteamOutage.Read(firstFailure, null, Oct13);
        Assert.Equal(1, reading.Failures);
        Assert.Equal(SteamOutage.Cause.None, SteamOutage.Judge(reading, At("2026-10-13 17:01:55")));
        // Two refusals within the minute: still not long enough to say so.
        var twoQuick = SteamOutage.Read(OutageLog[..(OutageLog.IndexOf("[2026-10-13 17:02:38]", StringComparison.Ordinal))], null, Oct13);
        Assert.Equal(2, twoQuick.Failures);
        Assert.Equal(SteamOutage.Cause.None, SteamOutage.Judge(twoQuick, At("2026-10-13 17:02:10")));
    }

    [Fact]
    public void SigningInAgainEndsTheOutage()
    {
        var reading = SteamOutage.Read(RecoveredLog, SignedInLogin, Oct13);
        Assert.True(reading.Connected);
        Assert.Equal(0, reading.Failures);
        Assert.Null(reading.FailingSince);
        Assert.Equal(SteamOutage.Cause.None, SteamOutage.Judge(reading, At("2026-10-13 17:15:00")));
    }

    [Fact]
    public void NormalReconnectsAreNotAnOutage()
    {
        // A 'Service Unavailable' kick and a dropped connection, each back within seconds.
        var reading = SteamOutage.Read(KickedAndBackLog, SignedInLogin, Oct13);
        Assert.True(reading.Connected);
        Assert.Equal(SteamOutage.Cause.None, SteamOutage.Judge(reading, At("2026-10-14 02:30:00")));

        // Mid-kick, before Steam's reconnect: one failure, not an outage even long after.
        var kicked = KickedAndBackLog[..KickedAndBackLog.IndexOf("[2026-10-13 22:32:52]", StringComparison.Ordinal)];
        Assert.Equal(SteamOutage.Cause.None, SteamOutage.Judge(SteamOutage.Read(kicked, null, Oct13), At("2026-10-13 22:40:00")));
    }

    [Fact]
    public void LinesFromBeforeSteamStartedDoNotCount()
    {
        // This Steam started after the outage: the outage's lines are an earlier run's.
        var reading = SteamOutage.Read(OutageLog, null, At("2026-10-13 18:00:00"));
        Assert.Equal(0, reading.Failures);
        Assert.Equal(SteamOutage.Cause.None, SteamOutage.Judge(reading, At("2026-10-13 18:05:00")));
    }

    [Fact]
    public void AccountAnswersAndClosingSteamAreNotAnOutage()
    {
        const string denied = """
            [2026-10-22 22:39:51] [Logging On, 4, 2] [U:1:12345] RecvMsgClientLogOnResponse() : [I:0:0] 'Access Denied'
            [2026-10-22 22:39:51] [Logged Off, 4, 0] [U:1:12345] ConnectionDisconnected('Disconnected By Remote Host') : 'Access Denied' (162.254.199.181:27017, UDP)
            [2026-10-22 22:39:51] [Logged Off, 4, 0] [U:1:12345] ConnectionDisconnected() not auto reconnecting due to Access Denied
            """;
        Assert.Equal(0, SteamOutage.Read(OutageLog + "\n" + denied, null, Oct13).Failures);

        const string closed = """
            [2026-10-25 11:59:14] [Logged On, 4, 7] [U:1:12345] LogOff()
            [2026-10-25 11:59:14] [Logged Off, 0, 0] [U:1:12345] ConnectionDisconnected('Disconnected By Remote Host') : 'OK' (162.254.199.165:443, WebSocket)
            [2026-10-25 11:59:14] [Logged Off, 0, 0] [U:1:12345] ConnectionDisconnected() not auto reconnecting due to user initiated logoff
            [2026-10-25 11:59:16] [Logged Off, 0, 0] [U:1:12345] Log session ended
            """;
        var reading = SteamOutage.Read(OutageLog + "\n" + closed, null, Oct13);
        Assert.Equal(0, reading.Failures);
        Assert.Null(reading.FailingSince);
        Assert.Equal(SteamOutage.Cause.None, SteamOutage.Judge(reading, At("2026-10-25 12:30:00")));
    }

    [Fact]
    public void SteamsOwnFailedInternetCheckIsTheInternet()
    {
        var reading = SteamOutage.Read(OutageLog.Replace("result=Connected (since 27295.2s ago), prev=Unknown",
            "result=Failed (since 0.0s ago), prev=Unknown", StringComparison.Ordinal), null, Oct13);
        Assert.True(reading.InternetDown);
        Assert.Equal(SteamOutage.Cause.Internet, SteamOutage.Judge(reading, At("2026-10-13 17:05:00")));
    }

    [Fact]
    public void LoadingUserDataThatNeverEndsIsAnOutage()
    {
        // Logged on fine, then the library never comes: today's "Loading User Data".
        var reading = SteamOutage.Read(LoggedOnAt1225, LoadingUserDataLogin, Oct26);
        Assert.True(reading.Connected);
        Assert.False(reading.SignedIn);
        Assert.Equal("WaitingForLibraryReady", reading.Step);
        Assert.Equal(SteamOutage.Cause.None, SteamOutage.Judge(reading, At("2026-10-26 12:25:40")));
        Assert.Equal(SteamOutage.Cause.Servers, SteamOutage.Judge(reading, At("2026-10-26 12:27:00")));
        // A launch that waited 45 s on it already says enough.
        Assert.Equal(SteamOutage.Cause.Servers, SteamOutage.Judge(reading, At("2026-10-26 12:25:56"), launchStalled: true));

        var normal = SteamOutage.Read(LoggedOnAt1225, SignedInLogin, Oct26);
        Assert.True(normal.SignedIn);
        Assert.Equal(SteamOutage.Cause.None, SteamOutage.Judge(normal, At("2026-10-26 13:00:00"), launchStalled: true));
    }

    [Fact]
    public void StuckWaitingForTheNetworkIsTheInternet()
    {
        const string login = """
            [2026-10-26 12:25:10] [ None ] Starting login
            [2026-10-26 12:25:10] [ None ] SetLoginState: WaitingForNetwork - OK
            """;
        Assert.Equal(SteamOutage.Cause.Internet, SteamOutage.Judge(SteamOutage.Read(null, login, Oct26), At("2026-10-26 12:27:00")));
        // Waiting for credentials is Steam's sign-in, not an outage.
        const string credentials = """
            [2026-10-22 22:39:51] [ WaitingForServerResponse ] SetLoginState: WaitingForCredentials - Access Denied
            """;
        Assert.Equal(SteamOutage.Cause.None, SteamOutage.Judge(SteamOutage.Read(null, credentials, Oct13), At("2026-10-22 23:00:00"), launchStalled: true));
    }

    [Theory]
    [InlineData("Servers", false, false, true, false, "PlayOffline")]
    [InlineData("Internet", false, false, true, false, "PlayOffline")]
    [InlineData("Servers", false, false, false, false, "NeedsSavedSignIn")]
    [InlineData("Servers", true, false, true, false, "None")]   // signed in: its games start
    [InlineData("Servers", false, true, true, false, "None")]   // offline already
    [InlineData("Servers", false, false, true, true, "None")]   // a game runs: never restart
    [InlineData("None", false, false, true, false, "None")]
    public void WhatExoOffers(string cause, bool signedIn, bool offline, bool savedSignIn, bool gameRunning, string offer) =>
        Assert.Equal(Enum.Parse<SteamOutage.Offer>(offer),
            SteamOutage.Decide(Enum.Parse<SteamOutage.Cause>(cause), signedIn, offline, savedSignIn, gameRunning));

    [Fact]
    public void TheNoticeSaysItPlainly()
    {
        Assert.Equal(("Steam's servers are down", "Your games still play offline."), SteamOutage.Notice(SteamOutage.Cause.Servers, SteamOutage.Offer.PlayOffline));
        Assert.Contains("saved sign-in", SteamOutage.Notice(SteamOutage.Cause.Servers, SteamOutage.Offer.NeedsSavedSignIn).Text, StringComparison.Ordinal);
        Assert.Equal("Steam can't connect", SteamOutage.Notice(SteamOutage.Cause.Internet, SteamOutage.Offer.PlayOffline).Title);
        Assert.Equal("Steam is back online", SteamOutage.BackNotice(exoSwitchedBack: true).Title);
        var answer = SteamOutage.PlayAnswer(SteamOutage.Cause.Servers, SteamOutage.Offer.PlayOffline);
        Assert.False(answer.Ok);
        Assert.Equal("steam", answer.OfflineStore);
        Assert.Equal("Steam's servers are down. Your games still play offline.", answer.Message);
        Assert.Null(SteamOutage.PlayAnswer(SteamOutage.Cause.Servers, SteamOutage.Offer.NeedsSavedSignIn).OfflineStore);
    }

    // Current Steam's file (LF, both values written as "0"), two accounts.
    private const string CurrentLoginUsers =
        "\"users\"\n{\n" +
        "\t\"76561197960265729\"\n\t{\n" +
        "\t\t\"AccountName\"\t\t\"mainlogin\"\n" +
        "\t\t\"PersonaName\"\t\t\"Main {one}\"\n" +
        "\t\t\"RememberPassword\"\t\t\"1\"\n" +
        "\t\t\"WantsOfflineMode\"\t\t\"0\"\n" +
        "\t\t\"SkipOfflineModeWarning\"\t\t\"0\"\n" +
        "\t\t\"AutoLogin\"\t\t\"1\"\n" +
        "\t\t\"MostRecent\"\t\t\"1\"\n" +
        "\t\t\"timestamp\"\t\t\"1793035511\"\n" +
        "\t}\n" +
        "\t\"76561197960265730\"\n\t{\n" +
        "\t\t\"AccountName\"\t\t\"altlogin\"\n" +
        "\t\t\"PersonaName\"\t\t\"Alt\"\n" +
        "\t\t\"RememberPassword\"\t\t\"1\"\n" +
        "\t\t\"WantsOfflineMode\"\t\t\"0\"\n" +
        "\t\t\"SkipOfflineModeWarning\"\t\t\"0\"\n" +
        "\t\t\"timestamp\"\t\t\"1793035000\"\n" +
        "\t}\n" +
        "}\n";

    // An older Steam's file (CRLF), without either value.
    private const string OlderLoginUsers =
        "\"users\"\r\n{\r\n" +
        "\t\"76561197960265729\"\r\n\t{\r\n" +
        "\t\t\"AccountName\"\t\t\"mainlogin\"\r\n" +
        "\t\t\"PersonaName\"\t\t\"Main\"\r\n" +
        "\t\t\"RememberPassword\"\t\t\"1\"\r\n" +
        "\t\t\"MostRecent\"\t\t\"1\"\r\n" +
        "\t\t\"Timestamp\"\t\t\"1700000000\"\r\n" +
        "\t}\r\n" +
        "}\r\n";

    [Fact]
    public void OfflineChangesOnlyTheTwoValuesOfThatAccount()
    {
        var (text, before) = SteamLoginUsers.SetOffline(CurrentLoginUsers, "76561197960265729")!.Value;
        Assert.Equal(new SteamLoginUsers.OfflineFlags("0", "0"), before);
        Assert.Equal(CurrentLoginUsers.Length, text.Length);
        var changed = Enumerable.Range(0, text.Length).Where(i => text[i] != CurrentLoginUsers[i]).ToArray();
        Assert.Equal(2, changed.Length);
        Assert.Contains("\t\t\"WantsOfflineMode\"\t\t\"1\"\n\t\t\"SkipOfflineModeWarning\"\t\t\"1\"\n\t\t\"AutoLogin\"", text, StringComparison.Ordinal);
        // The other account stays online.
        Assert.Equal(new SteamLoginUsers.OfflineFlags("0", "0"), SteamLoginUsers.ReadOffline(text, "76561197960265730"));
        Assert.Equal(new SteamLoginUsers.OfflineFlags("1", "1"), SteamLoginUsers.ReadOffline(text, "76561197960265729"));

        Assert.Equal(CurrentLoginUsers, SteamLoginUsers.RestoreOffline(text, "76561197960265729", before));
    }

    [Fact]
    public void MissingValuesAreAddedInTheFilesStyleAndRemovedAgain()
    {
        var (text, before) = SteamLoginUsers.SetOffline(OlderLoginUsers, "76561197960265729")!.Value;
        Assert.Equal(new SteamLoginUsers.OfflineFlags(null, null), before);
        Assert.Contains("\t\t\"Timestamp\"\t\t\"1700000000\"\r\n\t\t\"WantsOfflineMode\"\t\t\"1\"\r\n\t\t\"SkipOfflineModeWarning\"\t\t\"1\"\r\n\t}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\r", text, StringComparison.Ordinal);
        Assert.Equal(OlderLoginUsers, SteamLoginUsers.RestoreOffline(text, "76561197960265729", before));
    }

    [Fact]
    public void PutBackKeepsWhatSteamChangedMeanwhile()
    {
        var (text, before) = SteamLoginUsers.SetOffline(CurrentLoginUsers, "76561197960265729")!.Value;
        // Steam rewrote its file while it ran offline: a newer timestamp, and SkipOfflineModeWarning back to 0 on its own.
        var steam = text.Replace("1793035511", "1793099999", StringComparison.Ordinal)
            .Replace("\"SkipOfflineModeWarning\"\t\t\"1\"", "\"SkipOfflineModeWarning\"\t\t\"0\"", StringComparison.Ordinal);
        var back = SteamLoginUsers.RestoreOffline(steam, "76561197960265729", before)!;
        Assert.Equal(CurrentLoginUsers.Replace("1793035511", "1793099999", StringComparison.Ordinal), back);
        Assert.Null(SteamLoginUsers.SetOffline(CurrentLoginUsers, "76561197960265799"));
        Assert.Null(SteamLoginUsers.RestoreOffline(CurrentLoginUsers, "76561197960265799", before));
    }

    [Fact]
    public void SwitchAndPutBackOnACopyLeaveTheFileByteForByte()
    {
        var root = Path.Combine(TestDataDirectory.Root, "steam-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var path = SteamLoginUsers.PathFor(root);
        var original = System.Text.Encoding.UTF8.GetBytes(OlderLoginUsers);
        File.WriteAllBytes(path, original);
        var record = Path.Combine(PathHelper.AppDataDir, "steam-offline.json");
        var copy = Path.Combine(PathHelper.AppDataDir, "steam-offline-loginusers.vdf");
        try
        {
            Assert.Null(SteamLoginUsers.WriteOffline(root));
            Assert.Equal(original, File.ReadAllBytes(copy));
            Assert.Equal(new SteamLoginUsers.OfflineFlags("1", "1"), SteamLoginUsers.ReadOffline(File.ReadAllText(path), "76561197960265729"));
            var first = SteamLoginUsers.LoadOfflineSwitch();
            Assert.Equal(new SteamLoginUsers.OfflineFlags(null, null), first!.Before);

            // A second switch in the same outage keeps the first values.
            Assert.Null(SteamLoginUsers.WriteOffline(root));
            Assert.Equal(first, SteamLoginUsers.LoadOfflineSwitch());

            Assert.True(SteamLoginUsers.WriteOnline(root));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(File.Exists(record));
            Assert.False(File.Exists(copy));
            Assert.Null(SteamLoginUsers.LoadOfflineSwitch());
        }
        finally
        {
            File.Delete(record);
            File.Delete(copy);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NoSavedSignInMeansNoSwitch()
    {
        var root = Path.Combine(TestDataDirectory.Root, "steam-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var path = SteamLoginUsers.PathFor(root);
        var text = OlderLoginUsers.Replace("\"RememberPassword\"\t\t\"1\"", "\"RememberPassword\"\t\t\"0\"", StringComparison.Ordinal);
        File.WriteAllText(path, text);
        try
        {
            Assert.NotNull(SteamLoginUsers.WriteOffline(root));
            Assert.Equal(text, File.ReadAllText(path));
            Assert.Null(SteamLoginUsers.LoadOfflineSwitch());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
