using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Tickets;

/// What one bulk action wants to do to every selected ticket. Every part is
/// optional; the validator requires at least one. The message is posted as
/// an internal Note or a public Comment (no outbound mail — v1 keeps bulk
/// off the customer-mail path; triggers still fire per ticket as they would
/// for a manual change).
public sealed record TicketBulkActionRequest(
    IReadOnlyList<Guid> TicketIds,
    string? MessageHtml,
    bool MessageIsInternal,
    Guid? StatusId,
    Guid? QueueId,
    Guid? PriorityId,
    Guid? AssigneeUserId,
    bool UnassignAssignee)
{
    public bool HasMessage => !string.IsNullOrWhiteSpace(MessageHtml);
    public bool HasFieldChanges =>
        StatusId.HasValue || QueueId.HasValue || PriorityId.HasValue || AssigneeUserId.HasValue || UnassignAssignee;
    public bool HasAnyChange => HasMessage || HasFieldChanges;
}

/// Why a ticket was left untouched. Stable codes — the UI maps them to copy.
public static class TicketBulkSkipReason
{
    public const string NotFound = "not_found";
    public const string NoAccess = "no_access";
    public const string TargetQueueNoAccess = "target_queue_no_access";
    public const string StatusNotInQueueScope = "status_not_in_queue_scope";
    public const string GateRequired = "status_gate_required";
    /// v0.0.103 — a checklist that blocks closing still has required open items.
    public const string ChecklistIncomplete = "checklist_incomplete";
    public const string Failed = "failed";
}

public sealed record TicketBulkSkipped(Guid TicketId, long? Number, string Reason);

public sealed record TicketBulkActionResult(
    Guid BatchId,
    int Total,
    int Succeeded,
    IReadOnlyList<TicketBulkSkipped> Skipped);

/// Thrown when the request violates a hard bound (empty, over the cap,
/// nothing to change). Endpoints translate to 400.
public sealed class TicketBulkActionRejectedException : Exception
{
    public string Code { get; }
    public TicketBulkActionRejectedException(string code, string message) : base(message) => Code = code;
}

public interface ITicketBulkActionService
{
    Task<TicketBulkActionResult> ExecuteAsync(
        TicketMutationActor actor, TicketBulkActionRequest request, CancellationToken ct);
}

/// Runs a bulk action as N independent single-ticket mutations through
/// <see cref="ITicketMutationService"/> (v0.0.102). Per ticket, in the order
/// an agent would do it by hand: post the message first, then apply the
/// field changes. Every ticket runs the full rule set; a ticket that fails a
/// rule is skipped with a reason and the batch continues. A ticket whose
/// status change matches a status gate is skipped too — gates need a per-
/// ticket confirmation the agent must give on the ticket itself. There is
/// no cross-ticket transaction by design: a bulk action is "do this N
/// times", not "all or nothing".
public sealed class TicketBulkActionService : ITicketBulkActionService
{
    /// Absolute ceiling regardless of the admin setting — bounds one
    /// synchronous request's worth of work.
    public const int HardMaxSelection = 500;

    private readonly ITicketMutationService _mutations;
    private readonly ISettingsService _settings;
    private readonly IAuditLogger _audit;
    private readonly ILogger<TicketBulkActionService> _logger;

