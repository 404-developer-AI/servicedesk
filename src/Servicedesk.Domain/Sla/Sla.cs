namespace Servicedesk.Domain.Sla;

public sealed record BusinessHoursSchema(
    Guid Id,
    string Name,
    string Timezone,
    string CountryCode,
    bool IsDefault,
    IReadOnlyList<BusinessHoursSlot> Slots,
    IReadOnlyList<Holiday> Holidays);

public sealed record BusinessHoursSlot(
    long Id,
    Guid SchemaId,
    int DayOfWeek,
    int StartMinute,
    int EndMinute);

public sealed record Holiday(
    long Id,
    Guid SchemaId,
    DateOnly Date,
    string Name,
    string Source,
    string CountryCode);

public sealed record SlaPolicy(
    Guid Id,
    Guid? QueueId,
    Guid PriorityId,
    Guid BusinessHoursSchemaId,
    int FirstResponseMinutes,
    int ResolutionMinutes,
    bool PauseOnPending);

public sealed record TicketSlaState(
    Guid TicketId,
    Guid? PolicyId,
    DateTime? FirstResponseDeadlineUtc,
    DateTime? ResolutionDeadlineUtc,
    DateTime? FirstResponseMetUtc,
    DateTime? ResolutionMetUtc,
    int? FirstResponseBusinessMinutes,
    int? ResolutionBusinessMinutes,
    bool IsPaused,
    DateTime? PausedSinceUtc,
    int PausedAccumMinutes,
    DateTime LastRecalcUtc,
    DateTime UpdatedUtc);
