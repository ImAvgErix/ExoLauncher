using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Discord's in-app reports: the questions Discord asks for each kind of report come from Discord (a tree of steps),
/// and the report goes back as Discord's apps send it: the kind, the menu's version and variant, the steps taken
/// (breadcrumbs), the answers (elements) and the ids of what's reported. Only Discord's kinds and the ids each needs.
/// </summary>
internal static class DiscordReports
{
    /// <summary>Each kind Exo reports and the ids Discord wants with it.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> Kinds = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["message"] = ["channel_id", "message_id"],
        ["first_dm"] = ["channel_id", "message_id"],
        ["user"] = ["reported_user_id"],
        ["guild"] = ["guild_id"],
        ["guild_scheduled_event"] = ["guild_id", "guild_scheduled_event_id"],
        ["application"] = ["application_id"],
    };

    private const int MostSteps = 32, MostAnswers = 32, LongestAnswer = 4000;

    /// <summary>The report Discord's apps send, or null when a part is missing or isn't Discord's shape.</summary>
    internal static JsonObject? Build(string kind, JsonElement p)
    {
        if (!Kinds.TryGetValue(kind, out var needs) || p.ValueKind != JsonValueKind.Object) return null;
        if (Text(p, "version") is not { Length: > 0 and <= 16 } version || Text(p, "variant") is not { Length: > 0 and <= 256 } variant) return null;
        var language = Text(p, "language") is { Length: > 0 and <= 16 } l && l.All(c => char.IsAsciiLetter(c) || c == '-') ? l : "en";
        if (!p.TryGetProperty("breadcrumbs", out var path) || path.ValueKind != JsonValueKind.Array) return null;
        var steps = new JsonArray();
        foreach (var step in path.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Number || !step.TryGetInt64(out var node) || node < 0 || steps.Count >= MostSteps) return null;
            steps.Add(node);
        }
        if (steps.Count == 0) return null;
        var elements = new JsonObject();
        if (p.TryGetProperty("elements", out var answers) && answers.ValueKind == JsonValueKind.Object)
        {
            foreach (var answer in answers.EnumerateObject())
            {
                if (answer.Name.Length is 0 or > 64 || answer.Value.ValueKind != JsonValueKind.Array || elements.Count >= MostAnswers) return null;
                var values = new JsonArray();
                foreach (var value in answer.Value.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= LongestAnswer } said || values.Count >= MostAnswers) return null;
                    values.Add(said);
                }
                elements[answer.Name] = values;
            }
        }
        var report = new JsonObject { ["version"] = version, ["variant"] = variant, ["name"] = kind, ["language"] = language, ["breadcrumbs"] = steps, ["elements"] = elements };
        if (!p.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Object) return null;
        foreach (var need in needs)
        {
            if (Text(ids, need) is not { Length: > 0 and <= 20 } id || !id.All(char.IsAsciiDigit)) return null;
            report[need] = id;
        }
        return report;
    }

    private static string? Text(JsonElement p, string name) =>
        p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
