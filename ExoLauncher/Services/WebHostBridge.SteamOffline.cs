using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Steam's servers down. Once the page asks (<c>stores.outages</c>), Exo reads Steam's own logs every 15 s while Steam
/// runs (<see cref="SteamOutage"/>) and says so once per outage (<c>stores.outage</c>). When Steam can't finish signing
/// in, Play and the notice offer Steam's own offline mode: after the page's confirm, Exo closes Steam (only while no
/// Steam game runs), turns the account's offline values on (<see cref="SteamLoginUsers.WriteOffline"/>) and starts it
/// again, hidden. While Steam runs offline because of Exo, Exo tries it online again while you're not playing (after
/// 10 minutes, then each time twice as long, at most hourly): the values go back exactly and Steam starts again; once it
/// signs in Exo says Steam is back, once, and if it still can't, Steam goes back offline without asking again. Go
/// online in Steam itself and Exo puts its values back the next time Steam is closed.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly TimeSpan SteamOutagePoll = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FirstOnlineTry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LongestOnlineTry = TimeSpan.FromMinutes(60);
    /// <summary>How long a try online may take to sign in before Steam goes back offline.</summary>
    private static readonly TimeSpan OnlineTryWait = TimeSpan.FromMinutes(3);
    /// <summary>No try online this soon after Play on a Steam game.</summary>
    private static readonly TimeSpan LaunchQuiet = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _steamRestartGate = new(1, 1);
    private readonly object _steamOutageGate = new();
    private System.Threading.Timer? _steamOutagePoll;
    private int _steamOutagePolling;
    private SteamOutageNow? _steamOutage;
    private int _steamOutages;
    private DateTimeOffset _steamPlayAt;

    /// <summary>The outage on now. <c>Notified</c>: the page has been told (a notice, or Play's own answer).</summary>
    private sealed class SteamOutageNow
    {
        public required int Id { get; init; }
        public SteamOutage.Cause Cause { get; set; }
        public SteamOutage.Offer Offer { get; set; }
        public bool Notified { get; set; }
        public DateTimeOffset NextOnlineTry { get; set; }
        public TimeSpan OnlineTryEvery { get; set; } = FirstOnlineTry;
        public DateTimeOffset? TryingOnlineSince { get; set; }
    }

    /// <summary>The outages the page shows; starts watching Steam the first time the page asks.</summary>
    private object StoresOutages()
    {
        if (_steamOutagePoll is null)
        {
            var poll = new System.Threading.Timer(_ => _ = SteamOutageTickAsync(), null, TimeSpan.FromSeconds(2), SteamOutagePoll);
            if (Interlocked.CompareExchange(ref _steamOutagePoll, poll, null) is not null) poll.Dispose();
        }
        lock (_steamOutageGate)
            return new { ok = true, outages = _steamOutage is { Notified: true, TryingOnlineSince: null } outage ? new[] { OutageDto(outage) } : [] };
    }

    /// <summary>The notice's Play offline, after the page's confirm: Steam restarts in its offline mode.</summary>
    private async Task<object> StoresPlayOfflineAsync(JsonElement p, bool hasParams)
    {
        if (ReadString(p, hasParams, "store") != "steam") return new { ok = false, message = "Exo can't switch this store offline." };
        _steamPlayAt = DateTimeOffset.UtcNow;
        var (ok, message) = await RestartSteamAsync(offline: true, _bridgeLifetime.Token).ConfigureAwait(true);
        if (ok)
            lock (_steamOutageGate)
            {
                var outage = _steamOutage ??= new SteamOutageNow { Id = ++_steamOutages, Cause = SteamOutage.Cause.Servers, Notified = true };
                outage.NextOnlineTry = DateTimeOffset.UtcNow + outage.OnlineTryEvery;
            }
        return new { ok, message };
    }

    /// <summary>Play on a Steam game while Steam can't sign in: Play's answer (offline mode to ask about) instead of a launch that waits on it.</summary>
    private object? SteamPlayOfflineFirst(GameEntry game)
    {
        if (game.Store != StoreKind.Steam) return null;
        _steamPlayAt = DateTimeOffset.UtcNow;
        if (SteamInstallation.ResolveRoot() is not { } root || SteamOutage.ForPlay(root, launchStalled: false) is not { } outage) return null;
        var answer = SteamOutage.PlayAnswer(outage.Cause, outage.Offer);
        NoteSteamPlayAnswer(answer);
        return MapDepAwareResult(answer);
    }

    /// <summary>Play said Steam's servers are down: that outage needs no notice of its own.</summary>
    private void NoteSteamPlayAnswer(LaunchResult result)
    {
        if (result.OfflineStore != "steam") return;
        lock (_steamOutageGate)
        {
            var outage = _steamOutage ??= new SteamOutageNow { Id = ++_steamOutages, Cause = SteamOutage.Cause.Servers };
            outage.Offer = SteamOutage.Offer.PlayOffline;
            outage.Notified = true;
        }
    }

    private async Task SteamOutageTickAsync()
    {
        if (_detached)
        {
            Interlocked.Exchange(ref _steamOutagePoll, null)?.Dispose();
            return;
        }
        if (Interlocked.Exchange(ref _steamOutagePolling, 1) == 1) return;
        try
        {
            if (SteamInstallation.ResolveRoot() is not { } root) return;
            var reading = SteamOutage.ReadLive(root);
            var standing = SteamLoginUsers.LoadOfflineSwitch();
            if (reading is null && standing is null) return;
            var (account, offline) = SteamLoginUsers.CurrentOffline(root);
            if (reading is null)
            {
                // Steam is closed. Gone online by its own Go Online while it ran: Exo's values go back now.
                if (!offline && await _steamRestartGate.WaitAsync(0).ConfigureAwait(false))
                {
                    try { if (!ProcessHelper.IsProcessRunning("steam")) SteamLoginUsers.WriteOnline(root); }
                    finally { _steamRestartGate.Release(); }
                }
                return;
            }

            var now = DateTimeOffset.UtcNow;
            SteamOutageNow? outage;
            lock (_steamOutageGate) outage = _steamOutage;
            if (offline)
            {
                // Steam runs offline: the user's own offline mode is theirs; Exo's is tried online now and then.
                if (standing is null) return;
                lock (_steamOutageGate)
                    outage = _steamOutage ??= new SteamOutageNow { Id = ++_steamOutages, Cause = SteamOutage.Cause.Servers, Notified = true, NextOnlineTry = now + FirstOnlineTry };
                if (outage.TryingOnlineSince is not null || now < outage.NextOnlineTry || SteamLoginUsers.IsGameRunning() || now - _steamPlayAt < LaunchQuiet) return;
                AppLog.Info("Steam: trying its servers again; Steam restarts online.");
                outage.TryingOnlineSince = now;
                var (tried, why) = await RestartSteamAsync(offline: false, _bridgeLifetime.Token).ConfigureAwait(false);
                if (!tried)
                {
                    AppLog.Info("Steam: stays offline for now: " + why);
                    outage.TryingOnlineSince = null;
                    outage.NextOnlineTry = now + outage.OnlineTryEvery;
                }
                return;
            }

            var cause = SteamOutage.Judge(reading, DateTime.Now);
            var back = reading.SignedIn && reading.Connected;
            if (outage?.TryingOnlineSince is { } since)
            {
                if (back) EndSteamOutage(outage, exoSwitchedBack: true);
                else if (cause != SteamOutage.Cause.None || now - since >= OnlineTryWait)
                {
                    // Still down: back offline, without asking again for this outage.
                    outage.OnlineTryEvery = TimeSpan.FromTicks(Math.Min(outage.OnlineTryEvery.Ticks * 2, LongestOnlineTry.Ticks));
                    outage.NextOnlineTry = now + outage.OnlineTryEvery;
                    outage.TryingOnlineSince = null;
                    if (!SteamLoginUsers.IsGameRunning())
                    {
                        var (ok, why) = await RestartSteamAsync(offline: true, _bridgeLifetime.Token).ConfigureAwait(false);
                        AppLog.Info(ok ? "Steam: its servers are still down; back in offline mode." : "Steam: could not go back offline: " + why);
                    }
                }
                return;
            }

            if (cause != SteamOutage.Cause.None)
            {
                var offer = SteamOutage.Decide(cause, reading.SignedIn, alreadyOffline: false, account?.Remembered == true, SteamLoginUsers.IsGameRunning());
                bool tell;
                lock (_steamOutageGate)
                {
                    outage = _steamOutage ??= new SteamOutageNow { Id = ++_steamOutages };
                    if (outage.Cause == SteamOutage.Cause.None) AppLog.Info($"Steam: can't reach its servers ({cause}; offer {offer}).");
                    outage.Cause = cause;
                    outage.Offer = offer;
                    tell = !outage.Notified;
                    outage.Notified = true;
                }
                if (tell) PostEvent("stores.outage", OutageDto(outage));
            }
            else if (outage is not null && back) EndSteamOutage(outage, exoSwitchedBack: false);
        }
        catch (Exception ex) { AppLog.Debug("Steam outage check failed: " + ex.GetType().Name + ": " + ex.Message); }
        finally { Interlocked.Exchange(ref _steamOutagePolling, 0); }
    }

    /// <summary>Steam signs in again: the outage is over, said once when it was said.</summary>
    private void EndSteamOutage(SteamOutageNow outage, bool exoSwitchedBack)
    {
        lock (_steamOutageGate)
        {
            if (!ReferenceEquals(_steamOutage, outage)) return;
            _steamOutage = null;
        }
        AppLog.Info(exoSwitchedBack ? "Steam: its servers answer again; back online." : "Steam: its servers answer again.");
        if (!outage.Notified) return;
        var (title, text) = SteamOutage.BackNotice(exoSwitchedBack);
        PostEvent("stores.outage", new { id = OutageId(outage), store = "steam", state = "back", title, text, canPlayOffline = false });
    }

    private static string OutageId(SteamOutageNow outage) => "steam-" + outage.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static object OutageDto(SteamOutageNow outage)
    {
        var (title, text) = SteamOutage.Notice(outage.Cause, outage.Offer);
        return new { id = OutageId(outage), store = "steam", state = "down", title, text, canPlayOffline = outage.Offer == SteamOutage.Offer.PlayOffline };
    }

    /// <summary>
    /// Closes Steam with its own <c>-shutdown</c> (never while a Steam game runs), turns Exo's offline switch on or puts
    /// it back while Steam is closed, and starts Steam again hidden. Going offline, it waits for Steam to sign in.
    /// </summary>
    private async Task<(bool Ok, string Message)> RestartSteamAsync(bool offline, CancellationToken ct)
    {
        var root = SteamInstallation.ResolveRoot();
        var exe = root is null ? null : Path.Combine(root, "steam.exe");
        if (root is null || exe is null || !File.Exists(exe)) return (false, "Steam is not installed.");
        var account = SteamLoginUsers.CurrentOffline(root).Account;
        if (offline && account is not { Remembered: true })
            return (false, "Steam plays offline only with a saved sign-in, so your Steam games wait until it's back.");
        await _steamRestartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (SteamLoginUsers.IsGameRunning()) return (false, "A Steam game is running. Close it, then try again.");
            AppLog.Info(offline ? "Steam: restarting in offline mode." : "Steam: restarting online.");
            if (!await CloseSteamAsync(exe, ct).ConfigureAwait(false)) return (false, "Steam did not close. Close Steam, then try again.");
            string? failure;
            try { failure = offline ? SteamLoginUsers.WriteOffline(root, account!.SteamId64) : SteamLoginUsers.WriteOnline(root) ? null : "Steam's settings could not be read."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Steam: offline switch not written: " + ex.GetType().Name);
                failure = "Exo could not change Steam's offline mode. Try again.";
            }
            SteamPlaytime.Invalidate();
            StartSteamHidden(exe);
            if (failure is not null) return (false, failure);
            if (!offline) return (true, "Steam is online.");
            // Signed in offline: its account is the active one, or its own sign-in steps reached Success.
            var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
            var accountId = account!.AccountId;
            while (DateTimeOffset.UtcNow < until)
            {
                if (SteamClientReady.ReadActiveUser().ToString(System.Globalization.CultureInfo.InvariantCulture) == accountId ||
                    SteamOutage.ReadLive(root)?.SignedIn == true)
                    return (true, "Steam is in offline mode.");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            AppLog.Warn("Steam: did not sign in offline within 60 s.");
            return (false, "Steam didn't start offline. Open Steam to see why.");
        }
        finally { _steamRestartGate.Release(); }
    }
}
