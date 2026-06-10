using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Storage;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers.FirstOpenGate;

/// Loads gate:first_open triggers, evaluates their condition tree against
/// the ticket an agent just opened, and returns the dialog payload for the
/// blocking title-review prompt — once per ticket. On confirmation it
/// dispatches the trigger's remaining actions through the regular action
/// dispatcher so the approval note (and anything else the admin added) is
/// a normal composable action rather than baked into the gate.
///
/// Fails closed: any malformed row (bad JSON, missing title_review action)
/// is logged and skipped so an agent sees no dialog rather than a broken
/// one — the ticket stays workable either way.
public sealed class FirstOpenGateService : IFirstOpenGateService
{
    private readonly ITriggerRepository _triggers;
    private readonly ITicketRepository _tickets;
    private readonly ITriggerConditionMatcher _matcher;
    private readonly ITriggerRenderContextFactory _renderFactory;
    private readonly ITriggerActionDispatcher _dispatcher;
    private readonly IUserService _users;
    private readonly IMailMessageRepository _mail;
    private readonly IBlobStore _blobs;
    private readonly ILogger<FirstOpenGateService> _logger;

    public FirstOpenGateService(
        ITriggerRepository triggers,
        ITicketRepository tickets,
        ITriggerConditionMatcher matcher,
        ITriggerRenderContextFactory renderFactory,
        ITriggerActionDispatcher dispatcher,
        IUserService users,
        IMailMessageRepository mail,
        IBlobStore blobs,
        ILogger<FirstOpenGateService> logger)
    {
        _triggers = triggers;
        _tickets = tickets;
        _matcher = matcher;
        _renderFactory = renderFactory;
        _dispatcher = dispatcher;
        _users = users;
        _mail = mail;
        _blobs = blobs;
        _logger = logger;
    }

    private const string DefaultFieldLabel = "Ticket title";

    public async Task<MatchedFirstOpenGate?> FindMatchingAsync(Guid ticketId, CancellationToken ct)
    {
        var detail = await _tickets.GetByIdAsync(ticketId, ct);
        if (detail is null) return null;

        // One-time gate: once any agent has reviewed the title the dialog
        // never re-surfaces for this ticket.
        if (await _tickets.IsTitleReviewedAsync(ticketId, ct)) return null;

        IReadOnlyList<TriggerRow> rows;
        try
        {
            rows = await _triggers.LoadActiveAsync(TriggerActivatorKind.Gate, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FirstOpenGateService failed to load gate triggers for ticket {TicketId}.", ticketId);
            return null;
        }
        if (rows.Count == 0) return null;

        // Mail-created tickets keep their body intentionally empty (the mail
        // lands in the timeline instead), so the original-request panel would
        // come up blank. Fall back to the ticket's first inbound email once,
        // shared by every candidate gate row.
        MailRequestFallback? mailFallback = null;
        if (string.IsNullOrWhiteSpace(detail.Body.BodyHtml)
            && string.IsNullOrWhiteSpace(detail.Body.BodyText))
        {
            mailFallback = await TryLoadMailFallbackAsync(ticketId, ct);
        }

        foreach (var row in rows)
        {
            if (!string.Equals(row.ActivatorMode, "first_open", StringComparison.Ordinal))
                continue;

            MatchedFirstOpenGate? gate;
            try { gate = ProjectGate(row, detail.Ticket.Subject, detail.Body, mailFallback); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "First-open gate {TriggerId} has a malformed title_review payload; skipping.", row.Id);
                continue;
            }
            if (gate is null) continue;

            JsonDocument condDoc;
            try { condDoc = JsonDocument.Parse(row.ConditionsJson); }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "First-open gate {TriggerId} has malformed conditions JSON; skipping.", row.Id);
                continue;
            }

