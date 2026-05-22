namespace Servicedesk.Infrastructure.Activity;

/// One row in the agent activity feed. Append-only — the feed reflects
/// "what an agent did at a moment in time" and never gets retroactively
/// edited. <see cref="EntityType"/> + <see cref="EntityId"/> is a generic
/// pointer back to the source object so the UI can deep-link to the
/// ticket / KB article / settings page that triggered the event.
///
/// Sealed class with get/set properties (not a positional record) per
/// the project-wide Dapper hydration rule — record-struct + positional-
/// record materialization has shipped two prior bugs in this codebase.
public sealed class ActivityFeedEntry
{
    public long Id { get; set; }
    public DateTime OccurredUtc { get; set; }
    public Guid AgentId { get; set; }
    public string AgentEmail { get; set; } = "";
    public string AgentRole { get; set; } = "";
    public string EventType { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? EntityExtra { get; set; }
    public string Summary { get; set; } = "";
    public string MetadataJson { get; set; } = "{}";

    /// Optional enrichment: the ticket subject + number when EntityType
    /// == "ticket". The feed query LEFT-JOINs the tickets table so the
    /// UI can render a clean link without a second round-trip; null for
    /// non-ticket entities.
    public long? TicketNumber { get; set; }
    public string? TicketSubject { get; set; }
}

/// Input to <see cref="IActivityRecorder.RecordAsync"/>. Caller fills in
/// the agent + event-type + summary; metadata is optional and stored as
/// jsonb for filter/search later. EntityType / EntityId are recommended
/// but optional (e.g. a generic "settings_changed" event still works
/// without an entity-id when the key is captured in metadata).
public sealed record ActivityRecord(
    Guid AgentId,
    string EventType,
    string Summary,
    string? EntityType = null,
    string? EntityId = null,
    string? EntityExtra = null,
    object? Metadata = null);
