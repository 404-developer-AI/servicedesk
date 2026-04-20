namespace Servicedesk.Infrastructure.Auth.Microsoft;

/// Core OIDC flow for M365 login. Stateless — the endpoint layer owns
/// cookie-state + session-minting; this service only speaks to Azure AD
/// and the users table. That split keeps the HTTP plumbing out of the
/// unit tests.
public interface IMicrosoftAuthService
{
    /// Builds the fields the endpoint needs to redirect the browser to the
    /// Azure AD authorize endpoint. The caller persists
    /// <see cref="ChallengeIntent.State"/>, <see cref="ChallengeIntent.Nonce"/>,
    /// and <see cref="ChallengeIntent.CodeVerifier"/> in a short-lived
    /// signed cookie so the matching values can be replayed on callback.
    Task<ChallengeIntent> CreateChallengeAsync(string redirectUri, string? returnUrl, CancellationToken ct = default);

    /// Exchanges the authorization <paramref name="code"/> for an
    /// id-token, validates it against the tenant's JWKS, and resolves the
    /// token's <c>oid</c> claim to an <see cref="ApplicationUser"/>. All
    /// rejection paths return a typed result with no PII beyond what the
    /// caller will already audit-log. Throws only on network / config
    /// faults — never on "user rejected".
    Task<CallbackResult> ValidateCallbackAsync(
        string code,
        string redirectUri,
        ChallengeIntent intent,
        CancellationToken ct = default);
}

/// Everything the endpoint needs to emit the 302 and to correlate the
/// subsequent callback. The three secret fields live in a server-signed,
/// HTTP-only cookie that is deleted on callback — never in the URL.
public sealed record ChallengeIntent(
    string AuthorizeUrl,
    string State,
    string Nonce,
    string CodeVerifier,
    string? ReturnUrl);

/// Outcome of a callback validation. Exactly one of the Success /
/// Rejected variants is produced. No throwing — all "normal-sad-path"
/// outcomes route through here.
public abstract record CallbackResult
{
    public sealed record Success(ApplicationUser User, string Oid) : CallbackResult;

    public sealed record Rejected(
        CallbackRejectReason Reason,
        string? Oid,
        string? PreferredUsername,
        string? Detail) : CallbackResult;
}

/// Discrete rejection reasons — each maps to exactly one audit-event type
/// on the endpoint side (<c>AuthEventTypes.MicrosoftLoginRejected*</c>).
public enum CallbackRejectReason
{
    /// Token validation failed (signature, issuer, audience, expiration,
    /// nonce). This is always treated as hostile and logs with detail.
    InvalidToken = 1,

    /// The authorization code exchange returned a non-success from Azure
    /// AD (invalid_grant, invalid_client, etc.). Logged with the Azure
    /// AD error-code in the audit payload; the user sees a generic error.
    CodeExchangeFailed = 2,

    /// Token was valid, but the <c>oid</c> claim is not linked to any
    /// row in the users table. An admin must run "Add from M365" first.
    UnknownOid = 3,

    /// User row exists with a Customer role. Customers are out of scope
    /// for the M365 login — customer-portal login lands in v0.1.x.
    CustomerRole = 4,

    /// User row exists and is Agent/Admin, but the row is marked
    /// inactive locally (set by a previous <c>AccountDisabled</c>
    /// rejection or by an admin via the user-admin UI).
    Inactive = 5,

    /// Graph reports <c>accountEnabled=false</c> for this OID, or the OID
    /// is no longer reachable in the tenant. We mark the local row
    /// inactive as a side effect so the next attempt short-circuits at
    /// <see cref="Inactive"/> without another Graph round-trip.
    AccountDisabled = 6,
}
