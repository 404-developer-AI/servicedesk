namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Read-only client for the Adsolut ERP Articles endpoint (artikels). Listing
/// uses cursor pagination; each list item already carries the full article
/// record (code, multi-language name/description, vatCode), so the sync upserts
/// straight from the page. The by-id fetch exists only for a manual per-row
/// resync.
public interface IAdsolutArticlesClient
{
    /// One page of articles. Pass <paramref name="cursor"/> = null for the first
    /// page, then the previous page's NextCursor while HasNext is true.
    /// <paramref name="modifiedSince"/> scopes a delta-sync (null = full).
    Task<AdsolutArticleListPage> ListPageAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        string? cursor,
        int pageSize,
        CancellationToken ct = default);

    /// Full article by id. Returns null if Adsolut answered 2xx with an
    /// empty/unparseable body.
    Task<AdsolutArticle?> GetByIdAsync(
        Guid administrationId,
        Guid articleId,
        CancellationToken ct = default);
}
