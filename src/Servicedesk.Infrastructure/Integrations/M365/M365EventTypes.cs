namespace Servicedesk.Infrastructure.Integrations.M365;

/// Security audit_log event types for the Microsoft 365 customer-tenant
/// reader (multi-tenant app, v0.0.77). Mirrors the namespaced vocabulary used
/// by the Telavox / Trmm / Zammad connectors so the audit-log UI can filter on
/// a stable prefix. These land in the hash-chained security trail via
/// IAuditLogger because they change a credential or a gate.
public static class M365EventTypes
{
    public const string Integration = "m365";

    public const string SecuritySecretUpdated     = "integration.m365.secret.updated";
    public const string SecuritySecretDeleted     = "integration.m365.secret.deleted";
    public const string SecurityConfigUpdated     = "integration.m365.config.updated";
    public const string SecurityEnabledChanged    = "integration.m365.enabled.changed";
    public const string SecurityConnectionTested  = "integration.m365.connection.tested";
}
