using Xunit;

namespace ExoLauncher.Tests;

public sealed class ProgramStartupTimingContractTests
{
    [Fact]
    public void ManagedStartupTiming_IsDeferredUntilWindowReady_AndLogsOnlyDurations()
    {
        var program = ReadRepoFile("ExoLauncher", "Program.cs");
        var timing = ReadRepoFile("ExoLauncher", "Helpers", "StartupTiming.cs");

        var main = program.IndexOf("public static void Main(string[] args)", StringComparison.Ordinal);
        var begin = program.IndexOf("StartupTiming.Begin();", main, StringComparison.Ordinal);
        var harden = program.IndexOf("NativeProcessSecurity.HardenDllSearch();", main, StringComparison.Ordinal);
        var markWinUi = program.IndexOf("StartupTiming.MarkWinUiStart();", main, StringComparison.Ordinal);
        var startWinUi = program.IndexOf("Application.Start(", main, StringComparison.Ordinal);
        Assert.True(main >= 0 && begin > main && begin < harden);
        Assert.True(markWinUi > harden && markWinUi < startWinUi);

        var ready = program.IndexOf("internal static void NotifyWindowReady()", StringComparison.Ordinal);
        var restore = program.IndexOf("TryDeliverRestore();", ready, StringComparison.Ordinal);
        var log = program.IndexOf("StartupTiming.LogWindowReady();", ready, StringComparison.Ordinal);
        Assert.True(ready >= 0 && log > ready && log < restore);

        Assert.Contains("Stopwatch.GetTimestamp()", timing, StringComparison.Ordinal);
        Assert.Contains("Stopwatch.GetElapsedTime(", timing, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _logged, 1)", timing, StringComparison.Ordinal);
        Assert.Contains(
            "PERF startup phase=window-ready managedEntryMs={managedEntryMs} winuiMs={winuiMs}",
            timing,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Process.GetCurrentProcess", timing, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.CommandLine", timing, StringComparison.Ordinal);
        Assert.DoesNotContain("args", timing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", timing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account", timing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", timing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestoreDispatch_DoesNotAllocateACapturingClosure()
    {
        var program = ReadRepoFile("ExoLauncher", "Program.cs");
        var start = program.IndexOf("private static void TryDeliverRestore()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = program[start..];

        Assert.Contains("TryEnqueue(static () =>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TryEnqueue(() =>", body, StringComparison.Ordinal);
        Assert.Contains("var current = App.MainAppWindow;", body, StringComparison.Ordinal);
        Assert.Contains("current.RestoreAndActivate();", body, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
}
