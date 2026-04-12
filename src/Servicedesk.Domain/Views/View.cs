namespace Servicedesk.Domain.Views;

public sealed record View(
    Guid Id,
    Guid UserId,
    string Name,
    string FiltersJson,
    int SortOrder,
    bool IsShared,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
