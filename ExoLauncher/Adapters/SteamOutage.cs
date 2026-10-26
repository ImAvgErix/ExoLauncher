using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Whether Steam can't reach its servers, read from Steam's own logs and nothing else: <c>logs\connection_log.txt</c>
/// (each logon and its answer, "Service Unavailable" and the like, and Steam's own internet check) and
/// <c>logs\steamui_login.txt</c> (the sign-in steps; "Loading User Data" is WaitingForLibraryReady). Only lines Steam
/// wrote since it started count. A connection Steam drops and gets back within a minute is normal, not an outage.
/// What Exo offers then is Steam's own offline mode (<see cref="SteamLoginUsers.SetOffline"/>), and only when Steam
/// can't finish signing in: a Steam that is signed in already plays its games without a restart.
/// </summary>
internal static partial class SteamOutage
{
    internal enum Cause { None, Servers, Internet }

    /// <summary>What Exo offers for an outage.</summary>
    internal enum Offer
    {
        /// <summary>Nothing to switch: Steam is signed in (its games start) or already offline.</summary>
        None,
        /// <summary>Restart Steam in its own offline mode.</summary>
        PlayOffline,
        /// <summary>Steam has no saved sign-in, so its offline mode can't start.</summary>
        NeedsSavedSignIn,
    }

    /// <param name="SignedIn">Steam finished signing in since it started (its library is ready).</param>
    /// <param name="Connected">Steam's last logon was answered OK and nothing failed since.</param>
    /// <param name="Step">The sign-in step Steam is on (steamui_login's state), null before it starts one.</param>
    /// <param name="StepActiveAt">Steam's last line about signing in.</param>
    /// <param name="Failures">Logons the servers refused or never answered since the last good one.</param>
    /// <param name="FailingSince">When the connection dropped or the first of those failed.</param>
    /// <param name="InternetDown">Steam's own internet check failed last.</param>
    internal sealed record Reading(bool SignedIn, bool Connected, string? Step, DateTime? StepActiveAt, int Failures,
        DateTime? FailingSince, bool InternetDown);

    /// <summary>Failed logons for this long, at least two, are an outage; one failure and a reconnect is normal.</summary>
    internal static readonly TimeSpan FailureSpan = TimeSpan.FromSeconds(60);
    /// <summary>A sign-in step Steam says nothing more about for this long is stuck (a normal one takes seconds).</summary>
    internal static readonly TimeSpan StuckSpan = TimeSpan.FromSeconds(90);

