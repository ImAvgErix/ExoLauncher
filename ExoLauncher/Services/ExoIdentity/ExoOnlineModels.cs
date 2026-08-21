using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExoLauncher.Services;

internal static class ExoOnlineSources
{
    public const string Live = "live";
    public const string Cache = "cache";
    public const string Unavailable = "unavailable";
}

internal sealed record ExoOnlineError(string Code, string Message);

internal sealed record ExoOnlineDiagnostics(
    bool Configured,
    bool? SignedIn,
    string Source,
    DateTimeOffset? LastSuccessfulSync,
    bool Retryable,
    ExoOnlineError? Error);

/// <summary>
/// Result returned by every optional online operation. A cache fallback is a
/// successful read with diagnostics describing the failed live refresh. It
/// never contains a bearer token, native upload path, or server response body.
/// Writes are deliberately not queued; callers retry an explicit write with
/// its original field timestamps if they choose to persist an outbox later.
/// </summary>
internal sealed record ExoOnlineResult<T>(
    bool Ok,
    T? Value,
    ExoOnlineDiagnostics Diagnostics,
    bool Queued = false);

internal sealed record ExoHandleSummary
{
    public string Display { get; init; } = "";
    public string Normalized { get; init; } = "";
    public DateTimeOffset? ClaimedAt { get; init; }
    public DateTimeOffset? ChangedAt { get; init; }
}

internal sealed record ExoFriend
{
    public string UserId { get; init; } = "";
    public ExoHandleSummary? Handle { get; init; }
    public List<string> Sources { get; init; } = [];
    public DateTimeOffset? ConnectedAt { get; init; }
    public ExoProfileMediaMetadata? Avatar { get; init; }
}

internal sealed record ExoFriendPage
{
    public List<ExoFriend> Friends { get; init; } = [];
    public string? NextCursor { get; init; }
}

internal sealed record ExoPublicProfile
{
    public string UserId { get; init; } = "";
    public ExoHandleSummary? Handle { get; init; }
    public Dictionary<string, JsonElement> Profile { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, ExoProfileMediaMetadata?> Media { get; init; } = new(StringComparer.Ordinal);
    public List<ExoProfileBadge> Badges { get; init; } = [];
}

internal static class ExoBadgeCatalog
{
    public static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    {
        "owner", "admin", "developer",
    };

    public static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "founder", "developer", "moderator", "contributor", "early_supporter",
    };

    public static readonly HashSet<string> ManageableKeys = new(StringComparer.Ordinal)
    {
        "developer", "moderator", "contributor", "early_supporter",
    };

    public static bool ValidateRoleSet(IEnumerable<string>? roles)
    {
        if (roles is null) return false;
        var values = roles.ToArray();
        return values.Length <= Roles.Count &&
               values.All(Roles.Contains) &&
               values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    }

    public static bool ValidateBadge(ExoProfileBadge? badge)
    {
        if (badge is null || !Keys.Contains(badge.Key))
            return false;
        if (!BoundedText(badge.Label, 40) || !BoundedText(badge.Description, 120))
            return false;
        return badge.Key switch
        {
            "founder" => Exact(badge, "Founder", "Founder of Exo", "founder"),
            "developer" => Exact(badge, "Developer", "Builds Exo", "staff"),
            "moderator" => Exact(badge, "Moderator", "Helps keep Exo welcoming", "staff"),
            "contributor" => Exact(badge, "Contributor", "Contributed to Exo", "community"),
            "early_supporter" => Exact(badge, "Early Supporter", "Supported Exo early", "supporter"),
            _ => false,
        };
    }

    public static bool SanitizeBadgeSet(List<ExoProfileBadge>? badges)
    {
        if (badges is null) return false;
        if (badges.Count > 32) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = badges.Count - 1; index >= 0; index--)
        {
            var badge = badges[index];
            if (!Keys.Contains(badge.Key))
            {
                // A newer server may add visual badges before this launcher is
                // updated. Unknown keys never cross the native boundary; known
                // keys still have to match the exact fixed projection below.
                badges.RemoveAt(index);
                continue;
            }
            if (!ValidateBadge(badge) || !seen.Add(badge.Key))
                return false;
        }
        return true;
    }

    private static bool BoundedText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool Exact(
        ExoProfileBadge badge,
        string label,
        string description,
        string tone) =>
        string.Equals(badge.Label, label, StringComparison.Ordinal) &&
        string.Equals(badge.Description, description, StringComparison.Ordinal) &&
        string.Equals(badge.Tone, tone, StringComparison.Ordinal);
}

