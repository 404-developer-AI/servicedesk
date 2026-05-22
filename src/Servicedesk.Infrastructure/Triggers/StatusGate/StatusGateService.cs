using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.StatusGate;

/// Loads gate:status_change triggers, filters by source/target status,
/// evaluates the regular condition tree against the current ticket
/// snapshot, and returns the prompt payload the UI needs to render a
/// confirmation dialog. Used by both the matching endpoint (read-only)
/// and the ticket-PATCH enforcement path; the second path also drives
/// note rendering via <see cref="RenderNoteAsync"/> so the confirmed
/// answer lands on the timeline.
///
/// Fails closed: any malformed row (bad JSON, missing payload, unknown
/// status id) is logged and skipped — admins see no dialog rather than
/// a broken one. The endpoint still applies the status change, so a
/// misconfigured gate cannot wedge the workflow.
public sealed class StatusGateService : IStatusGateService
{
    private readonly ITriggerRepository _triggers;
    private readonly ITicketRepository _tickets;
    private readonly ITriggerConditionMatcher _matcher;
    private readonly ITriggerRenderContextFactory _renderFactory;
    private readonly ITriggerTemplateRenderer _renderer;
    private readonly IUserService _users;
    private readonly ILogger<StatusGateService> _logger;

    public StatusGateService(
        ITriggerRepository triggers,
        ITicketRepository tickets,
        ITriggerConditionMatcher matcher,
        ITriggerRenderContextFactory renderFactory,
        ITriggerTemplateRenderer renderer,
        IUserService users,
        ILogger<StatusGateService> logger)
    {
        _triggers = triggers;
        _tickets = tickets;
        _matcher = matcher;
        _renderFactory = renderFactory;
        _renderer = renderer;
        _users = users;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MatchedStatusGate>> FindMatchingAsync(
        Guid ticketId, Guid targetStatusId, CancellationToken ct)
    {
        var detail = await _tickets.GetByIdAsync(ticketId, ct);
        if (detail is null) return Array.Empty<MatchedStatusGate>();

        // No gate ever applies when the target equals the current status —
        // a no-op status change must not surface a confirmation dialog.
        if (detail.Ticket.StatusId == targetStatusId) return Array.Empty<MatchedStatusGate>();

        IReadOnlyList<TriggerRow> rows;
        try
        {
            rows = await _triggers.LoadActiveAsync(TriggerActivatorKind.Gate, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StatusGateService failed to load gate triggers for ticket {TicketId}.", ticketId);
            return Array.Empty<MatchedStatusGate>();
        }
        if (rows.Count == 0) return Array.Empty<MatchedStatusGate>();

        var result = new List<MatchedStatusGate>(rows.Count);
        foreach (var row in rows)
        {
            if (!string.Equals(row.ActivatorMode, "status_change", StringComparison.Ordinal))
                continue;

            MatchedStatusGate? gate;
            try { gate = ProjectGate(row); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gate trigger {TriggerId} has a malformed prompt_confirm payload; skipping.", row.Id);
                continue;
            }
            if (gate is null) continue;

            if (gate.ToStatusId != targetStatusId) continue;
            if (gate.FromStatusId.HasValue && gate.FromStatusId.Value != detail.Ticket.StatusId)
                continue;

            JsonDocument condDoc;
            try { condDoc = JsonDocument.Parse(row.ConditionsJson); }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Gate trigger {TriggerId} has malformed conditions JSON; skipping.", row.Id);
                continue;
            }

            try
            {
                var ctx = new TriggerEvaluationContext(
                    TicketId: ticketId,
                    Ticket: detail.Ticket,
                    TriggeringEvent: null,
                    ChangeSet: new TriggerChangeSet(
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            TriggerFieldKeys.TicketStatusId,
                        },
                        ArticleAdded: false),
                    UtcNow: DateTime.UtcNow);
                if (!_matcher.Matches(condDoc.RootElement, ctx)) continue;
            }
            finally { condDoc.Dispose(); }

            result.Add(gate);
        }
        return result;
    }

