using System.Runtime.InteropServices.WindowsRuntime;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ExoLauncher.Tests;

/// <summary>
/// Library folders, their pictures and hidden games, as settings.json keeps them.
/// Ways it could fail (silently: the Library just shows something else after the next save):
/// - a game in a second folder is dropped from it (the one-folder rule coming back), or listed twice in one;
/// - a folder's sort is lost or an unknown word is kept, so the folder orders by nothing the UI knows;
/// - a folder takes another folder's picture, a path or any other text as its picture;
/// - a hidden game is listed twice, or a blank or control-character id is kept;
/// - a picture that is not PNG, JPEG or WebP, too large, the wrong shape or broken is stored;
/// - what is stored is the bytes as sent rather than a JPEG Exo wrote itself;
/// - a deleted or replaced folder's picture stays on disk, or a picture a folder still holds is deleted;
/// - a folder picture's request is cut off by the bridge's 1 MB limit.
/// </summary>
public sealed class LibraryFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-folder-art-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static LibraryFolder Folder(string id, params string[] games) => new() { Id = id, Name = id, Games = [.. games] };

    [Fact]
    public void AGameStaysInEveryFolderItIsIn_OnceInEach()
    {
        var clean = SettingsService.NormalizeLibraryFolders([Folder("a", "steam:1", "steam:2", "STEAM:1"), Folder("b", "steam:1")]);
        Assert.Equal(["steam:1", "steam:2"], clean[0].Games);
        Assert.Equal(["steam:1"], clean[1].Games);
    }

    [Theory]
    [InlineData("folder", "folder")]
    [InlineData("played", "played")]
    [InlineData("recent", "recent")]
    [InlineData("name", "name")]
    [InlineData("Played", "folder")]
    [InlineData("hidden", "folder")]
    [InlineData(null, "folder")]
    public void AFolderKeepsOnlyASortTheLibraryKnows(string? sort, string kept)
    {
        var folder = Folder("a", "steam:1");
        folder.Sort = sort!;
        Assert.Equal(kept, SettingsService.NormalizeLibraryFolders([folder])[0].Sort);
    }

    [Fact]
    public void AFolderKeepsOnlyItsOwnPicture()
    {
        const string own = "folder-art-a1-0123456789abcdef.jpg";
        static string? Kept(string id, string? art) => SettingsService.NormalizeLibraryFolders([new LibraryFolder { Id = id, Games = ["steam:1"], Art = art }])[0].Art;
        Assert.Equal(own, Kept("a1", own));
        Assert.Equal(own, Kept("a1", "https://covers.exo-launcher.local/" + own));
        Assert.Null(Kept("a2", own));
        Assert.Null(Kept("a", own));
        Assert.Null(Kept("a1", "folder-art-a1-0123456789ABCDEF.jpg"));
        Assert.Null(Kept("a1", "folder-art-a1-0123456789abcdef.png"));
        Assert.Null(Kept("a1", @"C:\Users\me\Pictures\cat.jpg"));
        Assert.Null(Kept("a1", "https://example.com/" + own));
        Assert.Null(Kept("a1", "custom-cover-" + new string('0', 64) + ".jpg"));
        Assert.Null(Kept("../a1", "folder-art-../a1-0123456789abcdef.jpg"));
        Assert.Null(Kept("a1", null));
    }

    [Fact]
    public void HiddenGamesAreValidIdsEachOnce()
    {
        var clean = SettingsService.NormalizeHiddenGames(["steam:1", "STEAM:1", "", "  ", "bad\nid", new string('x', 513), null, "epic:2"]);
        Assert.Equal(["steam:1", "epic:2"], clean);
    }

    [Fact]
    public void AFolderPictureFitsTheBridge()
    {
        var request = "{\"version\":1,\"id\":\"r1\",\"method\":\"library.folderArt\",\"params\":{\"image\":\"" + new string('A', 6_000_000) + "\"}}";
        Assert.True(ExoBridgeProtocol.TryParseRequest(request, out _));
        Assert.True(ExoBridgeProtocol.MaxFolderArtCharacters >= FolderArtStore.MaxBytes * 4 / 3);
    }

    [Fact]
    public async Task APictureIsStoredAsAJpegExoWrote()
    {
        var store = new FolderArtStore(_root);
        var png = await Encode(BitmapEncoder.PngEncoderId, 200, 300);
        var saved = await store.SaveAsync("a1", png);
        Assert.True(saved.Ok, saved.Message);
        Assert.Equal(saved.FileName, FolderArtStore.FileNameFor("a1", saved.FileName));
        var stored = await File.ReadAllBytesAsync(Path.Combine(_root, saved.FileName!));
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, stored[..3]);
        Assert.NotEqual(png, stored);
        Assert.Equal("https://covers.exo-launcher.local/" + saved.FileName, store.ResolveUrl(saved.FileName));
    }

    [Fact]
    public async Task AJpegIsTakenToo()
    {
        var saved = await new FolderArtStore(_root).SaveAsync("a1", await Encode(BitmapEncoder.JpegEncoderId, 400, 600));
        Assert.True(saved.Ok, saved.Message);
    }

    [Fact]
    public async Task OnlyAPictureOfTheRightKindSizeAndShapeIsStored()
    {
        var store = new FolderArtStore(_root);
        var png = await Encode(BitmapEncoder.PngEncoderId, 200, 300);
        Assert.Equal("That folder can't take a picture.", (await store.SaveAsync("../a1", png)).Message);
        Assert.Equal("That folder can't take a picture.", (await store.SaveAsync(null, png)).Message);
        Assert.Equal("That file isn't a picture.", (await store.SaveAsync("a1", [0x89, 0x50])).Message);
        Assert.Equal("Exo takes PNG, JPEG and WebP pictures.", (await store.SaveAsync("a1", "GIF89a this is not taken at all"u8.ToArray())).Message);
        var huge = new byte[FolderArtStore.MaxBytes + 1];
        png.CopyTo(huge, 0);
        Assert.Equal("Pictures have to be under 15 MB.", (await store.SaveAsync("a1", huge)).Message);
        Assert.Equal("Crop the picture to the folder's shape.", (await store.SaveAsync("a1", await Encode(BitmapEncoder.PngEncoderId, 300, 300))).Message);
        Assert.Equal("Choose a picture at least 64 pixels on each side.", (await store.SaveAsync("a1", await Encode(BitmapEncoder.PngEncoderId, 40, 60))).Message);
        Assert.Equal("Exo could not read that picture.", (await store.SaveAsync("a1", png[..40])).Message);
        Assert.False(Directory.Exists(_root) && Directory.EnumerateFiles(_root).Any());
    }

    [Fact]
    public void TheSignatureDecidesTheKind()
    {
        Assert.Equal<Guid?>(BitmapDecoder.WebpDecoderId, FolderArtStore.DecoderFor("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal<Guid?>(BitmapDecoder.JpegDecoderId, FolderArtStore.DecoderFor([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Null(FolderArtStore.DecoderFor("RIFF\0\0\0\0WAVEfmt "u8));
        Assert.Null(FolderArtStore.DecoderFor("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
    }

    [Fact]
    public async Task APictureNoFolderHoldsIsDeleted()
    {
        var store = new FolderArtStore(_root);
        var kept = (await store.SaveAsync("a1", await Encode(BitmapEncoder.PngEncoderId, 200, 300))).FileName!;
        var gone = (await store.SaveAsync("b2", await Encode(BitmapEncoder.PngEncoderId, 100, 150))).FileName!;
        File.WriteAllText(Path.Combine(_root, "custom-cover-x.jpg"), "not a folder picture");
        store.DeleteUnreferenced(["https://covers.exo-launcher.local/" + kept, null]);
        Assert.True(File.Exists(Path.Combine(_root, kept)));
        Assert.False(File.Exists(Path.Combine(_root, gone)));
        Assert.Null(store.ResolveUrl(gone));
        Assert.True(File.Exists(Path.Combine(_root, "custom-cover-x.jpg")));
    }

    /// <summary>A w×h picture in two colors, as the given encoder writes it.</summary>
    private static async Task<byte[]> Encode(Guid encoderId, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var left = i / 4 % width < width / 2;
            pixels[i] = left ? (byte)200 : (byte)30;
            pixels[i + 1] = 90;
            pixels[i + 2] = left ? (byte)30 : (byte)200;
            pixels[i + 3] = 255;
        }
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        await output.GetInputStreamAt(0).ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
