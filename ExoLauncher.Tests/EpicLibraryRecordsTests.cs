using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Epic's install records can put the wrong games in the library:
/// - a first install the new launcher queued, paused or never finished (only a pendingVersion) reads as installed;
/// - Unreal Engine or a tool such as Twinmotion reads as a game, named by its app id;
/// - a folder left after an uninstall (no executable) reads as installed;
/// - a game with no catalog title keeps its codename or 32-hex-digit id when its name is known elsewhere
///   (the launcher's download history, the account's owned list), or a hex id wins over a real title on length;
/// - a newer build Epic lists never shows Update, or a list older than an update that just landed shows it again;
/// - Legendary's "not installed, cannot uninstall" (exit 0) reads as a successful Remove.
/// </summary>
public sealed class EpicLibraryRecordsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-epic-records-" + Guid.NewGuid().ToString("N"));

    public EpicLibraryRecordsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string relative, params string[] lines)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    // Shapes from the new launcher's own files on a real PC (Rocket League's first install, then an update).
    [Fact]
    public void AFirstInstallThatNeverFinishedIsNotInstalledButAnUpdateInProgressIs()
    {
        Write("installations.jsonl",
            """{"version":1}""",
            """{"installationId":"A","complete":false,"dir":"C:\\Games\\new","namespace":"n1","itemId":"i1","artifactId":"Never","pendingVersion":"1.0","writtenAt":"2026-10-14T18:32:33Z"}""",
            """{"installationId":"B","complete":false,"dir":"C:\\Games\\rl","namespace":"n2","itemId":"i2","artifactId":"Sugar","version":"60","pendingVersion":"60.1","writtenAt":"2026-10-21T00:14:12Z"}""");

        var installs = EpicLauncherV2.ReadInstallations(_dir).ToDictionary(i => i.AppName);

        Assert.False(installs["Never"].EverInstalled);
        Assert.True(installs["Sugar"].EverInstalled);
        Assert.False(installs["Sugar"].UpToDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 0, 14, 12, TimeSpan.Zero), installs["Sugar"].WrittenAt);
    }

    [Fact]
    public void UnrealEngineAndToolsAreNotGames()
    {
        Write(Path.Combine("cache", "prod", "engines-cache.json"),
            """[{"appId":{"namespaceId":"ue","itemId":"18b3","appName":"UE_5.8"},"title":"Unreal Engine 5.8"}]""");
        Write(Path.Combine("cache", "prod", "tools-cache.json"),
            """[{"family":"twinmotion","versions":[{"appId":{"namespaceId":"poodle","itemId":"bee9","appName":"121dd78a6a07410e8cc26845e0dc9238"}}]}]""");
        var nonGames = EpicLauncherV2.ReadNonGameIds(_dir);

        EpicLauncherV2.Installation Install(string app, string ns, string item) => new(app, ns, item, @"C:\x", null, true, "1");
        Assert.True(EpicLauncherV2.IsNonGame(Install("121dd78a6a07410e8cc26845e0dc9238", "poodle", "bee9"), nonGames));
        Assert.True(EpicLauncherV2.IsNonGame(Install("UE_5.8", "ue", "18b3"), nonGames));
        Assert.True(EpicLauncherV2.IsNonGame(Install("UE_5.1", "ue", "old"), nonGames));
        Assert.False(EpicLauncherV2.IsNonGame(Install("Sugar", "9773", "530d"), nonGames));
    }

    [Fact]
    public void TheDownloadHistoryNamesWhatTheCatalogCacheForgot()
    {
        Write(Path.Combine("cache", "prod", "catalog-cache.json"),
            """{"items":{"4fe7":{"item":{"title":"Fortnite"}}}}""");
        Write(Path.Combine("download", "history.json"),
            """{"acct":[{"key":"9773:530d:Sugar","titleCache":"Rocket League®"},{"key":"4fe7:4fe7:Fortnite","titleCache":"Old name"}]}""");

        var titles = EpicLauncherV2.ReadTitles(_dir);

        Assert.Equal("Rocket League®", titles["530d"]);
        Assert.Equal("Fortnite", titles["4fe7"]);
    }

    [Fact]
    public void AFolderWithoutItsExecutableIsNotAnInstall()
    {
        var game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(Path.Combine(game, "Binaries", "Win64"));

        Assert.False(EpicAdapter.InstallPresent(game, "Binaries/Win64/Launcher.exe"));
        File.WriteAllText(Path.Combine(game, "Binaries", "Win64", "Launcher.exe"), "");
        Assert.True(EpicAdapter.InstallPresent(game, "Binaries/Win64/Launcher.exe"));
        Assert.True(EpicAdapter.InstallPresent(game, null));
        Assert.False(EpicAdapter.InstallPresent(Path.Combine(_dir, "gone"), null));
    }

    [Fact]
    public void ANewerBuildShowsUpdateOnlyWhenSeenAfterTheInstallChanged()
    {
        // Legendary's assets.json as it is on a real PC (one Windows list of app_name and build_version).
        var latest = EpicAdapter.LatestBuilds.Parse(
            """{"Windows":[{"app_name":"Sugar","build_version":"++Prime+Update60.1-CL-530866"},{"app_name":"Fortnite","build_version":"++Fortnite+Release-42.40-CL-59343437-Windows"}]}""",
            new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc));
        var before = new DateTime(2026, 10, 21, 4, 0, 0, DateTimeKind.Utc);
        var after = new DateTime(2026, 10, 21, 13, 0, 0, DateTimeKind.Utc);

        Assert.True(latest.NewerThan("Sugar", "++Prime+Update60-CL-529371", before));
        Assert.False(latest.NewerThan("Sugar", "++Prime+Update60.1-CL-530866", before));
        Assert.False(latest.NewerThan("Sugar", "++Prime+Update60.2-CL-531000", after));
        Assert.False(latest.NewerThan("Unknown", "1", before));
        Assert.False(latest.NewerThan("Sugar", null, before));
    }

    [Fact]
    public void AnAppIdIsNeverPreferredAsATitle()
    {
        const string hex = "94bc5ec13f8f438c97fdbef3e9019e27";
        Assert.Equal("LEGO Fortnite Odyssey", EpicEglMerge.PreferTitle(hex, "LEGO Fortnite Odyssey", hex));
        Assert.Equal("Satisfactory", EpicEglMerge.PreferTitle("Satisfactory", hex, hex));
        Assert.Equal("Satisfactory", EpicEglMerge.PreferTitle("CrabEA", "Satisfactory", "CrabEA"));
        Assert.Equal("Satisfactory", EpicEglMerge.PreferTitle("Satisfactory", "CrabEA", "CrabEA"));
    }

    [Fact]
    public void TheOwnedListNamesAnInstallKnownOnlyByItsAppId()
    {
        var installed = new GameEntry
        {
            Id = "epic:CrabEA", Title = "CrabEA", Store = StoreKind.Epic, Installed = true, LaunchTarget = "CrabEA", Path = @"C:\Games\Satisfactory",
        };
        var owned = new LegendaryCli.GameRow("CrabEA", "Satisfactory", null, null, false) { Categories = ["games"] };

        var game = Assert.Single(EpicAdapter.MergeOwned([installed], [owned], hasLegendary: true));

        Assert.Equal("Satisfactory", game.Title);
        Assert.True(game.Installed);
    }

    [Fact]
    public void LegendarySayingNotInstalledIsNotARemoval()
    {
        Assert.True(LegendaryCli.NotInstalled("[cli] ERROR: Game CrabEA not installed, cannot uninstall!"));
        Assert.Equal("com.epicgames.launcher://apps/9773%3A530d%3ASugar?action=uninstall",
            EpicAdapter.BuildEpicActionUris("Sugar", "9773", "530d", "uninstall").First());
    }
}
