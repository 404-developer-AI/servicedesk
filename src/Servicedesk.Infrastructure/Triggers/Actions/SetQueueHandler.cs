using System.Text.Json;
using Servicedesk.Infrastructure.Forms;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class SetQueueHandler : ITriggerActionHandler
{
    private readonly TriggerFieldWrite _write;
    private readonly ITaxonomyRepository _taxonomy;

    public SetQueueHandler(TriggerFieldWrite write, ITaxonomyRepository taxonomy)
    {
        _write = write;
        _taxonomy = taxonomy;
    }

    public string Kind => "set_queue";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        Guid newQueueId;
        if (FormFieldActionSource.HasFormField(actionJson))
        {
            // v0.1.58 — the queue picked on the form. The filler's access to
            // it was checked when the form was saved; it must still exist.
            var source = FormFieldActionSource.Resolve(actionJson, ctx, FormFieldType.Queue);
            if (source.Error is not null) return TriggerActionResult.Failed(Kind, source.Error);
            if (source.EmptyReason is not null) return TriggerActionResult.NoOp(Kind, new { reason = source.EmptyReason });
            newQueueId = source.Field!.RefId!.Value;
            if (await _taxonomy.GetQueueAsync(newQueueId, ct) is not { IsActive: true })
                return TriggerActionResult.Failed(Kind, $"The queue picked on the form ({source.Field.Display}) no longer exists or is inactive.");
        }
        else if (!ActionJson.TryReadGuid(actionJson, "queue_id", out newQueueId))
            return TriggerActionResult.Failed(Kind, "Action is missing required string 'queue_id'.");

        // The shared write: project tickets stay in the pinned project queue
        // (a logged no-op), and a status outside the new queue's allowed
        // list flips to its default — the same rules as an agent's move.
        return await _write.ApplyAsync(Kind, "queue_id", new TicketFieldUpdate(QueueId: newQueueId), ctx, ct);
    }
}
