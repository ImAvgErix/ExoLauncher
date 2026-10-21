using System.Runtime.InteropServices;
using System.Text;

namespace ExoLauncher.SteamIpc;

/// <summary>
/// <c>owned</c> action: asks the running, signed-in Steam client which of the
/// candidate app ids the current account holds a license for. Uses only the
/// public Steamworks ISteamClient / ISteamApps flat vtables, so no Web API key
/// is needed and nothing leaves the machine.
/// </summary>
internal static partial class Program
{
    // Public ISteamClient vtable (isteamclient.h, SteamClient017..023):
    //  0 CreateSteamPipe, 1 BReleaseSteamPipe, 2 ConnectToGlobalUser,
    //  3 CreateLocalUser, 4 ReleaseUser, 5 GetISteamUser, 6 GetISteamGameServer,
    //  7 SetLocalIPBinding, 8 GetISteamFriends, 9 GetISteamUtils,
    // 10 GetISteamMatchmaking, 11 GetISteamMatchmakingServers,
    // 12 GetISteamGenericInterface, 13 GetISteamUserStats,
    // 14 GetISteamGameServerStats, 15 GetISteamApps.
    internal const int ClientCreateSteamPipe = 0;
    internal const int ClientReleaseSteamPipe = 1;
    internal const int ClientConnectToGlobalUser = 2;
    internal const int ClientReleaseUser = 4;
    internal const int ClientGetISteamApps = 15;

    // Public ISteamApps vtable (isteamapps.h, STEAMAPPS_INTERFACE_VERSION008):
    //  0 BIsSubscribed, 1 BIsLowViolence, 2 BIsCybercafe, 3 BIsVACBanned,
    //  4 GetCurrentGameLanguage, 5 GetAvailableGameLanguages,
    //  6 BIsSubscribedApp(AppId_t), 7 BIsDlcInstalled(AppId_t),
    //  8 GetEarliestPurchaseUnixTime(AppId_t), 9 BIsSubscribedFromFreeWeekend().
    // BIsSubscribedFromFreeWeekend / BIsSubscribedFromFamilySharing (27) take
    // no app id: they describe the calling game only, so they cannot classify
    // arbitrary candidates from this out-of-game helper.
    internal const int AppsBIsSubscribedApp = 6;
    internal const int AppsGetEarliestPurchaseUnixTime = 8;
    internal const string SteamAppsVersion = "STEAMAPPS_INTERFACE_VERSION008";

