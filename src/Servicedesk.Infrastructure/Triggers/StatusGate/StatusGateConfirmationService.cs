using System.Net;
using System.Text.Json;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Triggers.StatusGate;

/// The agent's answer to one matched gate, as posted with the status change.
public sealed record GateConfirmation(
    Guid TriggerId,
    IReadOnlyDictionary<string, string>? Answers,
    Guid? CompanyId = null,
    string? Role = null);

/// One resolved choice-question pick, for the StatusGateDecision event.
public sealed record GateChoiceDecision(string QuestionLabel, string ChosenLabel, string Outcome);

/// A contact-company link a confirmed <c>contact_company_link</c> gate needs
/// before the status flip.
public sealed record PendingContactCompanyLink(Guid ContactId, Guid CompanyId, string Role);

/// One gate whose confirmation is complete. <see cref="Answers"/> (prompt
/// gates) or <see cref="Extras"/> (company-link gates) feed the note template;
/// <see cref="KeepOpen"/> is set when a chosen option holds the ticket in its
/// current status (v0.0.89).
public sealed record ConfirmedStatusGate(
    MatchedStatusGate Gate,
    IReadOnlyDictionary<string, string>? Answers,
    IReadOnlyDictionary<string, string>? Extras,
    PendingContactCompanyLink? PendingLink,
    bool KeepOpen = false,
    IReadOnlyList<GateChoiceDecision>? Decisions = null);

/// Outcome of matching the agent's confirmations against the gates.
/// <see cref="InvalidCompany"/>: a company-link answer names a company that is
/// missing or inactive (400). <see cref="Missing"/>: gates without a complete
/// confirmation (409 with those gates). Otherwise every gate is confirmed.
public sealed record GateNegotiation(
    IReadOnlyList<MatchedStatusGate> Missing,
    IReadOnlyList<ConfirmedStatusGate> Confirmed,
    bool InvalidCompany = false)
{
    public bool KeepOpen => Confirmed.Any(c => c.KeepOpen);
}

/// The status-change gate negotiation of the ticket PATCH (v0.0.42 / v0.0.89),
/// lifted out of the endpoint (ROADMAP §2 C4). Gates are a confirmation dialog
/// for an agent's own status change only — triggers, mail-ingest and bulk
/// actions never negotiate (bulk skips a gated ticket). Three steps around
/// the field write: <see cref="NegotiateAsync"/> before it,
/// <see cref="ApplyPendingLinksAsync"/> just before it (the link is the
/// gate's precondition), <see cref="RecordAsync"/> after it.
public interface IStatusGateConfirmationService
{
    Task<GateNegotiation> NegotiateAsync(
        IReadOnlyList<MatchedStatusGate> gates, IReadOnlyList<GateConfirmation> confirmations, CancellationToken ct);

    Task ApplyPendingLinksAsync(GateNegotiation negotiation, CancellationToken ct);

    /// Appends the confirmation note of each gate (when its template renders
    /// non-empty) and one internal StatusGateDecision event per choice pick.
    /// Runs after the field write so the notes sort after the StatusChange.
    /// Returns true when anything was written.
    Task<bool> RecordAsync(Guid ticketId, Guid agentUserId, GateNegotiation negotiation, CancellationToken ct);
}

public sealed class StatusGateConfirmationService : IStatusGateConfirmationService
{
    private readonly ICompanyRepository _companies;
    private readonly IStatusGateService _gates;
    private readonly ITicketRepository _tickets;

    public StatusGateConfirmationService(ICompanyRepository companies, IStatusGateService gates, ITicketRepository tickets)
    {
        _companies = companies;
        _gates = gates;
        _tickets = tickets;
    }

