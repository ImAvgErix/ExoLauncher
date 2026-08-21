using System.Net;
using System.Net.Http.Headers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ArtworkPipelineHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "exo-art-pipeline-" + Guid.NewGuid().ToString("N"));

    public ArtworkPipelineHardeningTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("https://cdn.cloudflare.steamstatic.com.evil.example/steam/apps/730/library_600x900.jpg")]
    [InlineData("https://cdn.cloudflare.steamstatic.com:444/steam/apps/730/library_600x900.jpg")]
    [InlineData("https://cdn.cloudflare.steamstatic.com@evil.example/steam/apps/730/library_600x900.jpg")]
    [InlineData("http://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900.jpg")]
    [InlineData("https://evil.example/path/cdn.cloudflare.steamstatic.com/library_600x900.jpg")]
    public void ArtworkOriginPolicy_RejectsLookalikeHostsPortsCredentialsAndPaths(string url)
    {
        Assert.False(CoverArtService.IsApprovedArtworkDownloadUrl(url));
        Assert.False(CoverArtService.IsOfficialSteamPortraitCdn(url));
    }

    [Fact]
    public void CollisionSafeCacheId_SeparatesPunctuationAndExactUnicodeIdentities()
    {
        Assert.NotEqual(
            CoverArtService.CollisionSafeCacheId("epic:foo-bar"),
            CoverArtService.CollisionSafeCacheId("epic:foo_bar"));
        Assert.NotEqual(
            CoverArtService.CollisionSafeCacheId("local:\u00e9"),
            CoverArtService.CollisionSafeCacheId("local:e\u0301"));
        Assert.Equal(
            CoverArtService.CollisionSafeCacheId("riot:valorant"),
            CoverArtService.CollisionSafeCacheId("riot:valorant"));
    }

    [Fact]
    public void ResolvePreferredUrl_StillReadsTheLegacySanitizedPortraitCache()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var id = "epic:legacy-cache-" + suffix;
        var legacy = new string(id.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
        var path = Path.Combine(CoverArtService.CacheRoot, legacy + ".jpg");
        Directory.CreateDirectory(CoverArtService.CacheRoot);
        var encoded = ValidJpeg();
        var padded = new byte[CoverArtService.MinCoverBytes + 64];
        encoded.CopyTo(padded, 0);
        try
        {
            File.WriteAllBytes(path, padded);
            var game = new GameEntry
            {
                Id = id,
                Title = "Legacy cache compatibility " + suffix,
                Store = StoreKind.Epic,
                Installed = true,
            };

            var resolved = CoverArtService.ResolvePreferredUrl(game);

            Assert.EndsWith("/" + legacy + ".jpg", resolved, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void FullDecode_ProducesPixelsForACompleteImage()
    {
        var path = Path.Combine(_root, "valid.jpg");
        File.WriteAllBytes(path, ValidJpeg());

        var decoded = CoverArtService.TryFullyDecodeImage(path, 4_096, out var dimensions);

        Assert.True(decoded);
        Assert.Equal((64, 96), dimensions);
    }

    [Fact]
    public void FullDecode_RejectsAHeaderOnlyMalformedJpeg()
    {
        var path = Path.Combine(_root, "malformed.jpg");
        File.WriteAllBytes(path, MalformedJpegFixture());

        Assert.False(CoverArtService.TryFullyDecodeImage(path, 4_096, out _));
    }

    [Fact]
    public void SearchWarmIntent_IsPortraitOnlyAndUsesSmallBoundedConcurrency()
    {
        Assert.False(CoverArtService.WarmIntentIncludesWideArt(
            CoverArtService.ArtworkWarmIntent.SearchPortrait));
        Assert.InRange(CoverArtService.SearchWarmConcurrency, 2, 4);
        var coverService = File.ReadAllText(Path.Combine(FindRepoRoot(), @"ExoLauncher\Services\CoverArtService.cs"));
        Assert.Contains("SearchArtworkGate", coverService, StringComparison.Ordinal);

        var root = FindRepoRoot();
        foreach (var relative in new[]
                 {
                     @"ExoLauncher\Services\WebHostBridge.cs",
                     @"ExoLauncher\Services\ShellController.cs",
                 })
        {
            var source = File.ReadAllText(Path.Combine(root, relative));
            Assert.Contains("WarmSearchPortraitCacheAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "WarmCacheAsync(needsArt, requested: true",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DownloadValidatedImageAsync_RejectsTruncationWithoutReplacingLastGoodFile()
    {
        var destination = Path.Combine(_root, "cover.jpg");
        var lastGood = ValidJpeg();
        await File.WriteAllBytesAsync(destination, lastGood);
        var truncated = lastGood[..^24];
        using var http = Client(_ => ImageResponse(truncated));

        var saved = await CoverArtService.DownloadValidatedImageAsync(
            http,
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900.jpg",
            destination,
            minimumBytes: 200,
            maximumBytes: 8 * 1024 * 1024,
            CancellationToken.None);

        Assert.False(saved);
        Assert.Equal(lastGood, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp.*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task DownloadValidatedImageAsync_StreamsAndAtomicallyPromotesACompleteImage()
    {
        var destination = Path.Combine(_root, "complete.jpg");
        var bytes = ValidJpeg();
        using var http = Client(_ => ImageResponse(bytes));

        var saved = await CoverArtService.DownloadValidatedImageAsync(
            http,
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900.jpg",
            destination,
            minimumBytes: 200,
            maximumBytes: 8 * 1024 * 1024,
            CancellationToken.None);

        Assert.True(saved);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp.*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task DownloadValidatedImageAsync_RejectsHugePixelClaimsBeforePromotion()
    {
        var destination = Path.Combine(_root, "huge.png");
        using var http = Client(_ => ImageResponse(HugePngHeader()));

        var saved = await CoverArtService.DownloadValidatedImageAsync(
            http,
            "https://images.gog-statics.com/huge.png",
            destination,
            minimumBytes: 24,
            maximumBytes: 8 * 1024 * 1024,
            CancellationToken.None);

        Assert.False(saved);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadValidatedImageAsync_RevalidatesTheFinalRedirectOrigin()
    {
        var destination = Path.Combine(_root, "redirect.jpg");
        using var http = Client(_ =>
        {
            var response = ImageResponse(ValidJpeg());
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://evil.example/cdn.cloudflare.steamstatic.com/library_600x900.jpg");
            return response;
        });

        var saved = await CoverArtService.DownloadValidatedImageAsync(
            http,
            "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900.jpg",
            destination,
            minimumBytes: 200,
            maximumBytes: 8 * 1024 * 1024,
            CancellationToken.None);

        Assert.False(saved);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadValidatedImageAsync_CancelsTheBodyReadAndLeavesNoPartialFile()
    {
        var destination = Path.Combine(_root, "cancelled.jpg");
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingReadStream()),
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CoverArtService.DownloadValidatedImageAsync(
                http,
                "https://cdn.cloudflare.steamstatic.com/steam/apps/730/library_600x900.jpg",
                destination,
                minimumBytes: 200,
                maximumBytes: 8 * 1024 * 1024,
                cancellation.Token));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp.*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void PostWriteMaintenance_EnforcesPressureCrossedDuringTheSession()
    {
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        Write("active.jpg", 80, now.AddDays(-30));
        Write("oldest.jpg", 80, now.AddDays(-20));
        Write("older.jpg", 80, now.AddDays(-10));
        var active = new GameEntry
        {
            Id = "active",
            Title = "Active",
            Store = StoreKind.Local,
            CoverUrl = CoverArtService.VirtualHostOrigin + "/active.jpg",
        };
        var policy = new CoverArtService.CacheMaintenancePolicy(
            HighWaterBytes: 200,
            LowWaterBytes: 100,
            HighWaterFiles: 100,
            LowWaterFiles: 90,
            MaxUnreferencedAge: TimeSpan.FromDays(365),
            MinimumEvictionAge: TimeSpan.Zero);

        var result = CoverArtService.RunPostWriteCacheMaintenance(
            _root,
            [active],
            now,
            policy);

        Assert.True(File.Exists(Path.Combine(_root, "active.jpg")));
        Assert.True(result.RemainingBytes <= policy.LowWaterBytes);
        Assert.Equal(2, result.DeletedFiles);
    }

    private void Write(string name, int length, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new StubHandler(response)) { Timeout = TimeSpan.FromSeconds(5) };

    private static HttpResponseMessage ImageResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return response;
    }

    private static byte[] ValidJpeg() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCABgAEADASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAT/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwCCAL0IAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD//2Q==");

    // A deliberately malformed legacy fixture kept to ensure truncated/header-only
    // payloads never become accepted just because they contain SOI/SOF bytes.
    private static byte[] MalformedJpegFixture() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCABaADwDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD4XooorrICiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooA//2Q==");

    private static byte[] HugePngHeader()
    {
        var bytes = new byte[64];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        bytes[11] = 13;
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';
        WriteBigEndian(bytes, 16, 100_000);
        WriteBigEndian(bytes, 20, 100_000);
        return bytes;
    }

    private static void WriteBigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = response(request);
            message.RequestMessage ??= request;
            return Task.FromResult(message);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
