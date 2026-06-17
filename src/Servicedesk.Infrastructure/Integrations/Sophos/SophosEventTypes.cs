namespace Servicedesk.Infrastructure.Integrations.Sophos;

/// Audit event types for the Sophos Central spam-filter connector (v0.0.78).
/// Mirrors the namespaced vocabulary used by the M365 / Telavox / Trmm
/// connectors so the audit-log UI can filter on a stable prefix. The
/// credential/gate events land in the hash-chained security trail via
/// IAuditLogger; the operational SyncTick lands in the integration_audit
/// trail via IIntegrationAuditLogger so it surfaces on the connector tile.
public static class SophosEventTypes
{
    public const string Integration = "sophos";

    public const string SecuritySecretUpdated    = "integration.sophos.secret.updated";
    public const string SecuritySecretDeleted    = "integration.sophos.secret.deleted";
    public const string SecurityEnabledChanged   = "integration.sophos.enabled.changed";
    public const string SecurityConfigUpdated    = "integration.sophos.config.updated";
    public const string SecurityConnectionTested = "integration.sophos.connection.tested";
    public const string SecurityTenantLinked     = "integration.sophos.tenant.linked";
    public const string SecurityTenantUnlinked   = "integration.sophos.tenant.unlinked";

    // Operational event for the background sync — integration_audit trail.
    public const string SyncTick = "integration.sophos.sync.tick";
}
