namespace Servicedesk.Infrastructure.Integrations.Claude;

/// Data access for the Claude usage log and per-agent budgets. Append-only
/// log; budget overrides live on the users row (NULL = use the global
/// default). All month windows are passed in by the caller as a UTC instant
/// so timing stays server-based and deterministic.
public interface IClaudeUsageStore
{
    Task LogAsync(ClaudeUsageEntry entry, CancellationToken ct);

    /// Total cost (micro-euros) charged to <paramref name="userId"/> since
    /// <paramref name="monthStartUtc"/>. Blocked rows carry zero cost.
    Task<long> GetMonthSpendMicroEurAsync(Guid userId, DateTime monthStartUtc, CancellationToken ct);

    /// Per-agent monthly budget override in euro cents, or null when the agent
    /// uses the global default.
    Task<int?> GetUserBudgetOverrideCentsAsync(Guid userId, CancellationToken ct);

    /// Sets (non-null) or clears (null) the per-agent budget override.
    Task SetUserBudgetOverrideCentsAsync(Guid userId, int? cents, CancellationToken ct);

    /// Admin overview: every agent/admin with their budget override and this
    /// month's spend + call count.
    Task<IReadOnlyList<ClaudeAgentUsage>> GetAgentMonthlyUsageAsync(DateTime monthStartUtc, CancellationToken ct);
}
