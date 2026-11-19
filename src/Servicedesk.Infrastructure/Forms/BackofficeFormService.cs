using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Health;
using Servicedesk.Infrastructure.Settings;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Infrastructure.Forms;

/// One back-office step that already committed. Fanned out by
/// <see cref="IBackofficeFormEvents"/> (realtime ping for the work list and
/// the badge; trigger pass for submitted / handled / reopened).
public sealed record BackofficeFormEvent(
    string Kind,
    Guid FormId,
    Guid TicketId,
    Guid? TemplateId,
    string FormName,
    Guid? ActorUserId,
    Guid? SubmittedByUserId,
    string? SubmittedByName,
    string? HandledByName,
    string HandledNote);

/// Event kinds; <see cref="BackofficeFormTriggerEvents"/> are the ones a
/// trigger condition can match on.
public static class BackofficeFormEventKind
{
    public const string Submitted = "submitted";
    public const string Claimed = "claimed";
    public const string Released = "released";
    public const string Handled = "handled";
    public const string Reopened = "reopened";
    public const string Updated = "updated";
    public const string Removed = "removed";

    public static readonly IReadOnlySet<string> TriggerEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        Submitted, Handled, Reopened,
    };
}

public interface IBackofficeFormEvents
{
    /// Best effort: the state change is committed before this runs, so a
    /// failing push or trigger pass is logged and never surfaces to the user.
    Task PublishAsync(BackofficeFormEvent evt, CancellationToken ct);
}

public sealed record BackofficeFormChange(string FieldId, string Label, string Before, string After);

public sealed record BackofficeFormView(
    BackofficeFormDetail Detail,
    /// Fields that changed since the version that was last handled (empty
    /// when the form was never handled or nothing changed since).
    IReadOnlyList<BackofficeFormChange> ChangesSinceHandled);

public interface IBackofficeFormService
{
    Task<BackofficeFormPage> ListAsync(BackofficeFormListQuery query, CancellationToken ct);
    Task<BackofficeFormCounts> CountsAsync(CancellationToken ct);
    Task<BackofficeFormView> GetAsync(Guid formId, CancellationToken ct);
    Task<BackofficeFormListItem> ClaimAsync(TicketMutationActor actor, Guid formId, bool takeOver, CancellationToken ct);
    Task<BackofficeFormListItem> ReleaseAsync(TicketMutationActor actor, Guid formId, CancellationToken ct);
    Task<BackofficeFormListItem> HandleAsync(TicketMutationActor actor, Guid formId, string? note, int expectedVersion, CancellationToken ct);
    Task<BackofficeFormListItem> ReopenAsync(TicketMutationActor actor, Guid formId, string? note, CancellationToken ct);
}

/// v0.1.54 — the Back office forms work list. The caller's access is the
/// per-user BackofficeForms flag (checked by the endpoint filter on every
/// route); there is deliberately no queue check here — back office follows
/// up every marked form (owner decision). Every transition is one guarded
/// UPDATE in the repository, so concurrent back-office users can never both
/// claim or both handle the same form. What happens on the ticket when a
/// form is handled is decided by the admin's triggers, never hard-coded.
public sealed class BackofficeFormService : IBackofficeFormService
{
    public const string AlreadyClaimed = "already_claimed";
    public const string WrongState = "wrong_state";

    private readonly IBackofficeFormRepository _repo;
    private readonly IBackofficeFormEvents _events;
    private readonly ISettingsService _settings;
    private readonly IAuditLogger _audit;

    public BackofficeFormService(
        IBackofficeFormRepository repo,
        IBackofficeFormEvents events,
        ISettingsService settings,
        IAuditLogger audit)
    {
        _repo = repo;
        _events = events;
        _settings = settings;
        _audit = audit;
    }

    private static bool IsAdmin(TicketMutationActor actor)
        => string.Equals(actor.Role, "Admin", StringComparison.OrdinalIgnoreCase);

    public Task<BackofficeFormPage> ListAsync(BackofficeFormListQuery query, CancellationToken ct)
        => _repo.ListAsync(query, ct);

    public Task<BackofficeFormCounts> CountsAsync(CancellationToken ct) => _repo.CountsAsync(ct);

    public async Task<BackofficeFormView> GetAsync(Guid formId, CancellationToken ct)
    {
        var detail = await _repo.GetDetailAsync(formId, ct)
                     ?? throw new FormRejectedException(FormRejectCode.NotFound, "Form not found.");
        var changes = new List<BackofficeFormChange>();
        if (detail.HandledAnswers is not null && detail.Item.HandledVersion is { } hv && detail.Item.Version > hv)
        {
            var tz = await HealthTimeFormat.ResolveDisplayZoneAsync(_settings, ct);
            string Fmt(DateTimeOffset dt) => HealthTimeFormat.Local(dt, tz);
            var byId = detail.Snapshot.Fields.ToDictionary(f => f.Id);
            foreach (var id in FormRenderer.ChangedFields(detail.Snapshot, detail.HandledAnswers, detail.Answers))
            {
                var f = byId[id];
                var key = id.ToString();
                changes.Add(new BackofficeFormChange(key, f.Label,
                    FormRenderer.DisplayValue(f, detail.HandledAnswers[key], Fmt),
                    FormRenderer.DisplayValue(f, detail.Answers[key], Fmt)));
            }
        }
        return new BackofficeFormView(detail, changes);
    }

