using Servicedesk.Domain.KnowledgeBase;

namespace Servicedesk.Infrastructure.Persistence.KnowledgeBase;

public interface IKbArticleRepository
{
    Task<KbArticleListPage> ListArticlesAsync(
        Guid? sectionId, string? status, string? search, int page, int pageSize, CancellationToken ct);
    Task<KbArticle?> GetArticleAsync(Guid id, CancellationToken ct);
    Task<KbArticleWithTranslation?> GetArticleBySlugAsync(string sectionSlug, string articleSlug, CancellationToken ct);
    Task<IReadOnlyList<KbFeaturedArticle>> ListFeaturedAsync(int limit, CancellationToken ct);

    Task<KbArticle> CreateArticleAsync(
        Guid sectionId, string slug, string status, string? editorNotes, int position,
        Guid actorUserId, CancellationToken ct);
    Task<KbArticle?> UpdateArticleAsync(
        Guid id, Guid sectionId, string slug, string? editorNotes, int position,
        Guid actorUserId, CancellationToken ct);
    Task<KbArticle?> FlipStatusAsync(
        Guid id, string newStatus, Guid actorUserId, CancellationToken ct);
    Task<KbArticle?> SetFeaturedAsync(
        Guid id, bool isFeatured, Guid actorUserId, CancellationToken ct);
    Task<KbArticle?> MoveArticleAsync(
        Guid id, Guid newSectionId, int newPosition, Guid actorUserId, CancellationToken ct);
    Task<bool> HardDeleteArticleAsync(Guid id, CancellationToken ct);

    Task<KbArticleTranslation?> GetTranslationAsync(Guid articleId, string localeCode, CancellationToken ct);
    Task<KbArticleTranslation> UpsertTranslationAsync(
        Guid articleId, string localeCode, string title, string bodyHtml, string bodyText, CancellationToken ct);

    /// True if an article with this slug already exists in the given
    /// section. Used by the create endpoint to derive a clash-free slug
    /// before committing the row.
    Task<bool> ArticleSlugExistsInSectionAsync(Guid sectionId, string slug, CancellationToken ct);
}

public sealed record KbArticleListPage(
    IReadOnlyList<KbArticleListItem> Items,
    int TotalHits,
    int Page,
    int PageSize);

public sealed record KbArticleListItem(
    Guid Id,
    Guid SectionId,
    string Slug,
    string Status,
    bool IsFeatured,
    int Position,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime LastStatusChangedUtc,
    Guid? LastStatusChangedByUserId,
    string Title);

public sealed record KbArticleWithTranslation(
    KbArticle Article,
    KbArticleTranslation Translation);

public sealed record KbFeaturedArticle(
    Guid Id,
    Guid SectionId,
    string Slug,
    string Title,
    DateTime UpdatedUtc);
