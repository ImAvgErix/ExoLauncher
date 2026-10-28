using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways keeping Steam's client current can go wrong:
/// - a downloaded update is missed, so "Updating Steam" shows at the next Play after all;
/// - an update Steam already installed as it started reads as waiting, so Steam restarts for nothing (every hour);
/// - the beta reads as not running when Steam already started on it, or a stable Steam reads as needing a restart;
/// - the beta file is written in a form Steam doesn't read (a line break, another branch).
/// </summary>
public sealed class SteamClientUpdateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("exo-steamclient-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // As Steam writes logs\bootstrap_log.txt (from a real one).
    private static readonly string[] Started =
    [
        "[2026-10-27 03:54:23] Startup - updater built Oct 26 2026 15:12:41",
        "[2026-10-27 03:54:23] Startup - Steam Client launched with: \"c:\\program files (x86)\\steam\\steam.exe\" -silent -nofriendsui -nochatui",
        "[2026-10-27 03:54:23] Opted in to client beta 'publicbeta' via beta file",
        "[2026-10-27 03:54:23] Verifying installation...",
        "[2026-10-27 03:54:24] Verification complete",
        "[2026-10-27 03:56:25] Download skipped: /steam_client_publicbeta_win64 version 1793056502, installed version 1793056502, existing pending version 0",
        "[2026-10-27 03:56:25] Nothing to do",
    ];

    private static readonly string[] Downloaded =
    [
        "[2026-10-27 11:14:13] Background update loop checking for update. . .",
        "[2026-10-27 11:14:14] Add pending download: https://client-update.akamai.steamstatic.com/bins_win64.zip.vz.3b11b7d7ec02d21149545edd596e7fa5c223fd52_35728796",
        "[2026-10-27 11:14:15] Downloading update (67,878 of 67,878 KB)...",
        "[2026-10-27 11:14:15] Download complete.",
    ];

    private static readonly string[] InstalledAsItStarted =
    [
        "[2026-10-26 19:08:17] Startup - updater built Oct 22 2026 14:38:29",
        "[2026-10-26 19:08:17] Opted in to client beta 'publicbeta' via beta file",
        "[2026-10-26 19:08:17] Found pending update",
        "[2026-10-26 19:08:17] Installing update...",
        "[2026-10-26 19:08:22] Update complete, launching Steam...",
        "[2026-10-26 19:08:22] Startup - updater built Oct 26 2026 15:12:41",
        "[2026-10-26 19:08:22] Opted in to client beta 'publicbeta' via beta file",
        "[2026-10-26 19:08:22] Verifying installation...",
    ];

    [Fact]
    public void AnUpdateDownloadedSinceItStartedWaits() =>
        Assert.Equal("a client update is waiting", SteamClientUpdate.RestartFor([.. Started, .. Downloaded], "publicbeta"));

    [Fact]
    public void AnUpdateInstalledAsItStartedDoesNot()
    {
        Assert.Null(SteamClientUpdate.RestartFor([.. Downloaded, .. InstalledAsItStarted], "publicbeta"));
        // Found at start and installed then and there, before Steam opened.
        Assert.Null(SteamClientUpdate.RestartFor(
        [
            Started[0], Started[2], "[2026-10-27 03:54:24] Checking for available updates...", .. Downloaded[2..],
            "[2026-10-27 03:54:30] Extracting package...", "[2026-10-27 03:54:31] Installing update...",
        ], "publicbeta"));
    }

    [Fact]
    public void NothingWaitingNothingToDo()
    {
        Assert.Null(SteamClientUpdate.RestartFor(Started, "publicbeta"));
        Assert.Null(SteamClientUpdate.RestartFor([], "publicbeta"));
        Assert.Null(SteamClientUpdate.RestartFor([.. Started.Where(line => !line.Contains("Opted in", StringComparison.Ordinal))], null));
    }

    [Fact]
    public void ABetaItHasntStartedOnYet()
    {
        var stable = Started.Where(line => !line.Contains("Opted in", StringComparison.Ordinal)).ToArray();
        Assert.Equal("it isn't on its publicbeta client yet", SteamClientUpdate.RestartFor(stable, "publicbeta"));
        // Started on another beta than the file now names.
        Assert.NotNull(SteamClientUpdate.RestartFor([.. stable, "[2026-10-27 03:54:23] Opted in to client beta 'steamdeck_publicbeta' via beta file"], "publicbeta"));
        // Already on it since its last start, though an earlier start was stable.
        Assert.Null(SteamClientUpdate.RestartFor([.. stable, .. Started], "publicbeta"));
    }

    [Fact]
    public void TheBetaFileAsSteamWritesIt()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "package"));
        Assert.Null(SteamClientUpdate.ReadBeta(_dir));
        SteamClientUpdate.JoinBeta(_dir);
        Assert.Equal("publicbeta", File.ReadAllText(Path.Combine(_dir, "package", "beta")));
        File.WriteAllText(Path.Combine(_dir, "package", "beta"), "steamdeck_publicbeta\r\n");
        Assert.Equal("steamdeck_publicbeta", SteamClientUpdate.ReadBeta(_dir));
    }

    [Fact]
    public void ReadsTheLogsEndWhileSteamHasItOpen()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_dir, "logs")).FullName;
        using var steam = new FileStream(Path.Combine(logs, "bootstrap_log.txt"), FileMode.Create, FileAccess.Write, FileShare.Read);
        using (var writer = new StreamWriter(steam, leaveOpen: true))
            for (var i = 0; i < 1000; i++) writer.WriteLine($"line {i}");
        var tail = SteamClientUpdate.ReadLog(_dir, 10);
        Assert.Equal(10, tail.Count);
        Assert.Equal("line 999", tail[^1]);
    }
}
