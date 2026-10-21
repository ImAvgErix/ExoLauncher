using System.Runtime.CompilerServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Before any test runs, all app state (settings, logs, caches) goes to a folder of this run's own.
/// Tests that set <see cref="PathHelper.DataDirOverrideVariable"/> themselves still can; without this,
/// anything logged before such a test (the log's path is fixed on its first line) went to the
/// user's real app.log.
/// </summary>
internal static class TestDataDirectory
{
    internal static string Root { get; } =
        Path.Combine(Path.GetTempPath(), "exo-tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Isolate()
    {
        Directory.CreateDirectory(Root);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable))) return;
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* the open app.log; temp is fine */ }
        };
    }
}
