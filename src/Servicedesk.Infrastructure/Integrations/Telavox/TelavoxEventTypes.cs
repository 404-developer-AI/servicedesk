namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Audit event-type strings for the Telavox call-popup integration. Two
/// audiences:
/// <list type="bullet">
/// <item><c>audit_log</c> — credential/admin actions (partner-token set,
/// agent provisioned, agent revoked). Hash-chained security trail.</item>
/// <item><c>integration_audit</c> — outbound HTTP calls (PAPI customers
/// list, CAPI poll tick, healthcheck). Carries latency + http_status +
/// upstream error.</item>
/// </list>
/// Centralised so endpoints, provisioning service and the (v0.0.34 commit C)
/// polling worker all agree on the wire-format names.
public static class TelavoxEventTypes
{
    /// Integration name written to <c>integration_audit.integration</c>.
    public const string Integration = "telavox";

    // ---- audit_log (security trail) -----------------------------------

    /// Admin updated the PAPI partner-token in protected_secrets. Mirrors
    /// the Adsolut <c>client_secret.updated</c> event — only the fact of
    /// change is recorded, never the value itself.
    public const string PartnerTokenUpdated = "integration.telavox.partner_token.updated";

    /// Admin cleared the PAPI partner-token. The integration is now inert
    /// until a new token is supplied.
    public const string PartnerTokenDeleted = "integration.telavox.partner_token.deleted";

    /// Admin pinned the Telavox customer-id from the test-connection
    /// dropdown. Payload carries the chosen id so a wrong-customer mistake
    /// is forensically traceable.
    public const string CustomerIdSelected = "integration.telavox.customer_id.selected";

    /// Admin manually linked an SD user to a Telavox extension. The
    /// provisioning service has at this point already minted a CAPI-token
    /// and stored it in protected_secrets; payload carries the SD userId +
    /// extension number so a wrong-extension mistake is traceable.
    public const string AgentProvisioned = "integration.telavox.agent.provisioned";

    /// Admin de-linked an SD user. Provisioning service has deleted the
    /// PAPI api-user, removed the protected_secrets row, and dropped the
    /// telavox_agent_links row.
    public const string AgentRevoked = "integration.telavox.agent.revoked";

    // ---- integration_audit (operational log) --------------------------

    /// GET /papi/v1/customers — PAPI customers list. Used by the
    /// /test-connection action to populate the customer-id dropdown.
    public const string CustomersList = "papi.customers.list";

    /// GET /papi/v1/customers/{customer}/extensions — full extensions
    /// list for the configured customer. Used by the integration page
    /// agent-mapping UI to populate the per-agent dropdown.
    public const string ExtensionsList = "papi.extensions.list";

    /// POST /papi/v1/customers/{customer}/api-users — provision a fresh
    /// CAPI token tied to one SD user. One row per click on "Link agent".
    public const string ApiUserCreate = "papi.api_user.create";

    /// DELETE /papi/v1/customers/{customer}/api-users/{email} — revoke
    /// the CAPI token. Fired on de-link.
    public const string ApiUserDelete = "papi.api_user.delete";

    /// CAPI poll tick — one outbound call per linked agent per
    /// PollIntervalSeconds. Lands in v0.0.34 commit C; constant defined
    /// here so the test-connection / provisioning endpoints can already
    /// reference the same names.
    public const string AgentCallsPoll = "capi.calls.poll";

    /// Healthcheck heartbeat row — written by the integrations
    /// healthcheck worker even when Telavox is unconfigured, so admins
    /// can see the worker is alive.
    public const string HealthcheckTick = "healthcheck.tick";
}
