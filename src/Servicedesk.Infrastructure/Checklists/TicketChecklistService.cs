using System.Text.Json;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Infrastructure.Checklists;

/// Stable rejection codes for the ticket-checklist operations. Endpoints map
/// them to HTTP; the frontend keys its messages on them.
public static class ChecklistRejectCode
{
    /// Ticket missing or no queue access — rendered as 404 like the ticket API.
    public const string NotFound = "not_found";
    public const string Disabled = "checklists_disabled";
    public const string TemplateNotAvailable = "template_not_available";
    public const string TooManyChecklists = "too_many_checklists";
    public const string TooManyItems = "too_many_items";
    /// Actor lacks the right for this specific action (detach a touched
    /// checklist as agent, edit/remove someone else's ad-hoc item, …).
    public const string Forbidden = "forbidden";
    public const string Invalid = "invalid";
}

public sealed class ChecklistRejectedException : Exception
{
    public string Code { get; }
    public ChecklistRejectedException(string code, string message) : base(message) => Code = code;
}

public sealed record ChecklistItemInput(
    string Title,
    string? Description,
    string? TeamLabel,
    string? TimingLabel,
    string? LinkUrl,
    string? LinkLabel,
    bool? IsRequired);

public interface ITicketChecklistService
{
    Task<IReadOnlyList<TicketChecklistView>> ListAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct);
    Task<IReadOnlyList<ChecklistTemplateSummary>> ListAvailableTemplatesAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct);
    Task<TicketChecklistView> AttachAsync(TicketMutationActor actor, Guid ticketId, Guid templateId, CancellationToken ct);
    Task DetachAsync(TicketMutationActor actor, Guid ticketId, Guid checklistId, CancellationToken ct);
    Task<TicketChecklistItem> SetItemStateAsync(TicketMutationActor actor, Guid ticketId, Guid itemId, string state, string? reason, string? comment, CancellationToken ct);
    Task<TicketChecklistItem> AddCommentAsync(TicketMutationActor actor, Guid ticketId, Guid itemId, string comment, CancellationToken ct);
    Task<TicketChecklistItem> AddItemAsync(TicketMutationActor actor, Guid ticketId, Guid checklistId, Guid? sectionId, ChecklistItemInput input, CancellationToken ct);
    Task<TicketChecklistItem> UpdateItemAsync(TicketMutationActor actor, Guid ticketId, Guid itemId, ChecklistItemInput input, CancellationToken ct);
    Task RemoveItemAsync(TicketMutationActor actor, Guid ticketId, Guid itemId, CancellationToken ct);
    Task<IReadOnlyList<TicketChecklistItemEvent>> ListItemEventsAsync(TicketMutationActor actor, Guid ticketId, Guid itemId, CancellationToken ct);
}

/// v0.0.103 — the rules around checklists on a ticket. Access = the ticket's
/// queue access (via the shared mutation precheck), so anyone who can work
/// the ticket can work its checklists; the finer rights (detach, ad-hoc item
/// edit/remove) live here. Every mutation ends with a realtime ticket
/// update so panel, bar, header, pop-out and list refresh for all viewers.
public sealed class TicketChecklistService : ITicketChecklistService
{
    private readonly ITicketChecklistRepository _repo;
    private readonly IChecklistTemplateRepository _templates;
    private readonly ITicketMutationService _mutations;
    private readonly ITicketRepository _tickets;
    private readonly IChecklistSettingsReader _settings;
    private readonly IAuditLogger _audit;
    private readonly ITicketListNotifier _notifier;

    public TicketChecklistService(
        ITicketChecklistRepository repo,
        IChecklistTemplateRepository templates,
        ITicketMutationService mutations,
        ITicketRepository tickets,
        IChecklistSettingsReader settings,
        IAuditLogger audit,
        ITicketListNotifier notifier)
    {
        _repo = repo;
        _templates = templates;
        _mutations = mutations;
        _tickets = tickets;
        _settings = settings;
        _audit = audit;
        _notifier = notifier;
    }

    private static bool IsAdmin(TicketMutationActor actor)
        => string.Equals(actor.Role, "Admin", StringComparison.OrdinalIgnoreCase);

    private async Task<TicketDetail> RequireTicketAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
    {
        var access = await _mutations.PrecheckAccessAsync(actor, ticketId, ct);
        if (access.Check != TicketMutationCheck.Ok || access.Ticket is null)
            throw new ChecklistRejectedException(ChecklistRejectCode.NotFound, "Ticket not found.");
        return access.Ticket;
    }

    private async Task<ChecklistRuntimeSettings> RequireEnabledAsync(CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        if (!s.Enabled)
            throw new ChecklistRejectedException(ChecklistRejectCode.Disabled, "Checklists are turned off in Settings → Tickets → Checklists.");
        return s;
    }

