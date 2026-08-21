using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// Courtesy handle rules matching <c>services/exo-id/CONTRACT.md</c>.
/// The server is the uniqueness gate; this only refuses values that cannot
/// succeed. Display casing is kept. Uniqueness is lowercase on the server.
/// </summary>
internal static class ExoHandle
{
    public const int MinLength = 3;
    public const int MaxLength = 24;
    public const string RuleMessage =
        "Use 3–24 letters, digits, or underscore, with at least one letter.";

    private static readonly Regex Pattern = new(
        "^[A-Za-z0-9_]{3,24}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "exo", "official", "admin", "support", "help",
        "steam", "epic", "gog", "riot", "system",
    };

    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public static bool TryValidate(string? value, out string handle, out string message)
    {
        handle = (value ?? string.Empty).Trim();
        if (handle.Length == 0)
        {
            message = "A handle is required.";
            return false;
        }

        foreach (var ch in handle)
        {
            if (ch > 127)
            {
                message = "Handle must be ASCII letters, digits, or underscore. Lookalike characters are refused.";
                return false;
            }
        }

        if (!Pattern.IsMatch(handle) || !HasLetter(handle))
        {
            message = RuleMessage;
            return false;
        }

        if (Reserved.Contains(Normalize(handle)))
        {
            message = "That handle is reserved.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool HasLetter(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsAsciiLetter(ch))
                return true;
        }

        return false;
    }
}
