using System.Text;
using System.Text.Json;
using System.Security.Principal;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ExoOnlineCacheTests
{
    [Fact]
    public void JsonCache_RoundTripsTypedDataWithoutPlaintextOnDisk()
    {
        using var directory = new TemporaryDirectory();
        var cache = new ExoOnlineCache(directory.Path);
        var sync = new DateTimeOffset(2026, 8, 19, 12, 34, 56, TimeSpan.FromHours(-5));
        var payload = new CachedPayload("Erix", "token-must-not-be-plaintext");

        Assert.True(cache.Write("immutable-user-123", "profile:me", payload, sync));
        Assert.True(cache.TryRead<CachedPayload>(
            "immutable-user-123",
            "profile:me",
            out var restored,
            out var restoredSync));

        Assert.Equal(payload, restored);
        Assert.Equal(sync.ToUniversalTime(), restoredSync);

        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.bin"));
        Assert.DoesNotContain("immutable-user-123", Path.GetFileName(file), StringComparison.Ordinal);
        Assert.DoesNotContain("profile:me", Path.GetFileName(file), StringComparison.Ordinal);
        var encrypted = File.ReadAllBytes(file);
        Assert.Equal(-1, encrypted.AsSpan().IndexOf(Encoding.UTF8.GetBytes("immutable-user-123")));
        Assert.Equal(-1, encrypted.AsSpan().IndexOf(Encoding.UTF8.GetBytes("profile:me")));
        Assert.Equal(-1, encrypted.AsSpan().IndexOf(Encoding.UTF8.GetBytes(payload.Token)));
        Assert.Equal(-1, encrypted.AsSpan().IndexOf(Encoding.UTF8.GetBytes(payload.DisplayName)));
        var sddl = ExoSessionFileAcl.ReadSddl(file);
        Assert.Contains("D:P", sddl, StringComparison.Ordinal);
        Assert.Contains(WindowsIdentity.GetCurrent().User!.Value, sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;WD)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;BU)", sddl, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void JsonCache_RejectsOversizedWritesWithoutReplacingLastGoodData()
    {
        using var directory = new TemporaryDirectory();
        var cache = new ExoOnlineCache(directory.Path);
        var stamp = new DateTimeOffset(2026, 8, 19, 18, 0, 0, TimeSpan.Zero);
        var lastGood = new CachedPayload("last-good", "still-encrypted");
        Assert.True(cache.Write("user-a", "profile:me", lastGood, stamp));

        var oversized = new LargePayload(new string('x', ExoOnlineCache.MaxPlaintextEntryBytes));
        Assert.False(cache.Write("user-a", "profile:me", oversized, stamp.AddMinutes(1)));

        Assert.True(cache.TryRead<CachedPayload>(
            "user-a", "profile:me", out var restored, out var restoredStamp));
        Assert.Equal(lastGood, restored);
        Assert.Equal(stamp, restoredStamp);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void JsonCache_PrefixRemovalIsAccountScopedAndClearRemovesEverySafeEntry()
    {
        using var directory = new TemporaryDirectory();
        var cache = new ExoOnlineCache(directory.Path);
        var stamp = DateTimeOffset.UtcNow;
        Assert.True(cache.Write("user-a", "profile:one", 1, stamp));
        Assert.True(cache.Write("user-a", "profile:two", 2, stamp));
        Assert.True(cache.Write("user-a", "friends:list", 3, stamp));
        Assert.True(cache.Write("user-b", "profile:one", 4, stamp));

        cache.RemoveByPrefix("user-a", "profile:");

        Assert.False(cache.TryRead<int>("user-a", "profile:one", out _, out _));
        Assert.False(cache.TryRead<int>("user-a", "profile:two", out _, out _));
        Assert.True(cache.TryRead<int>("user-a", "friends:list", out var friends, out _));
        Assert.Equal(3, friends);
        Assert.True(cache.TryRead<int>("user-b", "profile:one", out var other, out _));
        Assert.Equal(4, other);

        cache.Clear();

        Assert.Empty(Directory.GetFiles(directory.Path, "*.bin"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void JsonCache_PrunesOldestEntriesToBothCountAndDiskBounds()
    {
        using var directory = new TemporaryDirectory();
        var countRoot = Path.Combine(directory.Path, "count");
        var countCache = new ExoOnlineCache(countRoot);
        string? oldestCountEntry = null;
        for (var index = 0; index <= ExoOnlineCache.MaxEntries; index++)
        {
            var before = Files(countRoot, "*.bin");
            Assert.True(countCache.Write("user", $"small:{index:D3}", index, DateTimeOffset.UtcNow));
            var added = Assert.Single(Files(countRoot, "*.bin").Except(before));
            oldestCountEntry ??= added;
            File.SetLastWriteTimeUtc(added, new DateTime(2000, 1, 1).AddMinutes(index));
        }

        Assert.False(File.Exists(oldestCountEntry));
        Assert.True(Files(countRoot, "*.bin").Length <= ExoOnlineCache.MaxEntries);

        var diskRoot = Path.Combine(directory.Path, "disk");
        var diskCache = new ExoOnlineCache(diskRoot);
        var large = new LargePayload(new string('z', 505 * 1024));
        string? oldestDiskEntry = null;
        for (var index = 0; index < 32; index++)
        {
            var before = Files(diskRoot, "*.bin");
            Assert.True(diskCache.Write("user", $"large:{index:D3}", large, DateTimeOffset.UtcNow));
            var added = Assert.Single(Files(diskRoot, "*.bin").Except(before));
            oldestDiskEntry ??= added;
            File.SetLastWriteTimeUtc(added, new DateTime(2001, 1, 1).AddMinutes(index));
        }

        Assert.True(diskCache.Write("user", "large:trigger", large, DateTimeOffset.UtcNow));

        var diskFiles = Files(diskRoot, "*.bin");
        Assert.False(File.Exists(oldestDiskEntry));
        Assert.True(diskFiles.Sum(path => new FileInfo(path).Length) <= ExoOnlineCache.MaxDiskBytes);
        Assert.True(diskFiles.Length <= ExoOnlineCache.MaxEntries);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ProfileMedia_StoresAValidatedImageAsOnlyASafeLocalReference()
    {
        using var directory = new TemporaryDirectory();
        var cache = new ExoProfileMediaCache(directory.Path);
        var bytes = Png(256);
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
        var metadata = new ExoProfileMediaMetadata
        {
            Kind = "avatar",
            Version = "profile-version-7",
            Url = "file:///C:/private/source/avatar.png?token=source-secret",
            ContentType = "image/png",
            Size = bytes.Length,
            Sha256 = sha256,
        };

        var stored = await cache.TryStoreAsync(
            "immutable-user-123",
            "avatar",
            "profile-version-7",
            new MemoryStream(bytes),
            metadata,
            CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Matches("^profile-[0-9a-f]{64}\\.png$", stored!.FileName);
        Assert.Equal($"{ExoProfileMediaCache.VirtualHostOrigin}/{stored.FileName}", stored.Url);
        Assert.Equal("image/png", stored.ContentType);
        Assert.Equal(bytes.Length, stored.Size);
        Assert.Equal(sha256, stored.Sha256);
        Assert.DoesNotContain("immutable-user-123", stored.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("profile-version-7", stored.FileName, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(directory.Path, stored.FileName), cache.ResolvePath(stored.FileName));

        var serialized = JsonSerializer.Serialize(stored);
        Assert.DoesNotContain("source-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("C:/private/source", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(ExoProfileMediaLocalRef).GetProperties(),
            property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ProfileMedia_InvalidResponseNeverReplacesTheLastGoodFile()
    {
        using var directory = new TemporaryDirectory();
        var cache = new ExoProfileMediaCache(directory.Path);
        var original = Png(512);
        var metadata = Media("avatar", "same-version", "image/png", original);
        var first = await cache.TryStoreAsync(
            "user", "avatar", "same-version", new MemoryStream(original), metadata);
        Assert.NotNull(first);
        var path = cache.ResolvePath(first!.FileName);
        Assert.NotNull(path);

        var invalid = Png(768);
        var invalidMetadata = Media("avatar", "same-version", "image/png", invalid) with
        {
            Sha256 = new string('0', 64),
        };
        var rejected = await cache.TryStoreAsync(
            "user", "avatar", "same-version", new MemoryStream(invalid), invalidMetadata);

        Assert.Null(rejected);
        Assert.Equal(original, File.ReadAllBytes(path!));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ProfileMedia_TryGetRevalidatesLastGoodBytesWithoutANewStream()
    {
        using var directory = new TemporaryDirectory();
        var bytes = WebP(256);
        var metadata = Media("banner", "fallback-v1", "image/webp", bytes);
        var writer = new ExoProfileMediaCache(directory.Path);
        var stored = await writer.TryStoreAsync(
            "user", "banner", "fallback-v1", new MemoryStream(bytes), metadata);
        Assert.NotNull(stored);

        var reopened = new ExoProfileMediaCache(directory.Path);
        var cached = reopened.TryGet("user", "banner", "fallback-v1", metadata);

        Assert.Equal(stored, cached);
        Assert.Null(reopened.TryGet(
            "user", "banner", "fallback-v1", metadata with { Sha256 = new string('0', 64) }));

        var path = reopened.ResolvePath(stored!.FileName);
        Assert.NotNull(path);
        var tampered = File.ReadAllBytes(path!);
        tampered[0] ^= 0xff;
        File.WriteAllBytes(path!, tampered);
        Assert.Null(reopened.TryGet("user", "banner", "fallback-v1", metadata));
        Assert.Empty(Files(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ProfileMedia_EnforcesMimeDeclaredAndActualSizeHashAndKindCaps()
    {
        using var directory = new TemporaryDirectory();
        var cache = new ExoProfileMediaCache(directory.Path);
        var png = Png(128);

        Assert.Null(await cache.TryStoreAsync(
            "user", "avatar", "gif", new MemoryStream(png),
            Media("avatar", "gif", "image/gif", png)));
        Assert.Null(await cache.TryStoreAsync(
            "user", "avatar", "mime-spoof", new MemoryStream(png),
            Media("avatar", "mime-spoof", "image/jpeg", png)));
        Assert.Null(await cache.TryStoreAsync(
            "user", "avatar", "length", new MemoryStream(png),
            Media("avatar", "length", "image/png", png) with { Size = png.Length - 1 }));
        Assert.Null(await cache.TryStoreAsync(
            "user", "avatar", "hash", new MemoryStream(png),
            Media("avatar", "hash", "image/png", png) with { Sha256 = new string('f', 64) }));
        Assert.Null(await cache.TryStoreAsync(
            "user", "avatar", "declared-cap", new MemoryStream(png),
            Media("avatar", "declared-cap", "image/png", png) with
            {
                Size = ExoProfileMediaCache.MaxAvatarBytes + 1,
            }));

        var actualOverCap = Png((int)ExoProfileMediaCache.MaxAvatarBytes);
        Assert.Null(await cache.TryStoreAsync(
            "user", "avatar", "actual-cap", new MemoryStream(actualOverCap),
            Media("avatar", "actual-cap", "image/png", actualOverCap) with
            {
                Size = ExoProfileMediaCache.MaxAvatarBytes,
                Sha256 = "",
            }));

        Assert.Empty(Files(directory.Path, "profile-*"));
        Assert.Empty(Files(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ProfileMedia_AcceptsGifWhenBytesMatchMime()
    {
        using var directory = new TemporaryDirectory();
        var cache = new ExoProfileMediaCache(directory.Path);
        var gif = new byte[]
        {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x00, 0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00,
            0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
            0x00, 0x02, 0x02, 0x4C, 0x01, 0x00, 0x3B,
        };
        var stored = await cache.TryStoreAsync(
            "user", "banner", "gif-v1", new MemoryStream(gif),
            Media("banner", "gif-v1", "image/gif", gif));
        Assert.NotNull(stored);
        Assert.EndsWith(".gif", stored!.FileName, StringComparison.Ordinal);
        Assert.Equal("image/gif", stored.ContentType);
    }

    [Fact]
    public async Task ProfileMedia_AcceptsPngJpegAndWebPOnlyWhenBytesMatchMime()
    {
        using var directory = new TemporaryDirectory();
        var cache = new ExoProfileMediaCache(directory.Path);
        var cases = new[]
        {
            (ContentType: "image/png", Extension: ".png", Bytes: Png(128)),
            (ContentType: "image/jpeg", Extension: ".jpg", Bytes: Jpeg(128)),
            (ContentType: "image/webp", Extension: ".webp", Bytes: WebP(128)),
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var version = $"format-{index}";
            var stored = await cache.TryStoreAsync(
                "user", "banner", version, new MemoryStream(item.Bytes),
                Media("banner", version, item.ContentType, item.Bytes));
            Assert.NotNull(stored);
            Assert.EndsWith(item.Extension, stored!.FileName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProfileMedia_ResolverRefusesTraversalAndMissingFiles()
    {
        using var directory = new TemporaryDirectory();
        var cache = new ExoProfileMediaCache(directory.Path);
        var bytes = Png(128);
        var stored = await cache.TryStoreAsync(
            "user", "avatar", "v1", new MemoryStream(bytes),
            Media("avatar", "v1", "image/png", bytes));
        Assert.NotNull(stored);
        Assert.Equal(
            Path.Combine(directory.Path, stored!.FileName),
            cache.ResolvePath("/" + stored.FileName));

        foreach (var hostile in new[]
                 {
                     "../settings.json",
                     "..%2fsettings.json",
                     @"C:\Windows\System32\config\SAM",
                     "profile-" + new string('a', 64) + ".png/../settings.json",
                     "profile-" + new string('a', 64) + ".exe",
                 })
            Assert.Null(cache.ResolvePath(hostile));

        Assert.Null(cache.ResolvePath("profile-" + new string('a', 64) + ".png"));
    }

    [Fact]
    public async Task ProfileMedia_PrunesTheOldestSafeFileAtTheBoundedCacheLimit()
    {
        using var directory = new TemporaryDirectory();
        var cache = new ExoProfileMediaCache(directory.Path);
        var bytes = Png(7 * 1024 * 1024 - 8);
        var stored = new List<ExoProfileMediaLocalRef>();
        var beforeTrigger = (int)(ExoProfileMediaCache.MaxCacheBytes / bytes.Length);
        for (var index = 0; index < beforeTrigger; index++)
        {
            var version = $"banner-{index}";
            var item = await cache.TryStoreAsync(
                "user", "banner", version, new MemoryStream(bytes),
                Media("banner", version, "image/png", bytes));
            stored.Add(Assert.IsType<ExoProfileMediaLocalRef>(item));
        }

        var oldestPath = cache.ResolvePath(stored[0].FileName);
        Assert.NotNull(oldestPath);
        File.SetLastWriteTimeUtc(oldestPath!, new DateTime(2000, 1, 1));
        var trigger = await cache.TryStoreAsync(
            "user", "banner", $"banner-{beforeTrigger}", new MemoryStream(bytes),
            Media("banner", $"banner-{beforeTrigger}", "image/png", bytes));

        Assert.NotNull(trigger);
        Assert.Null(cache.ResolvePath(stored[0].FileName));
        Assert.True(Files(directory.Path, "profile-*")
            .Sum(path => new FileInfo(path).Length) <= ExoProfileMediaCache.MaxCacheBytes);
        Assert.Empty(Files(directory.Path, "*.tmp"));
    }

    private sealed record CachedPayload(string DisplayName, string Token);
    private sealed record LargePayload(string Data);

    private static ExoProfileMediaMetadata Media(
        string kind,
        string version,
        string contentType,
        byte[] bytes) => new()
    {
        Kind = kind,
        Version = version,
        Url = "https://download.example.test/media?token=not-local",
        ContentType = contentType,
        Size = bytes.LongLength,
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant(),
    };

    private static byte[] Png(int padding)
    {
        var bytes = new byte[8 + padding];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Jpeg(int padding)
    {
        var bytes = new byte[3 + padding];
        new byte[] { 0xff, 0xd8, 0xff }.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] WebP(int padding)
    {
        var bytes = new byte[12 + padding];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("WEBP").CopyTo(bytes, 8);
        return bytes;
    }

    private static string[] Files(string root, string pattern) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly)
            : [];

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ExoOnlineCacheTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
