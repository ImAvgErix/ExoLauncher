using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class CoverCacheMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "exo-cover-maintenance-" + Guid.NewGuid().ToString("N"));

    public CoverCacheMaintenanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* test cleanup is best effort */ }
    }

    [Fact]
    public void RunCacheMaintenance_EvictsOldestUnreferencedFilesToLowWater()
    {
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        Write("100.jpg", 100, now.AddDays(-40));
        Write("oldest.jpg", 100, now.AddDays(-30));
        Write("older.jpg", 100, now.AddDays(-20));
        Write("newest.jpg", 100, now.AddDays(-10));

        var active = new GameEntry
        {
            Id = "steam:100",
            Title = "Active",
            Store = StoreKind.Steam,
            LaunchTarget = "100",
            CoverUrl = CoverArtService.VirtualHostOrigin + "/100.jpg",
        };
        var policy = Policy(highBytes: 350, lowBytes: 200, maxAge: TimeSpan.FromDays(365));

        var result = CoverArtService.RunCacheMaintenance(_root, [active], now, policy);

        Assert.True(File.Exists(Path.Combine(_root, "100.jpg")));
        Assert.False(File.Exists(Path.Combine(_root, "oldest.jpg")));
        Assert.False(File.Exists(Path.Combine(_root, "older.jpg")));
        Assert.True(File.Exists(Path.Combine(_root, "newest.jpg")));
        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(200, result.DeletedBytes);
        Assert.Equal(2, result.RemainingFiles);
        Assert.Equal(200, result.RemainingBytes);
    }

    [Fact]
    public void RunCacheMaintenance_ExpiresOnlyOldUnreferencedArtAndPreservesSpecialFiles()
    {
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        Write("active-game.jpg", 64, now.AddDays(-200));
        Write("stale.jpg", 64, now.AddDays(-100));
        Write("fresh.jpg", 64, now.AddDays(-20));
        Write("profile-banner-deadbeef.jpg", 64, now.AddDays(-200));
        Write("inflight.jpg.part", 64, now.AddDays(-200));
        Write("title-steam-map.json", 64, now.AddDays(-200));

        var active = new GameEntry
        {
            Id = "active-game",
            Title = "Active",
            Store = StoreKind.Local,
            CoverUrl = CoverArtService.VirtualHostOrigin + "/active-game.jpg",
        };
        var policy = Policy(
            highBytes: 10_000,
            lowBytes: 9_000,
            maxAge: TimeSpan.FromDays(90));

        var result = CoverArtService.RunCacheMaintenance(_root, [active], now, policy);

        Assert.True(File.Exists(Path.Combine(_root, "active-game.jpg")));
        Assert.False(File.Exists(Path.Combine(_root, "stale.jpg")));
        Assert.True(File.Exists(Path.Combine(_root, "fresh.jpg")));
        Assert.True(File.Exists(Path.Combine(_root, "profile-banner-deadbeef.jpg")));
        Assert.True(File.Exists(Path.Combine(_root, "inflight.jpg.part")));
        Assert.True(File.Exists(Path.Combine(_root, "title-steam-map.json")));
        Assert.Equal(1, result.DeletedFiles);
    }

    [Fact]
    public void RunCacheMaintenance_RemovesOnlyByteIdenticalSteamDuplicates()
    {
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var identical = Enumerable.Repeat((byte)0x5A, 128).ToArray();
        Write("123.jpg", identical, now.AddDays(-1));
        Write("123_2x.jpg", identical, now.AddDays(-1));
        Write("steam_123.jpg", identical, now.AddDays(-1));
        Write("456.jpg", identical, now.AddDays(-1));
        Write("steam_456.jpg", Enumerable.Repeat((byte)0xA5, 128).ToArray(), now.AddDays(-1));

        var active = new GameEntry
        {
            Id = "steam:123",
            Title = "Active Steam Game",
            Store = StoreKind.Steam,
            LaunchTarget = "123",
            CoverUrl = CoverArtService.VirtualHostOrigin + "/123.jpg",
        };
        var policy = Policy(
            highBytes: 10_000,
            lowBytes: 9_000,
            maxAge: TimeSpan.FromDays(365));

        var result = CoverArtService.RunCacheMaintenance(_root, [active], now, policy);

        Assert.True(File.Exists(Path.Combine(_root, "123.jpg")));
        Assert.False(File.Exists(Path.Combine(_root, "123_2x.jpg")));
        Assert.False(File.Exists(Path.Combine(_root, "steam_123.jpg")));
        Assert.True(File.Exists(Path.Combine(_root, "456.jpg")));
        Assert.True(File.Exists(Path.Combine(_root, "steam_456.jpg")));
        Assert.Equal(2, result.DeletedFiles);
    }

    [Fact]
    public void RunCacheMaintenance_DoesNotEvictRecentlyWrittenFilesUnderPressure()
    {
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        Write("recent.jpg", 500, now.AddMinutes(-2));
        var policy = Policy(
            highBytes: 100,
            lowBytes: 50,
            maxAge: TimeSpan.FromDays(1),
            minimumAge: TimeSpan.FromMinutes(15));

        var result = CoverArtService.RunCacheMaintenance(
            _root,
            Array.Empty<GameEntry>(),
            now,
            policy);

        Assert.True(File.Exists(Path.Combine(_root, "recent.jpg")));
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(500, result.RemainingBytes);
    }

    [Fact]
    public void RunCacheMaintenance_AppliesFileCountHighAndLowWaterMarks()
    {
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        for (var index = 1; index <= 5; index++)
            Write($"cover-{index}.jpg", 10, now.AddDays(index - 6));
        var policy = new CoverArtService.CacheMaintenancePolicy(
            HighWaterBytes: 10_000,
            LowWaterBytes: 9_000,
            HighWaterFiles: 4,
            LowWaterFiles: 2,
            MaxUnreferencedAge: TimeSpan.FromDays(365),
            MinimumEvictionAge: TimeSpan.Zero);

        var result = CoverArtService.RunCacheMaintenance(
            _root,
            Array.Empty<GameEntry>(),
            now,
            policy);

        Assert.False(File.Exists(Path.Combine(_root, "cover-1.jpg")));
        Assert.False(File.Exists(Path.Combine(_root, "cover-2.jpg")));
        Assert.False(File.Exists(Path.Combine(_root, "cover-3.jpg")));
        Assert.True(File.Exists(Path.Combine(_root, "cover-4.jpg")));
        Assert.True(File.Exists(Path.Combine(_root, "cover-5.jpg")));
        Assert.Equal(2, result.RemainingFiles);
    }

    private static CoverArtService.CacheMaintenancePolicy Policy(
        long highBytes,
        long lowBytes,
        TimeSpan maxAge,
        TimeSpan? minimumAge = null) => new(
        HighWaterBytes: highBytes,
        LowWaterBytes: lowBytes,
        HighWaterFiles: 1_000,
        LowWaterFiles: 900,
        MaxUnreferencedAge: maxAge,
        MinimumEvictionAge: minimumAge ?? TimeSpan.Zero);

    private void Write(string name, int length, DateTimeOffset lastWrite) =>
        Write(name, new byte[length], lastWrite);

    private void Write(string name, byte[] bytes, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
    }
}
