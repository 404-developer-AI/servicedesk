namespace Servicedesk.Domain.KnowledgeBase;

/// One supported locale in the knowledge base. Seeded with `nl-BE` (active)
/// and `en-US` (inactive secondary) by the bootstrapper. Multi-language
/// rendering is reserved for a later version; v0.0.31 only writes/reads
/// translations for the default locale.
public sealed record KbLocale(
    string Code,
    string DisplayName,
    bool IsActive,
    int SortOrder);

/// Singleton config row for the knowledge base. Exactly one row exists at
/// any time, enforced at the database layer with a unique-on-(true) index.
/// `DefaultLocaleCode` drives which translation is shown when an article is
/// missing a translation in the user's preferred locale.
public sealed record KnowledgeBaseConfig(
    Guid Id,
    bool IsActive,
    string DefaultLocaleCode,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