    public async Task<GateNegotiation> NegotiateAsync(
        IReadOnlyList<MatchedStatusGate> gates, IReadOnlyList<GateConfirmation> confirmations, CancellationToken ct)
    {
        var byTrigger = confirmations.GroupBy(c => c.TriggerId).ToDictionary(g => g.Key, g => g.First());
        var missing = new List<MatchedStatusGate>();
        var confirmed = new List<ConfirmedStatusGate>();

        foreach (var gate in gates)
        {
            if (!byTrigger.TryGetValue(gate.TriggerId, out var conf))
            {
                missing.Add(gate);
                continue;
            }
            switch (gate.Kind)
            {
                case "prompt_confirm":
                {
                    var resolution = ResolvePromptConfirm(gate, conf.Answers ?? new Dictionary<string, string>(StringComparer.Ordinal));
                    if (resolution is null) { missing.Add(gate); break; }
                    confirmed.Add(new ConfirmedStatusGate(
                        gate, resolution.Answers, Extras: null, PendingLink: null,
                        KeepOpen: resolution.KeepOpen, Decisions: resolution.Decisions));
                    break;
                }
                case "contact_company_link":
                {
                    var role = NormalizeContactCompanyRole(conf.Role);
                    if (!conf.CompanyId.HasValue || role is null || !gate.RequesterContactId.HasValue)
                    {
                        missing.Add(gate);
                        break;
                    }
                    var company = await _companies.GetCompanyAsync(conf.CompanyId.Value, ct);
                    if (company is null || !company.IsActive)
                        return new GateNegotiation(Array.Empty<MatchedStatusGate>(), Array.Empty<ConfirmedStatusGate>(), InvalidCompany: true);
                    var extras = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["company.id"] = company.Id.ToString(),
                        ["company.name"] = company.Name,
                        ["company.code"] = company.Code ?? string.Empty,
                        ["company.role"] = role,
                    };
                    confirmed.Add(new ConfirmedStatusGate(gate, Answers: null, extras,
                        new PendingContactCompanyLink(gate.RequesterContactId.Value, company.Id, role)));
                    break;
                }
                default:
                    // An unknown kind cannot be satisfied — the validator
                    // rejects it at save time; defend in depth so a future
                    // kind cannot silently bypass.
                    missing.Add(gate);
                    break;
            }
        }
        return new GateNegotiation(missing, confirmed);
    }

    public async Task ApplyPendingLinksAsync(GateNegotiation negotiation, CancellationToken ct)
    {
        // UpsertContactLinkAsync keeps the one-primary invariant.
        foreach (var link in negotiation.Confirmed.Select(c => c.PendingLink).OfType<PendingContactCompanyLink>())
            await _companies.UpsertContactLinkAsync(link.ContactId, link.CompanyId, link.Role, ct);
    }

    public async Task<bool> RecordAsync(Guid ticketId, Guid agentUserId, GateNegotiation negotiation, CancellationToken ct)
    {
        var wrote = false;
        // Failures here do not roll the status change back — the audit trail
        // still shows the change happened.
        foreach (var c in negotiation.Confirmed)
        {
            var bodyHtml = await _gates.RenderNoteAsync(c.Gate, ticketId, agentUserId, c.Answers, c.Extras, ct);
            if (string.IsNullOrWhiteSpace(bodyHtml)) continue;
            await _tickets.AddEventAsync(ticketId, new NewTicketEvent(
                EventType: "Note",
                BodyText: null,
                BodyHtml: bodyHtml,
                IsInternal: c.Gate.NoteVisibility != "public",
                AuthorUserId: agentUserId,
                MetadataJson: JsonSerializer.Serialize(new { gate_trigger_id = c.Gate.TriggerId })), ct);
            wrote = true;
        }

        // v0.0.89 — each choice decision (allow or keep-open) is its own
        // internal event, auditable whether or not the gate added a note.
        foreach (var c in negotiation.Confirmed)
        {
            foreach (var d in c.Decisions ?? Array.Empty<GateChoiceDecision>())
            {
                var keptOpen = d.Outcome == "keep_open";
                await _tickets.AddEventAsync(ticketId, new NewTicketEvent(
                    EventType: "StatusGateDecision",
                    BodyText: null,
                    BodyHtml: DecisionHtml(c.Gate, d, keptOpen),
                    IsInternal: true,
                    AuthorUserId: agentUserId,
                    MetadataJson: JsonSerializer.Serialize(new
                    {
                        gate_trigger_id = c.Gate.TriggerId,
                        gate_name = c.Gate.Name,
                        question = d.QuestionLabel,
                        choice = d.ChosenLabel,
                        outcome = d.Outcome,
                        to_status_id = c.Gate.ToStatusId,
                    })), ct);
                wrote = true;
            }
        }
        return wrote;
    }

    /// Validates a prompt_confirm gate's answers. Null when they don't satisfy
    /// the gate (a missing confirmation → 409). Otherwise: the note-template
    /// values (for a choice the picked option's label, not its index),
    /// KeepOpen when a picked option's outcome is keep_open, and one decision
    /// per choice question. The outcome comes from the server-side gate
    /// definition, never from the client (which only sends the index).
    internal static PromptConfirmResolution? ResolvePromptConfirm(
        MatchedStatusGate gate, IReadOnlyDictionary<string, string> answers)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var decisions = new List<GateChoiceDecision>();
        var keepOpen = false;

        foreach (var q in gate.Questions)
        {
            switch (q.Type)
            {
                case "text":
                    if (answers.TryGetValue(q.Key, out var txt) && !string.IsNullOrWhiteSpace(txt))
                        resolved[q.Key] = txt;
                    else if (q.Required)
                        return null;
                    break;
                case "yesno":
                    if (answers.TryGetValue(q.Key, out var yn) && !string.IsNullOrWhiteSpace(yn))
                        resolved[q.Key] = yn;
                    else if (q.YesLabel is not null)
                        return null;
                    break;
                case "choice":
                    var options = q.Options ?? Array.Empty<GateChoiceOption>();
                    if (options.Count == 0) return null; // misconfigured — cannot satisfy
                    if (!answers.TryGetValue(q.Key, out var pick)
                        || !int.TryParse(pick, out var idx)
                        || idx < 0 || idx >= options.Count)
                        return null; // no valid selection
                    var chosen = options[idx];
                    resolved[q.Key] = chosen.Label;
                    decisions.Add(new GateChoiceDecision(q.Label, chosen.Label, chosen.Outcome));
                    if (chosen.Outcome == "keep_open") keepOpen = true;
                    break;
            }
        }
        return new PromptConfirmResolution(resolved, keepOpen, decisions);
    }

    internal sealed record PromptConfirmResolution(
        IReadOnlyDictionary<string, string> Answers, bool KeepOpen, IReadOnlyList<GateChoiceDecision> Decisions);

    /// The agent-picked link role, normalised; null when malformed. Mirrors
    /// the DB CHECK constraint <c>chk_contact_companies_role</c>.
    internal static string? NormalizeContactCompanyRole(string? role) =>
        string.IsNullOrWhiteSpace(role) ? null : role.Trim().ToLowerInvariant() switch
        {
            "primary" => "primary",
            "secondary" => "secondary",
            "supplier" => "supplier",
            _ => null,
        };

    private static string DecisionHtml(MatchedStatusGate gate, GateChoiceDecision d, bool keptOpen)
    {
        var title = WebUtility.HtmlEncode(gate.Title);
        var question = WebUtility.HtmlEncode(d.QuestionLabel);
        var choice = WebUtility.HtmlEncode(d.ChosenLabel);
        var outcomeText = keptOpen ? "ticket kept open" : "status change allowed";
        return $"<p><strong>Status-change check — {title}</strong></p>"
             + $"<p>{question}<br>Chosen: <strong>{choice}</strong> ({outcomeText})</p>";
    }
}
