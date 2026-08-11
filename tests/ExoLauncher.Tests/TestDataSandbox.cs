using System.Runtime.CompilerServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Points every path in <see cref="PathHelper"/> at a throwaway directory before
/// any test runs.
///
/// Without this, SettingsService wrote to the real %LOCALAPPDATA%\ExoLauncher,
/// so running the suite replaced the developer's own settings.json with test
/// fixtures — wiping favorites and resetting onboarding on their next launch.
/// </summary>
internal static class TestDataSandbox
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "ExoLauncherTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, dir);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* temp dir cleanup is best-effort */ }
        };
    }
}
