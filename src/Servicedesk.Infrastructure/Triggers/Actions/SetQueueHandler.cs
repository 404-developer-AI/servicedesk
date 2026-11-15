using System.Text.Json;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetQueueHandler : ITriggerActionHandler
{
    private readonly SystemFieldMutator _mutator;
    private readonly ISettingsService _settings;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ITicketRepository _tickets;

    public SetQueueHandler(
        SystemFieldMutator mutator,
        ISettingsService settings,
        ITaxonomyRepository taxonomy,
        ITicketRepository tickets)
    {
        _mutator = mutator;
        _settings = settings;
        _taxonomy = taxonomy;
        _tickets = tickets;
    }

    public string Kind => "set_queue";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!ActionJson.TryReadGuid(actionJson, "queue_id", out var newQueueId))
            return TriggerActionResult.Failed(Kind, "Action is missing required string 'queue_id'.");

        // v0.0.105 — project tickets are pinned to the configured project
        // queue: a trigger may not move one elsewhere. Same rule the manual
        // and bulk paths enforce in TicketMutationService.
        if (ctx.Ticket.IsProject && newQueueId != ctx.Ticket.QueueId
            && await ProjectQueuePin.GetPinnedQueueIdAsync(_settings, ct) is Guid pinned
            && newQueueId != pinned)
        {
            return TriggerActionResult.NoOp(Kind,
                new { reason = "Suppressed: project tickets are pinned to the project queue." });
        }

        var outcome = await _mutator.ChangeFieldAsync(
            ctx.TicketId,
            SystemFieldDescriptor.Queue,
            currentValue: ctx.Ticket.QueueId,
            newValue: newQueueId,
            triggerId: ctx.TriggerId,
            ct: ct);

        Guid? statusFlippedTo = null;
        if (outcome.Status == FieldChangeStatus.Applied)
            statusFlippedTo = await FlipStatusIntoQueueScopeAsync(ctx, newQueueId, ct);

        return outcome.Status switch
        {
            FieldChangeStatus.Applied => TriggerActionResult.Applied(Kind, new
            {
                column = outcome.Column,
                from = outcome.From,
                to = outcome.To,
                fromName = outcome.FromName,
                toName = outcome.ToName,
                statusFlippedTo,
            }),
            FieldChangeStatus.NoOp => TriggerActionResult.NoOp(Kind, new { column = outcome.Column }),
            _ => TriggerActionResult.Failed(Kind, outcome.Reason ?? "Unknown failure."),
        };
    }

    /// The agent path's queue-change rule (TicketRepository.UpdateFieldsAsync):
    /// when the new queue has an allowed-status list and a default status, a
    /// status outside that list flips to the default. Recorded as its own
    /// trigger status change so the timeline shows why the status moved.
    /// Returns the new status id, or null when nothing had to change.
    private async Task<Guid?> FlipStatusIntoQueueScopeAsync(
        TriggerEvaluationContext ctx, Guid newQueueId, CancellationToken ct)
    {
        var queue = await _taxonomy.GetQueueAsync(newQueueId, ct);
        if (queue?.AllowedStatusIds is not { Count: > 0 } allowed || queue.DefaultStatusId is not Guid fallback)
            return null;

        // Fresh read: an earlier action of the same trigger may already have
        // changed the status, so the context snapshot can be stale.
        var current = await _tickets.GetCoreAsync(ctx.TicketId, ct);
        if (current is null || allowed.Contains(current.StatusId))
            return null;

        var flip = await _mutator.ChangeStatusAsync(
            ctx.TicketId, current.StatusId, fallback, ctx.TriggerId, ct);
        return flip.Status == FieldChangeStatus.Applied ? fallback : null;
    }
}
