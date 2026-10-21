namespace ExoLauncher.Helpers;

/// <summary>
/// The music room's Songs page: every song in the user's library, in one
/// order, narrowed by a typed filter. Spotify's is Liked Songs (its own sort
/// and filter, as its Liked Songs page asks for them); Apple Music's is the
/// library's songs and YouTube Music's its library Songs (FEmusic_liked_videos),
/// read whole into the hidden page and ordered there. The same order plays:
/// a song chosen on the page plays on through the list as shown.
/// </summary>
internal static class MusicSongs
{
    private static readonly HashSet<string> Sorts = ["added", "title", "artist", "album"];

    /// "sort|filter": one of the page's sort ids, then the typed filter (any
    /// text up to 100 characters, no control characters). It reaches the page
    /// only as a JSON string.
    internal static bool IsQuery(string? query)
    {
        if (query is null) return true;
        var bar = query.IndexOf('|');
        if (bar < 0 || query.Length - bar - 1 > 100) return false;
        var sort = query[..bar];
        return (sort.Length == 0 || Sorts.Contains(sort)) && !query.Any(char.IsControl);
    }
}
