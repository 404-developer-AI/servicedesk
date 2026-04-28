namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Persistent store for the singleton <c>adsolut_connection</c> row. The
/// AdsolutAuthService owns the read-modify-write cycle; this contract is
/// intentionally narrow so a future swap (e.g. to a multi-tenant per-row
/// store) only changes the implementation, not the call sites.
public interface IAdsolutConnectionStore
{
    Task<AdsolutConnection?> GetAsync(CancellationToken ct = default);

    Task SaveAsync(AdsolutConnection connection, CancellationToken ct = default);

    /// Removes the singleton row. Companion call to clearing the
    /// refresh-token entry in <c>protected_secrets</c>; both happen
    /// together when an admin disconnects.
    Task DeleteAsync(CancellationToken ct = default);
}
