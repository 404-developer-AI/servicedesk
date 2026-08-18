using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Checklists;

/// The close-block rule (v0.0.103): a status change into one of the
/// configured "closing" state categories is refused while an attached
/// checklist with block_close still has required open items. Evaluated by
/// <see cref="Tickets.TicketMutationService"/> so the single-ticket PATCH
/// and bulk actions share it; automation (triggers, mail-ingest, merge)
/// writes through the repository and is deliberately not covered — same
/// principle as status gates.
public interface IChecklistCloseGuard
{
    /// Empty = not blocked. Non-empty = the checklists that block, in
    /// display order.
    Task<IReadOnlyList<ChecklistBlocker>> FindBlockersAsync(Guid ticketId, Guid targetStatusId, CancellationToken ct);
}

public sealed class ChecklistCloseGuard : IChecklistCloseGuard
{
    private readonly ITicketChecklistRepository _checklists;
    private readonly ITaxonomyRepository _taxonomy;
    private readonly IChecklistSettingsReader _settings;

    public ChecklistCloseGuard(
        ITicketChecklistRepository checklists,
        ITaxonomyRepository taxonomy,
        IChecklistSettingsReader settings)
    {
        _checklists = checklists;
        _taxonomy = taxonomy;
        _settings = settings;
    }

    public async Task<IReadOnlyList<ChecklistBlocker>> FindBlockersAsync(Guid ticketId, Guid targetStatusId, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        if (!settings.Enabled || settings.BlockingStateCategories.Count == 0)
            return Array.Empty<ChecklistBlocker>();

        var status = await _taxonomy.GetStatusAsync(targetStatusId, ct);
        if (status is null || !settings.IsBlockingCategory(status.StateCategory))
            return Array.Empty<ChecklistBlocker>();

        return await _checklists.GetBlockersAsync(ticketId, ct);
    }
}
