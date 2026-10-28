using System.Collections;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Saving settings copies them field by field. A setting added to <see cref="AppSettings"/> but not to that copy is
/// lost on every save (Start Lucid with Exo turned itself back off this way), so every simple setting must survive it.
/// </summary>
public sealed class SettingsCloneTests
{
    [Fact]
    public void Every_simple_setting_survives_a_save()
    {
        var source = new AppSettings();
        var changed = new List<string>();
        foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value = type == typeof(bool) ? !(bool)(property.GetValue(source) ?? false)
                : type == typeof(string) ? "C:\\Exo\\Lucid.exe"
                : null;
            if (value is null) continue;
            property.SetValue(source, value);
            changed.Add(property.Name);
        }

        var copy = SettingsService.Clone(source);

        // Settings the copy deliberately normalizes to a fixed set of values or rewrites.
        string[] normalized = ["TextColour", "TextSize", "SortMode", "DiscordInputProfile", "MouseBridge", "DefaultInstallRoot",
            "DiscordInputDevice", "DiscordOutputDevice", "AppVersion", "NvidiaDriverBranch"];
        var lost = changed.Except(normalized).Where(name =>
        {
            var property = typeof(AppSettings).GetProperty(name)!;
            return !Equals(property.GetValue(source), property.GetValue(copy));
        }).ToList();
        Assert.Empty(lost);
    }
}
