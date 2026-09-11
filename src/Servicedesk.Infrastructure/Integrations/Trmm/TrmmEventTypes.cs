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
    // v0.1.10 — per-agent checks read by the Remote Desktop sync. Only
    // failures are audited per call (success would be one row per server
    // per cycle); the cycle itself lands as RdsSync* rows below.
    public const string AgentChecksList = "agent_checks_list";

    // Lifecycle events emitted by TrmmSyncWorker. Sync-tick rows are the
    // healthcheck row equivalents for this integration — one per cycle,
    // with the per-table counts on the payload.
    public const string SyncStarted   = "sync_started";
    public const string SyncCompleted = "sync_completed";
    public const string SyncFailed    = "sync_failed";

    // v0.1.10 — Remote Desktop (RDS) check sync. Separate cadence from the
    // agent mirror; one started + one completed/failed row per cycle with
    // the per-status counts on the payload.
    public const string RdsSyncStarted   = "rds_sync_started";
    public const string RdsSyncCompleted = "rds_sync_completed";
    public const string RdsSyncFailed    = "rds_sync_failed";

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
    public const string SecurityRdsSyncTriggered = "integration.trmm.rds_sync.triggered";
    public const string SecurityRdsSettingsUpdated = "integration.trmm.rds_settings.updated";
    // Client notes (v0.1.10) are agent-level content, not a credential or
    // gate — only the admin override paths (editing / deleting someone
    // else's note) land in the security trail.
    public const string SecurityClientNoteOverridden = "assets.remote_desktop.note.overridden";
}
