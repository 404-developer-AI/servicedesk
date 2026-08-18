using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Notifications;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;

namespace Servicedesk.Infrastructure.Checklists;

/// v0.0.103 — what happens when a trigger's set-status action is refused by
/// the checklist close block: a <c>ChecklistCloseBlocked</c> timeline event
/// for everyone, and a bell notification + realtime push for the agent who
/// caused it (the author of the article that fired the trigger; the ticket's
/// assignee for scheduled runs). The ticket page turns the push into the
/// same "Checklist not finished" dialog an agent gets on a manual change.
public interface IChecklistCloseBlockReporter
{
    Task ReportTriggerBlockedAsync(
        Ticket ticket,
        TicketEvent? triggeringEvent,
        Guid triggerId,
        string triggerName,
        Guid targetStatusId,
        IReadOnlyList<ChecklistBlocker> blockers,
        CancellationToken ct);
}

public sealed class ChecklistCloseBlockReporter : IChecklistCloseBlockReporter
{
    public const string NotificationType = "checklist_blocked";

    private readonly ITicketRepository _tickets;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly INotificationRepository _notifications;
    private readonly IUserNotifier _pusher;
    private readonly ILogger<ChecklistCloseBlockReporter> _logger;

    public ChecklistCloseBlockReporter(
        ITicketRepository tickets,
        ITaxonomyRepository taxonomy,
        INotificationRepository notifications,
        IUserNotifier pusher,
        ILogger<ChecklistCloseBlockReporter> logger)
    {
        _tickets = tickets;
        _taxonomy = taxonomy;
        _notifications = notifications;
        _pusher = pusher;
        _logger = logger;
    }

    public async Task ReportTriggerBlockedAsync(
        Ticket ticket, TicketEvent? triggeringEvent, Guid triggerId, string triggerName,
        Guid targetStatusId, IReadOnlyList<ChecklistBlocker> blockers, CancellationToken ct)
    {
        var status = await _taxonomy.GetStatusAsync(targetStatusId, ct);
        var statusName = status?.Name ?? "a closing status";
        var items = blockers.Select(b => new ChecklistCloseBlockedItem(b.ChecklistId, b.Name, b.OpenRequired)).ToList();

        // Timeline event — visible to every agent on the ticket, and the
        // anchor the bell notification jumps to.
        var evt = await _tickets.AddEventAsync(ticket.Id, new NewTicketEvent(
            EventType: nameof(TicketEventType.ChecklistCloseBlocked),
            BodyText: null, BodyHtml: null, IsInternal: true,
            AuthorUserId: null,
            MetadataJson: JsonSerializer.Serialize(new
            {
                triggerId,
                triggerName,
                targetStatusId,
                targetStatusName = statusName,
                checklists = items.Select(i => new { checklistId = i.ChecklistId, name = i.Name, openRequired = i.OpenRequired }),
            })), ct);

        // Who should hear about it: the agent whose article fired the
        // trigger, else the assignee. Nobody → timeline only.
        var recipient = triggeringEvent?.AuthorUserId ?? ticket.AssigneeUserId;
        if (recipient is null || evt is null) return;

        var summary = items.Count == 1
            ? $"“{items[0].Name}” has {items[0].OpenRequired} required item{(items[0].OpenRequired == 1 ? "" : "s")} open"
            : $"{items.Count} checklists still have required items open";
        var preview = $"Trigger “{triggerName}” could not set the ticket to {statusName}: {summary}.";

        try
        {
            var rows = await _notifications.CreateManyAsync(new[]
            {
                new NewUserNotification(
                    UserId: recipient.Value,
                    SourceUserId: null,
                    NotificationType: NotificationType,
                    TicketId: ticket.Id,
                    TicketNumber: ticket.Number,
                    TicketSubject: ticket.Subject,
                    EventId: evt.Id,
                    EventType: nameof(TicketEventType.ChecklistCloseBlocked),
                    PreviewText: preview),
            }, ct);
            var row = rows.FirstOrDefault();
            await _pusher.NotifyChecklistCloseBlockedAsync(recipient.Value, new ChecklistCloseBlockedPush(
                NotificationId: row?.Id ?? Guid.Empty,
                TicketId: ticket.Id,
                TicketNumber: ticket.Number,
                TicketSubject: ticket.Subject,
                TriggerName: triggerName,
                TargetStatusName: statusName,
                Checklists: items,
                EventId: evt.Id,
                CreatedUtc: DateTime.UtcNow), ct);
        }
        catch (Exception ex)
        {
            // Best effort: the block itself already held (the status was not
            // changed) and the timeline event is written; a failed
            // notification must never fail the trigger run.
            _logger.LogWarning(ex, "Checklist close-block notification failed for ticket {TicketId}.", ticket.Id);
        }
    }
}