    public async Task<BackofficeFormListItem> ClaimAsync(TicketMutationActor actor, Guid formId, bool takeOver, CancellationToken ct)
    {
        var before = await RequireItemAsync(formId, ct);
        if (before.Status == BackofficeFormStatus.InProgress && before.ClaimedByUserId == actor.UserId)
            return before; // already mine — idempotent

        var result = await _repo.ClaimAsync(formId, actor.UserId, takeOver, ct);
        if (result is null)
        {
            var now = await RequireItemAsync(formId, ct);
            throw now.Status == BackofficeFormStatus.InProgress
                ? new FormRejectedException(AlreadyClaimed,
                    $"{now.ClaimedByName ?? "Someone else"} is already handling this form.")
                : new FormRejectedException(WrongState, "This form has already been handled.");
        }

        var item = await RequireItemAsync(formId, ct);
        await AuditAsync(actor, "ticket.form.backoffice.claimed", item,
            new { takeOverFrom = before.Status == BackofficeFormStatus.InProgress ? before.ClaimedByUserId : null }, ct);
        await PublishAsync(BackofficeFormEventKind.Claimed, item, actor, ct);
        return item;
    }

    public async Task<BackofficeFormListItem> ReleaseAsync(TicketMutationActor actor, Guid formId, CancellationToken ct)
    {
        await RequireItemAsync(formId, ct);
        var result = await _repo.ReleaseAsync(formId, actor.UserId, IsAdmin(actor), ct);
        if (result is null)
        {
            var now = await RequireItemAsync(formId, ct);
            throw new FormRejectedException(WrongState, now.Status == BackofficeFormStatus.InProgress
                ? "Only the person handling this form can release it."
                : "Nobody is handling this form any more.");
        }

        var item = await RequireItemAsync(formId, ct);
        await AuditAsync(actor, "ticket.form.backoffice.released", item, null, ct);
        await PublishAsync(BackofficeFormEventKind.Released, item, actor, ct);
        return item;
    }

    public async Task<BackofficeFormListItem> HandleAsync(TicketMutationActor actor, Guid formId, string? note, int expectedVersion, CancellationToken ct)
    {
        var text = await ValidateNoteAsync(note, required: await NoteRequiredAsync(ct));
        var before = await RequireItemAsync(formId, ct);
        if (before.Status == BackofficeFormStatus.Handled)
            throw new FormRejectedException(WrongState, "This form has already been handled.");

        var result = await _repo.HandleAsync(formId, actor.UserId, text, IsAdmin(actor), expectedVersion, ct);
        if (result is null)
        {
            var now = await RequireItemAsync(formId, ct);
            if (now.Status != BackofficeFormStatus.Handled && now.Version != expectedVersion)
                throw new FormRejectedException(FormRejectCode.VersionConflict,
                    "The form was changed while you had it open. Check the latest version and handle it again.");
            throw now.Status == BackofficeFormStatus.InProgress
                ? new FormRejectedException(AlreadyClaimed,
                    $"{now.ClaimedByName ?? "Someone else"} is handling this form. Take it over first.")
                : new FormRejectedException(WrongState, "This form has already been handled.");
        }

        var item = await RequireItemAsync(formId, ct);
        await AuditAsync(actor, "ticket.form.backoffice.handled", item, new { version = result.Version, noteLength = text.Length }, ct);
        await PublishAsync(BackofficeFormEventKind.Handled, item, actor, ct);
        return item;
    }

    public async Task<BackofficeFormListItem> ReopenAsync(TicketMutationActor actor, Guid formId, string? note, CancellationToken ct)
    {
        var text = await ValidateNoteAsync(note, required: false);
        await RequireItemAsync(formId, ct);
        var result = await _repo.ReopenAsync(formId, actor.UserId, text, ct)
                     ?? throw new FormRejectedException(WrongState, "Only a handled form can be reopened.");

        var item = await RequireItemAsync(formId, ct);
        await AuditAsync(actor, "ticket.form.backoffice.reopened", item, new { version = result.Version }, ct);
        await PublishAsync(BackofficeFormEventKind.Reopened, item, actor, ct);
        return item;
    }

