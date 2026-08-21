using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExoLauncher.Services;

internal sealed record ExoBridgeRequest(string Id, string Method, JsonElement Params, bool HasParams);

internal sealed record ExoAccountState
{
    public bool Ok { get; init; }
    public bool SignedIn { get; init; }
    public bool Configured { get; init; }
    public string[] Providers { get; init; } = [];
    public string? Message { get; init; }
    public string? Id { get; init; }
    public string? Handle { get; init; }
    public string? Email { get; init; }
    public string? Provider { get; init; }
    public string[] Roles { get; init; } = [];
    public bool CanManageBadges { get; init; }
    public List<ExoProfileBadge> Badges { get; init; } = [];
}

internal sealed record ExoBridgePresenceEntry
{
    public string? UserId { get; init; }
    public string Status { get; init; } = "unknown";
    public string? GameId { get; init; }
    public string? GameTitle { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public bool Available { get; init; }
}

internal sealed record ExoBridgePresenceEvent
{
    public string Kind { get; init; } = "transportError";
    public ExoBridgePresenceEntry? Presence { get; init; }
    public ExoOnlineError? Error { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
}

/// <summary>
/// Stable JSON-RPC wire schema shared by WebHostBridge and its contract tests.
/// It parses one request shape and emits exactly one result/event envelope shape.
/// </summary>
internal static class ExoBridgeProtocol
{
    internal const int MaxRequestCharacters = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static bool TryParseRequest(string? raw, out ExoBridgeRequest request)
    {
        request = new ExoBridgeRequest(string.Empty, string.Empty, default, false);
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxRequestCharacters)
            return false;

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
                return false;

            var id = idElement.GetString();
            var method = methodElement.GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(method) ||
                id.Length > 128 || method.Length > 128)
                return false;

            var hasParams = root.TryGetProperty("params", out var paramsElement);
            request = new ExoBridgeRequest(
                id,
                method,
                hasParams ? paramsElement.Clone() : default,
                hasParams);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string SerializeResponse(string id, bool ok, object? result, string? error)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["ok"] = ok,
        };
        if (ok)
            payload["result"] = result;
        else
            payload["error"] = error ?? "error";
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    internal static string SerializeEvent(string name, object? data) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["event"] = name,
                ["data"] = data,
            },
            JsonOptions);
}
