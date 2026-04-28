namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Audit event-type strings for the Adsolut OAuth integration. Centralised
/// so endpoint, service and (future) Health/security-activity bucket all
/// agree on the wire-format names.
public static class AdsolutEventTypes
{
    /// Admin-initiated authorize step. No credentials in the payload — only
    /// environment + redirect_uri, both already in Settings.
    public const string ConnectStarted = "integration.adsolut.connect.started";

    /// Code exchange + persist succeeded. Payload carries the authorized
    /// subject + email so an admin can spot-check who linked the account.
    public const string ConnectSucceeded = "integration.adsolut.connect.succeeded";

    /// Callback rejected before tokens were minted. Payload carries the
    /// reject reason + sanitized detail (no codes, no secrets).
    public const string ConnectFailed = "integration.adsolut.connect.failed";

    /// Successful refresh — RT rotated, sliding window reset. Logged at
    /// info-level so a long-running connection produces a steady heartbeat
    /// in the audit trail.
    public const string TokenRefreshed = "integration.adsolut.token.refreshed";

    /// Refresh failed — non-success from the token endpoint or a parse
    /// failure on the response. Payload distinguishes transient (network)
    /// from terminal (invalid_grant) so an admin can decide between waiting
    /// and reconnecting.
    public const string TokenRefreshFailed = "integration.adsolut.token.refresh_failed";

    /// Admin-initiated disconnect. Wipes the refresh-token + metadata; the
    /// client_id / client_secret stay so a quick reconnect is one click.
    public const string Disconnected = "integration.adsolut.disconnected";

    /// Admin updated the client secret (PUT /api/admin/integrations/adsolut/secret).
    /// Mirrors the Graph-secret event; the actual value never leaves the
    /// secret store.
    public const string ClientSecretUpdated = "integration.adsolut.client_secret.updated";

    /// Admin cleared the client secret. Implies the connection is now
    /// inert until a new secret arrives.
    public const string ClientSecretDeleted = "integration.adsolut.client_secret.deleted";
}
