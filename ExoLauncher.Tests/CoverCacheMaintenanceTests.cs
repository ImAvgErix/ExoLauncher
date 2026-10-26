using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways cover-cache maintenance could eat the cache it keeps:
/// - a library whose protected art alone passes the water mark has every other file (the thumbnails) deleted on each pass,
///   and writing them again starts the next pass;
/// - a cache past its mark with little protected art is not trimmed;
/// - a file newer than the minimum age, or protected, goes.
/// </summary>
public sealed class CoverCacheMaintenanceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-cache-" + Guid.NewGuid().ToString("N"));

    // 10 files high, 5 low: small enough to cross with a handful of files.
    private static readonly CoverArtService.CacheMaintenancePolicy Policy = new(
        HighWaterBytes: 1L << 30, LowWaterBytes: 1L << 29, HighWaterFiles: 10, LowWaterFiles: 5,
        MaxUnreferencedAge: TimeSpan.FromDays(90), MinimumEvictionAge: TimeSpan.FromMinutes(15));

    public CoverCacheMaintenanceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private void Image(string name, TimeSpan age)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);
        File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
    }

    [Fact]
    public void ProtectedArtThatPassesTheMarkDoesNotEmptyTheRest()
    {
        // 30 files the cache never evicts (profile pictures), more than the mark of 10, and four old thumbnails.
        for (var i = 0; i < 30; i++) Image($"profile-{i}.jpg", TimeSpan.FromDays(200));
        foreach (var name in new[] { "thumb-a.jpg", "thumb-b.jpg", "thumb-c.jpg", "thumb-d.jpg" }) Image(name, TimeSpan.FromHours(2));

        var result = CoverArtService.RunCacheMaintenance(_root, [], Now, Policy);

        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(Path.Combine(_root, "thumb-a.jpg")));
    }

    [Fact]
    public void UnprotectedFilesPastTheMarkAreTrimmedOldestFirstToTheLowMark()
    {
        // 14 unprotected thumbnails, oldest first by name, over the mark of 10: trimmed down to 5.
        for (var i = 0; i < 14; i++) Image($"thumb-{i:00}.jpg", TimeSpan.FromHours(30 - i));

        var result = CoverArtService.RunCacheMaintenance(_root, [], Now, Policy);

        Assert.Equal(9, result.DeletedFiles);
        Assert.Equal(5, Directory.GetFiles(_root, "thumb-*.jpg").Length);
        Assert.False(File.Exists(Path.Combine(_root, "thumb-00.jpg")));   // the oldest went
        Assert.True(File.Exists(Path.Combine(_root, "thumb-13.jpg")));    // the newest stayed
    }

    [Fact]
    public void AFileNewerThanTheMinimumAgeIsLeftWhateverTheCount()
    {
        for (var i = 0; i < 14; i++) Image($"thumb-{i:00}.jpg", TimeSpan.FromMinutes(5));
        Assert.Equal(0, CoverArtService.RunCacheMaintenance(_root, [], Now, Policy).DeletedFiles);
    }
}
