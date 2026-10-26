using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public static partial class CoverArtService
{
    internal readonly record struct CacheMaintenancePolicy(
        long HighWaterBytes,
        long LowWaterBytes,
        int HighWaterFiles,
        int LowWaterFiles,
        TimeSpan MaxUnreferencedAge,
        TimeSpan MinimumEvictionAge);

    internal readonly record struct CacheMaintenanceResult(
        int ExaminedFiles,
        int DeletedFiles,
        long DeletedBytes,
        int RemainingFiles,
        long RemainingBytes);

    internal static readonly CacheMaintenancePolicy DefaultCacheMaintenancePolicy = new(
        HighWaterBytes: 512L * 1024 * 1024,
        LowWaterBytes: 384L * 1024 * 1024,
        HighWaterFiles: 2_048,
        LowWaterFiles: 1_536,
        MaxUnreferencedAge: TimeSpan.FromDays(90),
        MinimumEvictionAge: TimeSpan.FromMinutes(15));


    private sealed record CacheFileSnapshot(
        string Path,
        string Name,
        long Length,
        DateTimeOffset LastWriteUtc);

    private static readonly CoalescedCacheMaintenance WriteMaintenance = new(() =>
    {
        var result = RunPostWriteCacheMaintenance();
        PersistTitleMap();
        return (result.RemainingFiles, result.RemainingBytes);
    }, TimeSpan.FromSeconds(2));

    private static void StartCacheMaintenanceOnce(IReadOnlyCollection<GameEntry> activeLibrary)
    {
        if (Interlocked.CompareExchange(ref _cacheMaintenanceStarted, 1, 0) != 0) return;
        lock (CacheMaintenanceGate) _activeCacheMaintenanceLibrary = activeLibrary.ToArray();
        _ = WriteMaintenance.Notify();
    }

    private static CacheMaintenanceResult RunPostWriteCacheMaintenance()
    {
        lock (CacheMaintenanceGate)
        {
            return RunCacheMaintenance(
                CacheRoot,
                _activeCacheMaintenanceLibrary,
                DateTimeOffset.UtcNow,
                DefaultCacheMaintenancePolicy);
        }
    }

    internal static void NotifyOwnedArtworkWrite() => _ = WriteMaintenance.Notify();

    /// <summary>
    /// Bounded cache maintenance. Active-library files and user profile images
    /// are never eviction candidates; exact legacy Steam duplicates are safe to
    /// remove only while an identical canonical poster remains.
    /// </summary>
    internal static CacheMaintenanceResult RunCacheMaintenance(
        string cacheRoot,
        IEnumerable<GameEntry> activeLibrary,
        DateTimeOffset nowUtc,
        CacheMaintenancePolicy policy)
    {
        if (!IsValidMaintenancePolicy(policy) || string.IsNullOrWhiteSpace(cacheRoot))
            return default;

        try
        {
            var root = Path.GetFullPath(cacheRoot);
            if (!Directory.Exists(root)) return default;

            DeleteStaleTemporaryFiles(root, nowUtc, policy.MinimumEvictionAge);
            var files = EnumerateCacheImages(root);
            if (files.Count == 0) return default;

            var (protectedNames, directReferences) = BuildProtectedCacheNames(
                root,
                activeLibrary ?? Array.Empty<GameEntry>());
            var alive = new HashSet<string>(
                files.Select(file => file.Path),
                StringComparer.OrdinalIgnoreCase);
            var byName = files.ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
            var remainingBytes = files.Sum(file => file.Length);
            var remainingFiles = files.Count;
            // The water marks bound what can be evicted: the active library's art and the never-evict files are not Exo's to drop, so
            // they are not counted against the budget. Counted, a library whose art alone passed the mark had every other
            // file (each thumbnail) deleted on every pass, and the write that made it again started the next pass.
            var keptFiles = files.Where(file => protectedNames.Contains(file.Name) || IsNeverEvictCacheName(file.Name)).ToList();
            var keptCount = keptFiles.Count;
            var keptBytes = keptFiles.Sum(file => file.Length);
            var crossedByteHighWater = remainingBytes - keptBytes > policy.HighWaterBytes;
            var crossedFileHighWater = remainingFiles - keptCount > policy.HighWaterFiles;
            var deletedBytes = 0L;
            var deletedFiles = 0;

            bool Delete(CacheFileSnapshot file)
            {
                if (!alive.Contains(file.Path) || !TryDeleteUnchanged(file)) return false;
                alive.Remove(file.Path);
                remainingBytes -= file.Length;
                remainingFiles--;
                deletedBytes += file.Length;
                deletedFiles++;
                return true;
            }

            // Proven redundant aliases are not an eviction: the exact bytes
            // remain in the canonical Steam poster file. Never remove the URL
            // an active row is currently holding.
            foreach (var file in files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (directReferences.Contains(file.Name) ||
                    IsNeverEvictCacheName(file.Name) ||
                    nowUtc - file.LastWriteUtc < policy.MinimumEvictionAge)
                    continue;
                if (!TryGetDuplicateReplacements(file.Name, out var replacementNames)) continue;

                foreach (var replacementName in replacementNames)
                {
                    if (!byName.TryGetValue(replacementName, out var replacement) ||
                        !alive.Contains(replacement.Path) ||
                        !FilesAreIdentical(file, replacement))
                        continue;
                    _ = Delete(file);
                    break;
                }
            }

            var evictionCandidates = files
                .Where(file => alive.Contains(file.Path))
                .Where(file => !protectedNames.Contains(file.Name))
                .Where(file => !IsNeverEvictCacheName(file.Name))
                .Where(file => nowUtc - file.LastWriteUtc >= policy.MinimumEvictionAge)
                .OrderBy(file => file.LastWriteUtc)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Age expiry is independent of pressure. Active art is protected,
            // so a title can remain untouched in the cache for years while it
            // is still part of the user's library.
            foreach (var file in evictionCandidates)
            {
                if (nowUtc - file.LastWriteUtc < policy.MaxUnreferencedAge) break;
                _ = Delete(file);
            }

            var reduceBytes = crossedByteHighWater;
            var reduceFiles = crossedFileHighWater;
            if (reduceBytes || reduceFiles)
            {
                foreach (var file in evictionCandidates)
                {
                    if ((!reduceBytes || remainingBytes - keptBytes <= policy.LowWaterBytes) &&
                        (!reduceFiles || remainingFiles - keptCount <= policy.LowWaterFiles))
                        break;
                    _ = Delete(file);
                }
            }

            return new CacheMaintenanceResult(
                files.Count,
                deletedFiles,
                deletedBytes,
                remainingFiles,
                remainingBytes);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Cover cache maintenance failed: " + ex.Message);
            return default;
        }
    }

    private static bool IsValidMaintenancePolicy(CacheMaintenancePolicy policy) =>
        policy.HighWaterBytes > 0 &&
        policy.LowWaterBytes >= 0 &&
        policy.LowWaterBytes < policy.HighWaterBytes &&
        policy.HighWaterFiles > 0 &&
        policy.LowWaterFiles >= 0 &&
        policy.LowWaterFiles < policy.HighWaterFiles &&
        policy.MaxUnreferencedAge >= TimeSpan.Zero &&
        policy.MinimumEvictionAge >= TimeSpan.Zero;

    /// <summary>
    /// Download and quality-check temporaries (<c>~…</c>) are deleted in a
    /// finally block; a crash or kill mid-download leaves them behind.
    /// </summary>
    private static void DeleteStaleTemporaryFiles(string root, DateTimeOffset nowUtc, TimeSpan minimumAge)
    {
        foreach (var path in Directory.EnumerateFiles(root, "~*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (nowUtc - new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) >= minimumAge)
                    File.Delete(path);
            }
            catch
            {
                // Still being written, or already gone.
            }
        }
    }

    private static List<CacheFileSnapshot> EnumerateCacheImages(string root)
    {
        var files = new List<CacheFileSnapshot>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var name = Path.GetFileName(path);
                if (!CacheImageExtensions.Contains(Path.GetExtension(name)) ||
                    IsTemporaryCacheName(name))
                    continue;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 0) continue;
                files.Add(new CacheFileSnapshot(
                    info.FullName,
                    name,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
            catch
            {
                // A cache file can disappear while the snapshot is built.
            }
        }
        return files;
    }

    private static (HashSet<string> Protected, HashSet<string> Direct) BuildProtectedCacheNames(
        string root,
        IEnumerable<GameEntry> activeLibrary)
    {
        var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in activeLibrary)
        {
            var direct = TryGetReferencedCacheFileName(root, game.CoverUrl);
            if (direct is not null)
            {
                protectedNames.Add(direct);
                directReferences.Add(direct);
            }

            AddIdentityCacheNames(
                protectedNames,
                game.Id,
                game.Store,
                game.LaunchTarget,
                ExtractSteamAppIdFromUrl(game.CoverUrl));
            foreach (var variant in game.Variants)
            {
                AddIdentityCacheNames(
                    protectedNames,
                    variant.Id,
                    variant.Store,
                    variant.LaunchTarget,
                    steamAppIdFromCover: null);
            }
        }
        return (protectedNames, directReferences);
    }

    private static void AddIdentityCacheNames(
        HashSet<string> names,
        string gameId,
        StoreKind store,
        string? launchTarget,
        string? steamAppIdFromCover)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        foreach (var slug in CacheIdentityCandidates(gameId))
        {
            foreach (var extension in CacheImageExtensions)
                names.Add(slug + extension);
        }
        names.Add(WideArtFileName(gameId));
        names.Add(GameIconArt.CacheFileName(gameId));

        string? steamAppId = steamAppIdFromCover;
        if (store == StoreKind.Steam)
        {
            if (!string.IsNullOrWhiteSpace(launchTarget) && launchTarget.All(char.IsDigit))
                steamAppId = launchTarget;
            else if (gameId.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
            {
                var fromId = gameId["steam:".Length..];
                if (fromId.All(char.IsDigit)) steamAppId = fromId;
            }
        }
        if (!string.IsNullOrWhiteSpace(steamAppId) && steamAppId.All(char.IsDigit))
        {
            names.Add(steamAppId + ".jpg");
            names.Add(steamAppId + "_2x.jpg");
        }

        if (store == StoreKind.Riot)
        {
            var productId = !string.IsNullOrWhiteSpace(launchTarget)
                ? launchTarget
                : gameId.StartsWith("riot:", StringComparison.OrdinalIgnoreCase)
                    ? gameId["riot:".Length..]
                    : null;
            if (!string.IsNullOrWhiteSpace(productId))
            {
                foreach (var safe in CacheIdentityCandidates(productId))
                {
                    var riot = "riot_" + safe;
                    foreach (var extension in CacheImageExtensions)
                        names.Add(riot + extension);
                    names.Add(riot + "_card.png");
                }
            }
        }

        if (store == StoreKind.Gog && gameId.StartsWith("gog:", StringComparison.OrdinalIgnoreCase))
        {
            var productId = gameId["gog:".Length..];
            if (productId.All(char.IsDigit)) names.Add("gog_" + productId + ".jpg");
        }
    }

    private static string? TryGetReferencedCacheFileName(string root, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            if (value.StartsWith(VirtualHostOrigin + "/", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(value, UriKind.Absolute);
                var name = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
                return IsSafeCacheFileName(name) ? name : null;
            }

            string? path = null;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uriValue) && uriValue.IsFile)
                path = uriValue.LocalPath;
            else if (Path.IsPathFullyQualified(value))
                path = value;
            if (path is null) return null;

            var fullPath = Path.GetFullPath(path);
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            var fileName = Path.GetFileName(fullPath);
            return IsSafeCacheFileName(fileName) ? fileName : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeCacheFileName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        !name.Contains("..", StringComparison.Ordinal) &&
        !name.Contains('/') &&
        !name.Contains('\\') &&
        CacheImageExtensions.Contains(Path.GetExtension(name));

    private static bool IsNeverEvictCacheName(string name) =>
        name.Equals("title-steam-map.json", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("profile-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("custom-cover-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(FolderArtStore.Prefix, StringComparison.OrdinalIgnoreCase) ||
        IsTemporaryCacheName(name);

    private static bool IsTemporaryCacheName(string name) =>
        name.StartsWith('~') ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".download", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetDuplicateReplacements(string name, out IReadOnlyList<string> replacements)
    {
        replacements = Array.Empty<string>();
        if (name.StartsWith("steam_", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            var appId = name["steam_".Length..^".jpg".Length];
            if (appId.Length > 0 && appId.All(char.IsDigit))
            {
                replacements = [appId + ".jpg", appId + "_2x.jpg"];
                return true;
            }
        }

        const string highResSuffix = "_2x.jpg";
        if (name.EndsWith(highResSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var appId = name[..^highResSuffix.Length];
            if (appId.Length > 0 && appId.All(char.IsDigit))
            {
                replacements = [appId + ".jpg"];
                return true;
            }
        }
        return false;
    }

    private static bool FilesAreIdentical(CacheFileSnapshot left, CacheFileSnapshot right)
    {
        if (left.Length != right.Length || left.Length < 0) return false;
        try
        {
            using var leftStream = new FileStream(
                left.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.SequentialScan);
            using var rightStream = new FileStream(
                right.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.SequentialScan);
            var leftBuffer = new byte[64 * 1024];
            var rightBuffer = new byte[64 * 1024];
            while (true)
            {
                var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
                var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
                if (leftRead != rightRead) return false;
                if (leftRead == 0) return true;
                if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeleteUnchanged(CacheFileSnapshot snapshot)
    {
        try
        {
            var current = new FileInfo(snapshot.Path);
            if (!current.Exists || current.Length != snapshot.Length ||
                current.LastWriteTimeUtc != snapshot.LastWriteUtc.UtcDateTime)
                return false;
            File.Delete(snapshot.Path);
            ImageSizeCache.TryRemove(snapshot.Path, out _);
            ImageHeaderCache.TryRemove(snapshot.Path, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }

}