    private static readonly string[] SteamClientVersions =
    [
        "SteamClient023",
        "SteamClient022",
        "SteamClient021",
        "SteamClient020",
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ReleaseUserFn(IntPtr self, int pipe, int user);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
    private delegate IntPtr GetISteamAppsFn(IntPtr self, int user, int pipe, string version);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool BIsSubscribedAppFn(IntPtr self, uint appId);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint GetEarliestPurchaseUnixTimeFn(IntPtr self, uint appId);

    /// <summary>
    /// <c>owned &lt;id,id,...&gt;</c> or <c>owned -</c> (ids on stdin, separated by
    /// commas/whitespace). Prints one JSON line:
    /// <c>{"owned":[...],"checked":N}</c>. Exit 0 = authoritative answer for
    /// every checked id; 3 = Steam not running / not signed in / layout mismatch.
    /// </summary>
    private static int RunOwned(string[] args)
    {
        var raw = args.Length >= 2 && args[1] != "-" && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1]
            : Console.In.ReadToEnd();
        var candidates = ParseAppIds(raw);
        if (candidates.Count == 0)
        {
            WriteOwned([], 0);
            return 0;
        }

        if (!TryConnectPublic(out var client, out var clientVt, out var pipe, out var user, out var apps))
            return 3;
        try
        {
            var appsVt = ReadVTable(apps, AppsGetEarliestPurchaseUnixTime + 1);
            var subscribed = Marshal.GetDelegateForFunctionPointer<BIsSubscribedAppFn>(appsVt[AppsBIsSubscribedApp]);
            var purchased = Marshal.GetDelegateForFunctionPointer<GetEarliestPurchaseUnixTimeFn>(
                appsVt[AppsGetEarliestPurchaseUnixTime]);

            // Sanity probe: app 7 is the Steam client itself and is licensed to
            // every account. A false answer means the pipe is not bound to a
            // signed-in user (or the vtable moved), so no answer is trustworthy.
            if (!subscribed(apps, 7))
            {
                Note("Steam IPC owned: ISteamApps did not report the Steam client license; refusing to answer.");
                return 3;
            }

            var verbose = args.Any(arg => arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase));
            var owned = new List<uint>();
            var unconfirmed = 0;
            foreach (var appId in candidates)
            {
                var isSubscribed = subscribed(apps, appId);
                var purchaseTime = isSubscribed ? purchased(apps, appId) : 0;
                if (verbose) Console.Error.WriteLine($"{appId} subscribed={isSubscribed} earliestPurchase={purchaseTime}");
                if (!isSubscribed) continue;
                // A zero earliest purchase time marks a license the account does
                // not hold itself (family share / borrowed / free-weekend
                // grant). Those are not the account's own library.
                if (purchaseTime == 0)
                {
                    unconfirmed++;
                    continue;
                }
                owned.Add(appId);
            }

            Note($"Steam IPC owned: checked={candidates.Count}; owned={owned.Count}; subscribed-without-purchase={unconfirmed}.");
            WriteOwned(owned, candidates.Count);
            return 0;
        }
        finally
        {
            ReleasePublic(client, clientVt, pipe, user);
        }
    }

    /// <summary>Every id is answered: a dropped id is a missing game. Exo sends batches.</summary>
    internal static List<uint> ParseAppIds(string? raw)
    {
        var result = new List<uint>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        var seen = new HashSet<uint>();
        foreach (var token in raw.Split([',', ' ', '\r', '\n', '\t', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!uint.TryParse(token, out var id) || id == 0) continue;
            if (seen.Add(id)) result.Add(id);
        }
        return result;
    }

    private static void WriteOwned(IReadOnlyList<uint> owned, int checkedCount)
    {
        var text = new StringBuilder("{\"owned\":[");
        for (var i = 0; i < owned.Count; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(owned[i]);
        }
        text.Append("],\"checked\":").Append(checkedCount).Append('}');
        Console.Out.WriteLine(text.ToString());
        Console.Out.Flush();
    }

    private static bool TryConnectPublic(
        out IntPtr client,
        out IntPtr[] clientVt,
        out int pipe,
        out int user,
        out IntPtr apps)
    {
        client = IntPtr.Zero;
        clientVt = [];
        pipe = 0;
        user = 0;
        apps = IntPtr.Zero;
        if (!TryLoadSteamclient(out var create))
            return false;

        foreach (var version in SteamClientVersions)
        {
            client = create(version, IntPtr.Zero);
            if (client == IntPtr.Zero) continue;
            Note($"Steam IPC owned: ISteamClient {version}.");
            break;
        }
        if (client == IntPtr.Zero)
        {
            Note("Steam IPC owned: no supported ISteamClient version.");
            return false;
        }

        clientVt = ReadVTable(client, ClientGetISteamApps + 1);
        var ok = false;
        try
        {
            pipe = Marshal.GetDelegateForFunctionPointer<CreateSteamPipeFn>(clientVt[ClientCreateSteamPipe])(client);
            if (pipe == 0)
            {
                Note("Steam IPC owned: CreateSteamPipe failed. Is Steam running?");
                return false;
            }
            user = Marshal.GetDelegateForFunctionPointer<ConnectToGlobalUserFn>(clientVt[ClientConnectToGlobalUser])(client, pipe);
            if (user == 0)
            {
                Note("Steam IPC owned: ConnectToGlobalUser failed. Is Steam signed in?");
                return false;
            }
            apps = Marshal.GetDelegateForFunctionPointer<GetISteamAppsFn>(clientVt[ClientGetISteamApps])(
                client, user, pipe, SteamAppsVersion);
            if (apps == IntPtr.Zero)
            {
                Note("Steam IPC owned: GetISteamApps returned null.");
                return false;
            }
            var rtti = ReadRttiName(apps);
            if (!rtti.Contains("SteamApps", StringComparison.Ordinal) && !rtti.StartsWith("empty", StringComparison.Ordinal) &&
                !rtti.StartsWith("no-col", StringComparison.Ordinal))
            {
                Note($"Steam IPC owned: ISteamApps layout mismatch (rtti={rtti}).");
                return false;
            }
            ok = true;
            return true;
        }
        finally
        {
            if (!ok)
            {
                ReleasePublic(client, clientVt, pipe, user);
                apps = IntPtr.Zero;
                pipe = user = 0;
            }
        }
    }

    private static void ReleasePublic(IntPtr client, IntPtr[] clientVt, int pipe, int user)
    {
        if (client == IntPtr.Zero || pipe == 0 || clientVt.Length <= ClientReleaseUser) return;
        try
        {
            if (user != 0)
                Marshal.GetDelegateForFunctionPointer<ReleaseUserFn>(clientVt[ClientReleaseUser])(client, pipe, user);
            Marshal.GetDelegateForFunctionPointer<ReleaseSteamPipeFn>(clientVt[ClientReleaseSteamPipe])(client, pipe);
        }
        catch
        {
            /* best-effort */
        }
    }
}
