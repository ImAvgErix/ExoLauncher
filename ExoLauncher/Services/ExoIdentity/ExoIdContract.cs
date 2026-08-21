namespace ExoLauncher.Services;

/// <summary>
/// Client mirror of <c>services/exo-id/CONTRACT.md</c>. Nothing else should
/// hard-code identity URLs, lifetimes, or endpoint names. The server document
/// is the source of truth — if a test comparing these strings to that file
/// fails, this file is wrong.
/// </summary>
internal static class ExoIdContract
{
    // Release trust anchor. Environment overrides may select loopback for
    // development, but cannot replace this with another public HTTPS origin.
    public const string ProductionOrigin = "https://exo-id.exo-erix.workers.dev";
    public const string CallbackPath = "/callback";
    public const string OriginEnvironmentVariable = "EXO_ID_ORIGIN";
    public const string CodeChallengeMethod = "S256";
    public const int MaxJsonResponseBytes = 512 * 1024;
    public const int MaxPasswordRequestBytes = 2 * 1024;

    public const string HealthPath = "/v1/health";
    public const string AuthStartPath = "/v1/auth/start";
    public const string AuthContinuePrefix = "/v1/auth/continue";
    public const string AuthCompletePath = "/v1/auth/complete";
    public const string AuthTokenPath = "/v1/auth/token";
    public const string AuthSignOutPath = "/v1/auth/sign-out";
    public const string PasswordSignUpPath = "/api/auth/sign-up/email";
    public const string PasswordSignInPath = "/api/auth/sign-in/email";
    public const string BearerSessionHeader = "set-auth-token";
    public const string SessionsPath = "/v1/sessions";
    public const string SessionsRevokePath = "/v1/sessions/revoke";
    public const string SessionsRevokeAllPath = "/v1/sessions/revoke-all";
    public const string MePath = "/v1/me";
    public const string MeExportPath = "/v1/me/export";
    public const string HandlePath = "/v1/handle";
    public const string ProfilePath = "/v1/profile";
    public const string SyncPath = "/v1/sync";
    public const string FriendsPath = "/v1/friends";
    public const string ProfilesPrefix = "/v1/profiles";
    public const string ProfilesSearchPath = "/v1/profiles/search";
    public const string PublicProfileSharePrefix = "/p";
    public const string ProfilePrivacyPath = "/v1/profile/privacy";
    public const string FriendRequestsPath = "/v1/friends/requests";
    public const string BlocksPath = "/v1/blocks";
    public const string LinksPath = "/v1/links";
    public const string LinksDiscoveryPath = "/v1/links/discovery";
    public const string LinksSteamStartPath = "/v1/links/steam/start";
    public const string LinksEpicPath = "/v1/links/epic";
    public const string LinksGogPath = "/v1/links/gog";
    public const string LinksMatchPath = "/v1/links/match";
    public const string ProfileMediaPrefix = "/v1/profile/media";
    public const string MediaPrefix = "/v1/media";
    public const string PresencePath = "/v1/presence";
    public const string PresenceSocketPath = "/v1/presence/socket";
    public const string AdminBadgesPath = "/v1/admin/badges";

    public static string ProfileMediaPath(string kind) =>
        ProfileMediaPrefix + "/" + Uri.EscapeDataString(kind);

    public static string MediaPath(string userId, string kind, string version) =>
        MediaPrefix + "/" + Uri.EscapeDataString(userId) + "/" +
        Uri.EscapeDataString(kind) + "/" + Uri.EscapeDataString(version);

    public static readonly TimeSpan PendingLoginLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MagicLinkLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AuthCodeLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);

    public static readonly string[] DocumentedPaths =
    [
        HealthPath,
        AuthStartPath,
        AuthContinuePrefix,
        AuthCompletePath,
        AuthTokenPath,
        AuthSignOutPath,
        SessionsPath,
        SessionsRevokePath,
        SessionsRevokeAllPath,
        MePath,
        MeExportPath,
        HandlePath,
        ProfilePath,
        SyncPath,
        ProfilePrivacyPath,
        ProfilesPrefix,
        ProfilesSearchPath,
        FriendsPath,
        FriendRequestsPath,
        BlocksPath,
        LinksPath,
        LinksDiscoveryPath,
        LinksSteamStartPath,
        LinksEpicPath,
        LinksGogPath,
        LinksMatchPath,
        ProfileMediaPrefix,
        MediaPrefix,
        PresencePath,
        PresenceSocketPath,
        AdminBadgesPath,
    ];

    public static string? ResolveOrigin(string? explicitOrigin = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitOrigin))
            return NormalizeOrigin(explicitOrigin);
        var env = Environment.GetEnvironmentVariable(OriginEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
            return NormalizeOrigin(env);
        return string.IsNullOrWhiteSpace(ProductionOrigin) ? null : NormalizeOrigin(ProductionOrigin);
    }

    public static string Combine(string origin, string path)
    {
        var root = origin.TrimEnd('/');
        if (string.IsNullOrEmpty(path)) return root;
        return path[0] == '/' ? root + path : root + "/" + path;
    }

    public static string LoopbackRedirectUri(int port) =>
        $"http://127.0.0.1:{port}{CallbackPath}";

    public static Uri? ResolvePresenceSocketUri(string? explicitOrigin = null)
    {
        var origin = ResolveOrigin(explicitOrigin);
        if (string.IsNullOrEmpty(origin))
            return null;
        var root = new Uri(origin, UriKind.Absolute);
        var builder = new UriBuilder(root)
        {
            Scheme = root.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = PresenceSocketPath,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    public static bool IsTrustedContinueUrl(string origin, string url)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var root))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("https" or "http"))
            return false;
        if (!string.Equals(uri.Scheme, root.Scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(uri.Host, root.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        if (uri.Port != root.Port)
            return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        return path.StartsWith(AuthContinuePrefix + "/", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, AuthContinuePrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTrustedSteamAuthorizationUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.IdnHost, "steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;
        return string.Equals(uri.AbsolutePath.TrimEnd('/'), "/openid/login", StringComparison.Ordinal);
    }

    internal static bool IsAllowedOrigin(string? origin, string? pinnedProductionOrigin)
    {
        if (!TryParseOrigin(origin, out var candidate))
            return false;
        if (IsLoopbackHost(candidate.Host))
            return true;
        if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !TryParseOrigin(pinnedProductionOrigin, out var pinned) ||
            IsLoopbackHost(pinned.Host) ||
            !string.Equals(pinned.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(candidate.IdnHost, pinned.IdnHost, StringComparison.OrdinalIgnoreCase) &&
               candidate.Port == pinned.Port;
    }

    private static string NormalizeOrigin(string origin)
    {
        var trimmed = origin.Trim().TrimEnd('/');
        if (!TryParseOrigin(trimmed, out _))
            throw new InvalidOperationException("The identity origin is not a valid URL.");
        if (!IsAllowedOrigin(trimmed, ProductionOrigin))
            throw new InvalidOperationException("The identity origin is not trusted by this build.");
        return trimmed;
    }

    private static bool TryParseOrigin(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.AbsolutePath is not ("" or "/"))
            return false;
        uri = parsed;
        return true;
    }

    internal static bool IsLoopbackHost(string host)
    {
        var unwrapped = host.Trim('[', ']');
        return string.Equals(unwrapped, "localhost", StringComparison.OrdinalIgnoreCase) ||
               System.Net.IPAddress.TryParse(unwrapped, out var address) &&
               System.Net.IPAddress.IsLoopback(address);
    }
}