    public async Task<IReadOnlyList<TicketChecklistView>> ListAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
    {
        await RequireTicketAsync(actor, ticketId, ct);
        return await _repo.ListForTicketAsync(ticketId, ct);
    }

    public async Task<IReadOnlyList<ChecklistTemplateSummary>> ListAvailableTemplatesAsync(TicketMutationActor actor, Guid ticketId, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        if (!settings.Enabled) return Array.Empty<ChecklistTemplateSummary>();
        var ticket = await RequireTicketAsync(actor, ticketId, ct);
        return await _templates.ListAvailableForQueueAsync(ticket.Ticket.QueueId, ct);
    }

    public async Task<TicketChecklistView> AttachAsync(TicketMutationActor actor, Guid ticketId, Guid templateId, CancellationToken ct)
    {
        var settings = await RequireEnabledAsync(ct);
        var ticket = await RequireTicketAsync(actor, ticketId, ct);

        var template = await _templates.GetAsync(templateId, ct);
        if (template is null || !template.IsActive
            || (template.QueueIds.Count > 0 && !template.QueueIds.Contains(ticket.Ticket.QueueId)))
        {
            // Inactive or out of scope for this ticket's queue is the same
            // answer as "no such template" — the picker never offered it.
            throw new ChecklistRejectedException(ChecklistRejectCode.TemplateNotAvailable,
                "This checklist is not available for the ticket's current queue.");
        }

        var count = await _repo.CountForTicketAsync(ticketId, ct);
        if (count >= settings.MaxPerTicket)
            throw new ChecklistRejectedException(ChecklistRejectCode.TooManyChecklists,
                $"A ticket can have at most {settings.MaxPerTicket} checklists.");

        var checklistId = await _repo.AttachAsync(
            ticketId, template.Id, template.Name, template.Description, template.BlockClose,
            template.Definition, actor.UserId, ct);

        await _tickets.AddEventAsync(ticketId, new NewTicketEvent(
            EventType: nameof(TicketEventType.ChecklistAttached),
            BodyText: null, BodyHtml: null, IsInternal: true,
            AuthorUserId: actor.UserId,
            MetadataJson: JsonSerializer.Serialize(new
            {
                checklistId,
                checklistName = template.Name,
                itemCount = template.ItemCount,
                blockClose = template.BlockClose,
            })), ct);

        await AuditAsync(actor, "ticket.checklist.attached", ticketId,
            new { checklistId, templateId = template.Id, template.Name, itemCount = template.ItemCount }, ct);
        await _notifier.NotifyUpdatedAsync(ticketId, ct);

        var views = await _repo.ListForTicketAsync(ticketId, ct);
        return views.First(v => v.Checklist.Id == checklistId);
    }

    public async Task DetachAsync(TicketMutationActor actor, Guid ticketId, Guid checklistId, CancellationToken ct)
    {
        await RequireTicketAsync(actor, ticketId, ct);
        var checklist = await _repo.GetChecklistAsync(checklistId, ct);
        if (checklist is null || checklist.TicketId != ticketId)
            throw new ChecklistRejectedException(ChecklistRejectCode.NotFound, "Checklist not found.");

        // Progress made (ticks, n/a, comments, added items) → admin only,
        // otherwise the close block could be sidestepped by detaching.
        if (checklist.Touched && !IsAdmin(actor))
            throw new ChecklistRejectedException(ChecklistRejectCode.Forbidden,
                "This checklist already has progress; only an admin can remove it.");

        await _repo.DetachAsync(checklistId, ct);

        await _tickets.AddEventAsync(ticketId, new NewTicketEvent(
            EventType: nameof(TicketEventType.ChecklistDetached),
            BodyText: null, BodyHtml: null, IsInternal: true,
            AuthorUserId: actor.UserId,
            MetadataJson: JsonSerializer.Serialize(new
            {
                checklistId,
                checklistName = checklist.Name,
                requiredDone = checklist.RequiredDone,
                requiredTotal = checklist.RequiredTotal,
                hadProgress = checklist.Touched,
            })), ct);

        await AuditAsync(actor, "ticket.checklist.detached", ticketId,
            new { checklistId, checklist.Name, checklist.RequiredDone, checklist.RequiredTotal, hadProgress = checklist.Touched }, ct);
        await _notifier.NotifyUpdatedAsync(ticketId, ct);
    }

