using System.Text.Json;

namespace ExoLauncher.Helpers;

/// <summary>One startup receipt for the installer that is currently retaining our previous payload.</summary>
internal static class UpdateStartupHealth
{
    internal const string NonceVariable = "EXO_UPDATE_HEALTH_NONCE";
    internal static bool Requested => IsNonce(Environment.GetEnvironmentVariable(NonceVariable));

    internal static bool MarkHealthy(string version)
    {
        var app = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var root = Path.GetDirectoryName(app);
        var nonce = Environment.GetEnvironmentVariable(NonceVariable);
        if (root is null || Environment.ProcessPath is not { } executable || !IsNonce(nonce)) return false;
        var written = TryWrite(root, executable, nonce!, version, Environment.ProcessId);
        if (written) Environment.SetEnvironmentVariable(NonceVariable, null);
        return written;
    }

    internal static bool TryWrite(string root, string executable, string nonce, string version, int processId)
    {
        try
        {
            if (!IsNonce(nonce)) return false;
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var app = Path.Combine(root, "app");
            if (!Path.GetFullPath(executable).Equals(Path.Combine(app, "ExoLauncher.exe"), StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var path in new[] { root, app, executable })
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            var pendingPath = Path.Combine(root, "update-pending.json");
            if (!File.Exists(pendingPath) || new FileInfo(pendingPath).Length > 4096 ||
                (File.GetAttributes(pendingPath) & FileAttributes.ReparsePoint) != 0) return false;
            using var pending = JsonDocument.Parse(File.ReadAllText(pendingPath));
            var record = pending.RootElement;
            if (record.GetProperty("nonce").GetString() != nonce || record.GetProperty("schema").GetInt32() != 1 ||
                Services.AppUpdateService.Normalize(record.GetProperty("version").GetString() ?? "") != Services.AppUpdateService.Normalize(version) ||
                !Path.GetFullPath(record.GetProperty("executable").GetString()!).Equals(executable, StringComparison.OrdinalIgnoreCase) ||
                !record.GetProperty("createdUtc").TryGetDateTimeOffset(out var created)) return false;
            var age = DateTimeOffset.UtcNow - created;
            if (age < TimeSpan.Zero || age > TimeSpan.FromMinutes(5)) return false;
            var receipt = Path.Combine(root, "update-health-" + nonce + ".json");
            var temporary = receipt + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new { schema = 1, nonce, version = Services.AppUpdateService.Normalize(version), processId, executable, healthy = true }));
                File.Move(temporary, receipt, overwrite: true);
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Update startup receipt was not written: " + ex.GetType().Name);
            return false;
        }
    }

    private static bool IsNonce(string? value) => value is { Length: 32 } && value.All(Uri.IsHexDigit);
}
