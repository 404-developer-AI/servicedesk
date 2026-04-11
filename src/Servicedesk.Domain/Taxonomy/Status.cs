namespace Servicedesk.Domain.Taxonomy;

public sealed record Status(
    Guid Id,
    string Name,
    string Slug,
    string StateCategory,
    string Color,
    string Icon,
    int SortOrder,
    bool IsActive,
    bool IsSystem,
    bool IsDefault,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