    public async Task<TicketChecklistItem> SetItemStateAsync(
        TicketMutationActor actor, Guid ticketId, Guid itemId, string state, string? reason, string? comment, CancellationToken ct)
    {
        var settings = await RequireEnabledAsync(ct);
        await RequireTicketAsync(actor, ticketId, ct);
        var item = await RequireItemAsync(ticketId, itemId, ct);

        state = (state ?? string.Empty).Trim().ToLowerInvariant();
        if (!ChecklistItemState.IsValid(state))
            throw new ChecklistRejectedException(ChecklistRejectCode.Invalid, "State must be open, done or na.");
        var trimmedReason = (reason ?? string.Empty).Trim();
        var trimmedComment = (comment ?? string.Empty).Trim();
        if (state == ChecklistItemState.NotApplicable && trimmedReason.Length == 0)
            throw new ChecklistRejectedException(ChecklistRejectCode.Invalid, "A reason is required to mark an item as not applicable.");
        if (trimmedReason.Length > ChecklistLimits.NaReasonMax)
            throw new ChecklistRejectedException(ChecklistRejectCode.Invalid, $"Reason must be at most {ChecklistLimits.NaReasonMax} characters.");
        if (trimmedComment.Length > ChecklistLimits.CommentMax)
            throw new ChecklistRejectedException(ChecklistRejectCode.Invalid, $"Comment must be at most {ChecklistLimits.CommentMax} characters.");

        var change = await _repo.SetItemStateAsync(itemId, state, trimmedReason, trimmedComment, actor.UserId, ct)
                     ?? throw new ChecklistRejectedException(ChecklistRejectCode.NotFound, "Item not found.");

        if (change.Changed)
        {
            if (settings.LogItemChangesToTimeline)
            {
                await _tickets.AddEventAsync(ticketId, new NewTicketEvent(
                    EventType: nameof(TicketEventType.ChecklistItemChanged),
                    BodyText: null, BodyHtml: null, IsInternal: true,
                    AuthorUserId: actor.UserId,
                    MetadataJson: JsonSerializer.Serialize(new
                    {
                        checklistId = change.ChecklistId,
                        checklistName = change.ChecklistName,
                        itemId,
                        itemTitle = item.Title,
                        fromState = change.FromState,
                        toState = change.ToState,
                        reason = state == ChecklistItemState.NotApplicable ? trimmedReason : null,
                    })), ct);
            }

            if (!change.WasComplete && change.IsComplete)
                await LogCompletionAsync(actor, ticketId, change, completed: true, ct);
            else if (change.WasComplete && !change.IsComplete)
                await LogCompletionAsync(actor, ticketId, change, completed: false, ct);

            await _notifier.NotifyUpdatedAsync(ticketId, ct);
        }

        return await _repo.GetItemAsync(itemId, ct) ?? item;
    }

