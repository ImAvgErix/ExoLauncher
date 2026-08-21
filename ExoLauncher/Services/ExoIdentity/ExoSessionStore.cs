using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Session blob at <c>%LOCALAPPDATA%\ExoLauncher\auth.bin</c>.
/// DPAPI-CurrentUser plus a restrictive DACL. Never written to settings.json,
/// WebView2 storage, logs, or exception messages.
/// </summary>
internal sealed class ExoSessionStore
{
    public const string FileName = "auth.bin";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    public ExoSessionStore() : this(System.IO.Path.Combine(PathHelper.AppDataDir, FileName))
    {
    }

    public ExoSessionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path => _path;

    public ExoSession? TryLoad()
    {
        try
        {
            if (!File.Exists(_path))
                return null;
            var blob = File.ReadAllBytes(_path);
            if (blob.Length == 0)
                return null;
            var json = ExoDpapi.Unprotect(blob);
            try
            {
                var session = JsonSerializer.Deserialize<ExoSession>(json, JsonOpts);
                if (session is null || string.IsNullOrEmpty(session.AccessToken))
                    return null;
                return session;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Exo session blob could not be read: " + ex.GetType().Name);
            return null;
        }
    }

    public void Save(ExoSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrEmpty(session.AccessToken))
            throw new InvalidOperationException("Refusing to store an empty session.");

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.SerializeToUtf8Bytes(session, JsonOpts);
        try
        {
            var blob = ExoDpapi.Protect(json);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(
                       temp,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.None))
            {
                try { ExoSessionFileAcl.RestrictToCurrentUser(temp); }
                catch (Exception ex)
                {
                    AppLog.Debug("Exo session ACL could not be applied: " + ex.GetType().Name);
                }

                stream.Write(blob, 0, blob.Length);
                stream.Flush(flushToDisk: true);
            }

            try { ExoSessionFileAcl.RestrictToCurrentUser(temp); }
            catch (Exception ex)
            {
                AppLog.Debug("Exo session ACL could not be reapplied: " + ex.GetType().Name);
            }

            File.Move(temp, _path, overwrite: true);
            try { ExoSessionFileAcl.RestrictToCurrentUser(_path); }
            catch (Exception ex)
            {
                AppLog.Debug("Exo session ACL could not be applied: " + ex.GetType().Name);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    public bool Delete()
    {
        var removed = true;
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (Exception ex)
        {
            removed = false;
            AppLog.Debug("Exo session blob could not be deleted: " + ex.GetType().Name);
        }

        try
        {
            var temp = _path + ".tmp";
            if (File.Exists(temp))
                File.Delete(temp);
        }
        catch (Exception ex)
        {
            removed = false;
            AppLog.Debug("Exo temporary session blob could not be deleted: " + ex.GetType().Name);
        }

        return removed && !File.Exists(_path) && !File.Exists(_path + ".tmp");
    }
}

/// <summary>In-memory session. Do not log or interpolate this type.</summary>
internal sealed class ExoSession
{
    public int V { get; set; } = 1;
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public string? AccountId { get; set; }
    public string? Handle { get; set; }
    public string? Email { get; set; }
    public string? Provider { get; set; }
}
