namespace Servicedesk.Infrastructure.Integrations.TdSynnex;

/// Audit vocabulary for the TD SYNNEX connector + Order Management (v0.1.59).
/// Credential/config/link events go to the hash-chained audit_log
/// (IAuditLogger); the per-tick sync summary goes to integration_audit so it
/// shows on the connector card.
public static class TdSynnexEventTypes
{
    public const string Integration = "tdsynnex";

    public const string SecuritySecretUpdated    = "integration.tdsynnex.secret.updated";
    public const string SecuritySecretDeleted    = "integration.tdsynnex.secret.deleted";
    public const string SecurityEnabledChanged   = "integration.tdsynnex.enabled.changed";
    public const string SecurityConfigUpdated    = "integration.tdsynnex.config.updated";
    public const string SecurityConnectionTested = "integration.tdsynnex.connection.tested";

    public const string TicketOrderLinked   = "ticket.tdsynnex_order.linked";
    public const string TicketOrderUnlinked = "ticket.tdsynnex_order.unlinked";

    public const string SyncTick = "integration.tdsynnex.sync.tick";
}