    public async Task<string?> RenderNoteAsync(
        MatchedStatusGate gate,
        Guid ticketId,
        Guid agentUserId,
        IReadOnlyDictionary<string, string>? answers,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gate.NoteTemplate)) return null;

        var detail = await _tickets.GetByIdAsync(ticketId, ct);
        if (detail is null) return null;

        var ctx = new TriggerEvaluationContext(
            TicketId: ticketId,
            Ticket: detail.Ticket,
            TriggeringEvent: null,
            ChangeSet: new TriggerChangeSet(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                ArticleAdded: false),
            UtcNow: DateTime.UtcNow);

        var baseCtx = await _renderFactory.BuildAsync(ctx, triggerLocale: null, triggerTimeZone: null, ct);
        var agent = await _users.FindByIdAsync(agentUserId, ct);

        var merged = new Dictionary<string, string?>(baseCtx.StringValues, StringComparer.Ordinal)
        {
            ["agent.email"] = agent?.Email,
            ["agent.name"] = agent?.Email,
        };

        // Per-question tokens — admin references each answer via
        // #{prompt.<key>}. Missing entries resolve to empty string via
        // the renderer's default-fallback.
        if (answers is not null)
        {
            foreach (var (key, value) in answers)
            {
                merged[$"prompt.{key}"] = value;
            }
        }
        // Legacy alias: gates migrated from the v0.0.42-pre shape carry a
        // synthesized text question with key="answer", so existing
        // templates using #{prompt.answer} keep resolving. When the
        // current gate has no "answer" key, fall back to the first
        // submitted value so a hand-rolled template doesn't break.
        if (!merged.ContainsKey("prompt.answer"))
        {
            merged["prompt.answer"] = answers?.Values.FirstOrDefault() ?? string.Empty;
        }

        var renderCtx = new TriggerRenderContext
        {
            StringValues = merged,
            DateTimeValues = baseCtx.DateTimeValues,
            DefaultTimeZoneId = baseCtx.DefaultTimeZoneId,
            Culture = baseCtx.Culture,
        };

        // The note ships as HTML — the timeline renders Note events via the
        // standard rich-text path. PlainText mode would swallow the line
        // breaks an admin types into the template.
        var rendered = _renderer.Render(gate.NoteTemplate, TemplateEscapeMode.Html, renderCtx);
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
    }

    private static MatchedStatusGate? ProjectGate(TriggerRow row)
    {
        if (string.IsNullOrWhiteSpace(row.ActionsJson)) return null;
        using var doc = JsonDocument.Parse(row.ActionsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var action in doc.RootElement.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object) continue;
            if (!action.TryGetProperty("kind", out var kindEl)
                || kindEl.ValueKind != JsonValueKind.String
                || kindEl.GetString() != "prompt_confirm")
                continue;
            return ProjectFromAction(row, action);
        }
        return null;
    }

    private static MatchedStatusGate? ProjectFromAction(TriggerRow row, JsonElement action)
    {
        if (!action.TryGetProperty("to_status_id", out var toEl)
            || toEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(toEl.GetString(), out var toStatus))
            return null;

        Guid? fromStatus = null;
        if (action.TryGetProperty("from_status_id", out var fromEl)
            && fromEl.ValueKind == JsonValueKind.String
            && Guid.TryParse(fromEl.GetString(), out var parsedFrom))
        {
            fromStatus = parsedFrom;
        }

        string title = TryStr(action, "title") ?? string.Empty;
        bool showMessage = !action.TryGetProperty("show_message", out var smEl)
            ? !string.IsNullOrWhiteSpace(TryStr(action, "message"))
            : smEl.ValueKind == JsonValueKind.True;
        string? message = showMessage ? TryStr(action, "message") : null;
        if (string.IsNullOrWhiteSpace(message)) message = null;

        string confirmLabel = TryStr(action, "confirm_label") ?? "Confirm";
        string cancelLabel = TryStr(action, "cancel_label") ?? "Cancel";
        string visibility = TryStr(action, "note_visibility") ?? "internal";
        if (visibility != "internal" && visibility != "public") visibility = "internal";
        string noteTemplate = TryStr(action, "note_template") ?? string.Empty;

        var questions = new List<GateQuestion>();
        if (action.TryGetProperty("questions", out var qArr)
            && qArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qArr.EnumerateArray())
            {
                var parsed = ProjectQuestion(q);
                if (parsed is not null) questions.Add(parsed);
            }
        }
        else
        {
            // Legacy pre-questions shape — synthesize one question from
            // prompt_label / prompt_required so the dialog and templating
            // path keep working until the admin re-saves the gate.
            var legacyLabel = TryStr(action, "prompt_label");
            if (!string.IsNullOrWhiteSpace(legacyLabel))
            {
                bool legacyRequired = action.TryGetProperty("prompt_required", out var pr)
                    && pr.ValueKind == JsonValueKind.True;
                questions.Add(new GateQuestion(
                    Key: "answer",
                    Type: "text",
                    Label: legacyLabel!,
                    Required: legacyRequired,
                    YesLabel: null,
                    NoLabel: null));
            }
        }

        return new MatchedStatusGate(
            TriggerId: row.Id,
            Name: row.Name,
            Title: title,
            Message: message,
            Questions: questions,
            ConfirmLabel: confirmLabel,
            CancelLabel: cancelLabel,
            NoteVisibility: visibility,
            NoteTemplate: noteTemplate,
            ToStatusId: toStatus,
            FromStatusId: fromStatus);
    }

    private static GateQuestion? ProjectQuestion(JsonElement q)
    {
        if (q.ValueKind != JsonValueKind.Object) return null;
        var key = TryStr(q, "key");
        if (string.IsNullOrWhiteSpace(key)) return null;
        var label = TryStr(q, "label") ?? string.Empty;
        var type = TryStr(q, "type") ?? "text";
        if (type != "text" && type != "yesno") return null;
        bool required = q.TryGetProperty("required", out var reqEl)
            && reqEl.ValueKind == JsonValueKind.True;
        string? yesLabel = type == "yesno" ? TryStr(q, "yes_label") : null;
        string? noLabel = type == "yesno" ? TryStr(q, "no_label") : null;
        if (string.IsNullOrWhiteSpace(yesLabel)) yesLabel = null;
        if (string.IsNullOrWhiteSpace(noLabel)) noLabel = null;
        return new GateQuestion(key!, type, label, required, yesLabel, noLabel);
    }

    private static string? TryStr(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
