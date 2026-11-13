using System.Text.Json;
using System.Text.Json.Nodes;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Infrastructure.Forms;

/// Stable rejection codes for ticket-form operations; endpoints map them to
/// HTTP and the frontend keys its messages on them.
public static class FormRejectCode
{
    /// Ticket/form missing or no queue access — rendered as 404.
    public const string NotFound = "not_found";
    public const string Disabled = "forms_disabled";
    public const string TemplateNotAvailable = "template_not_available";
    public const string TooManyForms = "too_many_forms";
    public const string VersionConflict = "version_conflict";
    public const string Forbidden = "forbidden";
    public const string Invalid = "invalid";
}

public sealed class FormRejectedException : Exception
{
    public string Code { get; }
    public Guid? FieldId { get; }
    public FormRejectedException(string code, string message, Guid? fieldId = null) : base(message)
    {
        Code = code;
        FieldId = fieldId;
    }
}

public sealed record TicketFormSaveResult(TicketFormDetail Form, long EventId);

public interface ITicketFormService
{
    Task<IReadOnlyList<TicketFormSummary>> ListAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct);
    Task<IReadOnlyList<FormTemplateSummary>> ListUsableTemplatesAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct);
    /// The template as it would be snapshotted — for the fill dialog.
    Task<FormTemplateDetail> GetUsableTemplateAsync(TicketMutationActor actor, Guid ticketId, Guid templateId, CancellationToken ct);
    Task<TicketFormSaveResult> CreateAsync(TicketMutationActor actor, Guid ticketId, Guid templateId, JsonObject? answers, CancellationToken ct);
    Task<TicketFormDetail> GetAsync(TicketMutationActor actor, Guid formId, CancellationToken ct);
    Task<IReadOnlyList<TicketFormVersion>> ListVersionsAsync(TicketMutationActor actor, Guid formId, CancellationToken ct);
    Task<TicketFormSaveResult> UpdateAsync(TicketMutationActor actor, Guid formId, int expectedVersion, JsonObject? answers, CancellationToken ct);
    Task DeleteAsync(TicketMutationActor actor, Guid formId, string? reason, CancellationToken ct);
    Task<IReadOnlyList<TicketFormArticleOption>> SearchArticlesAsync(TicketMutationActor actor, Guid ticketId, bool contractOnly, string? search, CancellationToken ct);
    Task<IReadOnlyList<TicketFormContactOption>> SearchContactsAsync(TicketMutationActor actor, Guid ticketId, string? search, CancellationToken ct);
}

/// v0.1.52 — the rules around filled forms on a ticket. Access = the ticket's
/// queue access via the shared mutation precheck (a form id alone never grants
/// anything: the form's ticket is resolved and checked on every call). Every
/// save writes a plain internal Note through <see cref="ITicketMutationService"/>
/// (so audit, realtime, SLA and triggers behave exactly as for a typed note)
/// plus a version row. Answers are validated against the form's own frozen
/// snapshot, never against the live template.
public sealed class TicketFormService : ITicketFormService
{
    private readonly ITicketFormRepository _repo;
    private readonly IFormTemplateRepository _templates;
    private readonly ITicketMutationService _mutations;
    private readonly IFormSettingsReader _settings;
    private readonly ISettingsService _appSettings;
    private readonly ITicketHtmlSanitizer _sanitizer;
    private readonly IAuditLogger _audit;

    public TicketFormService(
        ITicketFormRepository repo,
        IFormTemplateRepository templates,
        ITicketMutationService mutations,
        IFormSettingsReader settings,
        ISettingsService appSettings,
        ITicketHtmlSanitizer sanitizer,
        IAuditLogger audit)
    {
        _repo = repo;
        _templates = templates;
        _mutations = mutations;
        _settings = settings;
        _appSettings = appSettings;
        _sanitizer = sanitizer;
        _audit = audit;
    }

    private static bool IsAdmin(TicketMutationActor actor)
        => string.Equals(actor.Role, "Admin", StringComparison.OrdinalIgnoreCase);

    private async Task<Ticket> RequireTicketAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
    {
        var access = await _mutations.PrecheckAccessAsync(actor, ticketId, ct);
        if (access.Check != TicketMutationCheck.Ok || access.Ticket is null)
            throw new FormRejectedException(FormRejectCode.NotFound, "Ticket not found.");
        return access.Ticket;
    }

