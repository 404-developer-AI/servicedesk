namespace Servicedesk.Domain.Taxonomy;

public sealed record Category(
    Guid Id,
    Guid? ParentId,
    string Name,
    string Slug,
    string Description,
    int SortOrder,
    bool IsActive,
    bool IsSystem,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    /// v0.1.61 — tickets in this category are handled via Back office
    /// forms: a UI-only soft lock (banner, greyed side-panel fields, notes
    /// always allowed). Never an access rule.
    bool IsLocked = false);