    // The answers Steam logs when its servers refuse a logon or never answer one. Account answers (Access Denied,
    // Invalid Password, Rate Limit Exceeded) are not an outage, and offline mode doesn't help with them.
    private static readonly HashSet<string> ServerAnswers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Service Unavailable", "Try Another CM", "Timeout", "No Connection", "Busy", "Connection Failed",
        "Remote Call Failed", "Pending",
    };

    /// <summary>The two logs' text (their tails), read as of <paramref name="since"/> (Steam's start).</summary>
    internal static Reading Read(string? connectionLog, string? loginLog, DateTime since)
    {
        bool connected = false, internetDown = false;
        var failures = 0;
        DateTime? failingSince = null;
        foreach (var (at, line) in Lines(connectionLog, since))
        {
            if (LogOnAnswer().Match(line) is { Success: true } answer)
            {
                var result = answer.Groups[1].Value;
                if (result == "OK") (connected, failures, failingSince) = (true, 0, null);
                else if (ServerAnswers.Contains(result)) Fail(at);
                else (connected, failures, failingSince) = (false, 0, null);
            }
            else if (LoggedOff().Match(line) is { Success: true } off && ServerAnswers.Contains(off.Groups[1].Value)) Fail(at);
            else if (line.Contains("ConnectFailed('", StringComparison.Ordinal)) Fail(at);
            else if (line.Contains("ConnectionDisconnected(", StringComparison.Ordinal))
            {
                connected = false;
                failingSince ??= at;
            }
            else if (line.Contains("user initiated logoff", StringComparison.Ordinal) || line.EndsWith("Log session ended", StringComparison.Ordinal))
                (connected, failures, failingSince) = (false, 0, null);
            else if (Connectivity().Match(line) is { Success: true } test)
                internetDown = !test.Groups[1].Value.Equals("Connected", StringComparison.Ordinal);
        }

        string? step = null;
        DateTime? stepActiveAt = null;
        foreach (var (at, line) in Lines(loginLog, since))
        {
            stepActiveAt = at;
            if (LoginState().Match(line) is { Success: true } state) step = state.Groups[1].Value;
        }
        return new Reading(step == "Success", connected, step, stepActiveAt, failures, failingSince, internetDown);

        void Fail(DateTime at)
        {
            connected = false;
            failures++;
            failingSince ??= at;
        }
    }

    /// <summary>
    /// Whether Steam is in an outage now. <paramref name="launchStalled"/>: a game Exo asked Steam for never started,
    /// so any refused logon or unfinished sign-in step is enough.
    /// </summary>
    internal static Cause Judge(Reading reading, DateTime now, bool launchStalled = false)
    {
        if (reading.SignedIn && reading.Connected) return Cause.None;
        var cause = reading.InternetDown ? Cause.Internet : Cause.Servers;
        if (reading.Failures >= 2 && reading.FailingSince is { } failing && now - failing >= FailureSpan) return cause;
        if (!reading.SignedIn && reading.Step is "WaitingForNetwork" or "WaitingForServerResponse" or "WaitingForLibraryReady" &&
            reading.StepActiveAt is { } active && (launchStalled || now - active >= StuckSpan))
            return reading.Step == "WaitingForNetwork" ? Cause.Internet : cause;
        if (launchStalled && !reading.SignedIn && reading.Failures >= 1) return cause;
        return Cause.None;
    }

    /// <summary>What Exo offers in an outage: Steam's offline mode, only when Steam can't sign in and can start offline.</summary>
    internal static Offer Decide(Cause cause, bool signedIn, bool alreadyOffline, bool savedSignIn, bool gameRunning) =>
        cause == Cause.None || signedIn || alreadyOffline || gameRunning ? Offer.None
        : savedSignIn ? Offer.PlayOffline : Offer.NeedsSavedSignIn;

    /// <summary>The notice for an outage, once: its title and words.</summary>
    internal static (string Title, string Text) Notice(Cause cause, Offer offer)
    {
        var title = cause == Cause.Internet ? "Steam can't connect" : "Steam's servers are down";
        var lead = cause == Cause.Internet ? "Your internet seems to be down. " : "";
        return offer switch
        {
            Offer.PlayOffline => (title, lead + "Your games still play offline."),
            Offer.NeedsSavedSignIn => (title, lead + "Steam plays offline only with a saved sign-in, so your Steam games wait until it's back."),
            _ => (title, lead + "Your games still play. Friends and multiplayer wait until Steam is back."),
        };
    }

    /// <summary>The notice when the outage is over, once.</summary>
    internal static (string Title, string Text) BackNotice(bool exoSwitchedBack) =>
        ("Steam is back online", exoSwitchedBack ? "Exo switched Steam back online." : "Its servers answer again.");

    /// <summary>
    /// Play on a Steam game while Steam can't sign in: what Exo offers instead of a launch that waits on Steam (or, with
    /// <paramref name="launchStalled"/>, after one did). Null when Steam is fine, not running, or offline mode can't help.
    /// </summary>
    internal static (Cause Cause, Offer Offer)? ForPlay(string steamRoot, bool launchStalled)
    {
        if (ReadLive(steamRoot) is not { } reading) return null;
        var cause = Judge(reading, DateTime.Now, launchStalled);
        if (cause == Cause.None) return null;
        var (account, offline) = SteamLoginUsers.CurrentOffline(steamRoot);
        var offer = Decide(cause, reading.SignedIn, offline, account?.Remembered == true, SteamLoginUsers.IsGameRunning());
        return offer == Offer.None ? null : (cause, offer);
    }

    /// <summary>Play's answer for <see cref="ForPlay"/>: the outage in a sentence, and Steam's offline mode to ask about when it applies.</summary>
    internal static LaunchResult PlayAnswer(Cause cause, Offer offer)
    {
        var (title, text) = Notice(cause, offer);
        return new LaunchResult
        {
            Ok = false,
            Message = title + ". " + text,
            OfflineStore = offer == Offer.PlayOffline ? "steam" : null,
            BackendStarted = "steam",
        };
    }

    /// <summary>What Steam's logs say now, since the running Steam started; null when Steam isn't running.</summary>
    internal static Reading? ReadLive(string steamRoot)
    {
        if (StartedAt() is not { } started) return null;
        var logs = Path.Combine(steamRoot, "logs");
        return Read(Tail(Path.Combine(logs, "connection_log.txt")), Tail(Path.Combine(logs, "steamui_login.txt")),
            started - TimeSpan.FromSeconds(5));
    }

    /// <summary>When the oldest running steam.exe started (local time, as Steam's logs are); null when none runs.</summary>
    private static DateTime? StartedAt()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName("steam"); }
        catch { return null; }
        DateTime? oldest = null;
        foreach (var process in processes)
        {
            try { if (!process.HasExited && (oldest is null || process.StartTime < oldest)) oldest = process.StartTime; }
            catch { /* exited or not ours */ }
            finally { process.Dispose(); }
        }
        return oldest;
    }

    private static string? Tail(string path)
    {
        try { return File.Exists(path) ? SteamContentLogProgress.ReadTail(path, 64 * 1024) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<(DateTime At, string Line)> Lines(string? text, DateTime since)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match match in StampedLine().Matches(text))
        {
            if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var at) || at < since)
                continue;
            yield return (at, match.Groups[2].Value.TrimEnd('\r'));
        }
    }

    [GeneratedRegex(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] (.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StampedLine();

    [GeneratedRegex(@"RecvMsgClientLogOnResponse\(\) : \[[^\]]*\] '([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex LogOnAnswer();

    [GeneratedRegex(@"RecvMsgClientLoggedOff\('([^']+)'\)", RegexOptions.CultureInvariant)]
    private static partial Regex LoggedOff();

    [GeneratedRegex(@"^Connectivity test: result=(\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex Connectivity();

    [GeneratedRegex(@"SetLoginState: (\w+) - ", RegexOptions.CultureInvariant)]
    private static partial Regex LoginState();
}
