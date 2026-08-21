using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Avatar and banner pictures come off the user's own disk, so the store has to
/// be the strict part: the bytes decide the format, the dimensions have to be
/// sane, the copy lands inside Exo, and a stored name can never be a path.
/// </summary>
public sealed class ProfileImageStoreTests
{
    [Fact]
    public void Save_CopiesARealPngIntoTheCoverCache()
    {
        InIsolatedDataDirectory(() =>
        {
            var source = WriteTemp("shot.png", ValidPng());

            var stored = ProfileImageStore.Save(source, "avatar");

            Assert.Null(stored.Message);
            Assert.NotNull(stored.FileName);
            Assert.StartsWith("profile-avatar-", stored.FileName!, StringComparison.Ordinal);
            Assert.EndsWith(".png", stored.FileName!, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(CoverArtService.CacheRoot, stored.FileName!)));

            // The picture is served from Exo's own cache, never from where it was picked.
            var url = ProfileImageStore.ResolveUrl(stored.FileName);
            Assert.Equal($"https://covers.exo-launcher.local/{stored.FileName}", url);
        });
    }

    [Fact]
    public void Save_TakesJpegAndNamesItByItsBytes()
    {
        InIsolatedDataDirectory(() =>
        {
            // The extension is a claim, not evidence: this JPEG is named .png.
            var source = WriteTemp("liar.png", ValidJpeg());

            var stored = ProfileImageStore.Save(source, "banner");

            Assert.Null(stored.Message);
            Assert.EndsWith(".jpg", stored.FileName!, StringComparison.Ordinal);
            Assert.StartsWith("profile-banner-", stored.FileName!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Save_TakesAnAnimatedGifByItsHeader()
    {
        InIsolatedDataDirectory(() =>
        {
            var source = WriteTemp("motion.gif", ValidGif());

            var stored = ProfileImageStore.Save(source, "gallery0");

            Assert.Null(stored.Message);
            Assert.EndsWith(".gif", stored.FileName!, StringComparison.Ordinal);
            Assert.StartsWith("profile-gallery0-", stored.FileName!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Save_RefusesAnythingThatIsNotPngOrJpeg()
    {
        InIsolatedDataDirectory(() =>
        {
            var source = WriteTemp("notes.png", new byte[400]);

            var stored = ProfileImageStore.Save(source, "avatar");

            Assert.Null(stored.FileName);
            Assert.Equal("Exo takes PNG, JPEG, WebP, and GIF.", stored.Message);
            Assert.Empty(Directory.GetFiles(CoverArtService.CacheRoot, "profile-*"));
        });
    }

    [Fact]
    public void Save_RefusesPicturesOverTheSizeCap()
    {
        InIsolatedDataDirectory(() =>
        {
            var source = WriteTemp(
                "huge.png",
                Png(1024, 1024, padding: (int)ProfileImageStore.MaxBytes + 1024));

            var stored = ProfileImageStore.Save(source, "avatar");

            Assert.Null(stored.FileName);
            Assert.Contains("under 8 MB", stored.Message);
        });
    }

    [Fact]
    public void Save_RefusesDimensionsOutsideTheSaneRange()
    {
        InIsolatedDataDirectory(() =>
        {
            var tiny = ProfileImageStore.Save(WriteTemp("tiny.png", Png(16, 16)), "avatar");
            Assert.Null(tiny.FileName);
            Assert.Contains("under 256×256", tiny.Message);

            var vast = ProfileImageStore.Save(WriteTemp("vast.png", Png(20_000, 900)), "banner");
            Assert.Null(vast.FileName);
            Assert.Contains("over 4096×4096", vast.Message);
        });
    }

    [Fact]
    public void Save_RefusesASlotItDoesNotOwn()
    {
        InIsolatedDataDirectory(() =>
        {
            var stored = ProfileImageStore.Save(WriteTemp("ok.png", Png(256, 256)), "wallpaper");

            Assert.Null(stored.FileName);
            Assert.Equal("Unknown image slot.", stored.Message);
        });
    }

    [Fact]
    public void Save_RefusesAPathTheUiCouldHaveTyped()
    {
        InIsolatedDataDirectory(() =>
        {
            Assert.Equal("Pick an image file.", ProfileImageStore.Save("shot.png", "avatar").Message);
            Assert.Equal("Pick an image file.", ProfileImageStore.Save(string.Empty, "avatar").Message);
            Assert.Equal("Pick an image file.", ProfileImageStore.Save(null, "avatar").Message);
        });
    }

    [Fact]
    public void FileName_OnlyEverAcceptsANameExoWroteItself()
    {
        foreach (var hostile in new[]
                 {
                     "../../settings.json",
                     "profile-avatar/../../settings.json",
                     @"C:\Windows\System32\config\SAM",
                     "settings.json",
                     "profile-avatar-../x.png",
                 })
        {
            Assert.Null(ProfileImageStore.FileName(hostile));
        }

        Assert.Equal("profile-avatar-abc123.png", ProfileImageStore.FileName("profile-avatar-abc123.png"));
    }

    [Fact]
    public void ResolveUrl_IsNullWhenThePictureIsGone()
    {
        InIsolatedDataDirectory(() =>
        {
            var stored = ProfileImageStore.Save(WriteTemp("shot.png", ValidPng()), "avatar");
            Assert.NotNull(ProfileImageStore.ResolveUrl(stored.FileName));

            ProfileImageStore.Delete(stored.FileName);

            // A picture deleted off disk resolves to nothing, so the page falls
            // back to cover art instead of a broken image.
            Assert.Null(ProfileImageStore.ResolveUrl(stored.FileName));
            Assert.Null(ProfileImageStore.ResolveUrl("settings.json"));
        });
    }

    [Fact]
    public void Delete_LeavesFilesExoDidNotWrite()
    {
        InIsolatedDataDirectory(() =>
        {
            Directory.CreateDirectory(CoverArtService.CacheRoot);
            var cover = Path.Combine(CoverArtService.CacheRoot, "620.jpg");
            File.WriteAllBytes(cover, ValidJpeg());

            ProfileImageStore.Delete("620.jpg");
            ProfileImageStore.Delete(@"..\settings.json");

            Assert.True(File.Exists(cover));
        });
    }

    private static string WriteTemp(string name, byte[] bytes)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ExoProfileImageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Enough of a PNG for the header readers. The pixels are not the point.</summary>
    private static byte[] Png(int width, int height, int padding = 256)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        bytes.AddRange([0, 0, 0, 13]);
        bytes.AddRange("IHDR"u8.ToArray());
        bytes.AddRange(BigEndian(width));
        bytes.AddRange(BigEndian(height));
        bytes.AddRange([8, 6, 0, 0, 0]);
        bytes.AddRange([0, 0, 0, 0]);
        bytes.AddRange(new byte[padding]);
        return bytes.ToArray();
    }

    [Fact]
    public void Save_RefusesATruncatedImageAfterHeaderValidation()
    {
        InIsolatedDataDirectory(() =>
        {
            var complete = ValidPng();
            var source = WriteTemp("truncated.png", complete[..^12]);

            var stored = ProfileImageStore.Save(source, "avatar");

            Assert.Null(stored.FileName);
            Assert.Contains("could not read", stored.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(CoverArtService.CacheRoot, "profile-*"));
        });
    }

    private static byte[] ValidPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAQAAAAEACAIAAADTED8xAAACAElEQVR42u3TQREAMAjAsDENiEAE/nXxRgOJhN41svrBVV8CDAAGAAOAAcAAYAAwABgADAAGAAOAAcAAYAAwABgADAAGAAOAAcAAYAAwABgADAAGAAOAAcAAYAAwABgADAAGAAOAAcAAYAAwABgADAAGAAOAAcAAYAAwABgADAAGAANgADAAGAAMAAYAA4ABwABgADAAGAAMAAYAA4ABwABgADAAGAAMAAYAA4ABwABgADAAGAAMAAYAA4ABwABgADAAGAAMAAYAA4ABwABgADAAGAAMAAYAA4ABwABgADAAGAAMgAHAAGAAMAAYAAwABgADgAHAAGAAMAAYAAwABgADgAHAAGAAMAAYAAwABgADgAHAAGAAMAAYAAwABgADgAHAAGAAMAAYAAwABgADgAHAAGAAMAAYAAwABgADgAHAAGAADAAGAAOAAcAAYAAwABgADAAGAAOAAcAAYAAwABgADAAGAAOAAcAAYAAwABgADAAGAAOAAcAAYAAwABgADAAGAAOAAcAAYAAwABgADAAGAAOAAcAAYAAwABgADAAGAAOAATAAGAAMAAYAA4ABwABgADAAGAAMAAYAA4ABwABgADAAGAAMAAYAA4ABwABgADAAGAAMAAYAA4ABwABgADAAGAAMAAYAA4ABwABgADAAGAAMAAYAA4ABwABgANgGujwCeEqQR24AAAAASUVORK5CYII=");

    private static byte[] ValidJpeg() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAHCAyADASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAT/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwCRgLEgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD//2Q==");

    private static byte[] ValidGif() => Convert.FromBase64String(
        "R0lGODdhQABAAIAAAAAAAAAAACwAAAAAQABAAEAIaQABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzBjypxJs6bNmzhz6tzJs6fPn0CDCh1KtKjRo0iTKl3KtKnTp1CjSp1KtarVq1izagUQEAA7");

    private static byte[] BigEndian(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static void InIsolatedDataDirectory(Action test)
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var root = Path.Combine(
            Path.GetTempPath(),
            "ExoProfileImageTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, root);
            Directory.CreateDirectory(CoverArtService.CacheRoot);
            test();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
            try { Directory.Delete(root, recursive: true); }
            catch { /* temporary test cleanup is best effort */ }
        }
    }
}