    public TicketBulkActionService(
        ITicketMutationService mutations,
        ISettingsService settings,
        IAuditLogger audit,
        ILogger<TicketBulkActionService> logger)
    {
        _mutations = mutations;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public async Task<TicketBulkActionResult> ExecuteAsync(
        TicketMutationActor actor, TicketBulkActionRequest request, CancellationToken ct)
    {
        bool enabled;
        try { enabled = await _settings.GetAsync<bool>(SettingKeys.Tickets.BulkActionsEnabled, ct); }
        catch { enabled = true; }
        if (!enabled)
            throw new TicketBulkActionRejectedException("bulk_disabled", "Bulk actions are disabled.");

        if (!request.HasAnyChange)
            throw new TicketBulkActionRejectedException("nothing_to_change", "Provide a message or at least one field change.");
        if (request.AssigneeUserId.HasValue && request.UnassignAssignee)
            throw new TicketBulkActionRejectedException("assignee_conflict", "assigneeUserId and unassignAssignee are mutually exclusive.");

        // Dedupe while preserving order — a double-click in the UI must not
        // post the same note twice on one ticket.
        var ids = request.TicketIds.Distinct().ToList();
        if (ids.Count == 0)
            throw new TicketBulkActionRejectedException("no_tickets", "Select at least one ticket.");

        int max;
        try { max = await _settings.GetAsync<int>(SettingKeys.Tickets.BulkActionsMaxSelection, ct); }
        catch { max = 100; }
        if (max <= 0) max = 100;
        max = Math.Min(max, HardMaxSelection);
        if (ids.Count > max)
            throw new TicketBulkActionRejectedException("too_many", $"A bulk action may touch at most {max} tickets.");

        var batchId = Guid.NewGuid();
        var skipped = new List<TicketBulkSkipped>();
        var succeeded = 0;

        var fieldUpdate = request.HasFieldChanges
            ? new TicketFieldUpdate(
                QueueId: request.QueueId,
                StatusId: request.StatusId,
                PriorityId: request.PriorityId,
                AssigneeUserId: request.AssigneeUserId,
                ClearAssignee: request.UnassignAssignee,
                BulkBatchId: batchId)
            : null;

        var noteMetadata = JsonSerializer.Serialize(new { bulk_batch_id = batchId });

        foreach (var ticketId in ids)
        {
            ct.ThrowIfCancellationRequested();
            long? number = null;
            try
            {
                // One precheck covers both legs: the field-update precheck is
                // a superset of the access precheck, and when there are no
                // field changes it degrades to exactly the access check.
                var pre = await _mutations.PrecheckFieldUpdateAsync(
                    actor, ticketId, fieldUpdate ?? new TicketFieldUpdate(), ct);
                number = pre.Ticket?.Ticket.Number;
                var reason = pre.Check switch
                {
                    TicketMutationCheck.Ok => pre.Gates.Count > 0 ? TicketBulkSkipReason.GateRequired : null,
                    TicketMutationCheck.NotFound => TicketBulkSkipReason.NotFound,
                    TicketMutationCheck.NoAccess => TicketBulkSkipReason.NoAccess,
                    TicketMutationCheck.TargetQueueNoAccess => TicketBulkSkipReason.TargetQueueNoAccess,
                    TicketMutationCheck.StatusNotInQueueScope => TicketBulkSkipReason.StatusNotInQueueScope,
                    TicketMutationCheck.ChecklistIncomplete => TicketBulkSkipReason.ChecklistIncomplete,
                    _ => TicketBulkSkipReason.Failed,
                };
                if (reason is not null)
                {
                    skipped.Add(new TicketBulkSkipped(ticketId, number, reason));
                    continue;
                }

                if (request.HasMessage)
                {
                    var evt = await _mutations.AddEventAsync(ticketId, new NewTicketEvent(
                        EventType: request.MessageIsInternal ? "Note" : "Comment",
                        BodyText: null,
                        BodyHtml: request.MessageHtml,
                        IsInternal: request.MessageIsInternal,
                        AuthorUserId: actor.UserId,
                        MetadataJson: noteMetadata), ct);
                    if (evt is null)
                    {
                        skipped.Add(new TicketBulkSkipped(ticketId, number, TicketBulkSkipReason.NotFound));
                        continue;
                    }
                    await _mutations.PublishEventAsync(actor, ticketId, evt,
                        new { evt.EventType, evt.IsInternal, bulkBatchId = batchId }, ct);
                }

                if (fieldUpdate is not null)
                {
                    var detail = await _mutations.ApplyFieldUpdateAsync(ticketId, fieldUpdate, actor.UserId, ct);
                    if (detail is null)
                    {
                        skipped.Add(new TicketBulkSkipped(ticketId, number, TicketBulkSkipReason.NotFound));
                        continue;
                    }
                    await _mutations.PublishFieldUpdateAsync(actor, ticketId, fieldUpdate,
                        new
                        {
                            request.QueueId,
                            request.StatusId,
                            request.PriorityId,
                            request.AssigneeUserId,
                            request.UnassignAssignee,
                            bulkBatchId = batchId,
                        }, changeSet: null, ct);
                }

                succeeded++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bulk action {BatchId}: ticket {TicketId} failed", batchId, ticketId);
                skipped.Add(new TicketBulkSkipped(ticketId, number, TicketBulkSkipReason.Failed));
            }
        }

        var result = new TicketBulkActionResult(batchId, ids.Count, succeeded, skipped);

        // One batch-level audit row on top of the per-ticket rows the
        // mutation service already wrote, so forensics can see the whole
        // action (who, how many, what) in a single line and correlate the
        // per-ticket rows via the batch id.
        await _audit.LogAsync(new AuditEvent(
            EventType: "ticket.bulk_action",
            Actor: actor.AuditActor,
            ActorRole: actor.AuditRole,
            Target: batchId.ToString(),
            ClientIp: actor.ClientIp,
            UserAgent: actor.UserAgent,
            Payload: new
            {
                batchId,
                total = ids.Count,
                succeeded,
                skipped = skipped.Count,
                skippedReasons = skipped.GroupBy(s => s.Reason).ToDictionary(g => g.Key, g => g.Count()),
                hasMessage = request.HasMessage,
                messageIsInternal = request.MessageIsInternal,
                request.StatusId,
                request.QueueId,
                request.PriorityId,
                request.AssigneeUserId,
                request.UnassignAssignee,
            }), ct);

        return result;
    }
}
