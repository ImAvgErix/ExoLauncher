using System.Security.Cryptography;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Files Exo swaps inside other programs' installs, proven in temp folders.
/// Ways it could fail: an upscaler backup is torn and later handed back as the "original"; Exo's write marker names
/// bytes Exo did not write, so Exo's vendor DLL is adopted as the original;
/// an unreadable download queue crashes startup or is overwritten.
/// </summary>
public sealed class DataSafetyTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "exo-ds-" + Guid.NewGuid().ToString("N"));

    public DataSafetyTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private static byte[] Bytes(int seed, int length = 4096)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    // ---- upscaler backups and marker ----

    [Fact]
    public void Upscaler_capture_saves_a_verified_whole_copy()
    {
        var dest = Path.Combine(_tmp, "nvngx_dlss.dll");
        File.WriteAllBytes(dest, Bytes(3));

        Assert.True(DlssSwapService.TryCaptureFirstShipped(dest));

        Assert.Equal(Bytes(3), File.ReadAllBytes(dest + DlssSwapService.SwapperBackupSuffix));
        Assert.Equal(Bytes(3), File.ReadAllBytes(dest + DlssSwapService.BackupSuffix));
        Assert.Empty(Directory.GetFiles(_tmp, "*.exo-tmp"));
    }

    [Fact]
    public void Upscaler_write_records_the_pinned_hash_and_refuses_without_one()
    {
        var dest = Path.Combine(_tmp, "nvngx_dlss.dll");
        var source = Path.Combine(_tmp, "vendor.dll");
        File.WriteAllBytes(dest, Bytes(4));
        File.WriteAllBytes(source, Bytes(5));
        Assert.True(DlssSwapService.TryCaptureFirstShipped(dest));

        Assert.ThrowsAny<IOException>(() => DlssSwapService.WriteWithBackup(dest, source, null));
        Assert.Equal(Bytes(4), File.ReadAllBytes(dest));

        DlssSwapService.WriteWithBackup(dest, source, Sha(Bytes(5)));
        Assert.Equal(Bytes(5), File.ReadAllBytes(dest));
        Assert.Equal(Sha(Bytes(5)), File.ReadAllText(dest + DlssSwapService.WrittenSuffix));
        Assert.Equal(Bytes(4), File.ReadAllBytes(dest + DlssSwapService.SwapperBackupSuffix));
        Assert.Empty(Directory.GetFiles(_tmp, "*.exo-tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Upscaler_reconcile_never_adopts_a_known_release_as_the_original(bool liveIsKnownRelease)
    {
        var dest = Path.Combine(_tmp, "nvngx_dlss.dll");
        File.WriteAllBytes(dest, Bytes(6));
        Assert.True(DlssSwapService.TryCaptureFirstShipped(dest));
        // Exo's write landed but its marker still names other bytes.
        File.WriteAllBytes(dest, Bytes(7));
        File.WriteAllText(dest + DlssSwapService.WrittenSuffix, Sha(Bytes(8)));

        Assert.True(DlssSwapService.ReconcileOriginal(dest, liveIsKnownRelease));

        var expected = liveIsKnownRelease ? Bytes(6) : Bytes(7); // unknown bytes are a game update: the new original
        Assert.Equal(expected, File.ReadAllBytes(dest + DlssSwapService.SwapperBackupSuffix));
        Assert.Equal(expected, File.ReadAllBytes(dest + DlssSwapService.BackupSuffix));
        Assert.False(File.Exists(dest + DlssSwapService.StaleSuffix));
    }

    // ---- download queue ----

    [Fact]
    public void Download_queue_with_an_unknown_format_is_kept_aside_and_saves_carry_on()
    {
        var path = Path.Combine(_tmp, "downloads.json");
        const string text = "{\"version\":2,\"intents\":[]}";
        File.WriteAllText(path, text);
        var store = new PendingDownloadStore(path);

        Assert.Empty(store.Load());
        var aside = Assert.Single(Directory.GetFiles(_tmp, "downloads.json.unreadable-*"));
        Assert.Equal(text, File.ReadAllText(aside));
        Assert.True(store.TrySave([], out var error));
        Assert.Null(error);
    }
}
