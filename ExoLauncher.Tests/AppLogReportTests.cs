using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways an error's Copy logs could copy the wrong thing: another room's chatter instead of this one's, an old warning
/// from hours ago, a crash's stack cut from its entry, or the user's name and profile folder left in.
/// </summary>
public sealed class AppLogReportTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 3, 30, 0, DateTimeKind.Utc);

    private static readonly string[] Log =
    [
        "[2026-10-27T00:10:00.0000000Z] WARN Runtimes: an old warning, hours ago.",
        "[2026-10-27T03:25:48.4880030Z] INFO Runtimes: .NET's release list couldn't be read (HttpRequestException); the catalog's versions stand in.",
        "[2026-10-27T03:25:49.0000000Z] INFO Discord: gateway resumed.",
        "[2026-10-27T03:26:06.6456515Z] INFO Runtimes: dotnet10-x64 failed.",
        "[2026-10-27T03:26:37.0983167Z] WARN Apps: C:\\Users\\Erix\\AppData\\Local\\Temp\\x.exe for Erix wasn't readable.",
        "[2026-10-27T03:26:39.0000000Z] CRASH Unhandled: System.AccessViolationException",
        "   at ExoLauncher.Services.LeftoverScan.ReadTasks()",
        "",
    ];

    [Fact]
    public void ItCopiesThisRoomsEntriesAndRecentTroubleOnly()
    {
        var report = AppLog.ReportFor("7 failed.", "pc", Log, Now, "3.16.0", "10.0.26300.0", "Erix", @"C:\Users\Erix");
        Assert.StartsWith("Exo Launcher 3.16.0 · Windows 10.0.26300.0 · 2026-10-27 03:30 UTC", report);
        Assert.Contains("Error (pc): 7 failed.", report);
        Assert.Contains("dotnet10-x64 failed.", report);
        Assert.Contains("release list couldn't be read", report);
        Assert.DoesNotContain("gateway resumed", report);           // another room's
        Assert.DoesNotContain("an old warning", report);            // over two hours old
        Assert.Contains("CRASH Unhandled", report);                 // trouble from the last half hour, any room
        Assert.Contains("LeftoverScan.ReadTasks()", report);        // with its stack
        Assert.Contains(@"%USERPROFILE%\AppData\Local\Temp\x.exe for <user>", report);
        Assert.DoesNotContain("Erix", report);
    }

    [Fact]
    public void AnAreaWithNothingSaysSo()
    {
        var report = AppLog.ReportFor("Didn't work.", "music", Log[..2], Now, "3.16.0", "10", "Erix", @"C:\Users\Erix");
        Assert.Contains("Nothing in Exo's log about it from the last two hours.", report);
    }

    [Fact]
    public void TheErrorsOwnNamesPickTheirLines()
    {
        // A Riot error from Settings: its lines say "riot", not "Settings"; an update check says "Update" and stays out.
        string[] log =
        [
            "[2026-10-27T03:20:00.0000000Z] INFO stores.auth: riot handoff to its own app: opened",
            "[2026-10-27T03:21:00.0000000Z] INFO Riot: switched the signed-in account.",
            "[2026-10-27T03:22:00.0000000Z] INFO PERF rpc method=app.checkUpdate elapsedMs=464",
        ];
        var report = AppLog.ReportFor("Riot Client accepted the request, but its window did not appear. Try opening it again.", "settings", log, Now, "3.16.0", "10", "Erix", @"C:\Users\Erix");
        Assert.Contains("switched the signed-in account", report);
        Assert.Contains("riot handoff", report);
        Assert.DoesNotContain("checkUpdate", report);
        Assert.Equal(["Riot"], AppLog.NamesIn("Riot Client accepted the request, but its window did not appear. Try opening it again."));
    }

    [Fact]
    public void ANameInsideAnotherWordStays() =>
        Assert.Equal("ImAvgErix.ExoLauncher <user>", AppLog.Personal("ImAvgErix.ExoLauncher Erix", "Erix", @"C:\Users\Erix"));
}
