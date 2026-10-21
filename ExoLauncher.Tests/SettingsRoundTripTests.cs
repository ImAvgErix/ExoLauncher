using System.Reflection;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// settings.json through the real SettingsService, save then load.
/// Ways it could fail: a new AppSettings property is left out of SettingsService.Clone, so every save
/// writes its default and the user's choice is gone after the next change (silently: nothing errors);
/// a per-screen map (NVIDIA color, the profile SDR gamma replaced) loses entries or its case-insensitive keys.
/// </summary>
public sealed class SettingsRoundTripTests : IDisposable
{
    private readonly string? _prior = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-settings-rt-" + Guid.NewGuid().ToString("N"));

    /// <summary>Locked product defaults, and values Clone normalizes on purpose (a fixed word list, validated keys).</summary>
    private static readonly HashSet<string> NotFree =
    [
        nameof(AppSettings.AntiCheatSafeMode), nameof(AppSettings.MinimizeWhilePlaying), nameof(AppSettings.AutoInstallRedistributables),
        nameof(AppSettings.CloseStoreClientsAfterLaunch), nameof(AppSettings.FramelessMode), nameof(AppSettings.CopyPortableIntoLibrary),
        nameof(AppSettings.AllowResize), nameof(AppSettings.CheckForUpdates), nameof(AppSettings.DiscordKeybinds),
        nameof(AppSettings.DiscordDisabledSounds), nameof(AppSettings.LaunchOverrides), nameof(AppSettings.CustomCoverImages),
        nameof(AppSettings.PreferredGameSources),
    ];

    public SettingsRoundTripTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _prior);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static IEnumerable<PropertyInfo> Free() => typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite && p.PropertyType != typeof(string) && !NotFree.Contains(p.Name));

    [Fact]
    public void Every_setting_survives_save_and_load()
    {
        var service = new SettingsService();
        service.Load();
        service.UpdateProfile(settings =>
        {
            foreach (var property in Free())
            {
                var type = property.PropertyType;
                if (type == typeof(bool)) property.SetValue(settings, !(bool)property.GetValue(settings)!);
                else if (type == typeof(int)) property.SetValue(settings, (int)property.GetValue(settings)! + 1);
                else if (type == typeof(int?)) property.SetValue(settings, 3);
                else if (property.GetValue(settings) is Dictionary<string, string> map) map[@"\\?\DISPLAY#Key"] = "value";
                else if (property.GetValue(settings) is List<string> list) list.Add("steam:1");
                else if (property.GetValue(settings) is List<LibraryFolder> folders) folders.Add(new LibraryFolder { Id = "f1", Name = "Shooters", Games = ["steam:1"] });
                else Assert.Fail(property.Name + " has a type this test does not set; add it here.");
            }
        });
        var expected = service.Current;

        var reloaded = new SettingsService();
        reloaded.Load();
        var actual = reloaded.Current;
        foreach (var property in Free())
        {
            var want = property.GetValue(expected);
            var got = property.GetValue(actual);
            if (want is Dictionary<string, string> map)
            {
                var gotMap = Assert.IsType<Dictionary<string, string>>(got);
                Assert.True(gotMap.TryGetValue(@"\\?\display#key", out var value) && value == "value", property.Name + " was not kept");
            }
            else if (want is List<string> list) Assert.True(list.SequenceEqual((List<string>)got!), property.Name + " was not kept");
            else if (want is List<LibraryFolder>)
                Assert.True(got is List<LibraryFolder> { Count: 1 } kept && kept[0] is { Id: "f1", Name: "Shooters" } && kept[0].Games.SequenceEqual(["steam:1"]), property.Name + " was not kept");
            else Assert.True(Equals(want, got), $"{property.Name} was not kept: {want} became {got}");
        }
    }
}
