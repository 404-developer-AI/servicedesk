namespace Servicedesk.Infrastructure.Triggers;

public interface ITriggerGroupRepository
{
    Task<IReadOnlyList<TriggerGroupRow>> ListAllAsync(CancellationToken ct);
    Task<TriggerGroupRow?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<TriggerGroupRow> CreateAsync(NewTriggerGroup row, CancellationToken ct);
    Task<TriggerGroupRow?> UpdateAsync(Guid id, UpdateTriggerGroup row, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// Bulk reorder — applies a list of (id, sortOrder) tuples in one
    /// transaction. Skips ids that don't exist. Used by the drag-and-drop
    /// UI when an admin reshuffles the group list.
    Task ReorderAsync(IReadOnlyList<TriggerGroupPlacement> placements, CancellationToken ct);
}
