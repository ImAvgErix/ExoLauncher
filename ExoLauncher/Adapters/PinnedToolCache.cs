using System.Collections.Concurrent;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// Memoizes the pinned-asset verification for the bundled CLI backends.
/// Verifying legendary.exe costs a SHA-256 of 17 MB and gogdl.exe 12 MB, and
/// every library projection asks each adapter whether its backend is present
/// once per game. The file identity (length + last write) is re-read on every
/// call, so a replaced or tampered binary is never trusted from the cache.
/// </summary>
internal static class PinnedToolCache
{
    private readonly record struct Stamp(long Length, long LastWriteUtcTicks);

    private static readonly ConcurrentDictionary<string, (Stamp Stamp, bool Verified)> Results =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsPinnedAsset(
        GitHubReleaseAsset asset,
        string path,
        Func<string, bool> validateExecutable)
    {
        if (!TryReadStamp(path, out var stamp))
        {
            Results.TryRemove(path ?? string.Empty, out _);
            return false;
        }

        if (Results.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Verified;

        var verified = VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(asset, path, validateExecutable);
        Results[path] = (stamp, verified);
        return verified;
    }

    private static bool TryReadStamp(string? path, out Stamp stamp)
    {
        stamp = default;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            stamp = new Stamp(info.Length, info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