internal sealed record ExoProfileBadge
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public string Tone { get; init; } = "";
    public DateTimeOffset? GrantedAt { get; init; }
}

internal sealed record ExoAdminBadgeState
{
    public ExoHandleSummary Handle { get; init; } = new();
    public List<ExoProfileBadge> Badges { get; init; } = [];
}

internal sealed record ExoPublicProfilePage
{
    public List<ExoPublicProfile> Profiles { get; init; } = [];
    public string? NextCursor { get; init; }
}

internal sealed record ExoProfilePrivacy
{
    public string ProfileVisibility { get; init; } = "friends";
    public bool Searchable { get; init; }
    public string RequestPolicy { get; init; } = "anyone";
    public string ActivityVisibility { get; init; } = "friends";
    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed record ExoProfilePrivacyEnvelope
{
    public ExoProfilePrivacy Privacy { get; init; } = new();
}

internal sealed record ExoFriendRequestUser
{
    public string UserId { get; init; } = "";
    public ExoHandleSummary? Handle { get; init; }
}

internal sealed record ExoFriendRequest
{
    public string Id { get; init; } = "";
    public string Direction { get; init; } = "";
    public ExoFriendRequestUser User { get; init; } = new();
    public string Status { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed record ExoFriendRequestEnvelope
{
    public ExoFriendRequest Request { get; init; } = new();
}

internal sealed record ExoFriendRequestPage
{
    public List<ExoFriendRequest> Incoming { get; init; } = [];
    public List<ExoFriendRequest> Outgoing { get; init; } = [];
    public string? NextIncomingCursor { get; init; }
    public string? NextOutgoingCursor { get; init; }
}

internal sealed record ExoBlock
{
    public string UserId { get; init; } = "";
    public ExoHandleSummary? Handle { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
}

internal sealed record ExoBlockEnvelope
{
    public ExoBlock Block { get; init; } = new();
}

internal sealed record ExoBlockPage
{
    public List<ExoBlock> Blocks { get; init; } = [];
    public string? NextCursor { get; init; }
}

internal sealed record ExoMutationAck
{
    public bool Ok { get; init; }
}

internal sealed record ExoDiscovery
{
    public bool Enabled { get; init; } = true;
    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed record ExoVerifiedStoreLink
{
    public string Store { get; init; } = "";
    public string ExternalId { get; init; } = "";
    public bool Verified { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
}

internal sealed record ExoStoreLinkEnvelope
{
    public ExoVerifiedStoreLink Link { get; init; } = new();
}

internal sealed record ExoConnection
{
    public string UserId { get; init; } = "";
    public ExoHandleSummary? Handle { get; init; }
    public string Store { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
}

internal sealed record ExoLinkState
{
    public ExoDiscovery Discovery { get; init; } = new();
    public List<ExoVerifiedStoreLink> Links { get; init; } = [];
    public List<ExoConnection> Connections { get; init; } = [];
}

internal sealed record ExoDiscoveryEnvelope
{
    public ExoDiscovery Discovery { get; init; } = new();
}

internal sealed record ExoMatchEnvelope
{
    public List<ExoConnection> Matches { get; init; } = [];
}

internal enum ExoLinkedStore
{
    Steam,
    Epic,
    Gog,
}

internal enum ExoStoreRelationship
{
    Mutual,
    OneSided,
}

/// <summary>
/// Native-only adapter for the short-lived token already held by Legendary or
/// gogdl. The React bridge must never implement or receive this interface.
/// </summary>
internal interface IExoStoreTokenSource
{
    ValueTask<string?> GetAccessTokenAsync(ExoLinkedStore store, CancellationToken cancellationToken);
}

internal sealed record ExoSteamLinkStart
{
    public string LinkId { get; init; } = "";
    public int ExpiresIn { get; init; }
    public string AuthorizationUrl { get; init; } = "";
}

internal sealed record ExoSessionInfo
{
    public string Id { get; init; } = "";
    public bool Current { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? UserAgent { get; set; }
}

internal sealed record ExoSessionPage
{
    public List<ExoSessionInfo> Sessions { get; init; } = [];
}

internal sealed record ExoExportAccount
{
    public string Id { get; init; } = "";
    public string? Name { get; set; }
    public string? Email { get; set; }
    public bool EmailVerified { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public List<string> Providers { get; init; } = [];
}

internal sealed record ExoAccountExport
{
    public DateTimeOffset? ExportedAt { get; init; }
    public ExoExportAccount Account { get; init; } = new();
    public ExoHandleSummary? Handle { get; init; }
    public List<string> Roles { get; init; } = [];
    public List<ExoProfileBadge> Badges { get; init; } = [];
    public Dictionary<string, JsonElement> Profile { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonElement> Preferences { get; init; } = new(StringComparer.Ordinal);
    public ExoProfilePrivacy Privacy { get; init; } = new();
    public Dictionary<string, ExoProfileMediaMetadata?> Media { get; init; } = new(StringComparer.Ordinal);
    public List<ExoSessionInfo> Sessions { get; init; } = [];
    public ExoDiscovery Discovery { get; init; } = new();
    public List<ExoVerifiedStoreLink> Links { get; init; } = [];
    public List<ExoConnection> Connections { get; init; } = [];
    public List<ExoExportDirectFriend> DirectFriends { get; init; } = [];
    public List<ExoExportFriendRequest> FriendRequests { get; init; } = [];
    public List<ExoExportBlock> Blocks { get; init; } = [];
    public List<ExoExportSuppression> Suppressions { get; init; } = [];
    public ExoExportPresence? Presence { get; init; }
}

internal sealed record ExoExportDirectFriend
{
    [JsonPropertyName("user_id")]
    public string UserId { get; init; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }
}

internal sealed record ExoExportFriendRequest
{
    public string Id { get; init; } = "";

    [JsonPropertyName("sender_id")]
    public string SenderId { get; init; } = "";

    [JsonPropertyName("recipient_id")]
    public string RecipientId { get; init; } = "";

    public string Status { get; init; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed record ExoExportBlock
{
    [JsonPropertyName("user_id")]
    public string UserId { get; init; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }
}

internal sealed record ExoExportSuppression
{
    [JsonPropertyName("user_id")]
    public string UserId { get; init; } = "";

    public string Reason { get; init; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }
}

internal sealed record ExoExportPresence
{
    public string UserId { get; init; } = "";
    public string Status { get; init; } = "unknown";
    public string? GameId { get; init; }
    public string? GameTitle { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public long Revision { get; init; }
}

internal sealed record ExoAccountDeleteResult
{
    public bool Ok { get; init; }
    public DateTimeOffset? HandleHeldUntil { get; init; }
}

internal sealed record ExoProfileMediaMetadata
{
    public string Kind { get; init; } = "";
    public string Version { get; init; } = "";
    public string Url { get; init; } = "";
    public string ContentType { get; init; } = "";
    public long Size { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Sha256 { get; init; } = "";
    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed record ExoProfileMediaEnvelope
{
    public ExoProfileMediaMetadata Media { get; init; } = new();
}

internal sealed record ExoPresenceWireEntry
{
    public string UserId { get; init; } = "";
    public string Status { get; init; } = "unknown";
    public string? GameId { get; init; }
    public string? GameTitle { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public string Availability { get; init; } = "unavailable";
}

internal sealed record ExoPresenceWireRoster
{
    public List<ExoPresenceWireEntry> Friends { get; init; } = [];
    public bool Unavailable { get; init; }
}

internal sealed record ExoProviderCapabilities
{
    public bool Google { get; init; }
    public bool Email { get; init; }
    public bool Password { get; init; }
}

internal sealed record ExoOnlineCapabilities
{
    public ExoProviderCapabilities Providers { get; init; } = new();
    public bool Profiles { get; init; }
    public bool Friends { get; init; }
    public bool Media { get; init; }
    public bool Presence { get; init; }
}

internal sealed record ExoOnlineHealth
{
    public bool Ok { get; init; }
    public string Service { get; init; } = "";
    public ExoOnlineCapabilities Capabilities { get; init; } = new();
}
