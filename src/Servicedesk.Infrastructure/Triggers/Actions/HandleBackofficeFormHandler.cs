using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Forms;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.Actions;

/// v0.1.61 — the <c>handle_backoffice_form</c> action: marks every open
/// back-office form of the chosen templates on this ticket as handled, with
/// an optional rendered note. Payload:
/// <code>{ "kind": "handle_backoffice_form", "template_ids": ["…"], "note": "Planned on #{form.field.from}" }</code>
/// Typical use: the planning form's save trigger handles the open
/// intervention request, wherever the planning form was filled in. It is the
/// only automatic way a form gets handled, and it is configured by the admin
/// — nothing is hard-coded. Each form is one guarded UPDATE + log row (the
/// same transition as a manual handle), so a form handled by a person at the
/// same moment is simply skipped. A form handled here does NOT start a
/// "handled" back-office trigger pass — that would let triggers start each
/// other; manual handling still does.
public static class HandleBackofficeFormAction
{
    public const string Kind = "handle_backoffice_form";
    public const int MaxTemplates = 20;
    public const int NoteTemplateMax = 2000;

    public sealed record Parsed(IReadOnlyList<Guid> TemplateIds, string Note);

    public static string? Validate(JsonElement action) => TryParse(action, out _, out var err) ? null : err;

    public static bool TryParse(JsonElement action, out Parsed parsed, out string? error)
    {
        parsed = new Parsed(Array.Empty<Guid>(), string.Empty);
        error = null;
        if (!action.TryGetProperty("template_ids", out var t) || t.ValueKind != JsonValueKind.Array)
        {
            error = "requires 'template_ids' (the form templates whose open back-office forms are handled).";
            return false;
        }
        var ids = new List<Guid>();
        foreach (var el in t.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String || !Guid.TryParse(el.GetString(), out var g) || g == Guid.Empty)
            {
                error = "'template_ids' must contain form template ids.";
                return false;
            }
            if (!ids.Contains(g)) ids.Add(g);
        }
        if (ids.Count == 0) { error = "choose at least one form template."; return false; }
        if (ids.Count > MaxTemplates) { error = $"at most {MaxTemplates} form templates."; return false; }

        var note = action.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String
            ? (n.GetString() ?? string.Empty).Trim() : string.Empty;
        if (note.Length > NoteTemplateMax) { error = $"'note' is longer than {NoteTemplateMax} characters."; return false; }

        parsed = new Parsed(ids, note);
        return true;
    }

    internal static string RenderNote(string template, TriggerEvaluationContext ctx, ITriggerTemplateRenderer renderer)
    {
        if (template.Length == 0) return string.Empty;
        var text = ctx.RenderContext is { } rc ? renderer.Render(template, TemplateEscapeMode.PlainText, rc) : template;
        text = text.Replace("\r\n", "\n").Trim();
        return text.Length > FormLimits.BackofficeNoteMax ? text[..FormLimits.BackofficeNoteMax] : text;
    }

    /// Who the log row names: the person whose step started this pass (the
    /// form saver, else the author of the triggering article); null = system.
    internal static Guid? Actor(TriggerEvaluationContext ctx)
        => ctx.ChangeSet.FormEvent?.ActorUserId ?? ctx.TriggeringEvent?.AuthorUserId;
}

internal sealed class HandleBackofficeFormHandler : ITriggerActionHandler
{
    private readonly IBackofficeFormRepository _repo;
    private readonly IBackofficeFormNotifier _notifier;
    private readonly ITicketListNotifier _tickets;
    private readonly ITriggerTemplateRenderer _renderer;
    private readonly ILogger<HandleBackofficeFormHandler> _logger;

    public HandleBackofficeFormHandler(
        IBackofficeFormRepository repo, IBackofficeFormNotifier notifier, ITicketListNotifier tickets,
        ITriggerTemplateRenderer renderer, ILogger<HandleBackofficeFormHandler> logger)
    {
        _repo = repo;
        _notifier = notifier;
        _tickets = tickets;
        _renderer = renderer;
        _logger = logger;
    }

    public string Kind => HandleBackofficeFormAction.Kind;

    public async Task<TriggerActionResult> ApplyAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!HandleBackofficeFormAction.TryParse(actionJson, out var p, out var err))
            return TriggerActionResult.Failed(Kind, err ?? "Invalid action.");

        var note = HandleBackofficeFormAction.RenderNote(p.Note, ctx, _renderer);
        var handled = await _repo.HandleOpenOnTicketAsync(
            ctx.TicketId, p.TemplateIds, HandleBackofficeFormAction.Actor(ctx), note, ct);
        if (handled.Count == 0)
            return TriggerActionResult.NoOp(Kind, new { reason = "no open back-office form of these templates on the ticket" });

        try { await _notifier.BackofficeFormsChangedAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Back office forms push failed for ticket {TicketId}.", ctx.TicketId); }
        try { await _tickets.NotifyUpdatedAsync(ctx.TicketId, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Ticket refresh push failed for ticket {TicketId}.", ctx.TicketId); }

        return TriggerActionResult.Applied(Kind, new { forms = handled.Count, formIds = handled });
    }
}

internal sealed class HandleBackofficeFormPreviewer : ITriggerActionPreviewer
{
    private readonly IBackofficeFormRepository _repo;
    private readonly ITriggerTemplateRenderer _renderer;

    public HandleBackofficeFormPreviewer(IBackofficeFormRepository repo, ITriggerTemplateRenderer renderer)
    {
        _repo = repo;
        _renderer = renderer;
    }

    public string Kind => HandleBackofficeFormAction.Kind;

    public async Task<TriggerActionPreviewResult> PreviewAsync(JsonElement actionJson, TriggerEvaluationContext ctx, CancellationToken ct)
    {
        if (!HandleBackofficeFormAction.TryParse(actionJson, out var p, out var err))
            return TriggerActionPreviewResult.Failed(Kind, err ?? "Invalid action.");
        var open = await _repo.CountOpenOnTicketAsync(ctx.TicketId, p.TemplateIds, ct);
        if (open == 0)
            return TriggerActionPreviewResult.WouldNoOp(Kind, new { reason = "no open back-office form of these templates on the ticket" });
        return TriggerActionPreviewResult.WouldApply(Kind, new
        {
            forms = open,
            note = HandleBackofficeFormAction.RenderNote(p.Note, ctx, _renderer),
        });
    }
}
