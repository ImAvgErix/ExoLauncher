namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The fonts of Discord's display name styles (Sakura, Jellybean, Modern and the rest), the files Discord's web
/// client uses. Discord serves them only to its own site, so Exo fetches each once into its data folder and hands
/// the page the bytes, which it registers as a font.
/// </summary>
internal static class DiscordFonts
{
    // The family as the room names it -> its file in Discord's web client (October 2026).
    private static readonly Dictionary<string, string> Files = new()
    {
        ["Sakura"] = "33d4f12a85e1f736", ["Jellybean"] = "e6f5f44abb520735", ["Modern"] = "c560709c3470bb66",
        ["Medieval"] = "52b541f86401a5b6", ["8Bit"] = "69c735ca5c604de7", ["Vampyre"] = "8f20cb550d739cea",
        ["Tempo"] = "27b34a77ba5d693e", ["Monkey Bars"] = "45ef50dce38a931a", ["Mainframe"] = "4841dace333e9054",
        ["Headbang"] = "c00df4f25667809b", ["Journal"] = "aa59ae54ce7e540a",
    };

    /// <summary>The font's bytes (WOFF2), or null for a family Discord doesn't have or a file that didn't arrive.</summary>
    internal static async Task<byte[]?> BytesAsync(string? family)
    {
        if (family is null || !Files.TryGetValue(family, out var file)) return null;
        return await DiscordSounds.AssetAsync("fonts", file, "woff2").ConfigureAwait(false) is { } path
            ? await File.ReadAllBytesAsync(path).ConfigureAwait(false)
            : null;
    }
}
