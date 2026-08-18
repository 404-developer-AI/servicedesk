using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Checklists;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Triggers;
using Servicedesk.Infrastructure.Triggers.StatusGate;

namespace Servicedesk.Infrastructure.Tickets;

/// Who is performing an agent-side ticket mutation. Carries both the
/// authorization identity (user id + role, for queue-access checks) and the
/// audit identity (label + role + client info, for the hash-chained audit
/// log) so callers resolve it once per request and pass it down.
public sealed record TicketMutationActor(
    Guid UserId,
    string Role,
    string AuditActor,
    string AuditRole,
    string? ClientIp,
    string? UserAgent);

/// Outcome of the pre-mutation rule check. Anything but <see cref="Ok"/>
/// means the caller must not apply the mutation.
public enum TicketMutationCheck
{
    Ok,
    /// Ticket does not exist (or is soft-deleted). Endpoints render this as
    /// 404 — same as no access, so an id probe cannot tell them apart.
    NotFound,
    /// Actor lacks access to the ticket's current queue.
    NoAccess,
    /// Actor lacks access to the queue the update would move the ticket to.
    TargetQueueNoAccess,
    /// Explicit status is outside the (target) queue's allowed-status list.
    StatusNotInQueueScope,
    /// v0.0.103 — the status change would resolve/close the ticket while an
    /// attached checklist that blocks closing still has required open items.
    /// <see cref="FieldUpdatePrecheck.ChecklistBlockers"/> names them.
    ChecklistIncomplete,
}

/// Result of <see cref="ITicketMutationService.PrecheckFieldUpdateAsync"/>.
/// <see cref="Gates"/> is non-empty only when the update changes the status
/// and one or more status gates match; the single-ticket endpoint then
/// negotiates confirmations with the agent, a bulk action skips the ticket.
public sealed record FieldUpdatePrecheck(
    TicketMutationCheck Check,
    TicketDetail? Ticket,
    IReadOnlyList<MatchedStatusGate> Gates,
    IReadOnlyList<ChecklistBlocker>? ChecklistBlockers = null)
{
    public static FieldUpdatePrecheck Fail(TicketMutationCheck check) =>
        new(check, null, Array.Empty<MatchedStatusGate>());

    public static FieldUpdatePrecheck Blocked(IReadOnlyList<ChecklistBlocker> blockers) =>
        new(TicketMutationCheck.ChecklistIncomplete, null, Array.Empty<MatchedStatusGate>(), blockers);
}

/// Result of <see cref="ITicketMutationService.PrecheckAccessAsync"/>.
public sealed record AccessPrecheck(TicketMutationCheck Check, TicketDetail? Ticket);

/// The one place the agent-side ticket mutation rules live (v0.0.102).
///
/// Before bulk actions the PATCH-ticket and POST-event endpoints each held
/// the full rule set inline: queue access on the current + target queue,
/// status-in-queue-scope, the status-gate probe, then the fan-out after the
/// write (audit → realtime → SLA recalc → trigger evaluation). A bulk action
/// must run *exactly* those rules per ticket, so they were lifted here and
/// both the single-ticket endpoints and the bulk service call in.
///
/// Deliberately split into precheck / apply / publish rather than one
/// "do everything" call: the single-ticket endpoint has to insert its gate-
/// confirmation handling (pending contact-company links before the write,
/// confirmation notes after it) between the write and the fan-out, and the
/// bulk service needs the precheck verdict per ticket before it decides to
/// touch anything. The apply step itself is the repository call.
public interface ITicketMutationService
{
    /// Loads the ticket and verifies the actor may act on its current queue.
    Task<AccessPrecheck> PrecheckAccessAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct);

    /// Full rule check for a field update: access on the current queue, access
    /// on the target queue when moving, explicit status inside the (target)
    /// queue's allowed list, and the status-gate probe when the status changes.
    Task<FieldUpdatePrecheck> PrecheckFieldUpdateAsync(
        TicketMutationActor actor, Guid ticketId, TicketFieldUpdate update, CancellationToken ct);

    /// Applies the field update (the repository write, incl. its change
    /// events and status auto-flip). Returns null when the ticket vanished
    /// between precheck and write.
    Task<TicketDetail?> ApplyFieldUpdateAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct);

    /// Post-write fan-out for a field update: audit (`ticket.updated`),
    /// realtime broadcast, SLA recalc, trigger evaluation. <paramref name="changeSet"/>
    /// lets the caller pass its own changed-field set (the PATCH endpoint
    /// derives it from the raw request); when null it is derived from
    /// <paramref name="update"/>.
    Task PublishFieldUpdateAsync(
        TicketMutationActor actor, Guid ticketId, TicketFieldUpdate update,
        object auditPayload, TriggerChangeSet? changeSet, CancellationToken ct);

    /// Writes a Comment/Note event. Derives the plain-text body from HTML when
    /// the caller only supplied HTML (so search, trigger conditions and mention
    /// notifications never see a null body_text). Returns null when the ticket
    /// vanished between precheck and write.
    Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct);

    /// Post-write fan-out for a new event: audit (`ticket.event.added`),
    /// realtime broadcast, SLA recalc, trigger evaluation (article-added).
    Task PublishEventAsync(
        TicketMutationActor actor, Guid ticketId, TicketEvent evt, object auditPayload, CancellationToken ct);
}

