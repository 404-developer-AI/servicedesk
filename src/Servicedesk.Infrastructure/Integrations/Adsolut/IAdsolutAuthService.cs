namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// OAuth2 Authorization Code dance against Wolters Kluwer login (used for
/// the Adsolut API). One install ↔ one administration; refresh-token is
/// stored install-wide and refreshed on demand.
///
/// Stateless w.r.t. HTTP — the endpoint layer owns cookies and audit-event
/// emission; this service only speaks to the IdP and the connection store.
public interface IAdsolutAuthService
{
    /// Builds the authorize-redirect URL + a server-side `state`. The
    /// caller is expected to persist <see cref="AdsolutChallengeIntent.State"/>
    /// in a short-lived signed cookie and replay it on the callback.
    /// Throws when the install is not configured (no client_id/secret).
    Task<AdsolutChallengeIntent> CreateChallengeAsync(string redirectUri, CancellationToken ct = default);

    /// Exchanges <paramref name="code"/> for tokens, decodes the id_token
    /// to extract the authorizing subject, and persists the refresh_token +
    /// connection metadata. Returns a typed outcome — never throws on a
    /// "user-cancelled" or "Wolters Kluwer rejected the code" path. The
    /// <paramref name="actorId"/>/<paramref name="actorRole"/> are read from
    /// the intent cookie by the callback endpoint and threaded through so
    /// the integration_audit row records who initiated the connect even
    /// though the cross-site redirect dropped the session cookie.
    Task<AdsolutCallbackResult> CompleteCallbackAsync(
        string code,
        string redirectUri,
        string? actorId = null,
        string? actorRole = null,
        CancellationToken ct = default);

    /// Uses the stored refresh token to mint a fresh access token. Rotates
    /// the refresh token (Adsolut sliding-1-month window) and updates
    /// <c>last_refreshed_utc</c>. Throws <see cref="AdsolutRefreshException"/>
    /// when the upstream rejects the refresh — callers decide whether to
    /// surface a "reconnect required" UI based on
    /// <see cref="AdsolutRefreshException.RequiresReconnect"/>. The
    /// <paramref name="source"/> tag (e.g. <c>admin_test</c>,
    /// <c>healthcheck</c>) is recorded in the integration_audit row so
    /// admins can filter scheduled noise from one-off admin clicks.
    Task<AdsolutRefreshResult> RefreshAccessTokenAsync(
        string source = "service",
        string? actorId = null,
        string? actorRole = null,
        CancellationToken ct = default);

    /// Wipes refresh_token + adsolut_connection. Client_id / client_secret
    /// remain in their stores so an admin can reconnect without
    /// re-pasting credentials.
    Task DisconnectAsync(CancellationToken ct = default);
}
