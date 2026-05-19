using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Triggers.Actions;
using Servicedesk.Infrastructure.Triggers.Templating;

namespace Servicedesk.Infrastructure.Triggers;

/// v0.0.39 — runs a manual <c>create_linked_ticket</c> trigger for a
/// parent ticket and returns a fully-rendered prefill DTO. The actual
/// ticket create still goes through the normal <c>POST /api/tickets</c>
/// endpoint — this service is just the "fetch what to put in the
/// drawer" step. Lives in Infrastructure rather than the action-handler
/// folder because it is invoked synchronously from an endpoint, not
/// from the event-driven evaluator (manual triggers are filtered out
/// of that loop at the loader).
public interface ILinkedTicketPresetService
{
    /// Lists the active manual triggers visible to the agent for a
    /// given parent ticket. The endpoint that calls this layer turns
    /// the result into one button per row in the side-panel picker.
    /// Inactive triggers and triggers whose ticket-type is deactivated
    /// are excluded — admins toggle either knob to hide a button.
    Task<IReadOnlyList<LinkedTicketPresetSummary>> ListAvailablePresetsAsync(
        Guid parentTicketId, CancellationToken ct);

    /// Resolves a single manual trigger against a parent ticket: renders
    /// every template field, looks up the requester/company per the
    /// configured source, and returns the prefill the drawer should
    /// open with. Returns null when the trigger is missing, not manual,
    /// inactive, or its bound ticket-type is no longer active — the
    /// endpoint translates null into a 404.
    Task<LinkedTicketPrefill?> ResolvePrefillAsync(
        Guid triggerId, Guid parentTicketId, Guid currentAgentUserId, CancellationToken ct);
}

public sealed record LinkedTicketPresetSummary(
    Guid TriggerId,
    string Name,
    Guid TicketTypeId,
    string TicketTypeCode,
    string TicketTypeLabel,
    string TicketTypeDescription,
    string TicketTypeIcon,
    string TicketTypeColor,
    int SortOrder);

public sealed record LinkedTicketPrefill(
    Guid TriggerId,
    Guid TicketTypeId,
    string TicketTypeCode,
    string Subject,
    string BodyHtml,
    Guid RequesterContactId,
    Guid? CompanyId,
    Guid QueueId,
    Guid StatusId,
    Guid PriorityId,
    Guid? CategoryId,
    Guid? AssigneeUserId,
    LinkedTicketInitialNotePrefill? InitialNote);

public sealed record LinkedTicketInitialNotePrefill(string BodyHtml, bool IsInternal);

public sealed class LinkedTicketPresetService : ILinkedTicketPresetService
{
    private readonly ITriggerRepository _triggers;
    private readonly ITicketRepository _tickets;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ICompanyRepository _companies;
    private readonly IUserService _users;
    private readonly ITriggerRenderContextFactory _renderCtxFactory;
    private readonly ITriggerTemplateRenderer _renderer;
    private readonly ILogger<LinkedTicketPresetService> _logger;