public sealed class TicketMutationService : ITicketMutationService
{
    private readonly ITicketRepository _tickets;
    private readonly IQueueAccessService _queueAccess;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IStatusGateService _gates;
    private readonly IAuditLogger _audit;
    private readonly ITicketListNotifier _notifier;
    private readonly ISlaEngine _sla;
    private readonly ITriggerService _triggers;
    private readonly IChecklistCloseGuard _checklistGuard;

    public TicketMutationService(
        ITicketRepository tickets,
        IQueueAccessService queueAccess,
        ITaxonomyRepository taxonomy,
        IStatusGateService gates,
        IAuditLogger audit,
        ITicketListNotifier notifier,
        ISlaEngine sla,
        ITriggerService triggers,
        IChecklistCloseGuard checklistGuard)
    {
        _tickets = tickets;
        _queueAccess = queueAccess;
        _taxonomy = taxonomy;
        _gates = gates;
        _audit = audit;
        _notifier = notifier;
        _sla = sla;
        _triggers = triggers;
        _checklistGuard = checklistGuard;
    }

    public async Task<AccessPrecheck> PrecheckAccessAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
    {
        var current = await _tickets.GetByIdAsync(ticketId, ct);
        if (current is null) return new AccessPrecheck(TicketMutationCheck.NotFound, null);
        if (!await _queueAccess.HasQueueAccessAsync(actor.UserId, actor.Role, current.Ticket.QueueId, ct))
            return new AccessPrecheck(TicketMutationCheck.NoAccess, null);
        return new AccessPrecheck(TicketMutationCheck.Ok, current);
    }

    public async Task<FieldUpdatePrecheck> PrecheckFieldUpdateAsync(
        TicketMutationActor actor, Guid ticketId, TicketFieldUpdate update, CancellationToken ct)
    {
        var access = await PrecheckAccessAsync(actor, ticketId, ct);
        if (access.Check != TicketMutationCheck.Ok || access.Ticket is null)
            return FieldUpdatePrecheck.Fail(access.Check);
        var current = access.Ticket;

        // Moving queues requires access to the destination too.
        if (update.QueueId.HasValue && update.QueueId != current.Ticket.QueueId)
        {
            if (!await _queueAccess.HasQueueAccessAsync(actor.UserId, actor.Role, update.QueueId.Value, ct))
                return FieldUpdatePrecheck.Fail(TicketMutationCheck.TargetQueueNoAccess);
        }

        // v0.0.40 polish — an explicit status must sit in the target queue's
        // allowed-list. Skipped when StatusId is unset (the repository's
        // auto-flip handles queue-only changes). Validated against the
        // current queue when the queue isn't changing.
        if (update.StatusId.HasValue)
        {
            var targetQueueId = update.QueueId ?? current.Ticket.QueueId;
            var targetQueue = await _taxonomy.GetQueueAsync(targetQueueId, ct);
            if (targetQueue is not null
                && targetQueue.AllowedStatusIds is { Count: > 0 } allowed
                && !allowed.Contains(update.StatusId.Value))
            {
                return FieldUpdatePrecheck.Fail(TicketMutationCheck.StatusNotInQueueScope);
            }
        }

        // v0.0.103 — checklist close block. A hard rule, not a gate: no
        // confirmation can satisfy it, the agent has to finish (or n/a) the
        // required items first. Only when the status actually changes.
        if (update.StatusId.HasValue && update.StatusId.Value != current.Ticket.StatusId)
        {
            var blockers = await _checklistGuard.FindBlockersAsync(ticketId, update.StatusId.Value, ct);
            if (blockers.Count > 0)
                return FieldUpdatePrecheck.Blocked(blockers);
        }

        // v0.0.42 — status-gate probe. Only when the status actually changes;
        // a same-value update is a no-op the matcher already filters.
        IReadOnlyList<MatchedStatusGate> gates = Array.Empty<MatchedStatusGate>();
        if (update.StatusId.HasValue && update.StatusId.Value != current.Ticket.StatusId)
            gates = await _gates.FindMatchingAsync(ticketId, update.StatusId.Value, ct);

        return new FieldUpdatePrecheck(TicketMutationCheck.Ok, current, gates);
    }

