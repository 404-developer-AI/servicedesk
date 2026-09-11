namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// HTTP surface for Tactical RMM. Implementations read the base URL +
/// API key per call from settings / protected_secrets, write one
/// integration_audit row per call, and surface upstream failures as
/// <see cref="TrmmApiException"/>.
public interface ITrmmApiClient
{
    /// Pulls every client and its embedded sites in one round-trip.
    /// TRMM does not expose a top-level <c>/sites/</c> endpoint —
    /// the canonical way to enumerate sites is the <c>sites</c> array
    /// on each <c>/clients/</c> row.
    Task<TrmmClientSnapshot> ListClientsAndSitesAsync(CancellationToken ct);

    Task<IReadOnlyList<TrmmAgent>> ListAgentsAsync(CancellationToken ct);

    /// Pulls every check (agent-level + policy-inherited) of one agent
    /// together with that agent's last result per check. Used by the
    /// Remote Desktop sync (v0.1.10) to read the RDS script-check output.
    /// Success is <b>not</b> audited per call — one row per agent per
    /// cycle would flood <c>integration_audit</c>; the RDS sync writes
    /// a single summary row instead. Failures are still audited.
    Task<IReadOnlyList<TrmmAgentCheck>> ListAgentChecksAsync(string agentId, CancellationToken ct);
    Task<TrmmConnectionTestResult> TestConnectionAsync(CancellationToken ct);
}

/// Result of <see cref="ITrmmApiClient.ListClientsAndSitesAsync"/>. The
/// two lists are emitted from a single <c>/clients/</c> call so the
/// pair is always self-consistent (every site's <c>ClientId</c> points
/// at a client in the same snapshot).
public sealed record TrmmClientSnapshot(
    IReadOnlyList<TrmmClient> Clients,
    IReadOnlyList<TrmmSite> Sites);
