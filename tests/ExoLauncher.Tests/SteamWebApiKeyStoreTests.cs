using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class SteamWebApiKeyStoreTests
{
    [Fact]
    public void Save_StoresAHexKeyAndNeverWritesSettingsJson()
    {
        InIsolatedDataDirectory(() =>
        {
            const string key = "0123456789abcdef0123456789abcdef";
            Assert.True(SteamWebApiKeyStore.Save(key));
            Assert.True(SteamWebApiKeyStore.HasKey());
            Assert.Equal(key, SteamWebApiKeyStore.TryRead());
            Assert.False(File.Exists(PathHelper.SettingsPath));
            Assert.True(File.Exists(SteamWebApiKeyStore.StorePath));
            var blob = File.ReadAllText(SteamWebApiKeyStore.StorePath);
            Assert.DoesNotContain(key, blob, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Save_EmptyClears_InvalidLeavesTheSavedKey()
    {
        InIsolatedDataDirectory(() =>
        {
            const string key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            Assert.True(SteamWebApiKeyStore.Save(key));
            Assert.False(SteamWebApiKeyStore.Save("not-a-key"));
            Assert.Equal(key, SteamWebApiKeyStore.TryRead());
            Assert.True(SteamWebApiKeyStore.Save(""));
            Assert.False(SteamWebApiKeyStore.HasKey());
            Assert.Null(SteamWebApiKeyStore.TryRead());
        });
    }

    [Fact]
    public void Normalize_RejectsAnythingThatIsNotAHexKey()
    {
        Assert.Null(SteamWebApiKeyStore.Normalize("short"));
        Assert.Null(SteamWebApiKeyStore.Normalize("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"));
        Assert.NotNull(SteamWebApiKeyStore.Normalize("0123456789ABCDEF0123456789abcdef"));
    }

    private static void InIsolatedDataDirectory(Action test)
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var root = Path.Combine(
            Path.GetTempPath(),
            "ExoLauncherSteamWebApiKeyStoreTests",
            Guid.NewGuid().ToString("N"));
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
