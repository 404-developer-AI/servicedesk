using System.Text.Json;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetQueueHandler : ITriggerActionHandler
{
    private readonly SystemFieldMutator _mutator;
    private readonly ISettingsService _settings;

    public SetQueueHandler(SystemFieldMutator mutator, ISettingsService settings)
    {
        _mutator = mutator;
        _settings = settings;
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

        return outcome.Status switch
        {
            FieldChangeStatus.Applied => TriggerActionResult.Applied(Kind, new
            {
                column = outcome.Column,
                from = outcome.From,
                to = outcome.To,
                fromName = outcome.FromName,
                toName = outcome.ToName,
            }),
            FieldChangeStatus.NoOp => TriggerActionResult.NoOp(Kind, new { column = outcome.Column }),
            _ => TriggerActionResult.Failed(Kind, outcome.Reason ?? "Unknown failure."),
        };
    }
}
