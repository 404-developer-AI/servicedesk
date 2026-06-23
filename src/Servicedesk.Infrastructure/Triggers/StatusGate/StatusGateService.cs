using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Companies;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.StatusGate;

/// Loads gate:status_change triggers, filters by source/target status,
/// evaluates the regular condition tree against the current ticket
/// snapshot, and returns the dialog payload the UI needs to render a
/// confirmation. Used by both the matching endpoint (read-only) and the
/// ticket-PATCH enforcement path; the second path also drives note
/// rendering via <see cref="RenderNoteAsync"/> so the confirmed answer
/// lands on the timeline.
///
/// Two gate kinds in v0.0.42:
///   * <c>prompt_confirm</c> — confirmation questions; the gate always
///     matches when its conditions pass.
///   * <c>require_contact_company</c> — hard-block when the ticket's
///     requester has zero rows in <c>contact_companies</c>. The matcher
///     loads the requester's link count and skips the gate when at least
///     one link already exists, so an admin-side fix between probe and
///     PATCH is honoured without surfacing a stale dialog.
///
/// Fails closed: any malformed row (bad JSON, missing payload, unknown
/// status id) is logged and skipped — admins see no dialog rather than
/// a broken one. The endpoint still applies the status change, so a
/// misconfigured gate cannot wedge the workflow.
public sealed class StatusGateService : IStatusGateService
{
    private readonly ITriggerRepository _triggers;
    private readonly ITicketRepository _tickets;
    private readonly ICompanyRepository _companies;
    private readonly ITriggerConditionMatcher _matcher;
    private readonly ITriggerRenderContextFactory _renderFactory;
    private readonly ITriggerTemplateRenderer _renderer;
    private readonly IUserService _users;
    private readonly IAdsolutOrderRepository _orders;
    private readonly ILogger<StatusGateService> _logger;

