using System.Text.Json;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class AddPublicNoteHandler : ITriggerActionHandler
{
    private readonly ITicketRepository _tickets;
    private readonly ITriggerTemplateRenderer _renderer;
    private readonly ISlaEngine _sla;

    public AddPublicNoteHandler(ITicketRepository tickets, ITriggerTemplateRenderer renderer, ISlaEngine sla)
    {
        _tickets = tickets;
        _renderer = renderer;
        _sla = sla;
    }

    public string Kind => "add_public_note";

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        var hasHtml = ActionJson.TryReadString(actionJson, "body_html", out var bodyHtml);
        var hasText = ActionJson.TryReadString(actionJson, "body_text", out var bodyText);
        if (!hasHtml && !hasText)
            return TriggerActionResult.Failed(Kind, "Action requires 'body_html' or 'body_text'.");

        if (ctx.RenderContext is { } rc)
        {
            if (hasHtml) bodyHtml = _renderer.Render(bodyHtml, TemplateEscapeMode.Html, rc);
            if (hasText) bodyText = _renderer.Render(bodyText, TemplateEscapeMode.PlainText, rc);
        }

        var metadata = TriggerEventMetadata.SystemNote(ctx.TriggerId);
        var evt = await _tickets.AddEventAsync(ctx.TicketId, new NewTicketEvent(
            EventType: TicketEventType.Note.ToString(),
            BodyText: hasText ? bodyText : null,
            BodyHtml: hasHtml ? bodyHtml : null,
            IsInternal: false,
            AuthorUserId: null,
            AuthorContactId: null,
            MetadataJson: metadata), ct);

        if (evt is null)
            return TriggerActionResult.Failed(Kind, "Ticket vanished mid-insert.");

        // Public notes are visible to the customer and count as agent
        // touches in the SLA engine (FR-detection + Comment handling),
        // mirroring how OutboundMailService and AddTicketEvent feed it.
        await _sla.OnTicketEventAsync(ctx.TicketId, evt.EventType, ct);

        return TriggerActionResult.Applied(Kind, new { eventId = evt.Id, isInternal = false });
    }
}
