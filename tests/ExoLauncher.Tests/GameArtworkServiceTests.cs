using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class GameArtworkServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-artwork-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Settings_CustomCoverMutationIsAtomicAcrossGroupedSourceIds()
    {
        var settings = new SettingsService(new AppSettings());
        var fileName = "custom-cover-" + new string('a', 64) + ".png";

        settings.SetCustomCoverImages(["steam:10", "epic:offer"], fileName);

        Assert.Equal(fileName, settings.Current.CustomCoverImages["steam:10"]);
        Assert.Equal(fileName, settings.Current.CustomCoverImages["epic:offer"]);

        settings.SetCustomCoverImages(["steam:10", "epic:offer"], null);

        Assert.Empty(settings.Current.CustomCoverImages);
        Assert.Throws<InvalidDataException>(() =>
            settings.SetCustomCoverImages(["steam:10"], "../settings.json"));
        Assert.Throws<InvalidDataException>(() =>
            settings.SetCustomCoverImages(["steam:10", "bad\nsource"], fileName));
        Assert.Empty(settings.Current.CustomCoverImages);
    }

    [Fact]
    public async Task LibraryOverlay_UsesOneLocalCustomCoverForEveryVariant_WithoutChangingSourceRows()
    {
        Directory.CreateDirectory(CoverArtService.CacheRoot);
        var fileName = "custom-cover-" + new string('b', 64) + ".jpg";
        await File.WriteAllBytesAsync(Path.Combine(CoverArtService.CacheRoot, fileName), JpegHeader(600, 900));
        var settings = new SettingsService(new AppSettings
        {
            CustomCoverImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["steam:10"] = fileName,
                ["epic:offer"] = fileName,
            },
        });
        var adapter = new ArtworkAdapter(
        [
            Game("steam:10", "Shared title", StoreKind.Steam, "10"),
            Game("epic:offer", "Shared title", StoreKind.Epic, "offer"),
        ]);
        var library = new LibraryService([adapter], settings);

        var cards = await library.GetLibraryAsync(force: true);
        var card = Assert.Single(cards);

        Assert.Equal("custom", card.CoverSource);
        Assert.Equal($"{CoverArtService.VirtualHostOrigin}/{fileName}", card.CoverUrl);
        Assert.Equal(card.Id, library.FindVisualCard("epic:offer")?.Id);
        Assert.Equal("custom", library.Find("epic:offer")?.CoverSource);
        Assert.All(library.FindVisualSources("steam:10"), source =>
            Assert.NotEqual("custom", source.CoverSource));
    }

    [Fact]
    public async Task ArtworkRevision_IsSharedByAVisualCardAndReturnedEvenWhenResetHasNoCover()
    {
        var settings = new SettingsService(new AppSettings());
        const string title = "Exo missing artwork fixture 7f219a";
        var adapter = new ArtworkAdapter(
        [
            Game("local:no-art", title, StoreKind.Local, launchTarget: null),
            Game("amazon:no-art", title, StoreKind.Amazon, launchTarget: null),
        ]);
        var library = new LibraryService([adapter], settings);
        _ = await library.GetLibraryAsync(force: true);

        var changed = await library.PublishArtworkChangeAsync("amazon:no-art", recomputeComputedCovers: true);

        Assert.NotNull(changed);
        Assert.Null(changed!.CoverUrl);
        Assert.True(changed.ArtRevision > 0);
        Assert.Equal(changed.ArtRevision, library.FindVisualCard("local:no-art")?.ArtRevision);
    }

    [Fact]
    public async Task ReplaceAndReset_ReturnAuthoritativeOwnedUninstalledCard_AndCleanTheUnusedCopy()
    {
        const string id = "local:owned-uninstalled-art";
        var settings = new SettingsService(new AppSettings());
        var library = new LibraryService(
            [new ArtworkAdapter([Game(id, "Owned artwork fixture", StoreKind.Local, launchTarget: null)])],
            settings);
        _ = await library.GetLibraryAsync(force: true);
        var picked = Path.Combine(_root, "picked.png");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(picked, ValidPortraitPng());
        var artwork = new GameArtworkService(library, settings);

        var replaced = await artwork.ReplaceAsync(id, picked);

        Assert.True(replaced.Ok, replaced.Message);
        Assert.Equal("custom", replaced.Game?.CoverSource);
        Assert.True(replaced.ArtRevision > 0);
        var storedName = Assert.Single(settings.Current.CustomCoverImages).Value;
        Assert.True(File.Exists(Path.Combine(CoverArtService.CacheRoot, storedName)));

        var reset = await artwork.ResetAsync(id);

        Assert.True(reset.Ok, reset.Message);
        Assert.Null(reset.Game?.CoverUrl);
        Assert.True(reset.ArtRevision > replaced.ArtRevision);
        Assert.Empty(settings.Current.CustomCoverImages);
        Assert.False(File.Exists(Path.Combine(CoverArtService.CacheRoot, storedName)));
    }

    [Fact]
    public async Task Report_IsUtf8BoundedAndScrubsPathSeparators()
    {
        var title = "Broken\nC:\\Users\\person\\Pictures\\cover.png " + new string('\u754c', 8_000);
        var library = new LibraryService(
            [new ArtworkAdapter([Game("local:report/path", title, StoreKind.Local, launchTarget: null)])],
            new SettingsService(new AppSettings()));
        _ = await library.GetLibraryAsync(force: true);
        var artwork = new GameArtworkService(library, new SettingsService(new AppSettings()));

        var report = artwork.BuildReport("local:report/path");

        Assert.True(report.Ok, report.Message);
        Assert.NotNull(report.Diagnostics);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(report.Diagnostics!) <= GameArtworkService.MaxReportBytes);
        Assert.DoesNotContain("\\", report.Diagnostics!, StringComparison.Ordinal);
        Assert.DoesNotContain("/path", report.Diagnostics!, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactEviction_ProtectsCustomProfileTitleMapAndEveryOtherGame()
    {
        var cache = Path.Combine(_root, "covers");
        Directory.CreateDirectory(cache);
        var target = Game("steam:10", "Target", StoreKind.Steam, "10");
        var other = Game("steam:20", "Other", StoreKind.Steam, "20");
        var custom = "custom-cover-" + new string('c', 64) + ".jpg";
        var names = new[]
        {
            "10.jpg", "10_2x.jpg", "steam_10.jpg", "hero_steam_10.jpg",
            "20.jpg", "steam_20.jpg", "hero_steam_20.jpg",
            custom, "profile-avatar-aaaaaaaaaaaaaaaa.png", "title-steam-map.json",
        };
        foreach (var name in names) File.WriteAllText(Path.Combine(cache, name), name);

        var deleted = CoverArtService.EvictComputedCacheFiles(cache, [target], [target, other]);

        Assert.True(deleted > 0);
        Assert.False(File.Exists(Path.Combine(cache, "10.jpg")));
        Assert.False(File.Exists(Path.Combine(cache, "steam_10.jpg")));
        Assert.True(File.Exists(Path.Combine(cache, "20.jpg")));
        Assert.True(File.Exists(Path.Combine(cache, "steam_20.jpg")));
        Assert.True(File.Exists(Path.Combine(cache, "hero_steam_20.jpg")));
        Assert.True(File.Exists(Path.Combine(cache, custom)));
        Assert.True(File.Exists(Path.Combine(cache, "profile-avatar-aaaaaaaaaaaaaaaa.png")));
        Assert.True(File.Exists(Path.Combine(cache, "title-steam-map.json")));
    }

    [Fact]
    public async Task ArtworkGate_SerializesRefetchAgainstAnExistingWarm()
    {
        var inside = 0;
        var maxInside = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Work(bool first)
        {
            var count = Interlocked.Increment(ref inside);
            InterlockedExtensions.Max(ref maxInside, count);
            if (first)
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
            }
            Interlocked.Decrement(ref inside);
        }

        var warm = CoverArtService.RunSerializedArtworkOperationAsync("steam:10", () => Work(first: true));
        await firstEntered.Task;
        var refetch = CoverArtService.RunSerializedArtworkOperationAsync("steam:10", () => Work(first: false));
        await Task.Delay(30);
        Assert.False(refetch.IsCompleted);
        releaseFirst.SetResult();

        await Task.WhenAll(warm, refetch);
        Assert.Equal(1, maxInside);
    }

    private static GameEntry Game(string id, string title, StoreKind store, string? launchTarget) => new()
    {
        Id = id,
        Title = title,
        Store = store,
        Installed = false,
        Owned = true,
        CanInstall = true,
        LaunchTarget = launchTarget,
        Status = "Owned",
    };

    private static byte[] JpegHeader(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xC0, 0x00, 0x11, 0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00,
        0xFF, 0xD9,
    ];

    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[33];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        bytes[11] = 13;
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';
        bytes[16] = (byte)(width >> 24);
        bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24);
        bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;
        return bytes;
    }

    private static byte[] ValidPortraitPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABgCAIAAAAip+O/AAAAaklEQVR42u3PMQ0AMAgAMJgGRCAC/7pmgoekddCsnrjsxXECAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIC+z5b2wE432X3WwAAAABJRU5ErkJggg==");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private sealed class ArtworkAdapter(IReadOnlyList<GameEntry> games) : IStoreAdapter
    {
        public string Id => "artwork-test";
        public string DisplayName => "Artwork test";
        public StoreKind Store => StoreKind.Local;
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) => Task.FromResult(games);
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = false, Message = "Not used." });
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "Not used." });
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "Not used." });
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "Not used." });
        public InstallProgress GetDownloadProgress(string gameId) => new() { GameId = gameId };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref location);
                if (candidate <= current || Interlocked.CompareExchange(ref location, candidate, current) == current) return;
            }
        }
    }
}
