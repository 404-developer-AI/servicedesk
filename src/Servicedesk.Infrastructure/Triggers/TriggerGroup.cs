namespace Servicedesk.Infrastructure.Triggers;

/// Row DTO for the <c>trigger_groups</c> table. Groups are a pure
/// UX-side construct: the evaluator and scheduler never look at them.
public sealed class TriggerGroupRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed record NewTriggerGroup(string Name, string? Color);

public sealed record UpdateTriggerGroup(string Name, string? Color);

/// One row of the bulk reorder payload — placement of a single trigger
/// within (or between) groups. <see cref="GroupId"/> is nullable to
/// support the "Ungrouped" pseudo-section.
public sealed record TriggerPlacement(Guid Id, Guid? GroupId, int SortOrder);

/// One row of the bulk reorder payload for groups themselves.
public sealed record TriggerGroupPlacement(Guid Id, int SortOrder);
