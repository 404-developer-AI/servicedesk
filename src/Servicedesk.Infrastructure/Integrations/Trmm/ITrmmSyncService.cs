namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// One end-to-end TRMM → local mirror sync run. Reads clients/sites/agents
/// from <see cref="ITrmmApiClient"/> and upserts into the three mirror
/// tables in one transaction-free batch (each upsert is its own statement
/// so a partial-failure halfway through still leaves the rest converged).
public interface ITrmmSyncService
{
    Task<TrmmSyncOutcome> RunOnceAsync(string trigger, CancellationToken ct);
}

/// Aggregate result of one sync cycle. Counts split per-table so the
/// audit-log payload tells an admin which leg of the sync moved data.
public sealed record TrmmSyncOutcome(
    bool Success,
    int Clients,
    int Sites,
    int Agents,
    int AutoLinkedCompanies,
    int LatencyMs,
    string? ErrorCode,
    string? ErrorMessage);