            try
            {
                var ctx = new TriggerEvaluationContext(
                    TicketId: ticketId,
                    Ticket: detail.Ticket,
                    TriggeringEvent: null,
                    ChangeSet: new TriggerChangeSet(
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), ArticleAdded: false),
                    UtcNow: DateTime.UtcNow,
                    TriggerId: row.Id);
                if (!_matcher.Matches(condDoc.RootElement, ctx)) continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "First-open gate {TriggerId} matcher crashed; skipping.", row.Id);
                continue;
            }
            finally { condDoc.Dispose(); }

            return gate;
        }
        return null;
    }

    public async Task RunConfirmActionsAsync(
        Guid triggerId,
        Guid ticketId,
        Guid agentUserId,
        string previousSubject,
        CancellationToken ct)
    {
        var trigger = await _triggers.GetByIdAsync(triggerId, ct);
        if (trigger is null || !trigger.IsActive) return;
        if (trigger.ActivatorKind != "gate" || trigger.ActivatorMode != "first_open") return;

        var detail = await _tickets.GetByIdAsync(ticketId, ct);
        if (detail is null) return;

        JsonDocument actionsDoc;
        try { actionsDoc = JsonDocument.Parse(trigger.ActionsJson); }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "First-open gate {TriggerId} has malformed actions JSON; skipping confirmation actions.", triggerId);
            return;
        }

        try
        {
            var ctx = new TriggerEvaluationContext(
                TicketId: ticketId,
                Ticket: detail.Ticket,
                TriggeringEvent: null,
                ChangeSet: new TriggerChangeSet(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), ArticleAdded: false),
                UtcNow: DateTime.UtcNow,
                TriggerId: trigger.Id);

            var baseCtx = await _renderFactory.BuildAsync(ctx, trigger.Locale, trigger.Timezone, ct);
            var agent = await _users.FindByIdAsync(agentUserId, ct);

            var merged = new Dictionary<string, string?>(baseCtx.StringValues, StringComparer.Ordinal)
            {
                ["agent.email"] = agent?.Email,
                ["agent.name"] = agent?.Email,
                ["ticket.subject_previous"] = previousSubject,
            };

            var renderCtx = new TriggerRenderContext
            {
                StringValues = merged,
                DateTimeValues = baseCtx.DateTimeValues,
                DefaultTimeZoneId = baseCtx.DefaultTimeZoneId,
                Culture = baseCtx.Culture,
            };

            var dispatchCtx = ctx with { RenderContext = renderCtx };
            var results = await _dispatcher.DispatchAllAsync(actionsDoc.RootElement, dispatchCtx, ct);

            var hasFailures = results.Any(r =>
                r.Status == TriggerActionStatus.Failed || r.Status == TriggerActionStatus.NoHandler);
            var outcome = hasFailures ? TriggerRunOutcome.Failed : TriggerRunOutcome.Applied;

            var diff = JsonSerializer.Serialize(new
            {
                trigger_id = trigger.Id,
                trigger_name = trigger.Name,
                gate = "first_open",
                actions = results.Select(r => new
                {
                    kind = r.Kind,
                    status = r.Status.ToString().ToLowerInvariant(),
                }),
            });

            try
            {
                await _triggers.RecordRunAsync(new TriggerRunRecord(
                    trigger.Id, ticketId, TicketEventId: null,
                    outcome, diff, null, null), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record trigger_run for first-open gate {TriggerId}.", trigger.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "First-open gate {TriggerId} confirmation actions crashed on ticket {TicketId}.", triggerId, ticketId);
        }
        finally
        {
            actionsDoc.Dispose();
        }
    }

    /// Original-request fallback sourced from the ticket's first inbound
    /// email. Exactly one of Html/Text is set; RawMailId is non-null only
    /// when the raw .eml blob is available for download.
    private sealed record MailRequestFallback(string? Html, string? Text, Guid? RawMailId);

    /// Loads the first inbound mail of the ticket and shapes it into the
    /// original-request fallback: HTML body (images stripped — inline cid:
    /// references can't resolve inside the dialog) preferred, plain text
    /// otherwise. Returns null when the ticket has no inbound mail or the
    /// mail carries no readable body. Fails open: any storage hiccup just
    /// means the panel stays empty, exactly like before the fallback.
    private async Task<MailRequestFallback?> TryLoadMailFallbackAsync(Guid ticketId, CancellationToken ct)
    {
        try
        {
            var mail = await _mail.GetFirstInboundForTicketAsync(ticketId, ct);
            if (mail is null) return null;

            string? html = null;
            if (!string.IsNullOrWhiteSpace(mail.BodyHtmlBlobHash))
            {
                await using var stream = await _blobs.OpenReadAsync(mail.BodyHtmlBlobHash, ct);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    var raw = await reader.ReadToEndAsync(ct);
                    var stripped = MailRequestHtml.StripImages(raw);
                    if (!string.IsNullOrWhiteSpace(stripped)) html = stripped;
                }
            }

            var text = html is null && !string.IsNullOrWhiteSpace(mail.BodyText)
                ? mail.BodyText
                : null;
            if (html is null && text is null) return null;

            var rawMailId = string.IsNullOrWhiteSpace(mail.RawEmlBlobHash) ? (Guid?)null : mail.Id;
            return new MailRequestFallback(html, text, rawMailId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "First-open gate mail-body fallback failed for ticket {TicketId}; showing no request panel.",
                ticketId);
            return null;
        }
    }

    /// Projects the single title_review action of a gate trigger into the
    /// dialog payload. Returns null when the row carries no title_review
    /// action (the validator prevents this, but defend in depth).
    private static MatchedFirstOpenGate? ProjectGate(
        TriggerRow row, string currentSubject, TicketBody body, MailRequestFallback? mailFallback)
    {
        if (string.IsNullOrWhiteSpace(row.ActionsJson)) return null;
        using var doc = JsonDocument.Parse(row.ActionsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var action in doc.RootElement.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object) continue;
            if (!action.TryGetProperty("kind", out var kindEl)
                || kindEl.ValueKind != JsonValueKind.String
                || kindEl.GetString() != "title_review")
                continue;

            var title = TryStr(action, "title") ?? string.Empty;
            bool showMessage = !action.TryGetProperty("show_message", out var smEl)
                ? !string.IsNullOrWhiteSpace(TryStr(action, "message"))
                : smEl.ValueKind == JsonValueKind.True;
            string? message = showMessage ? TryStr(action, "message") : null;
            if (string.IsNullOrWhiteSpace(message)) message = null;

            var fieldLabel = TryStr(action, "field_label");
            if (string.IsNullOrWhiteSpace(fieldLabel)) fieldLabel = DefaultFieldLabel;

            var confirmLabel = TryStr(action, "confirm_label") ?? "This title is suitable";

            // Default on when the property is absent so a pre-v0.0.73 payload
            // still shows the original-request panel.
            bool showRequest = !action.TryGetProperty("show_request", out var srEl)
                || srEl.ValueKind != JsonValueKind.False;

            string? requestHtml = null;
            string? requestText = null;
            bool requestFromMail = false;
            Guid? requestMailId = null;
            if (showRequest)
            {
                if (!string.IsNullOrWhiteSpace(body.BodyHtml))
                    requestHtml = body.BodyHtml;
                else if (!string.IsNullOrWhiteSpace(body.BodyText))
                    requestText = body.BodyText;
                else if (mailFallback is not null)
                {
                    requestHtml = mailFallback.Html;
                    requestText = mailFallback.Text;
                    requestFromMail = true;
                    requestMailId = mailFallback.RawMailId;
                }
            }

            return new MatchedFirstOpenGate(
                TriggerId: row.Id,
                Name: row.Name,
                Title: title,
                Message: message,
                FieldLabel: fieldLabel!,
                ConfirmLabel: confirmLabel,
                CurrentSubject: currentSubject,
                ShowRequest: showRequest,
                RequestBodyHtml: requestHtml,
                RequestBodyText: requestText,
                RequestFromMail: requestFromMail,
                RequestMailId: requestMailId);
        }
        return null;
    }

    private static string? TryStr(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
