namespace Servicedesk.Domain.TicketTemplates;

/// A pre-canned set of ticket field values an agent picks while creating a
/// ticket. Every pre-fill field is optional — a template that only sets
/// <see cref="QueueId"/> + <see cref="PriorityId"/> leaves the rest of the
/// New-Ticket form untouched on apply. <see cref="Subject"/>,
/// <see cref="BodyHtml"/> and <see cref="InitialNoteHtml"/> may contain
/// {{tokens}} resolved against the chosen requester + company at apply time
/// (the same engine compose templates use).
public sealed record TicketTemplate(
    Guid Id,
    string Name,
    string? Description,
    bool IsActive,
    string Subject,
    string BodyHtml,
    string InitialNoteHtml,
    bool InitialNoteInternal,
    Guid? QueueId,
    Guid? PriorityId,
    Guid? StatusId,
    Guid? CategoryId,
    Guid? TicketTypeId,
    Guid? AssigneeUserId,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? CreatedBy);
