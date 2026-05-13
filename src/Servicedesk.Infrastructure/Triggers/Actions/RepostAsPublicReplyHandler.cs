using System.Text.Json;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Sla;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// Duplicates the triggering article verbatim as a public reply. Intended
/// pairing with an <c>article.is_internal IS true</c> + body-text marker
/// condition so an agent typing an internal note containing a known
/// template-heading also surfaces a customer-visible copy without retyping.
/// The body is already sanitised when it landed in <c>ticket_events</c>
/// (Tiptap → server sanitizer in TicketEndpoints.addEvent), so the handler
/// passes <c>BodyHtml</c> + <c>BodyText</c> through without re-rendering —
/// running it through the trigger-template renderer would HTML-escape the
/// markup and destroy the formatting the action exists to preserve.
internal sealed class RepostAsPublicReplyHandler : ITriggerActionHandler
{
    private readonly ITicketRepository _tickets;
    private readonly ISlaEngine _sla;

    public RepostAsPublicReplyHandler(ITicketRepository tickets, ISlaEngine sla)
    {
        _tickets = tickets;
        _sla = sla;
    }

    public string Kind => "repost_as_public_reply";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        var src = ctx.TriggeringEvent;
        if (src is null)
            return TriggerActionResult.Failed(Kind, "Requires a triggering article to repost.");
        if (!src.IsInternal)
            return TriggerActionResult.NoOp(Kind, new { reason = "triggering article is already public" });
        if (string.IsNullOrWhiteSpace(src.BodyHtml) && string.IsNullOrWhiteSpace(src.BodyText))
            return TriggerActionResult.Failed(Kind, "Triggering article has no body to repost.");

        var metadata = TriggerEventMetadata.SystemNote(ctx.TriggerId, new Dictionary<string, object?>
        {
            ["from_event_id"] = src.Id,
        });

        // Always emit as Comment, not as the triggering article's type.
        // The timeline UI treats a public-visible item as Comment + !internal;
        // emitting a Note with is_internal=false renders with the Note icon
        // + "Internal note" label and looks identical to an internal note,
        // which is exactly the opposite of what this action exists to do.
        var evt = await _tickets.AddEventAsync(ctx.TicketId, new NewTicketEvent(
            EventType: TicketEventType.Comment.ToString(),
            BodyText: src.BodyText,
            BodyHtml: src.BodyHtml,
            IsInternal: false,
            AuthorUserId: null,
            AuthorContactId: null,
            MetadataJson: metadata), ct);

        if (evt is null)
            return TriggerActionResult.Failed(Kind, "Ticket vanished mid-insert.");

        await _sla.OnTicketEventAsync(ctx.TicketId, evt.EventType, ct);

        return TriggerActionResult.Applied(Kind, new { eventId = evt.Id, fromEventId = src.Id, isInternal = false });
    }
}