    public Task<TicketDetail?> ApplyFieldUpdateAsync(Guid ticketId, TicketFieldUpdate update, Guid actorUserId, CancellationToken ct)
        => _tickets.UpdateFieldsAsync(ticketId, update, actorUserId, ct);

    public async Task PublishFieldUpdateAsync(
        TicketMutationActor actor, Guid ticketId, TicketFieldUpdate update,
        object auditPayload, TriggerChangeSet? changeSet, CancellationToken ct)
    {
        await _audit.LogAsync(new AuditEvent(
            EventType: "ticket.updated",
            Actor: actor.AuditActor,
            ActorRole: actor.AuditRole,
            Target: ticketId.ToString(),
            ClientIp: actor.ClientIp,
            UserAgent: actor.UserAgent,
            Payload: auditPayload), ct);

        await _notifier.NotifyUpdatedAsync(ticketId, ct);
        await _sla.OnTicketFieldsChangedAsync(ticketId, ct);

        // Trigger evaluator (v0.0.24 Blok 2): any provided field counts as
        // "changed" (a same-value update still trips the Selective check —
        // refining that would cost a pre-update fetch we don't pay elsewhere).
        changeSet ??= new TriggerChangeSet(DeriveChangedFields(update), ArticleAdded: false);
        await _triggers.EvaluateAsync(
            ticketId: ticketId,
            ticketEventId: null,
            activatorKind: TriggerActivatorKind.Action,
            changeSet: changeSet,
            ct: ct);
    }

    public static HashSet<string> DeriveChangedFields(TicketFieldUpdate update)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (update.QueueId.HasValue) changed.Add(TriggerFieldKeys.TicketQueueId);
        if (update.StatusId.HasValue) changed.Add(TriggerFieldKeys.TicketStatusId);
        if (update.PriorityId.HasValue) changed.Add(TriggerFieldKeys.TicketPriorityId);
        if (update.CategoryId.HasValue) changed.Add(TriggerFieldKeys.TicketCategoryId);
        if (update.AssigneeUserId.HasValue || update.ClearAssignee) changed.Add(TriggerFieldKeys.TicketOwnerId);
        if (update.Subject is not null) changed.Add(TriggerFieldKeys.TicketSubject);
        return changed;
    }

    public Task<TicketEvent?> AddEventAsync(Guid ticketId, NewTicketEvent input, CancellationToken ct)
    {
        // The agent UI only sends bodyHtml (the rich-text composer's output).
        // Without a plain-text version, downstream consumers that index or
        // match against body_text (full-text search, trigger `article.body`
        // conditions, mention notifications) silently see null and skip the
        // event. Strip the HTML server-side so the row carries both.
        var bodyText = input.BodyText;
        if (string.IsNullOrWhiteSpace(bodyText) && !string.IsNullOrWhiteSpace(input.BodyHtml))
        {
            var stripped = KbBodyStripper.HtmlToText(input.BodyHtml);
            if (!string.IsNullOrWhiteSpace(stripped)) bodyText = stripped;
        }
        return _tickets.AddEventAsync(ticketId, input with { BodyText = bodyText }, ct);
    }

    public async Task PublishEventAsync(
        TicketMutationActor actor, Guid ticketId, TicketEvent evt, object auditPayload, CancellationToken ct)
    {
        await _audit.LogAsync(new AuditEvent(
            EventType: "ticket.event.added",
            Actor: actor.AuditActor,
            ActorRole: actor.AuditRole,
            Target: ticketId.ToString(),
            ClientIp: actor.ClientIp,
            UserAgent: actor.UserAgent,
            Payload: auditPayload), ct);

        await _notifier.NotifyUpdatedAsync(ticketId, ct);
        await _sla.OnTicketEventAsync(ticketId, evt.EventType, ct);

        // A new article satisfies the trigger Selective short-circuit by
        // itself — no per-field changedFields tracking needed.
        await _triggers.EvaluateAsync(
            ticketId: ticketId,
            ticketEventId: evt.Id,
            activatorKind: TriggerActivatorKind.Action,
            changeSet: TriggerChangeSet.ArticleOnly(),
            ct: ct);
    }
}
