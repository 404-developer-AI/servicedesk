using Servicedesk.Domain.Sla;

namespace Servicedesk.Infrastructure.Sla;

public interface ISlaRepository
{
    // ---- Business hours ----
    Task<IReadOnlyList<BusinessHoursSchema>> ListSchemasAsync(CancellationToken ct);
    Task<BusinessHoursSchema?> GetSchemaAsync(Guid id, CancellationToken ct);
    Task<BusinessHoursSchema?> GetDefaultSchemaAsync(CancellationToken ct);
    Task<Guid> CreateSchemaAsync(string name, string timezone, string countryCode, bool isDefault, CancellationToken ct);
    Task UpdateSchemaAsync(Guid id, string name, string timezone, string countryCode, bool isDefault, CancellationToken ct);
    Task DeleteSchemaAsync(Guid id, CancellationToken ct);

    Task SetSlotsAsync(Guid schemaId, IReadOnlyList<(int Day, int Start, int End)> slots, CancellationToken ct);

    // ---- Holidays ----
    Task<IReadOnlyList<Holiday>> ListHolidaysAsync(Guid schemaId, int? year, CancellationToken ct);
    Task AddHolidayAsync(Guid schemaId, DateOnly date, string name, string source, string countryCode, CancellationToken ct);
    Task DeleteHolidayAsync(long id, CancellationToken ct);
    Task ReplaceNagerHolidaysAsync(Guid schemaId, int year, string countryCode, IReadOnlyList<(DateOnly Date, string Name)> holidays, CancellationToken ct);

    // ---- Policies ----
    Task<IReadOnlyList<SlaPolicy>> ListPoliciesAsync(CancellationToken ct);
    Task<SlaPolicy?> GetPolicyAsync(Guid id, CancellationToken ct);
    Task<SlaPolicy?> FindPolicyAsync(Guid? queueId, Guid priorityId, CancellationToken ct);
    Task<Guid> UpsertPolicyAsync(Guid? queueId, Guid priorityId, Guid schemaId, int? firstResponseMinutes, int? resolutionMinutes, bool pauseOnPending, CancellationToken ct);
    Task DeletePolicyAsync(Guid id, CancellationToken ct);

    /// v0.0.101 — policy + schema resolution from an in-process snapshot of
    /// the whole SLA configuration (all policies, all schemas with slots +
    /// holidays). The snapshot is (re)loaded at most once per <paramref
    /// name="maxAge"/> and dropped immediately by every write on this
    /// repository, so the recalc path (worker + every ticket mutation) no
    /// longer pays 4 queries per ticket for configuration that changes a few
    /// times a year. Same resolution rule as <see cref="FindPolicyAsync"/>:
    /// queue-specific policy for the priority wins over the queue-less
    /// fallback. <c>Schema</c> is null when the policy points at a missing
    /// schema (caller logs, as before).
    Task<SlaResolvedPolicy?> ResolvePolicyAsync(Guid? queueId, Guid priorityId, TimeSpan maxAge, CancellationToken ct);

    // ---- Ticket SLA state ----
    Task<TicketSlaState?> GetStateAsync(Guid ticketId, CancellationToken ct);
    /// Writes the state row only when something other than the recalc
    /// timestamps changed (v0.0.101) — the periodic sweep re-derives the
    /// same values for most tickets and used to rewrite every row every
    /// cycle. Returns true when a row was inserted/updated.
    Task<bool> UpsertStateAsync(TicketSlaState state, CancellationToken ct);
    /// v0.0.101 — keyset page over the tickets the periodic sweep should
    /// revisit: not deleted, not closed, not resolved (a resolved ticket's
    /// SLA numbers are frozen until it is reopened, and reopening is a field
    /// change that recalcs on its own). Ordered by (updated_utc, id) ascending
    /// so the worker can walk the whole open set across cycles with a cursor
    /// instead of recomputing the same oldest N every cycle; the predicate
    /// matches the partial index ix_tickets_open_updated exactly.
    Task<IReadOnlyList<SlaRecalcCandidate>> ListRecalcCandidatesAsync(int limit, SlaRecalcCursor? after, CancellationToken ct);
    /// v0.0.101 — resolved-but-open tickets that never got a state row (e.g.
    /// imported before the engine ran). Used once at worker start-up so the
    /// sweep predicate above can stay index-friendly.
    Task<IReadOnlyList<Guid>> ListResolvedWithoutStateAsync(int limit, CancellationToken ct);

    // ---- Queries for UI ----
    Task<IReadOnlyList<SlaLogRow>> QueryLogAsync(SlaLogFilter filter, CancellationToken ct);
    Task<IReadOnlyList<QueueAvgPickup>> AvgPickupPerQueueAsync(int days, CancellationToken ct);
}

public sealed record SlaResolvedPolicy(SlaPolicy Policy, BusinessHoursSchema? Schema);

public sealed record SlaRecalcCandidate(Guid Id, DateTime UpdatedUtc);

public sealed record SlaRecalcCursor(DateTime UpdatedUtc, Guid Id);

public sealed record SlaLogFilter(
    Guid? QueueId,
    Guid? PriorityId,
    Guid? StatusId,
    bool? BreachedOnly,
    DateTime? FromUtc,
    DateTime? ToUtc,
    string? Search,
    int Limit,
    long? CursorNumber);

public sealed record SlaLogRow(
    Guid TicketId,
    long Number,
    string Subject,
    Guid QueueId,
    string QueueName,
    Guid PriorityId,
    string PriorityName,
    Guid StatusId,
    string StatusName,
    DateTime CreatedUtc,
    DateTime? FirstResponseDeadlineUtc,
    DateTime? FirstResponseMetUtc,
    DateTime? ResolutionDeadlineUtc,
    DateTime? ResolutionMetUtc,
    int? FirstResponseTargetMinutes,
    int? ResolutionTargetMinutes,
    int? FirstResponseBusinessMinutes,
    int? ResolutionBusinessMinutes,
    bool IsPaused,
    bool FirstResponseBreached,
    bool ResolutionBreached);

public sealed record QueueAvgPickup(
    Guid QueueId,
    string QueueName,
    long TicketCount,
    double? AvgBusinessMinutes);
