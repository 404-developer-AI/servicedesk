using System.Text.Json;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Sla;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.Actions;

internal sealed class AddInternalNoteHandler : ITriggerActionHandler
{
    private readonly ITicketRepository _tickets;
    private readonly ITriggerTemplateRenderer _renderer;
    private readonly ISlaEngine _sla;

    public AddInternalNoteHandler(ITicketRepository tickets, ITriggerTemplateRenderer renderer, ISlaEngine sla)
    {
        _tickets = tickets;
        _renderer = renderer;
        _sla = sla;
    }

    public string Kind => "add_internal_note";

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
            IsInternal: true,
            AuthorUserId: null,
            AuthorContactId: null,
            MetadataJson: metadata), ct);

        if (evt is null)
            return TriggerActionResult.Failed(Kind, "Ticket vanished mid-insert.");

        // Internal notes don't reset SLA on their own (Note isn't a
        // first-response signal), but the engine still recomputes the
        // ticket's stage so any downstream policy that conditions on
        // event-presence stays consistent with the agent-typed path.
        await _sla.OnTicketEventAsync(ctx.TicketId, evt.EventType, ct);

        return TriggerActionResult.Applied(Kind, new { eventId = evt.Id, isInternal = true });
    }
}