    /// Loads the form and checks access on the ticket it belongs to.
    private async Task<(TicketFormDetail Form, Ticket Ticket)> RequireFormAsync(TicketMutationActor actor, Guid formId, CancellationToken ct)
    {
        var form = await _repo.GetAsync(formId, ct)
                   ?? throw new FormRejectedException(FormRejectCode.NotFound, "Form not found.");
        var access = await _mutations.PrecheckAccessAsync(actor, form.Summary.TicketId, ct);
        if (access.Check != TicketMutationCheck.Ok || access.Ticket is null)
            throw new FormRejectedException(FormRejectCode.NotFound, "Form not found.");
        return (form, access.Ticket);
    }

    private async Task<FormTemplateDetail> RequireTemplateForTicketAsync(Ticket ticket, Guid templateId, CancellationToken ct)
    {
        var template = await _templates.GetAsync(templateId, ct);
        if (template is null || !template.IsActive
            || (template.QueueIds.Count > 0 && !template.QueueIds.Contains(ticket.QueueId)))
        {
            throw new FormRejectedException(FormRejectCode.TemplateNotAvailable,
                "This form is not available for the ticket's current queue.");
        }
        return template;
    }

    public async Task<IReadOnlyList<TicketFormSummary>> ListAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
    {
        await RequireTicketAsync(actor, ticketId, ct);
        return await _repo.ListForTicketAsync(ticketId, ct);
    }

