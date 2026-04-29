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

    // ---- integration_audit event types ---------------------------------
    //
    // The constants below land in integration_audit (operational log),
    // not audit_log (security trail). They carry latency + http_status
    // + upstream error_code so an admin can spot a slow or flapping
    // integration without correlating across two tables.

    /// Integration name written to <c>integration_audit.integration</c>.
    /// Centralised so the worker, endpoints and reader all agree.
    public const string Integration = "adsolut";

    /// Outbound HTTP call to the WK token endpoint to exchange an
    /// authorization code for tokens (server-side leg of the OAuth dance).
    /// Latency + http_status are populated; actor_id/role are populated
    /// from the intent-cookie that survived the cross-site redirect.
    public const string OAuthCodeExchange = "oauth.code_exchange";

    /// Outbound HTTP call to the WK token endpoint to rotate the refresh
    /// token. Source is one of: scheduled healthcheck tick, admin-clicked
    /// "Test refresh", or a future API-call path. Payload distinguishes
    /// the source so the table can be filtered.
    public const string OAuthRefresh = "oauth.refresh";

    /// One healthcheck tick. Always produces a row (even when skipped
    /// because the integration isn't configured), so admins can see the
    /// worker is alive. Outcome is the resolved status the SignalR push
    /// would carry — ok = healthy, warn = configured-but-not-connected
    /// or transient failure, error = invalid_grant / sliding window
    /// elapsed.
    public const string HealthcheckTick = "healthcheck.tick";

    // ---- v0.0.26 Companies pull ---------------------------------------

    /// GET /adm/v1/administrations — list dossiers the authorized user owns.
    public const string AdministrationsList = "administrations.list";

    /// POST /adm/v1/administrations/{id}/integrations — activate the
    /// integration on the chosen dossier. Without this every Accounting
    /// API call returns empty (per WK docs).
    public const string AdministrationsActivate = "administrations.activate";

    /// DELETE /adm/v1/administrations/{id}/integrations — fired on
    /// disconnect. Leaving the integration active has financial impact
    /// per WK docs, so this runs before tokens get wiped.
    public const string AdministrationsDeactivate = "administrations.deactivate";

    /// GET /acc/v1/customers — paginated list, optionally with
    /// ?ModifiedSince= for delta-sync. Each page is one audit row so a
    /// slow page is independently visible.
    public const string CustomersList = "customers.list";

    /// GET /acc/v1/suppliers — only used when the IncludeSuppliers toggle
    /// is on. Same pagination + delta-sync mechanics as customers.
    public const string SuppliersList = "suppliers.list";

    /// One sync-worker tick summary. Carries the totals (seen / upserted /
    /// skipped) + duration + outcome so admins can see whether the last
    /// tick did real work or was a noop.
    public const string SyncTick = "sync.tick";

    /// Admin-triggered raw-API probe from the Adsolut settings page debug
    /// card. Hits /customers or /suppliers with a Code= filter; the response
    /// body is shown back to the admin verbatim. Distinct from the regular
    /// CustomersList / SuppliersList ticks so audit filters can separate
    /// diagnostic noise from operational sync traffic.
    public const string DebugLookup = "debug.lookup";
}
