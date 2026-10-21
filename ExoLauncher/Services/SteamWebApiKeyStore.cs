using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The user's own Steam Web API key, kept on this PC. Never embedded, never
/// sent to the UI after save, never written to settings.json or a log.
/// </summary>
internal static class SteamWebApiKeyStore
{
    internal const string FileName = "steam-web-api.bin";
    private const int MinLength = 20;
    private const int MaxLength = 64;

    private static readonly object Gate = new();
    private static readonly HashSet<string> PendingDeletes = new(StringComparer.OrdinalIgnoreCase);

    internal static string StorePath => Path.Combine(PathHelper.AppDataDir, FileName);

    internal static bool HasKey()
    {
        lock (Gate) return TryReadUnlocked() is not null;
    }

    internal static string? TryRead()
    {
        lock (Gate) return TryReadUnlocked();
    }

    /// <returns>False when the value is not a key Exo will store.</returns>
    internal static bool Save(string? raw)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return ClearUnlocked();
            }

            var key = Normalize(raw);
            if (key is null) return false;

            byte[]? bytes = null;
            try
            {
                Directory.CreateDirectory(PathHelper.AppDataDir);
                bytes = Encoding.UTF8.GetBytes(key);
                var blob = ExoDpapi.Protect(bytes);
                CryptographicOperations.ZeroMemory(bytes);
                var tmp = StorePath + ".tmp";
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
                        AppLog.Debug("Steam Web API key ACL could not be applied: " + aclEx.GetType().Name);
                    }

                    stream.Write(blob, 0, blob.Length);
                    stream.Flush(flushToDisk: true);
                }

                try { SecretFileAcl.RestrictToCurrentUser(tmp); }
                catch (Exception aclEx)
                {
                    AppLog.Debug("Steam Web API key ACL could not be reapplied: " + aclEx.GetType().Name);
                }

                File.Move(tmp, StorePath, overwrite: true);
                File.Delete(StorePath + ".clear-pending");
                PendingDeletes.Remove(StorePath);
                try
                {
                    SecretFileAcl.RestrictToCurrentUser(StorePath);
                }
                catch (Exception aclEx)
                {
                    // The key is already on disk at this point, so a failed final
                    // ACL application must be visible rather than silently left
                    // on inherited permissions.
                    AppLog.Warn("Steam Web API key file kept default permissions: " + aclEx.GetType().Name);
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Steam Web API key could not be stored: " + ex.GetType().Name);
                return false;
            }
            finally
            {
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    internal static string? Normalize(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length is < MinLength or > MaxLength) return null;
        if (!trimmed.All(ch => char.IsAsciiHexDigit(ch))) return null;
        return trimmed;
    }

    private static string? TryReadUnlocked()
    {
        byte[]? bytes = null;
        try
        {
            var path = StorePath;
            if (PendingDeletes.Contains(path) || File.Exists(path + ".clear-pending"))
            {
                ClearUnlocked();
                return null;
            }
            if (!File.Exists(path)) return null;
            var blob = File.ReadAllBytes(path);
            if (blob.Length == 0) return null;
            bytes = ExoDpapi.Unprotect(blob);
            var key = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return Normalize(key);
        }
        catch (CryptographicException)
        {
            // Another Windows profile's DPAPI blob: HasKey() is false so the
            // UI shows the key as unset; logged once rather than per probe.
            StoreSecretStore.ReportUndecryptableOnce(StorePath, "Steam Web API key");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam Web API key could not be read: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool ClearUnlocked()
    {
        var path = StorePath;
        PendingDeletes.Add(path);
        try
        {
            using var stream = new FileStream(path + ".clear-pending", FileMode.Create, FileAccess.Write,
                FileShare.None, 1, FileOptions.WriteThrough);
            stream.WriteByte(1);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam key cleanup intent could not be stored: " + ex.GetType().Name);
        }
        try
        {
            File.Delete(path);
            File.Delete(path + ".clear-pending");
            PendingDeletes.Remove(path);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam Web API key could not be cleared: " + ex.GetType().Name);
            return false;
        }
    }
}
