namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// Integration + event-type constants for <c>integration_audit</c> rows
/// written by the Tactical RMM connector. Matches the convention used by
/// the Zammad and Telavox connectors so the audit-log UI can filter on a
/// stable, namespaced vocabulary.
public static class TrmmEventTypes
{
    public const string Integration = "trmm";

    // HTTP calls. One event-type per logical endpoint so an admin can spot
    // which leg of a sync failed at a glance. Sites are not a separate
    // call — TRMM embeds them on every /clients/ row, so the snapshot
    // ships under ClientsList.
    public const string ClientsList = "clients_list";
    public const string AgentsList  = "agents_list";
    public const string VersionGet  = "version_get";

    // Lifecycle events emitted by TrmmSyncWorker. Sync-tick rows are the
    // healthcheck row equivalents for this integration — one per cycle,
    // with the per-table counts on the payload.
    public const string SyncStarted   = "sync_started";
    public const string SyncCompleted = "sync_completed";
    public const string SyncFailed    = "sync_failed";

    // Admin actions. Configured covers a base-URL or API-key write; the
    // matching audit-log security row is in audit_log via IAuditLogger.
    public const string Configured        = "configured";
    public const string ConnectionTested  = "connection_tested";
    public const string ClientMappingSet  = "client_mapping_set";

    // Security audit_log event types (via IAuditLogger). Distinct from the
    // integration_audit entries above — these land in the hash-chained
    // security trail because they change a credential or a gate.
    public const string SecurityApiKeyUpdated  = "integration.trmm.api_key.updated";
    public const string SecurityApiKeyDeleted  = "integration.trmm.api_key.deleted";
    public const string SecurityBaseUrlUpdated = "integration.trmm.base_url.updated";
    public const string SecurityEnabledChanged = "integration.trmm.enabled.changed";
    public const string SecurityClientMappingSet = "integration.trmm.client_mapping.set";
    public const string SecuritySyncTriggered  = "integration.trmm.sync.triggered";
}
