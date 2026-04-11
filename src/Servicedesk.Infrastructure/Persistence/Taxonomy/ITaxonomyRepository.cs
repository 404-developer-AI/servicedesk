using Servicedesk.Domain.Taxonomy;

namespace Servicedesk.Infrastructure.Persistence.Taxonomy;

public enum DeleteResult
{
    Deleted,
    NotFound,
    SystemProtected,
    InUse,
}

public interface ITaxonomyRepository
{
    Task<IReadOnlyList<Queue>> ListQueuesAsync(CancellationToken ct);
    Task<Queue?> GetQueueAsync(Guid id, CancellationToken ct);
    Task<Queue> CreateQueueAsync(Queue q, CancellationToken ct);
    Task<Queue?> UpdateQueueAsync(Guid id, string name, string slug, string description, string color, string icon, int sortOrder, bool isActive, CancellationToken ct);
    Task<DeleteResult> DeleteQueueAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Priority>> ListPrioritiesAsync(CancellationToken ct);
    Task<Priority?> GetPriorityAsync(Guid id, CancellationToken ct);
    Task<Priority> CreatePriorityAsync(Priority p, CancellationToken ct);
    Task<Priority?> UpdatePriorityAsync(Guid id, string name, string slug, int level, string color, string icon, int sortOrder, bool isActive, CancellationToken ct);
    Task<DeleteResult> DeletePriorityAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Status>> ListStatusesAsync(CancellationToken ct);
    Task<Status?> GetStatusAsync(Guid id, CancellationToken ct);
    Task<Status> CreateStatusAsync(Status s, CancellationToken ct);
    Task<Status?> UpdateStatusAsync(Guid id, string name, string slug, string stateCategory, string color, string icon, int sortOrder, bool isActive, bool isDefault, CancellationToken ct);
    Task<DeleteResult> DeleteStatusAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken ct);
    Task<Category?> GetCategoryAsync(Guid id, CancellationToken ct);
    Task<Category> CreateCategoryAsync(Category c, CancellationToken ct);
    Task<Category?> UpdateCategoryAsync(Guid id, Guid? parentId, string name, string slug, string description, int sortOrder, bool isActive, CancellationToken ct);
    Task<DeleteResult> DeleteCategoryAsync(Guid id, CancellationToken ct);
}
