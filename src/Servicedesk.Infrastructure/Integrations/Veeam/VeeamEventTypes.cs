namespace Servicedesk.Infrastructure.Integrations.Veeam;

/// Security audit_log event types for the Veeam backup matching integration
/// (Veeam Service Provider Console reader). Mirrors the namespaced vocabulary
/// used by the M365 / Sophos / Telavox connectors so the audit-log UI can
/// filter on a stable prefix. These land in the hash-chained security trail via
/// IAuditLogger because they change a credential or a gate.
public static class VeeamEventTypes
{
    public const string Integration = "veeam";

    public const string SecuritySecretUpdated    = "integration.veeam.secret.updated";
    public const string SecuritySecretDeleted    = "integration.veeam.secret.deleted";
    public const string SecurityConfigUpdated    = "integration.veeam.config.updated";
    public const string SecurityEnabledChanged   = "integration.veeam.enabled.changed";
    public const string SecurityConnectionTested = "integration.veeam.connection.tested";
}
