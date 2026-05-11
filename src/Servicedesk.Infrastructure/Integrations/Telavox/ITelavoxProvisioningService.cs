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

    /// Manual fallback for installs where PAPI api-user creation is not
    /// available — admin pastes a CAPI bearer-token they minted via the
    /// Telavox webapp directly. Side-effects: write to protected_secrets,
    /// insert/update <c>telavox_agent_links</c>. No PAPI call is made.
    /// <see cref="TelavoxAgentLink.TelavoxUserId"/> is set to the sentinel
    /// <c>"manual"</c> on the resulting row so <see cref="RevokeAgentAsync"/>
    /// knows to skip the upstream DELETE.
    Task<TelavoxAgentLink> ProvisionAgentManualAsync(
        TelavoxProvisionAgentManualRequest request, CancellationToken ct = default);

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

/// Manual-link variant — admin paste'd CAPI bearer-token replaces the
/// PAPI mint round-trip. The token is treated as an opaque secret; the
/// provisioning service writes it verbatim to protected_secrets and
/// performs no validation against Telavox before storing (the polling
/// worker is the first thing that finds out if the token is bad).
public sealed record TelavoxProvisionAgentManualRequest(
    Guid UserId,
    string TelavoxExtension,
    string CapiToken,
    string Actor,
    string ActorRole);
