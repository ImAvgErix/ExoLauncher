using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class OfficialClientLocatorTests
{
    private static readonly OfficialClientDefinition Definition = new(
        ExecutableNames: ["VendorClient.exe"],
        DefaultPaths: [@"D:\Vendor\VendorClient.exe"],
        UninstallDisplayNames: ["Vendor Client"]);

    [Fact]
    public void DefaultPath_RequiresExactKnownExecutable()
    {
        var command = OfficialClientLocator.ResolveFromEvidence(
            Definition,
            path => path == @"D:\Vendor\VendorClient.exe",
            Array.Empty<string?>(),
            Array.Empty<OfficialClientUninstallEntry>(),
            Array.Empty<string>());

        Assert.NotNull(command);
        Assert.Equal(@"D:\Vendor\VendorClient.exe", command!.FileName);
        Assert.False(command.IsAppx);
    }

    [Fact]
    public void AppPath_ParsesQuotedExecutableButRejectsUnknownName()
    {
        var command = OfficialClientLocator.ResolveFromEvidence(
            Definition,
            path => path == @"E:\Apps\VendorClient.exe",
            ["\"E:\\Apps\\VendorClient.exe\" --background", "F:\\Apps\\Other.exe"],
            Array.Empty<OfficialClientUninstallEntry>(),
            Array.Empty<string>());

        Assert.NotNull(command);
        Assert.Equal(@"E:\Apps\VendorClient.exe", command!.FileName);
    }

    [Fact]
    public void UninstallEvidence_RequiresMatchingVendorAndVerifiedExe()
    {
        var command = OfficialClientLocator.ResolveFromEvidence(
            Definition,
            path => path == @"G:\Vendor\VendorClient.exe",
            Array.Empty<string?>(),
            [
                new OfficialClientUninstallEntry("Different Vendor", @"G:\Vendor", null),
                new OfficialClientUninstallEntry("Vendor Client", @"G:\Vendor", null),
            ],
            Array.Empty<string>());

        Assert.NotNull(command);
        Assert.Equal(@"G:\Vendor\VendorClient.exe", command!.FileName);
    }

    [Fact]
    public void AppxEvidence_UsesOnlyConfiguredPackageAndAumid()
    {
        var xbox = new OfficialClientDefinition(
            ExecutableNames: ["XboxPcApp.exe"],
            DefaultPaths: Array.Empty<string>(),
            UninstallDisplayNames: ["Xbox"],
            AppxPackagePrefix: "Microsoft.GamingApp_",
            AppxApplicationUserModelId: "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.XboxPcApp");

        var command = OfficialClientLocator.ResolveFromEvidence(
            xbox,
            _ => false,
            Array.Empty<string?>(),
            Array.Empty<OfficialClientUninstallEntry>(),
            ["Microsoft.GamingApp_2508.1001.1.0_x64__8wekyb3d8bbwe"]);

        Assert.NotNull(command);
        Assert.True(command!.IsAppx);
        Assert.Equal("explorer.exe", command.FileName);
        Assert.Equal(
            "shell:AppsFolder\\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.XboxPcApp",
            command.Arguments);
    }

    [Fact]
    public void MissingEvidence_FailsClosed()
    {
        var command = OfficialClientLocator.ResolveFromEvidence(
            Definition,
            _ => false,
            [@"C:\Untrusted\VendorClient.exe"],
            [new OfficialClientUninstallEntry("Vendor Client", @"C:\Untrusted", null)],
            Array.Empty<string>());

        Assert.Null(command);
    }

    [Fact]
    public void StoreMatrix_OfficialClientPresenceDoesNotClaimSignIn()
    {
        var library = new LibraryService([new PresenceOnlyOfficialAdapter()], new SettingsService());

        var status = Assert.Single(library.StoreMatrix());

        Assert.True(status.agentPresent);
        Assert.True(status.clientPresent);
        Assert.False(status.signedIn);
        Assert.Equal("Found", status.detail);
    }

    [Fact]
    public async Task OfficialAdapter_DoesNotInventLibraryOrTitleControl()
    {
        var adapter = new EaAdapter();
        var game = new GameEntry
        {
            Id = "ea:example",
            Title = "Example",
            Store = StoreKind.Ea,
        };

        var library = await adapter.GetLibraryAsync();
        var launch = await adapter.LaunchAsync(game, new LaunchOptions());
        var install = await adapter.InstallAsync(game, null, progress: null);

        Assert.Empty(library);
        Assert.False(launch.Ok);
        Assert.False(launch.HandoffOnly);
        Assert.Contains("cannot launch individual", launch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(install.Ok);
        Assert.False(install.HandoffOnly);
        Assert.Contains("cannot install games", install.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RockstarAdapter_UsesHonestPresenceOnlyContract()
    {
        var adapter = new RockstarAdapter();

        Assert.Equal(StoreKind.Rockstar, adapter.Store);
        Assert.Equal("rockstar", adapter.Id);
        Assert.Equal("Rockstar Games Launcher", adapter.DisplayName);
        Assert.IsAssignableFrom<IOfficialStoreClient>(adapter);
    }

    private sealed class PresenceOnlyOfficialAdapter : IStoreAdapter, IOfficialStoreClient
    {
        public StoreKind Store => StoreKind.Xbox;
        public string Id => "xbox";
        public string DisplayName => "Xbox app";
        public IReadOnlyList<string> ClientProcessNames => ["XboxPcApp"];
        public bool IsAgentPresent() => true;
        public bool IsClientPresent() => true;
        public StoreClientLaunchCommand? GetClientLaunchCommand() => new(@"C:\Vendor\XboxPcApp.exe");
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult());
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult());
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult());
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult());
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult());
        public InstallProgress GetDownloadProgress(string gameId) => new() { GameId = gameId };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) => Task.CompletedTask;
    }
}
