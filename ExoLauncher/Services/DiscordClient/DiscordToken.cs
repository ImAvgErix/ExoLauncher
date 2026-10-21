using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The Discord sign-in of Exo's own Discord client, kept on this PC DPAPI-wrapped
/// (<see cref="ExoDpapi"/>) in a file only this Windows user can read. It is sent
/// only to Discord's own hosts (<see cref="DiscordRest"/>, <see cref="DiscordSession"/>),
/// never to the UI, never written to settings.json or a log.
/// </summary>
internal static class DiscordToken
{
    private const string FileName = "discord-client.bin";
    private static readonly Lock Gate = new();

    private static string StorePath => Path.Combine(PathHelper.AppDataDir, FileName);

    internal static string? Read()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(StorePath)) return null;
                var bytes = ExoDpapi.Unprotect(File.ReadAllBytes(StorePath));
                var token = Encoding.UTF8.GetString(bytes);
                CryptographicOperations.ZeroMemory(bytes);
                return token.Length is > 20 and < 200 ? token : null;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Discord sign-in could not be read: " + ex.GetType().Name);
                return null;
            }
        }
    }

    internal static bool Save(string token)
    {
        lock (Gate)
        {
            var bytes = Encoding.UTF8.GetBytes(token);
            try
            {
                Directory.CreateDirectory(PathHelper.AppDataDir);
                var blob = ExoDpapi.Protect(bytes);
                var tmp = StorePath + ".tmp";
                File.WriteAllBytes(tmp, blob);
                try { SecretFileAcl.RestrictToCurrentUser(tmp); }
                catch (Exception aclEx) { AppLog.Debug("Discord sign-in ACL: " + aclEx.GetType().Name); }
                File.Move(tmp, StorePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Discord sign-in could not be saved: " + ex.GetType().Name);
                return false;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            try { File.Delete(StorePath); }
            catch (Exception ex) { AppLog.Warn("Discord sign-in could not be removed: " + ex.GetType().Name); }
        }
    }
}