    public async Task<TicketChecklistItem> AddCommentAsync(TicketMutationActor actor, Guid ticketId, Guid itemId, string comment, CancellationToken ct)
    {
        await RequireEnabledAsync(ct);
        await RequireTicketAsync(actor, ticketId, ct);
        var item = await RequireItemAsync(ticketId, itemId, ct);
        var trimmed = (comment ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new ChecklistRejectedException(ChecklistRejectCode.Invalid, "Comment cannot be empty.");
        if (trimmed.Length > ChecklistLimits.CommentMax)
            throw new ChecklistRejectedException(ChecklistRejectCode.Invalid, $"Comment must be at most {ChecklistLimits.CommentMax} characters.");
        await _repo.AddCommentAsync(itemId, trimmed, actor.UserId, ct);
        await _notifier.NotifyUpdatedAsync(ticketId, ct);
        return await _repo.GetItemAsync(itemId, ct) ?? item;
    }

    public async Task<TicketChecklistItem> AddItemAsync(
        TicketMutationActor actor, Guid ticketId, Guid checklistId, Guid? sectionId, ChecklistItemInput input, CancellationToken ct)
    {
        var settings = await RequireEnabledAsync(ct);
        await RequireTicketAsync(actor, ticketId, ct);
        var checklist = await _repo.GetChecklistAsync(checklistId, ct);
        if (checklist is null || checklist.TicketId != ticketId)
            throw new ChecklistRejectedException(ChecklistRejectCode.NotFound, "Checklist not found.");

        var item = ToTemplateItem(input);
        var err = ChecklistTemplateValidator.ValidateItem(item);
        if (err is not null) throw new ChecklistRejectedException(ChecklistRejectCode.Invalid, err);

        var count = await _repo.CountItemsAsync(checklistId, ct);
        if (count >= settings.MaxItemsPerChecklist)
            throw new ChecklistRejectedException(ChecklistRejectCode.TooManyItems,
                $"A checklist can have at most {settings.MaxItemsPerChecklist} items.");

        var itemId = await _repo.AddItemAsync(checklistId, sectionId, item, actor.UserId, ct)
                     ?? throw new ChecklistRejectedException(ChecklistRejectCode.NotFound, "Checklist not found.");

        await AuditAsync(actor, "ticket.checklist.item_added", ticketId,
            new { checklistId, itemId, item.Title, item.IsRequired }, ct);
        await _notifier.NotifyUpdatedAsync(ticketId, ct);
        return await _repo.GetItemAsync(itemId, ct)
               ?? throw new ChecklistRejectedException(ChecklistRejectCode.NotFound, "Item not found.");
    }

    public async Task<TicketChecklistItem> UpdateItemAsync(
        TicketMutationActor actor, Guid ticketId, Guid itemId, ChecklistItemInput input, CancellationToken ct)
    {
        await RequireEnabledAsync(ct);
        await RequireTicketAsync(actor, ticketId, ct);
        var existing = await RequireItemAsync(ticketId, itemId, ct);
        RequireAdHocRights(actor, existing, "edit");

        var item = ToTemplateItem(input);
        item.IsRequired = existing.IsRequired;
        var err = ChecklistTemplateValidator.ValidateItem(item);
        if (err is not null) throw new ChecklistRejectedException(ChecklistRejectCode.Invalid, err);

        await _repo.UpdateItemAsync(itemId, item, actor.UserId, ct);
        await AuditAsync(actor, "ticket.checklist.item_edited", ticketId,
            new { checklistId = existing.ChecklistId, itemId, item.Title }, ct);
        await _notifier.NotifyUpdatedAsync(ticketId, ct);
        return await _repo.GetItemAsync(itemId, ct) ?? existing;
    }

    public async Task RemoveItemAsync(TicketMutationActor actor, Guid ticketId, Guid itemId, CancellationToken ct)
    {
        await RequireEnabledAsync(ct);
        await RequireTicketAsync(actor, ticketId, ct);
        var existing = await RequireItemAsync(ticketId, itemId, ct);
        RequireAdHocRights(actor, existing, "remove");

        await _repo.RemoveItemAsync(itemId, actor.UserId, ct);
        await AuditAsync(actor, "ticket.checklist.item_removed", ticketId,
            new { checklistId = existing.ChecklistId, itemId, existing.Title }, ct);
        await _notifier.NotifyUpdatedAsync(ticketId, ct);
    }

    public async Task<IReadOnlyList<TicketChecklistItemEvent>> ListItemEventsAsync(
        TicketMutationActor actor, Guid ticketId, Guid itemId, CancellationToken ct)
    {
        await RequireTicketAsync(actor, ticketId, ct);
        await RequireItemAsync(ticketId, itemId, ct);
        return await _repo.ListItemEventsAsync(itemId, ct);
    }

    // ---- helpers ------------------------------------------------------

    private async Task<TicketChecklistItem> RequireItemAsync(Guid ticketId, Guid itemId, CancellationToken ct)
    {
        var item = await _repo.GetItemAsync(itemId, ct);
        if (item is null || item.TicketId != ticketId)
            throw new ChecklistRejectedException(ChecklistRejectCode.NotFound, "Item not found.");
        return item;
    }

    /// Template items are immutable on the ticket (n/a is the escape hatch);
    /// ad-hoc items can be edited/removed while still open by whoever added
    /// them or by an admin.
    private static void RequireAdHocRights(TicketMutationActor actor, TicketChecklistItem item, string verb)
    {
        if (!item.IsAdHoc)
            throw new ChecklistRejectedException(ChecklistRejectCode.Forbidden,
                $"Template items cannot be {verb}ed on the ticket — mark them not applicable instead.");
        if (item.State != ChecklistItemState.Open)
            throw new ChecklistRejectedException(ChecklistRejectCode.Forbidden,
                $"Only open items can be {verb}ed. Reopen it first.");
        if (!IsAdmin(actor) && item.AddedByUserId != actor.UserId)
            throw new ChecklistRejectedException(ChecklistRejectCode.Forbidden,
                $"Only the agent who added this item (or an admin) can {verb} it.");
    }

    private static ChecklistTemplateItem ToTemplateItem(ChecklistItemInput input) => new()
    {
        Title = input.Title ?? string.Empty,
        Description = input.Description ?? string.Empty,
        TeamLabel = input.TeamLabel ?? string.Empty,
        TimingLabel = input.TimingLabel ?? string.Empty,
        LinkUrl = input.LinkUrl ?? string.Empty,
        LinkLabel = input.LinkLabel ?? string.Empty,
        IsRequired = input.IsRequired ?? true,
    };

    private async Task LogCompletionAsync(TicketMutationActor actor, Guid ticketId, ChecklistItemStateChange change, bool completed, CancellationToken ct)
    {
        await _tickets.AddEventAsync(ticketId, new NewTicketEvent(
            EventType: completed ? nameof(TicketEventType.ChecklistCompleted) : nameof(TicketEventType.ChecklistReopened),
            BodyText: null, BodyHtml: null, IsInternal: true,
            AuthorUserId: actor.UserId,
            MetadataJson: JsonSerializer.Serialize(new
            {
                checklistId = change.ChecklistId,
                checklistName = change.ChecklistName,
            })), ct);
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
