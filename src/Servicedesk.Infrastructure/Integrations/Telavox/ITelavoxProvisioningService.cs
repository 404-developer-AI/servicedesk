namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Setup-time write surface for the Telavox integration. Wraps the three
/// admin-initiated actions:
/// <list type="bullet">
/// <item><see cref="TestConnectionAsync"/> — verifies the partner-token
/// by calling PAPI /customers; the returned customers populate the
/// admin's dropdown for pinning <c>Telavox.PartnerCustomerId</c>.</item>
/// <item><see cref="ProvisionAgentAsync"/> — links one SD user to one
/// Telavox extension. Side-effects: PAPI POST /api-users (mint CAPI
/// token), write to protected_secrets, insert/update
/// <c>telavox_agent_links</c>.</item>
/// <item><see cref="RevokeAgentAsync"/> — reverses the above. PAPI
/// DELETE /api-users, remove protected_secret, delete link row.</item>
/// </list>
/// Errors propagate as <see cref="TelavoxApiException"/>; the endpoint
/// layer maps those to a 502 envelope so the SPA can show the upstream
/// detail to the admin.
public interface ITelavoxProvisioningService
{
    Task<TelavoxTestConnectionResult> TestConnectionAsync(CancellationToken ct = default);

    Task<TelavoxAgentLink> ProvisionAgentAsync(
        TelavoxProvisionAgentRequest request, CancellationToken ct = default);

    /// Reverses <see cref="ProvisionAgentAsync"/>. Writes an
    /// <see cref="Servicedesk.Infrastructure.Audit.AuditEvent"/> row tagged
    /// with the supplied actor — every privileged action must audit. A
    /// future system-driven revoke (e.g. user-deactivation cascade) should
    /// pass <c>actor="system"</c> / <c>actorRole="System"</c>.
    Task RevokeAgentAsync(
        Guid userId, string actor, string actorRole, CancellationToken ct = default);
}

/// Outcome of <see cref="ITelavoxProvisioningService.TestConnectionAsync"/>.
/// <see cref="Customers"/> populates the admin dropdown; an empty list is
/// the diagnostic signal that the partner-token works but covers zero
/// customers (Telavox-side misconfiguration).
public sealed record TelavoxTestConnectionResult(
    IReadOnlyList<TelavoxCustomer> Customers);

/// Inputs to <see cref="ITelavoxProvisioningService.ProvisionAgentAsync"/>.
/// The agent's SD <c>users.email</c> is read inside the service so the
/// caller doesn't have to pass it (avoids a stale-email bug if the admin
/// pasted the wrong address into a form).
public sealed record TelavoxProvisioningInput(
    Guid UserId,
    string TelavoxExtension);

/// Wrapper carrying both the user-supplied request body fields and the
/// resolved actor/role for audit logging.
public sealed record TelavoxProvisionAgentRequest(
    Guid UserId,
    string TelavoxExtension,
    string Actor,
    string ActorRole);
