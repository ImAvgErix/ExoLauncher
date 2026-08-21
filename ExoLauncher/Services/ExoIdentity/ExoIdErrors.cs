namespace ExoLauncher.Services;

/// <summary>
/// User-facing copy for every code in <c>services/exo-id/CONTRACT.md</c>.
/// Prefer the mapped sentence over the raw server body.
/// </summary>
internal static class ExoIdErrors
{
    internal static readonly string[] Catalog =
    [
        "UNAUTHENTICATED",
        "RATE_LIMITED",
        "INVALID_REQUEST",
        "INVALID_REDIRECT_URI",
        "INVALID_PKCE",
        "INVALID_PROVIDER",
        "INVALID_GRANT",
        "LOGIN_EXPIRED",
        "GOOGLE_NOT_CONFIGURED",
        "EMAIL_NOT_CONFIGURED",
        "ACCOUNT_CONFLICT",
        "INVALID_CREDENTIALS",
        "INVALID_PASSWORD",
        "HANDLE_INVALID",
        "HANDLE_RESERVED",
        "HANDLE_TAKEN",
        "HANDLE_CONFUSABLE",
        "HANDLE_COOLDOWN",
        "HANDLE_REQUIRED",
        "SYNC_DENIED_KEY",
        "LINK_UNVERIFIED",
        "LINK_TAKEN",
        "LINK_INVALID",
        "LINK_VERIFY_FAILED",
        "LINK_STORE_UNSUPPORTED",
        "MATCH_TOO_LARGE",
        "REAUTHENTICATION_REQUIRED",
        "NOT_FOUND",
        "INTERNAL",
    ];

    public static string? UserMessage(string? code) => code?.Trim() switch
    {
        "UNAUTHENTICATED" => "You are signed out.",
        "RATE_LIMITED" => RateLimited(null),
        "INVALID_REQUEST" => "That request was not valid.",
        "INVALID_REDIRECT_URI" => "Sign-in could not start. Try again.",
        "INVALID_PKCE" => "Sign-in could not be verified. Try again.",
        "INVALID_PROVIDER" => "Unknown sign-in provider.",
        "INVALID_GRANT" => "Sign-in expired. Try again.",
        "LOGIN_EXPIRED" => "Sign-in expired. Try again.",
        "GOOGLE_NOT_CONFIGURED" => "Google sign-in is not set up.",
        "EMAIL_NOT_CONFIGURED" => "Email sign-in is not set up.",
        "ACCOUNT_CONFLICT" => "The account request could not be completed.",
        "INVALID_CREDENTIALS" => "The email or password is incorrect.",
        "INVALID_PASSWORD" => "Password must be 12 to 128 characters.",
        "HANDLE_INVALID" => ExoHandle.RuleMessage,
        "HANDLE_RESERVED" => "That handle is reserved.",
        "HANDLE_TAKEN" => "That handle is taken.",
        "HANDLE_CONFUSABLE" => "That handle is too close to one that is taken.",
        "HANDLE_COOLDOWN" => "Handle can change once every 30 days.",
        "HANDLE_REQUIRED" => "Reserve a handle first.",
        "SYNC_DENIED_KEY" => "That setting does not sync.",
        "LINK_UNVERIFIED" => "Verify that store account before matching friends.",
        "LINK_TAKEN" => "That store account is already linked.",
        "LINK_INVALID" => "That store account link was not valid.",
        "LINK_VERIFY_FAILED" => "The store could not verify that account.",
        "LINK_STORE_UNSUPPORTED" => "That store cannot be linked yet.",
        "MATCH_TOO_LARGE" => "Send at most 200 store friends at a time.",
        "REAUTHENTICATION_REQUIRED" => "Sign in again before deleting the account.",
        "NOT_FOUND" => "Not found.",
        "INTERNAL" => "The identity service could not complete that request.",
        _ => null,
    };

    public static string RateLimited(int? retryAfterSec) =>
        retryAfterSec is > 0
            ? $"Too many attempts. Try again in {retryAfterSec.Value} seconds."
            : "Too many attempts. Try again later.";
}
