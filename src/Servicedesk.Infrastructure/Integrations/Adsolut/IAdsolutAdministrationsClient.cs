namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One Adsolut administration (dossier) the authorized user owns. Returned
/// by <see cref="IAdsolutAdministrationsClient.ListAsync"/> so the admin UI
/// can render a picker. Only the fields the UI actually surfaces are
/// projected — the upstream API returns more (vatNumber, address, …) but
/// we don't persist or display them yet.
public sealed record AdsolutAdministrationSummary(
    Guid Id,
    string Name,
    string? Code);

/// HTTP client for the Adsolut Administrations service (`/adm/v1`).
/// Distinct from the Accounting client because the base path differs and
/// the endpoints are always-on (an admin needs to list dossiers BEFORE the
/// integration is activated against any specific dossier). Every call
/// writes a row to <c>integration_audit</c> so a hung Administrations
/// roundtrip surfaces in the admin overview without correlating logs.
public interface IAdsolutAdministrationsClient
{
    Task<IReadOnlyList<AdsolutAdministrationSummary>> ListAsync(CancellationToken ct = default);

    /// POST /adm/v1/administrations/{id}/integrations. Required step after
    /// the admin picks a dossier — without it every Accounting call returns
    /// empty (per WK docs). Idempotent on the WK side: re-activating an
    /// already-activated dossier returns 200 with no change.
    Task ActivateAsync(Guid administrationId, CancellationToken ct = default);

    /// DELETE /adm/v1/administrations/{id}/integrations. Called by the
    /// disconnect flow before tokens are wiped — leaving the integration
    /// active has financial impact for the customer per WK docs. Best-effort:
    /// failures are logged to integration_audit but do not block the local
    /// disconnect (a stuck deactivate must not orphan a refresh token).
    Task DeactivateAsync(Guid administrationId, CancellationToken ct = default);
}
