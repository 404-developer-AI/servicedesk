namespace Servicedesk.Domain.Views;

public sealed record View(
    Guid Id,
    Guid UserId,
    string Name,
    string FiltersJson,
    string? Columns,
    int SortOrder,
    bool IsShared,
    string DisplayConfigJson,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