    public async Task<IReadOnlyList<FormTemplateSummary>> ListUsableTemplatesAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        if (!settings.Enabled) return Array.Empty<FormTemplateSummary>();
        var ticket = await RequireTicketAsync(actor, ticketId, ct);
        return await _templates.ListAvailableForQueueAsync(ticket.QueueId, ct);
    }

    public async Task<FormTemplateDetail> GetUsableTemplateAsync(TicketMutationActor actor, Guid ticketId, Guid templateId, CancellationToken ct)
    {
        await RequireEnabledAsync(ct);
        var ticket = await RequireTicketAsync(actor, ticketId, ct);
        return await RequireTemplateForTicketAsync(ticket, templateId, ct);
    }

    private async Task<FormRuntimeSettings> RequireEnabledAsync(CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        if (!s.Enabled)
            throw new FormRejectedException(FormRejectCode.Disabled, "Forms are turned off in Settings → Templates → Forms.");
        return s;
    }

    public async Task<TicketFormSaveResult> CreateAsync(
        TicketMutationActor actor, Guid ticketId, Guid templateId, JsonObject? answers, CancellationToken ct)
    {
        var settings = await RequireEnabledAsync(ct);
        var ticket = await RequireTicketAsync(actor, ticketId, ct);
        var template = await RequireTemplateForTicketAsync(ticket, templateId, ct);

        if (await _repo.CountForTicketAsync(ticketId, ct) >= settings.MaxPerTicket)
            throw new FormRejectedException(FormRejectCode.TooManyForms,
                $"A ticket can have at most {settings.MaxPerTicket} forms.");

        var snapshot = template.Definition;
        var normalized = await NormalizeAnswersAsync(snapshot, answers, ticket, settings, ct);
        var searchText = FormRenderer.FlattenForSearch(snapshot, normalized);

        var formId = await _repo.CreateAsync(ticketId, template.Id, template.Name, snapshot, normalized, searchText, actor.UserId, ct);

        long eventId;
        try
        {
            eventId = await WriteNoteAsync(actor, ticketId, formId, template.Name, 1, snapshot, normalized, null, null, ct);
        }
        catch
        {
            // No note = no form: a form that never reached the timeline would
            // be invisible history. Roll the row back and surface the error.
            await _repo.HardDeleteAsync(formId, ct);
            throw;
        }
        await _repo.SetVersionEventAsync(formId, 1, eventId, ct);

        await AuditAsync(actor, "ticket.form.created", ticketId,
            new { formId, templateId = template.Id, template.Name, answeredFields = normalized.Count }, ct);

        var detail = await _repo.GetAsync(formId, ct)
                     ?? throw new FormRejectedException(FormRejectCode.NotFound, "Form not found.");
        return new TicketFormSaveResult(detail, eventId);
    }

    public async Task<TicketFormDetail> GetAsync(TicketMutationActor actor, Guid formId, CancellationToken ct)
        => (await RequireFormAsync(actor, formId, ct)).Form;

    public async Task<IReadOnlyList<TicketFormVersion>> ListVersionsAsync(TicketMutationActor actor, Guid formId, CancellationToken ct)
    {
        await RequireFormAsync(actor, formId, ct);
        return await _repo.ListVersionsAsync(formId, ct);
    }

    public async Task<TicketFormSaveResult> UpdateAsync(
        TicketMutationActor actor, Guid formId, int expectedVersion, JsonObject? answers, CancellationToken ct)
    {
        // Forms off = no new form activity at all (filled forms stay readable).
        var settings = await RequireEnabledAsync(ct);
        var (form, ticket) = await RequireFormAsync(actor, formId, ct);
        if (form.Summary.Version != expectedVersion)
            throw VersionConflict();

        var normalized = await NormalizeAnswersAsync(form.Snapshot, answers, ticket, settings, ct);
        var changed = FormRenderer.ChangedFields(form.Snapshot, form.Answers, normalized);
        if (changed.Count == 0)
            throw new FormRejectedException(FormRejectCode.Invalid, "Nothing changed.");

        var searchText = FormRenderer.FlattenForSearch(form.Snapshot, normalized);
        var newVersion = await _repo.UpdateAsync(formId, expectedVersion, normalized, searchText, actor.UserId, ct)
                         ?? throw VersionConflict();

        var eventId = await WriteNoteAsync(actor, ticket.Id, formId, form.Summary.Name, newVersion,
            form.Snapshot, normalized, form.Answers, changed, ct);
        await _repo.SetVersionEventAsync(formId, newVersion, eventId, ct);

        await AuditAsync(actor, "ticket.form.updated", ticket.Id,
            new { formId, form.Summary.Name, version = newVersion, changedFields = changed.Count }, ct);

        var detail = await _repo.GetAsync(formId, ct)
                     ?? throw new FormRejectedException(FormRejectCode.NotFound, "Form not found.");
        return new TicketFormSaveResult(detail, eventId);
    }

    private static FormRejectedException VersionConflict() => new(FormRejectCode.VersionConflict,
        "Someone else saved this form in the meantime. Reopen it to see the latest version.");

    public async Task DeleteAsync(TicketMutationActor actor, Guid formId, string? reason, CancellationToken ct)
    {
        var (form, ticket) = await RequireFormAsync(actor, formId, ct);
        if (!IsAdmin(actor))
            throw new FormRejectedException(FormRejectCode.Forbidden, "Only an admin can remove a form.");
        var why = (reason ?? string.Empty).Trim();
        if (why.Length == 0)
            throw new FormRejectedException(FormRejectCode.Invalid, "A reason is required.");
        if (why.Length > FormLimits.DeleteReasonMax)
            throw new FormRejectedException(FormRejectCode.Invalid, $"The reason must be at most {FormLimits.DeleteReasonMax} characters.");

        if (!await _repo.SoftDeleteAsync(formId, actor.UserId, why, ct))
            throw new FormRejectedException(FormRejectCode.NotFound, "Form not found.");

        var html = "<p><strong>" + System.Net.WebUtility.HtmlEncode($"Form removed: {form.Summary.Name}")
                   + "</strong></p><p>" + System.Net.WebUtility.HtmlEncode(why) + "</p>";
        await AddNoteAsync(actor, ticket.Id, html, new { formId, formName = form.Summary.Name, formAction = "removed" }, ct);

        await AuditAsync(actor, "ticket.form.deleted", ticket.Id,
            new { formId, form.Summary.Name, form.Summary.Version, reason = why }, ct);
    }

    public async Task<IReadOnlyList<TicketFormArticleOption>> SearchArticlesAsync(
        TicketMutationActor actor, Guid ticketId, bool contractOnly, string? search, CancellationToken ct)
    {
        var ticket = await RequireTicketAsync(actor, ticketId, ct);
        var term = (search ?? string.Empty).Trim();
        if (term.Length > 100) term = term[..100];
        return await _repo.SearchArticlesAsync(ticket.CompanyId, contractOnly, term, 30, ct);
    }

    public async Task<IReadOnlyList<TicketFormContactOption>> SearchContactsAsync(
        TicketMutationActor actor, Guid ticketId, string? search, CancellationToken ct)
    {
        var ticket = await RequireTicketAsync(actor, ticketId, ct);
        var term = (search ?? string.Empty).Trim();
        if (term.Length > 100) term = term[..100];
        return await _repo.SearchCompanyContactsAsync(ticket.CompanyId, ticket.RequesterContactId, term, 30, ct);
    }

    private async Task<JsonObject> NormalizeAnswersAsync(
        FormDefinition def, JsonObject? answers, Ticket ticket, FormRuntimeSettings settings, CancellationToken ct)
    {
        FormAnswerValidator.ParsedAnswers parsed;
        try { parsed = FormAnswerValidator.Parse(def, answers, settings); }
        catch (FormAnswerException ex) { throw new FormRejectedException(FormRejectCode.Invalid, ex.Message, ex.FieldId); }

        var agents = await _repo.ResolveAgentsAsync(parsed.References.AgentIds.ToList(), ct);
        if (agents.Count != parsed.References.AgentIds.Count)
            throw new FormRejectedException(FormRejectCode.Invalid, "A selected agent no longer exists.");
        var contacts = await _repo.ResolveCompanyContactsAsync(
            ticket.CompanyId, ticket.RequesterContactId, parsed.References.ContactIds.ToList(), ct);
        if (contacts.Count != parsed.References.ContactIds.Count)
            throw new FormRejectedException(FormRejectCode.Invalid, "A selected contact does not belong to this ticket's company.");
        var articles = await _repo.ResolveArticlesAsync(parsed.References.ArticleIds.ToList(), ct);
        if (articles.Count != parsed.References.ArticleIds.Count)
            throw new FormRejectedException(FormRejectCode.Invalid, "A selected article no longer exists.");

        return FormAnswerValidator.Build(def, parsed, new FormResolvedReferences(agents, contacts, articles));
    }

    private async Task<long> WriteNoteAsync(
        TicketMutationActor actor, Guid ticketId, Guid formId, string formName, int version,
        FormDefinition def, JsonObject answers, JsonObject? previous, IReadOnlyCollection<Guid>? changed, CancellationToken ct)
    {
        var tz = await HealthTimeFormat.ResolveDisplayZoneAsync(_appSettings, ct);
        var html = FormRenderer.RenderNoteHtml(formId, formName, version, def, answers, previous, changed,
            dt => HealthTimeFormat.Local(dt, tz));
        return await AddNoteAsync(actor, ticketId, html,
            new { formId, formName, formVersion = version, formAction = version == 1 ? "created" : "updated" }, ct);
    }

    /// Writes the internal Note through the shared mutation path: the same
    /// event write + fan-out (audit, realtime, SLA, triggers) as a typed note.
    private async Task<long> AddNoteAsync(TicketMutationActor actor, Guid ticketId, string html, object metadata, CancellationToken ct)
    {
        var evt = await _mutations.AddEventAsync(ticketId, new NewTicketEvent(
            EventType: nameof(TicketEventType.Note),
            BodyText: null,
            BodyHtml: _sanitizer.Sanitize(html),
            IsInternal: true,
            AuthorUserId: actor.UserId,
            MetadataJson: JsonSerializer.Serialize(metadata)), ct)
            ?? throw new FormRejectedException(FormRejectCode.NotFound, "Ticket not found.");
        await _mutations.PublishEventAsync(actor, ticketId, evt, new
        {
            evt.EventType,
            evt.IsInternal,
            source = "form",
        }, ct);
        return evt.Id;
    }

    private Task AuditAsync(TicketMutationActor actor, string eventType, Guid ticketId, object payload, CancellationToken ct)
        => _audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor.AuditActor,
            ActorRole: actor.AuditRole,
            Target: ticketId.ToString(),
            ClientIp: actor.ClientIp,
            UserAgent: actor.UserAgent,
            Payload: payload), ct);
}
