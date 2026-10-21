namespace ExoLauncher.Services;

/// <summary>Read-only Steam family-share hint from an app manifest.</summary>
internal static class SteamLibraryExtras
{
    internal static bool ManifestLooksFamilyShared(IReadOnlyDictionary<string, string> fields, string? steamId64)
    {
        if (!string.IsNullOrWhiteSpace(fields.GetValueOrDefault("SharedBy")))
            return true;
        if (!string.IsNullOrWhiteSpace(fields.GetValueOrDefault("BorrowedFrom")))
            return true;
        // Field lookup is case-insensitive, so this also covers OwnerSteamId.
        var owner = fields.GetValueOrDefault("OwnerSteamID");
        return !string.IsNullOrWhiteSpace(owner)
               && !string.IsNullOrWhiteSpace(steamId64)
               && !owner.Equals(steamId64, StringComparison.OrdinalIgnoreCase);
    }
}
