using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Attack 5 — data at rest. Are secrets written in clear text, and does the log
/// redactor catch tokens/emails? Exercises the REAL key store (ExoDpapi +
/// SteamWebApiKeyStore), SecretFileAcl, and AppLog.Redact / AppLog write path
/// into a temp data dir.
/// </summary>
internal static class Attack5_DataAtRest
{
    internal static void Run(string tempDataDir)
    {
        Check.Section("Attack 5 — data at rest (secret storage + log redaction)");

        // --- DPAPI round-trip and ciphertext does not contain plaintext ---
        var plain = Encoding.UTF8.GetBytes("super-secret-token-ABCDEF0123456789");
        var blob = ExoDpapi.Protect(plain);
        var back = ExoDpapi.Unprotect(blob);
        Check.That("DPAPI round-trips", back.AsSpan().SequenceEqual(plain), "Unprotect(Protect(x)) == x");
        Check.That("DPAPI blob is not clear text",
            Encoding.UTF8.GetString(blob).Contains("super-secret-token") == false &&
            IndexOf(blob, plain) < 0,
            "the plaintext bytes do not appear anywhere in the encrypted blob");

        // --- SteamWebApiKeyStore writes an encrypted .bin, not clear text ---
        var fakeKey = "0123456789ABCDEF0123456789ABCDEF01234567"; // 40 hex chars, matches Normalize
        var saved = SteamWebApiKeyStore.Save(fakeKey);
        Check.That("Steam Web API key saved", saved, "Save(validHexKey) == true");
        var binPath = SteamWebApiKeyStore.StorePath;
        var rawBytes = File.Exists(binPath) ? File.ReadAllBytes(binPath) : Array.Empty<byte>();
        Check.That("steam-web-api.bin exists", rawBytes.Length > 0, "file at " + binPath);
        Check.That("Stored key is encrypted at rest (not clear text)",
            !ContainsText(rawBytes, fakeKey),
            "the raw key string does not appear in steam-web-api.bin (DPAPI-wrapped)");
        Check.That("Stored key round-trips through the store",
            SteamWebApiKeyStore.TryRead() == fakeKey, "TryRead() returns the saved key");

        // Scan the ENTIRE temp data dir (settings, logs, caches) for the key in clear text.
        var leaked = new List<string>();
        foreach (var f in Directory.EnumerateFiles(tempDataDir, "*", SearchOption.AllDirectories))
        {
            try { if (ContainsText(File.ReadAllBytes(f), fakeKey)) leaked.Add(f); } catch { }
        }
        Check.That("Key appears in clear text nowhere in the data dir",
            leaked.Count == 0, leaked.Count == 0 ? "scanned all files, no clear-text key" : "LEAKED in: " + string.Join(", ", leaked));

        // --- AppLog.Redact (Helpers/AppLog.cs) ---
        var cases = new (string Name, string Input, bool ShouldRedact)[]
        {
            ("bearer", "Authorization: Bearer abc.DEF-123_tok", true),
            ("access_token=", "oauth failed access_token=SEKRET_VALUE_123 retrying", true),
            ("json token", "{\"token\":\"SEKRET_VALUE_123\",\"x\":1}", true),
            ("refresh_token", "refresh_token: rt_SEKRET_VALUE_123", true),
            ("client_secret", "client_secret=cs_SEKRET_VALUE_123", true),
            ("steam_id", "steam_id=76561197960287930", true),
            ("email", "user player.one@example.com signed in", true),
            ("bare-token-no-label", "got value 0123456789ABCDEF0123456789ABCDEF01234567", false),
        };
        foreach (var c in cases)
        {
            var red = AppLog.Redact(c.Input);
            bool stillHasSecret = red.Contains("SEKRET_VALUE_123") || red.Contains("abc.DEF-123_tok")
                || red.Contains("76561197960287930") || red.Contains("@example.com") || red.Contains("rt_SEKRET") || red.Contains("cs_SEKRET");
            if (c.ShouldRedact)
                Check.That($"Redact catches [{c.Name}]", !stillHasSecret, "'" + c.Input + "' -> '" + red + "'");
            else
                Check.Info($"LIMITATION [{c.Name}]: unlabeled secret is NOT redacted -> '" + red + "'");
        }

        // --- End-to-end: AppLog write path redacts before hitting app.log ---
        AppLog.Info("probe access_token=END2END_SEKRET and email probe@example.com");
        Thread.Sleep(600); // async channel writer
        var logPath = Path.Combine(PathHelper.LogsDir, "app.log");
        // AppLog keeps the append handle open for the run; share with it.
        string logText = "";
        if (File.Exists(logPath))
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            logText = sr.ReadToEnd();
        }
        Check.That("app.log contains no clear-text token/email",
            logText.Length > 0 && !logText.Contains("END2END_SEKRET") && !logText.Contains("probe@example.com"),
            File.Exists(logPath) ? "wrote app.log, secret + email redacted" : "app.log not written");

        // Cleanup secret file.
        try { SteamWebApiKeyStore.Save(null); } catch { }
    }

    private static bool ContainsText(byte[] haystack, string needle)
    {
        foreach (var enc in new Encoding[] { Encoding.UTF8, Encoding.Unicode, Encoding.ASCII })
            if (IndexOf(haystack, enc.GetBytes(needle)) >= 0) return true;
        return false;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
