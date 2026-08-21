using ExoLauncher.Services;

namespace ExoLauncher.Ui;

/// <summary>
/// Installed-library scoring. Exact/prefix matches win; fuzzy matching needs
/// strong token coverage so a typo does not turn the library into a broad list.
/// </summary>
internal static class LibrarySearch
{
    public static int Score(string title, string query)
    {
        var normalizedTitle = StoreSearchService.Normalize(title);
        var normalizedQuery = StoreSearchService.Normalize(query);
        if (string.IsNullOrEmpty(normalizedTitle) || string.IsNullOrEmpty(normalizedQuery))
            return -1;
        if (normalizedTitle == normalizedQuery) return 1200;
        if (normalizedTitle.StartsWith(normalizedQuery, StringComparison.Ordinal)) return 1050;
        if (normalizedTitle.Contains(" " + normalizedQuery + " ", StringComparison.Ordinal)
            || normalizedTitle.StartsWith(normalizedQuery + " ", StringComparison.Ordinal)
            || normalizedTitle.EndsWith(" " + normalizedQuery, StringComparison.Ordinal))
            return 900;

        var titleTokens = ExpandAdjacentTokens(Tokens(normalizedTitle));
        var queryTokens = Tokens(normalizedQuery);
        var used = new bool[titleTokens.Length];
        var matched = 0;
        var exact = 0;
        var prefixes = 0;
        var fuzzy = 0;
        var inOrder = true;
        var lastTitleIndex = -1;
        var unmatchedAreOnlyNumbers = true;

        foreach (var queryToken in queryTokens)
        {
            var bestIndex = -1;
            var bestQuality = 0;
            for (var index = 0; index < titleTokens.Length; index++)
            {
                if (used[index]) continue;
                var quality = TokenQuality(titleTokens[index], queryToken);
                if (quality <= bestQuality) continue;
                bestIndex = index;
                bestQuality = quality;
            }

            if (bestIndex < 0)
            {
                unmatchedAreOnlyNumbers &= queryToken.Length == 1 && char.IsDigit(queryToken[0]);
                continue;
            }

            used[bestIndex] = true;
            matched++;
            if (bestIndex < lastTitleIndex) inOrder = false;
            lastTitleIndex = bestIndex;
            if (bestQuality == 3) exact++;
            else if (bestQuality == 2) prefixes++;
            else fuzzy++;
        }

        var nonNumericQueryCount = queryTokens.Count(token => !token.All(char.IsDigit));
        var allMatched = matched == queryTokens.Length;
        var strongPartial = unmatchedAreOnlyNumbers && matched >= 2 && nonNumericQueryCount >= 2;
        var singleStrongToken =
            queryTokens.Length == 1 &&
            matched == 1 &&
            (exact == 1 || (prefixes == 1 && queryTokens[0].Length >= 3));
        if (!allMatched && !strongPartial && !singleStrongToken) return -1;
        if (queryTokens.Length == 1 && fuzzy == 1 && queryTokens[0].Length < 5) return -1;

        var score = 620 + exact * 95 + prefixes * 55 + fuzzy * 24;
        score += Math.Min(80, matched * 18);
        if (inOrder) score += 30;
        if (strongPartial) score -= 85;
        score -= fuzzy * 12;
        return score;
    }

    private static string[] Tokens(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // Compound names are commonly typed without their visual separator
    // ("spiderman" for "Spider-Man"). Add only adjacent joins so fuzzy
    // matching remains bounded and cannot degrade into substring matching.
    private static string[] ExpandAdjacentTokens(string[] tokens)
    {
        if (tokens.Length < 2) return tokens;
        var expanded = new List<string>(tokens.Length * 2 - 1);
        for (var index = 0; index < tokens.Length; index++)
        {
            expanded.Add(tokens[index]);
            if (index + 1 < tokens.Length)
                expanded.Add(tokens[index] + tokens[index + 1]);
        }
        return expanded.ToArray();
    }

    private static int TokenQuality(string titleToken, string queryToken)
    {
        if (titleToken == queryToken) return 3;
        if (queryToken.Length >= 3 && titleToken.Length >= 3 &&
            (titleToken.StartsWith(queryToken, StringComparison.Ordinal) ||
             queryToken.StartsWith(titleToken, StringComparison.Ordinal)))
            return 2;
        if (titleToken.Length < 4 || queryToken.Length < 4) return 0;
        if (titleToken[0] != queryToken[0]) return 0;
        var max = titleToken.Length <= 4 || queryToken.Length <= 4 ? 1
            : Math.Max(titleToken.Length, queryToken.Length) <= 7 ? 1 : 2;
        return Damerau(titleToken, queryToken, max) <= max ? 1 : 0;
    }

    private static int Damerau(string left, string right, int max)
    {
        if (Math.Abs(left.Length - right.Length) > max) return max + 1;
        var previousPrevious = new int[right.Length + 1];
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1])
                    value = Math.Min(value, previousPrevious[j - 2] + 1);
                current[j] = value;
                rowMin = Math.Min(rowMin, value);
            }
            if (rowMin > max) return max + 1;
            Array.Copy(previous, previousPrevious, previous.Length);
            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
