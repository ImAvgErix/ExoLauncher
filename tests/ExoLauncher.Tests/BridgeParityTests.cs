using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// WebHostBridge is the JSON-RPC host surface for the React UI.
/// ShellController remains as a typed C# twin used by leftover native controls.
/// </summary>
public class BridgeParityTests
{
    private static readonly string[] RequiredRpcMethods =
    [
        "\"library.get\"",
        "\"library.refresh\"",
        "\"game.launch\"",
        "\"game.stop\"",
        "\"game.install\"",
        "\"game.update\"",
        "\"game.uninstall\"",
        "\"game.repair\"",
        "\"game.extras\"",
        "\"game.toggleFavorite\"",
        "\"art.replace\"",
        "\"art.reset\"",
        "\"art.refetch\"",
        "\"art.report\"",
        "\"game.cancelInstall\"",
        "\"stores.search\"",
        "\"stores.check\"",
        "\"stores.matrix\"",
        "\"friends.list\"",
        "\"profile.get\"",
        "\"account.get\"",
        "\"account.signIn\"",
        "\"account.createPassword\"",
        "\"account.signInPassword\"",
        "\"account.signOut\"",
        "\"account.reserveHandle\"",
        "\"account.getProfile\"",
        "\"account.setProfile\"",
        "\"online.profiles.get\"",
        "\"online.profiles.search\"",
        "\"online.profiles.share\"",
        "\"online.badges.get\"",
        "\"online.badges.grant\"",
        "\"online.badges.revoke\"",
        "\"online.privacy.get\"",
        "\"online.privacy.set\"",
        "\"online.friends.list\"",
        "\"online.friends.requests\"",
        "\"online.friends.request\"",
        "\"online.friends.accept\"",
        "\"online.friends.decline\"",
        "\"online.friends.remove\"",
        "\"online.blocks.list\"",
        "\"online.blocks.block\"",
        "\"online.blocks.unblock\"",
        "\"online.links.get\"",
        "\"online.links.discovery\"",
        "\"online.links.link\"",
        "\"online.links.unlink\"",
        "\"online.links.match\"",
        "\"online.sessions.list\"",
        "\"online.sessions.revoke\"",
        "\"online.sessions.revokeAll\"",
        "\"online.account.export\"",
        "\"online.account.delete\"",
        "\"online.media.upload\"",
        "\"online.media.delete\"",
        "\"online.media.download\"",
        "\"online.presence.get\"",
        "\"settings.get\"",
        "\"settings.set\"",
        "\"shell.showStore\"",
        "\"shell.pickFolder\"",
        "\"app.checkUpdate\"",
        "\"app.installUpdate\"",
        "\"dlss.status\"",
        "\"dlss.updateAll\"",
        "\"dlss.restore\"",
    ];

    [Fact]
    public void WebHostBridge_DispatchesTheHostSurface()
    {
        var bridge = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Services", "WebHostBridge.cs")));
        Assert.Contains("\"game.launch\" =>", bridge, StringComparison.Ordinal);
        Assert.Contains("WebViewTrustPolicy", bridge, StringComparison.Ordinal);
        foreach (var method in RequiredRpcMethods)
            Assert.Contains(method, bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("\"dlss.ensureLatest\"", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void ReactShell_HostsViteUi()
    {
        var root = RepoRoot();
        Assert.True(Directory.Exists(Path.Combine(root, "ui")));
        Assert.True(File.Exists(Path.Combine(root, "ui", "src", "components", "LauncherApp.tsx")));
        var csproj = File.ReadAllText(Path.Combine(root, "ExoLauncher", "ExoLauncher.csproj"));
        Assert.Contains("BuildWebUi", csproj, StringComparison.Ordinal);
        Assert.Contains("wwwroot", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("npm run build", csproj, StringComparison.Ordinal);
        var window = File.ReadAllText(Path.Combine(root, "ExoLauncher", "MainWindow.xaml"));
        Assert.Contains("WebView2", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WebHost\"", window, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlineBridge_OwnsPresenceLifecycleAndForwardsTypedUpdates()
    {
        var bridge = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Services", "WebHostBridge.cs")));

        Assert.Contains("ExoOnlineClient", bridge, StringComparison.Ordinal);
        Assert.Contains("ExoPresenceClient", bridge, StringComparison.Ordinal);
        Assert.Contains("StartPresenceIfSignedInAsync", bridge, StringComparison.Ordinal);
        Assert.Contains("StopPresenceAsync", bridge, StringComparison.Ordinal);
        Assert.Contains("QueuePresenceFromLibrary", bridge, StringComparison.Ordinal);
        Assert.Contains("\"online.presence\"", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadString(p, hasParams, \"accessToken\")", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadString(p, hasParams, \"nativePath\")", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountBridge_UsesDedicatedPasswordMethodsAndNeverLogsCredentials()
    {
        var bridge = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Services", "WebHostBridge.cs")));

        Assert.Contains("CreatePasswordAccountAsync", bridge, StringComparison.Ordinal);
        Assert.Contains("SignInWithPasswordAsync", bridge, StringComparison.Ordinal);
        Assert.Contains("ReadString(p, hasParams, \"password\")", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("AppLog.Debug(password", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("PostEvent(\"password", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellController_StillExposesTypedDlssHelpers()
    {
        var shell = File.ReadAllText(FindRepoFile(Path.Combine("ExoLauncher", "Services", "ShellController.cs")));
        Assert.Contains("public Task<object> DlssStatusAsync", shell, StringComparison.Ordinal);
        Assert.Contains("public Task<object> DlssUpdateAllAsync", shell, StringComparison.Ordinal);
        Assert.Contains("public Task<object> DlssRestoreAsync", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("\"game.launch\" =>", shell, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string FindRepoFile(string relative)
    {
        var candidate = Path.Combine(RepoRoot(), relative);
        Assert.True(File.Exists(candidate), relative + " not found.");
        return candidate;
    }
}
