namespace Servicedesk.Domain.KnowledgeBase;

/// Allowed values for `kb_articles.status`. Mirrors the database CHECK
/// constraint; any new value here must be added to the constraint at the
/// same time (DatabaseBootstrapper.cs).
public static class KbArticleStatus
{
    public const string Draft = "Draft";
    public const string Internal = "Internal";
    public const string Published = "Published";
    public const string Archived = "Archived";

    public static bool IsValid(string status) => status switch
    {
        Draft or Internal or Published or Archived => true,
        _ => false,
    };

    /// Statuses an agent (non-admin) is allowed to land an article in.
    /// Admin-only flips: Draft|Internal → Published|Archived, and back.
    public static bool IsAgentReachable(string status) =>
        status is Draft or Internal;
}

/// One article in the knowledge base. The body lives in
/// `kb_article_translations` so a single article can carry multiple
/// per-locale renderings; v0.0.31 always writes the default locale only.
/// `PublishAt` and `ArchiveAt` are reserved for a future scheduling
/// worker and stay NULL in this version.
public sealed record KbArticle(
    Guid Id,
    Guid SectionId,
    string Slug,
    string Status,
    bool IsFeatured,
    string? EditorNotes,
    int Position,
    DateTime? PublishAt,
    DateTime? ArchiveAt,
    DateTime LastStatusChangedUtc,
    Guid? LastStatusChangedByUserId,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? CreatedByUserId,
    Guid? UpdatedByUserId);

/// Per-locale title + body for an article. `BodyHtml` is the
/// sanitized rich-text payload shown in the reader; `BodyText` is the
/// HTML-stripped form fed into the database-side generated `tsvector`
/// for full-text search.
public sealed record KbArticleTranslation(
    Guid Id,
    Guid ArticleId,
    string LocaleCode,
    string Title,
    string BodyHtml,
    string BodyText,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
