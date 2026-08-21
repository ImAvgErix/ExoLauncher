using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class GameCoverImageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-cover-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportAsync_AcceptsOnlyBoundedPortraitPngOrJpeg_AndUsesAContentAddressedAtomicName()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "chosen.bin");
        await File.WriteAllBytesAsync(source, ValidPortraitPng());
        var cache = Path.Combine(_root, "cache");
        var store = new GameCoverImageStore(cache);

        var first = await store.ImportAsync(source);
        var second = await store.ImportAsync(source);

        Assert.True(first.Ok, first.Message);
        Assert.Equal(first.FileName, second.FileName);
        Assert.Matches("^custom-cover-[0-9a-f]{64}\\.png$", first.FileName!);
        Assert.True(File.Exists(Path.Combine(cache, first.FileName!)));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(cache, "*", SearchOption.TopDirectoryOnly),
            path => Path.GetFileName(path).Contains(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(900, 600, "portrait")]
    [InlineData(63, 96, "64")]
    [InlineData(2731, 4097, "4096")]
    public async Task ImportAsync_RejectsUnsafeDimensionsWithoutCreatingAnOwnedCopy(
        int width,
        int height,
        string messagePart)
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, $"{width}x{height}.png");
        await File.WriteAllBytesAsync(source, PngHeader(width, height));
        var cache = Path.Combine(_root, "cache");
        var store = new GameCoverImageStore(cache);

        var result = await store.ImportAsync(source);

        Assert.False(result.Ok);
        Assert.Contains(messagePart, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(cache) && Directory.EnumerateFiles(cache).Any());
    }

    [Fact]
    public async Task ImportAsync_RejectsSpoofedAndOversizedFilesBeforeWritingTheCache()
    {
        Directory.CreateDirectory(_root);
        var spoofed = Path.Combine(_root, "spoofed.png");
        await File.WriteAllTextAsync(spoofed, "<html>not an image</html>");
        var oversized = Path.Combine(_root, "oversized.jpg");
        await using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(GameCoverImageStore.MaxBytes + 1);
        }
        var cache = Path.Combine(_root, "cache");
        var store = new GameCoverImageStore(cache);

        var spoofedResult = await store.ImportAsync(spoofed);
        var oversizedResult = await store.ImportAsync(oversized);

        Assert.False(spoofedResult.Ok);
        Assert.Contains("PNG and JPEG", spoofedResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(oversizedResult.Ok);
        Assert.Contains("8 MB", oversizedResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(cache) && Directory.EnumerateFiles(cache).Any());
    }

    [Fact]
    public async Task ImportAsync_RejectsATruncatedImageAfterHeaderValidation()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "truncated.png");
        var complete = ValidPortraitPng();
        await File.WriteAllBytesAsync(source, complete[..^12]);
        var cache = Path.Combine(_root, "cache");

        var result = await new GameCoverImageStore(cache).ImportAsync(source);

        Assert.False(result.Ok);
        Assert.Contains("could not read", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(cache) && Directory.EnumerateFiles(cache).Any());
    }

    [Fact]
    public async Task ImportAsync_ValidatesBeforeReusingAName_AndAtomicallyRepairsTamperedOwnedContent()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "chosen.png");
        var bytes = ValidPortraitPng();
        await File.WriteAllBytesAsync(source, bytes);
        var cache = Path.Combine(_root, "cache");
        var store = new GameCoverImageStore(cache);
        var first = await store.ImportAsync(source);
        Assert.True(first.Ok, first.Message);
        await File.WriteAllTextAsync(Path.Combine(cache, first.FileName!), "tampered");

        var repaired = await store.ImportAsync(source);

        Assert.True(repaired.Ok, repaired.Message);
        Assert.True(repaired.Created);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(cache, first.FileName!)));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(cache, "*", SearchOption.TopDirectoryOnly),
            path => Path.GetFileName(path).Contains(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("custom-cover-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png", true)]
    [InlineData("custom-cover-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.jpg", false)]
    [InlineData("../custom-cover-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png", false)]
    [InlineData("profile-avatar-aaaaaaaaaaaaaaaa.png", false)]
    [InlineData("title-steam-map.json", false)]
    public void FileName_RecognizesOnlyOwnedContentAddressedCoverNames(string value, bool expected)
    {
        Assert.Equal(expected, GameCoverImageStore.FileName(value) is not null);
    }

    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[33];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        bytes[11] = 13; // IHDR payload length, big endian.
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';
        WriteBigEndian(bytes, 16, width);
        WriteBigEndian(bytes, 20, height);
        bytes[24] = 8;
        bytes[25] = 2;
        return bytes;
    }

    private static byte[] ValidPortraitPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABgCAIAAAAip+O/AAAAaklEQVR42u3PMQ0AMAgAMJgGRCAC/7pmgoekddCsnrjsxXECAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIC+z5b2wE432X3WwAAAABJRU5ErkJggg==");

    private static void WriteBigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
