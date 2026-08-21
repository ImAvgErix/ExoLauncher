using System.IO.Compression;
using System.Text;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class NileCliTests
{
    [Fact]
    public void Args_MatchTheDocumentedNileSurface()
    {
        Assert.Equal(["auth", "--login"], NileCli.AuthLoginArgs());
        Assert.Equal(["auth", "--status"], NileCli.AuthStatusArgs());
        Assert.Equal(["library", "list", "--json"], NileCli.LibraryListArgs());
        Assert.Equal(["library", "sync"], NileCli.LibrarySyncArgs());
        Assert.Equal(["install", "abc", "--base-path", @"C:\Games"], NileCli.InstallArgs("abc", @"C:\Games"));
        Assert.Equal(["update", "abc"], NileCli.UpdateArgs("abc"));
        Assert.Equal(["verify", "abc"], NileCli.VerifyArgs("abc"));
        Assert.Equal(["launch", "abc"], NileCli.LaunchArgs("abc"));
        Assert.Equal(["uninstall", "abc"], NileCli.UninstallArgs("abc"));
    }

    [Fact]
    public void AuthStatus_RequiresLoggedInTrue()
    {
        Assert.True(NileCli.IsAuthenticatedStatusResponse(0, """{"Username":"Ada","LoggedIn":true}"""));
        Assert.False(NileCli.IsAuthenticatedStatusResponse(0, """{"Username":" ","LoggedIn":false}"""));
        Assert.False(NileCli.IsAuthenticatedStatusResponse(1, """{"LoggedIn":true}"""));
        Assert.False(NileCli.IsAuthenticatedStatusResponse(0, "not json"));
        Assert.False(NileCli.IsAuthenticatedStatusResponse(0, ""));
    }

    [Fact]
    public void CurrentUserSession_RequiresAUserId()
    {
        Assert.True(NileCli.IsCurrentUserSession("""{"name":"Ada","user_id":"amzn1.account.abc"}"""));
        Assert.False(NileCli.IsCurrentUserSession("""{"name":"Ada"}"""));
        Assert.False(NileCli.IsCurrentUserSession(""));
        Assert.False(NileCli.IsCurrentUserSession("{"));
    }

    [Fact]
    public void HasLocalSession_IsFalseWhenNoConfigExists()
    {
        Assert.False(NileCli.HasLocalSession(
            [Path.Combine(Path.GetTempPath(), "exo-nile-missing-" + Guid.NewGuid().ToString("N"))],
            _ => false,
            _ => null));
    }

    [Fact]
    public void HasLocalSession_AcceptsCurrentUserJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-nile-session-" + Guid.NewGuid().ToString("N"));
        Assert.True(NileCli.HasLocalSession(
            [root],
            path => path.EndsWith("current_user.json", StringComparison.OrdinalIgnoreCase),
            _ => """{"name":"Ada","user_id":"amzn1.account.abc"}"""));
    }

    [Fact]
    public void LibraryJson_ReadsProductIdAndTitle()
    {
        var rows = NileCli.ParseLibraryJson("""
            [{"id":"old","product":{"id":"prime-hades","title":"Hades"}}]
            """);
        var row = Assert.Single(rows);
        Assert.Equal("prime-hades", row.ProductId);
        Assert.Equal("Hades", row.Title);
        Assert.False(row.Installed);
    }

    [Fact]
    public void Merge_MarksInstalledRowsFromInstalledJson()
    {
        var owned = NileCli.ParseLibraryJson("""
            [{"product":{"id":"prime-hades","title":"Hades"}},{"product":{"id":"prime-gone","title":"Gone"}}]
            """);
        var installed = NileCli.ParseInstalledJson("""
            [{"id":"prime-hades","path":"D:\\Amazon\\Hades","size":123}]
            """);
        var merged = NileCli.MergeOwnedAndInstalled(owned, installed);
        var hades = Assert.Single(merged, row => row.ProductId == "prime-hades");
        Assert.True(hades.Installed);
        Assert.Equal(@"D:\Amazon\Hades", hades.InstallPath);
        Assert.Equal(123, hades.SizeBytes);
        Assert.False(Assert.Single(merged, row => row.ProductId == "prime-gone").Installed);
    }

    [Fact]
    public void ParseLibraryJson_EmptyOrMalformedIsNoLibrary()
    {
        Assert.Empty(NileCli.ParseLibraryJson(""));
        Assert.Empty(NileCli.ParseLibraryJson("{"));
        Assert.Empty(NileCli.ParseLibraryJson("{}"));
        Assert.Empty(NileCli.ParseInstalledJson("[]"));
    }

    [Fact]
    public void ProgressLine_ReadsPercentAndSpeed()
    {
        Assert.True(NileCli.TryParseProgressLine(
            "= Progress: 45.23 12345/67890, Running for: 00:01:00, ETA: 00:02:00",
            out var percent, out _, out _));
        Assert.Equal(45.23, percent);

        Assert.True(NileCli.TryParseProgressLine(
            " + Download\t- 12.34 MiB/s",
            out _, out var bps, out _));
        Assert.NotNull(bps);
        Assert.InRange(bps!.Value, 12.3 * 1024 * 1024, 12.4 * 1024 * 1024);
    }

    [Fact]
    public void HasAnyBinary_IsFalseWhenNothingExists()
    {
        Assert.False(NileCli.HasAnyBinary(_ => false, ["C:\\missing\\nile.exe"]));
    }

    [Fact]
    public void ReadCachedLibrary_IsEmptyWhenConfigIsAbsent()
    {
        Assert.Empty(NileCli.ReadCachedLibrary(
            [Path.Combine(Path.GetTempPath(), "exo-nile-lib-missing-" + Guid.NewGuid().ToString("N"))],
            _ => false,
            _ => null));
    }

    [Fact]
    public void ReadCachedLibrary_MergesLibraryAndInstalledJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-nile-cache-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("prime-hades", Assert.Single(NileCli.ReadCachedLibrary(
            [root],
            path => path.EndsWith("library.json", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("installed.json", StringComparison.OrdinalIgnoreCase),
            path => path.EndsWith("library.json", StringComparison.OrdinalIgnoreCase)
                ? """[{"product":{"id":"prime-hades","title":"Hades"}}]"""
                : """[{"id":"prime-hades","path":"D:\\Amazon\\Hades","size":50}]""")).ProductId);
    }
}

