using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
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
    private readonly IBackofficeFormEvents _backoffice;
    private readonly IBackofficeFormRepository _backofficeRepo;
    private readonly IQueueAccessService _queueAccess;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IFormTriggerRunner _formTriggers;
    private readonly Calendar.ICalendarSyncService _calendar;
    private readonly ILogger<TicketFormService> _logger;

    public TicketFormService(
        ITicketFormRepository repo,
        IFormTemplateRepository templates,
        ITicketMutationService mutations,
        IFormSettingsReader settings,
        ISettingsService appSettings,
        ITicketHtmlSanitizer sanitizer,
        IAuditLogger audit,
        IBackofficeFormEvents backoffice,
        IBackofficeFormRepository backofficeRepo,
        IQueueAccessService queueAccess,
        ITaxonomyRepository taxonomy,
        IFormTriggerRunner formTriggers,
        Calendar.ICalendarSyncService calendar,
        ILogger<TicketFormService> logger)
    {
        _calendar = calendar;
        _queueAccess = queueAccess;
        _taxonomy = taxonomy;
        _formTriggers = formTriggers;
        _logger = logger;
        _repo = repo;
        _templates = templates;
        _mutations = mutations;
        _settings = settings;
        _appSettings = appSettings;
        _sanitizer = sanitizer;
        _audit = audit;
        _backoffice = backoffice;
        _backofficeRepo = backofficeRepo;
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
        // v0.1.61 — offered in the ticket's queue through `::` or a header
        // button, or the plan form of an open back-office request (Plan).
        var template = await _templates.GetAsync(templateId, ct);
        if (template is null || !template.IsActive
            || (!template.UsableIn(ticket.QueueId)
                && !await _repo.IsPlanFormOfOpenRequestAsync(ticket.Id, templateId, ct)))
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
        // Resolved for this ticket's queue: InPicker = offered through `::`
        // here; HeaderButtonLabel only when the button shows in this queue.
        var q = ticket.QueueId;
        return (await _templates.ListAvailableForQueueAsync(q, ct))
            .Select(t => t with { InPicker = t.InPickerFor(q), HeaderButtonLabel = t.ButtonFor(q) ? t.HeaderButtonLabel : null })
            .ToList();
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
        await RequirePlannerAsync(actor, template.Outlook, ct);
        var normalized = await NormalizeAnswersAsync(actor, snapshot, answers, null, ticket, settings, ct);
        CheckAppointment(template.Outlook, snapshot, normalized, ticket);
        var searchText = FormRenderer.FlattenForSearch(snapshot, normalized);

        var formId = await _repo.CreateAsync(ticketId, template.Id, template.Name, snapshot, normalized, searchText,
            actor.UserId, template.SendToBackoffice, ct);

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
        await _repo.BindAttachmentsToEventAsync(ticketId, FormRenderer.AttachmentIds(snapshot, normalized), eventId, ct);

        await AuditAsync(actor, "ticket.form.created", ticketId,
            new { formId, templateId = template.Id, template.Name, answeredFields = normalized.Count,
                  sendToBackoffice = template.SendToBackoffice }, ct);

        await RunSavedTriggersAsync(FormSavedEventKind.Created, formId, ticketId, template.Id, template.Name,
            actor, snapshot, normalized, null, eventId, ct);

        if (template.SendToBackoffice)
            await PublishBackofficeAsync(BackofficeFormEventKind.Submitted, formId, actor, ct);

        await PlanCalendarAsync(template.Outlook, formId, ct);

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

        // The live template decides whether this form plans in Outlook.
        var outlook = form.TemplateId is { } tid ? (await _templates.GetAsync(tid, ct))?.Outlook : null;
        await RequirePlannerAsync(actor, outlook, ct);
        var normalized = await NormalizeAnswersAsync(actor, form.Snapshot, answers, form.Answers, ticket, settings, ct);
        CheckAppointment(outlook, form.Snapshot, normalized, ticket);
        var changed = FormRenderer.ChangedFields(form.Snapshot, form.Answers, normalized);
        if (changed.Count == 0)
            throw new FormRejectedException(FormRejectCode.Invalid, "Nothing changed.");

        var searchText = FormRenderer.FlattenForSearch(form.Snapshot, normalized);
        var update = await _repo.UpdateAsync(formId, expectedVersion, normalized, searchText, actor.UserId, ct)
                     ?? throw VersionConflict();
        var newVersion = update.Version;

        var eventId = await WriteNoteAsync(actor, ticket.Id, formId, form.Summary.Name, newVersion,
            form.Snapshot, normalized, form.Answers, changed, ct);
        await _repo.SetVersionEventAsync(formId, newVersion, eventId, ct);
        await _repo.BindAttachmentsToEventAsync(ticket.Id, FormRenderer.AttachmentIds(form.Snapshot, normalized), eventId, ct);

        await AuditAsync(actor, "ticket.form.updated", ticket.Id,
            new { formId, form.Summary.Name, version = newVersion, changedFields = changed.Count }, ct);

        await RunSavedTriggersAsync(FormSavedEventKind.Updated, formId, ticket.Id, form.TemplateId, form.Summary.Name,
            actor, form.Snapshot, normalized, form.Answers, eventId, ct);

        // A handled back-office form that changes goes back to Open.
        if (update.PreviousBoStatus is not null)
        {
            await PublishBackofficeAsync(update.PreviousBoStatus == BackofficeFormStatus.Handled
                ? BackofficeFormEventKind.Reopened : BackofficeFormEventKind.Updated, formId, actor, ct);
        }

        await PlanCalendarAsync(outlook, formId, ct);

        var detail = await _repo.GetAsync(formId, ct)
                     ?? throw new FormRejectedException(FormRejectCode.NotFound, "Form not found.");
        return new TicketFormSaveResult(detail, eventId);
    }

    /// v0.1.61 — a form whose template plans in Outlook may only be saved by
    /// someone with "Plan in calendars" signed in with Microsoft 365 (server
    /// gate; the client only mirrors it).
    private async Task RequirePlannerAsync(TicketMutationActor actor, FormOutlookMapping? outlook, CancellationToken ct)
    {
        if (outlook is not { Enabled: true }) return;
        if (await _calendar.CheckPlannerAsync(actor.UserId, actor.Amr, ct) is { } reason)
            throw new FormRejectedException(FormRejectCode.Forbidden, reason);
    }

    /// The appointment must make sense before anything is saved: end after
    /// start, and no invitations leave a project ticket (internal only).
    private static void CheckAppointment(FormOutlookMapping? outlook, FormDefinition def, JsonObject answers, Ticket ticket)
    {
        if (outlook is not { Enabled: true }) return;
        var (start, end) = outlook.ReadTimes(def, answers);
        if (start is not null && end is not null && end <= start)
            throw new FormRejectedException(FormRejectCode.Invalid, "The appointment must end after it starts.", outlook.EndFieldId);
        if (ticket.IsProject && outlook.InviteesFieldId is { } inv
            && answers[inv.ToString()] is JsonArray people && people.Count > 0)
            throw new FormRejectedException(FormRejectCode.Invalid,
                "Project tickets are internal: you can't invite people from outside the team.", inv);
    }

    /// Best effort like the trigger pass: the form is saved; the calendar
    /// plan is recorded and written by the worker (retried on failure).
    private async Task PlanCalendarAsync(FormOutlookMapping? outlook, Guid formId, CancellationToken ct)
    {
        try { await _calendar.PlanAsync(formId, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Calendar plan failed for form {FormId}.", formId); }
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

        try { await _calendar.CancelAllAsync(formId, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Cancelling the appointments of form {FormId} failed.", formId); }

        if (form.Summary.BoStatus is not null)
        {
            await _backoffice.PublishAsync(new BackofficeFormEvent(BackofficeFormEventKind.Removed, formId, ticket.Id,
                form.TemplateId, form.Summary.Name, actor.UserId, form.Summary.CreatedByUserId,
                form.Summary.CreatedByName, form.Summary.BoHandledByName, string.Empty), ct);
        }
    }

    /// Back-office fan-out (work-list ping + trigger pass) from the row as
    /// committed, so the trigger sees the post-change state.
    private async Task PublishBackofficeAsync(string kind, Guid formId, TicketMutationActor actor, CancellationToken ct)
    {
        var item = await _backofficeRepo.GetItemAsync(formId, BackofficeFormService.ParseWaiting("Pending"), ct);
        if (item is null) return;
        await _backoffice.PublishAsync(BackofficeFormService.ToEvent(kind, item, actor.UserId), ct);
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

    /// v0.1.58 — the trigger pass for a saved form (created / updated). Best
    /// effort like the back-office pass: the form and its note are committed,
    /// so a failing trigger is logged and never undoes the save.
    private async Task RunSavedTriggersAsync(
        string kind, Guid formId, Guid ticketId, Guid? templateId, string formName, TicketMutationActor actor,
        FormDefinition snapshot, JsonObject answers, JsonObject? previous, long eventId, CancellationToken ct)
    {
        try
        {
            await _formTriggers.RunSavedAsync(new FormSavedEvent(
                kind, formId, ticketId, templateId, formName, actor.UserId,
                snapshot, answers, previous, eventId), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Form trigger pass failed for form {FormId}.", formId);
        }
    }

    private async Task<JsonObject> NormalizeAnswersAsync(
        TicketMutationActor actor, FormDefinition def, JsonObject? answers, JsonObject? previous,
        Ticket ticket, FormRuntimeSettings settings, CancellationToken ct)
    {
        FormAnswerValidator.ParsedAnswers parsed;
        try { parsed = FormAnswerValidator.Parse(def, answers, settings, _sanitizer.Sanitize); }
        catch (FormAnswerException ex) { throw new FormRejectedException(FormRejectCode.Invalid, ex.Message, ex.FieldId); }

        // A date a trigger can use (pending till from a form field) must be in
        // the future — but only when it is new or changed, so a form whose
        // intervention already took place can still be corrected later.
        if (FormAnswerValidator.TriggerDateInPast(def, parsed, previous, DateTime.UtcNow) is { } past)
            throw new FormRejectedException(FormRejectCode.Invalid,
                $"'{past.Label}': the date and time must be in the future.", past.Id);

        var queues = parsed.References.QueueIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await ResolveQueuesAsync(await _queueAccess.GetScopeAsync(actor.UserId, actor.Role, ct),
                parsed.References.QueueIds, id => _taxonomy.GetQueueAsync(id, ct));

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

        // v0.1.61 — invitees: any active contact; attachments: this ticket's own.
        var invitees = await _repo.ResolveActiveContactsAsync(parsed.References.InviteeContactIds.ToList(), ct);
        if (invitees.Count != parsed.References.InviteeContactIds.Count)
            throw new FormRejectedException(FormRejectCode.Invalid, "A selected contact no longer exists or has no e-mail address.");
        var files = await _repo.ResolveTicketAttachmentsAsync(ticket.Id, parsed.References.AttachmentIds.ToList(), ct);
        if (files.Count != parsed.References.AttachmentIds.Count)
            throw new FormRejectedException(FormRejectCode.Invalid, "A file is not (or no longer) attached to this ticket. Upload it again.");
        var maxBytes = settings.MaxAttachmentBytes;
        if (maxBytes > 0 && files.Values.Sum(f => f.Size) > maxBytes)
            throw new FormRejectedException(FormRejectCode.Invalid,
                $"The files on this form are larger than {maxBytes / (1024 * 1024)} MB together. Remove a file or make it smaller.");

        return FormAnswerValidator.Build(def, parsed, new FormResolvedReferences(agents, contacts, articles)
        {
            Queues = queues,
            InviteeContacts = invitees,
            Attachments = files.ToDictionary(kv => kv.Key, kv => (kv.Value.Name, kv.Value.Size, kv.Value.ContentType)),
        });
    }

    /// Queue fields: only active queues the person filling in the form can
    /// access (a trigger may move the ticket there, so this is the boundary).
    internal static async Task<Dictionary<Guid, string>> ResolveQueuesAsync(
        QueueAccessScope scope, IEnumerable<Guid> ids, Func<Guid, Task<Domain.Taxonomy.Queue?>> load)
    {
        var queues = new Dictionary<Guid, string>();
        foreach (var qid in ids)
        {
            var q = scope.CanSee(qid) ? await load(qid) : null;
            if (q is null || !q.IsActive)
                throw new FormRejectedException(FormRejectCode.Invalid, "A selected queue is not available to you.");
            queues[qid] = q.Name;
        }
        return queues;
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
