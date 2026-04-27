namespace Servicedesk.Infrastructure.Triggers;

/// Row DTO for the <c>triggers</c> table. Sealed class with auto-properties
/// so Dapper can hydrate via the column-alias matching pattern used elsewhere
/// in this project (every SELECT column carries an <c>AS PascalCase</c> alias).
public sealed class TriggerRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string ActivatorKind { get; set; } = string.Empty;
    public string ActivatorMode { get; set; } = string.Empty;
    public string ConditionsJson { get; set; } = "{}";
    public string ActionsJson { get; set; } = "[]";
    public string? Locale { get; set; }
    public string? Timezone { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
}
