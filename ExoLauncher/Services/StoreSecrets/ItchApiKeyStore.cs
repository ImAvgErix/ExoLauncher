namespace ExoLauncher.Services;

/// <summary>User API key from https://itch.io/user/settings/api-keys.</summary>
internal static class ItchApiKeyStore
{
    internal const string FileName = "itch-api.bin";

    internal static bool HasKey() => StoreSecretStore.Has(FileName, Normalize);

    internal static string? TryRead() => StoreSecretStore.TryRead(FileName, Normalize);

    internal static bool Save(string? raw) => StoreSecretStore.Save(FileName, raw, Normalize);

    internal static string? Normalize(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length is < 16 or > 256) return null;
        if (!trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            return null;
        return trimmed;
    }
}