public sealed class StoreLayerMatrixHonestyTests
{
    [Fact]
    public void RiotDownloads_AreWiredWhenTheClientIsPresent()
    {
        var present = StoreLayerMatrix.For("riot", new StoreLayerMatrix.Context(
            ClientPresent: true,
            BackendPresent: true,
            SessionPresent: false,
            WebApiKeyPresent: false,
            LocalDatabasePresent: false));
        Assert.Equal(StoreLayerMatrix.Partial, present.Login);
        Assert.Equal(StoreLayerMatrix.Partial, present.Owned);
        Assert.Equal(StoreLayerMatrix.Wired, present.Downloads);
        Assert.Contains("never patches around anti-cheat", present.Note, StringComparison.Ordinal);

        var absent = StoreLayerMatrix.For("riot", new StoreLayerMatrix.Context(false, false, false, false, false));
        Assert.Equal(StoreLayerMatrix.None, absent.Login);
        Assert.Equal(StoreLayerMatrix.None, absent.Owned);
        Assert.Equal(StoreLayerMatrix.None, absent.Downloads);
    }

    [Fact]
    public void Amazon_TracksNileSessionHonestly()
    {
        var ready = StoreLayerMatrix.For("amazon", new StoreLayerMatrix.Context(
            ClientPresent: false,
            BackendPresent: true,
            SessionPresent: true,
            WebApiKeyPresent: false,
            LocalDatabasePresent: false));
        Assert.Equal(StoreLayerMatrix.Wired, ready.Login);
        Assert.Equal(StoreLayerMatrix.Wired, ready.Owned);
        Assert.Equal(StoreLayerMatrix.Wired, ready.Downloads);

        var sessionWithoutNile = StoreLayerMatrix.For(
            "amazon",
            new StoreLayerMatrix.Context(false, false, true, false, false));
        Assert.Equal(StoreLayerMatrix.Partial, sessionWithoutNile.Login);
        Assert.Equal(StoreLayerMatrix.None, sessionWithoutNile.Owned);
        Assert.Equal(StoreLayerMatrix.None, sessionWithoutNile.Downloads);
    }

