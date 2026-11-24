using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Checklists;

/// The close-block rule (v0.0.103): a status change into one of the
/// configured "closing" state categories is refused while an attached
/// checklist with block_close still has required open items. Judged twice:
/// by <c>TicketFieldRules</c> before the write (PATCH, bulk, trigger
/// "Set status" — a clean refusal with the blockers) and by
/// <c>TicketFieldWriter</c> under the ticket row lock for the effective
/// status (queue auto-flip, an item reopened in between; 2026-11-24).
/// Merge moves the source ticket with its own UPDATE and is not covered.
public interface IChecklistCloseGuard
{
    /// Empty = not blocked. Non-empty = the checklists that block, in
    /// display order.
    Task<IReadOnlyList<ChecklistBlocker>> FindBlockersAsync(Guid ticketId, Guid targetStatusId, CancellationToken ct);
}

/// The field writer refused a status change because of the close block
/// (see <see cref="IChecklistCloseGuard"/>): thrown by the agent write path
/// so the PATCH, bulk and ISO callers answer like the precheck does.
public sealed class ChecklistBlockedException(IReadOnlyList<ChecklistBlocker> blockers)
    : InvalidOperationException("The checklist close block refused the status change.")
{
    public IReadOnlyList<ChecklistBlocker> Blockers { get; } = blockers;
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
