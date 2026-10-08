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
    DateTime UpdatedUtc,
    // v0.1.18 — false = column layout is locked to the view for every agent.
    bool AllowUserColumns = true,
    // v0.1.31 — true = Insights Rewind snapshots this view every interval.
    bool RewindTracked = false);
