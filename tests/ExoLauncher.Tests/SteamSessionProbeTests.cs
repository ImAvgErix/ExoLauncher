using ExoLauncher.Adapters;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Steam sign-in is Steam's. Exo may only claim the weaker, true thing: an
/// active local account whose userdata it can actually read.
/// </summary>
public sealed class SteamSessionProbeTests
{
    [Fact]
    public void NoSteamRoot_IsNeverASession()
    {
        Assert.False(SteamSessionProbe.HasReadableAccount(null));
        Assert.False(SteamSessionProbe.HasReadableAccount("   "));
    }

    [Fact]
    public void SoleUserdataAccount_ResolvesAsAReadableAccount()
    {
        var root = CreateSteamRoot(("900000001", "440", 12));
        try
        {
            SteamPlaytime.Invalidate();
            Assert.True(SteamSessionProbe.HasReadableAccount(root));
        }
        finally
        {
            SteamPlaytime.Invalidate();
            try { Directory.Delete(root, recursive: true); } catch { /* temp tree */ }
        }
    }

    [Fact]
    public void InstalledSteamWithoutUserdata_IsNotASession()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        try
        {
            SteamPlaytime.Invalidate();
            Assert.False(SteamSessionProbe.HasReadableAccount(root));
        }
        finally
        {
            SteamPlaytime.Invalidate();
            try { Directory.Delete(root, recursive: true); } catch { /* temp tree */ }
        }
    }

    /// <summary>A shared PC with no active user must not pick someone's account.</summary>
    [Fact]
    public void SeveralAccountsWithNoActiveUser_StaysUnknown()
    {
        var root = CreateSteamRoot(("900000001", "440", 12), ("900000002", "440", 34));
        try
        {
            SteamPlaytime.Invalidate();
            Assert.False(SteamSessionProbe.HasReadableAccount(root));
        }
        finally
        {
            SteamPlaytime.Invalidate();
            try { Directory.Delete(root, recursive: true); } catch { /* temp tree */ }
        }
    }

    private static string CreateSteamRoot(params (string Account, string App, int Minutes)[] rows)
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-steam-session-" + Guid.NewGuid().ToString("N"));
        foreach (var row in rows)
        {
            var directory = Path.Combine(root, "userdata", row.Account, "config");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "localconfig.vdf"),
                $"\"apps\" {{ \"{row.App}\" {{ \"Playtime\" \"{row.Minutes}\" }} }}");
        }
        return root;
    }
}
