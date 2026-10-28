using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Helpers;

/// <summary>
/// Listen along: the song a friend plays (their Discord activity's title, artists and length) found among the
/// listener's own service's search results. Services write the same recording differently: "Song (feat. X)",
/// "Song - Remastered 2011", artists as "A, B", "A; B", "A &amp; B" or "A - Topic". The best candidate shares the
/// title's words and an artist, and its length is within a few seconds. A live, remix, acoustic, instrumental,
/// karaoke, cover or sped-up version only wins when the friend's song is that version too.
///
/// How it can fail: another artist's song with the same name wins (an artist must match when both sides name one);
/// the music video or an extended mix wins over the album cut (length decides between equals); nothing matches when
/// one side writes "&amp;" and the other "and", or one adds "Remastered" (both are folded away); a live version
/// stands in for the studio one (version words must agree).
/// </summary>
internal static class MusicMatch
{
    internal sealed record Song(string Title, IReadOnlyList<string> Artists, long? DurationMs);

    private const int MaxCandidates = 50;
    private const int MaxText = 300;
    private const double Enough = 0.62;

    // Words that make a different recording; "remaster", "radio edit", "mono" and the like don't.
    private static readonly string[] Versions = ["live", "remix", "acoustic", "instrumental", "karaoke", "cover", "sped up", "slowed",
        "nightcore", "demo", "unplugged", "reverb", "8d", "orchestral", "piano version", "extended"];
    private static readonly string[] Joins = [",", ";", " & ", " x ", " and ", " feat. ", " feat ", " ft. ", " ft ", " with ", " / "];

    /// The index of the best candidate, or -1 when none is close enough.
    internal static int Best(Song wanted, IReadOnlyList<Song> candidates)
    {
        var (best, bestScore) = (-1, Enough);
        for (var i = 0; i < Math.Min(candidates.Count, MaxCandidates); i++)
        {
            // Strictly better: on a tie the service's own order (its relevance) wins.
            var score = Score(wanted, candidates[i]);
            if (score > bestScore + 0.0001) (best, bestScore) = (i, score);
        }
        return best;
    }

    /// 0 to about 1: how surely a candidate is the wanted recording.
    internal static double Score(Song wanted, Song candidate)
    {
        var (wantedWords, candidateWords) = (Words(Core(wanted.Title)), Words(Core(candidate.Title)));
        if (wantedWords.Count == 0 || candidateWords.Count == 0) return 0;
        var title = (double)wantedWords.Intersect(candidateWords).Count() / wantedWords.Union(candidateWords).Count();
        if (title < 0.6) return 0;
        var (wantedArtists, candidateArtists) = (Artists(wanted), Artists(candidate));
        var artist = wantedArtists.Count == 0 || candidateArtists.Count == 0 ? 0.5
            : wantedArtists.Any(a => candidateArtists.Any(b => SameArtist(a, b))) ? 1 : 0;
        if (artist == 0) return 0;
        var length = wanted.DurationMs is { } w && candidate.DurationMs is { } c
            ? Math.Abs(w - c) switch { <= 2500 => 1, <= 6000 => 0.7, <= 15000 => 0.3, _ => 0 } : 0.5;
        var version = Tags(wanted.Title).SetEquals(Tags(candidate.Title)) ? 0 : 0.4;
        return title * 0.5 + artist * 0.3 + length * 0.2 - version;
    }

    /// music.match: { song: { title, artists, durationMs }, candidates: [the same] } → { ok, index }.
    internal static object Answer(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("song", out var song) || Read(song) is not { } wanted ||
            !p.TryGetProperty("candidates", out var list) || list.ValueKind != JsonValueKind.Array)
            return new { ok = false, message = "Nothing to match." };
        var candidates = list.EnumerateArray().Take(MaxCandidates).Select(c => Read(c) ?? new Song("", [], null)).ToList();
        return new { ok = true, index = Best(wanted, candidates) };
    }

    private static Song? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String) return null;
        var artists = e.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Take(10).Select(x => Clip(x.GetString())).ToList() : [];
        long? length = e.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var ms) && ms > 0 ? ms : null;
        return new Song(Clip(t.GetString()), artists, length);
    }

    private static string Clip(string? text) => text is null ? "" : text.Length > MaxText ? text[..MaxText] : text;

    // Lower case, no accents, "&" read as "and".
    private static string Fold(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) plain.Append(char.ToLowerInvariant(ch));
        return plain.ToString().Normalize(NormalizationForm.FormC).Replace("&", " and ", StringComparison.Ordinal);
    }

    // The title without what services add around it: (feat. …), [Remastered], " - 2011 Remaster", " - Live at …".
    private static string Core(string title)
    {
        var text = Fold(title);
        var cut = text.IndexOfAny(['(', '[']);
        if (cut > 0) text = text[..cut];
        var dash = text.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) text = text[..dash];
        var feat = text.IndexOf(" feat", StringComparison.Ordinal);
        return feat > 0 ? text[..feat] : text;
    }

    private static HashSet<string> Words(string text) => [.. Spaced(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    // Letters and digits as words between single spaces, with a space at each end.
    private static string Spaced(string text) => " " + string.Join(' ', Regex.Split(text, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0)) + " ";

    // The version words in a title, beyond any its own name has ("Live Forever" is not live).
    private static HashSet<string> Tags(string title)
    {
        var (all, core) = (Spaced(Fold(title)), Spaced(Core(title)));
        return [.. Versions.Where(v => all.Contains(" " + v + " ", StringComparison.Ordinal) && !core.Contains(" " + v + " ", StringComparison.Ordinal))];
    }

    // "Beatles" is "The Beatles"; a short name must match whole ("Ye" is not inside "Kanye West").
    private static bool SameArtist(string a, string b) =>
        a == b || Math.Min(a.Length, b.Length) >= 5 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));

    private static List<string> Artists(Song song)
    {
        var names = new List<string>();
        foreach (var each in song.Artists)
        {
            var text = " " + Fold(each) + " ";
            foreach (var join in Joins) text = text.Replace(join, "|", StringComparison.Ordinal);
            foreach (var part in text.Split('|'))
            {
                var name = part.Replace(" - topic", "", StringComparison.Ordinal).Replace("vevo", "", StringComparison.Ordinal).Trim();
                if (name.StartsWith("the ", StringComparison.Ordinal)) name = name[4..];
                name = new string([.. name.Where(char.IsLetterOrDigit)]);
                if (name.Length > 0) names.Add(name);
            }
        }
        return names;
    }
}
