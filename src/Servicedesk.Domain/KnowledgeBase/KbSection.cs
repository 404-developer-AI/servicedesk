namespace Servicedesk.Domain.KnowledgeBase;

/// Tree node grouping articles. `ParentSectionId == null` denotes a root
/// section. Slugs are unique within siblings (database-enforced via two
/// partial unique indexes — root scope and per-parent scope).
public sealed record KbSection(
    Guid Id,
    Guid? ParentSectionId,
    string Slug,
    string? IconName,
    int Position,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? CreatedByUserId,
    Guid? UpdatedByUserId);

/// Per-locale label for a section.
public sealed record KbSectionTranslation(
    Guid Id,
    Guid SectionId,
    string LocaleCode,
    string Title,
    string? Description,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
