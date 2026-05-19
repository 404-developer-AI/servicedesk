namespace Servicedesk.Domain.Taxonomy;

/// First-class ticket-type taxonomy. Seeded with `support`, `order` and
/// `iso27001` at install time; admins can add their own. Drives the
/// "Create linked X ticket" picker in the ticket side panel — each
/// active manual trigger is bound to exactly one of these rows and
/// produces a ticket of the matching type. The code is a stable
/// CITEXT slug (case-insensitive), the label / icon / color are
/// surfaced verbatim in the UI.
public sealed record TicketType(
    Guid Id,
    string Code,
    string Label,
    string Description,
    string Icon,
    string Color,
    int SortOrder,
    bool IsActive,
    bool IsSystem,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
