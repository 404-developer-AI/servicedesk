namespace Servicedesk.Domain.Taxonomy;

public sealed record Category(
    Guid Id,
    Guid? ParentId,
    string Name,
    string Slug,
    string Description,
    int SortOrder,
    bool IsActive,
    bool IsSystem,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