    public LinkedTicketPresetService(
        ITriggerRepository triggers,
        ITicketRepository tickets,
        ITaxonomyRepository taxonomy,
        ICompanyRepository companies,
        IUserService users,
        ITriggerRenderContextFactory renderCtxFactory,
        ITriggerTemplateRenderer renderer,
        ILogger<LinkedTicketPresetService> logger)
    {
        _triggers = triggers;
        _tickets = tickets;
        _taxonomy = taxonomy;
        _companies = companies;
        _users = users;
        _renderCtxFactory = renderCtxFactory;
        _renderer = renderer;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LinkedTicketPresetSummary>> ListAvailablePresetsAsync(
        Guid parentTicketId, CancellationToken ct)
    {
        // We don't need parent context to filter the list today — every
        // active manual trigger is offered. parentTicketId is accepted
        // so the contract stays stable when future versions add
        // queue/visibility filtering. The merged-state check is the
        // caller's job (side panel hides the picker entirely on merged
        // tickets, identical to the existing "Create linked ticket"
        // gate in RelationshipsBlock).
        _ = parentTicketId;

        var allManual = await _triggers.LoadActiveAsync(TriggerActivatorKind.Manual, ct);
        if (allManual.Count == 0) return Array.Empty<LinkedTicketPresetSummary>();

        var types = (await _taxonomy.ListTicketTypesAsync(ct))
            .Where(t => t.IsActive)
            .ToDictionary(t => t.Id);

        var summaries = new List<LinkedTicketPresetSummary>(allManual.Count);
        foreach (var t in allManual)
        {
            if (t.ManualTicketTypeId is not Guid typeId) continue;
            if (!types.TryGetValue(typeId, out var type)) continue;
            summaries.Add(new LinkedTicketPresetSummary(
                TriggerId: t.Id,
                Name: t.Name,
                TicketTypeId: type.Id,
                TicketTypeCode: type.Code,
                TicketTypeLabel: type.Label,
                TicketTypeDescription: type.Description,
                TicketTypeIcon: type.Icon,
                TicketTypeColor: type.Color,
                SortOrder: type.SortOrder));
        }
        // Sort buttons by the ticket-type's own sort_order so the
        // picker stays in the same order across triggers of the same
        // type. Trigger name is a tiebreaker for predictable rendering.
        return summaries
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.TicketTypeLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<LinkedTicketPrefill?> ResolvePrefillAsync(
        Guid triggerId, Guid parentTicketId, Guid currentAgentUserId, CancellationToken ct)
    {
        var trigger = await _triggers.GetByIdAsync(triggerId, ct);
        if (trigger is null || !trigger.IsActive) return null;
        if (!string.Equals(trigger.ActivatorKind, "manual", StringComparison.Ordinal)) return null;
        if (trigger.ManualTicketTypeId is not Guid typeId) return null;

        var type = await _taxonomy.GetTicketTypeAsync(typeId, ct);
        if (type is null || !type.IsActive) return null;

        var parent = await _tickets.GetByIdAsync(parentTicketId, ct);
        if (parent is null) return null;
        var parentTicket = parent.Ticket;

        var action = ExtractCreateLinkedTicketAction(trigger.ActionsJson);
        if (action is null)
        {
            _logger.LogWarning(
                "Manual trigger {TriggerId} is missing a create_linked_ticket action — preset unusable.",
                trigger.Id);
            return null;
        }
        var act = action.Value;

        // Build the render context for the PARENT ticket so #{ticket.*}
        // tokens substitute the parent's values. The agent expects
        // "#{ticket.company.name} — replacement order" to resolve to
        // the parent's company name, not an empty placeholder.
        var evalCtx = new TriggerEvaluationContext(
            TicketId: parentTicket.Id,
            Ticket: parentTicket,
            TriggeringEvent: null,
            ChangeSet: TriggerChangeSet.AllFieldsNew(),
            UtcNow: DateTime.UtcNow,
            TriggerId: trigger.Id);
        var renderCtx = await _renderCtxFactory.BuildAsync(evalCtx, trigger.Locale, trigger.Timezone, ct);

        var subject = string.IsNullOrEmpty(act.SubjectTemplate)
            ? parentTicket.Subject
            : _renderer.Render(act.SubjectTemplate, TemplateEscapeMode.PlainText, renderCtx);
        var bodyHtml = string.IsNullOrEmpty(act.BodyHtmlTemplate)
            ? string.Empty
            : _renderer.Render(act.BodyHtmlTemplate, TemplateEscapeMode.Html, renderCtx);

        var requesterContactId = await ResolveRequesterAsync(act, parentTicket, currentAgentUserId, ct);
        var companyId = await ResolveCompanyAsync(act, parentTicket, requesterContactId, ct);

        LinkedTicketInitialNotePrefill? initialNote = null;
        if (act.InitialNote is { } noteCfg && !string.IsNullOrEmpty(noteCfg.BodyHtmlTemplate))
        {
            var renderedNote = _renderer.Render(noteCfg.BodyHtmlTemplate, TemplateEscapeMode.Html, renderCtx);
            initialNote = new LinkedTicketInitialNotePrefill(renderedNote, noteCfg.IsInternal);
        }

        return new LinkedTicketPrefill(
            TriggerId: trigger.Id,
            TicketTypeId: type.Id,
            TicketTypeCode: type.Code,
            Subject: subject,
            BodyHtml: bodyHtml,
            RequesterContactId: requesterContactId,
            CompanyId: companyId,
            QueueId: act.QueueId ?? parentTicket.QueueId,
            StatusId: act.StatusId ?? parentTicket.StatusId,
            PriorityId: act.PriorityId ?? parentTicket.PriorityId,
            CategoryId: act.CategoryId,
            AssigneeUserId: act.AssigneeUserId,
            InitialNote: initialNote);
    }

    private async Task<Guid> ResolveRequesterAsync(
        CreateLinkedTicketAction act, Ticket parent, Guid currentAgentUserId, CancellationToken ct)
    {
        switch (act.RequesterSource)
        {
            case "fixed_contact":
                if (act.RequesterContactId is Guid fixedId && fixedId != Guid.Empty)
                    return fixedId;
                return parent.RequesterContactId;
            case "current_agent":
                // Try to find a contact row that matches the agent's
                // user email. Servicedesk keeps users and contacts as
                // separate entities; when an agent doubles as a
                // requester they typically also have a contact row
                // with the same email (CITEXT match). Fall back to the
                // parent's requester if no match — better to surface a
                // sane default than fail the whole drawer-open.
                var agent = await _users.FindByIdAsync(currentAgentUserId, ct);
                if (agent is null || string.IsNullOrWhiteSpace(agent.Email))
                    return parent.RequesterContactId;
                var contact = await _companies.GetContactByEmailAsync(agent.Email, ct);
                return contact?.Id ?? parent.RequesterContactId;
            case "parent":
            default:
                return parent.RequesterContactId;
        }
    }

    private async Task<Guid?> ResolveCompanyAsync(
        CreateLinkedTicketAction act, Ticket parent, Guid requesterContactId, CancellationToken ct)
    {
        switch (act.CompanySource)
        {
            case "fixed_company":
                if (act.CompanyId is Guid fixedCo && fixedCo != Guid.Empty)
                    return fixedCo;
                return parent.CompanyId;
            case "from_requester_primary":
                // Walk the contact's primary company link. When the
                // requester changed via fixed_contact / current_agent
                // this lands on a different company than the parent's,
                // which is the whole point of this option.
                var primary = await _companies.GetPrimaryCompanyForContactAsync(requesterContactId, ct);
                return primary?.Id ?? parent.CompanyId;
            case "parent":
            default:
                return parent.CompanyId;
        }
    }

    private static CreateLinkedTicketAction? ExtractCreateLinkedTicketAction(string actionsJson)
    {
        if (string.IsNullOrWhiteSpace(actionsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(actionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("kind", out var kindEl)) continue;
                if (kindEl.ValueKind != JsonValueKind.String) continue;
                if (!string.Equals(kindEl.GetString(), "create_linked_ticket", StringComparison.Ordinal)) continue;
                return ParseAction(entry);
            }
        }
        catch (JsonException)
        {
            // Fail soft — a malformed actions JSON should not crash the
            // side-panel open. The endpoint returns 404 / empty list
            // and the admin can fix the trigger later. Logging happens
            // at the caller because we don't have a logger here.
        }
        return null;
    }

    private static CreateLinkedTicketAction ParseAction(JsonElement el)
    {
        ActionJson.TryReadString(el, "subject_template", out var subject);
        ActionJson.TryReadString(el, "body_html_template", out var bodyHtml);
        ActionJson.TryReadGuidOrNull(el, "queue_id", out var queueId);
        ActionJson.TryReadGuidOrNull(el, "status_id", out var statusId);
        ActionJson.TryReadGuidOrNull(el, "priority_id", out var priorityId);
        ActionJson.TryReadGuidOrNull(el, "category_id", out var categoryId);
        ActionJson.TryReadGuidOrNull(el, "assignee_user_id", out var assigneeUserId);
        ActionJson.TryReadString(el, "requester_source", out var requesterSource);
        ActionJson.TryReadGuidOrNull(el, "requester_contact_id", out var requesterContactId);
        ActionJson.TryReadString(el, "company_source", out var companySource);
        ActionJson.TryReadGuidOrNull(el, "company_id", out var companyId);

        CreateLinkedTicketInitialNote? initialNote = null;
        if (el.TryGetProperty("initial_note", out var noteEl) && noteEl.ValueKind == JsonValueKind.Object)
        {
            ActionJson.TryReadString(noteEl, "body_html_template", out var noteBody);
            ActionJson.TryReadBool(noteEl, "is_internal", out var noteInternal);
            if (!string.IsNullOrWhiteSpace(noteBody))
            {
                initialNote = new CreateLinkedTicketInitialNote(noteBody, noteInternal);
            }
        }

        return new CreateLinkedTicketAction(
            SubjectTemplate: subject,
            BodyHtmlTemplate: bodyHtml,
            QueueId: queueId,
            StatusId: statusId,
            PriorityId: priorityId,
            CategoryId: categoryId,
            AssigneeUserId: assigneeUserId,
            RequesterSource: string.IsNullOrWhiteSpace(requesterSource)
                ? "parent"
                : requesterSource.ToLowerInvariant(),
            RequesterContactId: requesterContactId,
            CompanySource: string.IsNullOrWhiteSpace(companySource)
                ? "parent"
                : companySource.ToLowerInvariant(),
            CompanyId: companyId,
            InitialNote: initialNote);
    }

    private readonly record struct CreateLinkedTicketAction(
        string SubjectTemplate,
        string BodyHtmlTemplate,
        Guid? QueueId,
        Guid? StatusId,
        Guid? PriorityId,
        Guid? CategoryId,
        Guid? AssigneeUserId,
        string RequesterSource,
        Guid? RequesterContactId,
        string CompanySource,
        Guid? CompanyId,
        CreateLinkedTicketInitialNote? InitialNote);

    private sealed record CreateLinkedTicketInitialNote(string BodyHtmlTemplate, bool IsInternal);
}
