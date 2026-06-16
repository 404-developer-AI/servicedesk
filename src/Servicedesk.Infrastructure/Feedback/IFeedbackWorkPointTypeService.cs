namespace Servicedesk.Infrastructure.Feedback;

/// Admin-managed catalogue of feedback work-point types. The board's type
/// dropdown reads the active list; mutation is admin-only. The catalogue
/// starts empty — there are no seeded types.
public interface IFeedbackWorkPointTypeService
{
    Task<IReadOnlyList<FeedbackWorkPointType>> ListAsync(bool includeInactive, CancellationToken ct = default);

    Task<FeedbackWorkPointType?> GetAsync(Guid id, CancellationToken ct = default);

    Task<CreateWorkPointTypeResult> CreateAsync(string name, string color, int sortOrder, CancellationToken ct = default);

    Task<UpdateWorkPointTypeResult> UpdateAsync(
        Guid id, string name, string color, int sortOrder, bool isActive, CancellationToken ct = default);

    /// Hard-deletes the type when no entry references it; otherwise leaves it
    /// in place and returns false so the caller can tell the admin to
    /// deactivate it instead (history keeps a readable type name).
    Task<DeleteWorkPointTypeResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

public enum DeleteWorkPointTypeResult
{
    Deleted,
    InUse,
    NotFound,
}
