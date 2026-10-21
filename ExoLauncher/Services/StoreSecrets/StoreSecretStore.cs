using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// DPAPI blob for a user-pasted store key. Same rules as the Steam Web API
/// key: never logged, never written to settings.json, never sent back to the UI.
/// </summary>
internal static class StoreSecretStore
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> PendingDeletes = new(StringComparer.OrdinalIgnoreCase);

    internal static string PathFor(string fileName) =>
        System.IO.Path.Combine(PathHelper.AppDataDir, fileName);

    internal static bool Has(string fileName, Func<string, string?> normalize)
    {
        lock (Gate) return TryReadUnlocked(fileName, normalize) is not null;
    }

    internal static string? TryRead(string fileName, Func<string, string?> normalize)
    {
        lock (Gate) return TryReadUnlocked(fileName, normalize);
    }

    internal static bool Save(string fileName, string? raw, Func<string, string?> normalize)
    {
        lock (Gate)
        {
            var path = PathFor(fileName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                PendingDeletes.Add(path);
                TryRecordDeleteIntent(path);
                return TryDelete(path);
            }

            var key = normalize(raw);
            if (key is null) return false;

            byte[]? bytes = null;
            try
            {
                Directory.CreateDirectory(PathHelper.AppDataDir);
                bytes = Encoding.UTF8.GetBytes(key);
                var blob = ExoDpapi.Protect(bytes);
                CryptographicOperations.ZeroMemory(bytes);
                var tmp = path + ".tmp";
                using (var stream = new FileStream(
                           tmp,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.None))
                {
                    try { SecretFileAcl.RestrictToCurrentUser(tmp); }
                    catch (Exception aclEx)
                    {
                        AppLog.Debug("Store key ACL could not be applied: " + aclEx.GetType().Name);
                    }

                    stream.Write(blob, 0, blob.Length);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tmp, path, overwrite: true);
                File.Delete(path + ".clear-pending");
                PendingDeletes.Remove(path);
                try { SecretFileAcl.RestrictToCurrentUser(path); }
                catch (Exception aclEx)
                {
                    AppLog.Warn("Store key file kept default permissions: " + aclEx.GetType().Name);
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Store key could not be stored: " + ex.GetType().Name);
                return false;
            }
            finally
            {
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static string? TryReadUnlocked(string fileName, Func<string, string?> normalize)
    {
        byte[]? bytes = null;
        try
        {
            var path = PathFor(fileName);
            if (PendingDeletes.Contains(path) || File.Exists(path + ".clear-pending"))
            {
                // Keep a failed clear inactive and retry it on the next access.
                TryDelete(path);
                return null;
            }
            if (!File.Exists(path)) return null;
            var blob = File.ReadAllBytes(path);
            if (blob.Length == 0) return null;
            bytes = ExoDpapi.Unprotect(blob);
            var key = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return normalize(key);
        }
        catch (CryptographicException)
        {
            // Encrypted under another Windows profile (or machine): the key is
            // unusable here and reads as unset. Logged once, not per probe.
            ReportUndecryptableOnce(PathFor(fileName), "Store key " + fileName);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Store key could not be read: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static readonly HashSet<string> ReportedUndecryptable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One Info line per blob that DPAPI cannot open for this Windows user.
    /// The store then reports the key as unset so the UI offers to re-enter it.
    /// </summary>
    internal static void ReportUndecryptableOnce(string path, string label)
    {
        lock (ReportedUndecryptable)
        {
            if (!ReportedUndecryptable.Add(path)) return;
        }
        AppLog.Info(label + " was saved under another Windows profile and cannot be decrypted here; treating it as unset.");
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + ".clear-pending");
            PendingDeletes.Remove(path);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Store key could not be cleared: " + ex.GetType().Name);
            return false;
        }
    }

    private static void TryRecordDeleteIntent(string path)
    {
        try
        {
            using var stream = new FileStream(path + ".clear-pending", FileMode.Create, FileAccess.Write,
                FileShare.None, 1, FileOptions.WriteThrough);
            stream.WriteByte(1);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            // The in-memory intent still prevents reuse if the directory also
            // rejects the non-secret marker; the clear reports deletion failure.
            AppLog.Debug("Store key cleanup intent could not be stored: " + ex.GetType().Name);
        }
    }
}
