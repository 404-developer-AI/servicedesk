namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Persistence surface for <c>telavox_agent_links</c>. One row per linked
/// SD user — the FK to <c>users</c> is <c>ON DELETE CASCADE</c>, so a hard
/// user-delete also drops the link (the provisioning service still revokes
/// the upstream api-user via Telavox PAPI before deletion when possible).
public interface ITelavoxAgentLinkStore
{
    Task<IReadOnlyList<TelavoxAgentLink>> ListAsync(CancellationToken ct = default);

    Task<TelavoxAgentLink?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    Task UpsertAsync(TelavoxAgentLink link, CancellationToken ct = default);

    /// Returns the number of rows deleted (0 = no row existed). Used by
    /// the provisioning service to decide whether to write the audit row
    /// after a revoke.
    Task<int> DeleteByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// Updates the operational columns (last_poll_utc, last_poll_error,
    /// consecutive_errors) without touching the immutable provisioning
    /// fields. Used by the polling worker landing in v0.0.34 commit C.
    Task UpdatePollOutcomeAsync(
        Guid userId,
        DateTime lastPollUtc,
        string? lastPollError,
        int consecutiveErrors,
        CancellationToken ct = default);
}
