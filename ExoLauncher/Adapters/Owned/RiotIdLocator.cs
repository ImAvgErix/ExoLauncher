using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Adapters;

/// <summary>
/// Reads the local Riot id_token payload for game_name and tag_line only.
/// The token is never logged or sent off this PC.
/// </summary>
internal static class RiotIdLocator
{
    private static readonly Regex Jwt = new(
        @"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
        RegexOptions.Compiled);

    internal static string? TryReadLocal()
    {
        var yaml = LocalStoreSessions.ReadTextIfExists(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Riot Games",
            "Riot Client",
            "Data",
            "RiotGamesPrivateSettings.yaml");
        return TryReadFromYaml(yaml);
    }

    internal static string? TryReadFromYaml(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        foreach (Match match in Jwt.Matches(yaml))
        {
            var id = TryReadFromJwt(match.Value);
            if (id is not null) return id;
        }

        return null;
    }

    internal static string? TryReadFromJwt(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Base64Url(parts[1]));
            using var document = JsonDocument.Parse(json);
            return FindRiotId(document.RootElement);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindRiotId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var name = FirstString(element, "game_name", "gameName");
            var tag = FirstString(element, "tag_line", "tagLine");
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(tag))
                return name.Trim() + "#" + tag.Trim();
            foreach (var property in element.EnumerateObject())
            {
                var nested = FindRiotId(property.Value);
                if (nested is not null) return nested;
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindRiotId(item);
                if (nested is not null) return nested;
            }
        }

        return null;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }

        return null;
    }

    private static byte[] Base64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }
}