    private async Task<BackofficeFormListItem> RequireItemAsync(Guid formId, CancellationToken ct)
        => await _repo.GetItemAsync(formId, ct)
           ?? throw new FormRejectedException(FormRejectCode.NotFound, "Form not found.");

    private async Task<bool> NoteRequiredAsync(CancellationToken ct)
    {
        try { return await _settings.GetAsync<bool>(SettingKeys.Forms.BackofficeHandleNoteRequired, ct); }
        catch { return true; }
    }

    private static Task<string> ValidateNoteAsync(string? note, bool required)
    {
        var text = (note ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (required && text.Length == 0)
            throw new FormRejectedException(FormRejectCode.Invalid, "A note is required.");
        if (text.Length > FormLimits.BackofficeNoteMax)
            throw new FormRejectedException(FormRejectCode.Invalid,
                $"The note must be at most {FormLimits.BackofficeNoteMax} characters.");
        return Task.FromResult(text);
    }

    private Task PublishAsync(string kind, BackofficeFormListItem item, TicketMutationActor actor, CancellationToken ct)
        => _events.PublishAsync(ToEvent(kind, item, actor.UserId), ct);

    public static BackofficeFormEvent ToEvent(string kind, BackofficeFormListItem item, Guid? actorUserId) => new(
        kind, item.Id, item.TicketId, item.TemplateId, item.Name, actorUserId,
        item.SubmittedByUserId, item.SubmittedByName, item.HandledByName, item.HandledNote);

    private Task AuditAsync(TicketMutationActor actor, string eventType, BackofficeFormListItem item, object? extra, CancellationToken ct)
        => _audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor.AuditActor,
            ActorRole: actor.AuditRole,
            Target: item.TicketId.ToString(),
            ClientIp: actor.ClientIp,
            UserAgent: actor.UserAgent,
            Payload: new { formId = item.Id, formName = item.Name, extra }), ct);
}

/// Realtime + trigger fan-out for back-office steps. The trigger pass is
/// wired in through <see cref="IFormTriggerRunner"/>; the realtime
/// ping through <see cref="Realtime.IBackofficeFormNotifier"/>.
public sealed class BackofficeFormEventPublisher : IBackofficeFormEvents
{
    private readonly Realtime.IBackofficeFormNotifier _notifier;
    private readonly Realtime.ITicketListNotifier _tickets;
    private readonly IFormTriggerRunner _triggers;
    private readonly ILogger<BackofficeFormEventPublisher> _logger;

    public BackofficeFormEventPublisher(
        Realtime.IBackofficeFormNotifier notifier,
        Realtime.ITicketListNotifier tickets,
        IFormTriggerRunner triggers,
        ILogger<BackofficeFormEventPublisher> logger)
    {
        _notifier = notifier;
        _tickets = tickets;
        _triggers = triggers;
        _logger = logger;
    }

    public async Task PublishAsync(BackofficeFormEvent evt, CancellationToken ct)
    {
        try { await _notifier.BackofficeFormsChangedAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Back office forms push failed for form {FormId}.", evt.FormId); }

        // Back-office steps don't touch the ticket itself, but the side
        // panel shows the form's back-office status: refresh open pages.
        if (evt.Kind is BackofficeFormEventKind.Claimed or BackofficeFormEventKind.Released
            or BackofficeFormEventKind.Handled or BackofficeFormEventKind.Reopened)
        {
            try { await _tickets.NotifyUpdatedAsync(evt.TicketId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Ticket refresh push failed for ticket {TicketId}.", evt.TicketId); }
        }

        if (!BackofficeFormEventKind.TriggerEvents.Contains(evt.Kind)) return;
        try { await _triggers.RunAsync(evt, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Back office form trigger pass failed for form {FormId}.", evt.FormId); }
    }
}

/// v0.1.58 — a filled form was saved (created, or edited into a new version).
/// <paramref name="Previous"/> = the answers before an edit (null on create),
/// so the trigger pass can tell which keyed fields changed.
public sealed record FormSavedEvent(
    string Kind,
    Guid FormId,
    Guid TicketId,
    Guid? TemplateId,
    string FormName,
    Guid? ActorUserId,
    FormDefinition Snapshot,
    System.Text.Json.Nodes.JsonObject Answers,
    System.Text.Json.Nodes.JsonObject? Previous,
    long EventId);

public static class FormSavedEventKind
{
    public const string Created = "created";
    public const string Updated = "updated";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Created, Updated };
}

/// Runs the action-trigger passes for forms: a back-office step
/// (<see cref="RunAsync"/>) and a form save (<see cref="RunSavedAsync"/>).
/// Implemented by the trigger layer (<c>Triggers.FormTriggerRunner</c>).
public interface IFormTriggerRunner
{
    Task RunAsync(BackofficeFormEvent evt, CancellationToken ct);
    Task RunSavedAsync(FormSavedEvent evt, CancellationToken ct);
}
