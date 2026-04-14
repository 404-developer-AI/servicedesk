namespace Servicedesk.Domain.Taxonomy;

public sealed record Queue(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    string Color,
    string Icon,
    int SortOrder,
    bool IsActive,
    bool IsSystem,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string? InboundMailboxAddress = null,
    string? OutboundMailboxAddress = null);
