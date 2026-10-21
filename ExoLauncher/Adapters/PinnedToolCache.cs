using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ExoLauncher.Services;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Adapters;

/// <summary>
/// Memoizes the pinned-asset verification for the bundled CLI backends.
/// Verifying legendary.exe costs a SHA-256 of 17 MB and gogdl.exe 12 MB, and
/// every library projection asks each adapter whether its backend is present
/// once per game. The stamp (length, last-write time, change time and
/// volume-plus-file id, read with GetFileInformationByHandleEx) is re-read on
/// every call; an execution lease reads it from the handle it holds open. The
/// tool is hashed again when any of the four differs, so a file swapped in by
/// rename or with its write time set back is never trusted on an old result.
/// </summary>
internal static class PinnedToolCache
{
    private readonly record struct Stamp(long Length, long LastWriteTicks, long ChangeTicks,
        ulong VolumeSerial, ulong FileIdLow, ulong FileIdHigh);

    private static readonly ConcurrentDictionary<string, (Stamp Stamp, GitHubReleaseAsset Asset, bool Verified)> Results =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="held">The execution lease's handle; the stamp is read from it.</param>
    public static bool IsPinnedAsset(
        GitHubReleaseAsset asset,
        string path,
        Func<string, bool> validateExecutable,
        SafeFileHandle? held = null)
    {
        if (!TryReadStamp(path, held, out var stamp))
        {
            Results.TryRemove(path ?? string.Empty, out _);
            return false;
        }

        if (Results.TryGetValue(path, out var cached) && cached.Stamp == stamp && cached.Asset == asset)
            return cached.Verified;

        var verified = VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(asset, path, validateExecutable);
        Results[path] = (stamp, asset, verified);
        return verified;
    }

    private static bool TryReadStamp(string? path, SafeFileHandle? held, out Stamp stamp)
    {
        stamp = default;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (held is not null) return TryReadStamp(held, out stamp);
            if (!File.Exists(path)) return false;
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return TryReadStamp(handle, out stamp);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadStamp(SafeFileHandle handle, out Stamp stamp)
    {
        stamp = default;
        if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out FileBasicInfo basic,
                (uint)Marshal.SizeOf<FileBasicInfo>())) return false;
        // FAT volumes have no 128-bit id; the other fields still count there.
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out FileIdInfo id,
                (uint)Marshal.SizeOf<FileIdInfo>())) id = default;
        stamp = new Stamp(RandomAccess.GetLength(handle), basic.LastWriteTime, basic.ChangeTime,
            id.VolumeSerialNumber, id.FileIdLow, id.FileIdHigh);
        return true;
    }

    private const int FileBasicInfoClass = 0;
    private const int FileIdInfoClass = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber, FileIdLow, FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileBasicInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);
}
