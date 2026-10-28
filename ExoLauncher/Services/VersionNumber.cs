namespace ExoLauncher.Services;

/// <summary>
/// Version numbers compared one way wherever Exo looks for a newer version (graphics and chipset drivers, Exo itself, the
/// version watch): the dotted numbers at the start, as many as given, a missing part read as 0 (26.10 is 26.10.0, not
/// older than it, as System.Version has it), leading zeros not counting (7.06 is 7.6), a leading "v" and anything after
/// the numbers ("-beta", "+build", " WHQL") left out. Text that doesn't start with a number is unreadable, and sorts
/// below every version. Three keep rules of their own on purpose: Lucid's mirrors Lucid's own updater, Sony's firmware
/// numbers are hex, and DLSS Swapper orders DLL file versions.
/// </summary>
internal static class VersionNumber
{
    /// <summary>The numbers, or null when the text doesn't start with one.</summary>
    internal static long[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.AsSpan().Trim();
        if (s.Length > 0 && s[0] is 'v' or 'V') s = s[1..];
        var parts = new List<long>();
        long current = 0;
        var digits = 0;
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c))
            {
                if (++digits > 18) return null;
                current = current * 10 + (c - '0');
            }
            else if (c == '.' && digits > 0)
            {
                parts.Add(current);
                current = 0;
                digits = 0;
            }
            else break;
        }
        if (digits > 0) parts.Add(current);
        return parts.Count > 0 ? [.. parts] : null;
    }

    /// <summary>Below zero when <paramref name="a"/> is older, zero when the same version, above zero when newer.</summary>
    internal static int Compare(string? a, string? b)
    {
        var x = Parse(a);
        var y = Parse(b);
        if (x is null || y is null) return (x is null ? 0 : 1) - (y is null ? 0 : 1);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var p = i < x.Length ? x[i] : 0;
            var q = i < y.Length ? y[i] : 0;
            if (p != q) return p.CompareTo(q);
        }
        return 0;
    }

    /// <summary>Whether <paramref name="latest"/> is newer than <paramref name="installed"/>; with nothing readable installed, any readable latest is.</summary>
    internal static bool Newer(string? latest, string? installed) =>
        Parse(latest) is not null && (Parse(installed) is null || Compare(latest, installed) > 0);

    /// <summary>Both readable and <paramref name="newest"/> is newer: an update is out for what's installed.</summary>
    internal static bool Behind(string? installed, string? newest) =>
        Parse(installed) is not null && Parse(newest) is not null && Compare(newest, installed) > 0;

    /// <summary>Both readable and the same version (26.10 and 26.10.0 are).</summary>
    internal static bool Same(string? a, string? b) => Parse(a) is not null && Parse(b) is not null && Compare(a, b) == 0;

    /// <summary>Both readable and <paramref name="have"/> is <paramref name="want"/> or newer (an install took).</summary>
    internal static bool AtLeast(string? have, string? want) => Parse(have) is not null && Parse(want) is not null && Compare(have, want) >= 0;

    internal static Comparer<string> Order { get; } = Comparer<string>.Create(Compare);
}
