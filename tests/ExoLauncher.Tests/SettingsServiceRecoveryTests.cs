using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SettingsServiceRecoveryTests
{
    [Fact]
    public void CorruptSettingsArePreservedAcrossSaveAndFlush()
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var root = Path.Combine(Path.GetTempPath(), "ExoLauncherSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, root);
            const string corrupt = "{ this is not valid json";
            File.WriteAllText(PathHelper.SettingsPath, corrupt);

            var settings = new SettingsService();
            settings.Load();

            Assert.True(settings.LoadFailed);
            Assert.Equal(corrupt, File.ReadAllText(PathHelper.SettingsPath));
            Assert.Equal(corrupt, File.ReadAllText(PathHelper.SettingsPath + ".corrupt"));

            Assert.False(settings.TrySave(out var error));
            Assert.Contains("could not be read", error, StringComparison.OrdinalIgnoreCase);
            settings.Flush();

            Assert.Equal(corrupt, File.ReadAllText(PathHelper.SettingsPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
            try { Directory.Delete(root, recursive: true); }
            catch { /* temporary test cleanup is best effort */ }
        }
    }
}
