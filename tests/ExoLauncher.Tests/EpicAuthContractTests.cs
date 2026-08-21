using Xunit;

namespace ExoLauncher.Tests;

public sealed class EpicAuthContractTests
{
    [Fact]
    public void AuthenticateAsync_AwaitsNormalAuth_AndVerifiesTheResult()
    {
        var source = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "EpicAdapter.cs")));
        var start = source.IndexOf("public async Task<AuthResult> AuthenticateAsync", StringComparison.Ordinal);
        var end = source.IndexOf("/// <summary>AMD64 only", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var method = source[start..end];

        Assert.DoesNotContain("\"--import\"", method, StringComparison.Ordinal);
        Assert.DoesNotContain("StartAuthConsole", method, StringComparison.Ordinal);
        Assert.Contains("LegendaryCli.AuthArgs()", method, StringComparison.Ordinal);
        Assert.Contains("await CliRunner.RunAsync", method, StringComparison.Ordinal);
        Assert.Contains("HasValidLegendarySessionAsync", method, StringComparison.Ordinal);
        Assert.Contains("OperationCanceledException", method, StringComparison.Ordinal);
        Assert.Contains("Ok = false", method, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryStartup_LeavesOwnedAndEglReconciliationOffTheCriticalPath()
    {
        var source = File.ReadAllText(FindRepoFile(
            Path.Combine("ExoLauncher", "Adapters", "EpicAdapter.cs")));
        var start = source.IndexOf(
            "public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private static void ScheduleEglSyncImport",
            start,
            StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var startup = source[start..end];
        Assert.Contains("EpicPlaytime.RefreshCachedMinutes();", startup, StringComparison.Ordinal);
        Assert.True(
            startup.IndexOf("EpicPlaytime.RefreshCachedMinutes();", StringComparison.Ordinal)
            < startup.IndexOf("if (legendary is not null)", StringComparison.Ordinal),
            "Epic hours must refresh from Legendary/Heroic user.json even when legendary.exe is missing.");
        Assert.Contains("ReadNativeInstalledLibrary", startup, StringComparison.Ordinal);
        Assert.Contains("LegendaryCli.ListInstalledArgs()", startup, StringComparison.Ordinal);
        Assert.Contains("ReadEpicManifests", startup, StringComparison.Ordinal);
        Assert.Contains("ReadLauncherInstalled", startup, StringComparison.Ordinal);
        Assert.Contains("ScheduleEglSyncImport(legendary)", startup, StringComparison.Ordinal);
        Assert.Contains("TryParseLegendaryListAsync", startup, StringComparison.Ordinal);
        Assert.Contains("OperationCanceledException", startup, StringComparison.Ordinal);
        Assert.True(
            startup.IndexOf("ReadNativeInstalledLibrary", StringComparison.Ordinal)
            < startup.IndexOf("TryListLegendaryInstalledAsync", StringComparison.Ordinal),
            "EGL/Legendary installed.json must be read before spawning legendary.exe.");
        Assert.DoesNotContain("ct.ThrowIfCancellationRequested()", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("LegendaryCli.ListOwnedArgs()", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("await TryEglSyncImportOnceAsync", startup, StringComparison.Ordinal);

        var reconcileEnd = source.IndexOf(
            "private static async Task TryEglSyncImportOnceAsync",
            end,
            StringComparison.Ordinal);
        Assert.True(reconcileEnd > end);
        var scheduler = source[end..reconcileEnd];
        Assert.Contains("Task.Run", scheduler, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange(ref _eglSyncScheduled, 1, 0)", scheduler, StringComparison.Ordinal);
        Assert.Contains("TryEglSyncImportOnceAsync", scheduler, StringComparison.Ordinal);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
