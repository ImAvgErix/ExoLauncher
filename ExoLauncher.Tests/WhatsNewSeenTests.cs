using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Whether Exo's What's New sheet shows comes from settings.json's whatsNewSeen.
/// Ways it could fail: a new install is shown notes about changes it never saw (the field is not seeded when no
/// settings file exists); a user from before the sheet never sees it (the field is seeded on every start, not only
/// for a new file); the seeded value is lost on the next save (left out of Clone); a later start re-seeds and hides
/// the next update's notes.
/// </summary>
public sealed class WhatsNewSeenTests : IDisposable
{
    private readonly string? _prior = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-whatsnew-" + Guid.NewGuid().ToString("N"));

    public WhatsNewSeenTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _prior);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static SettingsService Start(string version)
    {
        var service = new SettingsService();
        service.Load();
        service.SyncAppVersion(version);
        return service;
    }

    [Fact]
    public void A_new_install_has_seen_its_version()
    {
        Assert.Equal("3.14.0", Start("3.14.0").Current.WhatsNewSeen);
        // Saved, and an update later still finds the older version: its notes show.
        Assert.Equal("3.14.0", Start("3.15.0").Current.WhatsNewSeen);
    }

    [Fact]
    public void A_settings_file_from_before_the_sheet_has_seen_nothing()
    {
        File.WriteAllText(PathHelper.SettingsPath, """{ "appVersion": "3.13.4", "onboardingComplete": true }""");
        Assert.Null(Start("3.14.0").Current.WhatsNewSeen);
    }

    [Fact]
    public void Seen_is_kept_and_checked()
    {
        File.WriteAllText(PathHelper.SettingsPath, """{ "appVersion": "3.13.4", "onboardingComplete": true }""");
        var service = Start("3.14.0");
        service.ApplyPatch(whatsNewSeen: "3.14.0");
        service.ApplyPatch(sortMode: "name");
        Assert.Equal("3.14.0", Start("3.14.0").Current.WhatsNewSeen);
        Assert.Throws<InvalidDataException>(() => service.ApplyPatch(whatsNewSeen: "../x"));
    }
}
