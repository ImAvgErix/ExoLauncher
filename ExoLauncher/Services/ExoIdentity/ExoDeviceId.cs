using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Opaque per-machine id for profile/sync last-write-wins. Not a secret.
/// Stored next to <c>auth.bin</c>, never in <c>settings.json</c>.
/// </summary>
internal static class ExoDeviceId
{
    public const string FileName = "device-id";

    public static string Get()
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, FileName);
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (IsValid(existing))
                    return existing;
            }

            var created = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, created);
            return created;
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    internal static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 1 and <= 80 &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
}
