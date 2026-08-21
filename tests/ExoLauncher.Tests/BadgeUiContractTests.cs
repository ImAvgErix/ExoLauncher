using Xunit;

namespace ExoLauncher.Tests;

public sealed class BadgeUiContractTests
{
    [Fact]
    public void BadgeManager_IsServerGatedAndReservedBadgesAreNotGrantable()
    {
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");

        Assert.Contains("accountState.canManageBadges", settings, StringComparison.Ordinal);
        Assert.Contains("Badge authority is checked again by Exo ID", settings, StringComparison.Ordinal);
        Assert.Contains("host.onlineBadgesGet(handle)", settings, StringComparison.Ordinal);
        Assert.Contains("host.onlineBadgesGrant(handle, badge)", settings, StringComparison.Ordinal);
        Assert.Contains("host.onlineBadgesRevoke(handle, badge)", settings, StringComparison.Ordinal);

        var grantableStart = settings.IndexOf("const GRANTABLE_BADGES", StringComparison.Ordinal);
        var grantableEnd = settings.IndexOf("const GRANTABLE_BADGE_KEYS", grantableStart, StringComparison.Ordinal);
        Assert.True(grantableStart >= 0 && grantableEnd > grantableStart);
        var grantable = settings[grantableStart..grantableEnd];
        Assert.DoesNotContain("founder", grantable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ceo", grantable, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("onlineBadgesGet:", host, StringComparison.Ordinal);
        Assert.Contains("'online.badges.grant'", host, StringComparison.Ordinal);
        Assert.Contains("'online.badges.revoke'", host, StringComparison.Ordinal);
        Assert.Contains("\"online.badges.get\" =>", bridge, StringComparison.Ordinal);
        Assert.Contains("\"online.badges.grant\" =>", bridge, StringComparison.Ordinal);
        Assert.Contains("\"online.badges.revoke\" =>", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadString(paramsEl, hasParams, \"accessToken\")", bridge, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
