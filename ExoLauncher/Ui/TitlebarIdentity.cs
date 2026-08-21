namespace ExoLauncher.Ui;

/// <summary>
/// Titlebar avatar. A profile read that lands before the library scan must not
/// look like the user has no art — keep the saved id, or keep what is already
/// on screen, until the library can confirm a real clear.
/// </summary>
internal static class TitlebarIdentity
{
    public readonly record struct AvatarPick(string Kind, string? GameId, string? ImageUrl, string Initial);

    public readonly record struct Applied(
        string? Name,
        string? AvatarGameId,
        string? AvatarImageUrl,
        bool Cacheable);

    public static string? CoalesceSavedAvatarGameId(string? fromProfile, string? fromSettings)
    {
        if (!string.IsNullOrWhiteSpace(fromProfile)) return fromProfile.Trim();
        if (!string.IsNullOrWhiteSpace(fromSettings)) return fromSettings.Trim();
        return null;
    }

    public static AvatarPick Pick(
        string? avatarImageUrl,
        string? avatarGameId,
        string? name,
        IEnumerable<string> libraryGameIds)
    {
        var image = (avatarImageUrl ?? string.Empty).Trim();
        if (image.Length > 0)
            return new AvatarPick("image", null, image, InitialOf(name));

        var id = (avatarGameId ?? string.Empty).Trim();
        if (id.Length > 0 &&
            libraryGameIds.Any(gameId => string.Equals(gameId, id, StringComparison.OrdinalIgnoreCase)))
        {
            return new AvatarPick("game", id, null, InitialOf(name));
        }

        return new AvatarPick("initial", null, null, InitialOf(name));
    }

    public static Applied Apply(
        bool ok,
        string? incomingName,
        string? incomingGameId,
        string? incomingImageUrl,
        string? currentGameId,
        string? currentImageUrl,
        bool libraryReady)
    {
        if (!ok) return new Applied(null, currentGameId, currentImageUrl, Cacheable: false);

        var nextGame = NullIfEmpty(incomingGameId);
        var nextImage = NullIfEmpty(incomingImageUrl);
        var stripped = nextGame is null && nextImage is null && !libraryReady;
        return new Applied(
            Name: NullIfEmpty(incomingName),
            AvatarGameId: nextGame ?? (libraryReady ? null : currentGameId),
            AvatarImageUrl: nextImage ?? (nextGame is not null ? null : libraryReady ? null : currentImageUrl),
            Cacheable: !stripped);
    }

    private static string InitialOf(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length > 0 ? char.ToUpperInvariant(trimmed[0]).ToString() : "E";
    }

    private static string? NullIfEmpty(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }
}