    [Theory]
    [InlineData("epic")]
    [InlineData("gog")]
    public void AgentBackedStores_NeverClaimDownloadsWithoutTheirBackend(string store)
    {
        var sessionOnly = StoreLayerMatrix.For(
            store,
            new StoreLayerMatrix.Context(true, false, true, false, true));

        Assert.Equal(StoreLayerMatrix.None, sessionOnly.Owned);
        Assert.Equal(StoreLayerMatrix.None, sessionOnly.Downloads);
    }

    [Fact]
    public void NewListAndLaunchStores_ArePartialOwnedOnlyWhenTheClientIsPresent()
    {
        foreach (var store in new[] { "itch", "minecraft", "roblox", "paradox", "wargaming" })
        {
            var present = StoreLayerMatrix.For(
                store,
                new StoreLayerMatrix.Context(true, false, false, false, false));
            Assert.Equal(StoreLayerMatrix.None, present.Login);
            Assert.Equal(StoreLayerMatrix.Partial, present.Owned);
            Assert.Equal(StoreLayerMatrix.None, present.Downloads);
            Assert.Equal(StoreLayerMatrix.None, present.Social);

            var absent = StoreLayerMatrix.For(
                store,
                new StoreLayerMatrix.Context(false, false, false, false, false));
            Assert.Equal(StoreLayerMatrix.None, absent.Owned);
        }
    }

    [Fact]
    public void SteamOwned_RequiresAReadableLocalAccount()
    {
        var signedOut = StoreLayerMatrix.For(
            "steam",
            new StoreLayerMatrix.Context(true, true, false, true, false));
        Assert.Equal(StoreLayerMatrix.None, signedOut.Owned);
        Assert.Equal(StoreLayerMatrix.Partial, signedOut.Social);
        Assert.Equal(StoreLayerMatrix.Partial, signedOut.Login);

        var readableAccount = StoreLayerMatrix.For(
            "steam",
            new StoreLayerMatrix.Context(true, true, true, true, false));
        Assert.Equal(StoreLayerMatrix.Wired, readableAccount.Owned);
    }
}

