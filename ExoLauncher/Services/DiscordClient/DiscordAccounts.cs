using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The Discord accounts signed in on this PC, for Switch accounts (up to five, as Discord's app keeps): each one's id,
/// name, picture and sign-in, kept as <see cref="DiscordToken"/> keeps the current one, DPAPI-wrapped in a file only this
/// Windows user can read. Sign-ins never leave the host: the room gets ids, names and pictures.
/// </summary>
internal static class DiscordAccounts
{
    private const string FileName = "discord-accounts.bin";
    internal const int Most = 5;
    private static readonly Lock Gate = new();

    internal sealed record Account(string Id, string Name, string? Avatar, string Token);

    private static string StorePath => Path.Combine(PathHelper.AppDataDir, FileName);

    internal static List<Account> List()
    {
        lock (Gate) return Load();
    }

    /// <summary>The account Exo signed into now goes first; the oldest beyond five goes.</summary>
    internal static void Remember(string id, string name, string? avatar, string token)
    {
        lock (Gate)
        {
            var accounts = Load();
            accounts.RemoveAll(a => a.Id == id);
            accounts.Insert(0, new Account(id, name, avatar, token));
            Save(accounts.Take(Most).ToList());
        }
    }

    internal static string? TokenOf(string id)
    {
        lock (Gate) return Load().FirstOrDefault(a => a.Id == id)?.Token;
    }

    /// <summary>A sign-in Discord ended (it signed the account out): that account goes from the list.</summary>
    internal static void ForgetToken(string token)
    {
        lock (Gate)
        {
            var accounts = Load();
            if (accounts.RemoveAll(a => a.Token == token) > 0) Save(accounts);
        }
    }

    internal static void Forget(string id)
    {
        lock (Gate)
        {
            var accounts = Load();
            if (accounts.RemoveAll(a => a.Id == id) > 0) Save(accounts);
        }
    }

    private static List<Account> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return [];
            var bytes = ExoDpapi.Unprotect(File.ReadAllBytes(StorePath));
            try { return JsonSerializer.Deserialize<List<Account>>(bytes) ?? []; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            AppLog.Warn("Discord accounts could not be read: " + ex.GetType().Name);
            return [];
        }
    }

    private static void Save(List<Account> accounts)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(accounts);
        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var tmp = StorePath + ".tmp";
            File.WriteAllBytes(tmp, ExoDpapi.Protect(bytes));
            try { SecretFileAcl.RestrictToCurrentUser(tmp); }
            catch (Exception aclEx) { AppLog.Debug("Discord accounts ACL: " + aclEx.GetType().Name); }
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            AppLog.Warn("Discord accounts could not be saved: " + ex.GetType().Name);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