    public StatusGateService(
        ITriggerRepository triggers,
        ITicketRepository tickets,
        ICompanyRepository companies,
        ITriggerConditionMatcher matcher,
        ITriggerRenderContextFactory renderFactory,
        ITriggerTemplateRenderer renderer,
        IUserService users,
        IAdsolutOrderRepository orders,
        ILogger<StatusGateService> logger)
    {
        _triggers = triggers;
        _tickets = tickets;
        _companies = companies;
        _matcher = matcher;
        _renderFactory = renderFactory;
        _renderer = renderer;
        _users = users;
        _orders = orders;
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

        // Lazy lookup of the requester's contact + link count. Two
        // require_contact_company gates on the same ticket share one
        // round-trip; tickets with no such gate skip the lookup entirely.
        Contact? cachedRequester = null;
        bool requesterLoaded = false;
        int? cachedLinkCount = null;

        // Lazy linked-order existence — only queried when a gate's
        // conditions actually reference ticket.has_linked_order, and shared
        // across every such gate on this same status change.
        bool? cachedHasOrder = null;
        async Task<bool> GetHasLinkedOrderAsync()
        {
            cachedHasOrder ??= await _orders.HasLinkedOrderAsync(detail.Ticket.Id, ct);
            return cachedHasOrder.Value;
        }

        async Task<(Contact? Contact, int LinkCount)> GetRequesterAsync()
        {
            if (!requesterLoaded)
            {
                cachedRequester = await _companies.GetContactAsync(detail.Ticket.RequesterContactId, ct);
                requesterLoaded = true;
            }
            if (cachedRequester is null) return (null, 0);
            if (!cachedLinkCount.HasValue)
            {
                var links = await _companies.ListContactLinksAsync(cachedRequester.Id, ct);
                cachedLinkCount = links.Count;
            }
            return (cachedRequester, cachedLinkCount.Value);
        }

        var result = new List<MatchedStatusGate>(rows.Count);
        foreach (var row in rows)
        {
            if (!string.Equals(row.ActivatorMode, "status_change", StringComparison.Ordinal))
                continue;

            MatchedStatusGate? gate;
            try { gate = ProjectGate(row); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gate trigger {TriggerId} has a malformed gate-action payload; skipping.", row.Id);
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
                bool hasLinkedOrder = false;
                if (_matcher.ReferencedFields(condDoc.RootElement)
                    .Contains(TriggerFieldKeys.TicketHasLinkedOrder))
                {
                    hasLinkedOrder = await GetHasLinkedOrderAsync();
                }
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
                    UtcNow: DateTime.UtcNow)
                {
                    HasLinkedOrder = hasLinkedOrder,
                };
                if (!_matcher.Matches(condDoc.RootElement, ctx)) continue;
            }
            finally { condDoc.Dispose(); }

            // Kind-specific match guard. require_contact_company only
            // surfaces when the requester has zero existing links — a
            // contact who already has any company link satisfies the
            // gate by construction and is allowed through.
            if (gate.Kind == "contact_company_link")
            {
                var (contact, linkCount) = await GetRequesterAsync();
                if (contact is null) continue;
                if (linkCount > 0) continue;
                var displayName = BuildContactDisplayName(contact);
                gate = gate with
                {
                    RequesterContactId = contact.Id,
                    RequesterDisplayName = displayName,
                };
            }

            result.Add(gate);
        }
        return result;
    }

    public async Task<string?> RenderNoteAsync(
        MatchedStatusGate gate,
        Guid ticketId,
        Guid agentUserId,
        IReadOnlyDictionary<string, string>? answers,
        IReadOnlyDictionary<string, string>? extras,
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

        // Verbatim extras — e.g. require_contact_company synthesizes
        // company.name / company.code / company.role from the just-
        // created link so the note template can reference them without
        // going through the prompt prefix.
        if (extras is not null)
        {
            foreach (var (key, value) in extras)
            {
                merged[key] = value;
            }
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
                || kindEl.ValueKind != JsonValueKind.String)
                continue;
            return kindEl.GetString() switch
            {
                "prompt_confirm" => ProjectFromPromptConfirm(row, action),
                "require_contact_company" => ProjectFromRequireContactCompany(row, action),
                _ => null,
            };
        }
        return null;
    }

    private static MatchedStatusGate? ProjectFromPromptConfirm(TriggerRow row, JsonElement action)
    {
        if (!TryReadStatusPair(action, out var toStatus, out var fromStatus))
            return null;

        string title = TryStr(action, "title") ?? string.Empty;
        bool showMessage = !action.TryGetProperty("show_message", out var smEl)
            ? !string.IsNullOrWhiteSpace(TryStr(action, "message"))
            : smEl.ValueKind == JsonValueKind.True;
        string? message = showMessage ? TryStr(action, "message") : null;
        if (string.IsNullOrWhiteSpace(message)) message = null;

        string confirmLabel = TryStr(action, "confirm_label") ?? "Confirm";
        string cancelLabel = TryStr(action, "cancel_label") ?? "Cancel";
        string visibility = NormalizeVisibility(action);
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
            Kind: "prompt_confirm",
            Title: title,
            Message: message,
            Questions: questions,
            ConfirmLabel: confirmLabel,
            CancelLabel: cancelLabel,
            NoteVisibility: visibility,
            NoteTemplate: noteTemplate,
            ToStatusId: toStatus,
            FromStatusId: fromStatus,
            RequesterContactId: null,
            RequesterDisplayName: null);
    }

    private static MatchedStatusGate? ProjectFromRequireContactCompany(TriggerRow row, JsonElement action)
    {
        if (!TryReadStatusPair(action, out var toStatus, out var fromStatus))
            return null;

        string title = TryStr(action, "title") ?? string.Empty;
        bool showMessage = !action.TryGetProperty("show_message", out var smEl)
            ? !string.IsNullOrWhiteSpace(TryStr(action, "message"))
            : smEl.ValueKind == JsonValueKind.True;
        string? message = showMessage ? TryStr(action, "message") : null;
        if (string.IsNullOrWhiteSpace(message)) message = null;

        string confirmLabel = TryStr(action, "confirm_label") ?? "Link company";
        string cancelLabel = TryStr(action, "cancel_label") ?? "Cancel";
        string visibility = NormalizeVisibility(action);
        string noteTemplate = TryStr(action, "note_template") ?? string.Empty;

        return new MatchedStatusGate(
            TriggerId: row.Id,
            Name: row.Name,
            Kind: "contact_company_link",
            Title: title,
            Message: message,
            Questions: Array.Empty<GateQuestion>(),
            ConfirmLabel: confirmLabel,
            CancelLabel: cancelLabel,
            NoteVisibility: visibility,
            NoteTemplate: noteTemplate,
            ToStatusId: toStatus,
            FromStatusId: fromStatus,
            // Populated in FindMatchingAsync once the requester is loaded.
            RequesterContactId: null,
            RequesterDisplayName: null);
    }

    private static bool TryReadStatusPair(JsonElement action, out Guid toStatus, out Guid? fromStatus)
    {
        toStatus = Guid.Empty;
        fromStatus = null;
        if (!action.TryGetProperty("to_status_id", out var toEl)
            || toEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(toEl.GetString(), out toStatus))
            return false;
        if (action.TryGetProperty("from_status_id", out var fromEl)
            && fromEl.ValueKind == JsonValueKind.String
            && Guid.TryParse(fromEl.GetString(), out var parsedFrom))
        {
            fromStatus = parsedFrom;
        }
        return true;
    }

    private static string NormalizeVisibility(JsonElement action)
    {
        string visibility = TryStr(action, "note_visibility") ?? "internal";
        return visibility is "internal" or "public" ? visibility : "internal";
    }

    private static GateQuestion? ProjectQuestion(JsonElement q)
    {
        if (q.ValueKind != JsonValueKind.Object) return null;
        var key = TryStr(q, "key");
        if (string.IsNullOrWhiteSpace(key)) return null;
        var label = TryStr(q, "label") ?? string.Empty;
        var type = TryStr(q, "type") ?? "text";
        if (type != "text" && type != "yesno" && type != "choice") return null;
        bool required = q.TryGetProperty("required", out var reqEl)
            && reqEl.ValueKind == JsonValueKind.True;
        string? yesLabel = type == "yesno" ? TryStr(q, "yes_label") : null;
        string? noLabel = type == "yesno" ? TryStr(q, "no_label") : null;
        if (string.IsNullOrWhiteSpace(yesLabel)) yesLabel = null;
        if (string.IsNullOrWhiteSpace(noLabel)) noLabel = null;
        var options = type == "choice" ? ProjectChoiceOptions(q) : null;
        return new GateQuestion(key!, type, label, required, yesLabel, noLabel, options);
    }

    /// Projects the <c>options</c> array of a choice question. Each option
    /// carries a label and an outcome — anything other than the two known
    /// outcomes is normalized to "allow" so a malformed row fails safe (the
    /// status change proceeds rather than silently sticking).
    private static IReadOnlyList<GateChoiceOption> ProjectChoiceOptions(JsonElement q)
    {
        var result = new List<GateChoiceOption>();
        if (!q.TryGetProperty("options", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var opt in arr.EnumerateArray())
        {
            if (opt.ValueKind != JsonValueKind.Object) continue;
            var label = TryStr(opt, "label");
            if (string.IsNullOrWhiteSpace(label)) continue;
            var outcome = TryStr(opt, "outcome") == "keep_open" ? "keep_open" : "allow";
            result.Add(new GateChoiceOption(label!, outcome));
        }
        return result;
    }

    private static string? TryStr(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// "First Last", or the email when names are blank. Used in the
    /// require_contact_company dialog so the agent knows which contact
    /// they're linking.
    private static string BuildContactDisplayName(Contact c)
    {
        var first = (c.FirstName ?? string.Empty).Trim();
        var last = (c.LastName ?? string.Empty).Trim();
        if (first.Length > 0 && last.Length > 0) return $"{first} {last}";
        if (first.Length > 0) return first;
        if (last.Length > 0) return last;
        var email = (c.Email ?? string.Empty).Trim();
        return email.Length > 0 ? email : c.Id.ToString();
    }
}
