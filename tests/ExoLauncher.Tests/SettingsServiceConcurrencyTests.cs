using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SettingsServiceConcurrencyTests
{
    [Fact]
    public async Task ParallelMutationsPersistOneCompleteLatestSnapshot()
    {
        await InIsolatedDataDirectory(async root =>
        {
            var settings = new SettingsService();
            Assert.True(settings.TrySave(out var initialError), initialError);

            const int count = 64;
            var favorites = Enumerable.Range(0, count)
                .Select(i => Task.Run(() => settings.ToggleFavorite($"steam:{i}")));
            var launches = Enumerable.Range(0, count)
                .Select(i => Task.Run(() => settings.RecordLaunch($"riot:{i}")));
            var patches = Enumerable.Range(0, count)
                .Select(i => Task.Run(() => settings.ApplyPatch(
                    trophyNotificationPositionX: i / (double)(count - 1),
                    trophyNotificationPositionY: (count - 1 - i) / (double)(count - 1))));

            await Task.WhenAll(favorites.Concat(launches).Concat(patches));

            var current = settings.Current;
            Assert.Equal(count, current.Favorites.Count);
            Assert.Equal(count, current.Favorites.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(count, current.LastPlayed.Count);
            Assert.Equal(40, current.Recent.Count);

            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(PathHelper.SettingsPath));
            var persistedRoot = persisted.RootElement;
            Assert.Equal(count, persistedRoot.GetProperty("favorites").GetArrayLength());
            Assert.Equal(count, persistedRoot.GetProperty("lastPlayed").EnumerateObject().Count());
            Assert.Equal(40, persistedRoot.GetProperty("recent").GetArrayLength());

            Assert.Empty(Directory.EnumerateFiles(root, "settings.json.*.tmp"));
        });
    }

    [Fact]
    public async Task CurrentIsDetachedFromMutableServiceCollections()
    {
        await InIsolatedDataDirectory(_ =>
        {
            var settings = new SettingsService();
            settings.ToggleFavorite("steam:kept");
            settings.RecordLaunch("riot:kept");

            var detached = settings.Current;
            detached.Favorites.Clear();
            detached.Recent.Clear();
            detached.LastPlayed.Clear();
            detached.SortMode = "store";

            var current = settings.Current;
            Assert.Contains("steam:kept", current.Favorites, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("riot:kept", current.Recent, StringComparer.OrdinalIgnoreCase);
            Assert.True(current.LastPlayed.ContainsKey("riot:kept"));
            Assert.Equal("name", current.SortMode);
            return Task.CompletedTask;
        });
    }

    private static async Task InIsolatedDataDirectory(Func<string, Task> test)
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var root = Path.Combine(
            Path.GetTempPath(),
            "ExoLauncherSettingsConcurrencyTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, root);
            await test(root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
            try { Directory.Delete(root, recursive: true); }
            catch { /* temporary test cleanup is best effort */ }
        }
    }
}
