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
                ClearUnlocked();
                return true;
            }

            var key = Normalize(raw);
            if (key is null) return false;

            try
            {
                Directory.CreateDirectory(PathHelper.AppDataDir);
                var bytes = Encoding.UTF8.GetBytes(key);
                var blob = ExoDpapi.Protect(bytes);
                CryptographicOperations.ZeroMemory(bytes);
                var tmp = StorePath + ".tmp";
                File.WriteAllBytes(tmp, blob);
                File.Move(tmp, StorePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Steam Web API key could not be stored: " + ex.GetType().Name);
                return false;
            }
        }
    }

    internal static void Clear()
    {
        lock (Gate) ClearUnlocked();
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
        try
        {
            var path = StorePath;
            if (!File.Exists(path)) return null;
            var blob = File.ReadAllBytes(path);
            if (blob.Length == 0) return null;
            var bytes = ExoDpapi.Unprotect(blob);
            var key = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return Normalize(key);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam Web API key could not be read: " + ex.GetType().Name);
            return null;
        }
    }

    private static void ClearUnlocked()
    {
        try
        {
            if (File.Exists(StorePath)) File.Delete(StorePath);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Steam Web API key could not be cleared: " + ex.GetType().Name);
        }
    }
}
