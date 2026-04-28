namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Wolters Kluwer login environment. UAT routes through `login-stg.*`,
/// production through `login.*`. Tokens minted in one are not accepted by
/// the other — a switch requires a disconnect + reconnect.
public enum AdsolutEnvironment
{
    Production = 0,
    Uat = 1,
}

/// Resolves the authorize/token endpoints for the configured environment.
/// Pure functions, no I/O — kept separate from the auth-service so unit
/// tests can pin the exact URL strings without a DI container.
public static class AdsolutOAuthEndpoints
{
    public const string ProductionAuthorize = "https://login.wolterskluwer.eu/auth/core/connect/authorize";
    public const string ProductionToken = "https://login.wolterskluwer.eu/auth/core/connect/token";
    public const string UatAuthorize = "https://login-stg.wolterskluwer.eu/auth/core/connect/authorize";
    public const string UatToken = "https://login-stg.wolterskluwer.eu/auth/core/connect/token";

    public static string Authorize(AdsolutEnvironment env) => env == AdsolutEnvironment.Uat
        ? UatAuthorize
        : ProductionAuthorize;

    public static string Token(AdsolutEnvironment env) => env == AdsolutEnvironment.Uat
        ? UatToken
        : ProductionToken;

    /// `uat` (case-insensitive) → UAT; anything else falls back to
    /// production. The fallback is loud-by-log at the call site so a typo
    /// is not silently routed at the wrong IdP.
    public static AdsolutEnvironment Parse(string? raw) =>
        string.Equals(raw?.Trim(), "uat", StringComparison.OrdinalIgnoreCase)
            ? AdsolutEnvironment.Uat
            : AdsolutEnvironment.Production;

    public static string ToWireValue(AdsolutEnvironment env) => env switch
    {
        AdsolutEnvironment.Uat => "uat",
        _ => "production",
    };
}

/// Singleton row in `adsolut_connection`. Non-secret bookkeeping the UI
/// surfaces (who authorized, when, when last refreshed). The actual
/// `client_secret` and `refresh_token` live in `protected_secrets` — this
/// record is safe to send to a privileged client; secrets never leave the
/// server.
public sealed record AdsolutConnection(
    string? AuthorizedSubject,
    string? AuthorizedEmail,
    DateTime? AuthorizedUtc,
    DateTime? LastRefreshedUtc,
    DateTime? AccessTokenExpiresUtc,
    string? LastRefreshError,
    DateTime? LastRefreshErrorUtc,
    DateTime UpdatedUtc);

/// Output of <c>CreateChallengeAsync</c>. The endpoint puts <c>State</c>
/// in a short-lived encrypted cookie and 302s to <c>AuthorizeUrl</c>; on
/// callback it replays the cookie value against the query-string state to
/// catch CSRF before reaching the token-exchange step.
public sealed record AdsolutChallengeIntent(string AuthorizeUrl, string State);

/// Outcome of the callback step. Success means the code was successfully
/// exchanged AND the resulting refresh token + metadata persisted. Rejected
/// covers every recoverable failure mode and carries enough detail for the
/// audit log without leaking secrets to the client.
public abstract record AdsolutCallbackResult
{
    public sealed record Success(string AuthorizedSubject, string? AuthorizedEmail) : AdsolutCallbackResult;

    public sealed record Rejected(AdsolutCallbackRejectReason Reason, string? Detail) : AdsolutCallbackResult;
}

public enum AdsolutCallbackRejectReason
{
    /// Client credentials missing — admin must fill them in before connecting.
    NotConfigured = 1,

    /// Code-for-token exchange returned a non-success from Wolters Kluwer.
    /// Detail carries the upstream error code (e.g. `invalid_grant`).
    CodeExchangeFailed = 2,

    /// Token endpoint returned a 200 but the body did not parse as a valid
    /// token response, or a required field (refresh_token) was missing.
    InvalidTokenResponse = 3,

    /// id_token arrived but could not be decoded / had no `sub` claim. We
    /// still persist the refresh token; the connection works, the UI just
    /// shows "Connected (unknown subject)" until next refresh.
    InvalidIdToken = 4,
}

/// Output of <c>RefreshAccessTokenAsync</c>. Access token is in-memory only —
/// callers must hold it for the lifetime of their request. Persisted state
/// (refresh_token, sliding-window metadata) is rotated as a side effect.
public sealed record AdsolutRefreshResult(string AccessToken, DateTime ExpiresUtc);

/// Reason a refresh attempt failed. Reconnect-required maps to a UI nudge;
/// transient errors keep the existing connection visible with a warning.
public sealed class AdsolutRefreshException : Exception
{
    public bool RequiresReconnect { get; }
    public string? UpstreamErrorCode { get; }

    public AdsolutRefreshException(string message, bool requiresReconnect, string? upstreamErrorCode = null)
        : base(message)
    {
        RequiresReconnect = requiresReconnect;
        UpstreamErrorCode = upstreamErrorCode;
    }
}
