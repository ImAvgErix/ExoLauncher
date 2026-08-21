using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Small DPAPI-protected cache for successful Exo online JSON responses.
/// File names are hashes of the immutable account and operation keys; neither
/// identifiers nor response data are exposed through the cache directory.
/// </summary>
internal sealed class ExoOnlineCache
{
    public const string DirectoryName = "online-cache";
    public const string EntryExtension = ".bin";
    public const int MaxPlaintextEntryBytes = 512 * 1024;
    public const int MaxEntries = 64;
    public const long MaxDiskBytes = 16L * 1024 * 1024;

    private const int FormatVersion = 1;
    private const int MaxKeyChars = 1024;
    private const int MaxEncryptedEntryBytes = MaxPlaintextEntryBytes + 64 * 1024;
    private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly string _root;

    internal ExoOnlineCache()
        : this(Path.Combine(PathHelper.AppDataDir, DirectoryName))
    {
    }

    internal ExoOnlineCache(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    internal bool TryRead<T>(
        string immutableUserId,
        string key,
        out T? value,
        out DateTimeOffset lastSuccessfulSync)
    {
        value = default;
        lastSuccessfulSync = default;
        if (!ValidKey(immutableUserId) || !ValidKey(key))
            return false;

        lock (_gate)
        {
            CleanupTemporaryFiles();
            var path = EntryPath(immutableUserId, key);
            if (!File.Exists(path))
                return false;

            byte[]? plaintext = null;
            try
            {
                var file = new FileInfo(path);
                if (file.Length <= 0 || file.Length > MaxEncryptedEntryBytes)
                    return false;

                plaintext = ExoDpapi.Unprotect(File.ReadAllBytes(path));
                if (plaintext.Length <= 0 || plaintext.Length > MaxPlaintextEntryBytes)
                    return false;

                var envelope = JsonSerializer.Deserialize<StoredEnvelope>(plaintext, JsonOptions);
                if (envelope is null ||
                    envelope.V != FormatVersion ||
                    !string.Equals(envelope.ImmutableUserId, immutableUserId, StringComparison.Ordinal) ||
                    !string.Equals(envelope.Key, key, StringComparison.Ordinal))
                    return false;

                value = envelope.Value.Deserialize<T>(JsonOptions);
                lastSuccessfulSync = envelope.LastSuccessfulSyncUtc.ToUniversalTime();
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
                catch { /* Cache data is still valid when an access-time touch fails. */ }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (plaintext is not null)
                    CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    internal bool Write<T>(
        string immutableUserId,
        string key,
        T value,
        DateTimeOffset lastSuccessfulSync)
    {
        if (!ValidKey(immutableUserId) || !ValidKey(key))
            return false;

        byte[]? plaintext = null;
        try
        {
            var envelope = new StoredEnvelope
            {
                V = FormatVersion,
                ImmutableUserId = immutableUserId,
                Key = key,
                LastSuccessfulSyncUtc = lastSuccessfulSync.ToUniversalTime(),
                Value = JsonSerializer.SerializeToElement(value, JsonOptions),
            };
            plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            if (plaintext.Length > MaxPlaintextEntryBytes)
                return false;

            var encrypted = ExoDpapi.Protect(plaintext);
            lock (_gate)
            {
                EnsureRoot();
                CleanupTemporaryFiles();
                var destination = EntryPath(immutableUserId, key);
                var temporary = Path.Combine(
                    _root,
                    $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var stream = new FileStream(
                               temporary,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               16 * 1024,
                               FileOptions.WriteThrough))
                    {
                        stream.Write(encrypted);
                        stream.Flush(flushToDisk: true);
                    }

                    ExoSessionFileAcl.RestrictToCurrentUser(temporary);
                    File.Move(temporary, destination, overwrite: true);
                    Prune(destination);
                    return true;
                }
                finally
                {
                    TryDelete(temporary);
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal void RemoveByPrefix(string immutableUserId, string prefix)
    {
        if (!ValidKey(immutableUserId) ||
            string.IsNullOrEmpty(prefix) ||
            prefix.Length > MaxKeyChars ||
            prefix.Contains('\0'))
            return;

        lock (_gate)
        {
            if (!Directory.Exists(_root))
                return;
            CleanupTemporaryFiles();
            foreach (var path in SafeEntryFiles())
            {
                byte[]? plaintext = null;
                try
                {
                    var file = new FileInfo(path);
                    if (file.Length <= 0 || file.Length > MaxEncryptedEntryBytes)
                        continue;
                    plaintext = ExoDpapi.Unprotect(File.ReadAllBytes(path));
                    if (plaintext.Length <= 0 || plaintext.Length > MaxPlaintextEntryBytes)
                        continue;
                    var envelope = JsonSerializer.Deserialize<StoredEnvelope>(plaintext, JsonOptions);
                    if (envelope is not null &&
                        envelope.V == FormatVersion &&
                        string.Equals(envelope.ImmutableUserId, immutableUserId, StringComparison.Ordinal) &&
                        envelope.Key.StartsWith(prefix, StringComparison.Ordinal))
                        TryDelete(path);
                }
                catch
                {
                    // A damaged entry cannot be attributed to this account safely.
                }
                finally
                {
                    if (plaintext is not null)
                        CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_root))
                return;
            foreach (var path in SafeEntryFiles())
                TryDelete(path);
            CleanupTemporaryFiles(deleteAll: true);
        }
    }

    private static bool ValidKey(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxKeyChars &&
        !value.Contains('\0');

    private string EntryPath(string immutableUserId, string key)
    {
        var material = Encoding.UTF8.GetBytes(immutableUserId + "\0" + key);
        try
        {
            var name = Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
            return Path.Combine(_root, name + EntryExtension);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private void EnsureRoot()
    {
        Directory.CreateDirectory(_root);
        ExoSessionFileAcl.RestrictToCurrentUser(_root);
    }

    private void Prune(string protectedPath)
    {
        try
        {
            var entries = SafeEntryFiles()
                .Select(path =>
                {
                    try { return new FileInfo(path); }
                    catch { return null; }
                })
                .Where(info => info is not null)
                .Cast<FileInfo>()
                .OrderBy(info => info.LastWriteTimeUtc)
                .ToList();
            var bytes = entries.Sum(info => SafeLength(info));

            foreach (var entry in entries.ToArray())
            {
                if (entries.Count <= MaxEntries && bytes <= MaxDiskBytes)
                    break;
                if (string.Equals(entry.FullName, protectedPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var length = SafeLength(entry);
                if (TryDelete(entry.FullName))
                {
                    bytes = Math.Max(0, bytes - length);
                    entries.Remove(entry);
                }
            }
        }
        catch
        {
            // Promotion already succeeded; maintenance failure does not turn it into a false write failure.
        }
    }

    private IEnumerable<string> SafeEntryFiles()
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_root, "*" + EntryExtension, SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length == 64 && name.All(IsLowerHex))
                yield return path;
        }
    }

    private void CleanupTemporaryFiles(bool deleteAll = false)
    {
        if (!Directory.Exists(_root))
            return;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_root, ".*.tmp", SearchOption.TopDirectoryOnly); }
        catch { return; }
        foreach (var path in files)
        {
            if (!deleteAll)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - StaleTemporaryFileAge)
                        continue;
                }
                catch
                {
                    continue;
                }
            }
            TryDelete(path);
        }
    }

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static long SafeLength(FileInfo info)
    {
        try { return info.Length; }
        catch { return 0; }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private sealed class StoredEnvelope
    {
        public int V { get; init; }
        public string ImmutableUserId { get; init; } = "";
        public string Key { get; init; } = "";
        public DateTimeOffset LastSuccessfulSyncUtc { get; init; }
        public JsonElement Value { get; init; }
    }
}
