using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Triggers;

namespace Servicedesk.Infrastructure.Tickets;

/// A ticket the server opens by itself. Optional fields fall back to the
/// install defaults (<see cref="TicketDefaults"/>); a status outside the
/// queue's allowed list or a Pending status (it needs a date) falls back too,
/// and an assignee that cannot take tickets is dropped.
public sealed record NewSystemTicket(
    string Subject,
    string BodyHtml,
    Guid RequesterContactId,
    Guid? CompanyId,
    Guid QueueId,
    Guid? StatusId = null,
    Guid? PriorityId = null,
    Guid? CategoryId = null,
    Guid? AssigneeUserId = null,
    Guid? TicketTypeId = null,
    InitialTicketNote? InitialNote = null);

public interface ISystemTicketCreator
{
    /// Creates the ticket and runs the same follow-up as an agent-created
    /// ticket: SLA, audit (actor = <paramref name="origin"/>), realtime push
    /// and a trigger pass with every field new. Null when the install has no
    /// active status or priority.
    Task<Ticket?> CreateAsync(NewSystemTicket input, string origin, CancellationToken ct);
}

/// v0.1.67 — the system path for creating tickets (first user: the
/// maintenance planner). Same steps as <c>POST /api/tickets</c> without an
/// agent: no queue-access check (the configuration chose the queue), source
/// <see cref="TicketSource.System"/>, an explicit company recorded as
/// "manual", and triggers run without an agent origin (like mail intake).
public sealed class SystemTicketCreator : ISystemTicketCreator
{
    private readonly ITicketRepository _tickets;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IQueueStatusScope _statusScope;
    private readonly IUserService _users;
    private readonly ISlaEngine _sla;
    private readonly ITriggerService _triggers;
    private readonly ITicketListNotifier _notifier;
    private readonly IAuditLogger _audit;

    public SystemTicketCreator(
        ITicketRepository tickets, ITaxonomyRepository taxonomy, IQueueStatusScope statusScope, IUserService users,
        ISlaEngine sla, ITriggerService triggers, ITicketListNotifier notifier, IAuditLogger audit)
    {
        _tickets = tickets;
        _taxonomy = taxonomy;
        _statusScope = statusScope;
        _users = users;
        _sla = sla;
        _triggers = triggers;
        _notifier = notifier;
        _audit = audit;
    }

    public async Task<Ticket?> CreateAsync(NewSystemTicket input, string origin, CancellationToken ct)
    {
        var defaults = await TicketDefaults.ResolveAsync(_taxonomy, ct);
        if (defaults is null) return null;

        var statusId = defaults.Value.StatusId;
        if (input.StatusId is { } wanted
            && await _statusScope.IsStatusAllowedAsync(input.QueueId, wanted, ct)
            && !await _statusScope.IsPendingStatusAsync(wanted, ct))
            statusId = wanted;

        Guid? assignee = input.AssigneeUserId is { } a && await TicketAssigneeRule.IsAssignableAsync(_users, a, ct) ? a : null;

        var ticket = await _tickets.CreateAsync(new NewTicket(
            Subject: string.IsNullOrWhiteSpace(input.Subject) ? "(no subject)" : input.Subject.Trim(),
            BodyText: KnowledgeBase.KbBodyStripper.HtmlToText(input.BodyHtml),
            BodyHtml: string.IsNullOrWhiteSpace(input.BodyHtml) ? null : input.BodyHtml,
            RequesterContactId: input.RequesterContactId,
            QueueId: input.QueueId,
            StatusId: statusId,
            PriorityId: input.PriorityId ?? defaults.Value.PriorityId,
            CategoryId: input.CategoryId,
            AssigneeUserId: assignee,
            Source: TicketSource.System.ToString(),
            CompanyId: input.CompanyId,
            AwaitingCompanyAssignment: false,
            CompanyResolvedVia: input.CompanyId is null ? "unresolved" : "manual",
            TicketTypeId: input.TicketTypeId,
            InitialNote: input.InitialNote), ct);

        await _audit.LogAsync(new AuditEvent(
            EventType: "ticket.created",
            Actor: origin,
            ActorRole: "system",
            Target: ticket.Id.ToString(),
            Payload: new { ticket.Number, input.QueueId, source = "System", origin }), ct);
        await _sla.OnTicketCreatedAsync(ticket.Id, ct);
        await _notifier.NotifyUpdatedAsync(ticket.Id, ct);
        await _triggers.EvaluateAsync(ticket.Id, ticketEventId: null, TriggerActivatorKind.Action,
            TriggerChangeSet.AllFieldsNew(), ct);
        return ticket;
    }
}
