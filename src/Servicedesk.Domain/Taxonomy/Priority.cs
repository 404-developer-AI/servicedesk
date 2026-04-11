namespace Servicedesk.Domain.Taxonomy;

public sealed record Priority(
    Guid Id,
    string Name,
    string Slug,
    int Level,
    string Color,
    string Icon,
    int SortOrder,
    bool IsActive,
    bool IsSystem,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
