using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SettingsServiceRecoveryTests
{
    [Fact]
    public void ExplicitOnboardingRestartClearsMarkerAndSurvivesProcessReload()
    {
        InIsolatedDataDirectory(() =>
        {
            var settings = new SettingsService();
            settings.Load();
            settings.ToggleFavorite("steam:kept");
            settings.UpdateProfile(profile => profile.ProfileName = "Kept profile");
            var sibling = Path.Combine(Path.GetDirectoryName(PathHelper.OnboardedMarkerPath)!, "keep.cache");
            File.WriteAllText(sibling, "keep");
            settings.ApplyPatch(onboardingComplete: true);

            Assert.True(File.Exists(PathHelper.OnboardedMarkerPath));

            settings.ApplyPatch(onboardingComplete: false);

            Assert.False(settings.Current.OnboardingComplete);
            Assert.False(File.Exists(PathHelper.OnboardedMarkerPath));
            Assert.True(File.Exists(sibling));
            Assert.Contains("steam:kept", settings.Current.Favorites);
            Assert.Equal("Kept profile", settings.Current.ProfileName);

            var reloaded = new SettingsService();
            reloaded.Load();
            Assert.False(reloaded.Current.OnboardingComplete);
            Assert.Contains("steam:kept", reloaded.Current.Favorites);
            Assert.Equal("Kept profile", reloaded.Current.ProfileName);
            Assert.Equal("keep", File.ReadAllText(sibling));
        });
    }

    [Fact]
    public void CompletingRerunOnboardingRecreatesAdvisoryMarker()
    {
        InIsolatedDataDirectory(() =>
        {
            var settings = new SettingsService();
            settings.Load();
            settings.ApplyPatch(onboardingComplete: true);
            settings.ApplyPatch(onboardingComplete: false);

            settings.ApplyPatch(onboardingComplete: true);

            Assert.True(File.Exists(PathHelper.OnboardedMarkerPath));
            var reloaded = new SettingsService();
            reloaded.Load();
            Assert.True(reloaded.Current.OnboardingComplete);
        });
    }

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

    private static void InIsolatedDataDirectory(Action test)
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var root = Path.Combine(Path.GetTempPath(), "ExoLauncherSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, root);
            test();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
            try { Directory.Delete(root, recursive: true); }
            catch { /* temporary test cleanup is best effort */ }
        }
    }
}