public sealed class AdditionalOfficialLibraryTests
{
    [Fact]
    public void ItchReceipt_RequiresAnExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-itch-" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(root, "Celeste");
        var itchDir = Path.Combine(gameDir, ".itch");
        Directory.CreateDirectory(itchDir);
        var exe = Path.Combine(gameDir, "Celeste.exe");
        File.WriteAllText(exe, "MZ");
        WriteGzip(Path.Combine(itchDir, "receipt.json.gz"), """{"game":{"id":12345,"title":"Celeste"}}""");
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        try
        {
            var games = OfficialInstalledLibraries.ScanItchReceiptFolders(
                [root], Directory.Exists, File.Exists, File.ReadAllBytes);
            var game = Assert.Single(games);
            Assert.Equal("Celeste", game.Title);
            Assert.Equal(StoreKind.Itch, game.Store);
            Assert.Equal(exe, game.LaunchTarget);
            Assert.StartsWith("itch:", game.Id, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ItchScan_IsEmptyWhenTheClientTreeIsAbsent()
    {
        var missing = Path.Combine(Path.GetTempPath(), "exo-itch-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(OfficialInstalledLibraries.ScanItchReceiptFolders(
            [missing], Directory.Exists, File.Exists, _ => null));
    }

    [Fact]
    public void Minecraft_RequiresAVersionJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-mc-" + Guid.NewGuid().ToString("N"));
        var versions = Path.Combine(root, "versions", "1.21");
        Directory.CreateDirectory(versions);
        File.WriteAllText(Path.Combine(versions, "1.21.json"), "{}");
        var launcher = Path.Combine(root, "MinecraftLauncher.exe");
        File.WriteAllText(launcher, "MZ");
        try
        {
            var games = OfficialInstalledLibraries.ParseMinecraftInstalls(
                root, bedrockPresent: false, launcher, Directory.Exists, File.Exists);
            var game = Assert.Single(games);
            Assert.Equal("Minecraft", game.Title);
            Assert.Equal(StoreKind.Minecraft, game.Store);
            Assert.Equal(launcher, game.LaunchTarget);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Minecraft_IsEmptyWithoutVersionsOrBedrock()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-mc-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Empty(OfficialInstalledLibraries.ParseMinecraftInstalls(
                root, bedrockPresent: false, null, Directory.Exists, File.Exists));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roblox_RequiresThePlayerExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-roblox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var player = Path.Combine(root, "RobloxPlayerBeta.exe");
        File.WriteAllText(player, "MZ");
        try
        {
            var game = Assert.Single(OfficialInstalledLibraries.ParseRobloxInstalls([player], File.Exists));
            Assert.Equal("Roblox", game.Title);
            Assert.Equal(StoreKind.Roblox, game.Store);
            Assert.Equal(player, game.LaunchTarget);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Assert.Empty(OfficialInstalledLibraries.ParseRobloxInstalls(
            [Path.Combine(root, "missing.exe")], File.Exists));
    }

    [Fact]
    public void Paradox_SkipsTheLauncherFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-paradox-" + Guid.NewGuid().ToString("N"));
        var stellaris = Path.Combine(root, "Stellaris");
        var launcher = Path.Combine(root, "launcher");
        Directory.CreateDirectory(stellaris);
        Directory.CreateDirectory(launcher);
        var exe = Path.Combine(stellaris, "stellaris.exe");
        File.WriteAllText(exe, "MZ");
        File.WriteAllText(Path.Combine(launcher, "Paradox Launcher.exe"), "MZ");
        try
        {
            var game = Assert.Single(OfficialInstalledLibraries.ParseParadoxInstalls(
                [new("Stellaris", stellaris), new("launcher", launcher)],
                Directory.Exists,
                File.Exists));
            Assert.Equal("Stellaris", game.Title);
            Assert.Equal(StoreKind.Paradox, game.Store);
            Assert.Equal(exe, game.LaunchTarget);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Wargaming_RequiresGameInfoXml()
    {
        var xml = """<root><game_id>wot.eu.production</game_id><name>World of Tanks</name></root>""";
        var dir = Path.Combine(Path.GetTempPath(), "exo-wgc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "WorldOfTanks.exe");
        File.WriteAllText(exe, "MZ");
        try
        {
            var game = OfficialInstalledLibraries.ParseWargamingGameInfo(xml, dir, File.Exists);
            Assert.NotNull(game);
            Assert.Equal("World of Tanks", game!.Title);
            Assert.Equal(StoreKind.Wargaming, game.Store);
            Assert.Equal(exe, game.LaunchTarget);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void XboxMutablePackage_ReadsMicrosoftGameConfigAtTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-xbox-mutable-" + Guid.NewGuid().ToString("N"));
        var pkg = Path.Combine(root, "ForzaHorizon5");
        Directory.CreateDirectory(pkg);
        var exe = Path.Combine(pkg, "ForzaHorizon5.exe");
        File.WriteAllText(exe, "MZ");
        File.WriteAllText(
            Path.Combine(pkg, "MicrosoftGame.config"),
            """<Game><ShellVisuals DefaultDisplayName="Forza Horizon 5" /><ExecutableList><Executable Name="ForzaHorizon5.exe" /></ExecutableList></Game>""");
        try
        {
            var game = Assert.Single(OfficialInstalledLibraries.ScanXboxMutableFolders(
                [root], Directory.Exists, File.Exists));
            Assert.Equal("Forza Horizon 5", game.Title);
            Assert.Equal(StoreKind.Xbox, game.Store);
            Assert.Equal(exe, game.LaunchTarget);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NewStoreAdapters_AreOfficialClientsAndStayEmptyWhenAbsent()
    {
        Assert.IsAssignableFrom<IOfficialStoreClient>(new ItchAdapter());
        Assert.IsAssignableFrom<IOfficialStoreClient>(new MinecraftAdapter());
        Assert.IsAssignableFrom<IOfficialStoreClient>(new RobloxAdapter());
        Assert.IsAssignableFrom<IOfficialStoreClient>(new ParadoxAdapter());
        Assert.IsAssignableFrom<IOfficialStoreClient>(new WargamingAdapter());
        Assert.IsAssignableFrom<IOfficialStoreClient>(new AmazonAdapter());

        Assert.Empty(OfficialInstalledLibraries.ParseRobloxInstalls([], _ => false));
        Assert.Empty(OfficialInstalledLibraries.ParseParadoxInstalls([], _ => false, _ => false));
        Assert.Empty(OfficialInstalledLibraries.ScanWargamingGameInfo([], _ => false, _ => false, _ => null));
    }

    [Fact]
    public void NewStoreChrome_IsLauncherOnly()
    {
        Assert.Equal(["itch"], StoreWindowHider.ItchClientProcessNames);
        Assert.Equal(["MinecraftLauncher"], StoreWindowHider.MinecraftClientProcessNames);
        Assert.Equal(["RobloxPlayerLauncher"], StoreWindowHider.RobloxClientProcessNames);
        Assert.DoesNotContain("RobloxPlayerBeta", StoreWindowHider.RobloxClientProcessNames);
        Assert.DoesNotContain("Minecraft", StoreWindowHider.MinecraftClientProcessNames);
        Assert.Equal(["wgc"], StoreWindowHider.WargamingClientProcessNames);
    }

    [Fact]
    public void ClientHandoff_IsHonestWhenTheOfficialClientIsMissing()
    {
        var missing = OfficialInstalledLibraries.ClientHandoff("Xbox app", opened: false, "install");
        Assert.False(missing.Ok);
        Assert.False(missing.HandoffOnly);
        Assert.Contains("not installed", missing.Message, StringComparison.OrdinalIgnoreCase);

        var opened = OfficialInstalledLibraries.ClientHandoff("Xbox app", opened: true, "install");
        Assert.True(opened.Ok);
        Assert.True(opened.HandoffOnly);
        Assert.Contains("Opened Xbox app", opened.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonAdapter_DoesNotFetchNileOnLibraryScan()
    {
        var amazon = File.ReadAllText(FindRepoFile("ExoLauncher", "Adapters", "AmazonAdapter.cs"));
        var getLibrary = Slice(amazon, "public Task<IReadOnlyList<GameEntry>> GetLibraryAsync", "public async Task<InstallResult> InstallAsync");
        Assert.Contains("NileCli.ReadCachedLibrary", getLibrary, StringComparison.Ordinal);
        Assert.Contains("OfficialInstalledLibraries.ScanAmazon()", getLibrary, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureNileAsync", getLibrary, StringComparison.Ordinal);
        Assert.DoesNotContain("CliRunner.RunAsync", getLibrary, StringComparison.Ordinal);
    }

    [Fact]
    public void AppServices_RegistersTheNewAdapters()
    {
        var services = File.ReadAllText(FindRepoFile("ExoLauncher", "Services", "AppServices.cs"));
        Assert.Contains("new AmazonAdapter()", services, StringComparison.Ordinal);
        Assert.Contains("new ItchAdapter()", services, StringComparison.Ordinal);
        Assert.Contains("new MinecraftAdapter()", services, StringComparison.Ordinal);
        Assert.Contains("new RobloxAdapter()", services, StringComparison.Ordinal);
        Assert.Contains("new ParadoxAdapter()", services, StringComparison.Ordinal);
        Assert.Contains("new WargamingAdapter()", services, StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var joined = Path.Combine(relative);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, joined);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(joined);
    }

    private static string Slice(string src, string start, string end)
    {
        var a = src.IndexOf(start, StringComparison.Ordinal);
        var b = src.IndexOf(end, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, start);
        return src[a..b];
    }

    private static void WriteGzip(string path, string json)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        var bytes = Encoding.UTF8.GetBytes(json);
        gzip.Write(bytes, 0, bytes.Length);
    }
}
