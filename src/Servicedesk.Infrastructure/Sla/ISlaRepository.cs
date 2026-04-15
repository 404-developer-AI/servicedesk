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
    Task<Guid> UpsertPolicyAsync(Guid? queueId, Guid priorityId, Guid schemaId, int firstResponseMinutes, int resolutionMinutes, bool pauseOnPending, CancellationToken ct);
    Task DeletePolicyAsync(Guid id, CancellationToken ct);

    // ---- Ticket SLA state ----
    Task<TicketSlaState?> GetStateAsync(Guid ticketId, CancellationToken ct);
    Task UpsertStateAsync(TicketSlaState state, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListActiveTicketIdsAsync(int limit, CancellationToken ct);

    // ---- Queries for UI ----
    Task<IReadOnlyList<SlaLogRow>> QueryLogAsync(SlaLogFilter filter, CancellationToken ct);
    Task<IReadOnlyList<QueueAvgPickup>> AvgPickupPerQueueAsync(int days, CancellationToken ct);
}

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
    int? FirstResponseBusinessMinutes,
    int? ResolutionBusinessMinutes,
    bool IsPaused,
    bool FirstResponseBreached,
    bool ResolutionBreached);

public sealed record QueueAvgPickup(
    Guid QueueId,
    string QueueName,
    int TicketCount,
    double? AvgBusinessMinutes);
